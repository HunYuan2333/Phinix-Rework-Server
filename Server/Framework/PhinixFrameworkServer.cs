using System;
using System.Collections.Generic;
using System.Linq;
using Authentication;
using Connections;
using UserManagement;
using Utils;
using Utils.Framework;

namespace PhinixServer.Framework
{
    public class PhinixFrameworkServer : ILoggable
    {
        public event EventHandler<LogEventArgs> OnLogEntry;

        public void RaiseLogEntry(LogEventArgs e) => OnLogEntry?.Invoke(this, e);

        private readonly NetServer netServer;
        private readonly ServerAuthenticator authenticator;
        private readonly ServerUserManager userManager;
        private readonly ExtensionHostContext extensionHostContext;
        private readonly DiscoveredPhinixExtensions discoveredExtensions;
        private readonly ServerPipelineRunner pipelineRunner;
        private readonly HashSet<string> serverCapabilities;
        private readonly Dictionary<string, HashSet<string>> connectionCapabilities = new Dictionary<string, HashSet<string>>();
        private readonly object connectionCapabilitiesLock = new object();

        public PhinixFrameworkServer(NetServer netServer, ServerAuthenticator authenticator, ServerUserManager userManager, ExtensionHostContext extensionHostContext = null)
        {
            this.netServer = netServer;
            this.authenticator = authenticator;
            this.userManager = userManager;
            this.extensionHostContext = extensionHostContext ?? ExtensionHostContext.Empty;
            this.discoveredExtensions = PhinixExtensionRegistry.DiscoverExtensions(this.extensionHostContext);
            this.pipelineRunner = new ServerPipelineRunner();
            this.serverCapabilities = new HashSet<string>(PhinixExtensionRegistry.CollectCapabilities(discoveredExtensions), StringComparer.OrdinalIgnoreCase);
            PhinixExtensionRegistry.ActivateExtensions(discoveredExtensions, this.extensionHostContext);
            LoadExtensionState();
            hydratePipelineRunner();

            netServer.RegisterPacketHandler(FrameworkProtocol.ModuleName, packetHandler);
            netServer.OnConnectionClosed += (_, args) =>
            {
                lock (connectionCapabilitiesLock)
                {
                    connectionCapabilities.Remove(args.ConnectionId);
                }
            };

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
            foreach (ExtensionPersistenceRegistration registration in extensionHostContext.Persistents)
            {
                string path = extensionHostContext.GetStoragePath(registration.ExtensionId, registration.LogicalName);
                if (string.IsNullOrEmpty(path))
                {
                    RaiseLogEntry(new LogEventArgs(
                        $"Skipped loading persistent state '{registration.ExtensionId}/{registration.LogicalName}' because no storage path was available.",
                        LogLevel.WARNING));
                    continue;
                }

                registration.Persistent.Load(path);
            }
        }

        public void SaveExtensionState()
        {
            foreach (ExtensionPersistenceRegistration registration in extensionHostContext.Persistents)
            {
                string path = extensionHostContext.GetStoragePath(registration.ExtensionId, registration.LogicalName);
                if (string.IsNullOrEmpty(path))
                {
                    RaiseLogEntry(new LogEventArgs(
                        $"Skipped saving persistent state '{registration.ExtensionId}/{registration.LogicalName}' because no storage path was available.",
                        LogLevel.WARNING));
                    continue;
                }

                registration.Persistent.Save(path);
            }
        }

        public void ShutdownExtensions()
        {
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

        private void packetHandler(string module, string connectionId, byte[] data)
        {
            FrameworkPacket packet;
            try
            {
                packet = FrameworkSerialization.DeserializePacket(data);
            }
            catch (Exception exception)
            {
                RaiseLogEntry(new LogEventArgs($"Failed to deserialize framework packet from {connectionId.Highlight(HighlightType.ConnectionID)}: {exception.Message}", LogLevel.WARNING));
                return;
            }

            switch (packet.Kind)
            {
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
                default:
                    RaiseLogEntry(new LogEventArgs($"Received unsupported framework packet kind '{packet.Kind}'", LogLevel.DEBUG));
                    break;
            }
        }

        private void handleHello(string connectionId, FrameworkPacket packet)
        {
            if (!authenticator.IsAuthenticated(connectionId, packet.SessionId) || !userManager.IsLoggedIn(connectionId, packet.SenderUuid))
            {
                RaiseLogEntry(new LogEventArgs($"Rejected framework hello from unauthenticated connection {connectionId.Highlight(HighlightType.ConnectionID)}", LogLevel.WARNING));
                return;
            }

            FrameworkCapabilitiesPayload payload = FrameworkSerialization.DeserializePayload<FrameworkCapabilitiesPayload>(packet.PayloadJson);
            lock (connectionCapabilitiesLock)
            {
                connectionCapabilities[connectionId] = new HashSet<string>(payload.Capabilities ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            }

            FrameworkPacket response = new FrameworkPacket
            {
                Kind = FrameworkProtocol.KindCapabilities,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                SessionId = packet.SessionId,
                PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkCapabilitiesPayload
                {
                    Capabilities = serverCapabilities.OrderBy(capability => capability).ToList()
                })
            };

            sendPacketDirect(connectionId, response);
        }

        private void handleMessage(string connectionId, FrameworkPacket message)
        {
            if (!authenticator.IsAuthenticated(connectionId, message.SessionId) || !userManager.IsLoggedIn(connectionId, message.SenderUuid))
            {
                RaiseLogEntry(new LogEventArgs($"Rejected framework message '{message.MessageType}' from unauthenticated connection {connectionId.Highlight(HighlightType.ConnectionID)}", LogLevel.WARNING));
                return;
            }

            if (!connectionHasCapability(connectionId, message.MessageType))
            {
                RaiseLogEntry(new LogEventArgs($"Rejected framework message '{message.MessageType}' from connection {connectionId.Highlight(HighlightType.ConnectionID)} because the capability was not negotiated.", LogLevel.WARNING));
                return;
            }

            if (!pipelineRunner.ProcessIncomingMessage(message, createServerContext(connectionId, message.SenderUuid, message.SessionId, null)))
            {
                RaiseLogEntry(new LogEventArgs($"No server framework handler registered for message type '{message.MessageType}'.", LogLevel.WARNING));
            }
        }

        private void handleCommand(string connectionId, FrameworkPacket command)
        {
            RaiseLogEntry(new LogEventArgs($"Received framework command '{command.MessageType}' flow={command.Flow} kind={command.Kind} from {connectionId}", LogLevel.INFO));

            if (!authenticator.IsAuthenticated(connectionId, command.SessionId) || !userManager.IsLoggedIn(connectionId, command.SenderUuid))
            {
                RaiseLogEntry(new LogEventArgs(
                    $"Rejected framework command '{command.MessageType}' from unauthenticated connection {connectionId.Highlight(HighlightType.ConnectionID)} " +
                    $"(session='{command.SessionId ?? "<null>"}', sender='{command.SenderUuid ?? "<null>"}')",
                    LogLevel.WARNING));
                return;
            }

            if (!connectionHasCapability(connectionId, command.MessageType))
            {
                RaiseLogEntry(new LogEventArgs($"Rejected framework command '{command.MessageType}' from connection {connectionId.Highlight(HighlightType.ConnectionID)} because the capability was not negotiated.", LogLevel.WARNING));
                return;
            }

            if (!pipelineRunner.ProcessIncomingCommand(command, createServerContext(connectionId, command.SenderUuid, command.SessionId, null)))
            {
                RaiseLogEntry(new LogEventArgs($"No server framework command handler registered for command type '{command.MessageType}'.", LogLevel.WARNING));
            }
            else
            {
                RaiseLogEntry(new LogEventArgs($"Framework command '{command.MessageType}' processed successfully.", LogLevel.INFO));
            }
        }

        private bool isConnectionFrameworkCapable(string connectionId)
        {
            lock (connectionCapabilitiesLock)
            {
                return connectionCapabilities.ContainsKey(connectionId);
            }
        }

        private string[] getRemoteCapabilities(string connectionId)
        {
            lock (connectionCapabilitiesLock)
            {
                if (!connectionCapabilities.TryGetValue(connectionId, out HashSet<string> capabilities))
                {
                    return Array.Empty<string>();
                }

                return capabilities.OrderBy(capability => capability).ToArray();
            }
        }

        private bool connectionHasCapability(string connectionId, string capability)
        {
            if (string.IsNullOrEmpty(capability)) return isConnectionFrameworkCapable(connectionId);

            lock (connectionCapabilitiesLock)
            {
                return connectionCapabilities.TryGetValue(connectionId, out HashSet<string> capabilities) && capabilities.Contains(capability);
            }
        }

        private void hydratePipelineRunner()
        {
            foreach (IServerInboundMessageInterceptor interceptor in discoveredExtensions.ServerInboundMessageInterceptors)
            {
                pipelineRunner.InboundMessageInterceptors.Add(new BoundServerInboundMessageInterceptor(interceptor, getExtensionId(interceptor)));
            }

            foreach (IServerDefaultMessageHandler handler in discoveredExtensions.ServerDefaultMessageHandlers)
            {
                pipelineRunner.DefaultMessageHandlers.Add(new BoundServerDefaultMessageHandler(handler, getExtensionId(handler)));
            }

            foreach (IServerMessageObserver observer in discoveredExtensions.ServerMessageObservers)
            {
                pipelineRunner.MessageObservers.Add(new BoundServerMessageObserver(observer, getExtensionId(observer)));
            }

            foreach (IServerInboundCommandInterceptor interceptor in discoveredExtensions.ServerInboundCommandInterceptors)
            {
                pipelineRunner.InboundCommandInterceptors.Add(new BoundServerInboundCommandInterceptor(interceptor, getExtensionId(interceptor)));
            }

            foreach (IServerDefaultCommandHandler handler in discoveredExtensions.ServerDefaultCommandHandlers)
            {
                pipelineRunner.DefaultCommandHandlers.Add(new BoundServerDefaultCommandHandler(handler, getExtensionId(handler)));
            }

            foreach (IServerCommandObserver observer in discoveredExtensions.ServerCommandObservers)
            {
                pipelineRunner.CommandObservers.Add(new BoundServerCommandObserver(observer, getExtensionId(observer)));
            }

            foreach (IServerOutboundPacketInterceptor interceptor in discoveredExtensions.ServerOutboundPacketInterceptors)
            {
                pipelineRunner.OutboundPacketInterceptors.Add(interceptor);
            }
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

        private void dispatchOutboundToConnection(string sourceExtensionId, string connectionId, FrameworkPacket packet)
        {
            if (string.IsNullOrEmpty(connectionId) || packet == null)
            {
                return;
            }

            pipelineRunner.DispatchOutbound(
                packet,
                new ServerOutboundPacketContext
                {
                    SourceExtensionId = sourceExtensionId,
                    TargetConnectionIds = new[] { connectionId },
                    DeliverToConnection = sendPacketDirect,
                    IsConnectionFrameworkCapable = isConnectionFrameworkCapable,
                    ConnectionHasCapability = connectionHasCapability,
                    Log = (logMessage, level) => RaiseLogEntry(new LogEventArgs(logMessage, level))
                });
        }

        private void dispatchOutboundBroadcast(string sourceExtensionId, FrameworkPacket packet, string[] excludedConnectionIds)
        {
            if (packet == null)
            {
                return;
            }

            string[] connectionIds = userManager.GetConnections();
            if (excludedConnectionIds != null)
            {
                connectionIds = connectionIds.Except(excludedConnectionIds).ToArray();
            }

            pipelineRunner.DispatchOutbound(
                packet,
                new ServerOutboundPacketContext
                {
                    SourceExtensionId = sourceExtensionId,
                    TargetConnectionIds = connectionIds.Where(isConnectionFrameworkCapable).ToArray(),
                    DeliverToConnection = sendPacketDirect,
                    IsConnectionFrameworkCapable = isConnectionFrameworkCapable,
                    ConnectionHasCapability = connectionHasCapability,
                    Log = (logMessage, level) => RaiseLogEntry(new LogEventArgs(logMessage, level))
                });
        }

        private void sendPacketDirect(string connectionId, FrameworkPacket packet)
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

            if (!netServer.TrySend(connectionId, FrameworkProtocol.ModuleName, FrameworkSerialization.SerializePacket(packet)))
            {
                RaiseLogEntry(new LogEventArgs($"Failed to send framework packet '{packet.Kind}/{packet.MessageType}' to connection {connectionId.Highlight(HighlightType.ConnectionID)}", LogLevel.ERROR));
            }
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
                return inner.InterceptIncomingMessage(message, context);
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
                return inner.HandleIncomingMessage(message, context);
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
                return inner.InterceptIncomingCommand(command, context);
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
                return inner.HandleIncomingCommand(command, context);
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
    }
}
