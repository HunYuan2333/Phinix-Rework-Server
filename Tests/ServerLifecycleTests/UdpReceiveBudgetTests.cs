using System;
using System.Buffers.Binary;
using System.Collections;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using Connections;
using LiteNetLib;
using LiteNetLib.Utils;

internal static class UdpReceiveBudgetTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    internal static void Run()
    {
        using var host = new NetServer(new IPEndPoint(IPAddress.Any, 0), 2);
        var backend = (NetManager)typeof(NetServer).GetField("server", Private).GetValue(host);
        object socket = typeof(NetManager).GetField("_socket", Private).GetValue(backend);
        var callback = (Delegate)socket.GetType().GetField("_onMessageReceived", Private).GetValue(socket);
        var receive = (Action<byte[], int, int, NetEndPoint>)callback.Method.CreateDelegate(
            typeof(Action<byte[], int, int, NetEndPoint>), callback.Target);
        var queue = (ICollection)typeof(NetManager).GetField("_netEventsQueue", Private).GetValue(backend);
        var heldFragments = typeof(NetPeer).GetField("_holdedFragments", Private);
        object gate = callback.Target.GetType().GetProperty("PeerGate", Private).GetValue(callback.Target);

        var client = new NetManager(new EventBasedNetListener(), "Phinix");
        var guard = callback.Target.GetType().GetMethod("Install", Private).Invoke(null, new object[] { client });
        object clientGate = guard.GetType().GetProperty("PeerGate", Private).GetValue(guard);
        using var connected = new ManualResetEventSlim();
        using var received = new ManualResetEventSlim();
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var fault = new ManualResetEventSlim();
        byte[] expected = Enumerable.Repeat((byte)0x5a, 1024 * 1024).ToArray();
        bool payloadMatches = false;
        int small = 0;
        host.OnConnectionEstablished += (_, _) => connected.Set();
        host.RegisterPacketHandler("large", (_, _, body) => { payloadMatches = expected.SequenceEqual(body); received.Set(); });
        host.RegisterPacketHandler("small", (_, _, body) => Interlocked.Increment(ref small));
        host.PollCompleted += (_, _) =>
        {
            if (received.IsSet && !resume.IsSet) { paused.Set(); resume.Wait(15000); }
        };
        string failure = null;
        host.WorkerFailed += type => { failure = type; fault.Set(); };

        host.Start();
        try
        {
            Require(client.Start(), "UDP fixture client failed to bind");
            var peer = client.Connect("127.0.0.1", host.LocalPort);
            Require(connected.Wait(5000), "UDP fixture did not connect");
            NetPeer remote;
            lock (gate) remote = backend.GetPeers().Single();
            var writer = new NetDataWriter();
            writer.Put("large");
            writer.Put(expected);
            Require(SpinWait.SpinUntil(() => peer.ConnectionState == ConnectionState.Connected, 5000),
                "UDP fixture did not finish handshake");
            lock (clientGate) peer.Send(writer, SendOptions.ReliableOrdered);
            Require(paused.Wait(15000) && payloadMatches, "Valid fragmented payload did not reach module intact");

            // Inject wire inputs at the installed socket callback with the actual backend
            // peer and a fixed poll barrier. No production client can trigger the barrier.
            var fragment = new byte[10];
            fragment[0] = 0x81; // Reliable unordered, fragmented.
            BinaryPrimitives.WriteUInt16LittleEndian(fragment.AsSpan(7), ushort.MaxValue);
            receive(fragment, fragment.Length, 0, remote.EndPoint);
            var merged = new byte[] { 0x13, 10, 0 }.Concat(fragment).ToArray();
            receive(merged, merged.Length, 0, remote.EndPoint);
            lock (gate) Require(((IDictionary)heldFragments.GetValue(remote)).Count == 0,
                "Oversized fragment reached allocation, directly or through merge");

            BinaryPrimitives.WriteUInt16LittleEndian(fragment.AsSpan(7), 4096);
            for (ushort sequence = 0; sequence < 12; sequence++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(fragment.AsSpan(1), sequence);
                BinaryPrimitives.WriteUInt16LittleEndian(fragment.AsSpan(3), sequence);
                receive(fragment, fragment.Length, 0, remote.EndPoint);
            }
            lock (gate)
            {
                int count = ((IDictionary)heldFragments.GetValue(remote)).Count;
                Require(count == 1, $"Expected one admitted incomplete group, got {count}");
            }

            writer.Reset();
            writer.Put("small");
            writer.Put((byte)1);
            var datagram = new byte[] { 0 }.Concat(writer.CopyData()).ToArray();
            lock (queue)
            {
                var evtType = queue.GetType().GetGenericArguments()[0];
                var enqueue = queue.GetType().GetMethod("Enqueue");
                for (int i = 0; i < 32; i++)
                {
                    object evt = Activator.CreateInstance(evtType, nonPublic: true);
                    var data = (NetDataReader)evtType.GetField("DataReader").GetValue(evt);
                    data.SetSource(expected);
                    enqueue.Invoke(queue, new[] { evt });
                }
            }
            receive(datagram, datagram.Length, 0, remote.EndPoint);
            lock (queue)
            {
                Require(queue.Count == 32, "Byte watermark did not stop ingress below the event limit");
                queue.GetType().GetMethod("Clear").Invoke(queue, null);
            }
            for (int i = 0; i < 600; i++) receive(datagram, datagram.Length, 0, remote.EndPoint);
            lock (queue) Require(queue.Count == 512, "UDP input was not bounded at the event high-water mark");
            resume.Set();
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref small) == 512, 5000), "Admitted events did not drain");
            receive(datagram, datagram.Length, 0, remote.EndPoint);
            Require(SpinWait.SpinUntil(() => Volatile.Read(ref small) == 513, 5000), "UDP ingress did not recover after drain");

            // Fixed backend fault: PollEvents dequeues this malformed internal entry.
            lock (queue) queue.GetType().GetMethod("Enqueue").Invoke(queue, new object[] { null });
            Require(fault.Wait(5000) && failure == "NullReferenceException", "Unexpected poll failure did not reach host owner");
        }
        finally { resume.Set(); client.Stop(); host.Stop(); }
        Console.WriteLine("UDP RECEIVE BUDGET RESULT passed; fragmented 1 MiB/oversized+merged/multi-group/queue drain/poll fault");
    }
}
