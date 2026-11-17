using System;
using Phinix.ExtensionCompatibility;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Authentication;
using Connections;
using UserManagement;
using Utils;
using Utils.Framework;
using ServerRuntime;

namespace PhinixServer.Framework
{
    public class PhinixFrameworkServer : ILoggable, IFrameworkServerBroadcaster
    {
        public event EventHandler<LogEventArgs> OnLogEntry;

        public void RaiseLogEntry(LogEventArgs e) => OnLogEntry?.Invoke(this, e);

        private readonly IServerTransport netServer;
        private readonly ServerAuthenticator authenticator;
        private readonly ServerUserManager userManager;
        private readonly ExtensionHostContext extensionHostContext;
        private ExtensionPersistenceRegistration[] shutdownRegistrations;
        private bool persistenceLoadFailed;
        private bool packetHandlerRegistered, connectionClosedSubscribed;
        private volatile bool outboundReady;
        private readonly DiscoveredPhinixExtensions discoveredExtensions;
        private readonly ServerPipelineRunner pipelineRunner;
        private readonly HashSet<string> serverCapabilities;
        private readonly ServerSessionRegistry sessionRegistry;
        private readonly CommandCapabilityRouter relayRouter;

        public PhinixFrameworkServer(IServerTransport netServer, ServerAuthenticator authenticator, ServerUserManager userManager, ExtensionHostContext extensionHostContext = null)
        {
            this.netServer = netServer;
            this.authenticator = authenticator;
            authenticator.SessionReplaced += ReplaceSession;
            this.userManager = userManager;
            this.extensionHostContext = extensionHostContext ?? new ExtensionHostContext();
            sessionRegistry = new ServerSessionRegistry(
                (connection, principal, session) => userManager.IsAuthenticatedPrincipal(connection, session, principal),
                (message, level) => RaiseLogEntry(new LogEventArgs(message, level)));
            this.extensionHostContext.AddService<IServerSessionReadiness>(sessionRegistry);
            this.extensionHostContext.TryGetService<RelayLimits>(out var relayLimits);
            relayRouter = new CommandCapabilityRouter(sessionRegistry, userManager.GetConnections,
                player => userManager.GetConnections().Any(id => userManager.IsLoggedIn(id, player)), relayLimits);
            if (this.extensionHostContext.TryGetService<ICommandRelayAuthorization>(out var relayAuthorization))
                relayRouter.Authorization = relayAuthorization;
            // All modules resolve the same neutral port during registration/activation.
            // The port refuses dispatch until initialization has finished.
            this.extensionHostContext.AddService<IFrameworkServerBroadcaster>(this);
            try
            {
                this.discoveredExtensions = PhinixExtensionRegistry.DiscoverExtensions(this.extensionHostContext);
                PhinixExtensionRegistry.ActivateExtensions(discoveredExtensions, this.extensionHostContext);
                this.serverCapabilities = new HashSet<string>(PhinixExtensionRegistry.CollectCapabilities(discoveredExtensions), StringComparer.OrdinalIgnoreCase);
                LoadExtensionState();
                this.pipelineRunner = buildPipelineRunner();
                netServer.RegisterPacketHandler(FrameworkProtocol.ModuleName, packetHandler);
                packetHandlerRegistered = true;
                netServer.OnConnectionClosed += ConnectionClosed;
                connectionClosedSubscribed = true;
            }
            catch
            {
                // Constructor failures have no owner to perform normal shutdown. Never save failed loads.
                DetachTransport();
                PhinixExtensionRegistry.ShutdownExtensions(discoveredExtensions, this.extensionHostContext);
                throw;
            }

            outboundReady = true;

            RaiseLogEntry(new LogEventArgs($"Discovered {discoveredExtensions.Extensions.Count} framework extension(s) and {serverCapabilities.Count} capability/capabilities."));
            if (discoveredExtensions.Modules.Count > 0)
            {
                RaiseLogEntry(new LogEventArgs(
                    $"Framework modules: {string.Join(", ", discoveredExtensions.Modules.Select(module => module.ExtensionId).OrderBy(extensionId => extensionId))}. " +
                    $"Server handlers={discoveredExtensions.ServerMessageHandlers.Count}, server commands={discoveredExtensions.ServerCommandHandlers.Count}, item codecs={discoveredExtensions.ItemCodecs.Count}."));
            }
            foreach (string diagnostic in discoveredExtensions.Diagnostics)
            {
                RaiseLogEntry(new LogEventArgs(diagnostic, LogLevel.DEBUG));
            }
            foreach (string warning in discoveredExtensions.Warnings)
            {
                RaiseLogEntry(new LogEventArgs(warning, LogLevel.WARNING));
            }
        }

        public void LoadExtensionState()
        {
            // A partial reload must never be followed by a periodic/final overwrite.
            persistenceLoadFailed = true;
            foreach (ExtensionPersistenceRegistration registration in extensionHostContext.Persistents)
            {
                IExtensionLogger logger = extensionHostContext.GetExtensionLogger(registration.ExtensionId);
                try
                {
                    string path = extensionHostContext.GetStoragePath(registration.ExtensionId, registration.LogicalName);
                    if (string.IsNullOrEmpty(path))
                    {
                        throw new IOException("Persistent state has no storage path.");
                    }

                    registration.Persistent.Load(path);
                }
                catch (Exception exception)
                {
                    // Parser messages/inner exceptions can contain private stored payloads.
                    logger.Log($"Failed to load persistent state '{registration.LogicalName}' ({exception.GetType().Name}).", LogLevel.ERROR);
                    throw new IOException("Extension persistent state could not be loaded.");
                }
            }
            persistenceLoadFailed = false;
        }

        public void SaveExtensionState() => TrySaveExtensionState();

        public bool TrySaveExtensionState()
        {
            if (persistenceLoadFailed) return false;
            bool saved = true;
            foreach (ExtensionPersistenceRegistration registration in shutdownRegistrations ?? extensionHostContext.Persistents.ToArray())
            {
                IExtensionLogger logger = extensionHostContext.GetExtensionLogger(registration.ExtensionId);
                try
                {
                    string path = extensionHostContext.GetStoragePath(registration.ExtensionId, registration.LogicalName);
                    if (string.IsNullOrEmpty(path))
                    {
                        saved = false;
                        logger.Log($"Skipped saving persistent state '{registration.LogicalName}' because no storage path was available.", LogLevel.WARNING);
                        continue;
                    }

                    registration.Persistent.Save(path);
                }
                catch (Exception exception)
                {
                    saved = false;
                    logger.Log($"Failed to save persistent state '{registration.LogicalName}'.", LogLevel.ERROR, exception);
                }
            }
            return saved;
        }

        private void ConnectionClosed(object sender, ConnectionEventArgs args)
        {
            relayRouter.Remove(args.ConnectionId);
            sessionRegistry.Remove(args.ConnectionId);
        }

        private void DetachTransport()
        {
            outboundReady = false;
            authenticator.SessionReplaced -= ReplaceSession;
            if (connectionClosedSubscribed) { netServer.OnConnectionClosed -= ConnectionClosed; connectionClosedSubscribed = false; }
            // A failed registration never owns the pre-existing module handler.
            if (packetHandlerRegistered) { netServer.UnregisterPacketHandler(FrameworkProtocol.ModuleName); packetHandlerRegistered = false; }
        }

        public void ShutdownExtensions()
        {
            DetachTransport();
            // Registry cleanup unregisters persistents; preserve generic owners for the final post-stop save.
            shutdownRegistrations ??= extensionHostContext.Persistents.ToArray();
            PhinixExtensionRegistry.ShutdownExtensions(discoveredExtensions, extensionHostContext);
        }

        public bool TryResolveExtensionApi<T>(out T implementation) where T : class
        {
            if (discoveredExtensions.ApiRegistry != null)
            {
                return discoveredExtensions.ApiRegistry.TryResolve(out implementation);
            }

            implementation = null;
            return false;
        }

        public IReadOnlyList<T> ResolveExtensionApis<T>() where T : class
        {
            return discoveredExtensions.ApiRegistry?.ResolveAll<T>() ?? Array.Empty<T>();
        }

        public void DispatchExtensionPacket(string sourceExtensionId, string connectionId, FrameworkPacket packet)
        {
            dispatchOutboundToConnection(sourceExtensionId, connectionId, packet);
        }

        public void Broadcast(string sourceExtensionId, FrameworkPacket packet, string[] excludedConnectionIds = null)
        {
            dispatchOutboundBroadcast(sourceExtensionId, packet, excludedConnectionIds);
        }

        private void ReplaceSession(string connection, string oldSession, string newSession)
        { if (!sessionRegistry.ReplaceSession(connection, oldSession, newSession)) netServer.DisconnectPeer(connection); }
        private void packetHandler(string module, string connectionId, byte[] data)
        {
            FrameworkPacket packet;
            try
            {
                packet = FrameworkSerialization.DeserializePacket(data);
            }
            catch (Exception exception)
            {
                RaiseLogEntry(new LogEventArgs("Failed to deserialize framework packet: " + exception.GetType().Name, LogLevel.WARNING));
                return;
            }

            if (packet == null || packet.Version != FrameworkProtocol.Version)
            {
                if (packet != null) rejectAdmission(connectionId, packet, "unsupported_version", packet.Kind == FrameworkProtocol.KindHello ? "Negotiation" : "Admission");
                RaiseLogEntry(new LogEventArgs("Rejected unsupported framework packet version.", LogLevel.WARNING));
                return;
            }

            switch (packet.Kind)
            {
                case CommandRelayProtocol.SnapshotKind:
                    handleCapabilitySnapshot(connectionId, packet, data.Length);
                    break;
                case FrameworkProtocol.KindHello:
                    handleHello(connectionId, packet);
                    break;
                case FrameworkProtocol.KindMessage:
                    if (packet.Flow == global::Phinix.Framework.FrameworkFlow.Unspecified)
                    {
                        packet.Flow = global::Phinix.Framework.FrameworkFlow.Message;
                    }
                    handleMessage(connectionId, packet);
                    break;
                case FrameworkProtocol.KindCommand:
                    if (packet.Flow == global::Phinix.Framework.FrameworkFlow.Unspecified)
                    {
                        packet.Flow = global::Phinix.Framework.FrameworkFlow.Command;
                    }
                    handleCommand(connectionId, packet);
                    break;
                case FrameworkProtocol.KindItem:
                    if (packet.Flow == global::Phinix.Framework.FrameworkFlow.Unspecified)
                    {
                        packet.Flow = global::Phinix.Framework.FrameworkFlow.Item;
                    }
                    handleItem(connectionId, packet);
                    break;
                default:
                    rejectAdmission(connectionId, packet, "unsupported_kind", "Admission");
                    RaiseLogEntry(new LogEventArgs("Received unsupported framework packet kind.", LogLevel.DEBUG));
                    break;
            }
        }

        private void rejectAdmission(string connectionId, FrameworkPacket request, string code, string stage)
        {
            // Only reply within an authenticated connection/session. Never echo a bearer from
            // another session, arbitrary payload, or pretend to resolve a previous operation.
            if (!authenticator.IsAuthenticated(connectionId, request.SessionId) ||
                string.IsNullOrEmpty(request.MessageId) || request.MessageId.Length > 128) return;
            string correlation = request.GetCorrelationId();
            if (string.IsNullOrEmpty(correlation) || correlation.Length > 128) return;
            var response = new FrameworkPacket
            {
                Kind = FrameworkAdmissionRejection.PacketKind,
                MessageType = FrameworkAdmissionRejection.PacketType,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                SessionId = request.SessionId,
                PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkAdmissionRejection
                {
                    Code = code, RequestMessageId = request.MessageId, CorrelationId = correlation, Stage = stage
                })
            };
            response.SetCorrelationId(correlation);
            // Neutral control plane bypasses business capability filters, never authentication.
            sendPacketWithOutcome(connectionId, response);
        }

        private void handleHello(string connectionId, FrameworkPacket packet)
        {
            if (!authenticator.IsAuthenticated(connectionId, packet.SessionId) || !userManager.IsLoggedIn(connectionId, packet.SenderUuid))
            {
                rejectAdmission(connectionId, packet, "not_authenticated", "Negotiation");
                RaiseLogEntry(new LogEventArgs("Rejected framework hello without a matching identity.", LogLevel.WARNING));
                return;
            }

            FrameworkCapabilitiesPayload payload;
            try { payload = FrameworkSerialization.DeserializePayload<FrameworkCapabilitiesPayload>(packet.PayloadJson); }
            catch { rejectAdmission(connectionId, packet, "invalid_hello", "Negotiation"); return; }
            if (payload == null || !sessionRegistry.TryBind(connectionId, packet.SenderUuid, packet.SessionId, packet.Version,
                payload.Capabilities, serverCapabilities, out var bound, out var failure))
            {
                rejectAdmission(connectionId, packet, "hello_conflict", "Negotiation");
                RaiseLogEntry(new LogEventArgs("Rejected invalid or conflicting framework hello.", LogLevel.WARNING));
                return;
            }

            FrameworkPacket response = new FrameworkPacket
            {
                Kind = FrameworkProtocol.KindCapabilities,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                SessionId = packet.SessionId,
                PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkCapabilitiesPayload
                {
                    Capabilities = bound.Capabilities.ToList()
                })
            };

            if (bound.Capabilities.Contains(CommandRelayProtocol.Capability, StringComparer.OrdinalIgnoreCase))
                response.SetMetadataValue(CommandRelayProtocol.EpochMetadata, bound.Epoch);
            response.SetCorrelationId(packet.GetCorrelationId());
            RaiseLogEntry(new LogEventArgs($"Framework hello accepted from {connectionId.Highlight(HighlightType.ConnectionID)}. Responding with {serverCapabilities.Count} capability/capabilities."));
            if (sendPacketWithOutcome(connectionId, response) == TransportSendOutcome.Submitted)
                sessionRegistry.MarkReady(bound);
        }

        private void handleMessage(string connectionId, FrameworkPacket message)
        {
            if (!sessionRegistry.MatchesReady(connectionId, message.SenderUuid, message.SessionId))
            {
                rejectAdmission(connectionId, message, "not_ready", "Admission");
                RaiseLogEntry(new LogEventArgs($"Rejected framework message '{message.MessageType}' from unauthenticated connection {connectionId.Highlight(HighlightType.ConnectionID)}", LogLevel.WARNING));
                return;
            }

            if (!connectionHasCapability(connectionId, message.MessageType))
            {
                rejectAdmission(connectionId, message, "unsupported_capability", "Admission");
                RaiseLogEntry(new LogEventArgs($"Rejected framework message '{message.MessageType}' from connection {connectionId.Highlight(HighlightType.ConnectionID)} because the capability was not negotiated.", LogLevel.WARNING));
                return;
            }

            if (!pipelineRunner.ProcessIncomingMessage(message, createServerContext(connectionId, message.SenderUuid, message.SessionId, null)))
            {
                RaiseLogEntry(new LogEventArgs($"No server framework handler registered for message type '{message.MessageType}'.", LogLevel.WARNING));
            }
        }

        private void handleCapabilitySnapshot(string connectionId, FrameworkPacket packet, int encodedLength)
        {
            if (!sessionRegistry.MatchesReady(connectionId, packet.SenderUuid, packet.SessionId) || !relayRouter.Supports(connectionId)) return;
            // Excess control traffic is dropped before payload decoding or ACK amplification.
            if (!relayRouter.AdmitControl(connectionId)) return;
            CommandCapabilityAck ack;
            if (encodedLength > CommandRelayProtocol.MaxPacketBytes)
                ack = new CommandCapabilityAck { Error = "PayloadTooLarge" };
            else
            {
                try { ack = relayRouter.Apply(connectionId, FrameworkSerialization.DeserializePayload<CommandCapabilitySnapshot>(packet.PayloadJson)); }
                catch { ack = new CommandCapabilityAck { Error = "InvalidCapability" }; }
            }
            sendRelayControl(connectionId, CommandRelayProtocol.AckKind, ack);
        }

        private void sendRelayControl<T>(string connectionId, string kind, T payload)
        {
            if (!sessionRegistry.TryGetReady(connectionId, out var session)) return;
            sendPacketWithOutcome(connectionId, new FrameworkPacket { Kind = kind,
                SenderUuid = FrameworkProtocol.SystemSenderUuid, SessionId = session.SessionId,
                PayloadJson = FrameworkSerialization.SerializePayload(payload) });
        }

        private void handleCommand(string connectionId, FrameworkPacket command)
        {
            bool relay = command.CommandRoute?.Mode == CommandProcessingMode.Relay;
            if (!sessionRegistry.MatchesReady(connectionId, command.SenderUuid, command.SessionId))
            {
                rejectAdmission(connectionId, command, "not_ready", "Admission");
                return;
            }
            if (!sessionRegistry.TryGetReady(connectionId, out var sender)) return;
            if (relay && (string.IsNullOrEmpty(command.MessageId) || command.MessageId.Length > 128)) return;
            // Capture trusted facts outside the plugin-mutable context and packet.
            string principal = sender.PrincipalId, epoch = sender.Epoch, requestId = command.MessageId;
            string Validate(FrameworkPacket current)
            {
                if (current == null || current.CommandRoute != null && !Enum.IsDefined(typeof(CommandProcessingMode), current.CommandRoute.Mode))
                    return "InvalidCommand";
                if (relay) return relayRouter.Validate(connectionId, principal, epoch, current);
                return null; // Preserve ordinary Command replacement admission semantics.
            }
            if (!relay && !connectionHasCapability(connectionId, command.MessageType))
            { rejectAdmission(connectionId, command, "unsupported_capability", "Admission"); return; }
            string admission = relay ? relayRouter.Admit(connectionId) : null;
            if (admission != null)
            { sendRelayControl(connectionId, CommandRelayProtocol.ResultKind, RelayResult.Failure(requestId, admission)); return; }
            var processed = pipelineRunner.ProcessCommand(command,
                createServerContext(connectionId, principal, sender.SessionId, null), Validate,
                final =>
                {
                    final.SenderUuid = principal;
                    final.MessageId = requestId;
                    return relayRouter.Route(connectionId, principal, epoch, final, (packet, targets) =>
                    {
                        // Freeze the authorized target set. Outbound hooks can only narrow it.
                        var allowed = new HashSet<string>(targets);
                        int authorizedBytes = FrameworkSerialization.SerializePacket(packet).Length;
                        return pipelineRunner.DispatchOutboundWithResult(packet, new ServerOutboundPacketContext
                        {
                            TargetConnectionIds = targets,
                            IsConnectionFrameworkCapable = id => allowed.Contains(id) && relayRouter.Supports(id),
                            Log = (message, level) => RaiseLogEntry(new LogEventArgs(message, level))
                        }, (id, outgoing) =>
                        {
                            if (!allowed.Contains(id) || Validate(outgoing) != null || !relayRouter.Eligible(id, outgoing) ||
                                outgoing.SenderUuid != principal || outgoing.MessageId != requestId ||
                                (outgoing.CommandRoute.TargetPlayerUuid == null && id == connectionId))
                                return TransportSendOutcome.NotSent;
                            if (FrameworkSerialization.SerializePacket(outgoing).Length > authorizedBytes) return TransportSendOutcome.NotSent;
                            return sendPacketWithOutcome(id, outgoing);
                        });
                    });
                });
            if (relay)
            {
                processed.Relay.RequestMessageId = requestId;
                sendRelayControl(connectionId, CommandRelayProtocol.ResultKind, processed.Relay);
            }
            else if (!processed.Consumed)
                RaiseLogEntry(new LogEventArgs("No server framework handler registered for command.", LogLevel.WARNING));
        }

        private void handleItem(string connectionId, FrameworkPacket item)
        {
            // 管线消息路由细节 → DEBUG（设计哲学 §3.8）
            RaiseLogEntry(new LogEventArgs($"Received framework item '{item.MessageType}' flow={item.Flow} kind={item.Kind} from {connectionId}", LogLevel.DEBUG));

            if (!sessionRegistry.MatchesReady(connectionId, item.SenderUuid, item.SessionId))
            {
                rejectAdmission(connectionId, item, "not_ready", "Admission");
                RaiseLogEntry(new LogEventArgs(
                    "Rejected framework item without a matching Ready session.",
                    LogLevel.WARNING));
                return;
            }

            if (!connectionHasCapability(connectionId, item.MessageType))
            {
                rejectAdmission(connectionId, item, "unsupported_capability", "Admission");
                RaiseLogEntry(new LogEventArgs($"Rejected framework item '{item.MessageType}' from connection {connectionId.Highlight(HighlightType.ConnectionID)} because the capability was not negotiated.", LogLevel.WARNING));
                return;
            }

            if (!pipelineRunner.ProcessIncomingItem(item, createServerContext(connectionId, item.SenderUuid, item.SessionId, null)))
            {
                RaiseLogEntry(new LogEventArgs($"No item codec registered for item type '{item.MessageType}'.", LogLevel.DEBUG));
            }
            else
            {
                RaiseLogEntry(new LogEventArgs($"Framework item '{item.MessageType}' consumed by the pipeline.", LogLevel.DEBUG));
            }
        }

        private bool isConnectionFrameworkCapable(string connectionId) => sessionRegistry.TryGetReady(connectionId, out _);

        private string[] getRemoteCapabilities(string connectionId) =>
            sessionRegistry.TryGetReady(connectionId, out var session) ? session.Capabilities.ToArray() : Array.Empty<string>();

        private bool connectionHasCapability(string connectionId, string capability) =>
            sessionRegistry.TryGetReady(connectionId, out var session) &&
            (string.IsNullOrEmpty(capability) || session.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase));

        private ServerPipelineRunner buildPipelineRunner()
        {
            var inboundMessageInterceptors = discoveredExtensions.ServerInboundMessageInterceptors
                .Select(interceptor => new BoundServerInboundMessageInterceptor(interceptor, getExtensionId(interceptor)))
                .OrderBy(i => i.Priority)
                .ToList();

            var defaultMessageHandlers = discoveredExtensions.ServerDefaultMessageHandlers
                .Select(handler => new BoundServerDefaultMessageHandler(handler, getExtensionId(handler)))
                .OrderBy(h => h.Priority)
                .ToList();

            var messageObservers = discoveredExtensions.ServerMessageObservers
                .Select(observer => new BoundServerMessageObserver(observer, getExtensionId(observer)))
                .OrderBy(o => o.Priority)
                .ToList();

            var inboundCommandInterceptors = discoveredExtensions.ServerInboundCommandInterceptors
                .Select(interceptor => new BoundServerInboundCommandInterceptor(interceptor, getExtensionId(interceptor)))
                .OrderBy(i => i.Priority)
                .ToList();

            var defaultCommandHandlers = discoveredExtensions.ServerDefaultCommandHandlers
                .Select(handler => new BoundServerDefaultCommandHandler(handler, getExtensionId(handler)))
                .OrderBy(h => h.Priority)
                .ToList();

            var commandObservers = discoveredExtensions.ServerCommandObservers
                .Select(observer => new BoundServerCommandObserver(observer, getExtensionId(observer)))
                .OrderBy(o => o.Priority)
                .ToList();

            var outboundPacketInterceptors = discoveredExtensions.ServerOutboundPacketInterceptors
                .OrderBy(i => i.Priority)
                .ToList();

            var itemCodecs = discoveredExtensions.ItemCodecs.ToList();

            var inboundItemInterceptors = discoveredExtensions.ServerInboundItemInterceptors
                .Select(interceptor => new BoundServerInboundItemInterceptor(interceptor, getExtensionId(interceptor)))
                .OrderBy(i => i.Priority)
                .ToList();

            var defaultItemHandlers = discoveredExtensions.ServerDefaultItemHandlers
                .Select(handler => new BoundServerDefaultItemHandler(handler, getExtensionId(handler)))
                .OrderBy(h => h.Priority)
                .ToList();

            var itemObservers = discoveredExtensions.ServerItemObservers
                .Select(observer => new BoundServerItemObserver(observer, getExtensionId(observer)))
                .OrderBy(o => o.Priority)
                .ToList();

            return new ServerPipelineRunner(
                inboundMessageInterceptors,
                defaultMessageHandlers,
                messageObservers,
                inboundCommandInterceptors,
                defaultCommandHandlers,
                commandObservers,
                outboundPacketInterceptors,
                itemCodecs,
                inboundItemInterceptors,
                defaultItemHandlers,
                itemObservers);
        }

        private ServerFrameworkContext createServerContext(string connectionId, string senderUuid, string sessionId, string sourceExtensionId)
        {
            ServerFrameworkContext context = new ServerFrameworkContext
            {
                ConnectionId = connectionId,
                SenderUuid = senderUuid,
                SessionId = sessionId,
                SourceExtensionId = sourceExtensionId,
                IsConnectionFrameworkCapable = isConnectionFrameworkCapable,
                RemoteCapabilities = getRemoteCapabilities(connectionId),
                ServerCapabilities = serverCapabilities.ToArray(),
                HasRemoteCapability = capability => connectionHasCapability(connectionId, capability),
                ConnectionHasCapability = connectionHasCapability,
                Log = (logMessage, level) => RaiseLogEntry(new LogEventArgs(logMessage, level))
            };

            context.SendMessage = (targetConnectionId, packet) => dispatchOutboundToConnection(context.SourceExtensionId, targetConnectionId, packet);
            context.BroadcastMessage = (packet, excludedConnectionIds) => dispatchOutboundBroadcast(context.SourceExtensionId, packet, excludedConnectionIds);
            return context;
        }

        private static string getExtensionId(object component)
        {
            return (component as IPhinixExtension)?.ExtensionId;
        }

        private void dispatchOutboundToConnection(string sourceExtensionId, string connectionId, FrameworkPacket packet) =>
            DispatchExtensionPacketWithResult(sourceExtensionId, connectionId, packet);

        public PacketDispatchResult DispatchExtensionPacketWithResult(string sourceExtensionId, string connectionId, FrameworkPacket packet)
        {
            if (string.IsNullOrEmpty(connectionId) || packet == null)
                return PacketDispatchResult.Single(connectionId, TransportSendOutcome.NotSent);

            if (!outboundReady) throw new InvalidOperationException("Framework outbound dispatch is unavailable during initialization or after shutdown.");
            return pipelineRunner.DispatchOutboundWithResult(
                packet,
                new ServerOutboundPacketContext
                {
                    SourceExtensionId = sourceExtensionId,
                    TargetConnectionIds = new[] { connectionId },
                    DeliverToConnection = sendPacketDirect,
                    IsConnectionFrameworkCapable = isConnectionFrameworkCapable,
                    ConnectionHasCapability = connectionHasCapability,
                    Log = (logMessage, level) => RaiseLogEntry(new LogEventArgs(logMessage, level))
                }, sendPacketWithOutcome);
        }

        private void dispatchOutboundBroadcast(string sourceExtensionId, FrameworkPacket packet, string[] excludedConnectionIds)
        {
            if (packet == null)
            {
                return;
            }

            if (!outboundReady) throw new InvalidOperationException("Framework outbound dispatch is unavailable during initialization or after shutdown.");
            string[] connectionIds = userManager.GetConnections();
            if (excludedConnectionIds != null)
            {
                connectionIds = connectionIds.Except(excludedConnectionIds).ToArray();
            }

            pipelineRunner.DispatchOutboundWithResult(
                packet,
                new ServerOutboundPacketContext
                {
                    SourceExtensionId = sourceExtensionId,
                    TargetConnectionIds = connectionIds.Where(isConnectionFrameworkCapable).ToArray(),
                    DeliverToConnection = sendPacketDirect,
                    IsConnectionFrameworkCapable = isConnectionFrameworkCapable,
                    ConnectionHasCapability = connectionHasCapability,
                    Log = (logMessage, level) => RaiseLogEntry(new LogEventArgs(logMessage, level))
                }, sendPacketWithOutcome);
        }

        private void sendPacketDirect(string connectionId, FrameworkPacket packet) => sendPacketWithOutcome(connectionId, packet);

        private TransportSendOutcome sendPacketWithOutcome(string connectionId, FrameworkPacket packet)
        {
            if (packet.Flow == global::Phinix.Framework.FrameworkFlow.Message || packet.Kind == FrameworkProtocol.KindMessage)
            {
                packet.Flow = global::Phinix.Framework.FrameworkFlow.Message;
                if (string.IsNullOrEmpty(packet.Kind)) packet.Kind = FrameworkProtocol.KindMessage;
            }
            else if (packet.Flow == global::Phinix.Framework.FrameworkFlow.Command || packet.Kind == FrameworkProtocol.KindCommand)
            {
                packet.Flow = global::Phinix.Framework.FrameworkFlow.Command;
                if (string.IsNullOrEmpty(packet.Kind)) packet.Kind = FrameworkProtocol.KindCommand;
            }
            else if (packet.Flow == global::Phinix.Framework.FrameworkFlow.Item || packet.Kind == FrameworkProtocol.KindItem)
            {
                packet.Flow = global::Phinix.Framework.FrameworkFlow.Item;
                if (string.IsNullOrEmpty(packet.Kind)) packet.Kind = FrameworkProtocol.KindItem;
            }

            byte[] serialized;
            try
            {
                // Envelopes are per-recipient. Never publish the sender's session to another peer.
                if (sessionRegistry.TryGetReady(connectionId, out var target))
                {
                    var outbound = FrameworkSerialization.DeserializePacket(FrameworkSerialization.SerializePacket(packet));
                    outbound.SessionId = target.SessionId;
                    serialized = FrameworkSerialization.SerializePacket(outbound);
                }
                else
                {
                    if ((packet.Kind != FrameworkProtocol.KindCapabilities && packet.Kind != FrameworkAdmissionRejection.PacketKind) ||
                        !authenticator.IsAuthenticated(connectionId, packet.SessionId)) return TransportSendOutcome.NotSent;
                    serialized = FrameworkSerialization.SerializePacket(packet);
                }
            }
            catch { return TransportSendOutcome.NotSent; }
            TransportSendOutcome outcome = packet.CommandRoute?.Mode == CommandProcessingMode.Relay
                ? (netServer as IRelayTransport)?.TrySendRelay(connectionId, FrameworkProtocol.ModuleName, serialized) ?? TransportSendOutcome.NotSent
                : netServer.TrySendWithOutcome(connectionId, FrameworkProtocol.ModuleName, serialized);
            if (outcome != TransportSendOutcome.Submitted)
            {
                try { RaiseLogEntry(new LogEventArgs($"Framework transport outcome={outcome} for packet '{packet.Kind}/{packet.MessageType}' to connection {connectionId.Highlight(HighlightType.ConnectionID)}", LogLevel.ERROR)); } catch { }
            }
            return outcome;
        }

        private sealed class BoundServerInboundMessageInterceptor : IServerInboundMessageInterceptor
        {
            private readonly IServerInboundMessageInterceptor inner;
            private readonly string extensionId;

            public BoundServerInboundMessageInterceptor(IServerInboundMessageInterceptor inner, string extensionId)
            {
                this.inner = inner;
                this.extensionId = extensionId;
            }

            public int Priority => inner.Priority;

            public bool CanInterceptIncomingMessage(FrameworkPacket message) => inner.CanInterceptIncomingMessage(message);

            public ServerIncomingMessageResult InterceptIncomingMessage(FrameworkPacket message, ServerFrameworkContext context)
            {
                context.SourceExtensionId = extensionId;
                return PluginResultAdapter.Normalize(inner.InterceptIncomingMessage(message, context), context);
            }
        }

        private sealed class BoundServerDefaultMessageHandler : IServerDefaultMessageHandler
        {
            private readonly IServerDefaultMessageHandler inner;
            private readonly string extensionId;

            public BoundServerDefaultMessageHandler(IServerDefaultMessageHandler inner, string extensionId)
            {
                this.inner = inner;
                this.extensionId = extensionId;
            }

            public int Priority => inner.Priority;

            public bool CanHandleIncomingMessage(FrameworkPacket message) => inner.CanHandleIncomingMessage(message);

            public ServerIncomingMessageResult HandleIncomingMessage(FrameworkPacket message, ServerFrameworkContext context)
            {
                context.SourceExtensionId = extensionId;
                return PluginResultAdapter.Normalize(inner.HandleIncomingMessage(message, context), context);
            }
        }

        private sealed class BoundServerInboundCommandInterceptor : IServerInboundCommandInterceptor
        {
            private readonly IServerInboundCommandInterceptor inner;
            private readonly string extensionId;

            public BoundServerInboundCommandInterceptor(IServerInboundCommandInterceptor inner, string extensionId)
            {
                this.inner = inner;
                this.extensionId = extensionId;
            }

            public int Priority => inner.Priority;

            public bool CanInterceptIncomingCommand(FrameworkPacket command) => inner.CanInterceptIncomingCommand(command);

            public ServerIncomingCommandResult InterceptIncomingCommand(FrameworkPacket command, ServerFrameworkContext context)
            {
                context.SourceExtensionId = extensionId;
                return PluginResultAdapter.Normalize(inner.InterceptIncomingCommand(command, context), context);
            }
        }

        private sealed class BoundServerDefaultCommandHandler : IServerDefaultCommandHandler
        {
            private readonly IServerDefaultCommandHandler inner;
            private readonly string extensionId;

            public BoundServerDefaultCommandHandler(IServerDefaultCommandHandler inner, string extensionId)
            {
                this.inner = inner;
                this.extensionId = extensionId;
            }

            public int Priority => inner.Priority;

            public bool CanHandleIncomingCommand(FrameworkPacket command) => inner.CanHandleIncomingCommand(command);

            public ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ServerFrameworkContext context)
            {
                context.SourceExtensionId = extensionId;
                return PluginResultAdapter.Normalize(inner.HandleIncomingCommand(command, context), context);
            }
        }

        private sealed class BoundServerMessageObserver : IServerMessageObserver
        {
            private readonly IServerMessageObserver inner;
            private readonly string extensionId;

            public BoundServerMessageObserver(IServerMessageObserver inner, string extensionId)
            {
                this.inner = inner;
                this.extensionId = extensionId;
            }

            public int Priority => inner.Priority;

            public bool CanObserveIncomingMessage(FrameworkPacket message) => inner.CanObserveIncomingMessage(message);

            public void ObserveIncomingMessage(FrameworkPacket message, ServerFrameworkContext context, MessageHandlingResultAction terminalAction)
            {
                context.SourceExtensionId = extensionId;
                inner.ObserveIncomingMessage(message, context, terminalAction);
            }
        }

        private sealed class BoundServerCommandObserver : IServerCommandObserver
        {
            private readonly IServerCommandObserver inner;
            private readonly string extensionId;

            public BoundServerCommandObserver(IServerCommandObserver inner, string extensionId)
            {
                this.inner = inner;
                this.extensionId = extensionId;
            }

            public int Priority => inner.Priority;

            public bool CanObserveIncomingCommand(FrameworkPacket command) => inner.CanObserveIncomingCommand(command);

            public void ObserveIncomingCommand(FrameworkPacket command, ServerFrameworkContext context, MessageHandlingResultAction terminalAction)
            {
                context.SourceExtensionId = extensionId;
                inner.ObserveIncomingCommand(command, context, terminalAction);
            }
        }

        private sealed class BoundServerInboundItemInterceptor : IServerInboundItemInterceptor
        {
            private readonly IServerInboundItemInterceptor inner;
            private readonly string extensionId;

            public BoundServerInboundItemInterceptor(IServerInboundItemInterceptor inner, string extensionId)
            {
                this.inner = inner;
                this.extensionId = extensionId;
            }

            public int Priority => inner.Priority;

            public bool CanInterceptIncomingItem(FrameworkPacket itemPacket) => inner.CanInterceptIncomingItem(itemPacket);

            public ServerIncomingItemResult InterceptIncomingItem(FrameworkPacket itemPacket, ServerFrameworkContext context)
            {
                context.SourceExtensionId = extensionId;
                return PluginResultAdapter.Normalize(inner.InterceptIncomingItem(itemPacket, context), context);
            }
        }

        private sealed class BoundServerDefaultItemHandler : IServerDefaultItemHandler
        {
            private readonly IServerDefaultItemHandler inner;
            private readonly string extensionId;

            public BoundServerDefaultItemHandler(IServerDefaultItemHandler inner, string extensionId)
            {
                this.inner = inner;
                this.extensionId = extensionId;
            }

            public int Priority => inner.Priority;

            public bool CanHandleIncomingItem(FrameworkPacket itemPacket) => inner.CanHandleIncomingItem(itemPacket);

            public ServerIncomingItemResult HandleIncomingItem(FrameworkPacket itemPacket, ServerFrameworkContext context, IReadOnlyList<IItemCodec> codecs)
            {
                context.SourceExtensionId = extensionId;
                return PluginResultAdapter.Normalize(inner.HandleIncomingItem(itemPacket, context, codecs), context);
            }
        }

        private sealed class BoundServerItemObserver : IServerItemObserver
        {
            private readonly IServerItemObserver inner;
            private readonly string extensionId;

            public BoundServerItemObserver(IServerItemObserver inner, string extensionId)
            {
                this.inner = inner;
                this.extensionId = extensionId;
            }

            public int Priority => inner.Priority;

            public bool CanObserveIncomingItem(FrameworkPacket itemPacket) => inner.CanObserveIncomingItem(itemPacket);

            public void ObserveIncomingItem(FrameworkPacket itemPacket, ServerFrameworkContext context, ItemHandlingResultAction terminalAction)
            {
                context.SourceExtensionId = extensionId;
                inner.ObserveIncomingItem(itemPacket, context, terminalAction);
            }
        }
    }
}
