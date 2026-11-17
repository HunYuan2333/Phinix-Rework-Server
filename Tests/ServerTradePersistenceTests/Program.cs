using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Data.Sqlite;
using Phinix.TradeExtension;
using Phinix.TradeExtension.Server;
using ServerRuntime;
using ServerStorage;
using UserManagement;
using Utils.Framework;

internal static partial class Program
{
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--child")
        {
            var f = new Fixture(args[1]);
            f.AfterSend = p => { if (p.MessageType == FrameworkTradeProtocol.OfferUpdateResponseType) { File.WriteAllText(Path.Combine(args[1], "barrier"), "committed-response-held"); Thread.Sleep(Timeout.Infinite); } };
            f.Cache("child-item", 17); f.Offer(args[2], new[] { "child-item" }, "child-offer"); return;
        }
        string root = Path.Combine(Path.GetTempPath(), "phinix-trade-durable-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            ItemCacheLifecycle(Path.Combine(root, "item-cache"));
            NotificationReliability(Path.Combine(root, "notification-reliability"));
            SnapshotFailureStillRecovers();
            TransientReadContention(Path.Combine(root, "transient-read-contention"));
            DeferredRecoveryFairness(Path.Combine(root, "deferred-recovery-fairness"));
            ModernReceipts(Path.Combine(root,"modern-receipts"));
            SqlPendingIndex(Path.Combine(root, "sql-pages"));
            ConfigurableQuotas(Path.Combine(root, "configuration"));
            AuthorityHealth(Path.Combine(root, "health"));
            LiveAuthorityRevision(Path.Combine(root, "revision"));
            PrincipalQuotas(Path.Combine(root, "principal"));
            CursorValidation(Path.Combine(root, "cursors"));
            RecoveryPages(Path.Combine(root, "pages"));
            CapacityReservations(Path.Combine(root, "capacity"));
            ExistingCapacityPressure(Path.Combine(root, "existing-capacity"));
            OperationQueries(Path.Combine(root, "queries"));
            ConsentVersions(Path.Combine(root, "consent"));
            Migration(Path.Combine(root, "migration"));
            var f = new Fixture(Path.Combine(root, "live"));
            string trade = f.Create("create-1"); byte[] sealedSource = File.ReadAllBytes(f.Path);
            f.Reopen(); Check(f.Snapshot().Trades.Single().TradeId == trade, "Create not committed before restart");
            f.Create("create-1"); Check(f.Snapshot().Trades.Count == 1, "Create replay duplicated trade");
            f.Sent.Clear(); Throws<OperationConflictException>(() => f.Create("create-1", "c")); Check(f.Sent.Count == 0, "Conflict forged result");
            f.Cache("item-1", 7); f.Offer(trade, new[] { "item-1", "missing" }, "bad-batch"); Check(!f.OfferResponse.Success, "Missing batch accepted");
            f.Offer(trade, new[] { "item-1" }, "offer-1"); Check(f.OfferResponse.Success, "Rejected batch consumed legitimate reference");
            f.Reopen(); f.Offer(trade, new[] { "item-1" }, "offer-1"); Check(f.OfferResponse.Success && f.OfferResponse.Items.Single().PayloadBytes[0] == 7, "Offer replay required consumed cache");
            f.Cache("item-1", 99); f.Sent.Clear(); Throws<OperationConflictException>(() => f.Offer(trade, new[] { "item-1" }, "offer-1")); Check(f.Sent.Count == 0, "Changed staged payload accepted as original operation");
            f.Cache("preserve", 8);
            Sql(f.Database, "CREATE TRIGGER test_fail BEFORE INSERT ON operations WHEN NEW.operation='offer-fail' BEGIN SELECT RAISE(ABORT,'synthetic rollback'); END;");
            f.Sent.Clear(); Throws<IOException>(() => f.Offer(trade, new[] { "preserve" }, "offer-fail"));
            Check(f.Sent.Count == 0 && !f.Service.TryCacheItemPacket("preserve", Item(99), f.Context("a")), "Commit failure sent result or consumed reference");
            Check(Scalar(f.Database, "SELECT count(*) FROM operations WHERE operation='offer-fail'").ToString() == "0", "Failed result persisted");
            Sql(f.Database, "DROP TRIGGER test_fail"); f.Reopen();
            Check(f.Snapshot().Trades.Single().Participants.Single(p => p.Uuid == "a").ItemsOnOffer.Single().PayloadBytes[0] == 7, "Rollback changed offer");
            f.Status(trade, "a", "accept-a", true); f.Directory.BOnline = false;
            f.Status(trade, "b", "accept-b", true); Check(f.StatusResponse.Success, "Completion failed");
            Check(f.Sent.Count(p => p.MessageType == FrameworkTradeProtocol.CompletedEventType) == 1, "Online recipient not attempted once");
            f.Status(trade, "b", "accept-b", true); Check(f.Sent.All(p => p.MessageType != FrameworkTradeProtocol.CompletedEventType), "Operation replay resent assets");
            f.Sent.Clear(); Throws<OperationConflictException>(() => f.Status(trade, "b", "accept-b", false)); Check(f.Sent.Count == 0, "Committed result was changed to rejection/refund");
            f.Reopen(); Check(f.Snapshot().Trades.Count == 0, "Terminal trade revived");
            f.Directory.BOnline = true; f.Ready("b"); f.Sent.Clear();
            f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send);
            Check(f.Sent.Count(p => p.MessageType == FrameworkTradeProtocol.CompletedEventType) == 1, "Offline Pending not delivered after ready");
            f.Sent.Clear(); f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send); Check(f.Sent.Count == 0, "Unknown delivery blindly repeated");
            f.Service.HandleCompletionAckRequest(Packet(new FrameworkTradeCompletionAckRequest { TradeId = trade }, "ack-b"), f.Context("b"));
            f.Reopen(); f.Sent.Clear(); f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send); Check(f.Sent.Count == 0, "Restart/weak ACK made unknown retryable");
            Check(File.ReadAllBytes(f.Path).SequenceEqual(sealedSource), "Old XML continued as writable authority");
            if (args.Length == 2 && args[0] == "--oracle") SqliteOperationJournal.OpenExisting(f.Database).BackupToNewFile(args[1]);
            Crash(Path.Combine(root, "crash"));
            Console.WriteLine("TRADE DURABLE RESULT passed; migration/restart/batch/replay/conflict/rollback/terminal-outbox/offline-pending/unknown/weak-ack/committed-kill/observed-consent/migration-rollback/operation-query/capacity-reservation/recovery-pages/principal-quota/configured-policy/authority-health; no game asset claim");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static void ItemCacheLifecycle(string root)
    {
        var f = new Fixture(root);
        long now = 0;
        typeof(PhinixFrameworkTradeServerService).GetField("itemCacheClock", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(f.Service, (Func<long>)(() => now));
        string trade = f.Create("cache-create");
        for (int i = 0; i < 200; i++) f.Cache("abandoned-" + i, 7);
        now = 599999;
        int capacityWarnings = 0;
        f.Service.OnLogEntry += (_, _) => { capacityWarnings++; throw new InvalidOperationException("synthetic diagnostic failure"); };
        Check(!f.Service.TryCacheItemPacket("overflow", Item(1), f.Context("a")), "Full cache evicted a live batch");
        for (int i = 0; i < 10; i++)
            Check(!f.Service.TryCacheItemPacket("overflow-" + i, Item(1), f.Context("a")), "Capacity refusal changed after a faulty diagnostic");
        Check(capacityWarnings == 1, "Repeated cache refusals flooded diagnostics");
        // Identical retransmission does not extend the lifetime of abandoned staging.
        f.Cache("abandoned-0", 7);
        now = 600000;
        f.Cache("fresh", 9);
        f.Offer(trade, new[] { "fresh", "abandoned-0" }, "expired-batch");
        Check(!f.OfferResponse.Success && f.OfferResponse.FailureMessage.Contains("expired"), "Expired reference accepted or rejection was ambiguous");
        f.Offer(trade, new[] { "fresh" }, "fresh-offer");
        Check(f.OfferResponse.Success, "Expired batch refusal consumed its fresh reference");
        now += 600000;
        f.Offer(trade, new[] { "fresh" }, "fresh-offer");
        Check(f.OfferResponse.Success, "Durable replay incorrectly required staging after expiry");

        for (int i = 0; i < 200; i++) f.Cache("disconnected-" + i, 4);
        f.Registry.Remove("connection-a");
        f.Ready("b");
        Check(f.Service.TryCacheItemPacket("other-player", Item(2), f.Context("b")), "Disconnected session blocked another player until timeout");
        f.Ready("a");
        f.Offer(trade, new[] { "disconnected-0" }, "old-session-batch");
        Check(!f.OfferResponse.Success, "Reconnection revived a disconnected session's staging");
        f.Cache("reconnected", 5);
        f.Offer(trade, new[] { "reconnected" }, "reconnected-offer");
        Check(f.OfferResponse.Success, "Reconnected session could not submit fresh staging");
        f.WorkQueue.Dispose();
    }

    private static void SqlPendingIndex(string root)
    {
        var f = new Fixture(root);
        for (int i = 0; i < 9; i++)
        {
            string trade = f.Create("sql-create-" + i);
            f.Directory.BOnline = false; f.Cancel(trade, "sql-cancel-" + i); f.Directory.BOnline = true;
        }
        Check((long)Scalar(f.Database, "SELECT count(*) FROM outbox WHERE recipient='B'") == 9, "Pending deliveries have no durable recipient index");
        Check((long)Scalar(f.Database, "SELECT count(*) FROM outbox WHERE recipient='A'") == 0, "Attempted delivery remains retryable in SQL");
        Check((long)Scalar(f.Database, "SELECT min(length(payload)) FROM outbox") == 32, "Index must contain evidence hashes, not duplicated asset payloads");
        f.Reopen(); f.Ready("b");
        Sql(f.Database, "CREATE TRIGGER test_index_remove BEFORE DELETE ON outbox BEGIN SELECT RAISE(ABORT,'synthetic index rollback'); END;");
        f.Sent.Clear(); Throws<IOException>(() => f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send));
        Check(f.Sent.Count == 0 && (long)Scalar(f.Database, "SELECT count(*) FROM outbox") == 9, "Index failure leaked send or consumed Pending");
        Sql(f.Database, "DROP TRIGGER test_index_remove"); f.Reopen();
        f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send);
        Check(f.Sent.Count == 8 && (long)Scalar(f.Database, "SELECT count(*) FROM outbox") == 1, "SQL recovery page did not commit exactly eight attempts");
        f.WorkQueue.Drain(); Check(f.Sent.Count == 9 && (long)Scalar(f.Database, "SELECT count(*) FROM outbox") == 0, "SQL cursor lost final pending row");
        f.Reopen(); f.Sent.Clear(); f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send);
        Check(f.Sent.Count == 0, "SQL projection revived Unknown");
        string offline = f.Create("sql-final-create"); f.Directory.BOnline = false; f.Cancel(offline, "sql-final-cancel");
        Sql(f.Database, "UPDATE outbox SET recipient='wrong-recipient'");
        Throws<InvalidDataException>(() => f.Reopen());
        Sql(f.Database, "UPDATE outbox SET recipient='B', payload=zeroblob(32)");
        Throws<InvalidDataException>(() => f.Reopen());

        var old = new Fixture(root + "-bootstrap", new TradeServerConfig { PrincipalRetainedTradeLimit = 0 });
        for (int i = 0; i < 65; i++)
        {
            string trade = old.Create("bootstrap-create-" + i);
            old.Directory.BOnline = false; old.Cancel(trade, "bootstrap-cancel-" + i); old.Directory.BOnline = true;
        }
        byte[] authoritative = (byte[])Scalar(old.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'");
        // An isolated preceding-version fixture has no derived index or migration marker.
        Sql(old.Database, "DELETE FROM outbox; DELETE FROM state WHERE id='trade-pending-index-v1'; DELETE FROM operations WHERE principal='system.migration' AND operation LIKE 'pending-index-v1-%'; DROP INDEX outbox_recipient_delivery;");
        Sql(old.Database, "CREATE TRIGGER test_index_bootstrap BEFORE INSERT ON operations WHEN NEW.operation='pending-index-v1-64' BEGIN SELECT RAISE(ABORT,'synthetic bootstrap interruption'); END;");
        Throws<IOException>(() => old.Reopen());
        Check((long)Scalar(old.Database, "SELECT count(*) FROM outbox") == 64 && Scalar(old.Database, "SELECT value FROM state WHERE id='trade-pending-index-v1'") == null, "Incomplete bootstrap became ready or leaked failed chunk");
        Sql(old.Database, "DROP TRIGGER test_index_bootstrap"); old.Reopen();
        Check((long)Scalar(old.Database, "SELECT count(*) FROM outbox") == 65 && ((byte[])Scalar(old.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'")).SequenceEqual(authoritative), "Resumed bootstrap changed authority or lost pending rows");
        old.Ready("b"); old.Sent.Clear(); old.Service.HandleUserLoggedIn("connection-b", "session-b", "b", old.Send);
        for (int i = 0; i < 8; i++) old.WorkQueue.Drain();
        Check(old.Sent.Count == 65 && (long)Scalar(old.Database, "SELECT count(*) FROM outbox") == 0, "Bootstrap/recovery lost or repeated a pending delivery");
    }
    private static void LiveAuthorityRevision(string root)
    {
        var f = new Fixture(root); string first = f.Create("revision-first");
        string old = f.Database + ".old", latest = f.Database + ".latest";
        SqliteOperationJournal.OpenExisting(f.Database).BackupToNewFile(old);
        string second = f.Create("revision-second", "c");
        SqliteOperationJournal.OpenExisting(f.Database).BackupToNewFile(latest);
        File.Copy(old, f.Database, true);
        Throws<IOException>(() => f.Snapshot());
        File.Copy(latest, f.Database, true);
        Throws<IOException>(() => f.Create("revision-sticky", "d"));
        f.Reopen(); Check(f.Snapshot().Trades.Select(t => t.TradeId).OrderBy(x => x).SequenceEqual(new[] { first, second }.OrderBy(x => x)), "Inspected reload lost committed trades");
        Check((long)Scalar(f.Database, "SELECT count(*) FROM operations WHERE operation='revision-sticky'") == 0, "Stale live authority wrote after rollback");
    }
    private static void AuthorityHealth(string root)
    {
        var f = new Fixture(root); string trade = f.Create("health-create");
        string marker = File.ReadAllText(f.Path + ".authority");
        File.Delete(f.Path + ".authority"); f.Sent.Clear();
        Throws<IOException>(() => f.Create("health-refused", "c")); Check(f.Sent.Count == 0, "Missing authority evidence still accepted mutation");
        File.WriteAllText(f.Path + ".authority", marker);
        Throws<IOException>(() => f.Create("health-sticky", "c"));
        Check(Scalar(f.Database, "SELECT count(*) FROM operations WHERE operation LIKE 'health-refused%' OR operation='health-sticky'").ToString() == "0", "Failed authority admission persisted success");
        f.Reopen(); Check(f.Snapshot().Trades.Single().TradeId == trade, "Restored authority evidence lost committed state");
        File.WriteAllText(f.Path + ".authority", new string('0', 129));
        Throws<IOException>(() => f.Snapshot()); File.WriteAllText(f.Path + ".authority", marker); f.Reopen();
        f.Cancel(trade, "health-cancel"); Check(f.StatusResponse.Success, "Inspection/reopen did not restore authority");
    }
    private static void ConfigurableQuotas(string root)
    {
        var config = new TradeServerConfig { PrincipalRetainedTradeLimit = 1, PrincipalPayloadByteLimit = 1, PrincipalAuxiliaryByteLimit = 0, RecoveryPageSize = 1 };
        var f = new Fixture(root, config); string t = f.Create("configured-create");
        config.PrincipalRetainedTradeLimit = 0; config.PrincipalPayloadByteLimit = 0;
        Check(string.IsNullOrEmpty(f.Create("configured-other", "c")), "Mutable config changed running admission policy");
        Check(f.Service.TryCacheItemPacket("configured-item", new FrameworkItemPayload { CodecId = "synthetic.opaque", PayloadBytes = new byte[2] }, f.Context("a")), "Configured fixture cache");
        f.Offer(t, new[] { "configured-item" }, "configured-offer"); Check(!f.OfferResponse.Success, "Configured payload limit not applied inside transaction candidate");
        f.Reopen(); f.Offer(t, new[] { "configured-item" }, "configured-offer"); Check(!f.OfferResponse.Success, "Configured rejection was not immutable after reload");
        Check(!string.IsNullOrEmpty(f.Create("configured-now-enabled", "c")), "Explicit quota zero did not disable count admission");
        var unlimited = new Fixture(root + "-disabled", new TradeServerConfig { PrincipalRetainedTradeLimit = 0, PrincipalPayloadByteLimit = 0, PrincipalAuxiliaryByteLimit = 0, RecoveryPageSize = 1 });
        for (int i = 0; i < 3; i++) { string id = unlimited.Create("configured-page-" + i); unlimited.Directory.BOnline = false; unlimited.Cancel(id, "configured-cancel-" + i); unlimited.Directory.BOnline = true; }
        unlimited.Ready("b"); unlimited.Sent.Clear(); unlimited.Service.HandleUserLoggedIn("connection-b", "session-b", "b", unlimited.Send);
        Check(unlimited.Sent.Count == 1, "Configured recovery page size ignored"); unlimited.WorkQueue.Drain(); Check(unlimited.Sent.Count == 2, "Configured continuation page size ignored"); unlimited.WorkQueue.Drain(); Check(unlimited.Sent.Count == 3, "Configured recovery did not finish");
        string big = unlimited.Create("configured-global-protection");
        string[] large = Enumerable.Range(0, 5).Select(i => "configured-large-" + i).ToArray();
        foreach (string id in large) Check(unlimited.Service.TryCacheItemPacket(id, new FrameworkItemPayload { CodecId = "synthetic.opaque", PayloadBytes = new byte[256 * 1024] }, unlimited.Context("a")), "Disabled quota global fixture cache");
        unlimited.Offer(big, large, "configured-global-refusal"); Check(!unlimited.OfferResponse.Success, "Disabling principal quotas also disabled the global terminal reserve");
        foreach (var bad in new[] { new TradeServerConfig { PrincipalRetainedTradeLimit = -1 }, new TradeServerConfig { PrincipalPayloadByteLimit = -1 }, new TradeServerConfig { PrincipalAuxiliaryByteLimit = -1 }, new TradeServerConfig { RecoveryPageSize = 0 }, new TradeServerConfig { RecoveryPageSize = 65 } })
            Throws<ArgumentOutOfRangeException>(() => new PhinixFrameworkTradeServerService(new SyntheticDirectory(), null, null, bad));
    }
    private static void PrincipalQuotas(string root)
    {
        var f = new Fixture(root); string first = f.Create("owner-create");
        var refs = Enumerable.Range(0, 4).Select(i => "owner-item-" + i).ToArray();
        foreach (string id in refs)
            Check(f.Service.TryCacheItemPacket(id, new FrameworkItemPayload { CodecId = "synthetic.opaque", PayloadBytes = new byte[256 * 1024] }, f.Context("a")), "Owner quota fixture cache");
        f.Offer(first, refs, "owner-full"); Check(f.OfferResponse.Success, "Exactly 1 MiB source payload refused");
        f.Status(first, "a", "owner-consent", true);
        string second = f.Create("owner-second", "c"); f.Cache("one-more", 9);
        byte[] before = (byte[])Scalar(f.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'");
        f.Offer(second, new[] { "one-more" }, "owner-overflow");
        Check(!f.OfferResponse.Success, "Per-principal payload quota was not enforced across trades");
        Check(((byte[])Scalar(f.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'")).SequenceEqual(before), "Owner rejection mutated ledger/consent");
        f.Offer(second, new[] { "one-more" }, "owner-overflow"); Check(!f.OfferResponse.Success, "Owner refusal replay changed");
        Check(!f.Service.TryCacheItemPacket("one-more", Item(99), f.Context("a")), "Owner refusal consumed legitimate reference");
        string unrelated = f.CreateAs("c", "d", "owner-unrelated");
        Check(!string.IsNullOrEmpty(unrelated), "Unrelated participant was blocked by another source quota");
        Check(f.Service.TryCacheItemPacket("other-owner", new FrameworkItemPayload { CodecId = "synthetic.opaque", PayloadBytes = new byte[32 * 1024] }, f.Context("c")), "Other source fixture cache");
        f.OfferAs(unrelated, new[] { "other-owner" }, "owner-independent", "c");
        Check(f.OfferResponse.Success, "Source quota was charged to the recipient or globally leaked");
        f.Offer(first, Array.Empty<string>(), "owner-explicit-clear"); Check(f.OfferResponse.Success, "Explicit quote reduction could not release active payload quota");
        f.Offer(second, new[] { "one-more" }, "owner-after-reduction"); Check(f.OfferResponse.Success, "Rejected reference was lost or active quota not recalculated");
        f.Reopen(); f.Offer(second, new[] { "one-more" }, "owner-overflow"); Check(!f.OfferResponse.Success, "Historical owner refusal was re-executed after quota became available");

        var metadata = new Fixture(root + "-metadata"); string mt = metadata.Create("metadata-create"); metadata.Cache("valid-reference", 5);
        Check(metadata.Service.TryCacheItemPacket("large-metadata", new FrameworkItemPayload { CodecId = "synthetic.opaque", Metadata = new List<FrameworkMetadataEntry> { new() { Key = "m", Value = new string('m', 64 * 1024) } } }, metadata.Context("a")), "Metadata fixture cache");
        metadata.Offer(mt, new[] { "valid-reference", "large-metadata" }, "metadata-refused"); Check(!metadata.OfferResponse.Success, "Source auxiliary text quota bypassed by metadata");
        metadata.Offer(mt, new[] { "valid-reference" }, "metadata-valid"); Check(metadata.OfferResponse.Success, "Metadata refusal consumed valid batch member");

        var slots = new Fixture(root + "-slots");
        for (int i = 0; i < 31; i++) { string t = slots.Create("slot-create-" + i); slots.Cancel(t, "slot-cancel-" + i); }
        string last = slots.Create("slot-last"); Check(!string.IsNullOrEmpty(last), "32nd retained slot was refused");
        Check(string.IsNullOrEmpty(slots.Create("slot-overflow", "c")), "Retained trade slot quota was not enforced");
        Check(string.IsNullOrEmpty(slots.CreateAs("A", "case-peer", "slot-case")), "Principal case variation bypassed quota");
        slots.Status(last, "a", "slot-accept-a", true); slots.Status(last, "b", "slot-accept-b", true);
        Check(slots.StatusResponse.Success, "Slot quota blocked an already admitted completion");
        slots.Service.HandleCompletionAckRequest(Packet(new FrameworkTradeCompletionAckRequest { TradeId = last }, "slot-weak-ack"), slots.Context("b"));
        Check(string.IsNullOrEmpty(slots.Create("slot-after-ack", "c")), "Weak ACK released retained quota");
        Check(!string.IsNullOrEmpty(slots.CreateAs("c", "d", "slot-other-party")), "Unrelated principal blocked by retained slots");
        slots.Reopen(); Check(string.IsNullOrEmpty(slots.Create("slot-after-restart", "c")), "Restart reset retained principal quota");
        slots.Ready("b"); slots.Sent.Clear(); slots.Service.HandleUserLoggedIn("connection-b", "session-b", "b", slots.Send);
        for (int i = 0; i < 4; i++) slots.WorkQueue.Drain();
        Check(slots.Sent.Count == 32, "Principal quota stopped already committed pending recovery");
        Check(string.IsNullOrEmpty(slots.Create("slot-after-delivery", "c")), "Transport attempt released retained slot quota");

        var text = new Fixture(root + "-utf8"); string tt = text.Create("text-create");
        string[] textRefs = Enumerable.Range(0, 4).Select(i => "utf8-" + i).ToArray();
        foreach (string id in textRefs) Check(text.Service.TryCacheItemPacket(id, new FrameworkItemPayload { CodecId = "synthetic.opaque", PayloadJson = new string('界', 100000) }, text.Context("a")), "UTF8 fixture cache");
        text.Offer(tt, textRefs, "text-refused"); Check(!text.OfferResponse.Success, "Payload quota counted characters instead of UTF8 bytes");

        var retained = new Fixture(root + "-retained"); string rt = retained.Create("retained-create");
        string[] retainedRefs = Enumerable.Range(0, 4).Select(i => "retained-" + i).ToArray();
        foreach (string id in retainedRefs) Check(retained.Service.TryCacheItemPacket(id, new FrameworkItemPayload { CodecId = "synthetic.opaque", PayloadBytes = new byte[256 * 1024] }, retained.Context("a")), "Retained fixture cache");
        retained.Offer(rt, retainedRefs, "retained-offer"); Check(retained.OfferResponse.Success, "Retained boundary setup failed");
        retained.Status(rt, "a", "retained-a", true); retained.Status(rt, "b", "retained-b", true);
        retained.Service.HandleCompletionAckRequest(Packet(new FrameworkTradeCompletionAckRequest { TradeId = rt }, "retained-ack"), retained.Context("b"));
        // Synthetic historical outbox without terminal snapshots: source attribution
        // must still be recovered from recipient/other-party and cancelled semantics.
        using (var c = new SqliteConnection("Data Source=" + retained.Database))
        {
            c.Open(); using var read = c.CreateCommand(); read.CommandText = "SELECT value FROM state WHERE id='trade-ledger-v1'";
            var xml = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString((byte[])read.ExecuteScalar()!));
            xml.Root!.Elements().Single(e => e.Name.LocalName == "TerminalTrades").RemoveNodes();
            using var write = c.CreateCommand(); write.CommandText = "UPDATE state SET value=$v WHERE id='trade-ledger-v1'";
            write.Parameters.AddWithValue("$v", System.Text.Encoding.UTF8.GetBytes(xml.ToString())); write.ExecuteNonQuery();
        }
        retained.Reopen(); string next = retained.Create("retained-next", "c"); retained.Cache("retained-extra", 1);
        retained.Offer(next, new[] { "retained-extra" }, "retained-refused");
        Check(!retained.OfferResponse.Success, "Orphan/weak-ACK outbox payload escaped source quota after restart");
    }
    private static void CursorValidation(string root)
    {
        var f = new Fixture(root);
        for (int i = 0; i < 2; i++)
        {
            string trade = f.Create("cursor-create-" + i);
            f.Directory.BOnline = false; f.Cancel(trade, "cursor-cancel-" + i); f.Directory.BOnline = true;
        }
        byte[] original = (byte[])Scalar(f.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'");
        void Write(byte[] bytes)
        {
            using var c = new SqliteConnection("Data Source=" + f.Database); c.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE state SET value=$v WHERE id='trade-ledger-v1'"; cmd.Parameters.AddWithValue("$v", bytes); cmd.ExecuteNonQuery();
        }
        foreach (bool duplicate in new[] { true, false })
        {
            var xml = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(original));
            var ids = xml.Root!.Elements().Single(e => e.Name.LocalName == "PendingCompletionNotifications").Elements()
                .Where(e => e.Elements().Any(c => c.Name.LocalName == "RecipientUuid" && c.Value == "b"))
                .Select(e => e.Elements().Single(c => c.Name.LocalName == "DeliveryId")).ToArray();
            ids[0].Value = duplicate ? ids[1].Value : "";
            Write(System.Text.Encoding.UTF8.GetBytes(xml.ToString()));
            Throws<InvalidDataException>(() => f.Reopen());
            Write(original); f.Reopen();
        }
        f.Ready("b"); f.Sent.Clear(); f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send);
        Check(f.Sent.Count == 2, "Invalid cursor handling discarded recoverable source records");
    }
    private static void RecoveryPages(string root)
    {
        var f = new Fixture(root);
        for (int i = 0; i < 17; i++)
        {
            string trade = f.Create("page-create-" + i);
            f.Directory.BOnline = false; f.Cancel(trade, "page-cancel-" + i); f.Directory.BOnline = true;
        }
        int Pending()
        {
            var xml = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString((byte[])Scalar(f.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'")));
            return xml.Root!.Elements().Single(e => e.Name.LocalName == "PendingCompletionNotifications").Elements().Count(e =>
                e.Elements().Any(c => c.Name.LocalName == "RecipientUuid" && c.Value == "b") && e.Elements().Any(c => c.Name.LocalName == "DeliveryState" && c.Value == "Pending"));
        }
        f.Ready("b"); f.Sent.Clear();
        f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send);
        Check(f.Sent.Count == 8 && Pending() == 9, "Recovery callback sent an unbounded backlog instead of one page");
        f.Registry.Remove("connection-b"); f.WorkQueue.Drain();
        Check(f.Sent.Count == 8 && Pending() == 9, "Disconnected continuation consumed a pending delivery");
        f.Ready("b"); f.Sent.Clear();
        f.Service.HandleUserLoggedIn("connection-b", "stale-session", "b", f.Send);
        Check(f.Sent.Count == 0 && Pending() == 9, "Stale login callback attempted deliveries");
        f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send);
        Check(f.Sent.Count == 8 && Pending() == 1, "Reconnect did not continue previously unattempted deliveries");
        f.Service.CancelPendingRecovery(); f.WorkQueue.Drain();
        Check(Pending() == 1 && f.Sent.Count == 8, "Stopped extension continued recovery");
        f.Reopen(); f.Sent.Clear();
        f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send);
        Check(f.Sent.Count == 1 && Pending() == 0, "Restart did not preserve the last pending record");
        f.WorkQueue.Drain(); f.Sent.Clear();
        f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send);
        Check(f.Sent.Count == 0, "Recovery resent OutcomeUnknown deliveries");

        // A separate uninterrupted recovery must progress automatically between polls.
        var g = new Fixture(root + "-drain");
        for (int i = 0; i < 17; i++)
        {
            string trade = g.Create("drain-create-" + i);
            g.Directory.BOnline = false; g.Cancel(trade, "drain-cancel-" + i); g.Directory.BOnline = true;
        }
        g.Ready("b"); g.Sent.Clear(); g.Service.HandleUserLoggedIn("connection-b", "session-b", "b", g.Send);
        Check(g.Sent.Count == 8, "First drain page");
        g.WorkQueue.Drain(); Check(g.Sent.Count == 16, "Next poll did not process exactly one next page");
        g.WorkQueue.Drain(); Check(g.Sent.Count == 17, "Final drain page did not progress");
        Check(g.Sent.Select(p => FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(p.PayloadJson).TradeId).Distinct().Count() == 17, "Paging duplicated or skipped a trade");
        var warnings = new List<string>(); g.Service.OnLogEntry += (_, e) => warnings.Add(e.Message);
        for (int i = 0; i < 9; i++)
        {
            string trade = g.Create("saturated-create-" + i);
            g.Directory.BOnline = false; g.Cancel(trade, "saturated-cancel-" + i); g.Directory.BOnline = true;
        }
        for (int i = 0; i < 64; i++) Check(g.WorkQueue.TryEnqueue(() => { }), "Queue saturation fixture");
        g.Sent.Clear(); g.Service.HandleUserLoggedIn("connection-b", "session-b", "b", g.Send);
        Check(g.Sent.Count == 8 && warnings.Any(w => w.Contains("unattempted deliveries remain Pending")), "Full queue silently stranded recovery");
        g.WorkQueue.Drain(); Check(g.Sent.Count == 8, "Full queue fell back to synchronous backlog drain");
        PulseRecovery(g.Service);
        for (int i = 0; i < 65; i++) g.WorkQueue.Drain();
        Check(g.Sent.Count == 9, "Queue saturation stranded the last page until reconnect");
        PulseRecovery(g.Service); g.WorkQueue.Drain();
        Check(g.Sent.Count == 9, "Resumed page duplicated already attempted deliveries");
        string interrupted = g.Create("send-failure-create");
        g.Directory.BOnline = false; g.Cancel(interrupted, "send-failure-cancel"); g.Directory.BOnline = true;
        g.Sent.Clear(); g.AfterSend = _ => throw new IOException("synthetic lost response");
        Throws<IOException>(() => g.Service.HandleUserLoggedIn("connection-b", "session-b", "b", g.Send));
        g.AfterSend = null; g.Reopen(); g.Sent.Clear();
        g.Service.HandleUserLoggedIn("connection-b", "session-b", "b", g.Send);
        Check(g.Sent.Count == 0, "Send failure caused attempted delivery to be blindly replayed");
    }
    private static void ExistingCapacityPressure(string root)
    {
        var f = new Fixture(root); string trade = f.Create("existing-create");
        Check(f.Service.TryCacheItemPacket("existing", new FrameworkItemPayload { CodecId = "synthetic.opaque", PayloadBytes = new byte[256 * 1024] }, f.Context("a")), "Existing fixture cache rejected");
        f.Offer(trade, new[] { "existing" }, "existing-offer");
        // Synthetic preceding-version ledger: valid current state, no completion reserve.
        // This is isolated test input, never a supported live database editing procedure.
        using (var c = new SqliteConnection("Data Source=" + f.Database))
        {
            c.Open(); using var read = c.CreateCommand(); read.CommandText = "SELECT value FROM state WHERE id='trade-ledger-v1'";
            var xml = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString((byte[])read.ExecuteScalar()!));
            var items = xml.Descendants().First(e => e.Name.LocalName == "ItemsOnOffer" && e.Elements().Any());
            var item = items.Elements().Single();
            for (int i = 0; i < 4; i++) items.Add(new System.Xml.Linq.XElement(item));
            using var write = c.CreateCommand(); write.CommandText = "UPDATE state SET value=$v WHERE id='trade-ledger-v1'";
            write.Parameters.AddWithValue("$v", System.Text.Encoding.UTF8.GetBytes(xml.ToString())); write.ExecuteNonQuery();
        }
        f.Reopen(); Check(f.Snapshot().Trades.Single().Participants.Single(p => p.Uuid == "a").ItemsOnOffer.Count == 5, "Existing pressure was filtered on load");
        f.Cancel(trade, "existing-cancel"); Check(f.StatusResponse.Success, "Soft admission ceiling blocked existing cancellation");
        f.Reopen(); Check(f.Snapshot().Trades.Count == 0, "Existing cancellation not durable");
        var retained = (byte[])Scalar(f.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'");
        Check(retained.Length > 3 * 1024 * 1024 && retained.Length < 4 * 1024 * 1024, "Existing pressure fixture did not cross soft ceiling");
        Check(string.IsNullOrEmpty(f.Create("existing-refused", "c")), "New trade bypassed existing retained backlog");
        var rejection = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(f.Sent.Single().PayloadJson);
        Check(!rejection.Success && rejection.FailureMessage.Contains("durable ledger capacity"), "Capacity create refusal unclear");
        Check(((byte[])Scalar(f.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'")).SequenceEqual(retained), "New admission discarded retained evidence");
        f.Reopen(); Check(string.IsNullOrEmpty(f.Create("existing-refused", "c")), "Capacity create refusal changed after restart");
        Check(FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(f.Sent.Single().PayloadJson).FailureMessage == rejection.FailureMessage, "Capacity replay lost original refusal message");
    }
    private static void CapacityReservations(string root)
    {
        var f = new Fixture(root); string trade = f.Create("capacity-create");
        f.Cache("original", 7); f.Offer(trade, new[] { "original" }, "capacity-original");
        long version = f.Snapshot().Trades.Single().SnapshotVersion;
        var refs = Enumerable.Range(0, 5).Select(i => "large-" + i).ToArray();
        foreach (string id in refs)
            Check(f.Service.TryCacheItemPacket(id, new FrameworkItemPayload { CodecId = "synthetic.opaque", PayloadBytes = new byte[256 * 1024] }, f.Context("a")), "Synthetic capacity item was not cached");
        f.Offer(trade, refs, "capacity-rejected");
        Check(!f.OfferResponse.Success, "Completion reservation admitted an oversized future terminal ledger");
        Check(f.OfferResponse.FailureMessage.Contains("durable ledger capacity"), "Capacity refusal was misreported as missing references");
        var unchanged = f.Snapshot().Trades.Single();
        Check(unchanged.SnapshotVersion == version && unchanged.Participants.Single(p => p.Uuid == "a").ItemsOnOffer.Single().PayloadBytes[0] == 7, "Capacity rejection changed original quote");
        f.Offer(trade, refs.Take(4).ToArray(), "capacity-admitted");
        Check(f.OfferResponse.Success && f.OfferResponse.Items.Count == 4, "Capacity rejection consumed legitimate references");
        f.Reopen(); Check(f.Snapshot().Trades.Single().Participants.Single(p => p.Uuid == "a").ItemsOnOffer.Count == 4, "Admitted reservation lost after restart");
        string second = f.Create("capacity-second", "c");
        Check(!string.IsNullOrEmpty(second), "Small new trade rejected with available capacity");
        Check(f.Service.TryCacheItemPacket("global", new FrameworkItemPayload { CodecId = "synthetic.opaque", PayloadBytes = new byte[256 * 1024] }, f.Context("a")), "Global fixture cache rejected");
        f.Offer(second, new[] { "global" }, "capacity-global");
        Check(!f.OfferResponse.Success, "Reservation did not account for other active trades");
        f.Status(trade, "a", "capacity-accept-a", true); f.Status(trade, "b", "capacity-accept-b", true);
        Check(f.StatusResponse.Success && f.Snapshot().Trades.Single().TradeId == second, "Reserved trade could not finish under pressure");
        f.Reopen();
        f.Service.HandleCompletionAckRequest(Packet(new FrameworkTradeCompletionAckRequest { TradeId = trade }, "capacity-weak-ack"), f.Context("b"));
        Check(f.Service.TryCacheItemPacket("after-ack", new FrameworkItemPayload { CodecId = "synthetic.opaque", PayloadBytes = new byte[256 * 1024] }, f.Context("a")), "Post-ACK fixture cache rejected");
        f.Offer(second, new[] { "after-ack" }, "capacity-after-ack");
        Check(!f.OfferResponse.Success, "Weak ACK released retained asset budget");
        f.Offer(second, new[] { "global" }, "capacity-global");
        Check(!f.OfferResponse.Success, "Capacity rejection result was not durable");
        f.Cancel(second, "capacity-cancel"); Check(f.StatusResponse.Success, "Cancellation was blocked by capacity");
        f.Reopen(); Check(f.Snapshot().Trades.Count == 0, "Terminal state revived after restart");
        byte[] state = (byte[])Scalar(f.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'");
        var xml = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString(state));
        var pending = xml.Root!.Elements().Single(e => e.Name.LocalName == "PendingCompletionNotifications").Elements().ToArray();
        Check(pending.Length == 4 && state.Length < 4 * 1024 * 1024, "Completion reservation lost delivery records or exceeded storage bound");
        Check(pending.SelectMany(e => e.Descendants().Where(d => d.Name.LocalName == "PayloadBytes")).Sum(e => Convert.FromBase64String(e.Value).Length) == 4 * 256 * 1024, "Terminal delivery payload bytes were dropped");
    }
    private static void OperationQueries(string root)
    {
        var f = new Fixture(root); string trade = f.Create("query-create");
        string fingerprint = Convert.ToHexString((byte[])Scalar(f.Database, "SELECT fingerprint FROM operations WHERE operation='query-create'"));
        FrameworkTradeOperationQueryResponse Query(string who, string op, string hash)
        {
            f.Ready(who); f.Sent.Clear();
            ((IFrameworkTradeOperationQueryApi)f.Service).HandleOperationQueryRequest(Packet(new FrameworkTradeOperationQueryRequest { OperationId = op, RequestFingerprint = hash }, "query-envelope"), f.Context(who));
            Check(f.Sent.Count == 1 && f.Sent[0].MessageType == FrameworkTradeOperationQueryProtocol.ResponseType, "Query triggered business events");
            return FrameworkSerialization.DeserializePayload<FrameworkTradeOperationQueryResponse>(f.Sent[0].PayloadJson);
        }
        string count = Scalar(f.Database, "SELECT count(*) FROM operations").ToString();
        byte[] before = (byte[])Scalar(f.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'");
        var module = new BuiltInTradeServerExtension();
        Check(module.GetCapabilities().Contains(FrameworkTradeOperationQueryProtocol.RequestType) && module.GetCapabilities().Contains(FrameworkTradeOperationQueryProtocol.ResponseType), "Query not advertised");
        Check(module.CanHandleIncomingCommand(new FrameworkPacket { CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request, MessageType = FrameworkTradeOperationQueryProtocol.RequestType }), "Query not routed");
        string expectedHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("create\n{\"OtherPartyUuid\":\"b\"}")));
        Check(fingerprint == expectedHash, "Stored create fingerprint differs from independent literal request");
        var original = Query("a", "query-create", expectedHash);
        Check(original.Status == "Committed" && original.Success == true && original.TradeId == trade && original.SnapshotVersion == 1, "Committed create not queryable");
        Check(Query("b", "query-create", fingerprint).Status == "NotFound", "Query leaked another principal result");
        Check(Query("a", "absent", fingerprint).Status == "NotFound", "Absent operation reported committed");
        Check(Query("a", "query-create", new string('0', 64)).Status == "Conflict", "Query ignored request fingerprint");
        foreach (string invalid in new[] { "", "xx", new string('a', 65), new string('z', 64) })
            Check(Query("a", "query-create", invalid).Status == "InvalidRequest", "Invalid fingerprint accepted");
        Check(Query("a", new string('a', 129), fingerprint).Status == "InvalidRequest", "Unbounded operation query accepted");
        Check(Scalar(f.Database, "SELECT count(*) FROM operations").ToString() == count && ((byte[])Scalar(f.Database, "SELECT value FROM state WHERE id='trade-ledger-v1'")).SequenceEqual(before), "Queries mutated durable ledger or operations");
        foreach (string json in new[] { "{", new string('x', 1025), "null" })
        {
            f.Sent.Clear();
            ((IFrameworkTradeOperationQueryApi)f.Service).HandleOperationQueryRequest(new FrameworkPacket { MessageId = "malformed", PayloadJson = json }, f.Context("a"));
            Check(FrameworkSerialization.DeserializePayload<FrameworkTradeOperationQueryResponse>(f.Sent.Single().PayloadJson).Status == "InvalidRequest", "Malformed query was accepted");
        }
        f.Cache("q-item", 11); f.Offer(trade, new[] { "q-item", "missing" }, "query-rejected");
        string rejectionHash = Convert.ToHexString((byte[])Scalar(f.Database, "SELECT fingerprint FROM operations WHERE operation='query-rejected'"));
        var rejected = Query("a", "query-rejected", rejectionHash);
        Check(rejected.Status == "Committed" && rejected.Success == false, "Committed rejection confused with unknown");
        f.Status(trade, "a", "query-a", true); f.Status(trade, "b", "query-b", true);
        string completeHash = Convert.ToHexString((byte[])Scalar(f.Database, "SELECT fingerprint FROM operations WHERE operation='query-b'"));
        f.Reopen(); var completed = Query("b", "query-b", completeHash);
        Check(completed.Status == "Committed" && completed.Completed && !completed.Cancelled && completed.TradeId == trade, "Terminal result lost after restart");
        Check(Query("a", "query-create", fingerprint).SnapshotVersion == 1, "Query returned current instead of original result");
        Check(!f.Sent[0].PayloadJson.Contains("Items") && !f.Sent[0].PayloadJson.Contains("Participants"), "Query exposed asset payloads");
        Check(Scalar(f.Database, "SELECT count(*) FROM operations WHERE operation='query-envelope'").ToString() == "0", "Read-only query wrote operation");
        f.Sent.Clear(); var wrongSession = f.Context("a"); wrongSession.SessionId = "stale";
        Throws<UnauthorizedAccessException>(() => ((IFrameworkTradeOperationQueryApi)f.Service).HandleOperationQueryRequest(Packet(new FrameworkTradeOperationQueryRequest { OperationId = "query-create", RequestFingerprint = fingerprint }, "query-envelope"), wrongSession));
        Check(f.Sent.Count == 0, "Stale session received query result");
        var unready = f.Context("unready"); f.Sent.Clear();
        Throws<UnauthorizedAccessException>(() => ((IFrameworkTradeOperationQueryApi)f.Service).HandleOperationQueryRequest(null, unready));
        Check(f.Sent.Count == 0, "Unready connection received query result");
        File.Move(f.Database, f.Database + ".held"); f.Sent.Clear();
        Throws<IOException>(() => Query("a", "query-create", fingerprint));
        Check(f.Sent.Count == 0 && !File.Exists(f.Database), "Missing database became NotFound or was recreated");
        File.Move(f.Database + ".held", f.Database);
    }
    private static void ConsentVersions(string root)
    {
        var f = new Fixture(root); string trade = f.Create("consent-create");
        f.Cache("quote-a", 17); f.Offer(trade, new[] { "quote-a" }, "quote-first");
        long seen = f.Snapshot().Trades.Single().SnapshotVersion;
        f.Cache("quote-b", 23); f.Offer(trade, new[] { "quote-b" }, "quote-second");
        long current = f.Snapshot().Trades.Single().SnapshotVersion;
        f.Reopen();
        foreach (var pair in new[] { ("stale", (long?)seen), ("missing", (long?)null), ("future", (long?)(current + 1)) })
        {
            f.StatusObserved(trade, "a", pair.Item1, true, pair.Item2);
            Check(!f.StatusResponse.Success, "Unobserved offer was accepted: " + pair.Item1);
            var unchanged = f.Snapshot().Trades.Single();
            Check(unchanged.SnapshotVersion == current && unchanged.Participants.All(p => !p.Accepted) && unchanged.Participants.Single(p => p.Uuid == "a").ItemsOnOffer.Single().PayloadBytes[0] == 23, "Rejected consent mutated quote");
        }
        f.StatusObserved(trade, "a", "bound-a", true, current); Check(f.StatusResponse.Success, "Current consent rejected");
        f.Sent.Clear(); Throws<OperationConflictException>(() => f.StatusObserved(trade, "a", "bound-a", true, current + 1));
        Check(f.Sent.Count == 0, "Changed consent fingerprint published result");
        f.Reopen(); f.StatusObserved(trade, "b", "bound-b", true, current);
        Check(f.StatusResponse.Success && f.Snapshot().Trades.Count == 0, "Acceptance-only revision invalidated same quote");

        // Emulate the preceding durable schema with an unbound acceptance; upgrade once before serving.
        var old = new Fixture(root + "-upgrade"); string oldTrade = old.Create("old-create");
        old.StatusObserved(oldTrade, "a", "old-accept", true, 1);
        using (var c = new SqliteConnection("Data Source=" + old.Database))
        {
            c.Open(); using var read = c.CreateCommand(); read.CommandText = "SELECT value FROM state WHERE id='trade-ledger-v1'";
            var xml = System.Xml.Linq.XDocument.Parse(System.Text.Encoding.UTF8.GetString((byte[])read.ExecuteScalar()!));
            foreach (var e in xml.Root!.Elements().Where(e => e.Name.LocalName == "ConsentSchemaVersion" || e.Name.LocalName == "OfferVersions").ToArray()) e.Remove();
            using var write = c.CreateCommand(); write.CommandText = "UPDATE state SET value=$v WHERE id='trade-ledger-v1'";
            write.Parameters.AddWithValue("$v", System.Text.Encoding.UTF8.GetBytes(xml.ToString())); write.ExecuteNonQuery();
            using var oldIntent = c.CreateCommand(); oldIntent.CommandText = "UPDATE operations SET fingerprint=$f WHERE operation='old-accept'";
            oldIntent.Parameters.AddWithValue("$f", SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("status\n" + FrameworkSerialization.SerializePayload(new FrameworkTradeStatusUpdateRequest { TradeId = oldTrade, Accepted = true }))));
            oldIntent.ExecuteNonQuery();
        }
        Sql(old.Database, "CREATE TRIGGER test_consent_migration_fail BEFORE INSERT ON operations WHEN NEW.operation='consent-version-v1' BEGIN SELECT RAISE(ABORT,'synthetic consent migration rollback'); END;");
        Throws<IOException>(() => old.Reopen());
        Throws<IOException>(() => old.Snapshot());
        Check(Scalar(old.Database, "SELECT count(*) FROM operations WHERE operation='consent-version-v1'").ToString() == "0", "Failed migration committed its result");
        Sql(old.Database, "DROP TRIGGER test_consent_migration_fail");
        old.Reopen(); var upgraded = old.Snapshot().Trades.Single();
        Check(upgraded.Participants.All(p => !p.Accepted) && upgraded.SnapshotVersion == 3, "Old unbound consent survived migration");
        old.StatusObserved(oldTrade, "a", "old-accept", true, null);
        Check(old.StatusResponse.Success && old.Snapshot().Trades.Single().Participants.All(p => !p.Accepted), "Historical result replay changed migrated consent");
        old.Reopen(); Check(old.Snapshot().Trades.Single().SnapshotVersion == 3, "Consent migration repeated");
        Check(Scalar(old.Database, "SELECT count(*) FROM operations WHERE principal='system.migration' AND operation='consent-version-v1'").ToString() == "1", "Migration not durably recorded");
    }
    private static void Migration(string root)
    {
        Directory.CreateDirectory(root); string path = System.IO.Path.Combine(root, "trade-state.bin");
        Type type = typeof(PhinixFrameworkTradeServerService).Assembly.GetType("Phinix.TradeExtension.Server.PhinixFrameworkTradeStore")!;
        object store = Activator.CreateInstance(type, true)!;
        object[] create = { "a", "b", null, null }; type.GetMethod("TryCreate")!.Invoke(store, create);
        string trade = ((FrameworkTradeStateSnapshot)create[2]).TradeId;
        object[] cancel = { trade, "a", null, true, null, false, false, null }; type.GetMethod("TrySetStatus")!.Invoke(store, cancel);
        type.GetMethod("QueueCompletionNotifications")!.Invoke(store, new[] { cancel[4], true });
        type.GetMethod("Save")!.Invoke(store, new object[] { path }); byte[] before = File.ReadAllBytes(path);
        var f = new Fixture(root); f.Ready("a"); f.Service.HandleUserLoggedIn("connection-a", "session-a", "a", f.Send);
        Check(f.Sent.Count == 0 && File.ReadAllBytes(path).SequenceEqual(before), "Import guessed old pending was unsent");
        File.Move(f.Database, f.Database + ".held"); Throws<IOException>(() => f.Reopen()); Check(!File.Exists(f.Database), "Missing DB re-imported stale XML");
        File.Move(f.Database + ".held", f.Database); f.Reopen();
        File.AppendAllText(path, "synthetic corruption"); Throws<IOException>(() => f.Reopen());
        Throws<IOException>(() => f.Create("after-load-failure")); Check(f.Sent.Count == 0, "Failed load continued with memory authority");
    }
    private static void Crash(string root)
    {
        var f = new Fixture(root); string trade = f.Create("crash-create");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { Assembly.GetExecutingAssembly().Location, "--child", root, trade }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        try
        {
            Check(SpinWait.SpinUntil(() => File.Exists(System.IO.Path.Combine(root, "barrier")) || process.HasExited, 10000) && !process.HasExited, "Trade response barrier failed");
            process.Kill(true); Check(process.WaitForExit(5000), "Trade child kill deadline"); f.Reopen();
            f.Offer(trade, new[] { "child-item" }, "child-offer"); Check(f.OfferResponse.Success && f.OfferResponse.Items.Single().PayloadBytes[0] == 17, "Committed offer lost after process kill");
        }
        finally { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } }
    }
    private static FrameworkItemPayload Item(byte value) => new() { CodecId = "synthetic.opaque", PayloadBytes = new[] { value } };
    private static FrameworkPacket Packet<T>(T payload, string id) => new() { MessageId = id, PayloadJson = FrameworkSerialization.SerializePayload(payload) };
    private static object Scalar(string path, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar()!;
    }
    private static void Sql(string path, string sql)
    { using var c = new SqliteConnection("Data Source=" + path); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private static void ModernReceipts(string root)
    {
        var f=new Fixture(root);f.Ready("a",true);f.Ready("b",true);string trade=f.Create("modern-create");
        f.Cache("modern-item",19);f.Offer(trade,new[]{"modern-item"},"modern-offer");
        f.Status(trade,"a","modern-a",true);f.Status(trade,"b","modern-b",true);
        var events=f.Sent.Where(p=>p.MessageType==FrameworkTradeProtocol.CompletedEventType).ToArray();Check(events.Length==2,"Both modern deliveries missing");
        TradeDeliveryReceiptResponse Receipt(FrameworkPacket delivery,string who,string kind,string hash=null)
        {
            var body=FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(delivery.PayloadJson);
            var request=new TradeDeliveryReceiptRequest {DeliveryId=delivery.Metadata.Single(m=>m.Key==TradeDeliveryProtocol.IdKey).Value,TradeId=trade,Cancelled=false,
                PayloadFingerprint=hash??delivery.Metadata.Single(m=>m.Key==TradeDeliveryProtocol.HashKey).Value,Kind=kind,
                SaveIdentity=kind=="SavedInventory"?new string('a',64):null,CheckpointFingerprint=kind=="SavedInventory"?new string('b',64):null};
            f.Sent.Clear();f.Service.HandleDeliveryReceipt(Packet(request,Guid.NewGuid().ToString("N")),f.Context(who));
            return FrameworkSerialization.DeserializePayload<TradeDeliveryReceiptResponse>(f.Sent.Single().PayloadJson);
        }
        var empty=events.Single(e=>FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(e.PayloadJson).Items.Count==0);
        var full=events.Single(e=>FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(e.PayloadJson).Items.Count==1);
        Check(!Receipt(full,"a","InboxStored").Accepted,"Cross recipient accepted");
        Check(Receipt(empty,"a","InboxStored").State=="Confirmed","Empty delivery requires no game materialization");
        Check(Receipt(full,"b","InboxStored").Accepted,"Inbox receipt refused");
        f.Ready("b",true);f.Sent.Clear();f.Service.HandleUserLoggedIn("connection-b","session-b","b",f.Send);
        Check(f.Sent.Any(p=>p.MessageType==FrameworkTradeProtocol.CompletedEventType&&p.Metadata.Any(m=>m.Key==TradeDeliveryProtocol.IdKey&&m.Value==full.Metadata.Single(x=>x.Key==m.Key).Value)),"Durable unconfirmed delivery not replayed on new epoch");
        Check(Receipt(full,"b","SavedInventory").State=="Confirmed","Saved receipt not committed");
        Check(Receipt(full,"b","SavedInventory").Accepted,"Receipt replay changed original result");
        using(var db=new SqliteConnection("Data Source="+f.Database)){db.Open();using var q=db.CreateCommand();q.CommandText="SELECT count(*) FROM state WHERE id LIKE 'trade.archive.%'";Check((long)q.ExecuteScalar()==1,"Both confirmed trade not archived");}
        f.Reopen();f.Ready("b",true);f.Sent.Clear();f.Service.HandleUserLoggedIn("connection-b","session-b","b",f.Send);
        Check(!f.Sent.Any(p=>p.MessageType==FrameworkTradeProtocol.CompletedEventType),"Confirmed archive replayed assets");f.WorkQueue.Dispose();
    }
    private sealed class Fixture
    {
        public string Path { get; }
        public string Database => Path + ".sqlite3";
        public readonly SyntheticDirectory Directory = new();
        public readonly ServerSessionRegistry Registry = new((_, _, _) => true);
        public PhinixFrameworkTradeServerService Service;
        public ServerWorkQueue WorkQueue;
        public readonly List<FrameworkPacket> Sent = new();
        public Action<FrameworkPacket> AfterSend;
        public Action<string, FrameworkPacket> BeforeSend;
        public readonly List<(string Connection, FrameworkPacket Packet)> SentTargets = new();
        private string expectedPrincipal, expectedOperation;
        private readonly TradeServerConfig config;
        public Fixture(string root, TradeServerConfig config = null) { this.config = config ?? new TradeServerConfig(); System.IO.Directory.CreateDirectory(root); Path = System.IO.Path.Combine(root, "trade-state.bin"); Reopen(); Ready("a"); }
        public void Ready(string who,bool modern=false)
        {
            string[] caps = { FrameworkTradeProtocol.CompletedEventType, FrameworkTradeProtocol.CancelledEventType, FrameworkTradeOperationQueryProtocol.RequestType, FrameworkTradeOperationQueryProtocol.ResponseType };
            if(modern)Registry.Remove("connection-"+who);
            if(modern)caps=caps.Concat(new[]{TradeDeliveryProtocol.Request,TradeDeliveryProtocol.Response}).ToArray();
            Check(Registry.TryBind("connection-" + who, who, "session-" + who, 2, caps, caps, out var ready, out _), "Synthetic readiness bind"); Registry.MarkReady(ready);
        }
        public void Reopen()
        {
            Service?.CancelPendingRecovery(); WorkQueue?.Dispose(); WorkQueue = new ServerWorkQueue(64, 1);
            Service = new PhinixFrameworkTradeServerService(Directory, Registry, WorkQueue, config); Service.Load(Path);
        }
        public ServerFrameworkContext Context(string who) => new() { ConnectionId = "connection-" + who, SessionId = "session-" + who, SenderUuid = who, SendMessage = Send };
        public void Send(string connection, FrameworkPacket packet)
        {
            BeforeSend?.Invoke(connection, packet);
            packet = FrameworkSerialization.DeserializePacket(FrameworkSerialization.SerializePacket(packet));
            if (expectedOperation != null && packet.CommandKind == global::Phinix.Framework.FrameworkCommandKind.Response)
            {
                using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); c.Open();
                using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM operations WHERE principal=$p AND operation=$o"; cmd.Parameters.AddWithValue("$p", expectedPrincipal); cmd.Parameters.AddWithValue("$o", expectedOperation);
                Check((long)cmd.ExecuteScalar()! == 1, "Response published before durable result");
            }
            Sent.Add(packet); SentTargets.Add((connection, packet)); AfterSend?.Invoke(packet);
        }
        private void Run(string who, string op, Action action)
        { Sent.Clear(); expectedPrincipal = who; expectedOperation = op; try { action(); } finally { expectedPrincipal = expectedOperation = null; } }
        public string Create(string op, string other = "b") => CreateAs("a", other, op);
        public string CreateAs(string who, string other, string op)
        { Run(who, op, () => Service.HandleCreateRequest(Packet(new FrameworkTradeCreateRequest { OtherPartyUuid = other }, op), Context(who))); return FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(Sent.First().PayloadJson).TradeId; }
        public void Cache(string id, byte value) => Check(Service.TryCacheItemPacket(id, Item(value), Context("a")), "Cache admission");
        public void Offer(string trade, string[] refs, string op) => OfferAs(trade, refs, op, "a");
        public void OfferAs(string trade, string[] refs, string op, string who) => Run(who, op, () => Service.HandleOfferUpdateRequest(Packet(new FrameworkTradeOfferUpdateRequest { TradeId = trade, ItemPacketRefs = refs.ToList() }, op), Context(who)));
        public FrameworkTradeOfferUpdateResponse OfferResponse => FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateResponse>(Sent.First(p => p.MessageType == FrameworkTradeProtocol.OfferUpdateResponseType).PayloadJson);
        private readonly Dictionary<string, long> observedOperations = new();
        public void Status(string trade, string who, string op, bool accepted)
        {
            string key = who + ":" + op;
            if (!observedOperations.TryGetValue(key, out long version))
                observedOperations[key] = version = Snapshot().Trades.Single(t => t.TradeId == trade).SnapshotVersion;
            StatusObserved(trade, who, op, accepted, version);
        }
        public void StatusObserved(string trade, string who, string op, bool accepted, long? version)
        {
            var packet = Packet(new FrameworkTradeStatusUpdateRequest { TradeId = trade, Accepted = accepted }, op);
            if (version.HasValue) packet.SetSnapshotVersion(version.Value);
            Run(who, op, () => Service.HandleStatusUpdateRequest(packet, Context(who)));
        }
        public void Cancel(string trade, string op) => Run("a", op, () => Service.HandleStatusUpdateRequest(Packet(new FrameworkTradeStatusUpdateRequest { TradeId = trade, Cancelled = true }, op), Context("a")));
        public FrameworkTradeStatusUpdateResponse StatusResponse => FrameworkSerialization.DeserializePayload<FrameworkTradeStatusUpdateResponse>(Sent.First(p => p.MessageType == FrameworkTradeProtocol.StatusUpdateResponseType).PayloadJson);
        public FrameworkTradeStateCollectionSnapshot Snapshot()
        { Sent.Clear(); Service.HandleSnapshotRequest(Context("a")); return FrameworkSerialization.DeserializePayload<FrameworkTradeStateCollectionSnapshot>(Sent.Single().PayloadJson); }
    }
    private sealed class SyntheticDirectory : IServerUserManager
    {
        public event EventHandler<ServerLoginEventArgs> OnLogin { add { } remove { } }
        public bool BOnline = true;
        public bool IsLoggedIn(string connectionId, string uuid) => uuid != "b" || BOnline;
        public string[] GetConnections() => new[] { "connection-a", "connection-b", "connection-c" };
        public bool TryGetConnection(string uuid, out string connectionId) { connectionId = "connection-" + uuid; return uuid != "b" || BOnline; }
        public bool TryGetDisplayName(string uuid, out string name) { name = uuid; return true; }
        public bool TryGetLoggedIn(string uuid, out bool online) { online = uuid != "b" || BOnline; return true; }
        public bool TryGetAcceptingTrades(string uuid, out bool accepting) { accepting = true; return true; }
    }
}
