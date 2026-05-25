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
            this.serverCapabilities = new HashSet<string>(PhinixExtensionRegistry.CollectCapabilities(discoveredExtensions), StringComparer.OrdinalIgnoreCase);
            PhinixExtensionRegistry.ActivateExtensions(discoveredExtensions, this.extensionHostContext);

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

            sendPacket(connectionId, response);
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

            bool matchedHandler = false;
            FrameworkPacket currentMessage = message;

            foreach (IServerMessageHandler handler in discoveredExtensions.ServerMessageHandlers.Where(handler => handler.CanHandleIncomingMessage(currentMessage)))
            {
                matchedHandler = true;
                ServerIncomingMessageResult result = handler.HandleIncomingMessage(
                    currentMessage,
                    new ServerFrameworkContext
                    {
                        ConnectionId = connectionId,
                        SenderUuid = currentMessage.SenderUuid,
                        SessionId = currentMessage.SessionId,
                        SendMessage = sendPacket,
                        BroadcastMessage = broadcastPacket,
                        IsConnectionFrameworkCapable = isConnectionFrameworkCapable,
                        RemoteCapabilities = getRemoteCapabilities(connectionId),
                        ServerCapabilities = serverCapabilities.ToArray(),
                        HasRemoteCapability = capability => connectionHasCapability(connectionId, capability),
                        ConnectionHasCapability = connectionHasCapability,
                        Log = (logMessage, level) => RaiseLogEntry(new LogEventArgs(logMessage, level))
                    }
                );

                if (result == null) continue;

                if (result.Action == MessageHandlingResultAction.ReplacePayload && result.Message != null)
                {
                    currentMessage = result.Message;
                    continue;
                }

                if (result.Action != MessageHandlingResultAction.Continue)
                {
                    return;
                }
            }

            if (!matchedHandler)
            {
                RaiseLogEntry(new LogEventArgs($"No server framework handler registered for message type '{currentMessage.MessageType}'.", LogLevel.WARNING));
            }
        }

        private void handleCommand(string connectionId, FrameworkPacket command)
        {
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

            bool matchedHandler = false;
            FrameworkPacket currentCommand = command;

            foreach (IServerCommandHandler handler in discoveredExtensions.ServerCommandHandlers.Where(handler => handler.CanHandleIncomingCommand(currentCommand)))
            {
                matchedHandler = true;
                ServerIncomingCommandResult result = handler.HandleIncomingCommand(
                    currentCommand,
                    new ServerFrameworkContext
                    {
                        ConnectionId = connectionId,
                        SenderUuid = currentCommand.SenderUuid,
                        SessionId = currentCommand.SessionId,
                        SendMessage = sendPacket,
                        BroadcastMessage = broadcastPacket,
                        IsConnectionFrameworkCapable = isConnectionFrameworkCapable,
                        RemoteCapabilities = getRemoteCapabilities(connectionId),
                        ServerCapabilities = serverCapabilities.ToArray(),
                        HasRemoteCapability = capability => connectionHasCapability(connectionId, capability),
                        ConnectionHasCapability = connectionHasCapability,
                        Log = (logMessage, level) => RaiseLogEntry(new LogEventArgs(logMessage, level))
                    }
                );

                if (result == null) continue;

                if (result.Action == MessageHandlingResultAction.ReplacePayload && result.Command != null)
                {
                    currentCommand = result.Command;
                    continue;
                }

                if (result.Action != MessageHandlingResultAction.Continue)
                {
                    return;
                }
            }

            if (!matchedHandler)
            {
                RaiseLogEntry(new LogEventArgs($"No server framework command handler registered for command type '{currentCommand.MessageType}'.", LogLevel.WARNING));
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

        private void sendPacket(string connectionId, FrameworkPacket packet)
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

        private void broadcastPacket(FrameworkPacket packet, string[] excludedConnectionIds)
        {
            string[] connectionIds = userManager.GetConnections();
            if (excludedConnectionIds != null)
            {
                connectionIds = connectionIds.Except(excludedConnectionIds).ToArray();
            }

            foreach (string connectionId in connectionIds.Where(isConnectionFrameworkCapable))
            {
                if (!string.IsNullOrEmpty(packet?.MessageType) && !connectionHasCapability(connectionId, packet.MessageType)) continue;
                sendPacket(connectionId, packet);
            }
        }
    }
}
