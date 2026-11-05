using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Connections;
using Phinix.ChatExtension.Server;
using ServerRuntime;
using UserManagement;
using Utils;
using Utils.Framework;
using Message = Phinix.Framework.BuiltInChatMessagePayload;

namespace Phinix.LegacyAdapter.Server
{
    public sealed class OriginalChatAdapter : IDisposable
    {
        private sealed class Peer
        {
            public string Principal;
            public bool HistorySent, Confirmed;
            public readonly Dictionary<string, (OriginalChatCodec.Request Request, Message Result)> Results = new(StringComparer.Ordinal);
        }
        private readonly IServerTransport transport;
        private readonly IServerUserManager users;
        private readonly IServerPrincipalAuthorization authorization;
        private readonly IServerSessionReadiness readiness;
        private readonly IFrameworkChatServerApi chat;
        private readonly IFrameworkChatMessageEvents events;
        private readonly Action<Message> publish;
        private readonly Action<string, LogLevel> log;
        private readonly Dictionary<string, Peer> peers = new(StringComparer.Ordinal);
        private readonly object gate = new();
        private readonly object lifetime = new();
        private bool active;

        public OriginalChatAdapter(IServerTransport transport, IServerUserManager users, IServerPrincipalAuthorization authorization,
            IServerSessionReadiness readiness, IFrameworkChatServerApi chat, Action<Message> publish, Action<string, LogLevel> log = null)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.users = users ?? throw new ArgumentNullException(nameof(users));
            this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
            this.readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
            this.chat = chat ?? throw new ArgumentNullException(nameof(chat));
            events = chat as IFrameworkChatMessageEvents ?? throw new InvalidOperationException("Chat message event API unavailable");
            this.publish = publish ?? throw new ArgumentNullException(nameof(publish)); this.log = log;
        }
        public void Start()
        {
            lock (lifetime)
            {
                lock (gate) if (active) throw new InvalidOperationException("OriginalChatAlreadyStarted");
                // This compatibility profile is explicitly scoped to the established UDP endpoint.
                if (transport is IAuthenticatedServerTransport) throw new NotSupportedException("OriginalChatRequiresUdpProfile");
                transport.RegisterPacketHandler(OriginalChatCodec.Module, Receive);
                lock (gate) active = true;
                try { users.OnLogin += Login; transport.OnConnectionClosed += Closed; events.MessageStored += Stored; }
                catch { Dispose(); throw; }
            }
        }
        private void Report(string reason)
        { try { log?.Invoke(reason, LogLevel.WARNING); } catch { } }
        private bool Modern(string connection) => readiness.TryGetReady(connection, out _);
        private bool Send(string connection, byte[] packet)
        {
            try
            {
                if (transport.TrySendWithOutcome(connection, OriginalChatCodec.Module, packet) == TransportSendOutcome.Submitted) return true;
            }
            catch { }
            Report("OriginalChatSendUnconfirmed"); return false;
        }
        private Message ProjectDisplayText(Message message)
        {
            string ReplyAuthor(string id)
            {
                if (chat is IChatMessageNameLookup names && names.TryGetDisplayNameAtSend(id, out var historical)) return historical;
                var parent = chat.GetHistory().FirstOrDefault(m => m.MessageId == id);
                if (parent != null && users.TryGetDisplayName(parent.SenderUuid, out var current) && !string.IsNullOrWhiteSpace(current))
                    return TextHelper.StripRichText(current) + " (current name)";
                return "unknown author";
            }
            string MentionName(string id) => users.TryGetDisplayName(id, out var current) && !string.IsNullOrWhiteSpace(current)
                ? TextHelper.StripRichText(current) + " (current name)" : "unknown user";
            return OriginalChatTextProjection.Project(message, ReplyAuthor, MentionName);
        }
        private bool History(string connection, Peer peer)
        {
            if (peer.HistorySent) return true;
            try { peer.HistorySent = Send(connection, OriginalChatCodec.History(chat.GetHistory().Select(ProjectDisplayText))); }
            catch (InvalidDataException) { Report("OriginalChatHistoryUnrepresentable"); }
            return peer.HistorySent;
        }
        private void Login(object sender, ServerLoginEventArgs args)
        {
            lock (gate)
            {
                if (!active || string.IsNullOrEmpty(args.ConnectionId) || string.IsNullOrEmpty(args.Uuid)) return;
                if (peers.TryGetValue(args.ConnectionId, out var existing) && existing.Principal == args.Uuid) return;
                var peer = new Peer { Principal = args.Uuid }; peers[args.ConnectionId] = peer;
                // Original clients have no Hello/history request. An opted-in endpoint sends
                // a text-projected history on legacy authentication login; Framework peers
                // may ignore it until Ready. Never synthesize empty history. Failed bootstrap
                // is reported once here and does not block independently valid new messages.
                if (!Modern(args.ConnectionId)) History(args.ConnectionId, peer);
            }
        }
        private void Closed(object sender, ConnectionEventArgs args) { lock (gate) peers.Remove(args.ConnectionId); }
        private void Receive(string module, string connection, byte[] bytes)
        {
            lock (gate)
            {
                if (!active || module != OriginalChatCodec.Module) return;
                OriginalChatCodec.Request request;
                try { request = OriginalChatCodec.DecodeRequest(bytes); }
                catch (Exception error) when (error is InvalidDataException || error is Google.Protobuf.InvalidProtocolBufferException || error is ArgumentException)
                { Report("OriginalChatMalformedRequest"); return; }
                bool permitted;
                try { permitted = !Modern(connection) && authorization.IsAuthenticatedPrincipal(connection, request.SessionId, request.PrincipalId); }
                catch { permitted = false; }
                if (!permitted)
                { Send(connection, OriginalChatCodec.Response(request.OriginalId)); Report("OriginalChatUnauthorizedOrProfileConflict"); return; }
                if (!peers.TryGetValue(connection, out var peer) || peer.Principal != request.PrincipalId)
                { Send(connection, OriginalChatCodec.Response(request.OriginalId)); Report("OriginalChatLoginRequired"); return; }
                if (peer.Results.TryGetValue(request.OriginalId, out var prior))
                {
                    if (prior.Request == request) Send(connection, OriginalChatCodec.Response(request.OriginalId, prior.Result));
                    else { Send(connection, OriginalChatCodec.Response(request.OriginalId)); Report("OriginalChatOperationConflict"); }
                    return;
                }
                if (peer.Results.Count >= 256)
                { Send(connection, OriginalChatCodec.Response(request.OriginalId)); Report("OriginalChatCapacityRefused"); return; }
                peer.Confirmed = true;
                // A genuine result from the same service/history as Framework traffic.
                var stored = chat.AddMessage(request.PrincipalId, request.Text);
                peer.Results.Add(request.OriginalId, (request, stored.Clone()));
                Send(connection, OriginalChatCodec.Response(request.OriginalId, stored));
                // Transport failure here never changes the original accepted result.
                try { publish(stored.Clone()); } catch { Report("OriginalChatFrameworkPublicationFailed"); }
            }
        }
        private void Stored(Message message)
        {
            lock (gate)
            {
                if (!active) return;
                var targets = peers.ToArray().Where(entry => entry.Value.Principal != message.SenderUuid &&
                    !Modern(entry.Key) && users.IsLoggedIn(entry.Key, entry.Value.Principal)).ToArray();
                if (targets.Length == 0) return;
                byte[] encoded = null;
                try { encoded = OriginalChatCodec.Broadcast(ProjectDisplayText(message)); }
                catch (InvalidDataException) { Report("OriginalChatMessageUnrepresentable"); }
                foreach (var entry in targets)
                {
                    string connection = entry.Key; var peer = entry.Value;
                    if (encoded == null)
                    {
                        // An identified original peer cannot receive replies/mentions as plain
                        // text without losing semantics. Explicitly close that unsupported route.
                        if (peer.Confirmed) transport.DisconnectPeer(connection);
                        continue;
                    }
                    // A failed history bootstrap is not an authorization or live-message gate.
                    Send(connection, encoded);
                }
            }
        }
        public void Dispose()
        {
            lock (lifetime)
            {
                lock (gate)
                {
                    if (!active) return;
                    active = false; peers.Clear();
                }
                // NetCommon dispatch holds its handler lock before entering Receive.
                // Never unregister under our gate: that reverses the lock order.
                try { events.MessageStored -= Stored; }
                finally
                {
                    try { users.OnLogin -= Login; }
                    finally { try { transport.OnConnectionClosed -= Closed; } finally { transport.UnregisterPacketHandler(OriginalChatCodec.Module); } }
                }
            }
        }
    }
}
