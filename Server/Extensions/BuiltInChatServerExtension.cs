using System.Collections.Generic;
using Google.Protobuf;
using Utils.Framework;

namespace PhinixServer.Extensions
{
    [PhinixExtension("builtin.chat")]
    public class BuiltInChatServerExtension : IPhinixExtension, ICapabilityProvider, IServerMessageHandler, IServerCommandHandler
    {
        public string ExtensionId => "builtin.chat";

        public int Priority => 1000;

        public IEnumerable<string> GetCapabilities()
        {
            yield return FrameworkProtocol.BuiltInChatMessageType;
            yield return FrameworkProtocol.BuiltInChatHistoryRequestType;
            yield return FrameworkProtocol.BuiltInChatHistorySyncCompleteType;
        }

        public bool CanHandleIncomingMessage(FrameworkPacket message)
        {
            return message != null && message.MessageType == FrameworkProtocol.BuiltInChatMessageType;
        }

        public ServerIncomingMessageResult HandleIncomingMessage(FrameworkPacket message, ServerFrameworkContext context)
        {
            global::Phinix.Framework.BuiltInChatMessagePayload incomingPacket = global::Phinix.Framework.BuiltInChatMessagePayload.Parser.ParseFrom(message.PayloadBytes ?? System.Array.Empty<byte>());
            global::Phinix.Framework.BuiltInChatMessagePayload storedMessage = Server.FrameworkChat.AddMessage(context.SenderUuid, incomingPacket.Message);
            context.BroadcastMessage?.Invoke(Server.FrameworkChat.BuildBroadcastPacket(storedMessage), null);

            return new ServerIncomingMessageResult
            {
                Action = MessageHandlingResultAction.Handled
            };
        }

        public bool CanHandleIncomingCommand(FrameworkPacket command)
        {
            return command != null && command.MessageType == FrameworkProtocol.BuiltInChatHistoryRequestType;
        }

        public ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ServerFrameworkContext context)
        {
            foreach (global::Phinix.Framework.BuiltInChatMessagePayload historyMessage in Server.FrameworkChat.GetHistory())
            {
                context.SendMessage?.Invoke(context.ConnectionId, Server.FrameworkChat.BuildBroadcastPacket(historyMessage));
            }

            context.SendMessage?.Invoke(context.ConnectionId, Server.FrameworkChat.BuildHistorySyncCompletePacket());

            return new ServerIncomingCommandResult
            {
                Action = MessageHandlingResultAction.Handled
            };
        }
    }
}
