using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Data.Sqlite;
using Phinix.LegacyAdapter.Server;
using Phinix.TradeExtension;
using Phinix.TradeExtension.Server;
using ServerRuntime;
using ServerStorage;
using UserManagement;
using Utils.Framework;
using Item = Phinix.Framework.FrameworkVanillaItemData;

internal static partial class Program
{
    private static int checks;
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
    private static void Refused(Action action)
    { try { action(); } catch (Exception e) when (e is InvalidDataException || e is InvalidProtocolBufferException) { checks++; return; } throw new Exception("Expected explicit refusal"); }
    // Independent old .proto field writer, including original enum values and duplicate stacks.
    private static byte[] OldItem(int quality = 7, int count = 7, byte[] inner = null)
    {
        using var stream = new MemoryStream();
        using (var w = new CodedOutputStream(stream, true))
        {
            w.WriteTag(1, WireFormat.WireType.LengthDelimited); w.WriteString("Steel");
            if (count != 0) { w.WriteTag(2, WireFormat.WireType.Varint); w.WriteInt32(count); }
            w.WriteTag(3, WireFormat.WireType.LengthDelimited); w.WriteString("WoodLog");
            if (quality != 0) { w.WriteTag(4, WireFormat.WireType.Varint); w.WriteInt32(quality); }
            w.WriteTag(5, WireFormat.WireType.Varint); w.WriteInt32(60);
            if (inner != null) { w.WriteTag(6, WireFormat.WireType.LengthDelimited); w.WriteBytes(ByteString.CopyFrom(inner)); }
            w.Flush();
        }
        return stream.ToArray();
    }
    private static byte[] Request(string trade, string token, IEnumerable<byte[]> items, string user = "a", string session = "session-a")
    {
        using var stream = new MemoryStream();
        using (var w = new CodedOutputStream(stream, true))
        {
            foreach (var pair in new[] { (1, session), (2, user), (3, trade) }) { w.WriteTag(pair.Item1, WireFormat.WireType.LengthDelimited); w.WriteString(pair.Item2); }
            foreach (var bytes in items) { w.WriteTag(4, WireFormat.WireType.LengthDelimited); w.WriteBytes(ByteString.CopyFrom(bytes)); }
            if (!string.IsNullOrEmpty(token)) { w.WriteTag(6, WireFormat.WireType.LengthDelimited); w.WriteString(token); }
            w.Flush();
        }
        return new Any { TypeUrl = "Phinix/Trading.UpdateTradeItemsPacket", Value = ByteString.CopyFrom(stream.ToArray()) }.ToByteArray();
    }
    private sealed record Result(string Trade, string Token, bool Success, int Failure, string Message, List<byte[]> Items);
    private static Result ReadResponse(byte[] bytes)
    {
        var any = Any.Parser.ParseFrom(bytes); Check(any.TypeUrl == "Phinix/Trading.UpdateTradeItemsResponsePacket", "Wrong response type");
        using var r = new CodedInputStream(any.Value.ToByteArray()); string trade = "", token = "", message = ""; bool success = false; int failure = 0; var items = new List<byte[]>(); uint tag;
        while ((tag = r.ReadTag()) != 0)
            switch (tag) { case 10: trade = r.ReadString(); break; case 18: token = r.ReadString(); break; case 24: success = r.ReadBool(); break; case 32: failure = r.ReadInt32(); break; case 42: message = r.ReadString(); break; case 50: items.Add(r.ReadBytes().ToByteArray()); break; default: throw new Exception("Unknown output field"); }
        return new Result(trade, token, success, failure, message, items);
    }
    private static void Main()
    {
        for (int q = 0; q <= 7; q++)
        {
            var bytes = OldItem(q); var mapped = OriginalTradeItemCodec.ToUnified(bytes);
            Check((int)Item.Parser.ParseFrom(mapped.PayloadBytes).Quality == q + 1, "Original/Framework enum offset lost quality");
            Check(OriginalTradeItemCodec.ToOriginal(mapped).SequenceEqual(bytes), "Original scalar roundtrip not exact");
        }
        var nested = OldItem(3, 1, OldItem(2, 4));
        Check(OriginalTradeItemCodec.ToOriginal(OriginalTradeItemCodec.ToUnified(nested)).SequenceEqual(nested), "Nested item or stuff lost");
        Refused(() => OriginalTradeItemCodec.ToUnified(OldItem(8)));
        Refused(() => OriginalTradeItemCodec.ToUnified(OldItem(count: 0)));
        Refused(() => OriginalTradeItemCodec.ToUnified(OldItem(count: -1)));
        Refused(() => OriginalTradeItemCodec.ToUnified(new byte[] { 10, 1, 65 }));
        Refused(() => OriginalTradeItemCodec.ToUnified(OldItem().Concat(new byte[] { 56, 1 }).ToArray()));
        Refused(() => OriginalTradeItemCodec.ToUnified(OldItem().Concat(new byte[] { 16, 1 }).ToArray()));
        byte[] depth = OldItem(); for (int i = 0; i < 16; i++) depth = OldItem(inner: depth);
        Refused(() => OriginalTradeItemCodec.ToUnified(depth));
        foreach (var bad in new[] { new FrameworkItemPayload { CodecId = "stateful.scribe", PayloadBytes = new byte[] { 1 } }, new FrameworkItemPayload { CodecId = "core.item.vanilla", PayloadBytes = new Item { DefName = "Steel", StackCount = 1 }.ToByteArray() } }) Refused(() => OriginalTradeItemCodec.ToOriginal(bad));
        var metadata = OriginalTradeItemCodec.ToUnified(OldItem()); metadata.Metadata.Add(new FrameworkMetadataEntry { Key = "extra", Value = "preserve" }); Refused(() => OriginalTradeItemCodec.ToOriginal(metadata));
        var unknown = OriginalTradeItemCodec.ToUnified(OldItem()); unknown.PayloadBytes = unknown.PayloadBytes.Concat(new byte[] { 56, 1 }).ToArray(); Refused(() => OriginalTradeItemCodec.ToOriginal(unknown));
        // Test the actual fixed original producer bytes, not adapter-generated expectations.
        Golden();
        BasicTerms();
        EndpointBoundaries();
        string root = Path.Combine(Path.GetTempPath(), "phinix-original-offer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var f = new Fixture(root); string trade = f.Create();
            var packet = Request(trade, "stable-token", new[] { OldItem(), nested, OldItem() });
            var request = OriginalTradeOfferCodec.Decode(packet);
            string operation = OriginalTradeOfferCodec.OperationId(request);
            f.ExpectOperation = operation; f.Submit(packet);
            Check(f.Last.Success && f.Last.Token == "stable-token" && f.Last.Trade == trade, "Old response lost actual token/trade/success");
            Check(f.Last.Items.Count == 3 && f.Last.Items[0].SequenceEqual(OldItem()) && f.Last.Items[1].SequenceEqual(nested) && f.Last.Items[2].SequenceEqual(OldItem()), "Batch order or duplicate stacks silently filtered");
            string before = f.Snapshot(); long count = f.Count(operation); Check(count == 1, "Operation not stored exactly once");
            f.Reopen(); f.Submit(Request(trade, "stable-token", new[] { OldItem(), nested, OldItem() }, session: "session-new"));
            Check(f.Last.Success && f.Count(operation) == 1 && f.Snapshot() == before, "Reconnect/restart did not replay original committed result");
            f.ExpectOperation = null; f.Submit(Request(trade, "stable-token", new[] { OldItem(count: 9) }, session: "session-new"));
            Check(!f.Last.Success && f.Last.Message == "OriginalOfferOperationConflict" && f.Count(operation) == 1 && f.Snapshot() == before, "Conflicting token overwrote original quote/result");
            foreach (var bad in new[] { Request(trade, "bad-tail", new[] { OldItem(), new byte[] { 10, 1, 65 } }), Request(trade, "", new[] { OldItem() }), Request(trade, "wrong-session", new[] { OldItem() }, session: "bad"), Request(trade, "cross-owner", new[] { OldItem() }, user: "b", session: "session-new") })
            {
                f.Submit(bad); Check(!f.Last.Success && f.Snapshot() == before, "Invalid whole batch/identity/token changed quote");
            }
            f.Readiness.Selected = true; f.Submit(Request(trade, "profile-switch", new[] { OldItem() })); f.Readiness.Selected = false;
            Check(!f.Last.Success && f.Snapshot() == before, "Ready Framework connection bypassed its selected profile");
            // Original status has no snapshot version. Never stamp last-sent/latest version
            // onto it as if the client had actually observed that quote.
            FrameworkTradeStatusUpdateResponse status = null;
            f.Service.HandleStatusUpdateRequest(new FrameworkPacket { MessageId = "unbound-original-consent", PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeStatusUpdateRequest { TradeId = trade, Accepted = true }) },
                new ServerFrameworkContext { ConnectionId = "connection-a", SessionId = "session-a", SenderUuid = "a", SendMessage = (_, p) => { if (p.MessageType == FrameworkTradeProtocol.StatusUpdateResponseType) status = FrameworkSerialization.DeserializePayload<FrameworkTradeStatusUpdateResponse>(p.PayloadJson); } });
            Check(status != null && !status.Success && f.Snapshot() == before, "Original unversioned acceptance was upgraded into fabricated consent");
            Check(f.Count(OriginalTradeOfferCodec.OperationId(OriginalTradeOfferCodec.Decode(Request(trade, "bad-tail", new[] { OldItem() })))) == 0, "Pre-validation failure reached business commit");
            var empty = Request(trade, "empty", Array.Empty<byte[]>(), session: "session-new"); f.ExpectOperation = OriginalTradeOfferCodec.OperationId(OriginalTradeOfferCodec.Decode(empty)); f.Submit(empty);
            Check(f.Last.Success && f.Last.Items.Count == 0, "Empty offer no longer clears only own quote");
            // COMMIT happened, but a response callback fails with a conflict-shaped exception.
            // The adapter must not catch that and invent a new failed business result.
            var lost = Request(trade, "lost-response", new[] { OldItem(count: 3) }, session: "session-new");
            int attempts = 0; f.ExpectOperation = null;
            try { f.Bridge.Submit("connection-a", lost, (_, __) => { attempts++; throw new OperationConflictException(); }, (_, __) => { }); throw new Exception("Expected held response"); }
            catch (OperationConflictException) { }
            Check(attempts == 1, "Post-commit send exception was rewritten as refusal");
            f.Reopen(); f.Submit(lost); Check(f.Last.Success && f.Last.Items.Single().SequenceEqual(OldItem(count: 3)), "Lost reply changed original committed result");
            // Real SQLite controlled abort inside operations INSERT, not a mocked commit result.
            var rollback = Request(trade, "rollback", new[] { OldItem(count: 8) }, session: "session-new");
            string rollbackId = OriginalTradeOfferCodec.OperationId(OriginalTradeOfferCodec.Decode(rollback)); before = f.Snapshot(); string ledgerBefore = f.LedgerHash();
            using (var c = f.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TRIGGER fail_original_offer BEFORE INSERT ON operations WHEN NEW.operation='" + rollbackId + "' BEGIN SELECT RAISE(ABORT,'synthetic-precommit'); END;"; cmd.ExecuteNonQuery(); }
            f.Last = null;
            try { f.Submit(rollback); throw new Exception("Expected SQLite abort"); } catch (IOException) { }
            Check(f.Last == null && f.Count(rollbackId) == 0 && f.Snapshot() == before && f.LedgerHash() == ledgerBefore, "Commit failure emitted an outcome or changed durable quote");
            f.Reopen(); Check(f.Snapshot() == before, "Rollback persisted partial quote after reload");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"ORIGINAL OFFER RESULT passed; checks={checks}; realSQLite=true; goldenOriginal=true; publicTradingIngress=component; gameAssets=false");
    }
    private static void Golden()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "Tests/Fixtures/OriginalTrade/normalized.json"))) folder = folder.Parent;
        if (folder == null) throw new Exception("Pinned original fixtures missing");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.FullName, "Tests/Fixtures/OriginalTrade/normalized.json")));
        int requests = 0;
        foreach (var entry in json.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            if (entry.GetProperty("typeUrl").GetString() != "Phinix/Trading.UpdateTradeItemsPacket" || !entry.GetProperty("direction").GetString().StartsWith("client-to")) continue;
            requests++;
            byte[] bytes = Convert.FromBase64String(entry.GetProperty("derivedAnyBase64").GetString());
            Check(Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.GetProperty("derivedSha256").GetString(), StringComparison.OrdinalIgnoreCase), "Golden hash mismatch");
            // Fixture intentionally strips SessionId: add a synthetic session only.
            var any = Any.Parser.ParseFrom(bytes); any.Value = ByteString.CopyFrom(new byte[] { 10, 1, 83 }.Concat(any.Value).ToArray());
            var decoded = OriginalTradeOfferCodec.Decode(any.ToByteArray());
            foreach (var item in decoded.RawItems) Check(OriginalTradeItemCodec.ToOriginal(OriginalTradeItemCodec.ToUnified(item)).SequenceEqual(item), "Actual fixed original item lost fields");
        }
        Check(requests == 3, "Golden producer request checks became vacuous");
    }
    private sealed class Ready : IServerSessionReadiness
    {
        public bool Selected; public event EventHandler<ServerSessionReadyEventArgs> SessionReady { add { } remove { } }
        public bool TryGetReady(string c, out ServerSessionReadyEventArgs s) { s = null; return Selected; }
    }
    private sealed class Fixture
    {
        public PhinixFrameworkTradeServerService Service; public OriginalTradeOfferBridge Bridge; public Result Last; public string ExpectOperation;
        private readonly string path; public string Database => path + ".sqlite3"; private readonly Users users = new();
        public readonly Ready Readiness = new();
        public Fixture(string root) { path = Path.Combine(root, "trade-state.bin"); Reopen(); }
        public void Reopen() { Service = new PhinixFrameworkTradeServerService(users, null, null, new TradeServerConfig(), new OriginalTradeBasicItemProjection()); Service.Load(path); Bridge = new OriginalTradeOfferBridge(Service, users, Readiness); }
        public SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); c.Open(); return c; }
        public string LedgerHash()
        {
            using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value FROM state WHERE id='trade-ledger-v1'";
            return Convert.ToHexString(SHA256.HashData((byte[])cmd.ExecuteScalar()));
        }
        public long Count(string operation) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM operations WHERE principal='a' AND operation=$o"; cmd.Parameters.AddWithValue("$o", operation); return (long)cmd.ExecuteScalar(); }
        public string Create(TradeNegotiationTerms terms = TradeNegotiationTerms.ObservedQuote)
        {
            string id = null;
            Service.HandleCreateWithTerms(new FrameworkPacket { MessageId = "setup-create", PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeCreateRequest { OtherPartyUuid = "b" }) }, new ServerFrameworkContext { ConnectionId = "connection-a", SessionId = "session-a", SenderUuid = "a", SendMessage = (_, p) => { if (p.MessageType == FrameworkTradeProtocol.CreateResponseType) { var r = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(p.PayloadJson); Check(r.Success, "Real setup create failed"); id = r.TradeId; } } }, terms);
            return id;
        }
        public void Submit(byte[] bytes)
        {
            Last = null; Bridge.Submit("connection-a", bytes, (_, data) => {
                var result = ReadResponse(data);
                if (result.Success && ExpectOperation != null) Check(Count(ExpectOperation) == 1, "Old success escaped before durable operation commit");
                Last = result;
            }, (_, __) => { });
        }
        public string Snapshot()
        {
            string result = null;
            Service.HandleSnapshotRequest(new ServerFrameworkContext { ConnectionId = "connection-a", SessionId = "session-a", SenderUuid = "a", SendMessage = (_, p) => result = p.PayloadJson }); return result;
        }
    }
    // Only the external user/authority directory is isolated. The transaction/store/serializer are real.
    private sealed class Users : IServerUserManager, IServerPrincipalAuthorization
    {
        public event EventHandler<ServerLoginEventArgs> OnLogin { add { } remove { } }
        public bool IsAuthenticatedPrincipal(string connection, string session, string principal) => connection == "connection-a" && principal == "a" && (session == "session-a" || session == "session-new");
        public bool IsLoggedIn(string c, string p) => true; public string[] GetConnections() => new[] { "connection-a", "connection-b" };
        public bool TryGetConnection(string u, out string c) { c = "connection-" + u; return true; }
        public bool TryGetDisplayName(string u, out string name) { name = u; return true; }
        public bool TryGetLoggedIn(string u, out bool loggedIn) { loggedIn = true; return true; }
        public bool TryGetAcceptingTrades(string u, out bool accepting) { accepting = true; return true; }
    }
}
