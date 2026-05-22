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
        private readonly DiscoveredPhinixExtensions discoveredExtensions;
        private readonly HashSet<string> serverCapabilities;
        private readonly Dictionary<string, HashSet<string>> connectionCapabilities = new Dictionary<string, HashSet<string>>();
        private readonly object connectionCapabilitiesLock = new object();

        public PhinixFrameworkServer(NetServer netServer, ServerAuthenticator authenticator, ServerUserManager userManager)
        {
            this.netServer = netServer;
            this.authenticator = authenticator;
            this.userManager = userManager;
            this.discoveredExtensions = PhinixExtensionRegistry.DiscoverExtensions();
            this.serverCapabilities = new HashSet<string>(PhinixExtensionRegistry.CollectCapabilities(discoveredExtensions), StringComparer.OrdinalIgnoreCase);

            netServer.RegisterPacketHandler(FrameworkProtocol.ModuleName, packetHandler);
            netServer.OnConnectionClosed += (_, args) =>
            {
                lock (connectionCapabilitiesLock)
                {
                    connectionCapabilities.Remove(args.ConnectionId);
                }
            };

            RaiseLogEntry(new LogEventArgs($"Discovered {discoveredExtensions.Extensions.Count} framework extension(s) and {serverCapabilities.Count} capability/capabilities."));
            foreach (string warning in discoveredExtensions.Warnings)
            {
                RaiseLogEntry(new LogEventArgs(warning, LogLevel.WARNING));
            }
        }

        private void packetHandler(string module, string connectionId, byte[] data)
        {
            FrameworkEnvelope envelope;
            try
            {
                envelope = FrameworkSerialization.DeserializeEnvelope(data);
            }
            catch (Exception exception)
            {
                RaiseLogEntry(new LogEventArgs($"Failed to deserialize framework envelope from {connectionId.Highlight(HighlightType.ConnectionID)}: {exception.Message}", LogLevel.WARNING));
                return;
            }

            switch (envelope.Kind)
            {
                case FrameworkProtocol.KindHello:
                    handleHello(connectionId, envelope);
                    break;
                case FrameworkProtocol.KindExtension:
                    handleExtension(connectionId, envelope);
                    break;
                default:
                    RaiseLogEntry(new LogEventArgs($"Received unsupported framework envelope kind '{envelope.Kind}'", LogLevel.DEBUG));
                    break;
            }
        }

        private void handleHello(string connectionId, FrameworkEnvelope envelope)
        {
            if (!authenticator.IsAuthenticated(connectionId, envelope.SessionId) || !userManager.IsLoggedIn(connectionId, envelope.SenderUuid))
            {
                RaiseLogEntry(new LogEventArgs($"Rejected framework hello from unauthenticated connection {connectionId.Highlight(HighlightType.ConnectionID)}", LogLevel.WARNING));
                return;
            }

            FrameworkCapabilitiesPayload payload = FrameworkSerialization.DeserializePayload<FrameworkCapabilitiesPayload>(envelope.PayloadJson);
            lock (connectionCapabilitiesLock)
            {
                connectionCapabilities[connectionId] = new HashSet<string>(payload.Capabilities ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            }

            FrameworkEnvelope response = new FrameworkEnvelope
            {
                Kind = FrameworkProtocol.KindCapabilities,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                SessionId = envelope.SessionId,
                PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkCapabilitiesPayload
                {
                    Capabilities = serverCapabilities.OrderBy(capability => capability).ToList()
                })
            };

            sendEnvelope(connectionId, response);
        }

        private void handleExtension(string connectionId, FrameworkEnvelope envelope)
        {
            if (!authenticator.IsAuthenticated(connectionId, envelope.SessionId) || !userManager.IsLoggedIn(connectionId, envelope.SenderUuid))
            {
                RaiseLogEntry(new LogEventArgs($"Rejected framework message '{envelope.MessageType}' from unauthenticated connection {connectionId.Highlight(HighlightType.ConnectionID)}", LogLevel.WARNING));
                return;
            }

            if (!connectionHasCapability(connectionId, envelope.MessageType))
            {
                RaiseLogEntry(new LogEventArgs($"Rejected framework message '{envelope.MessageType}' from connection {connectionId.Highlight(HighlightType.ConnectionID)} because the capability was not negotiated.", LogLevel.WARNING));
                return;
            }

            bool matchedHandler = false;
            FrameworkEnvelope currentEnvelope = envelope;

            foreach (IServerMessageHandler handler in discoveredExtensions.ServerMessageHandlers.Where(handler => handler.CanHandleIncomingEnvelope(currentEnvelope)))
            {
                matchedHandler = true;
                ServerIncomingMessageResult result = handler.HandleIncomingEnvelope(
                    currentEnvelope,
                    new ServerFrameworkContext
                    {
                        ConnectionId = connectionId,
                        SenderUuid = currentEnvelope.SenderUuid,
                        SessionId = currentEnvelope.SessionId,
                        SendEnvelope = sendEnvelope,
                        BroadcastEnvelope = broadcastEnvelope,
                        IsConnectionFrameworkCapable = isConnectionFrameworkCapable,
                        RemoteCapabilities = getRemoteCapabilities(connectionId),
                        ServerCapabilities = serverCapabilities.ToArray(),
                        HasRemoteCapability = capability => connectionHasCapability(connectionId, capability),
                        ConnectionHasCapability = connectionHasCapability,
                        Log = (message, level) => RaiseLogEntry(new LogEventArgs(message, level))
                    }
                );

                if (result == null) continue;

                if (result.Action == MessageHandlingResultAction.ReplacePayload && result.Envelope != null)
                {
                    currentEnvelope = result.Envelope;
                    continue;
                }

                if (result.Action != MessageHandlingResultAction.Continue)
                {
                    return;
                }
            }

            if (!matchedHandler)
            {
                RaiseLogEntry(new LogEventArgs($"No server framework handler registered for message type '{currentEnvelope.MessageType}'.", LogLevel.WARNING));
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

        private void sendEnvelope(string connectionId, FrameworkEnvelope envelope)
        {
            if (!netServer.TrySend(connectionId, FrameworkProtocol.ModuleName, FrameworkSerialization.SerializeEnvelope(envelope)))
            {
                RaiseLogEntry(new LogEventArgs($"Failed to send framework envelope '{envelope.Kind}/{envelope.MessageType}' to connection {connectionId.Highlight(HighlightType.ConnectionID)}", LogLevel.ERROR));
            }
        }

        private void broadcastEnvelope(FrameworkEnvelope envelope, string[] excludedConnectionIds)
        {
            string[] connectionIds = userManager.GetConnections();
            if (excludedConnectionIds != null)
            {
                connectionIds = connectionIds.Except(excludedConnectionIds).ToArray();
            }

            foreach (string connectionId in connectionIds.Where(isConnectionFrameworkCapable))
            {
                if (!string.IsNullOrEmpty(envelope?.MessageType) && !connectionHasCapability(connectionId, envelope.MessageType)) continue;
                sendEnvelope(connectionId, envelope);
            }
        }
    }
}
