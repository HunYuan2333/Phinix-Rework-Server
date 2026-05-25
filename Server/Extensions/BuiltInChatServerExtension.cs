using System.Collections.Generic;
using Google.Protobuf;
using PhinixServer.Framework;
using Utils.Framework;

namespace PhinixServer.Extensions
{
    [PhinixExtension("builtin.chat")]
    public class BuiltInChatServerExtension : IPhinixExtensionModule, ICapabilityProvider, IServerMessageHandler, IServerCommandHandler
    {
        private IFrameworkChatServerApi chatApi;

        public string ExtensionId => "builtin.chat";

        public int Priority => 1000;

        public void Register(IExtensionBuilder builder)
        {
            chatApi = builder.HostContext.GetRequiredService<IFrameworkChatServerApi>();
            builder.RegisterApi(chatApi);
            builder.AddCapabilityProvider(this);
            builder.AddServerMessageHandler(this);
            builder.AddServerCommandHandler(this);
        }

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
            global::Phinix.Framework.BuiltInChatMessagePayload storedMessage = chatApi.AddMessage(context.SenderUuid, incomingPacket.Message);
            context.BroadcastMessage?.Invoke(chatApi.BuildBroadcastPacket(storedMessage), null);

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
            foreach (global::Phinix.Framework.BuiltInChatMessagePayload historyMessage in chatApi.GetHistory())
            {
                context.SendMessage?.Invoke(context.ConnectionId, chatApi.BuildBroadcastPacket(historyMessage));
            }

            context.SendMessage?.Invoke(context.ConnectionId, chatApi.BuildHistorySyncCompletePacket());

            return new ServerIncomingCommandResult
            {
                Action = MessageHandlingResultAction.Handled
            };
        }
    }
}
