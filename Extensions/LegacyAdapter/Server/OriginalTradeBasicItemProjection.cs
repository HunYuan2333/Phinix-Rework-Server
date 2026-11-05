using System;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Phinix.TradeExtension;
using Phinix.TradeExtension.Server;
using PhinixClient.Trade;
using Utils.Framework;

namespace Phinix.LegacyAdapter.Server;

// Explicitly lossy, opt-in projection. The strict codec remains lossless.
// Preserve every root/nested item and stack; discard only state absent from ProtoThing.
public sealed class OriginalTradeBasicItemProjection : ITradeBasicItemProjection
{
    public FrameworkItemPayload Project(FrameworkItemPayload item)
    {
        if (item == null) throw new InvalidDataException("OriginalBasicItemMissing");
        byte[] preview;
        if (item.CodecId == OriginalTradeItemCodec.CodecId)
            preview = item.PayloadBytes;
        else if (item.CodecId == StatefulTradeItemProtocol.ScribeCodecId)
        {
            var entries = item.Metadata?.Where(e => e?.Key == StatefulTradeItemProtocol.PreviewMetadataKey).ToArray();
            if (entries?.Length != 1 || string.IsNullOrEmpty(entries[0].Value) ||
                entries[0].Value.Length > StatefulTradeItemProtocol.MaxPreviewMetadataChars ||
                item.PayloadBytes == null || item.PayloadBytes.Length == 0 ||
                item.PayloadBytes.Length > StatefulTradeItemProtocol.MaxStatePayloadBytes)
                throw new InvalidDataException("OriginalBasicPreviewMissingOrAmbiguous");
            try { preview = Convert.FromBase64String(entries[0].Value); }
            catch (FormatException e) { throw new InvalidDataException("OriginalBasicPreviewEncoding", e); }
        }
        else throw new InvalidDataException("OriginalBasicPreviewCodecUnavailable");

        try
        {
            var basic = new FrameworkItemPayload { CodecId = OriginalTradeItemCodec.CodecId, PayloadBytes = preview };
            // Validate every field/depth/value, then reconstruct only the supported fields.
            // Neither opaque Scribe state nor arbitrary metadata reaches the old recipient.
            return OriginalTradeItemCodec.ToUnified(OriginalTradeItemCodec.ToOriginal(basic));
        }
        catch (InvalidProtocolBufferException e) { throw new InvalidDataException("OriginalBasicPreviewMalformed", e); }
    }
}
