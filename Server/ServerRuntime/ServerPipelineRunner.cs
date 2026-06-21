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

        public IReadOnlyList<IServerInboundItemInterceptor> InboundItemInterceptors { get; }

        public IReadOnlyList<IServerDefaultItemHandler> DefaultItemHandlers { get; }

        public IReadOnlyList<IServerItemObserver> ItemObservers { get; }

        public ServerPipelineRunner(
            IReadOnlyList<IServerInboundMessageInterceptor> inboundMessageInterceptors,
            IReadOnlyList<IServerDefaultMessageHandler> defaultMessageHandlers,
            IReadOnlyList<IServerMessageObserver> messageObservers,
            IReadOnlyList<IServerInboundCommandInterceptor> inboundCommandInterceptors,
            IReadOnlyList<IServerDefaultCommandHandler> defaultCommandHandlers,
            IReadOnlyList<IServerCommandObserver> commandObservers,
            IReadOnlyList<IServerOutboundPacketInterceptor> outboundPacketInterceptors,
            IReadOnlyList<IItemCodec> itemCodecs,
            IReadOnlyList<IServerInboundItemInterceptor> inboundItemInterceptors = null,
            IReadOnlyList<IServerDefaultItemHandler> defaultItemHandlers = null,
            IReadOnlyList<IServerItemObserver> itemObservers = null)
        {
            InboundMessageInterceptors = inboundMessageInterceptors ?? Array.Empty<IServerInboundMessageInterceptor>();
            DefaultMessageHandlers = defaultMessageHandlers ?? Array.Empty<IServerDefaultMessageHandler>();
            MessageObservers = messageObservers ?? Array.Empty<IServerMessageObserver>();
            InboundCommandInterceptors = inboundCommandInterceptors ?? Array.Empty<IServerInboundCommandInterceptor>();
            DefaultCommandHandlers = defaultCommandHandlers ?? Array.Empty<IServerDefaultCommandHandler>();
            CommandObservers = commandObservers ?? Array.Empty<IServerCommandObserver>();
            OutboundPacketInterceptors = outboundPacketInterceptors ?? Array.Empty<IServerOutboundPacketInterceptor>();
            ItemCodecs = itemCodecs ?? Array.Empty<IItemCodec>();
            InboundItemInterceptors = inboundItemInterceptors ?? Array.Empty<IServerInboundItemInterceptor>();
            DefaultItemHandlers = defaultItemHandlers ?? Array.Empty<IServerDefaultItemHandler>();
            ItemObservers = itemObservers ?? Array.Empty<IServerItemObserver>();
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
            if (item == null)
            {
                return false;
            }

            FrameworkPacket currentItem = item;
            ItemHandlingResultAction terminalAction = ItemHandlingResultAction.Continue;
            ServerIncomingItemResult terminalResult = null;

            // 1. InboundInterception
            foreach (IServerInboundItemInterceptor interceptor in InboundItemInterceptors.Where(candidate => safeCanInterceptItem(candidate, currentItem, context)))
            {
                ServerIncomingItemResult result = null;
                try
                {
                    result = interceptor.InterceptIncomingItem(currentItem, context);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Item interceptor {interceptor.GetType().FullName} threw for '{currentItem.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }

                if (result?.Action == ItemHandlingResultAction.LegacyFallback)
                {
                    context.Log?.Invoke(
                        $"Item interceptor {interceptor.GetType().FullName} returned LegacyFallback for '{currentItem.MessageType}' — treating as Continue.",
                        LogLevel.WARNING);
                }

                if (shouldContinueItem(result?.Action))
                {
                    continue;
                }

                if (isReplaceItem(result?.Action) && result?.Item != null)
                {
                    currentItem = result.Item;
                    terminalResult = result;
                    continue;
                }

                if (isBlockedItem(result?.Action) || isHandledItem(result?.Action))
                {
                    terminalAction = normalizeTerminalItemAction(result.Action);
                    terminalResult = result;
                    observeItem(currentItem, context, terminalAction, terminalResult);
                    return true;
                }
            }

            // 2. DefaultProcess (注册的 handlers)
            bool matchedHandler = false;
            foreach (IServerDefaultItemHandler handler in DefaultItemHandlers.Where(candidate => safeCanHandleItem(candidate, currentItem, context)))
            {
                matchedHandler = true;
                ServerIncomingItemResult result = null;
                try
                {
                    result = handler.HandleIncomingItem(currentItem, context, ItemCodecs);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Item handler {handler.GetType().FullName} threw for '{currentItem.MessageType}': {ex}",
                        LogLevel.ERROR);
                    result = new ServerIncomingItemResult
                    {
                        Action = ItemHandlingResultAction.Continue,
                        FailureReason = $"Handler '{handler.GetType().FullName}' threw exception",
                        FailureException = ex
                    };
                    continue;
                }

                if (result?.Action == ItemHandlingResultAction.LegacyFallback)
                {
                    context.Log?.Invoke(
                        $"Item handler {handler.GetType().FullName} returned LegacyFallback for '{currentItem.MessageType}' — treating as Continue.",
                        LogLevel.WARNING);
                }

                if (shouldContinueItem(result?.Action))
                {
                    continue;
                }

                if (isReplaceItem(result?.Action) && result?.Item != null)
                {
                    currentItem = result.Item;
                    terminalResult = result;
                    continue;
                }

                if (isHandledItem(result?.Action))
                {
                    terminalAction = normalizeTerminalItemAction(result.Action);
                    terminalResult = result;
                    observeItem(currentItem, context, terminalAction, terminalResult);
                    return true;
                }

                if (isBlockedItem(result?.Action))
                {
                    terminalAction = normalizeTerminalItemAction(result.Action);
                    terminalResult = result;
                    observeItem(currentItem, context, terminalAction, terminalResult);
                    return true;
                }
            }

            // 3. 内置兜底：如果没有 handler 处理，用注册的 codec 尝试解码（保留旧行为）
            if (terminalResult == null || shouldContinueItem(terminalResult?.Action))
            {
                ServerIncomingItemResult builtinResult = decodeWithCodecsBuiltin(currentItem, context);
                if (builtinResult != null && isHandledItem(builtinResult.Action))
                {
                    terminalResult = builtinResult;
                    terminalAction = ItemHandlingResultAction.Handled;
                    matchedHandler = true;
                }
                else if (builtinResult != null && !string.IsNullOrEmpty(builtinResult.FailureReason))
                {
                    terminalResult = builtinResult;
                }
            }

            if (matchedHandler || (terminalResult != null && isHandledItem(terminalResult.Action)))
            {
                observeItem(currentItem, context, terminalAction, terminalResult);
            }

            return matchedHandler || (terminalResult != null && isHandledItem(terminalResult.Action));
        }

        private static bool safeCanInterceptItem(IServerInboundItemInterceptor interceptor, FrameworkPacket item, ServerFrameworkContext context)
        {
            try
            {
                return interceptor.CanInterceptIncomingItem(item);
            }
            catch (Exception ex)
            {
                context.Log?.Invoke(
                    $"Item interceptor {interceptor.GetType().FullName}.CanIntercept threw for '{item.MessageType}': {ex}",
                    LogLevel.ERROR);
                return false;
            }
        }

        private static bool safeCanHandleItem(IServerDefaultItemHandler handler, FrameworkPacket item, ServerFrameworkContext context)
        {
            try
            {
                return handler.CanHandleIncomingItem(item);
            }
            catch (Exception ex)
            {
                context.Log?.Invoke(
                    $"Item handler {handler.GetType().FullName}.CanHandle threw for '{item.MessageType}': {ex}",
                    LogLevel.ERROR);
                return false;
            }
        }

        private ServerIncomingItemResult decodeWithCodecsBuiltin(FrameworkPacket item, ServerFrameworkContext context)
        {
            if (!FrameworkSerialization.TryExtractItemPayload(item, out FrameworkItemPayload payload))
            {
                return new ServerIncomingItemResult
                {
                    Action = ItemHandlingResultAction.Continue,
                    FailureReason = "Item packet has no extractable payload (neither PayloadBytes nor PayloadJson)"
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
                    if (decoded != null && codec.CanEncode(decoded, codecContext))
                    {
                        codec.Encode(decoded, codecContext);
                    }

                    return new ServerIncomingItemResult
                    {
                        Action = ItemHandlingResultAction.Handled,
                        DecodedItem = decoded,
                        HandledByHandlerId = "builtin.codec-decoder"
                    };
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Item codec '{codec.CodecId}' threw for '{item.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }
            }

            context.Log?.Invoke(
                $"No item codec registered for item type '{item.MessageType}' (codec_id='{payload.CodecId}').",
                LogLevel.DEBUG);

            return new ServerIncomingItemResult
            {
                Action = ItemHandlingResultAction.Continue,
                FailureReason = $"No codec matched codec_id='{payload.CodecId}'"
            };
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

        private void observeItem(FrameworkPacket item, ServerFrameworkContext context, ItemHandlingResultAction terminalAction, ServerIncomingItemResult result)
        {
            foreach (IServerItemObserver observer in ItemObservers)
            {
                bool canObserve;
                try
                {
                    canObserve = observer.CanObserveIncomingItem(item);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Item observer {observer.GetType().FullName}.CanObserve threw for '{item.MessageType}': {ex}",
                        LogLevel.ERROR);
                    continue;
                }

                if (!canObserve)
                {
                    continue;
                }

                try
                {
                    observer.ObserveIncomingItem(item, context, terminalAction);
                }
                catch (Exception ex)
                {
                    context.Log?.Invoke(
                        $"Item observer {observer.GetType().FullName} threw for '{item.MessageType}': {ex}",
                        LogLevel.ERROR);
                }
            }
        }

        private static bool shouldContinueItem(ItemHandlingResultAction? action)
        {
            return !action.HasValue ||
                   action.Value == ItemHandlingResultAction.Continue ||
                   action.Value == ItemHandlingResultAction.LegacyFallback;
        }

        private static bool isReplaceItem(ItemHandlingResultAction? action)
        {
            return action == ItemHandlingResultAction.ReplacePayload;
        }

        private static bool isHandledItem(ItemHandlingResultAction? action)
        {
            return action == ItemHandlingResultAction.Handled;
        }

        private static bool isBlockedItem(ItemHandlingResultAction? action)
        {
            return action == ItemHandlingResultAction.StopPropagation ||
                   action == ItemHandlingResultAction.SuppressDefault;
        }

        private static ItemHandlingResultAction normalizeTerminalItemAction(ItemHandlingResultAction action)
        {
            if (isReplaceItem(action))
            {
                return ItemHandlingResultAction.ReplacePayload;
            }

            if (isBlockedItem(action))
            {
                if (action == ItemHandlingResultAction.SuppressDefault)
                {
                    return ItemHandlingResultAction.SuppressDefault;
                }
                return ItemHandlingResultAction.StopPropagation;
            }

            if (isHandledItem(action))
            {
                return ItemHandlingResultAction.Handled;
            }

            return action;
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
