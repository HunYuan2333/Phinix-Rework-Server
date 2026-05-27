using Utils;
using Utils.Framework;

namespace PhinixServer.Extensions
{
    [PhinixExtension("builtin.server.pipeline-logging")]
    public sealed class FrameworkPipelineLoggingServerExtension : IPhinixExtensionModule, IServerMessageObserver, IServerCommandObserver
    {
        public string ExtensionId => "builtin.server.pipeline-logging";

        public int Priority => int.MaxValue;

        public void Register(IExtensionBuilder builder)
        {
            builder.AddServerMessageObserver(this);
            builder.AddServerCommandObserver(this);
        }

        public bool CanObserveIncomingMessage(FrameworkPacket message)
        {
            return message != null;
        }

        public void ObserveIncomingMessage(FrameworkPacket message, ServerFrameworkContext context, MessageHandlingResultAction terminalAction)
        {
            context.Log?.Invoke(
                $"Observed framework message '{message.MessageType ?? "unknown"}' from '{context.SenderUuid ?? "<null>"}' " +
                $"on connection '{context.ConnectionId ?? "<null>"}' with terminal action '{terminalAction}'.",
                LogLevel.DEBUG);
        }

        public bool CanObserveIncomingCommand(FrameworkPacket command)
        {
            return command != null;
        }

        public void ObserveIncomingCommand(FrameworkPacket command, ServerFrameworkContext context, MessageHandlingResultAction terminalAction)
        {
            context.Log?.Invoke(
                $"Observed framework command '{command.MessageType ?? "unknown"}' from '{context.SenderUuid ?? "<null>"}' " +
                $"on connection '{context.ConnectionId ?? "<null>"}' with terminal action '{terminalAction}'.",
                LogLevel.DEBUG);
        }
    }
}
