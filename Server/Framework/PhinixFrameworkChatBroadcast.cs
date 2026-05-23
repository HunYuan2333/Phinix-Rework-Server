using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Utils.Framework;

namespace PhinixServer.Framework
{
    public sealed class PhinixFrameworkChatBroadcast
    {
        public FrameworkPacket BuildChatMessage(global::Phinix.Framework.BuiltInChatMessagePayload chatMessage)
        {
            return new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Message,
                MessageType = FrameworkProtocol.BuiltInChatMessageType,
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
                MessageType = FrameworkProtocol.BuiltInChatHistorySyncCompleteType,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Event,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                PayloadBytes = new Empty().ToByteArray()
            };
        }
    }
}
