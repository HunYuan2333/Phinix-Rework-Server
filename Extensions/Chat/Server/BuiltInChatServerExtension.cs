using System;
using System.Collections.Generic;
using Google.Protobuf;
using Utils.Framework;

namespace Phinix.ChatExtension.Server
{
    [PhinixExtension("builtin.chat")]
    public class BuiltInChatServerExtension : IPhinixExtensionModule, IActivatablePhinixExtensionModule, ICapabilityProvider, IServerDefaultMessageHandler, IServerDefaultCommandHandler
    {
        private const string HistoryStorageName = "chat-history.bin";
        private const string HistoryCapacityOption = "builtin.chat.history-capacity";

        private IFrameworkChatServerApi chatApi;
        private EventHandler<Utils.LogEventArgs> logForwarder;

        public string ExtensionId => "builtin.chat";

        public int Priority => 1000;

        public void Register(IExtensionBuilder builder)
        {
            var configProvider = builder.HostContext.TryGetService<IExtensionConfigProvider>(out var provider)
                ? provider
                : null;
            ChatServerConfig config = configProvider != null
                ? configProvider.GetConfig<ChatServerConfig>()
                : new ChatServerConfig();
            config.Validate();

            chatApi = chatApi ?? new PhinixFrameworkChatService(
                config.HistoryLength,
                config.NoticeHistoryLength,
                config.NoticeRetentionWindowHours,
                builder.HostContext.GetRequiredService<UserManagement.IServerUserManager>());
            builder.RegisterApi(chatApi);
            builder.HostContext.RegisterPersistent(ExtensionId, HistoryStorageName, chatApi);
            builder.AddCapabilityProvider(this);
            builder.AddServerDefaultMessageHandler(this);
            builder.AddServerDefaultCommandHandler(this);
            builder.AddConsoleCommandProvider(new NoticeConsoleCommand(chatApi,
                () => builder.HostContext.TryGetService<IFrameworkServerBroadcaster>(out var b) ? b : null));
        }

        public void Activate(ExtensionHostContext hostContext)
        {
            if (chatApi == null || hostContext == null)
            {
                return;
            }

            if (logForwarder == null)
            {
                logForwarder = (_, logEvent) => hostContext.Log?.Invoke(logEvent.Message, logEvent.LogLevel);
            }

            chatApi.OnLogEntry += logForwarder;
        }

        public void Shutdown(ExtensionHostContext hostContext)
        {
            if (chatApi != null && logForwarder != null)
            {
                chatApi.OnLogEntry -= logForwarder;
            }
        }

        public IEnumerable<string> GetCapabilities()
        {
            yield return FrameworkChatProtocol.MessageType;
            yield return FrameworkChatProtocol.HistoryRequestType;
            yield return FrameworkChatProtocol.HistorySyncCompleteType;
        }

        public bool CanHandleIncomingMessage(FrameworkPacket message)
        {
            return message != null && message.MessageType == FrameworkChatProtocol.MessageType;
        }

        public ServerIncomingMessageResult HandleIncomingMessage(FrameworkPacket message, ServerFrameworkContext context)
        {
            global::Phinix.Framework.BuiltInChatMessagePayload incomingPacket;
            try
            {
                incomingPacket = global::Phinix.Framework.BuiltInChatMessagePayload.Parser.ParseFrom(message.PayloadBytes ?? Array.Empty<byte>());
            }
            catch (Exception)
            {
                return new ServerIncomingMessageResult { Action = MessageHandlingResultAction.Handle };
            }

            global::Phinix.Framework.BuiltInChatMessagePayload storedMessage = chatApi.AddMessage(
                context.SenderUuid,
                incomingPacket.Message,
                incomingPacket.MentionedUuids,
                incomingPacket.ReplyToMessageId,
                incomingPacket.ReplyToSnippet);
            context.BroadcastMessage?.Invoke(chatApi.BuildBroadcastPacket(storedMessage), null);

            return new ServerIncomingMessageResult
            {
                Action = MessageHandlingResultAction.Handle
            };
        }

        public bool CanHandleIncomingCommand(FrameworkPacket command)
        {
            return command != null && command.MessageType == FrameworkChatProtocol.HistoryRequestType;
        }

        public ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ServerFrameworkContext context)
        {
            foreach (global::Phinix.Framework.BuiltInChatMessagePayload historyMessage in chatApi.GetHistory())
            {
                context.SendMessage?.Invoke(context.ConnectionId, chatApi.BuildBroadcastPacket(historyMessage));
            }

            DateTime cutoff = DateTime.UtcNow.AddHours(-chatApi.NoticeRetentionWindowHours);
            foreach (global::Phinix.Framework.BuiltInChatMessagePayload notice in chatApi.GetNoticesSince(cutoff))
            {
                FrameworkPacket packet = chatApi.BuildBroadcastPacket(notice);
                context.SendMessage?.Invoke(context.ConnectionId, packet);
            }

            context.SendMessage?.Invoke(context.ConnectionId, chatApi.BuildHistorySyncCompletePacket());

            return new ServerIncomingCommandResult
            {
                Action = MessageHandlingResultAction.Handle
            };
        }
    }
}
