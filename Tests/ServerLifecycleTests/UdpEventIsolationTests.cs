using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Connections;
using LiteNetLib;
using Utils;

internal static class UdpEventIsolationTests
{
    private static int checks;
    private static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    private static void Wait(NetManager client, Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.ElapsedMilliseconds < 5000) { client?.PollEvents(); Thread.Sleep(5); }
        Check(ready(), "UDP event audit deadline");
    }
    public static void BindPolicy()
    {
        using var server = new NetServer(new IPEndPoint(IPAddress.Loopback, 0), 1);
        bool refused = false;
        try { server.Start(); } catch (NotSupportedException) { refused = true; }
        Check(refused && !server.Listening, "UDP silently ignored requested bind address instead of failing closed");
        Console.WriteLine("UDP BIND POLICY RESULT passed; unsupported-specific-address-fails-before-listening");
    }
    public static void Run()
    {
        checks = 0;
        using var server = new NetServer(new IPEndPoint(IPAddress.Any,0), 3);
        int poll = 0, laterPoll = 0, established = 0, laterEstablished = 0, laterClosed = 0, laterLogs = 0;
        string connection = null, logs = "";
        server.OnLogEntry += (_, _) => throw new InvalidOperationException("synthetic-private-log");
        server.OnLogEntry += (_, e) => { logs += e.Message; Interlocked.Increment(ref laterLogs); };
        server.PollCompleted += (_, _) => { if (Interlocked.Increment(ref poll) == 1) throw new InvalidOperationException("synthetic-private-poll"); };
        server.PollCompleted += (_, _) => Interlocked.Increment(ref laterPoll);
        server.OnConnectionEstablished += (_, _) => { if (Interlocked.Increment(ref established) == 1) throw new InvalidOperationException("synthetic-private-connect"); };
        server.OnConnectionEstablished += (_, e) => { connection = e.ConnectionId; Interlocked.Increment(ref laterEstablished); };
        server.OnConnectionClosed += (_, _) => throw new InvalidOperationException("synthetic-private-close");
        server.OnConnectionClosed += (_, _) => Interlocked.Increment(ref laterClosed);
        server.Start();
        Wait(null, () => Volatile.Read(ref laterPoll) > 0);
        var client = new NetManager(new EventBasedNetListener(), "Phinix");
        try
        {
            Check(client.Start(), "First client did not start"); client.Connect("127.0.0.1",server.LocalPort);
            Wait(client, () => Volatile.Read(ref laterEstablished) == 1 && Volatile.Read(ref laterClosed) == 1);
            Check(server.TrySendWithOutcome(connection, "synthetic", new byte[] {1}) == TransportSendOutcome.NotSent,
                "Connection with failed establishment observer remained admitted");
        }
        finally { client.Stop(); }
        client = new NetManager(new EventBasedNetListener(), "Phinix");
        try
        {
            Check(client.Start(), "Second client did not start"); client.Connect("127.0.0.1",server.LocalPort);
            Wait(client, () => Volatile.Read(ref laterEstablished) == 2);
            Check(server.TrySendWithOutcome(connection,"synthetic",new byte[] {1}) == TransportSendOutcome.Submitted,
                "One bad callback disabled subsequent independent connections");
            server.DisconnectPeer(connection);
            Wait(client, () => Volatile.Read(ref laterClosed) == 2);
        }
        finally { client.Stop(); }
        server.Stop();
        Check(laterLogs >= 3 && !logs.Contains("synthetic-private"), "Failed callback suppressed later diagnostic subscriber or leaked its exception contents");
        Console.WriteLine($"UDP EVENT ISOLATION RESULT passed; checks={checks}; realUDP/poll-survives/connect-fails-closed/next-connection-recovers/close-isolation");
    }
}
