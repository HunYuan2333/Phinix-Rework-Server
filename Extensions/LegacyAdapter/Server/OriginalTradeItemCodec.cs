using System;
using System.Collections.Generic;
using System.IO;
using Google.Protobuf;
using Utils.Framework;
using Item = Phinix.Framework.FrameworkVanillaItemData;
using Quality = Phinix.Framework.FrameworkItemQuality;

namespace Phinix.LegacyAdapter.Server
{
    // thomotron/Phinix ce82b8f ProtoThing. A syntactic, lossless representation
    // bridge only; this does not attest game Defs, materialization or saved assets.
    public static class OriginalTradeItemCodec
    {
        public const string CodecId = "core.item.vanilla";
        public const int MaximumDepth = 16;
        public const int MaximumBytes = 1024 * 1024;
        public static FrameworkItemPayload ToUnified(byte[] bytes) => new()
        { CodecId = CodecId, PayloadBytes = Read(bytes, false, 0).ToByteArray() };

        public static byte[] ToOriginal(FrameworkItemPayload payload)
        {
            if (payload == null || !string.Equals(payload.CodecId, CodecId, StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(payload.PayloadJson) || payload.Metadata?.Count > 0)
                throw new InvalidDataException("OriginalItemUnrepresentableCodecOrMetadata");
            return Write(Read(payload.PayloadBytes, true, 0));
        }
        private static Item Read(byte[] bytes, bool unified, int depth)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes || depth >= MaximumDepth)
                throw new InvalidDataException("OriginalItemSizeOrDepth");
            using var r = new CodedInputStream(bytes);
            var seen = new HashSet<uint>(); var item = new Item(); int quality = 0; uint tag;
            while ((tag = r.ReadTag()) != 0)
            {
                if (!seen.Add(tag)) throw new InvalidDataException("OriginalItemDuplicateField");
                switch (tag)
                {
                    case 10: item.DefName = r.ReadString(); break;
                    case 16: item.StackCount = r.ReadInt32(); break;
                    case 26: item.StuffDefName = r.ReadString(); break;
                    case 32: quality = r.ReadInt32(); break;
                    case 40: item.HitPoints = r.ReadInt32(); break;
                    case 50: item.InnerItem = Read(r.ReadBytes().ToByteArray(), unified, depth + 1); break;
                    default: throw new InvalidDataException("OriginalItemUnknownField");
                }
            }
            if (string.IsNullOrWhiteSpace(item.DefName) || item.DefName.Length > 256 || item.StuffDefName.Length > 256 ||
                item.StackCount <= 0 || item.HitPoints < -1 || quality < (unified ? 1 : 0) || quality > (unified ? 8 : 7))
                throw new InvalidDataException("OriginalItemInvalidValue");
            // Original Awful=0 ... None=7; Framework Unspecified=0, Awful=1 ... None=8.
            item.Quality = (Quality)(unified ? quality : quality + 1);
            return item;
        }
        private static byte[] Write(Item item)
        {
            using var stream = new MemoryStream();
            using (var w = new CodedOutputStream(stream, true))
            {
                w.WriteTag(1, WireFormat.WireType.LengthDelimited); w.WriteString(item.DefName);
                w.WriteTag(2, WireFormat.WireType.Varint); w.WriteInt32(item.StackCount);
                if (!string.IsNullOrEmpty(item.StuffDefName)) { w.WriteTag(3, WireFormat.WireType.LengthDelimited); w.WriteString(item.StuffDefName); }
                int quality = (int)item.Quality - 1;
                if (quality != 0) { w.WriteTag(4, WireFormat.WireType.Varint); w.WriteInt32(quality); }
                if (item.HitPoints != 0) { w.WriteTag(5, WireFormat.WireType.Varint); w.WriteInt32(item.HitPoints); }
                if (item.InnerItem != null) { w.WriteTag(6, WireFormat.WireType.LengthDelimited); w.WriteBytes(ByteString.CopyFrom(Write(item.InnerItem))); }
                w.Flush();
            }
            return stream.ToArray();
        }
    }
}
