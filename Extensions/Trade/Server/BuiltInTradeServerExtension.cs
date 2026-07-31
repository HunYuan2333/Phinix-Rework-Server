using System;
using System.Collections.Generic;
using Utils;
using Utils.Framework;

namespace Phinix.TradeExtension.Server
{
    [PhinixExtension(FrameworkTradeProtocol.Capability)]
    public sealed class BuiltInTradeServerExtension : IPhinixExtensionModule, IActivatablePhinixExtensionModule, ICapabilityProvider, IServerDefaultCommandHandler, IServerDefaultItemHandler
    {
        private const string TradeStateStorageName = "trade-state.bin";

        private IFrameworkTradeServerApi tradeApi;
        private UserManagement.IServerUserManager userManager;
        private EventHandler<Utils.LogEventArgs> logForwarder;
        private EventHandler<UserManagement.ServerLoginEventArgs> loginForwarder;
        private IFrameworkServerPacketDispatcher packetDispatcher;

        public string ExtensionId => FrameworkTradeProtocol.Capability;

        public int Priority => 1100;

        public void Register(IExtensionBuilder builder)
        {
            userManager = builder.HostContext.GetRequiredService<UserManagement.IServerUserManager>();
            packetDispatcher = builder.HostContext.GetRequiredService<IFrameworkServerPacketDispatcher>();
            tradeApi = tradeApi ?? new PhinixFrameworkTradeServerService(userManager);
            builder.RegisterApi(tradeApi);
            builder.HostContext.RegisterPersistent(ExtensionId, TradeStateStorageName, tradeApi);
            builder.AddCapabilityProvider(this);
            builder.AddServerDefaultCommandHandler(this);
            builder.AddServerDefaultItemHandler(this);
        }

        public void Activate(ExtensionHostContext hostContext)
        {
            if (tradeApi == null || hostContext == null)
            {
                return;
            }

            if (logForwarder == null)
            {
                logForwarder = (_, logEvent) => hostContext.Log?.Invoke(logEvent.Message, logEvent.LogLevel);
            }

            if (loginForwarder == null)
            {
                loginForwarder = (_, args) => tradeApi.HandleUserLoggedIn(
                    args.ConnectionId,
                    null,
                    args.Uuid,
                    (connectionId, packet) => packetDispatcher.Send(connectionId, packet, ExtensionId));
            }

            tradeApi.OnLogEntry += logForwarder;
            if (userManager != null)
            {
                userManager.OnLogin += loginForwarder;
            }
        }

        public void Shutdown(ExtensionHostContext hostContext)
        {
            if (tradeApi != null && logForwarder != null)
            {
                tradeApi.OnLogEntry -= logForwarder;
            }

            if (userManager != null && loginForwarder != null)
            {
                userManager.OnLogin -= loginForwarder;
            }
        }

        public IEnumerable<string> GetCapabilities()
        {
            yield return FrameworkTradeProtocol.Capability;
            yield return FrameworkTradeProtocol.CreateRequestType;
            yield return FrameworkTradeProtocol.CreateResponseType;
            yield return FrameworkTradeProtocol.SnapshotType;
            yield return FrameworkTradeProtocol.OfferUpdateRequestType;
            yield return FrameworkTradeProtocol.OfferUpdateResponseType;
            yield return FrameworkTradeProtocol.StatusUpdateRequestType;
            yield return FrameworkTradeProtocol.StatusUpdateResponseType;
            yield return FrameworkTradeProtocol.CompletedEventType;
            yield return FrameworkTradeProtocol.CancelledEventType;
        }

        public bool CanHandleIncomingCommand(FrameworkPacket command)
        {
            return command != null &&
                   command.CommandKind == global::Phinix.Framework.FrameworkCommandKind.Request &&
                   (command.MessageType == FrameworkTradeProtocol.SnapshotType ||
                    command.MessageType == FrameworkTradeProtocol.CreateRequestType ||
                    command.MessageType == FrameworkTradeProtocol.OfferUpdateRequestType ||
                    command.MessageType == FrameworkTradeProtocol.StatusUpdateRequestType);
        }

        public ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ServerFrameworkContext context)
        {
            // 管线路由细节 → DEBUG（设计哲学 §3.8），避免每条交易命令刷服务端日志
            context.Log?.Invoke($"[TradeServer] HandleIncomingCommand: type={command.MessageType}, from={context.SenderUuid}", LogLevel.DEBUG);

            switch (command.MessageType)
            {
                case FrameworkTradeProtocol.SnapshotType:
                    tradeApi.HandleSnapshotRequest(context);
                    break;
                case FrameworkTradeProtocol.CreateRequestType:
                    tradeApi.HandleCreateRequest(command, context);
                    break;
                case FrameworkTradeProtocol.OfferUpdateRequestType:
                    tradeApi.HandleOfferUpdateRequest(command, context);
                    break;
                case FrameworkTradeProtocol.StatusUpdateRequestType:
                    tradeApi.HandleStatusUpdateRequest(command, context);
                    break;
            }

            return new ServerIncomingCommandResult
            {
                Action = MessageHandlingResultAction.Handle
            };
        }

        public bool CanHandleIncomingItem(FrameworkPacket itemPacket)
        {
            return itemPacket != null && !string.IsNullOrEmpty(itemPacket.PayloadJson);
        }

        public ServerIncomingItemResult HandleIncomingItem(FrameworkPacket itemPacket, ServerFrameworkContext context, IReadOnlyList<IItemCodec> codecs)
        {
            if (string.IsNullOrEmpty(itemPacket.PayloadJson))
            {
                return new ServerIncomingItemResult
                {
                    Action = ItemHandlingResultAction.Continue,
                    FailureReason = "Item packet has no PayloadJson"
                };
            }

            FrameworkItemPayload payload;
            try
            {
                payload = FrameworkSerialization.DeserializePayload<FrameworkItemPayload>(itemPacket.PayloadJson);
            }
            catch (Exception ex)
            {
                return new ServerIncomingItemResult
                {
                    Action = ItemHandlingResultAction.Continue,
                    FailureReason = $"Failed to deserialize item payload: {ex.Message}",
                    FailureException = ex
                };
            }

            if (payload == null || string.IsNullOrEmpty(payload.CodecId))
            {
                return new ServerIncomingItemResult
                {
                    Action = ItemHandlingResultAction.Continue,
                    FailureReason = "Payload or CodecId is empty"
                };
            }

            tradeApi.CacheItemPacket(itemPacket.MessageId, payload);

            context.Log?.Invoke(
                $"[TradeServer] Cached item packet id={itemPacket.MessageId}, codec={payload.CodecId}",
                LogLevel.DEBUG);

            return new ServerIncomingItemResult
            {
                Action = ItemHandlingResultAction.Handled,
                DecodedItem = payload,
                HandledByHandlerId = ExtensionId
            };
        }
    }
}
