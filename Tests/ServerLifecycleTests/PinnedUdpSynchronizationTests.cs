using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Connections;
using LiteNetLib;

internal static class PinnedUdpSynchronizationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run()
    {
        using var server = new NetServer(new IPEndPoint(IPAddress.Loopback, 0), 2);
        var manager = (NetManager)typeof(NetServer).GetField("server", Private).GetValue(server);
        object peers = typeof(NetManager).GetField("_peers", Private).GetValue(manager);
        object socket = typeof(NetManager).GetField("_socket", Private).GetValue(manager);
        var callback = (Delegate)socket.GetType().GetField("_onMessageReceived", Private).GetValue(socket);
        Check(callback.Target.GetType().Name == "PinnedUdpPeerSynchronization", "Socket callback was left unsynchronized");
        var property = callback.Target.GetType().GetProperty("PeerGate", Private);
        Check(ReferenceEquals(property.GetValue(callback.Target), peers), "Socket synchronization does not use backend logic gate");
        Check(!manager.UnsyncedEvents, "Business callbacks must stay on poll worker");
        // Stable lock contract, not a probabilistic reconnect loop. Private constructor
        // isolates only the callback; production has no remotely selectable fault mode.
        var type = callback.Target.GetType(); object gate = new object(); bool inside = false;
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Action<byte[], int, int, NetEndPoint> sink = (_, __, ___, ____) => inside = Monitor.IsEntered(gate);
        var guard = Activator.CreateInstance(type, Private, null, new object[] { gate, sink }, null);
        var guardedReceive = (Action<byte[], int, int, NetEndPoint>)type.GetMethod("Receive", Private).CreateDelegate(typeof(Action<byte[], int, int, NetEndPoint>), guard);
        var logic = Task.Run(() => { lock (gate) { held.Set(); release.Wait(); } });
        Check(held.Wait(2000), "Logic barrier not entered");
        var receive = Task.Run(() => guardedReceive(Array.Empty<byte>(), 0, 0, null));
        release.Set(); Check(Task.WaitAll(new[] { logic, receive }, 2000), "Socket/logic barrier did not complete");
        Check(inside, "Receive callback ran outside peer mutation gate");
        P3NetworkProbe.CheckProbeDisconnectGate();
        Console.WriteLine("PINNED UDP SYNCHRONIZATION RESULT passed; checks=6; deterministic-peer-gate; no vendor mutation");
    }
}
