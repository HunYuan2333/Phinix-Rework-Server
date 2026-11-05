using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using ServerTransport;

internal static class ListenerScenarios
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(5, deadline.Token);
    }
    private static async Task<TcpClient> Socket(IPEndPoint endpoint)
    {
        var socket = new TcpClient();
        try { await socket.ConnectAsync(endpoint).WaitAsync(TimeSpan.FromSeconds(3)); return socket; }
        catch { socket.Dispose(); throw; }
    }
    private static Task<TlsPacketChannel> Connect(TcpClient socket, X509Certificate2 root, byte[] pin) =>
        TlsPacketChannel.ConnectAsync(socket.GetStream(), "localhost", pin, root, X509RevocationMode.NoCheck,
            maximum:4096, handshakeTimeout:TimeSpan.FromSeconds(2), frameTimeout:TimeSpan.FromSeconds(2));
    private static async Task Refused(TcpClient socket)
    {
        byte[] one = new byte[1];
        try
        {
            if (await socket.GetStream().ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) != 0)
                throw new Exception("Refused socket received application bytes");
        }
        catch (IOException) { } // A reset or EOF both mean refusal; a timeout is not success.
    }
    internal static async Task Run(X509Certificate2 cert, X509Certificate2 root, byte[] pin, Action<bool,string> check)
    {
        // Literal frame oracle against a listener-owned channel, no UDP or business-specific callback.
        var entered=Signal(); var done=Signal(); int applications=0;
        var failures=new ConcurrentQueue<TlsConnectionFailure>();
        var listener=new TlsPacketListener(new IPEndPoint(IPAddress.Loopback,0),cert,async (channel,ct)=>{
            Interlocked.Increment(ref applications); entered.TrySetResult();
            byte[]? body=await channel.ReadAsync(ct); check(body!=null && body.SequenceEqual(new byte[]{17,0,255}),"Listener changed opaque payload");
            await channel.WriteAsync(new byte[]{42},ct);done.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan,ct);
        },maximumConnections:2,maximumHandshakes:1,maximumPacket:4096,handshakeTimeout:TimeSpan.FromSeconds(2),diagnostic:failures.Enqueue);
        await using (listener)
        {
            using(var silent=await Socket(listener.LocalEndpoint))
            {
                await Wait(()=>listener.PendingHandshakes==1);
                using var excess=await Socket(listener.LocalEndpoint);await Refused(excess);
                await Wait(()=>failures.Contains(TlsConnectionFailure.HandshakeLimit));
                check(failures.Contains(TlsConnectionFailure.HandshakeLimit),"Handshake budget must refuse before creating another TLS worker");
                check(applications==0 && listener.ConnectionCount==1,"Unprotected peer entered application or exceeded admission budget");
            }
            await Wait(()=>listener.ConnectionCount==0);
            using var client=await Socket(listener.LocalEndpoint);await using var channel=await Connect(client,root,pin);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await channel.WriteAsync(new byte[]{17,0,255});check((await channel.ReadAsync())!.SequenceEqual(new byte[]{42}),"Listener reverse frame differs");
            await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var silent2=await Socket(listener.LocalEndpoint);await Wait(()=>listener.ConnectionCount==2);
            using var third=await Socket(listener.LocalEndpoint);await Refused(third);
            await Wait(()=>failures.Contains(TlsConnectionFailure.ConnectionLimit));
            check(failures.Contains(TlsConnectionFailure.ConnectionLimit),"Connection budget must include authenticated and handshaking peers");
            check(listener.ConnectionCount==2 && listener.PendingHandshakes==1,"Connection/handshake counters drifted");
        }
        check(listener.ConnectionCount==0 && listener.PendingHandshakes==0 && listener.Completion.IsCompletedSuccessfully,"Stop must await all connections and handshakes");
        await listener.DisposeAsync();check(listener.Completion.IsCompletedSuccessfully,"Repeated disposal must preserve completion");
        using(var rebound=new TcpListener(listener.LocalEndpoint)) { rebound.Start();check(true,"Stopped listener releases its port"); }

        // Independent invalid TLS cannot reach the application; one bad peer cannot poison the next.
        var failed=Signal();var called=Signal();int invalidCalls=0;
        await using(var recovering=new TlsPacketListener(new IPEndPoint(IPAddress.Loopback,0),cert,(_,_)=>{
            Interlocked.Increment(ref invalidCalls);called.TrySetResult();return Task.CompletedTask;
        },maximumConnections:2,maximumHandshakes:1,handshakeTimeout:TimeSpan.FromSeconds(2),diagnostic:_=>{failed.TrySetResult();throw new Exception("synthetic diagnostic failure");}))
        {
            using(var raw=await Socket(recovering.LocalEndpoint))
            {
                await raw.GetStream().WriteAsync(new byte[]{0,0,0,0,0});raw.Client.Shutdown(SocketShutdown.Send);
                await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));await Wait(()=>recovering.ConnectionCount==0);
                check(invalidCalls==0,"Invalid TLS reached application");
            }
            using var valid=await Socket(recovering.LocalEndpoint);await using var secure=await Connect(valid,root,pin);
            await called.Task.WaitAsync(TimeSpan.FromSeconds(3));await Wait(()=>recovering.ConnectionCount==0);
            check(invalidCalls==1,"Handshake failure or diagnostic exception poisoned future admission");
        }

        var timedOut=Signal();int timeoutCalls=0;
        await using(var timed=new TlsPacketListener(new IPEndPoint(IPAddress.Loopback,0),cert,(_,_)=>{
            Interlocked.Increment(ref timeoutCalls);return Task.CompletedTask;
        },maximumConnections:1,maximumHandshakes:1,handshakeTimeout:TimeSpan.FromMilliseconds(100),
            diagnostic:reason=>{if(reason==TlsConnectionFailure.HandshakeFailed)timedOut.TrySetResult();}))
        {
            using var silent=await Socket(timed.LocalEndpoint);
            await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(3));await Refused(silent);
            await Wait(()=>timed.ConnectionCount==0);
            check(timed.PendingHandshakes==0 && timeoutCalls==0,"Handshake timeout did not release its slot without calling application");
        }
        var applicationFailed=Signal();var nextCalled=Signal();int appCalls=0;
        await using(var isolated=new TlsPacketListener(new IPEndPoint(IPAddress.Loopback,0),cert,(_,_)=>{
            if(Interlocked.Increment(ref appCalls)==1)throw new InvalidOperationException("synthetic handler failure");
            nextCalled.TrySetResult();return Task.CompletedTask;
        },maximumConnections:1,maximumHandshakes:1,
            diagnostic:reason=>{if(reason==TlsConnectionFailure.ApplicationFailed)applicationFailed.TrySetResult();}))
        {
            using(var first=await Socket(isolated.LocalEndpoint))
            await using(var secure=await Connect(first,root,pin))
            { await applicationFailed.Task.WaitAsync(TimeSpan.FromSeconds(3));await Wait(()=>isolated.ConnectionCount==0); }
            using var second=await Socket(isolated.LocalEndpoint);await using var next=await Connect(second,root,pin);
            await nextCalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(appCalls==2,"Application exception prevented future secure connections");
        }

        var stalledEntered=Signal();var release=Signal();
        var stalled=new TlsPacketListener(new IPEndPoint(IPAddress.Loopback,0),cert,async (_,_)=>{
            stalledEntered.TrySetResult();await release.Task;
        },maximumConnections:1,maximumHandshakes:1,shutdownTimeout:TimeSpan.FromMilliseconds(80));
        using(var client=await Socket(stalled.LocalEndpoint))
        await using(var channel=await Connect(client,root,pin))
        {
            await stalledEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try {await stalled.DisposeAsync();throw new Exception("Uncooperative handler was reported as stopped");}
            catch(TimeoutException) {check(!stalled.Completion.IsCompleted && stalled.ConnectionCount==1,"Timed-out shutdown discarded live worker ownership");}
            finally {release.TrySetResult();}
            await stalled.Completion.WaitAsync(TimeSpan.FromSeconds(3));await stalled.DisposeAsync();
            check(stalled.ConnectionCount==0,"Delayed handler cleanup did not complete");
        }
        // Cancellation callbacks can fail; sockets/workers must still be cleaned and failure observable.
        var callbackReady=Signal();
        var badCallback=new TlsPacketListener(new IPEndPoint(IPAddress.Loopback,0),cert,async (_,ct)=>{
            using var registration=ct.Register(()=>throw new InvalidOperationException("synthetic cancellation failure"));
            callbackReady.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,ct);
        },maximumConnections:1,maximumHandshakes:1);
        using(var socket=await Socket(badCallback.LocalEndpoint))
        await using(var channel=await Connect(socket,root,pin))
        {
            await callbackReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
            bool failedStop=false;try {await badCallback.DisposeAsync();}catch(AggregateException){failedStop=true;}
            check(failedStop && badCallback.ConnectionCount==0 && badCallback.Completion.IsFaulted,
                "Cancellation callback failure must remain observable after owned resources drain");
        }
        // Validate before binding, including inconsistent budgets and unusable credentials.
        foreach(var make in new Func<TlsPacketListener>[] {
            ()=>new(new IPEndPoint(IPAddress.Loopback,0),cert,(_,_)=>Task.CompletedTask,maximumConnections:0),
            ()=>new(new IPEndPoint(IPAddress.Loopback,0),cert,(_,_)=>Task.CompletedTask,maximumConnections:1,maximumHandshakes:2),
            ()=>new(new IPEndPoint(IPAddress.Loopback,0),cert,(_,_)=>Task.CompletedTask,maximumPacket:0),
            ()=>new(new IPEndPoint(IPAddress.Loopback,0),cert,(_,_)=>Task.CompletedTask,shutdownTimeout:TimeSpan.Zero) })
        {
            try {await using var bad=make();throw new Exception("Invalid listener options accepted");}
            catch(ArgumentOutOfRangeException) {check(true,"Invalid limits refused");}
        }
    }
}
