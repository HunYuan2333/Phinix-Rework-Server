using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Phinix.TradeExtension;
using Utils.Framework;

namespace Phinix.LegacyAdapter.Server
{
    public static class OriginalTradeOfferCodec
    {
        public sealed record Request(string SessionId, string PrincipalId, string TradeId, string Token, IReadOnlyList<byte[]> RawItems)
        { public override string ToString() => "OriginalTradeOfferRequest(redacted)"; }
        public static Request Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > OriginalTradeItemCodec.MaximumBytes)
                throw new InvalidDataException("OriginalOfferPacketSize");
            var any = Any.Parser.ParseFrom(bytes);
            if (any.TypeUrl != "Phinix/Trading.UpdateTradeItemsPacket") throw new InvalidDataException("OriginalOfferPacketType");
            using var r = new CodedInputStream(any.Value.ToByteArray());
            var seen = new HashSet<uint>(); var items = new List<byte[]>(); string session = "", user = "", trade = "", token = ""; uint tag;
            while ((tag = r.ReadTag()) != 0)
            {
                if (tag != 34 && !seen.Add(tag)) throw new InvalidDataException("OriginalOfferDuplicateField");
                switch (tag)
                {
                    case 10: session = r.ReadString(); break;
                    case 18: user = r.ReadString(); break;
                    case 26: trade = r.ReadString(); break;
                    case 34:
                        if (items.Count >= 200) throw new InvalidDataException("OriginalOfferBatchLimit");
                        items.Add(r.ReadBytes().ToByteArray()); break;
                    case 50: token = r.ReadString(); break;
                    // Field 5 is server-to-client OtherPartyItems, never client authority.
                    default: throw new InvalidDataException("OriginalOfferUnknownOrWrongDirectionField");
                }
            }
            if (new[] { session, user, trade }.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256) || token.Length > 256)
                throw new InvalidDataException("OriginalOfferInvalidIdentity");
            return new Request(session, user, trade, token, items.AsReadOnly());
        }
        public static string OperationId(Request request)
        {
            if (string.IsNullOrWhiteSpace(request.Token)) throw new InvalidDataException("OriginalOfferStableTokenRequired");
            // No session/credential material in the durable operation ID. Preserve the same
            // operation across reconnects; length prefixes disambiguate opaque tokens.
            string text = "original-offer-v1" + string.Concat(new[] { request.PrincipalId, request.TradeId, request.Token }.Select(v => v.Length + ":" + v));
            return "original.offer." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
        public static FrameworkPacket Translate(Request request, bool allowTokenless = false)
        {
            string operation = allowTokenless && string.IsNullOrWhiteSpace(request.Token) ? "original.offer.unkeyed." + Guid.NewGuid().ToString("N") : OperationId(request);
            // Validate the entire batch before invoking business code or consuming any refs.
            var items = request.RawItems.Select(OriginalTradeItemCodec.ToUnified).ToList();
            // Validate even the reply size/representation before changing authority state.
            Response(request, new FrameworkTradeOfferUpdateResponse { Success = true, Items = items });
            return new FrameworkPacket
            {
                MessageId = operation, Flow = Phinix.Framework.FrameworkFlow.Command,
                CommandKind = Phinix.Framework.FrameworkCommandKind.Request,
                MessageType = FrameworkTradeProtocol.OfferUpdateRequestType,
                PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeOfferUpdateRequest { TradeId = request.TradeId, Items = items })
            };
        }
        internal static int Failure(FrameworkTradeFailureReason failure) => failure switch
        {
            FrameworkTradeFailureReason.SessionInvalid => 0,
            FrameworkTradeFailureReason.LoginInvalid or FrameworkTradeFailureReason.NotTradeParticipant => 1,
            FrameworkTradeFailureReason.OtherPartyOffline => 3,
            FrameworkTradeFailureReason.OtherPartyDoesNotExist => 4,
            FrameworkTradeFailureReason.AlreadyTrading => 5,
            FrameworkTradeFailureReason.TradeDoesNotExist => 6,
            FrameworkTradeFailureReason.NotAcceptingTrades => 7,
            _ => 2
        };
        public static byte[] Response(Request request, FrameworkTradeOfferUpdateResponse result)
        {
            // Do not emit half a batch if one result item cannot be represented.
            var bodies = result.Success ? result.Items.Select(OriginalTradeItemCodec.ToOriginal).ToArray() : Array.Empty<byte[]>();
            using var stream = new MemoryStream();
            using (var w = new CodedOutputStream(stream, true))
            {
                void Text(int field, string value) { if (!string.IsNullOrEmpty(value)) { w.WriteTag(field, WireFormat.WireType.LengthDelimited); w.WriteString(value); } }
                Text(1, request.TradeId); Text(2, request.Token);
                if (result.Success) { w.WriteTag(3, WireFormat.WireType.Varint); w.WriteBool(true); }
                else
                {
                    int failure = Failure(result.FailureReason);
                    if (failure != 0) { w.WriteTag(4, WireFormat.WireType.Varint); w.WriteInt32(failure); }
                    Text(5, result.FailureMessage);
                }
                foreach (var body in bodies) { w.WriteTag(6, WireFormat.WireType.LengthDelimited); w.WriteBytes(ByteString.CopyFrom(body)); }
                w.Flush();
            }
            var bytes = new Any { TypeUrl = "Phinix/Trading.UpdateTradeItemsResponsePacket", Value = ByteString.CopyFrom(stream.ToArray()) }.ToByteArray();
            if (bytes.Length > OriginalTradeItemCodec.MaximumBytes) throw new InvalidDataException("OriginalOfferResponseSize");
            return bytes;
        }
    }
}
