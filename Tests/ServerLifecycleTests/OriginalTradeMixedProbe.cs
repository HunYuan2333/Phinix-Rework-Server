using System;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Phinix.TradeExtension;
using Utils.Framework;

internal static partial class P3NetworkProbe
{
    internal static void CheckProbeDisconnectGate()
    {
        using var server = new Connections.NetServer(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0), 2);
        server.Start();
        using var peer = new Peer(server.LocalPort, "synthetic-disconnect-gate");
        using var started = new System.Threading.ManualResetEventSlim();
        System.Threading.Tasks.Task disconnect;
        lock (peer.PeerGate)
        {
            disconnect = System.Threading.Tasks.Task.Run(() => { started.Set(); peer.Disconnect(); });
            Check(started.Wait(2000), "Probe disconnect barrier did not start");
            Check(!disconnect.Wait(100), "Probe disconnect bypassed the pinned peer mutation gate");
        }
        Check(disconnect.Wait(2000), "Probe disconnect did not resume after peer gate release");
        Console.WriteLine("PROBE DISCONNECT GATE RESULT passed; deterministic-held-gate; no vendor mutation");
    }

    public static void RunTradeMixed(int port, string directory)
    {
        int checks = 0;
        void Assert(bool ok, string why) { Check(ok, why); checks++; }
        using var modern = new Peer(port, "synthetic-p5-trade-mixed-modern");
        modern.Until(() => modern.Ready);
        File.WriteAllText(Path.Combine(directory, "trade-modern-ready"), modern.Principal);
        modern.Until(() => File.Exists(Path.Combine(directory, "trade-original-ready")));
        string original = File.ReadAllText(Path.Combine(directory, "trade-original-ready"));
        modern.Until(() => modern.Packets.Any(p => p.MessageType == FrameworkTradeProtocol.CreateResponseType && FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(p.PayloadJson).Success));
        string id = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(modern.Packets.First(p => p.MessageType == FrameworkTradeProtocol.CreateResponseType).PayloadJson).TradeId;
        FrameworkTradeStateSnapshot Snapshot(string tradeId) => modern.Packets.Where(p => p.MessageType == FrameworkTradeProtocol.SnapshotType)
            .SelectMany(p => FrameworkSerialization.DeserializePayload<FrameworkTradeStateCollectionSnapshot>(p.PayloadJson).Trades).LastOrDefault(t => t.TradeId == tradeId);
        modern.Until(() => Snapshot(id)?.Participants.Single(p => p.Uuid == original).ItemsOnOffer.Count == 1);
        Assert(modern.Packets.Any(p => p.MessageType == FrameworkTradeProtocol.SnapshotType && p.TryGetMetadataValue("trade_basic_terms_v1", out var marked) && marked.Split(',').Contains(id)), "Mixed trade missing explicit downgrade notice metadata");
        var stateful = new FrameworkItemPayload
        {
            CodecId = "core.item.scribe-v1", PayloadBytes = new byte[] { 9, 8, 7 },
            Metadata = new() { new() { Key = "trade_item_preview_v1", Value = Convert.ToBase64String(new global::Phinix.Framework.FrameworkVanillaItemData
                { DefName = "Silver", StackCount = 500, HitPoints = -1, Quality = global::Phinix.Framework.FrameworkItemQuality.None }.ToByteArray()) }, new() { Key = "synthetic-extra", Value = "discard" } }
        };
        var secondStack = FrameworkSerialization.DeserializePayload<FrameworkItemPayload>(FrameworkSerialization.SerializePayload(stateful));
        secondStack.Metadata.Single(e => e.Key == "trade_item_preview_v1").Value = Convert.ToBase64String(new global::Phinix.Framework.FrameworkVanillaItemData
            { DefName = "Silver", StackCount = 300, HitPoints = -1, Quality = global::Phinix.Framework.FrameworkItemQuality.None }.ToByteArray());
        var stagedSecond = FrameworkSerialization.BuildItemPacket(secondStack, "", ""); stagedSecond.MessageType = FrameworkTradeProtocol.Capability;
        var stagedItem = FrameworkSerialization.BuildItemPacket(stateful, "", "");
        stagedItem.MessageType = FrameworkTradeProtocol.Capability;
        modern.Send(stagedItem); modern.Send(stagedSecond);
        modern.Command(FrameworkTradeProtocol.OfferUpdateRequestType, new FrameworkTradeOfferUpdateRequest { TradeId = id, ItemPacketRefs = new() { stagedItem.MessageId, stagedSecond.MessageId } }, "mixed-stateful-offer");
        modern.Until(() => modern.Find(FrameworkTradeProtocol.OfferUpdateResponseType, "mixed-stateful-offer") != null);
        var offer = FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateResponse>(modern.Find(FrameworkTradeProtocol.OfferUpdateResponseType, "mixed-stateful-offer").PayloadJson);
        Assert(offer.Success && offer.Items.Count == 2 && offer.Items.All(i => i.CodecId == "core.item.vanilla" && i.Metadata.Count == 0), "Mixed Scribe offer not projected before authoritative response");
        var silver = offer.Items.Select(i => global::Phinix.Framework.FrameworkVanillaItemData.Parser.ParseFrom(i.PayloadBytes)).ToArray();
        Assert(silver.Select(i => i.StackCount).SequenceEqual(new[] { 500, 300 }) && silver.All(i => i.DefName == "Silver" && i.HitPoints == -1), "800 silver lost stack boundaries or no-HP sentinel");
        modern.Until(() => Snapshot(id).Participants.Single(p => p.Uuid == original).Accepted);
        var accept = new FrameworkPacket { MessageId = "mixed-modern-accept", Kind = FrameworkProtocol.KindCommand, CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request,
            MessageType = FrameworkTradeProtocol.StatusUpdateRequestType, PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeStatusUpdateRequest { TradeId = id, Accepted = true }) };
        accept.SetSnapshotVersion(Snapshot(id).SnapshotVersion); modern.Send(accept);
        modern.Until(() => modern.Packets.Any(p => p.MessageType == FrameworkTradeProtocol.CompletedEventType));
        var completed = FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(modern.Packets.Single(p => p.MessageType == FrameworkTradeProtocol.CompletedEventType).PayloadJson);
        var received = global::Phinix.Framework.FrameworkVanillaItemData.Parser.ParseFrom(completed.Items.Single().PayloadBytes);
        Assert(completed.TradeId == id && completed.OtherPartyUuid == original && received.DefName == "Steel" && received.StackCount == 13, "Mixed completion recipient/items wrong");
        modern.Until(() => File.Exists(Path.Combine(directory, "trade-mixed-first-done")));
        modern.Command(FrameworkTradeProtocol.CreateRequestType, new FrameworkTradeCreateRequest { OtherPartyUuid = original }, "mixed-modern-create");
        modern.Until(() => modern.Find(FrameworkTradeProtocol.CreateResponseType, "mixed-modern-create") != null);
        var created = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(modern.Find(FrameworkTradeProtocol.CreateResponseType, "mixed-modern-create").PayloadJson);
        Assert(created.Success, "Modern cannot initiate toward confirmed original");
        modern.Until(() => modern.Packets.Any(p => p.MessageType == FrameworkTradeProtocol.CancelledEventType));
        var cancelled = FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(modern.Packets.Single(p => p.MessageType == FrameworkTradeProtocol.CancelledEventType).PayloadJson);
        Assert(cancelled.TradeId == created.TradeId && cancelled.Items.Count == 0, "Mixed zero-offer cancellation failed");

        // Same enabled adapter must leave actual Framework-to-Framework trades strict.
        using var other = new Peer(port, "synthetic-p5-trade-mixed-other-modern"); other.Until(() => other.Ready);
        var strict = Create(modern, other, "mixed-strict-create"); Assert(strict.Success, "Modern pair setup failed");
        modern.Command(FrameworkTradeProtocol.OfferUpdateRequestType, new FrameworkTradeOfferUpdateRequest { TradeId = strict.TradeId, Items = new() { stateful } }, "mixed-strict-offer");
        Wait(modern, other, () => modern.Find(FrameworkTradeProtocol.OfferUpdateResponseType, "mixed-strict-offer") != null);
        var preserved = FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateResponse>(modern.Find(FrameworkTradeProtocol.OfferUpdateResponseType, "mixed-strict-offer").PayloadJson).Items.Single();
        Assert(FrameworkSerialization.SerializePayload(preserved) == FrameworkSerialization.SerializePayload(stateful), "Enabled adapter degraded a modern pair");
        modern.Command(FrameworkTradeProtocol.StatusUpdateRequestType, new FrameworkTradeStatusUpdateRequest { TradeId = strict.TradeId, Accepted = true }, "mixed-strict-unbound");
        Wait(modern, other, () => modern.Find(FrameworkTradeProtocol.StatusUpdateResponseType, "mixed-strict-unbound") != null);
        Assert(!FrameworkSerialization.DeserializePayload<FrameworkTradeStatusUpdateResponse>(modern.Find(FrameworkTradeProtocol.StatusUpdateResponseType, "mixed-strict-unbound").PayloadJson).Success, "Modern pair lost observed-quote consent");
        File.WriteAllText(Path.Combine(directory, "trade-modern-done"), "done");
        Console.WriteLine("ORIGINAL TRADE MIXED RESULT passed; checks=" + checks + "; realUDP=true; modernPairStrict=true; gameAssets=false");
    }
}
