using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Phinix.TradeExtension;
using Utils.Framework;

namespace Phinix.LegacyAdapter.Server;

// Pinned thomotron/Phinix ce82b8f fields/directions. No business or refund authority.
public static class OriginalTradeWireCodec
{
    public sealed record Request(string Type, string SessionId, string PrincipalId, string TargetId, bool Accepted, bool Cancelled)
    { public override string ToString() => "OriginalTradeRequest(redacted)"; }
    public static Request Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > OriginalTradeItemCodec.MaximumBytes) throw new InvalidDataException("OriginalTradingPacketSize");
        var any = Any.Parser.ParseFrom(bytes);
        string type = any.TypeUrl;
        if (type != "Phinix/Trading.CreateTradePacket" && type != "Phinix/Trading.UpdateTradeStatusPacket") throw new InvalidDataException("OriginalTradingRequestType");
        using var r = new CodedInputStream(any.Value.ToByteArray());
        var seen = new HashSet<uint>(); string session = "", principal = "", target = ""; bool accepted = false, cancelled = false; uint tag;
        while ((tag = r.ReadTag()) != 0)
        {
            if (!seen.Add(tag)) throw new InvalidDataException("OriginalTradingDuplicateField");
            switch (tag)
            {
                case 10: session = r.ReadString(); break;
                case 18: principal = r.ReadString(); break;
                case 26: target = r.ReadString(); break;
                case 32 when type.EndsWith("UpdateTradeStatusPacket", StringComparison.Ordinal): accepted = r.ReadBool(); break;
                case 48 when type.EndsWith("UpdateTradeStatusPacket", StringComparison.Ordinal): cancelled = r.ReadBool(); break;
                default: throw new InvalidDataException("OriginalTradingUnknownOrWrongDirectionField");
            }
        }
        if (new[] { session, principal, target }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 128 || s.Contains('\0')))
            throw new InvalidDataException("OriginalTradingIdentity");
        return new Request(type, session, principal, target, accepted, cancelled);
    }
    public static string Type(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > OriginalTradeItemCodec.MaximumBytes) throw new InvalidDataException("OriginalTradingPacketSize");
        return Any.Parser.ParseFrom(bytes).TypeUrl;
    }
    private static byte[] Body(Action<CodedOutputStream> write)
    {
        using var stream = new MemoryStream();
        using (var w = new CodedOutputStream(stream, true)) { write(w); w.Flush(); }
        return stream.ToArray();
    }
    private static byte[] Pack(string type, Action<CodedOutputStream> write)
    {
        var bytes = new Any { TypeUrl = "Phinix/Trading." + type, Value = ByteString.CopyFrom(Body(write)) }.ToByteArray();
        if (bytes.Length > OriginalTradeItemCodec.MaximumBytes) throw new InvalidDataException("OriginalTradingOutputSize");
        return bytes;
    }
    private static void Text(CodedOutputStream w, int field, string text)
    { if (!string.IsNullOrEmpty(text)) { w.WriteTag(field, WireFormat.WireType.LengthDelimited); w.WriteString(text); } }
    private static void Flag(CodedOutputStream w, int field, bool flag)
    { if (flag) { w.WriteTag(field, WireFormat.WireType.Varint); w.WriteBool(true); } }
    private static void Items(CodedOutputStream w, int field, IEnumerable<FrameworkItemPayload> items)
    { foreach (var item in items ?? Array.Empty<FrameworkItemPayload>()) Bytes(w, field, OriginalTradeItemCodec.ToOriginal(item)); }
    private static void Bytes(CodedOutputStream w, int field, byte[] bytes)
    { w.WriteTag(field, WireFormat.WireType.LengthDelimited); w.WriteBytes(ByteString.CopyFrom(bytes)); }
    private static (FrameworkTradeParticipantSnapshot Own, FrameworkTradeParticipantSnapshot Other) Parties(FrameworkTradeStateSnapshot trade, string principal)
    {
        if (trade?.Participants?.Count != 2) throw new InvalidDataException("OriginalTradingParticipants");
        var own = trade.Participants.SingleOrDefault(p => p.Uuid == principal);
        var other = trade.Participants.SingleOrDefault(p => p.Uuid != principal);
        if (own == null || other == null) throw new InvalidDataException("OriginalTradingRecipient");
        return (own, other);
    }
    private static byte[] TradeBody(FrameworkTradeStateSnapshot trade, string principal)
    {
        var (own, other) = Parties(trade, principal);
        return Body(w => { Text(w, 1, trade.TradeId); Text(w, 2, other.Uuid); Items(w, 3, own.ItemsOnOffer); Items(w, 4, other.ItemsOnOffer); Flag(w, 5, own.Accepted); Flag(w, 6, other.Accepted); });
    }
    public static byte[][] Snapshot(FrameworkTradeStateCollectionSnapshot snapshot, string principal, bool full)
    {
        // Validate/materialize ALL output before the first transport send.
        if (full) return new[] { Pack("SyncTradesPacket", w => { foreach (var trade in snapshot.Trades) Bytes(w, 1, TradeBody(trade, principal)); }) };
        var packets = new List<byte[]>();
        foreach (var trade in snapshot.Trades)
        {
            var (own, other) = Parties(trade, principal);
            packets.Add(Pack("UpdateTradeItemsPacket", w => { Text(w, 3, trade.TradeId); Items(w, 4, own.ItemsOnOffer); Items(w, 5, other.ItemsOnOffer); }));
            packets.Add(Pack("UpdateTradeStatusPacket", w => { Text(w, 3, trade.TradeId); Flag(w, 4, own.Accepted); Flag(w, 5, other.Accepted); }));
        }
        return packets.ToArray();
    }
    public static byte[] Create(FrameworkTradeCreateResponse result) => Pack("CreateTradeResponsePacket", w =>
    {
        Flag(w, 1, result.Success); Text(w, 2, result.TradeId); Text(w, 3, result.OtherPartyUuid);
        if (!result.Success)
        {
            int failure = OriginalTradeOfferCodec.Failure(result.FailureReason);
            if (failure != 0) { w.WriteTag(4, WireFormat.WireType.Varint); w.WriteInt32(failure); }
            Text(w, 5, result.FailureMessage);
        }
    });
    public static byte[] Complete(FrameworkTradeCompletionEvent result) => Pack("CompleteTradePacket", w =>
    { Text(w, 1, result.TradeId); Text(w, 2, result.OtherPartyUuid); Flag(w, 3, !result.Cancelled); Items(w, 4, result.Items); });
}
