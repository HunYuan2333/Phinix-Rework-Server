using System;
using System.Collections.Generic;
using System.Linq;
using Utils;
using Utils.Framework;

namespace ServerRuntime
{
    public sealed class ServerPipelineRunner
    {
        public IList<IServerInboundMessageInterceptor> InboundMessageInterceptors { get; } = new List<IServerInboundMessageInterceptor>();

        public IList<IServerDefaultMessageHandler> DefaultMessageHandlers { get; } = new List<IServerDefaultMessageHandler>();

        public IList<IServerMessageObserver> MessageObservers { get; } = new List<IServerMessageObserver>();

        public IList<IServerInboundCommandInterceptor> InboundCommandInterceptors { get; } = new List<IServerInboundCommandInterceptor>();

        public IList<IServerDefaultCommandHandler> DefaultCommandHandlers { get; } = new List<IServerDefaultCommandHandler>();

        public IList<IServerCommandObserver> CommandObservers { get; } = new List<IServerCommandObserver>();

        public IList<IServerOutboundPacketInterceptor> OutboundPacketInterceptors { get; } = new List<IServerOutboundPacketInterceptor>();

        public bool ProcessIncomingMessage(FrameworkPacket message, ServerFrameworkContext context)
        {
            FrameworkPacket currentMessage = message;
            MessageHandlingResultAction terminalAction = MessageHandlingResultAction.Continue;

            foreach (IServerInboundMessageInterceptor interceptor in InboundMessageInterceptors.Where(candidate => candidate.CanInterceptIncomingMessage(currentMessage)))
            {
                ServerIncomingMessageResult result = null;
                try
                {
                    result = interceptor.InterceptIncomingMessage(currentMessage, context);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Message interceptor {interceptor.GetType().FullName} threw for '{currentMessage.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }
                if (shouldContinue(result?.Action))
                {
                    continue;
                }

                if (isReplace(result?.Action) && result?.Message != null)
                {
                    currentMessage = result.Message;
                    continue;
                }

                if (isBlocked(result?.Action) || isHandled(result?.Action))
                {
                    terminalAction = normalizeTerminalAction(result.Action);
                    observeMessage(currentMessage, context, terminalAction);
                    return true;
                }
            }

            bool matchedHandler = false;
            foreach (IServerDefaultMessageHandler handler in DefaultMessageHandlers.Where(candidate => candidate.CanHandleIncomingMessage(currentMessage)))
            {
                matchedHandler = true;
                ServerIncomingMessageResult result = null;
                try
                {
                    result = handler.HandleIncomingMessage(currentMessage, context);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Message handler {handler.GetType().FullName} threw for '{currentMessage.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }
                if (shouldContinue(result?.Action))
                {
                    continue;
                }

                if (isReplace(result?.Action) && result?.Message != null)
                {
                    currentMessage = result.Message;
                    continue;
                }

                if (isBlocked(result?.Action) || isHandled(result?.Action))
                {
                    terminalAction = normalizeTerminalAction(result.Action);
                    observeMessage(currentMessage, context, terminalAction);
                    return true;
                }
            }

            if (matchedHandler)
            {
                observeMessage(currentMessage, context, terminalAction);
            }

            return matchedHandler;
        }

        public bool ProcessIncomingCommand(FrameworkPacket command, ServerFrameworkContext context)
        {
            FrameworkPacket currentCommand = command;
            MessageHandlingResultAction terminalAction = MessageHandlingResultAction.Continue;

            foreach (IServerInboundCommandInterceptor interceptor in InboundCommandInterceptors.Where(candidate => candidate.CanInterceptIncomingCommand(currentCommand)))
            {
                ServerIncomingCommandResult result = null;
                try
                {
                    result = interceptor.InterceptIncomingCommand(currentCommand, context);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Command interceptor {interceptor.GetType().FullName} threw for '{currentCommand.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }
                if (shouldContinue(result?.Action))
                {
                    continue;
                }

                if (isReplace(result?.Action) && result?.Command != null)
                {
                    currentCommand = result.Command;
                    continue;
                }

                if (isBlocked(result?.Action) || isHandled(result?.Action))
                {
                    terminalAction = normalizeTerminalAction(result.Action);
                    observeCommand(currentCommand, context, terminalAction);
                    return true;
                }
            }

            bool matchedHandler = false;
            foreach (IServerDefaultCommandHandler handler in DefaultCommandHandlers.Where(candidate => candidate.CanHandleIncomingCommand(currentCommand)))
            {
                matchedHandler = true;
                ServerIncomingCommandResult result = null;
                try
                {
                    result = handler.HandleIncomingCommand(currentCommand, context);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Command handler {handler.GetType().FullName} threw for '{currentCommand.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }
                if (shouldContinue(result?.Action))
                {
                    continue;
                }

                if (isReplace(result?.Action) && result?.Command != null)
                {
                    currentCommand = result.Command;
                    continue;
                }

                if (isBlocked(result?.Action) || isHandled(result?.Action))
                {
                    terminalAction = normalizeTerminalAction(result.Action);
                    observeCommand(currentCommand, context, terminalAction);
                    return true;
                }
            }

            if (matchedHandler)
            {
                observeCommand(currentCommand, context, terminalAction);
            }

            return matchedHandler;
        }

        public void DispatchOutbound(FrameworkPacket packet, ServerOutboundPacketContext context)
        {
            if (packet == null || context?.DeliverToConnection == null)
            {
                return;
            }

            FrameworkPacket currentPacket = packet;
            IReadOnlyCollection<string> currentTargets = context.TargetConnectionIds ?? Array.Empty<string>();

            foreach (IServerOutboundPacketInterceptor interceptor in OutboundPacketInterceptors.Where(candidate => candidate.CanInterceptOutgoingPacket(currentPacket, createSnapshot(context, currentTargets))))
            {
                ServerOutgoingPacketResult result = interceptor.InterceptOutgoingPacket(currentPacket, createSnapshot(context, currentTargets));
                if (result == null || shouldContinue(result.Action))
                {
                    continue;
                }

                if (isReplace(result.Action))
                {
                    if (result.Packet != null)
                    {
                        currentPacket = result.Packet;
                    }

                    if (result.TargetConnectionIds != null)
                    {
                        currentTargets = result.TargetConnectionIds;
                    }

                    continue;
                }

                if (isBlocked(result.Action))
                {
                    return;
                }

                if (isHandled(result.Action))
                {
                    if (result.Packet != null)
                    {
                        currentPacket = result.Packet;
                    }

                    if (result.TargetConnectionIds != null)
                    {
                        currentTargets = result.TargetConnectionIds;
                    }
                    break;
                }
            }

            foreach (string connectionId in currentTargets.Where(connectionId => !string.IsNullOrEmpty(connectionId)))
            {
                if (context.IsConnectionFrameworkCapable != null && !context.IsConnectionFrameworkCapable(connectionId))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(currentPacket.MessageType) &&
                    context.ConnectionHasCapability != null &&
                    !context.ConnectionHasCapability(connectionId, currentPacket.MessageType))
                {
                    continue;
                }

                context.DeliverToConnection(connectionId, currentPacket);
            }
        }

        private static ServerOutboundPacketContext createSnapshot(ServerOutboundPacketContext context, IReadOnlyCollection<string> currentTargets)
        {
            return new ServerOutboundPacketContext
            {
                SourceExtensionId = context.SourceExtensionId,
                TargetConnectionIds = currentTargets ?? Array.Empty<string>(),
                DeliverToConnection = context.DeliverToConnection,
                IsConnectionFrameworkCapable = context.IsConnectionFrameworkCapable,
                ConnectionHasCapability = context.ConnectionHasCapability,
                Log = context.Log
            };
        }

        private static bool shouldContinue(MessageHandlingResultAction? action)
        {
            return !action.HasValue ||
                   action.Value == MessageHandlingResultAction.Continue ||
                   action.Value == MessageHandlingResultAction.Observe;
        }

        private static bool isReplace(MessageHandlingResultAction? action)
        {
            return action == MessageHandlingResultAction.Replace ||
                   action == MessageHandlingResultAction.ReplacePayload;
        }

        private static bool isHandled(MessageHandlingResultAction? action)
        {
            return action == MessageHandlingResultAction.Handle ||
                   action == MessageHandlingResultAction.Handled;
        }

        private static bool isBlocked(MessageHandlingResultAction? action)
        {
            return action == MessageHandlingResultAction.Block ||
                   action == MessageHandlingResultAction.StopPropagation ||
                   action == MessageHandlingResultAction.SuppressDefault;
        }

        private void observeMessage(FrameworkPacket message, ServerFrameworkContext context, MessageHandlingResultAction terminalAction)
        {
            foreach (IServerMessageObserver observer in MessageObservers.Where(candidate => candidate.CanObserveIncomingMessage(message)))
            {
                observer.ObserveIncomingMessage(message, context, terminalAction);
            }
        }

        private void observeCommand(FrameworkPacket command, ServerFrameworkContext context, MessageHandlingResultAction terminalAction)
        {
            foreach (IServerCommandObserver observer in CommandObservers.Where(candidate => candidate.CanObserveIncomingCommand(command)))
            {
                observer.ObserveIncomingCommand(command, context, terminalAction);
            }
        }

        private static MessageHandlingResultAction normalizeTerminalAction(MessageHandlingResultAction action)
        {
            if (isReplace(action))
            {
                return MessageHandlingResultAction.Replace;
            }

            if (isBlocked(action))
            {
                return MessageHandlingResultAction.Block;
            }

            if (isHandled(action))
            {
                return MessageHandlingResultAction.Handle;
            }

            return action;
        }
    }
}
