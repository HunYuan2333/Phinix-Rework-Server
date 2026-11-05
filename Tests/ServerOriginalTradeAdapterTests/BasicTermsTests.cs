using System;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Phinix.LegacyAdapter.Server;
using Phinix.TradeExtension;
using Phinix.TradeExtension.Server;
using Utils.Framework;

internal static partial class Program
{
    private static FrameworkItemPayload Stateful(byte[] preview) => new()
    {
        CodecId = "core.item.scribe-v1", PayloadBytes = new byte[] { 1, 2, 3 },
        Metadata = new() { new() { Key = "trade_item_preview_v1", Value = Convert.ToBase64String(preview) }, new() { Key = "extra", Value = "discard-by-explicit-policy" } }
    };
    private static void BasicTerms()
    {
        var projection = new OriginalTradeBasicItemProjection();
        var source = OriginalTradeItemCodec.ToUnified(OldItem(5, 17, OldItem(2, 3)));
        var stateful = Stateful(source.PayloadBytes);
        var basic = projection.Project(stateful);
        Check(OriginalTradeItemCodec.ToOriginal(basic).SequenceEqual(OldItem(5, 17, OldItem(2, 3))), "Lossy policy changed basic values, nested item or stack");
        Check(basic.Metadata.Count == 0 && string.IsNullOrEmpty(basic.PayloadJson) && stateful.Metadata.Count == 2 && stateful.PayloadBytes.SequenceEqual(new byte[] { 1, 2, 3 }), "Projection mutated source or retained opaque data");
        source.Metadata.Add(new() { Key = "extra", Value = "discard" });
        Check(projection.Project(source).Metadata.Count == 0, "Basic codec metadata not discarded under explicit terms");
        Refused(() => OriginalTradeItemCodec.ToOriginal(source)); // Strict codec still rejects extras.
        var ambiguous = Stateful(source.PayloadBytes); ambiguous.Metadata.Add(ambiguous.Metadata[0]);
        Refused(() => projection.Project(ambiguous));
        Refused(() => projection.Project(new FrameworkItemPayload { CodecId = "unknown" }));
        Refused(() => projection.Project(Stateful(new byte[] { 10, 1, 65 })));
        var invalidEncoding = Stateful(source.PayloadBytes); invalidEncoding.Metadata[0].Value = "%%%";
        Refused(() => projection.Project(invalidEncoding));

        // Silver has useHitPoints=false; real Thing snapshots use the -1 HP sentinel.
        // 800 silver is two original stacks (500 + 300), never a filtered/merged batch.
        foreach (int count in new[] { 500, 300 })
        {
            var silver = new Phinix.Framework.FrameworkVanillaItemData { DefName = "Silver", StackCount = count,
                HitPoints = -1, Quality = Phinix.Framework.FrameworkItemQuality.None };
            var mapped = projection.Project(Stateful(silver.ToByteArray()));
            var roundtrip = Phinix.Framework.FrameworkVanillaItemData.Parser.ParseFrom(mapped.PayloadBytes);
            Check(roundtrip.DefName == "Silver" && roundtrip.StackCount == count && roundtrip.HitPoints == -1,
                "Silver no-hitpoints sentinel was rejected or changed");
        }
        Refused(() => projection.Project(Stateful(new Phinix.Framework.FrameworkVanillaItemData { DefName = "Silver", StackCount = 1,
            HitPoints = -2, Quality = Phinix.Framework.FrameworkItemQuality.None }.ToByteArray())));
        string root = Path.Combine(Path.GetTempPath(), "phinix-basic-terms-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var f = new Fixture(root); string trade = f.Create(TradeNegotiationTerms.BasicItemsCurrentQuote);
            FrameworkTradeOfferUpdateResponse Offer(string op, params FrameworkItemPayload[] items)
            {
                FrameworkTradeOfferUpdateResponse response = null;
                f.Service.HandleOfferUpdateRequest(new FrameworkPacket { MessageId = op, PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeOfferUpdateRequest { TradeId = trade, Items = items.ToList() }) },
                    new ServerFrameworkContext { ConnectionId = "connection-a", SessionId = "session-a", SenderUuid = "a", SendMessage = (_, p) => { if (p.MessageType == FrameworkTradeProtocol.OfferUpdateResponseType) response = FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateResponse>(p.PayloadJson); } });
                return response;
            }
            FrameworkTradeStatusUpdateResponse Status(string user, string op, bool accepted, long? version = null, bool? cancel = null)
            {
                FrameworkTradeStatusUpdateResponse response = null;
                var packet = new FrameworkPacket { MessageId = op, PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeStatusUpdateRequest { TradeId = trade, Accepted = accepted, Cancelled = cancel }) };
                if (version.HasValue) packet.SetSnapshotVersion(version.Value);
                f.Service.HandleStatusUpdateRequest(packet, new ServerFrameworkContext { ConnectionId = "connection-" + user, SessionId = "session-" + user, SenderUuid = user,
                    SendMessage = (_, p) => { if (p.MessageType == FrameworkTradeProtocol.StatusUpdateResponseType) response = FrameworkSerialization.DeserializePayload<FrameworkTradeStatusUpdateResponse>(p.PayloadJson); } });
                return response;
            }
            var response = Offer("basic-offer", stateful, stateful);
            Check(response.Success && response.Items.Count == 2 && response.Items.All(i => i.CodecId == OriginalTradeItemCodec.CodecId && i.Metadata.Count == 0 && OriginalTradeItemCodec.ToOriginal(i).SequenceEqual(OldItem(5, 17, OldItem(2, 3)))), "Real service did not commit complete projected offer");
            string before = f.Snapshot(); f.Reopen(); Check(f.Snapshot() == before, "Restart changed downgraded quote");
            Check(Offer("basic-offer", stateful, stateful).Success && f.Snapshot() == before, "Projected committed result not replayable");
            Check(!Offer("bad-basic-tail", stateful, new FrameworkItemPayload { CodecId = "unknown" }).Success && f.Snapshot() == before, "Invalid tail partially replaced downgraded quote");
            var scope = new ServerFrameworkContext { ConnectionId = "connection-a", SessionId = "session-a", SenderUuid = "a", SendMessage = (_, __) => { } };
            var referenceRequest = new FrameworkPacket { MessageId = "basic-reference-offer", PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeOfferUpdateRequest { TradeId = trade, ItemPacketRefs = new() { "staged-stateful" } }) };
            Check(f.Service.TryCacheItemPacket("staged-stateful", stateful, scope), "Could not stage real reference");
            f.Service.HandleOfferUpdateRequest(referenceRequest, scope);
            Check(f.Service.TryCacheItemPacket("staged-stateful", stateful, scope), "Committed reference was not consumed");
            f.Service.HandleOfferUpdateRequest(referenceRequest, scope); // Compare original fingerprint, not lossy result bytes.
            Check(true, "Same referenced payload should replay after projection");
            f.Reopen();
            var altered = Stateful(source.PayloadBytes); altered.Metadata[1].Value = "different-discarded-state";
            Check(f.Service.TryCacheItemPacket("staged-stateful", altered, scope), "Could not stage conflicting reference");
            try { f.Service.HandleOfferUpdateRequest(referenceRequest, scope); throw new Exception("Projected equivalence hid conflicting source payload"); }
            catch (ServerStorage.OperationConflictException) { checks++; }
            before = f.Snapshot();
            Check(!Status("a", "stale-basic-consent", true, 1).Success && f.Snapshot() == before, "Explicit stale version ignored under basic terms");
            Check(Status("a", "accept-current-a", true).Success, "Current-quote consent still refused under persisted basic terms");
            Check(Offer("basic-reset").Success, "Basic empty offer failed");
            var reset = FrameworkSerialization.DeserializePayload<FrameworkTradeStateCollectionSnapshot>(f.Snapshot()).Trades.Single();
            Check(reset.Participants.All(p => !p.Accepted && p.ItemsOnOffer.Count == 0), "Basic reset did not clear offer and both consent flags");
            Check(Offer("basic-reoffer", stateful, stateful).Success && Status("a", "accept-current-a-again", true).Success, "Basic reoffer/current consent failed");
            f.Reopen(); // Accepted flag AND terms must both survive a real SQLite reopen.
            Check(Status("b", "accept-current-b", true).Success, "Restart lost basic consent terms or first participant acceptance");
            Check(FrameworkSerialization.DeserializePayload<FrameworkTradeStateCollectionSnapshot>(f.Snapshot()).Trades.Count == 0, "Both-current acceptance did not terminalize trade");
            using var c = f.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT value FROM state WHERE id='trade-ledger-v1'";
            string xml = System.Text.Encoding.UTF8.GetString((byte[])cmd.ExecuteScalar());
            Check(xml.Contains("BasicItemsCurrentQuote") && xml.Contains(">Pending</") && !xml.Contains("discard-by-explicit-policy"), "Terminal ledger lost terms/outbox or retained discarded extras");
            f.Reopen(); Check(Status("b", "accept-current-b", true).Success, "Terminal current-quote result replay changed after restart");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
