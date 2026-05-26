using System;
using System.Collections.Generic;
using Utils.Framework;

namespace Phinix.TradeExtension.Server
{
    [PhinixExtension(FrameworkTradeProtocol.Capability)]
    public sealed class BuiltInTradeServerExtension : IPhinixExtensionModule, IActivatablePhinixExtensionModule, ICapabilityProvider, IServerCommandHandler
    {
        private const string TradeStateStorageName = "trade-state.bin";

        private IFrameworkTradeServerApi tradeApi;
        private UserManagement.ServerUserManager userManager;
        private EventHandler<Utils.LogEventArgs> logForwarder;
        private EventHandler<UserManagement.ServerLoginEventArgs> loginForwarder;
        private IFrameworkServerPacketDispatcher packetDispatcher;

        public string ExtensionId => FrameworkTradeProtocol.Capability;

        public int Priority => 1100;

        public void Register(IExtensionBuilder builder)
        {
            userManager = builder.HostContext.GetRequiredService<UserManagement.ServerUserManager>();
            packetDispatcher = builder.HostContext.GetRequiredService<IFrameworkServerPacketDispatcher>();
            tradeApi = tradeApi ?? new PhinixFrameworkTradeServerService(userManager);
            builder.RegisterApi(tradeApi);
            builder.HostContext.RegisterPersistent(ExtensionId, TradeStateStorageName, tradeApi);
            builder.AddCapabilityProvider(this);
            builder.AddServerCommandHandler(this);
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
                loginForwarder = (_, args) => tradeApi.HandleUserLoggedIn(args.ConnectionId, null, args.Uuid, packetDispatcher.Send);
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
                Action = MessageHandlingResultAction.Handled
            };
        }
    }
}
