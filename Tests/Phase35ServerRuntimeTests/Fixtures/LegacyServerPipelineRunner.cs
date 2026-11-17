// Frozen pre-Relay runner from 360e716c78b1f39143291b2753f6300a2740e04a; original SHA-256 589b9f14f0ac66b9042b6144ea93b401710df6a4915a3b344a3ee803842a7164
// Only the class/constructor name is isolated; no behavior edits.
using Connections;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Utils;
using Utils.Framework;

namespace ServerRuntime
{
    public sealed class LegacyServerPipelineRunner
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

        public LegacyServerPipelineRunner(
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

        private static bool TryInboundGate(Func<bool> predicate, ServerFrameworkContext context, out bool eligible)
        {
            try { eligible = predicate(); return true; }
            catch (Exception error)
            {
                eligible = false;
                LogFailure(context, "Inbound predicate failed", error);
                return false;
            }
        }

        private static void LogFailure(ServerFrameworkContext context, string message, Exception error)
        {
            // Diagnostics must not resume a request with unknown effects or leak payloads.
            try { context?.Log?.Invoke(message + ": " + error.GetType().Name, LogLevel.ERROR); } catch { }
        }

        public bool ProcessIncomingMessage(FrameworkPacket message, ServerFrameworkContext context)
        {
            FrameworkPacket currentMessage = message;
            MessageHandlingResultAction terminalAction = MessageHandlingResultAction.Continue;

            foreach (IServerInboundMessageInterceptor interceptor in InboundMessageInterceptors)
            {
                if (!TryInboundGate(() => interceptor.CanInterceptIncomingMessage(currentMessage), context, out bool eligible))
                {
                    observeMessage(currentMessage, context, MessageHandlingResultAction.StopPropagation);
                    return true;
                }
                if (!eligible) continue;
                ServerIncomingMessageResult result = null;
                try
                {
                    result = interceptor.InterceptIncomingMessage(currentMessage, context);
                }
                catch (Exception ex)
                {
                    LogFailure(context, "Inbound processing failed", ex);
                    observeMessage(currentMessage, context, MessageHandlingResultAction.StopPropagation);
                    return true;
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
            foreach (IServerDefaultMessageHandler handler in DefaultMessageHandlers)
            {
                if (!TryInboundGate(() => handler.CanHandleIncomingMessage(currentMessage), context, out bool eligible))
                {
                    observeMessage(currentMessage, context, MessageHandlingResultAction.StopPropagation);
                    return true;
                }
                if (!eligible) continue;
                matchedHandler = true;
                ServerIncomingMessageResult result = null;
                try
                {
                    result = handler.HandleIncomingMessage(currentMessage, context);
                }
                catch (Exception ex)
                {
                    LogFailure(context, "Inbound processing failed", ex);
                    observeMessage(currentMessage, context, MessageHandlingResultAction.StopPropagation);
                    return true;
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

            foreach (IServerInboundCommandInterceptor interceptor in InboundCommandInterceptors)
            {
                if (!TryInboundGate(() => interceptor.CanInterceptIncomingCommand(currentCommand), context, out bool eligible))
                {
                    observeCommand(currentCommand, context, MessageHandlingResultAction.StopPropagation);
                    return true;
                }
                if (!eligible) continue;
                ServerIncomingCommandResult result = null;
                try
                {
                    result = interceptor.InterceptIncomingCommand(currentCommand, context);
                }
                catch (Exception ex)
                {
                    LogFailure(context, "Inbound processing failed", ex);
                    observeCommand(currentCommand, context, MessageHandlingResultAction.StopPropagation);
                    return true;
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
            foreach (IServerDefaultCommandHandler handler in DefaultCommandHandlers)
            {
                if (!TryInboundGate(() => handler.CanHandleIncomingCommand(currentCommand), context, out bool eligible))
                {
                    observeCommand(currentCommand, context, MessageHandlingResultAction.StopPropagation);
                    return true;
                }
                if (!eligible) continue;
                matchedHandler = true;
                ServerIncomingCommandResult result = null;
                try
                {
                    result = handler.HandleIncomingCommand(currentCommand, context);
                }
                catch (Exception ex)
                {
                    LogFailure(context, "Inbound processing failed", ex);
                    observeCommand(currentCommand, context, MessageHandlingResultAction.StopPropagation);
                    return true;
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
            foreach (IServerInboundItemInterceptor interceptor in InboundItemInterceptors)
            {
                if (!TryInboundGate(() => interceptor.CanInterceptIncomingItem(currentItem), context, out bool eligible))
                    return stopItem(currentItem, context);
                if (!eligible) continue;
                ServerIncomingItemResult result = null;
                try
                {
                    result = interceptor.InterceptIncomingItem(currentItem, context);
                }
                catch (Exception ex)
                {
                    LogFailure(context, "Inbound item processing failed", ex);
                    return stopItem(currentItem, context);
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
            foreach (IServerDefaultItemHandler handler in DefaultItemHandlers)
            {
                if (!TryInboundGate(() => handler.CanHandleIncomingItem(currentItem), context, out bool eligible))
                    return stopItem(currentItem, context);
                if (!eligible) continue;
                matchedHandler = true;
                ServerIncomingItemResult result = null;
                try
                {
                    result = handler.HandleIncomingItem(currentItem, context, ItemCodecs);
                }
                catch (Exception ex)
                {
                    LogFailure(context, "Inbound item processing failed", ex);
                    return stopItem(currentItem, context);
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
                ServerIncomingItemResult builtinResult;
                try { builtinResult = decodeWithCodecsBuiltin(currentItem, context); }
                catch (Exception error)
                {
                    LogFailure(context, "Inbound item decoding failed", error);
                    return stopItem(currentItem, context);
                }
                if (builtinResult != null && isBlockedItem(builtinResult.Action))
                    return stopItem(currentItem, context);
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

        private bool stopItem(FrameworkPacket item, ServerFrameworkContext context)
        {
            // Consumed/stopped is not a successful business or delivery receipt.
            observeItem(item, context, ItemHandlingResultAction.StopPropagation,
                new ServerIncomingItemResult { Action = ItemHandlingResultAction.StopPropagation });
            return true;
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
                try
                {
                    if (!codec.CanDecode(payload, codecContext)) continue;
                    object decoded = codec.Decode(payload, codecContext);
                    if (decoded == null) throw new InvalidDataException("Item decoder returned no result.");
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
                    LogFailure(context, "Inbound item codec failed", ex);
                    return new ServerIncomingItemResult { Action = ItemHandlingResultAction.StopPropagation };
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

        public void DispatchOutbound(FrameworkPacket packet, ServerOutboundPacketContext context) => dispatchOutbound(packet, context, false);

        public PacketDispatchResult DispatchOutboundWithResult(FrameworkPacket packet, ServerOutboundPacketContext context,
            Func<string, FrameworkPacket, TransportSendOutcome> deliver)
        {
            var outcomes = (context?.TargetConnectionIds ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id))
                .Distinct().ToDictionary(id => id, _ => TransportSendOutcome.NotSent);
            if (packet == null || context == null || deliver == null) return new PacketDispatchResult(outcomes);
            bool uncertainFailure = false, closed = false;
            var handoffGate = new object();
            var attempted = new HashSet<string>();
            var snapshot = createSnapshot(context, context.TargetConnectionIds);
            snapshot.DeliverToConnection = (id, outgoing) =>
            {
                lock (handoffGate)
                {
                    // Report publication closes this scoped callback. Async work must initiate a
                    // separate dispatch, rather than send after its caller received NotSent.
                    if (closed || string.IsNullOrEmpty(id)) return;
                    try
                    {
                        // Interceptors receive the same gated handoff, never a capability bypass.
                        if ((context.IsConnectionFrameworkCapable != null && !context.IsConnectionFrameworkCapable(id)) ||
                            (!string.IsNullOrEmpty(outgoing?.MessageType) && context.ConnectionHasCapability != null && !context.ConnectionHasCapability(id, outgoing.MessageType)))
                        { if (!attempted.Contains(id)) outcomes[id] = TransportSendOutcome.NotSent; return; }
                        // An interceptor may call delivery and then Continue; never send a second copy.
                        if (!attempted.Add(id)) { outcomes[id] = TransportSendOutcome.Unknown; return; }
                        outcomes[id] = deliver(id, outgoing);
                    }
                    catch { outcomes[id] = TransportSendOutcome.Unknown; }
                }
            };
            try { dispatchOutbound(packet, snapshot, true); }
            catch (Exception error)
            {
                // Interceptor/predicate may already have produced side effects. Do not continue
                // another candidate or misclassify the request as safe to retry.
                lock (handoffGate)
                {
                    uncertainFailure = closed = true;
                    foreach (string id in outcomes.Keys.ToArray()) outcomes[id] = TransportSendOutcome.Unknown;
                }
                try { context.Log?.Invoke("Outbound dispatch failed: " + error.GetType().Name, LogLevel.ERROR); } catch { }
            }
            lock (handoffGate)
            {
                closed = true;
                return new PacketDispatchResult(outcomes, uncertainFailure);
            }
        }

        private void dispatchOutbound(FrameworkPacket packet, ServerOutboundPacketContext context, bool stopOnFailure)
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
                    if (stopOnFailure) throw;
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
                    if (stopOnFailure) throw;
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

            foreach (string connectionId in currentTargets.Where(connectionId => !string.IsNullOrEmpty(connectionId)).Distinct())
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
            foreach (IServerMessageObserver observer in MessageObservers)
            {
                bool canObserve;
                try
                {
                    canObserve = observer.CanObserveIncomingMessage(message);
                }
                catch (Exception ex)
                {
                    LogFailure(context, "Inbound observer failed", ex);
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
                    LogFailure(context, "Inbound observer failed", ex);
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
                    LogFailure(context, "Inbound observer failed", ex);
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
                    LogFailure(context, "Inbound observer failed", ex);
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
                    LogFailure(context, "Inbound observer failed", ex);
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
                    LogFailure(context, "Inbound observer failed", ex);
                }
            }
        }

        private static bool shouldContinueItem(ItemHandlingResultAction? action)
        {
            return !action.HasValue ||
                   action.Value == ItemHandlingResultAction.Continue;
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
