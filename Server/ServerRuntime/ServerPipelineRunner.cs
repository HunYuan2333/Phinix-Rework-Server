using System;
using System.Collections.Generic;
using System.Linq;
using Utils;
using Utils.Framework;

namespace ServerRuntime
{
    public sealed class ServerPipelineRunner
    {
        public IReadOnlyList<IServerInboundMessageInterceptor> InboundMessageInterceptors { get; }

        public IReadOnlyList<IServerDefaultMessageHandler> DefaultMessageHandlers { get; }

        public IReadOnlyList<IServerMessageObserver> MessageObservers { get; }

        public IReadOnlyList<IServerInboundCommandInterceptor> InboundCommandInterceptors { get; }

        public IReadOnlyList<IServerDefaultCommandHandler> DefaultCommandHandlers { get; }

        public IReadOnlyList<IServerCommandObserver> CommandObservers { get; }

        public IReadOnlyList<IServerOutboundPacketInterceptor> OutboundPacketInterceptors { get; }

        public IReadOnlyList<IItemCodec> ItemCodecs { get; }

        public ServerPipelineRunner(
            IReadOnlyList<IServerInboundMessageInterceptor> inboundMessageInterceptors,
            IReadOnlyList<IServerDefaultMessageHandler> defaultMessageHandlers,
            IReadOnlyList<IServerMessageObserver> messageObservers,
            IReadOnlyList<IServerInboundCommandInterceptor> inboundCommandInterceptors,
            IReadOnlyList<IServerDefaultCommandHandler> defaultCommandHandlers,
            IReadOnlyList<IServerCommandObserver> commandObservers,
            IReadOnlyList<IServerOutboundPacketInterceptor> outboundPacketInterceptors,
            IReadOnlyList<IItemCodec> itemCodecs)
        {
            InboundMessageInterceptors = inboundMessageInterceptors ?? Array.Empty<IServerInboundMessageInterceptor>();
            DefaultMessageHandlers = defaultMessageHandlers ?? Array.Empty<IServerDefaultMessageHandler>();
            MessageObservers = messageObservers ?? Array.Empty<IServerMessageObserver>();
            InboundCommandInterceptors = inboundCommandInterceptors ?? Array.Empty<IServerInboundCommandInterceptor>();
            DefaultCommandHandlers = defaultCommandHandlers ?? Array.Empty<IServerDefaultCommandHandler>();
            CommandObservers = commandObservers ?? Array.Empty<IServerCommandObserver>();
            OutboundPacketInterceptors = outboundPacketInterceptors ?? Array.Empty<IServerOutboundPacketInterceptor>();
            ItemCodecs = itemCodecs ?? Array.Empty<IItemCodec>();
        }

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

                if (result?.Action == MessageHandlingResultAction.LegacyFallback)
                {
                    context.Log?.Invoke(
                        $"Message interceptor {interceptor.GetType().FullName} returned LegacyFallback for '{currentMessage.MessageType}' — treating as Continue.",
                        LogLevel.WARNING);
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

                if (result?.Action == MessageHandlingResultAction.LegacyFallback)
                {
                    context.Log?.Invoke(
                        $"Message handler {handler.GetType().FullName} returned LegacyFallback for '{currentMessage.MessageType}' — treating as Continue.",
                        LogLevel.WARNING);
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

                if (result?.Action == MessageHandlingResultAction.LegacyFallback)
                {
                    context.Log?.Invoke(
                        $"Command interceptor {interceptor.GetType().FullName} returned LegacyFallback for '{currentCommand.MessageType}' — treating as Continue.",
                        LogLevel.WARNING);
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

                if (result?.Action == MessageHandlingResultAction.LegacyFallback)
                {
                    context.Log?.Invoke(
                        $"Command handler {handler.GetType().FullName} returned LegacyFallback for '{currentCommand.MessageType}' — treating as Continue.",
                        LogLevel.WARNING);
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

        public bool ProcessIncomingItem(FrameworkPacket item, ServerFrameworkContext context)
        {
            if (item == null || string.IsNullOrEmpty(item.PayloadJson))
            {
                return false;
            }

            FrameworkItemPayload payload;
            try
            {
                payload = FrameworkSerialization.DeserializePayload<FrameworkItemPayload>(item.PayloadJson);
            }
            catch (Exception ex)
            {
                context.Log?.Invoke($"Failed to deserialize item payload: {ex.Message}", LogLevel.WARNING);
                return false;
            }

            if (payload == null || string.IsNullOrEmpty(payload.CodecId))
            {
                return false;
            }

            ItemCodecContext codecContext = new ItemCodecContext
            {
                CompatibilityMode = FrameworkCompatibilityMode.FrameworkV2,
                Log = context.Log
            };

            foreach (IItemCodec codec in ItemCodecs)
            {
                if (!codec.CanDecode(payload, codecContext))
                {
                    continue;
                }

                try
                {
                    object decoded = codec.Decode(payload, codecContext);
                    if (codec.CanEncode(decoded, codecContext))
                    {
                        codec.Encode(decoded, codecContext);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Item codec '{codec.CodecId}' threw for '{item.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }
            }

            return false;
        }

        public void DispatchOutbound(FrameworkPacket packet, ServerOutboundPacketContext context)
        {
            if (packet == null || context?.DeliverToConnection == null)
            {
                return;
            }

            FrameworkPacket currentPacket = packet;
            IReadOnlyCollection<string> currentTargets = context.TargetConnectionIds ?? Array.Empty<string>();

            foreach (IServerOutboundPacketInterceptor interceptor in OutboundPacketInterceptors)
            {
                ServerOutboundPacketContext snapshot = createSnapshot(context, currentTargets);
                bool canIntercept;
                try
                {
                    canIntercept = interceptor.CanInterceptOutgoingPacket(currentPacket, snapshot);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Outbound packet interceptor {interceptor.GetType().FullName}.CanIntercept threw for '{currentPacket.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }

                if (!canIntercept)
                {
                    continue;
                }

                ServerOutgoingPacketResult result;
                try
                {
                    result = interceptor.InterceptOutgoingPacket(currentPacket, snapshot);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Outbound packet interceptor {interceptor.GetType().FullName} threw for '{currentPacket.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }

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
                   action.Value == MessageHandlingResultAction.Observe ||
                   action.Value == MessageHandlingResultAction.LegacyFallback;
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
            foreach (IServerMessageObserver observer in MessageObservers)
            {
                bool canObserve;
                try
                {
                    canObserve = observer.CanObserveIncomingMessage(message);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Message observer {observer.GetType().FullName}.CanObserve threw for '{message.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }

                if (!canObserve)
                {
                    continue;
                }

                try
                {
                    observer.ObserveIncomingMessage(message, context, terminalAction);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Message observer {observer.GetType().FullName} threw for '{message.MessageType}': {ex}",
                        LogLevel.ERROR);
                }
            }
        }

        private void observeCommand(FrameworkPacket command, ServerFrameworkContext context, MessageHandlingResultAction terminalAction)
        {
            foreach (IServerCommandObserver observer in CommandObservers)
            {
                bool canObserve;
                try
                {
                    canObserve = observer.CanObserveIncomingCommand(command);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Command observer {observer.GetType().FullName}.CanObserve threw for '{command.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }

                if (!canObserve)
                {
                    continue;
                }

                try
                {
                    observer.ObserveIncomingCommand(command, context, terminalAction);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Command observer {observer.GetType().FullName} threw for '{command.MessageType}': {ex}",
                        LogLevel.ERROR);
                }
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
