using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Message = Phinix.Framework.BuiltInChatMessagePayload;

namespace Phinix.LegacyAdapter.Server
{
    // thomotron/Phinix ce82b8f: Common/Chat/Packets/*.proto. Never borrow a
    // generated modern DTO's field layout or infer success from a send result.
    public static class OriginalChatCodec
    {
        public const string Module = "Chat";
        public const int MaximumPacketBytes = 256 * 1024;
        public sealed record Request(string SessionId, string PrincipalId, string OriginalId, string Text)
        { public override string ToString() => "Original chat request (session and content omitted)"; }

        public static Request DecodeRequest(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > 32 * 1024) throw new InvalidDataException("LegacyChatPacketSize");
            var envelope = Any.Parser.ParseFrom(data);
            if (envelope.TypeUrl != "Phinix/Chat.ChatMessagePacket") throw new InvalidDataException("LegacyChatPacketType");
            using var input = new CodedInputStream(envelope.Value.ToByteArray());
            string session = "", principal = "", id = "", text = "";
            var seen = new HashSet<uint>(); uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (!seen.Add(tag)) throw new InvalidDataException("LegacyChatDuplicateField");
                switch (tag)
                {
                    case 10: session = input.ReadString(); break;
                    case 18: principal = input.ReadString(); break;
                    case 26: text = input.ReadString(); break;
                    case 34: Timestamp.Parser.ParseFrom(input.ReadBytes()); break; // Client time is never authoritative.
                    case 42: id = input.ReadString(); break;
                    default: throw new InvalidDataException("LegacyChatUnrepresentableField");
                }
            }
            if (new[] { session, principal, id }.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256) ||
                string.IsNullOrEmpty(text) || text.Length > 8192) throw new InvalidDataException("LegacyChatInvalidRequest");
            return new Request(session, principal, id, text);
        }

        public static bool CanExpress(Message message) => message != null && message.Timestamp != null &&
            !message.IsNotice && message.MentionedUuids.Count == 0 && string.IsNullOrEmpty(message.ReplyToMessageId) &&
            string.IsNullOrEmpty(message.ReplyToSnippet) && !string.IsNullOrEmpty(message.MessageId) && !string.IsNullOrEmpty(message.SenderUuid);

        private static byte[] Encode(Action<CodedOutputStream> write)
        {
            using var stream = new MemoryStream();
            using (var output = new CodedOutputStream(stream, true)) { write(output); output.Flush(); }
            return stream.ToArray();
        }
        private static void Text(CodedOutputStream output, int field, string text)
        { if (!string.IsNullOrEmpty(text)) { output.WriteTag(field, WireFormat.WireType.LengthDelimited); output.WriteString(text); } }
        private static byte[] Envelope(string type, byte[] bytes)
        {
            var encoded = new Any { TypeUrl = "Phinix/Chat." + type, Value = ByteString.CopyFrom(bytes) }.ToByteArray();
            if (encoded.Length > MaximumPacketBytes) throw new InvalidDataException("LegacyChatPacketSize");
            return encoded;
        }
        private static byte[] MessageBody(Message message)
        {
            if (!CanExpress(message)) throw new InvalidDataException("LegacyChatUnrepresentableMessage");
            return Encode(output => {
                Text(output, 2, message.SenderUuid); Text(output, 3, message.Message);
                output.WriteTag(4, WireFormat.WireType.LengthDelimited); output.WriteMessage(message.Timestamp);
                Text(output, 5, message.MessageId);
            });
        }
        public static byte[] Broadcast(Message message) => Envelope("ChatMessagePacket", MessageBody(message));
        public static byte[] Response(string originalId, Message stored = null) => Envelope("ChatMessageResponsePacket", Encode(output => {
            if (stored != null)
            {
                if (!CanExpress(stored)) throw new InvalidDataException("LegacyChatUnrepresentableMessage");
                output.WriteTag(1, WireFormat.WireType.Varint); output.WriteBool(true);
            }
            Text(output, 2, originalId); Text(output, 3, stored?.MessageId); Text(output, 4, stored?.Message);
        }));
        public static byte[] History(IEnumerable<Message> messages)
        {
            // Translate all before producing a packet: never omit unrepresentable rows.
            var bodies = messages.Select(MessageBody).ToArray();
            if (bodies.Sum(bytes => (long)bytes.Length + 8) > MaximumPacketBytes - 128) throw new InvalidDataException("LegacyChatHistorySize");
            return Envelope("ChatHistoryPacket", Encode(output => {
                foreach (var bytes in bodies) { output.WriteTag(1, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(bytes)); }
            }));
        }
    }
}
