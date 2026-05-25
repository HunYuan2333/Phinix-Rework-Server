using System.Collections.Generic;
using Phinix.TradeExtension;
using PhinixServer.Framework;
using Utils.Framework;

namespace PhinixServer.Extensions
{
    [PhinixExtension(FrameworkTradeProtocol.Capability)]
    public sealed class BuiltInTradeServerExtension : IPhinixExtensionModule, ICapabilityProvider, IServerCommandHandler
    {
        private BuiltInTradeServerHostServices hostServices;

        public string ExtensionId => FrameworkTradeProtocol.Capability;

        public int Priority => 1100;

        public void Register(IExtensionComponentSink sink, ExtensionHostContext hostContext)
        {
            hostServices = hostContext.GetRequiredService<BuiltInTradeServerHostServices>();
            sink.AddCapabilityProvider(this);
            sink.AddServerCommandHandler(this);
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
                    hostServices.TradeService.HandleSnapshotRequest(context);
                    break;
                case FrameworkTradeProtocol.CreateRequestType:
                    hostServices.TradeService.HandleCreateRequest(command, context);
                    break;
                case FrameworkTradeProtocol.OfferUpdateRequestType:
                    hostServices.TradeService.HandleOfferUpdateRequest(command, context);
                    break;
                case FrameworkTradeProtocol.StatusUpdateRequestType:
                    hostServices.TradeService.HandleStatusUpdateRequest(command, context);
                    break;
            }

            return new ServerIncomingCommandResult
            {
                Action = MessageHandlingResultAction.Handled
            };
        }
    }
}
