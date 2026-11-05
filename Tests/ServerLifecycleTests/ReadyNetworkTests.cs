using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using Authentication;
using Connections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using LiteNetLib;
using LiteNetLib.Utils;
using PhinixServer;
using PhinixServer.Framework;
using ServerRuntime;
using UserManagement;
using Utils;
using Utils.Framework;

internal static class ReadyNetworkTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private sealed class Peer : IDisposable
    {
        public readonly NetManager Client;
        public NetPeer Connection;
        public string Session, Principal;
        public readonly List<FrameworkPacket> Framework = new List<FrameworkPacket>();
        public string Error;
        public int AuthResponses;
        private bool pauseLogin;
        private readonly string key;
        public Peer(int port, string key)
        {
            this.key = key;
            var events = new EventBasedNetListener();
            Client = new NetManager(events, "Phinix");
            events.PeerConnectedEvent += peer => Connection = peer;
            events.NetworkReceiveEvent += Receive;
            Check(Client.Start(), "Client failed to start");
            Connection = Client.Connect("127.0.0.1", port);
        }
        private void Send(string module, byte[] bytes)
        {
            var writer = new NetDataWriter(); writer.Put(module); writer.Put(bytes);
            Connection.Send(writer, SendOptions.ReliableOrdered);
        }
        private void Receive(NetPeer peer, NetDataReader reader)
        {
            try
            {
                string module = reader.GetString(); byte[] bytes = reader.GetRemainingBytes();
                if (module == FrameworkProtocol.ModuleName) { Framework.Add(FrameworkSerialization.DeserializePacket(bytes)); return; }
                var envelope = Any.Parser.ParseFrom(bytes);
                if (envelope.Is(HelloPacket.Descriptor))
                {
                    Session = envelope.Unpack<HelloPacket>().SessionId;
                    Send("Authentication", ProtobufPacketHelper.Pack(new AuthenticatePacket { SessionId = Session, AuthType = AuthTypes.ClientKey, Username = key }).ToByteArray());
                }
                else if (envelope.Is(AuthResponsePacket.Descriptor))
                {
                    Check(envelope.Unpack<AuthResponsePacket>().Success, "Authentication failed");
                    AuthResponses++;
                    if (pauseLogin) return;
                    Send("UserManagement", ProtobufPacketHelper.Pack(new LoginPacket { SessionId = Session, DisplayName = "synthetic-ready", AcceptingTrades = true }).ToByteArray());
                }
                else if (envelope.Is(LoginResponsePacket.Descriptor))
                {
                    var login = envelope.Unpack<LoginResponsePacket>(); Check(login.Success, "Login failed"); Principal = login.Uuid;
                }
            }
            catch { Error = "Protocol decoding/handshake failed"; }
        }
        public void ReauthenticateOnly(string newKey)
        {
            pauseLogin = true;
            Send("Authentication", ProtobufPacketHelper.Pack(new AuthenticatePacket { SessionId = Session, AuthType = AuthTypes.ClientKey, Username = newKey }).ToByteArray());
        }
        public string Hello(int version = 2, params string[] caps)
        {
            var packet = new FrameworkPacket { Kind = FrameworkProtocol.KindHello, MessageId = Guid.NewGuid().ToString(), Version = version, SessionId = Session, SenderUuid = Principal,
                PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkCapabilitiesPayload { Capabilities = caps.ToList() }) };
            Send(FrameworkProtocol.ModuleName, FrameworkSerialization.SerializePacket(packet)); return packet.MessageId;
        }
        public string UnsupportedCommand()
        {
            var packet = new FrameworkPacket { Kind = FrameworkProtocol.KindCommand, MessageId = Guid.NewGuid().ToString(), MessageType = "synthetic.unsupported",
                SessionId = Session, SenderUuid = Principal, PayloadJson = "{}" };
            packet.SetCorrelationId("synthetic-stable-operation");
            Send(FrameworkProtocol.ModuleName, FrameworkSerialization.SerializePacket(packet)); return packet.MessageId;
        }
        public void PumpUntil(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < 5000) { Client.PollEvents(); if (Error != null) throw new Exception(Error); Thread.Sleep(5); }
            Check(condition(), "Ready network condition deadline");
        }
        public void PumpFor(int milliseconds)
        {
            var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < milliseconds) { Client.PollEvents(); Thread.Sleep(5); }
            Check(Error == null, "Unexpected network decode failure");
        }
        public void Dispose() { Client.Stop(); }
    }
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "phinix-ready-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var net = new NetServer(new IPEndPoint(IPAddress.Any, 0), 4);
        var auth = new ServerAuthenticator(net, "synthetic", "synthetic", AuthTypes.ClientKey);
        var users = new ServerUserManager(net, auth, 100);
        PhinixFrameworkServer framework = null;
        try
        {
            var context = new ExtensionHostContext { HostKind = "server", StorageProvider = new FileSystemExtensionStorageProvider(directory) };
            context.AddService<IServerUserManager>(users);
            using var workQueue = new ServerWorkQueue();
            context.AddService<IServerWorkQueue>(workQueue);
            net.PollCompleted += (_, _) => workQueue.Drain();
            context.AddService<IExtensionConfigProvider>(new Config());
            var dispatcher = new FrameworkServerPacketDispatcher();
            context.AddService<IFrameworkServerPacketDispatcher>(dispatcher);
            context.AddService<IResultServerPacketDispatcher>(dispatcher);
            ExtensionAssemblyLoader.LoadAssemblies(new[] { Path.Combine(Directory.GetCurrentDirectory(), "Server/bin/Release/net10.0/Extensions") });
            framework = new PhinixFrameworkServer(net, auth, users, context);
            dispatcher.ConfigureWithResult((connection, source, packet) => framework.DispatchExtensionPacketWithResult(source, connection, packet));
            var ready = context.GetRequiredService<IServerSessionReadiness>();
            int readyCount = 0; string epoch = null, currentConnection = null;
            ready.SessionReady += (_, args) => { epoch = args.Epoch; currentConnection = args.ConnectionId; Interlocked.Increment(ref readyCount); };
            net.Start();
            string key = Guid.NewGuid().ToString("N"); string principal, initialEpoch = null;
            using (var peer = new Peer(net.LocalPort, key))
            {
                peer.PumpUntil(() => peer.Principal != null); principal = peer.Principal;
                Check(readyCount == 0, "Login alone emitted Ready");
                // Seed an empty cancelled trade using the real store transitions/notification creation.
                // This tests protocol notification timing only, never game assets/conservation.
                var persistent = context.Persistents.Single(p => p.LogicalName == "trade-state.bin").Persistent;
                object store = persistent.GetType().GetField("store", Private).GetValue(persistent);
                object[] create = { principal, "synthetic-other", null, null };
                Check((bool)store.GetType().GetMethod("TryCreate").Invoke(store, create), "Real store create failed");
                string trade = (string)create[2].GetType().GetProperty("TradeId").GetValue(create[2]);
                object[] cancel = { trade, principal, null, true, null, false, false, null };
                Check((bool)store.GetType().GetMethod("TrySetStatus").Invoke(store, cancel), "Real store cancel failed");
                store.GetType().GetMethod("QueueCompletionNotifications").Invoke(store, new object[] { cancel[4], true });
                // Model an imported ambiguous notification, not a newly proven-unsent Pending.
                foreach (object pending in (System.Collections.IEnumerable)store.GetType().GetField("pendingCompletionNotifications", Private).GetValue(store))
                    pending.GetType().GetProperty("DeliveryState").SetValue(pending, "OutcomeUnknown");
                var journal = (ServerStorage.SqliteOperationJournal)store.GetType().GetField("journal", Private).GetValue(store);
                byte[] seed = (byte[])store.GetType().GetMethod("ExportState", Private).Invoke(store, null);
                journal.Execute("test.seed", "ambiguous", new byte[] { 1 }, tx => { tx.WriteState("trade-ledger-v1", seed); return new byte[] { 1 }; });
                peer.PumpFor(100);
                Check(peer.Framework.Count == 0, "Trade recovery sent before negotiation");
                peer.Principal = "synthetic-wrong-principal";
                string wrongIdentity = peer.Hello(2, "builtin.trade", "trade.cancelled.event", "trade.state.snapshot");
                peer.PumpUntil(() => peer.Framework.Count > 0);
                AdmissionControlTests.Verify(peer.Framework.Last(), "not_authenticated", "Negotiation", wrongIdentity);
                Check(readyCount == 0, "Wrong identity negotiated Ready"); peer.Framework.Clear();
                peer.Principal = principal;
                string wrongVersion = peer.Hello(999, "builtin.trade", "trade.cancelled.event", "trade.state.snapshot");
                peer.PumpUntil(() => peer.Framework.Count > 0);
                AdmissionControlTests.Verify(peer.Framework.Last(), "unsupported_version", "Negotiation", wrongVersion);
                Check(readyCount == 0, "Unsupported version emitted Ready"); peer.Framework.Clear();
                peer.Hello(2, "builtin.trade", "trade.cancelled.event", "trade.state.snapshot");
                peer.PumpUntil(() => peer.Framework.Any(p => p.MessageType == "trade.state.snapshot") && readyCount == 1);
                Check(peer.Framework.First().Kind == FrameworkProtocol.KindCapabilities, "Recovery preceded capability response");
                Check(((Array)store.GetType().GetMethod("GetPendingCompletionNotificationsFor").Invoke(store, new object[] { principal })).Length == 1,
                    "Ready removed ambiguous pending record");
                string oldEpoch = epoch; initialEpoch = epoch;
                peer.Hello(2, "trade.state.snapshot", "trade.cancelled.event", "builtin.trade"); peer.PumpFor(100);
                Check(readyCount == 1 && epoch == oldEpoch && peer.Framework.Count(p => p.MessageType == "trade.state.snapshot") == 1 && !peer.Framework.Any(p => p.MessageType == "trade.cancelled.event"),
                    "Repeated Hello re-executed recovery");
                int responses = peer.Framework.Count;
                string conflict = peer.Hello(2, "builtin.trade");
                peer.PumpUntil(() => peer.Framework.Count > responses);
                AdmissionControlTests.Verify(peer.Framework.Last(), "hello_conflict", "Negotiation", conflict);
                Check(peer.Framework.Count == responses + 1 && ready.TryGetReady(currentConnection, out var pinned) && pinned.Capabilities.Contains("trade.cancelled.event"),
                    "Conflicting Hello replaced capabilities");
                string denied = peer.UnsupportedCommand();
                peer.PumpUntil(() => peer.Framework.Any(p => p.GetCorrelationId() == "synthetic-stable-operation"));
                AdmissionControlTests.Verify(peer.Framework.Last(p => p.GetCorrelationId() == "synthetic-stable-operation"), "unsupported_capability", "Admission", denied);
                Check(readyCount == 1, "Business capability refusal invalidated unrelated session");
                var sessions = (Dictionary<string, Session>)typeof(ServerAuthenticator).GetField("sessions", Private).GetValue(auth);
                object sessionGate = typeof(ServerAuthenticator).GetField("sessionsLock", Private).GetValue(auth);
                lock (sessionGate) sessions[currentConnection].Expiry = DateTime.UtcNow.AddMinutes(-1);
                Check(dispatcher.SendWithResult(currentConnection, new FrameworkPacket { MessageType = "trade.cancelled.event" }).For(currentConnection) == TransportSendOutcome.NotSent,
                    "Expired recipient remained subscribed");
                Check(!ready.TryGetReady(currentConnection, out _), "Expired session remained Ready");
            }
            net.DisconnectPeer(currentConnection);
            using (var reconnected = new Peer(net.LocalPort, key))
            {
                reconnected.PumpUntil(() => reconnected.Principal != null);
                Check(reconnected.Principal == principal, "Reconnect changed persistent identity");
                reconnected.Hello(2, "builtin.trade", "trade.cancelled.event", "trade.state.snapshot");
                reconnected.PumpUntil(() => readyCount == 2 && reconnected.Framework.Any(p => p.MessageType == "trade.state.snapshot"));
                Check(epoch != initialEpoch && ready.TryGetReady(currentConnection, out _), "Reconnect did not establish a fresh epoch");
                reconnected.PumpFor(100);
                Check(!reconnected.Framework.Any(p => p.MessageType == "trade.cancelled.event"), "Ambiguous pending delivery blindly replayed");
                reconnected.ReauthenticateOnly(Guid.NewGuid().ToString("N"));
                reconnected.PumpUntil(() => reconnected.AuthResponses == 2);
                Check(!ready.TryGetReady(currentConnection, out _), "Credential switch retained old principal authorization before relogin");
            }
            Console.WriteLine("READY NETWORK RESULT passed; real auth/login/hello/version/replay/conflict/expiry/reconnect/plugin-snapshot/ambiguous-delivery-held/correlated-control; no game ACK");
        }
        finally
        {
            net.Stop(); framework?.ShutdownExtensions(); auth.Stop();
            Directory.Delete(directory, true);
        }
    }
}
