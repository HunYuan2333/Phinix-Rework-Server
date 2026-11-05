using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Net;
using System.Text;
using System.Text.Json;
using Authentication;
using Connections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Phinix.TradeExtension;
using Phinix.TradeExtension.Server;
using UserManagement;
using Utils.Framework;

// L2: real service/store/serialization. Only the user directory and outgoing network are isolated.
// No game inventory, durable commit, actual socket, or delivery claim is made by these tests.
internal static class Program
{
    private static int failed;
    private static int cases;

    private static int Main()
    {
        Run("AS01-missing-ref", () => RejectBatch(new[] { "valid", "missing" }));
        Run("AS01-all-missing", () => RejectBatch(new[] { "missing" }));
        Run("AS01-duplicate-ref", () => RejectBatch(new[] { "valid", "valid" }));
        Run("AS01-blank-ref", () => RejectBatch(new[] { "valid", "" }));
        Run("AS01-invalid-trade-preserves-ref", () => InvalidTarget("a", "not-a-trade"));
        Run("AS01-nonparticipant-preserves-ref", () => InvalidTarget("outsider", null));
        Run("AS01-valid-complete-payload", ValidBatch);
        Run("AS01-empty-offer", EmptyOffer);
        Run("AS01-scoped-ref", ScopedReference);
        Run("AS01-cache-conflict", CacheConflict);
        Run("AS01-cache-capacity-preserves-ref", CacheCapacity);
        Run("SV11-item-byte-budget", ItemByteBudget);
        Run("SV11-session-byte-budget", SessionByteBudget);
        Run("SV11-global-byte-budget", GlobalByteBudget);
        Run("SV11-cache-key-budget", CacheKeyBudget);
        Run("AS01-session-isolation", SessionIsolation);
        Run("AS01-invalid-inline", InvalidInline);
        Run("P5-historical-inline-complete-batch", () =>
        {
            var f = new Fixture(); var items = new List<FrameworkItemPayload> { Item(1), Item(2) };
            var response = f.Offer(Array.Empty<string>(), inline: items);
            Check(response.Success, "Historical inline offer must remain supported");
            Check(FrameworkSerialization.SerializePayload(f.Snapshot().Participants.Single(p => p.Uuid == "a").ItemsOnOffer) == FrameworkSerialization.SerializePayload(items), "Inline translation changed item order, codec bytes or metadata");
        });
        Run("P5-historical-inline-invalid-tail-preserves-batch", () =>
        {
            var f = new Fixture(); f.Cache("valid", Item(9), "a");
            var before = FrameworkSerialization.SerializePayload(f.Snapshot());
            Check(!f.Offer(Array.Empty<string>(), inline: new List<FrameworkItemPayload> { Item(1), null }).Success, "Malformed inline tail must reject whole batch");
            Check(before == FrameworkSerialization.SerializePayload(f.Snapshot()), "Malformed historical inline batch changed quote");
            Check(f.Offer(new[] { "valid" }).Success, "Historical inline refusal consumed unrelated staged reference");
        });

        Run("AS01-mixed-inline-refs-refused", () =>
        {
            var f = new Fixture(); f.Cache("valid", Item(1), "a");
            Check(!f.Offer(new[] { "valid" }, inline: new List<FrameworkItemPayload> { Item(2) }).Success, "Mixed inputs silently filtered assets");
            Check(f.Offer(new[] { "valid" }).Success, "Mixed refusal consumed reference");
        });
        Run("AS01-plugin-scoped-item", PluginScopedItem);
        Run("FR14-user-delta-secret", UserDeltaSecret);
        Run("FR14-user-sync-secret", UserSyncSecret);
        Run("FR14-auth-log-secret", AuthenticationLogSecret);
        Run("trade-diagnostics-default-off", () => TradeDiagnostics(false));
        Run("trade-diagnostics-opt-in", () => TradeDiagnostics(true));
        Run("trade-diagnostics-config-compatibility", DiagnosticConfigCompatibility);
        foreach (var assembly in new[] { typeof(PhinixFrameworkTradeServerService).Assembly, typeof(FrameworkTradeProtocol).Assembly,
                     typeof(ServerUserManager).Assembly, typeof(ServerAuthenticator).Assembly, typeof(FrameworkPacket).Assembly, typeof(NetServer).Assembly })
            Console.WriteLine(JsonSerializer.Serialize(new { assembly = assembly.FullName, references = assembly.GetReferencedAssemblies().Select(a => a.FullName).ToArray() }));
        Console.WriteLine($"RESULT cases={cases} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string id, Action test)
    {
        cases++;
        try { test(); Console.WriteLine(JsonSerializer.Serialize(new { id, layer = "L2", passed = true })); }
        catch (Exception error)
        {
            failed++;
            Console.WriteLine(JsonSerializer.Serialize(new { id, layer = "L2", passed = false, reason = error.Message }));
        }
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static FrameworkItemPayload Item(byte value) => new FrameworkItemPayload
    {
        CodecId = "test.opaque-v1", PayloadJson = "{\"synthetic\":true}",
        PayloadBytes = new[] { value, (byte)0, (byte)255 },
        Metadata = new List<FrameworkMetadataEntry> { new FrameworkMetadataEntry { Key = "lineage", Value = "synthetic-" + value } }
    };

    private static void RejectBatch(string[] refs)
    {
        var f = new Fixture();
        f.Cache("valid", Item(1), "a");
        var before = FrameworkSerialization.SerializePayload(f.Snapshot());
        var result = f.Offer(refs);
        Check(!result.Success, "Invalid reference batch must be rejected");
        Check(f.LastCorrelation == "synthetic-operation", "Rejection must correlate to original operation");
        Check(before == FrameworkSerialization.SerializePayload(f.Snapshot()), "Rejected batch must preserve whole trade snapshot");
        // Retry only the legitimate reference to independently prove it wasn't consumed on rejection.
        Check(f.Offer(new[] { "valid" }).Success, "Rejected batch consumed a legitimate reference");
    }

    private static void InvalidTarget(string sender, string trade)
    {
        var f = new Fixture();
        f.Cache("valid", Item(2), sender);
        var before = FrameworkSerialization.SerializePayload(f.Snapshot());
        Check(!f.Offer(new[] { "valid" }, sender, trade).Success, "Unauthorized/unknown trade must reject");
        Check(before == FrameworkSerialization.SerializePayload(f.Snapshot()), "Invalid target changed trade");
        // The same owner can subsequently use the reference in a trade where they participate.
        if (sender != "a") f.Create(sender, "b");
        var retry = f.Offer(new[] { "valid" }, sender);
        Check(retry.Success && retry.Items.Count == 1, "Invalid target consumed a legitimate reference");
    }

    private static void ValidBatch()
    {
        var f = new Fixture();
        f.Cache("one", Item(17), "a");
        f.Cache("two", Item(31), "a");
        var response = f.Offer(new[] { "one", "two" });
        Check(response.Success, "Complete valid batch must succeed");
        var items = f.Snapshot().Participants.Single(p => p.Uuid == "a").ItemsOnOffer;
        Check(items.Count == 2, "Valid batch must preserve both payloads");
        Check(FrameworkSerialization.SerializePayload(items) == FrameworkSerialization.SerializePayload(new List<FrameworkItemPayload> { Item(17), Item(31) }), "Opaque payload/metadata/order changed");
        var before = FrameworkSerialization.SerializePayload(f.Snapshot());
        Check(!f.Offer(new[] { "one" }).Success, "Consumed reference must not be reusable");
        Check(before == FrameworkSerialization.SerializePayload(f.Snapshot()), "Reused reference changed trade");
    }

    private static void EmptyOffer()
    {
        var f = new Fixture();
        f.Cache("one", Item(1), "a");
        Check(f.Offer(new[] { "one" }).Success, "Initial offer must succeed");
        Check(f.Offer(Array.Empty<string>()).Success, "An explicit empty offer is valid");
        Check(f.Snapshot().Participants.Single(p => p.Uuid == "a").ItemsOnOffer.Count == 0, "Empty offer must clear previous offer");
    }

    private static void ScopedReference()
    {
        var f = new Fixture();
        f.Cache("shared", Item(1), "a");
        f.Cache("shared", Item(2), "b");
        Check(!f.Offer(new[] { "shared" }, "outsider").Success, "Another owner cannot use a cached reference");
        Check(f.Offer(new[] { "shared" }, "a").Success, "Owner A reference must survive other owner insertion");
        var a = f.Snapshot().Participants.Single(p => p.Uuid == "a").ItemsOnOffer;
        Check(a.Count == 1 && a[0].PayloadBytes.SequenceEqual(Item(1).PayloadBytes), "Cross-owner ID collision replaced owner's payload");
        Check(f.Offer(new[] { "shared" }, "b").Success, "Owner B must have an independent reference");
    }

    private static void CacheConflict()
    {
        var f = new Fixture();
        var original = Item(1);
        f.Cache("one", original, "a");
        original.PayloadBytes[0] = 99;
        Check(!f.Service.TryCacheItemPacket("one", Item(2), f.Context("a")), "Conflicting packet ID must reject replacement");
        Check(f.Service.TryCacheItemPacket("one", Item(1), f.Context("a")), "Identical packet replay must be harmless");
        Check(f.Offer(new[] { "one" }).Items.Single().PayloadBytes[0] == 1, "Caller mutation or conflict changed cached payload");
    }

    private static void CacheCapacity()
    {
        var f = new Fixture();
        for (int i = 0; i < 200; i++) f.Cache("item-" + i, Item(1), "a");
        Check(!f.Service.TryCacheItemPacket("overflow", Item(1), f.Context("a")), "Cache must refuse capacity overflow");
        Check(f.Offer(new[] { "item-0" }).Items.Count == 1, "Capacity refusal evicted an existing reference");
    }

    // Independent acceptance limits: encoded UTF-8 item <=1 MiB, scope <=4 MiB, global <=16 MiB.
    // These are input ceilings, not production performance or managed-heap measurements.
    private static FrameworkItemPayload LargeItem(int length) => new FrameworkItemPayload
    {
        CodecId = "test.synthetic", PayloadBytes = new byte[length]
    };

    private static void ItemByteBudget()
    {
        var f = new Fixture();
        f.Cache("valid", Item(1), "a");
        var before = FrameworkSerialization.SerializePayload(f.Snapshot());
        var invalid = new[] {
            LargeItem(800000), // binary JSON expansion exceeds encoded budget
            new FrameworkItemPayload { CodecId = "test.synthetic", PayloadJson = new string('x', 1024 * 1024 + 1) },
            new FrameworkItemPayload { CodecId = "test.synthetic", Metadata = new List<FrameworkMetadataEntry> {
                new FrameworkMetadataEntry { Key = "large", Value = new string('x', 1024 * 1024 + 1) } } },
            new FrameworkItemPayload { CodecId = "test.synthetic", PayloadJson = new string((char)1, 200000) },
            new FrameworkItemPayload { CodecId = "test.synthetic", Metadata = Enumerable.Range(0, 1025)
                .Select(i => new FrameworkMetadataEntry { Key = "k", Value = "v" }).ToList() }
        };
        foreach (var item in invalid)
            Check(!f.Service.TryCacheItemPacket("large", item, f.Context("a")), "Oversized bytes/JSON/metadata/escaping accepted");
        Check(before == FrameworkSerialization.SerializePayload(f.Snapshot()), "Oversized input changed offer");
        Check(f.Offer(new[] { "valid" }).Success, "Oversized input consumed valid reference");
    }

    private static void SessionByteBudget()
    {
        var f = new Fixture();
        var item = LargeItem(400000);
        int encoded = Encoding.UTF8.GetByteCount(FrameworkSerialization.SerializePayload(item));
        int capacity = 4 * 1024 * 1024 / encoded;
        Check(capacity > 1 && capacity < 200, "Nontrivial independent scope boundary");
        for (int i = 0; i < capacity; i++) f.Cache("large-" + i, item, "a");
        Check(!f.Service.TryCacheItemPacket("overflow", item, f.Context("a")), "Session byte overflow accepted");
        Check(f.Service.TryCacheItemPacket("large-0", item, f.Context("a")), "Identical replay charged budget twice");
        Check(!f.Offer(new[] { "large-0", "missing" }).Success, "Incomplete batch accepted");
        Check(!f.Service.TryCacheItemPacket("overflow", item, f.Context("a")), "Rejected batch incorrectly released budget");
        var otherSession = f.Context("a"); otherSession.SessionId = "another-session";
        Check(f.Service.TryCacheItemPacket("independent", item, otherSession), "Other session inherited full budget");
        Check(f.Offer(new[] { "large-0" }).Success, "Budget exhaustion evicted a valid reference");
        Check(f.Service.TryCacheItemPacket("overflow", item, f.Context("a")), "Consumption did not release scope bytes");
    }

    private static void GlobalByteBudget()
    {
        var f = new Fixture();
        var item = LargeItem(400000);
        int encoded = Encoding.UTF8.GetByteCount(FrameworkSerialization.SerializePayload(item));
        int scopeCapacity = 4 * 1024 * 1024 / encoded;
        int globalCapacity = 16 * 1024 * 1024 / encoded;
        for (int i = 0; i < globalCapacity; i++)
            f.Cache("global-" + i, item, i < scopeCapacity ? "a" : "scope-" + (i / scopeCapacity));
        Check(!f.Service.TryCacheItemPacket("overflow", item, f.Context("fresh-scope")), "Global byte overflow accepted");
        Check(f.Offer(new[] { "global-0" }).Success, "Global refusal evicted original reference");
        Check(f.Service.TryCacheItemPacket("overflow", item, f.Context("fresh-scope")), "Consumption did not release global bytes");
    }

    private static void CacheKeyBudget()
    {
        var f = new Fixture();
        Check(!f.Service.TryCacheItemPacket(new string('x', 257), Item(1), f.Context("a")), "Unbounded packet key accepted");
        var context = f.Context("a"); context.SessionId = new string('x', 257);
        Check(!f.Service.TryCacheItemPacket("valid", Item(1), context), "Unbounded scope key accepted");
        f.Cache(new string('x', 256), Item(1), "a");
        Check(f.Offer(new[] { new string('x', 256) }).Success, "Boundary-sized key rejected");
    }

    private static void SessionIsolation()
    {
        var f = new Fixture();
        var oldSession = f.Context("a");
        oldSession.SessionId = "previous-session";
        Check(f.Service.TryCacheItemPacket("one", Item(1), oldSession), "Old-session setup failed");
        Check(!f.Offer(new[] { "one" }).Success, "New session must not consume old-session reference");
        f.Cache("one", Item(2), "a");
        Check(f.Offer(new[] { "one" }).Items.Single().PayloadBytes[0] == 2, "Sessions collided on reference ID");
    }

    private static void InvalidInline()
    {
        var f = new Fixture();
        f.Cache("one", Item(1), "a");
        var before = FrameworkSerialization.SerializePayload(f.Snapshot());
        Check(!f.Offer(new[] { "one" }, inline: new List<FrameworkItemPayload> { Item(2) }).Success, "Ambiguous inline and reference batch must reject");
        Check(!f.Offer(Array.Empty<string>(), inline: new List<FrameworkItemPayload> { null }).Success, "Null item must reject without mutation");
        Check(before == FrameworkSerialization.SerializePayload(f.Snapshot()), "Invalid inline batch mutated offer");
        Check(f.Offer(new[] { "one" }).Items.Count == 1, "Invalid inline batch consumed legitimate reference");
    }

    private static void PluginScopedItem()
    {
        var f = new Fixture();
        var plugin = new BuiltInTradeServerExtension();
        typeof(BuiltInTradeServerExtension).GetField("tradeApi", Private).SetValue(plugin, f.Service);
        var packet = new FrameworkPacket { MessageId = "plugin-item", PayloadJson = FrameworkSerialization.SerializePayload(Item(1)) };
        var result = plugin.HandleIncomingItem(packet, f.Context("a"), Array.Empty<IItemCodec>());
        Check(result.Action == ItemHandlingResultAction.Handled, "Plugin must accept scoped item");
        packet.PayloadJson = FrameworkSerialization.SerializePayload(Item(2));
        result = plugin.HandleIncomingItem(packet, f.Context("a"), Array.Empty<IItemCodec>());
        Check(result.Action == ItemHandlingResultAction.StopPropagation, "Plugin must explicitly stop conflicting cache input");
        Check(f.Offer(new[] { "plugin-item" }).Items.Single().PayloadBytes[0] == 1, "Plugin cache boundary replaced original payload");
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void UserDeltaSecret() => UserSecrets(false);
    private static void UserSyncSecret() => UserSecrets(true);

    private static void UserSecrets(bool sync)
    {
        using var net = new NetServer(new IPEndPoint(IPAddress.Loopback, 0), 2);
        var packets = new List<byte[]>();
        var users = new ServerUserManager(net, null, 100, (_, __, bytes) => { packets.Add(bytes); return true; });
        const string secret = "synthetic-credential-never-emit";
        string uuid = users.CreateUser(secret, "synthetic-user");
        var connections = (Dictionary<string, string>)typeof(ServerUserManager).GetField("connectedUsers", Private).GetValue(users);
        connections.Add("synthetic-connection", uuid);
        if (sync)
            typeof(ServerUserManager).GetMethod("sendSyncPacket", Private).Invoke(users, new object[] { "synthetic-connection" });
        else
        {
            Check(users.TryLogIn(uuid), "Synthetic user login failed");
            Check(users.UpdateUser(uuid, "changed", true), "Synthetic user update failed");
            Check(users.TryLogOut(uuid), "Synthetic user logout failed");
        }
        Check(packets.Count == (sync ? 1 : 3), "Expected every user update path to send a packet");
        foreach (byte[] bytes in packets)
        {
            Check(!Encoding.UTF8.GetString(bytes).Contains(secret), "User wire packet leaked synthetic credential");
            var envelope = Any.Parser.ParseFrom(bytes);
            var publicUsers = sync ? envelope.Unpack<UserSyncPacket>().Users.ToArray() : new[] { envelope.Unpack<UserUpdatePacket>().User };
            Check(publicUsers.All(u => u.Username == "" && u.Uuid == uuid), "Public projection must preserve UUID and omit credential");
        }
        Check(users.TryGetUserUuid(secret, out string mapped) && mapped == uuid, "Public projection changed persistent identity mapping");
    }

    private static void AuthenticationLogSecret()
    {
        using var net = new NetServer(new IPEndPoint(IPAddress.Loopback, 0), 2);
        var auth = new ServerAuthenticator(net, "synthetic", "synthetic", AuthTypes.ClientKey);
        try
        {
            const string secret = "synthetic-bearer-never-emit";
            const string sessionId = "synthetic-session-never-emit";
            var messages = new List<string>();
            auth.OnLogEntry += (_, e) => messages.Add(e.Message);
            var sessions = (Dictionary<string, Session>)typeof(ServerAuthenticator).GetField("sessions", Private).GetValue(auth);
            sessions.Add("synthetic-connection", new Session { ConnectionId = "synthetic-connection", SessionId = sessionId, Expiry = DateTime.UtcNow.AddMinutes(1) });
            typeof(ServerAuthenticator).GetMethod("authenticatePacketHandler", Private).Invoke(auth, new object[]
            {
                "synthetic-connection", new AuthenticatePacket { SessionId = sessionId, AuthType = AuthTypes.ClientKey, Username = secret }
            });
            Check(messages.Count > 0, "Auth log scenario did not execute");
            Check(messages.All(m => !m.Contains(secret) && !m.Contains(sessionId)), "Auth logs leaked credential or session secret");
        }
        finally { auth.Stop(); }
    }

    private static void DiagnosticConfigCompatibility()
    {
        var serializer = new System.Runtime.Serialization.DataContractSerializer(typeof(TradeServerConfig));
        TradeServerConfig Read(string xml)
        {
            using var input = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(xml));
            return (TradeServerConfig)serializer.ReadObject(input);
        }
        using var stream = new System.IO.MemoryStream();
        serializer.WriteObject(stream, new TradeServerConfig { DiagnosticLogging = true });
        string xml = Encoding.UTF8.GetString(stream.ToArray());
        Check(Read(xml).DiagnosticLogging, "Explicit diagnostic switch lost during actual DataContract roundtrip");
        var old = Read(xml.Replace("<DiagnosticLogging>true</DiagnosticLogging>", ""));
        Check(!old.DiagnosticLogging && old.RecoveryPageSize == 8, "Old configuration must default tracing off without changing limits");
    }

    private static void TradeDiagnostics(bool enabled)
    {
        var config = new TradeServerConfig();
        Check(!config.DiagnosticLogging, "Verbose transaction tracing must default off");
        config.DiagnosticLogging = enabled;
        var f = new Fixture(config);
        var messages = new List<Utils.LogEventArgs>();
        f.Service.OnLogEntry += (_, entry) => messages.Add(entry);
        // A real valid status mutation, followed by an invalid one: success trace is optional,
        // refusal evidence and actual command responses must survive either setting.
        var request = new FrameworkPacket { PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeStatusUpdateRequest { TradeId = f.Snapshot().TradeId, Cancelled = true }) };
        f.Service.HandleStatusUpdateRequest(request, f.Context("a"));
        Check(messages.Any(m => m.Message.Contains("SUCCESS")) == enabled, "Trace switch does not control success diagnostics");
        int before = messages.Count;
        f.Service.HandleStatusUpdateRequest(new FrameworkPacket { PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeStatusUpdateRequest { TradeId = "missing", Cancelled = true }) }, f.Context("a"));
        Check(messages.Skip(before).Any(m => m.LogLevel == Utils.LogLevel.WARNING), "Disabling trace must not suppress rejection diagnostics");
    }

    private sealed class Fixture
    {
        public readonly PhinixFrameworkTradeServerService Service;
        private readonly List<FrameworkPacket> sent = new List<FrameworkPacket>();
        private string tradeId;
        public string LastCorrelation { get; private set; }

        public Fixture(TradeServerConfig config = null)
        { Service = new PhinixFrameworkTradeServerService(new Directory(), null, null, config ?? new TradeServerConfig()); Create("a", "b"); }
        public ServerFrameworkContext Context(string sender) => new ServerFrameworkContext
        {
            ConnectionId = "connection-" + sender, SessionId = "session-" + sender, SenderUuid = sender,
            SendMessage = (_, packet) => sent.Add(packet)
        };

        public void Create(string a, string b)
        {
            sent.Clear();
            Service.HandleCreateRequest(new FrameworkPacket { PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeCreateRequest { OtherPartyUuid = b }) }, Context(a));
            var response = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(sent.First(p => p.MessageType == FrameworkTradeProtocol.CreateResponseType).PayloadJson);
            Check(response.Success, "Synthetic trade setup must succeed");
            tradeId = response.TradeId;
        }

        public void Cache(string id, FrameworkItemPayload payload, string owner)
        {
            // Before the fix, use the historical public entry point so the negative control
            // exercises the actual defective cache. Afterward, use the additive scoped API.
            var scoped = typeof(PhinixFrameworkTradeServerService).GetMethod("TryCacheItemPacket");
            if (scoped == null) Service.CacheItemPacket(id, payload);
            else Check((bool)scoped.Invoke(Service, new object[] { id, payload, Context(owner) }), "Synthetic item must cache successfully");
        }

        public FrameworkTradeOfferUpdateResponse Offer(string[] refs, string sender = "a", string target = null, List<FrameworkItemPayload> inline = null)
        {
            sent.Clear();
            var packet = new FrameworkPacket { PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeOfferUpdateRequest { TradeId = target ?? tradeId, ItemPacketRefs = refs.ToList(), Items = inline ?? new List<FrameworkItemPayload>() }) };
            packet.SetCorrelationId("synthetic-operation");
            Service.HandleOfferUpdateRequest(packet, Context(sender));
            var response = sent.Single(p => p.MessageType == FrameworkTradeProtocol.OfferUpdateResponseType);
            LastCorrelation = response.GetCorrelationId();
            return FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateResponse>(response.PayloadJson);
        }

        public FrameworkTradeStateSnapshot Snapshot()
        {
            sent.Clear();
            Service.HandleSnapshotRequest(Context("a"));
            return FrameworkSerialization.DeserializePayload<FrameworkTradeStateCollectionSnapshot>(sent.Single().PayloadJson).Trades.Single(t => t.TradeId == tradeId);
        }
    }

    private sealed class Directory : IServerUserManager
    {
        public event EventHandler<ServerLoginEventArgs> OnLogin { add { } remove { } }
        public bool IsLoggedIn(string connectionId, string uuid) => true;
        public string[] GetConnections() => new[] { "connection-a", "connection-b" };
        public bool TryGetConnection(string uuid, out string connectionId) { connectionId = "connection-" + uuid; return true; }
        public bool TryGetDisplayName(string uuid, out string displayName) { displayName = uuid; return true; }
        public bool TryGetLoggedIn(string uuid, out bool loggedIn) { loggedIn = true; return true; }
        public bool TryGetAcceptingTrades(string uuid, out bool acceptingTrades) { acceptingTrades = true; return true; }
    }
}
