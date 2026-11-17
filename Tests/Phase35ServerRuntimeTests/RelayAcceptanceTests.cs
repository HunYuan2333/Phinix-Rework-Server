using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Connections;
using Google.Protobuf;
using ServerRuntime;
using Utils.Framework;

internal static partial class Program
{
    // Component cross-version matrix: frozen old wire shape and old pipeline,
    // current serializers, current client sync service, and current server pipeline.
    // It deliberately does not claim that an old release process or game was launched.
    private static void AssertRelayCompatibilityMatrix()
    {
        foreach (bool newClient in new[] { false, true })
        foreach (bool newServer in new[] { false, true })
        foreach (bool protobuf in new[] { false, true })
        {
            var harness = new PipelineTestHarness();
            int handled = 0, observed = 0;
            harness.DefaultCommandHandlers.Add(new DelegateServerDefaultCommandHandler(p => p.MessageType == "trade.request", (p, _) =>
            {
                if (protobuf)
                    Assert(global::Phinix.Framework.FrameworkCommandPacket.Parser.ParseFrom(p.PayloadBytes).CommandKind ==
                        global::Phinix.Framework.FrameworkCommandKind.Request, "Matrix protobuf survives old/new envelope readers.");
                else Assert(p.PayloadJson == "{\"caseId\":\"matrix\"}", "Matrix JSON payload preserved.");
                handled++;
                return new ServerIncomingCommandResult { Action = MessageHandlingResultAction.Handled };
            }));
            harness.CommandObservers.Add(new DelegateServerCommandObserver(_ => true, (_, __, ___) => observed++));
            var old = new LegacyServerPipelineRunner(harness.InboundMessageInterceptors, harness.DefaultMessageHandlers,
                harness.MessageObservers, harness.InboundCommandInterceptors, harness.DefaultCommandHandlers,
                harness.CommandObservers, harness.OutboundPacketInterceptors, harness.ItemCodecs);
            var outgoing = new FrameworkPacket { Kind = "command", MessageType = "trade.request",
                PayloadJson = "{\"caseId\":\"matrix\"}", PayloadBytes = protobuf ?
                    new global::Phinix.Framework.FrameworkCommandPacket { CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request }.ToByteArray() : Array.Empty<byte>() };
            string wire = newClient ? FrameworkSerialization.SerializePayload(outgoing) :
                FrameworkSerialization.SerializePayload(FrameworkSerialization.DeserializePayload<Utils.Framework.LegacyWire.FrameworkPacket>(
                    FrameworkSerialization.SerializePayload(outgoing)));
            var incoming = newServer ? FrameworkSerialization.DeserializePayload<FrameworkPacket>(wire) :
                FrameworkSerialization.DeserializePayload<FrameworkPacket>(FrameworkSerialization.SerializePayload(
                    FrameworkSerialization.DeserializePayload<Utils.Framework.LegacyWire.FrameworkPacket>(wire)));
            Assert(incoming.CommandRoute == null, "Missing route remains server processing in every matrix cell.");
            Assert(newServer ? harness.Runner.ProcessIncomingCommand(incoming, CreateServerContext()) :
                old.ProcessIncomingCommand(incoming, CreateServerContext()), "Old/new pipeline consumes known ordinary Command.");
            Assert(handled == 1 && observed == 1, "Every matrix cell executes ordinary handler and observer once.");
            incoming.MessageType = "unknown";
            Assert(!(newServer ? harness.Runner.ProcessIncomingCommand(incoming, CreateServerContext()) :
                old.ProcessIncomingCommand(incoming, CreateServerContext())), "Unknown ordinary Command stays unconsumed.");
            Assert(handled == 1 && observed == 1, "Unknown ordinary Command has no business side effects.");
            // The return path crosses the sender's reader as well.
            string reply = FrameworkSerialization.SerializePayload(incoming);
            Assert((newClient ? FrameworkSerialization.DeserializePayload<FrameworkPacket>(reply).MessageId :
                FrameworkSerialization.DeserializePayload<Utils.Framework.LegacyWire.FrameworkPacket>(reply).MessageId) == outgoing.MessageId,
                "Response correlation survives the reverse direction.");
            using var client = new ClientCommandRelay(new ClientCommandCapabilities(), _ =>
                throw new Exception("Unsupported Relay must never hit a legacy wire."));
            if (!newServer) Assert(client.SendRelay("author.research", "request", "{}").Result.Error == "UnsupportedRelay",
                "Old server path refuses Relay locally before any send.");
        }
        Console.WriteLine("RELAY MATRIX PASS: 4 client/server component combinations x 2 JSON/protobuf payloads; frozen old wire/runner.");
    }

    private sealed class RelayLoadFixture
    {
        public DateTime Time = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        public readonly HashSet<string> Live = new HashSet<string>();
        public readonly Dictionary<string, string> Epochs = new Dictionary<string, string>();
        public readonly ServerSessionRegistry Sessions;
        public readonly CommandCapabilityRouter Router;
        public RelayLoadFixture(int count, RelayLimits limits = null)
        {
            Sessions = new ServerSessionRegistry((c, p, s) => Live.Contains(c));
            Router = new CommandCapabilityRouter(Sessions, () => Live.ToArray(), p => Live.Contains(p), limits, () => Time);
            for (int i = 0; i < count; i++) Bind("c" + i);
        }
        public void Bind(string id)
        {
            Live.Add(id);
            string[] caps = { CommandRelayProtocol.Capability };
            if (!Sessions.TryBind(id, id, "synthetic", 2, caps, caps, out var session, out var error))
                throw new Exception("Synthetic session bind failed: " + error);
            Sessions.MarkReady(session); Epochs[id] = session.Epoch;
            Declare(id, 1, true);
        }
        public void Declare(string id, long revision, bool active)
        {
            var ack = Router.Apply(id, new CommandCapabilitySnapshot { Epoch = Epochs[id], Revision = revision,
                Capabilities = active ? new List<CommandCapability> { new CommandCapability {
                    PluginId = "author.research", CommandType = "request", Version = 1 } } : new List<CommandCapability>() });
            if (ack.Error != null) throw new Exception("Synthetic capability sync failed: " + ack.Error);
        }
        public RelayResult Route(string sender, FrameworkPacket packet, Func<FrameworkPacket, string[], PacketDispatchResult> deliver)
        {
            string error = Router.Admit(sender);
            return error == null ? Router.Route(sender, sender, Epochs[sender], packet, deliver) : RelayResult.Failure(packet.MessageId, error);
        }
        public static PacketDispatchResult Submitted(FrameworkPacket _, string[] targets) =>
            new PacketDispatchResult(targets.ToDictionary(t => t, t => TransportSendOutcome.Submitted));
        public int Count(string field) => ((IDictionary)typeof(CommandCapabilityRouter).GetField(field,
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Router)).Count;
    }

    private static void AssertRelaySustainedLoad()
    {
        var f = new RelayLoadFixture(3);
        long deliveries = 0;
        var packet = RelayPacket();
        // 30 virtual minutes, 10 requests/second, alternating unicast/broadcast.
        // Real router budgets advance via the injectable clock; no real-time soak claim.
        for (int tick = 0; tick < 18000; tick++)
        {
            f.Time = f.Time.AddMilliseconds(100);
            packet.CommandRoute.TargetPlayerUuid = tick % 2 == 0 ? "c1" : null;
            var result = f.Route("c0", packet, RelayLoadFixture.Submitted);
            if (result.Error != null || result.Deliveries.Count != (tick % 2 == 0 ? 1 : 2))
                throw new Exception("Sustained Relay delivery mismatch at " + tick);
            deliveries += result.Deliveries.Count;
        }
        Assert(deliveries == 27000, "30 virtual minutes: 18000 requests yield exactly 27000 recipient outcomes.");
        Assert(f.Count("entries") == 3 && f.Count("index") == 1, "Steady load does not grow capability/index cardinality.");
        Console.WriteLine("RELAY P01 PASS: 1800 virtual seconds; requests=18000; deliveries=27000.");
    }

    private static void AssertRelaySlowRecipient()
    {
        var f = new RelayLoadFixture(3);
        // Fault-injected bounded delivery boundary: keep one receiver full while
        // allowing the other receiver to progress, then drain and recover.
        int submitted = 0, rejected = 0;
        bool slow = true;
        PacketDispatchResult Deliver(FrameworkPacket _, string[] targets) => new PacketDispatchResult(targets.ToDictionary(t => t, t =>
        {
            if (t == "c1" && slow) { rejected++; return TransportSendOutcome.NotSent; }
            submitted++; return TransportSendOutcome.Submitted;
        }));
        for (int i = 0; i < 1000; i++)
        {
            f.Time = f.Time.AddMilliseconds(100);
            var result = f.Route("c0", RelayPacket(), Deliver);
            if (result.Status != "Partial" || result.Error != "DeliveryFailed" || result.Deliveries.Count != 2)
                throw new Exception("Slow receiver must retain per-connection outcomes.");
        }
        Assert(submitted == 1000 && rejected == 1000, "Saturated receiver cannot prevent 1000 healthy recipient handoffs.");
        slow = false; f.Time = f.Time.AddSeconds(1);
        Assert(f.Route("c0", RelayPacket(), Deliver).Error == null && submitted == 1002, "Drained recipient resumes without reconnect or replay.");
        Console.WriteLine("RELAY P02 PASS: 1000 partial submissions; healthy peer progresses; drained peer recovers.");
    }

    private static void AssertRelayBroadcastAmplification()
    {
        var f = new RelayLoadFixture(6, new RelayLimits { GlobalBurstFanoutBytes = 1000000, GlobalFanoutBytesPerSecond = 100000 });
        var packet = RelayPacket(); packet.PayloadJson = new string('x', 48000);
        int calls = 0, accepted = 0, denied = 0;
        PacketDispatchResult Deliver(FrameworkPacket p, string[] targets) { calls++; return RelayLoadFixture.Submitted(p, targets); }
        for (int i = 0; i < 6; i++)
        {
            var result = f.Route("c" + i, packet, Deliver);
            if (result.Error == "RateLimited") denied++;
            else { accepted++; Assert(result.Deliveries.Count == 5, "Broadcast delivers full set; never silently truncates."); }
        }
        Assert(accepted == 4 && denied == 2 && calls == 4, "Shared global budget rejects multi-sender fanout before handoff.");
        f.Time = f.Time.AddSeconds(10);
        Assert(f.Route("c5", packet, Deliver).Error == null, "Global fanout budget recovers after refill.");
        var over = new RelayLoadFixture(130); int excessCalls = 0;
        Assert(over.Route("c0", RelayPacket(), (p, targets) => { excessCalls++; return RelayLoadFixture.Submitted(p, targets); }).Error == "RateLimited"
            && excessCalls == 0, "129 recipients exceed the 128 limit without partial delivery.");
        Console.WriteLine("RELAY P03 PASS: global multi-sender byte budget/refill; 129-recipient broadcast refused atomically.");
    }

    private static void AssertRelayReconnectStress()
    {
        var f = new RelayLoadFixture(2);
        for (int cycle = 0; cycle < 100; cycle++)
        {
            string oldEpoch = f.Epochs["c1"];
            f.Time = f.Time.AddSeconds(1); f.Declare("c1", 2, false);
            Assert(f.Route("c0", RelayPacket("c1"), RelayLoadFixture.Submitted).Error == "NoCapability", "Revoked connection receives nothing.");
            f.Router.AdmitControl("c1"); f.Router.Admit("c1");
            f.Router.Remove("c1"); f.Sessions.Remove("c1"); f.Live.Remove("c1");
            Assert(f.Route("c0", RelayPacket("c1"), RelayLoadFixture.Submitted).Error == "TargetOffline", "Disconnected connection receives nothing.");
            f.Bind("c1");
            Assert(f.Router.Apply("c1", new CommandCapabilitySnapshot { Epoch = oldEpoch, Revision = 999 }).Error == "InvalidCapability",
                "Stale epoch cannot resurrect capability after reconnect.");
            Assert(f.Route("c0", RelayPacket("c1"), RelayLoadFixture.Submitted).Deliveries.Count == 1, "Reconnected recipient gets exactly one handoff.");
        }
        foreach (string id in f.Live.ToArray()) { f.Router.Remove(id); f.Sessions.Remove(id); }
        foreach (string field in new[] { "entries", "index", "controlBudgets", "commandBudgets" })
            Assert(f.Count(field) == 0, "100-cycle teardown leaves no retained state in " + field);
        Console.WriteLine("RELAY P04 PASS: 100 revoke/disconnect/reconnect cycles; all four state tables empty after teardown.");
    }

    private static void AssertRelayPendingFlood()
    {
        DateTime time = DateTime.UtcNow;
        int sends = 0;
        var capabilities = new ClientCommandCapabilities();
        using var client = new ClientCommandRelay(capabilities, _ => { sends++; return true; }, () => time);
        client.Begin("flood");
        client.Acknowledge(new CommandCapabilityAck { Epoch = "flood", Revision = capabilities.Revision });
        var pending = Enumerable.Range(0, 64).Select(_ => client.SendRelay("author.research", "request", "{}")).ToArray();
        Assert(pending.All(p => !p.IsCompleted), "Exactly 64 pending business requests retained.");
        int before = sends;
        var denied = client.SendRelay("author.research", "request", "{}");
        Assert(denied.IsCompleted && sends == before, "Pending request overflow is refused before transport send.");
        time = time.AddSeconds(16); client.Pulse();
        Assert(pending.All(p => p.Result.Error == "Unknown"), "Flooded pending requests all timeout as Unknown.");
        Assert(sends <= before + 1, "Timer may retry snapshot, never the 64 business commands.");
        var next = client.SendRelay("author.research", "request", "{}");
        Assert(!next.IsCompleted, "Timeout cleanup frees pending capacity.");
        client.Disconnect(); Assert(next.Result.Error == "Unknown", "Disconnect completes last unknown without replay.");
        Console.WriteLine("RELAY P05 PASS: pending ceiling=64; overflow refusal; timeout drain/recovery; no business replay.");
    }

    private static void AssertRelayTlsQueueBounds()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        using var server = new TlsNetServer(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0), cert);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var sessionType = typeof(TlsNetServer).GetNestedType("Session", BindingFlags.NonPublic);
        // Inject only the session/channel boundary; use the production admission code
        // and real bounded Channel. No send loop drains it while the recipient is slow.
        object session = Activator.CreateInstance(sessionType, flags, null, new object[] { "slow", null }, null);
        var sessions = (IDictionary)typeof(TlsNetServer).GetField("sessions", flags).GetValue(server);
        var outgoing = (System.Threading.Channels.Channel<byte[]>)sessionType.GetField("Outgoing", flags).GetValue(session);
        var bytes = sessionType.GetField("Bytes", flags);
        sessions.Add("slow", session);
        try
        {
            for (int i = 0; i < 16; i++) Assert(server.TrySendRelay("slow", "relay", new byte[1024]) == TransportSendOutcome.Submitted,
                "TLS bounded queue accepts exactly its configured 16 frames.");
            int retained = (int)bytes.GetValue(session);
            Assert(server.TrySendRelay("slow", "relay", new byte[1024]) == TransportSendOutcome.NotSent &&
                (int)bytes.GetValue(session) == retained, "Full TLS queue refuses before changing byte accounting.");
            int drained = 0;
            while (outgoing.Reader.TryRead(out var body)) { drained++; bytes.SetValue(session, (int)bytes.GetValue(session) - body.Length); }
            Assert(drained == 16 && (int)bytes.GetValue(session) == 0, "Draining exact queued bytes returns TLS accounting to zero.");
            Assert(server.TrySendRelay("slow", "relay", new byte[1024]) == TransportSendOutcome.Submitted, "Drained TLS queue resumes.");
            bytes.SetValue(session, 4 * 1024 * 1024);
            Assert(server.TrySendRelay("slow", "relay", new byte[1024]) == TransportSendOutcome.NotSent, "TLS byte ceiling independently refuses admission.");
            Assert(server.TrySendRelay("slow", "relay", new byte[65537]) == TransportSendOutcome.NotSent, "TLS Relay rejects over-64KiB packet.");
        }
        finally { sessions.Clear(); } // Synthetic session has no TLS channel to dispose.
        Console.WriteLine("RELAY TLS QUEUE PASS: actual 16-frame channel, byte ceiling, oversize refusal and drain/recovery.");
    }

    private static void AssertRelayUdpQueueBounds()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var server = new NetServer(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0), 8);
        var clients = Enumerable.Range(0, 5).Select(_ => new LiteNetLib.NetManager(new LiteNetLib.EventBasedNetListener(), "Phinix")).ToArray();
        server.Start();
        try
        {
            foreach (var client in clients) { Assert(client.Start(), "UDP queue fixture binds."); client.Connect("127.0.0.1", server.LocalPort); }
            var backend = (LiteNetLib.NetManager)typeof(NetServer).GetField("server", flags).GetValue(server);
            object sync = typeof(NetServer).GetField("peerSynchronization", flags).GetValue(server);
            object gate = sync.GetType().GetProperty("PeerGate", flags).GetValue(sync);
            var peers = (Dictionary<string, LiteNetLib.NetPeer>)typeof(NetServer).GetField("connectedPeers", flags).GetValue(server);
            object peersGate = typeof(NetServer).GetField("connectedPeersLock", flags).GetValue(server);
            Assert(System.Threading.SpinWait.SpinUntil(() =>
            {
                foreach (var client in clients) client.PollEvents();
                lock (gate) lock (peersGate) return peers.Count == 5 && backend.GetPeers().Count() == 5 &&
                    backend.GetPeers().All(p => p.ConnectionState == LiteNetLib.ConnectionState.Connected);
            }, 5000), "Five real UDP peers connect for global queue test.");
            string available;
            lock (gate)
            {
                var entries = peers.ToArray();
                for (int i = 0; i < 4; i++)
                {
                    for (int n = 0; n < 2048; n++) entries[i].Value.Send(new byte[] { 1 }, LiteNetLib.SendOptions.ReliableOrdered);
                    Assert(entries[i].Value.PacketsCountInReliableOrderedQueue == 2048, "Pinned UDP backend retains exactly 2048 queued packets while worker is paused.");
                    Assert(server.TrySendRelay(entries[i].Key, "relay", new byte[] { 2 }) == TransportSendOutcome.NotSent,
                        "Per-peer UDP ceiling refuses Relay before enqueue.");
                }
                available = entries[4].Key;
                Assert(entries[4].Value.PacketsCountInReliableOrderedQueue == 0 &&
                    server.TrySendRelay(available, "relay", new byte[] { 2 }) == TransportSendOutcome.NotSent,
                    "Global 8192 queued packets refuse an otherwise empty recipient queue.");
            }
            Assert(System.Threading.SpinWait.SpinUntil(() =>
            {
                foreach (var client in clients) client.PollEvents();
                lock (gate) return backend.GetPeers().Sum(p => p.PacketsCountInReliableOrderedQueue) < 8192;
            }, 10000), "Actual UDP ACK processing drains global backlog after worker resumes.");
            Assert(server.TrySendRelay(available, "relay", new byte[] { 2 }) == TransportSendOutcome.Submitted, "Drained UDP global queue accepts Relay again.");
            Console.WriteLine("RELAY UDP QUEUE PASS: actual backend, 2048 per peer/8192 global packets, rejection, ACK drain/recovery.");
        }
        finally { foreach (var client in clients) client.Stop(); }
    }
}
