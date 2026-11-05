using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using Connections;
using LiteNetLib;

internal static class SendOutcomeTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Send(NetServer server, string id, string module, byte[] payload)
    {
        var method = typeof(NetServer).GetMethod("TrySendWithOutcome");
        Check(method != null, "Transport has no API distinguishing not-sent/submitted/unknown");
        return method.Invoke(server, new object[] { id, module, payload }).ToString();
    }
    public static void Basic()
    {
        using var server = new NetServer(new IPEndPoint(IPAddress.Loopback, 0), 2);
        Check(Send(server, "absent", "synthetic", new byte[] { 1 }) == "NotSent", "Absent peer must be explicitly not sent");
        Check(Send(server, null, "synthetic", new byte[] { 1 }) == "NotSent", "Invalid ID must be not sent");
        Check(Send(server, "absent", null, new byte[] { 1 }) == "NotSent", "Invalid module must be not sent");
        Check(Send(server, "absent", "synthetic", null) == "NotSent", "Null payload must be not sent");
        var attempt = typeof(NetServer).GetMethod("AttemptTransportSend", BindingFlags.Static | BindingFlags.NonPublic);
        Check(attempt != null, "Transport handoff boundary missing");
        string Attempt(Func<bool> connected, Action handoff) => attempt.Invoke(null, new object[] { connected, handoff }).ToString();
        int calls = 0;
        Check(Attempt(() => false, () => calls++) == "NotSent" && calls == 0, "Disconnected check invoked handoff");
        Check(Attempt(() => true, () => { calls++; throw new InvalidOperationException("synthetic-private"); }) == "Unknown" && calls == 1,
            "Exception after handoff must remain unknown, without retry");
        calls = 0;
        Check(Attempt(() => calls == 0, () => calls++) == "Unknown" && calls == 1, "Disconnect during handoff must remain unknown");
        Check(Attempt(() => true, () => { }) == "Submitted", "Successful API return should indicate submission only");
        Console.WriteLine("SEND OUTCOME RESULT passed; basic=8; no delivery/commit assertion");
    }
    public static void Network()
    {
        using var server = new NetServer(new IPEndPoint(IPAddress.Any, 0), 2);
        var listener = new EventBasedNetListener();
        var client = new NetManager(listener, "Phinix");
        string connection = null;
        byte[] received = null;
        string module = null;
        using var connected = new ManualResetEventSlim();
        server.OnConnectionEstablished += (_, e) => { connection = e.ConnectionId; connected.Set(); };
        listener.NetworkReceiveEvent += (_, reader) => { module = reader.GetString(); received = reader.GetRemainingBytes(); };
        try
        {
            server.Start();
            int port = ((NetManager)typeof(NetServer).GetField("server", Private).GetValue(server)).LocalPort;
            using (var conflict = new NetServer(new IPEndPoint(IPAddress.Any, port), 1))
            {
                bool refused = false;
                try { conflict.Start(); } catch (System.IO.IOException) { refused = true; }
                Check(refused && !conflict.Listening, "Failed bind reported startup success");
                Check(typeof(NetServer).GetField("pollThread", Private).GetValue(conflict) == null, "Failed bind started polling worker");
            }
            Check(port != 0 && client.Start(), "Actual UDP endpoints failed to start");
            client.Connect("127.0.0.1", port);
            var deadline = Stopwatch.StartNew();
            while (!connected.IsSet && deadline.ElapsedMilliseconds < 5000) { client.PollEvents(); Thread.Sleep(5); }
            Check(connected.IsSet, "Actual client did not connect");
            byte[] expected = { 0, 1, 255, 17 };
            Check(Send(server, connection, "synthetic.transport", expected) == "Submitted", "Real peer did not report API submission");
            deadline.Restart();
            while (received == null && deadline.ElapsedMilliseconds < 5000) { client.PollEvents(); Thread.Sleep(5); }
            Check(module == "synthetic.transport" && received != null && expected.SequenceEqual(received), "Real wire module/payload mismatch");
            server.DisconnectPeer(connection);
            Check(Send(server, connection, "synthetic.transport", expected) == "NotSent", "Explicitly removed peer must not submit");
            Check(!server.TrySend(connection, "synthetic.transport", expected), "Legacy bool API reported success for removed peer");
            Console.WriteLine("SEND NETWORK RESULT passed; connected-submission/exact-wire/disconnected-refusal; no application ACK");
        }
        finally { client.Stop(); }
    }
}
