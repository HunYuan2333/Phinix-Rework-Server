using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Utils.Framework;

namespace Phinix.ChatExtension.Server
{
    public sealed class PhinixFrameworkChatBroadcast
    {
        public FrameworkPacket BuildChatMessage(global::Phinix.Framework.BuiltInChatMessagePayload chatMessage)
        {
            return new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Message,
                MessageType = FrameworkChatProtocol.MessageType,
                MessageId = chatMessage.MessageId,
                SenderUuid = chatMessage.SenderUuid,
                TimestampUtcTicks = chatMessage.Timestamp != null ? chatMessage.Timestamp.ToDateTime().ToUniversalTime().Ticks : 0L,
                PayloadBytes = chatMessage.ToByteArray()
            };
        }

        public FrameworkPacket BuildHistorySyncComplete()
        {
            return new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                MessageType = FrameworkChatProtocol.HistorySyncCompleteType,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Event,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                PayloadBytes = new Empty().ToByteArray()
            };
        }
    }
}
