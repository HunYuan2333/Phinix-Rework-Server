using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Connections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using Phinix.LegacyAdapter.Server;
using Phinix.TradeExtension.Server;
using ServerRuntime;
using UserManagement;
using Utils;
using Utils.Framework;

internal static partial class Program
{
    private static byte[] CreateWire(string owner = "a", string session = "session-a")
    {
        using var stream = new MemoryStream(); using (var w = new CodedOutputStream(stream, true))
        {
            foreach (var pair in new[] { (1, session), (2, owner), (3, "b") }) { w.WriteTag(pair.Item1, WireFormat.WireType.LengthDelimited); w.WriteString(pair.Item2); }
            w.Flush();
        }
        return new Any { TypeUrl = "Phinix/Trading.CreateTradePacket", Value = ByteString.CopyFrom(stream.ToArray()) }.ToByteArray();
    }
    private static void EndpointBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "phinix-trade-endpoint-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(root);
        try
        {
            var users = new RouteUsers(); var wire = new RouteWire(); var ready = new Ready();
            var service = new PhinixFrameworkTradeServerService(users, ready); service.Load(Path.Combine(root, "trades"));
            using var adapter = new OriginalTradeAdapter(wire, users, users, users, ready, service, (_, __) => { });
            adapter.Start(); users.Login("a"); users.Login("b");
            Check(adapter.TryGetSession("connection-a", out var provisional) && !provisional.Confirmed, "Login alone falsely identified exact original profile");
            Check(wire.Sent.All(p => Any.Parser.ParseFrom(p).TypeUrl == "Phinix/Trading.SyncTradesPacket"), "Login bootstrap attempted delivery");
            wire.Sent.Clear();
            wire.Receive("connection-a", CreateWire("b"));
            Check(wire.Sent.Count == 0 && adapter.TryGetSession("connection-a", out var stillProvisional) && !stillProvisional.Confirmed, "Spoofed principal selected alternate endpoint");
            ready.Selected = true; wire.Receive("connection-a", CreateWire());
            Check(wire.Sent.Count == 0 && !adapter.TryGetSession("connection-a", out _), "Ready Framework route accepted original traffic");
            ready.Selected = false;
            using (var collision = new OriginalTradeAdapter(wire, users, users, users, ready, service, (_, __) => { }))
            {
                try { collision.Start(); throw new Exception("Duplicate endpoint provider accepted"); }
                catch (InvalidOperationException) { checks++; }
            }
            Check(wire.Handler != null, "Failed activation unregistered the running adapter");
            wire.Receive("connection-a", CreateWire());
            Check(adapter.TryGetSession("connection-a", out var confirmed) && confirmed.Confirmed && wire.Sent.Any(p => Any.Parser.ParseFrom(p).TypeUrl == "Phinix/Trading.CreateTradeResponsePacket"), "Actual authenticated raw request did not select original route");
            users.Revoked = true;
            Check(!adapter.TryGetSession("connection-a", out _), "Revoked authenticated session still authorized endpoint output");
            adapter.Dispose(); Check(wire.Handler == null && users.Subscribers == 0, "Adapter shutdown leaked route or login subscription");
            users.Revoked = false; adapter.Start(); users.Login("a");
            Check(adapter.TryGetSession("connection-a", out var restarted) && !restarted.Confirmed, "Reactivation retained prior session selection");
        }
        finally { SqliteConnection.ClearAllPools(); System.IO.Directory.Delete(root, true); }
    }
    private sealed class RouteUsers : IServerUserManager, IServerPrincipalAuthorization, IServerAuthenticatedSessionLookup
    {
        private EventHandler<ServerLoginEventArgs> login;
        public int Subscribers => login?.GetInvocationList().Length ?? 0;
        public bool Revoked;
        public event EventHandler<ServerLoginEventArgs> OnLogin { add => login += value; remove => login -= value; }
        public void Login(string user) => login?.Invoke(this, new ServerLoginEventArgs("connection-" + user, user));
        public bool IsAuthenticatedPrincipal(string c, string s, string p) => !Revoked && c == "connection-" + p && s == "session-" + p;
        public bool TryGetAuthenticatedSession(string c, string p, out string s) { s = "session-" + p; return IsAuthenticatedPrincipal(c, s, p); }
        public bool IsLoggedIn(string c, string p) => c == "connection-" + p;
        public string[] GetConnections() => new[] { "connection-a", "connection-b" };
        public bool TryGetConnection(string p, out string c) { c = "connection-" + p; return p is "a" or "b"; }
        public bool TryGetDisplayName(string p, out string name) { name = p; return true; }
        public bool TryGetLoggedIn(string p, out bool value) { value = true; return true; }
        public bool TryGetAcceptingTrades(string p, out bool value) { value = true; return true; }
    }
    private sealed class RouteWire : IServerTransport
    {
        public NetCommon.PacketHandlerDelegate Handler;
        public readonly List<byte[]> Sent = new();
        public void Receive(string c, byte[] p) => Handler("Trading", c, p);
        public IPEndPoint Endpoint => new(IPAddress.Loopback, 0); public int LocalPort => 0; public bool Listening => true;
        public event EventHandler<LogEventArgs> OnLogEntry { add { } remove { } } public void RaiseLogEntry(LogEventArgs e) { }
        public event EventHandler PollCompleted { add { } remove { } }
        public event EventHandler<ConnectionEventArgs> OnConnectionEstablished { add { } remove { } }
        public event EventHandler<ConnectionEventArgs> OnConnectionClosed { add { } remove { } }
        public void RegisterPacketHandler(string m, NetCommon.PacketHandlerDelegate h) { if (Handler != null) throw new InvalidOperationException(); Handler = h; }
        public void UnregisterPacketHandler(string m) => Handler = null;
        public void UnregisterAllPacketHandlers() => Handler = null;
        public void Start() { } public void Stop() { } public void Dispose() { }
        public void Send(string c, string m, byte[] p) => TrySend(c, m, p);
        public bool TrySend(string c, string m, byte[] p) => TrySendWithOutcome(c, m, p) == TransportSendOutcome.Submitted;
        public TransportSendOutcome TrySendWithOutcome(string c, string m, byte[] p) { Sent.Add(p); return TransportSendOutcome.Submitted; }
        public void DisconnectPeer(string c) { }
    }
}
