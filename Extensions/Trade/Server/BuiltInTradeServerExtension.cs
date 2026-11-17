using System;
using ServerRuntime;
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
        private EventHandler<ServerSessionReadyEventArgs> readyForwarder;
        private IServerSessionReadiness readiness;
        private IFrameworkServerPacketDispatcher packetDispatcher;
        private Connections.IServerTransport recoveryTransport;
        private EventHandler recoveryPoll;

        public string ExtensionId => FrameworkTradeProtocol.Capability;

        public int Priority => 1100;

        public void Register(IExtensionBuilder builder)
        {
            userManager = builder.HostContext.GetRequiredService<UserManagement.IServerUserManager>();
            packetDispatcher = builder.HostContext.GetRequiredService<IFrameworkServerPacketDispatcher>();
            var config = builder.HostContext.GetRequiredService<IExtensionConfigProvider>().GetConfig<TradeServerConfig>();
            config.Validate();
            tradeApi = tradeApi ?? new PhinixFrameworkTradeServerService(userManager, builder.HostContext.GetRequiredService<IServerSessionReadiness>(), builder.HostContext.GetRequiredService<IServerWorkQueue>(), config);
            builder.RegisterApi(tradeApi);
            builder.RegisterApi((ITradeNegotiationApi)tradeApi);
            builder.RegisterApi((ITradeEndpointRegistration)tradeApi);
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

            readiness = hostContext.GetRequiredService<IServerSessionReadiness>();
            if (readyForwarder == null)
            {
                // Recovery retains the negotiated receipt contract and never invents an outcome.
                readyForwarder = (_, args) =>
                {
                    try
                    {
                        tradeApi.HandleSnapshotRequest(new ServerFrameworkContext
                        {
                            ConnectionId = args.ConnectionId,
                            SessionId = args.SessionId,
                            SenderUuid = args.PrincipalId,
                            SendMessage = (connectionId, packet) => packetDispatcher.Send(connectionId, packet, ExtensionId)
                        });
                    }
                    finally
                    {
                        tradeApi.HandleUserLoggedIn(args.ConnectionId, args.SessionId, args.PrincipalId,
                            (connectionId, packet) => packetDispatcher.Send(connectionId, packet, ExtensionId));
                    }
                };
            }

            tradeApi.OnLogEntry += logForwarder;
            readiness.SessionReady += readyForwarder;
            if (recoveryTransport == null && hostContext.TryGetService<Connections.IServerTransport>(out var transport))
            {
                recoveryTransport = transport;
                recoveryPoll = (_, _) => (tradeApi as PhinixFrameworkTradeServerService)?.RetryPendingRecovery();
                recoveryTransport.PollCompleted += recoveryPoll;
            }
        }

        public void Shutdown(ExtensionHostContext hostContext)
        {
            if (recoveryTransport != null) recoveryTransport.PollCompleted -= recoveryPoll;
            recoveryTransport = null; recoveryPoll = null;
            (tradeApi as PhinixFrameworkTradeServerService)?.CancelPendingRecovery();
            (tradeApi as PhinixFrameworkTradeServerService)?.ClearItemPacketCache();
            if (tradeApi != null && logForwarder != null)
            {
                tradeApi.OnLogEntry -= logForwarder;
            }

            if (readiness != null && readyForwarder != null)
            {
                readiness.SessionReady -= readyForwarder;
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
            yield return FrameworkTradeProtocol.CompletionAckRequestType;
            yield return FrameworkTradeProtocol.CompletionAckResponseType;
            yield return FrameworkTradeOperationQueryProtocol.RequestType;
            yield return FrameworkTradeOperationQueryProtocol.ResponseType;
            yield return TradeDeliveryProtocol.Request;
            yield return TradeDeliveryProtocol.Response;
        }

        public bool CanHandleIncomingCommand(FrameworkPacket command)
        {
            return command != null &&
                   command.CommandKind == global::Phinix.Framework.FrameworkCommandKind.Request &&
                   (command.MessageType == FrameworkTradeProtocol.SnapshotType ||
                    command.MessageType == FrameworkTradeProtocol.CreateRequestType ||
                    command.MessageType == FrameworkTradeProtocol.OfferUpdateRequestType ||
                    command.MessageType == FrameworkTradeProtocol.StatusUpdateRequestType ||
                    command.MessageType == FrameworkTradeProtocol.CompletionAckRequestType ||
                    command.MessageType == FrameworkTradeOperationQueryProtocol.RequestType || command.MessageType==TradeDeliveryProtocol.Request);
        }

        public ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ServerFrameworkContext context)
        {
            // 管线路由细节 → DEBUG（设计哲学 §3.8），避免每条交易命令刷服务端日志
            context.Log?.Invoke($"[TradeServer] HandleIncomingCommand: type={command.MessageType}, from={context.SenderUuid}", LogLevel.DEBUG);

            switch (command.MessageType)
            {
                case TradeDeliveryProtocol.Request:
                    ((ITradeDeliveryReceiptApi)tradeApi).HandleDeliveryReceipt(command,context);
                    break;
                case FrameworkTradeOperationQueryProtocol.RequestType:
                    ((IFrameworkTradeOperationQueryApi)tradeApi).HandleOperationQueryRequest(command, context);
                    break;
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
                case FrameworkTradeProtocol.CompletionAckRequestType:
                    tradeApi.HandleCompletionAckRequest(command, context);
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

            if (!(tradeApi is IScopedFrameworkTradeItemCache cache) ||
                !cache.TryCacheItemPacket(itemPacket.MessageId, payload, context))
            {
                return new ServerIncomingItemResult
                {
                    Action = ItemHandlingResultAction.StopPropagation,
                    FailureReason = "Item cache rejected the packet scope, identity, content or capacity."
                };
            }

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
