using System;
using System.Collections.Concurrent;
using System.IO;
using Connections;
using Google.Protobuf;
using Phinix.TradeExtension;
using Phinix.TradeExtension.Server;
using ServerRuntime;
using UserManagement;
using Utils;
using Utils.Framework;

namespace Phinix.LegacyAdapter.Server;

public sealed class OriginalTradeAdapter : ITradeEndpointRouter, IDisposable
{
    private readonly IServerTransport transport;
    private readonly IServerUserManager users;
    private readonly IServerPrincipalAuthorization authorization;
    private readonly IServerAuthenticatedSessionLookup sessions;
    private readonly IServerSessionReadiness readiness;
    private readonly IFrameworkTradeServerApi trade;
    private readonly Action<string, FrameworkPacket> normalSend;
    private readonly Action<string, LogLevel> log;
    private readonly ConcurrentDictionary<string, TradeEndpointSession> peers = new(StringComparer.Ordinal);
    private sealed class PendingOffer(OriginalTradeOfferCodec.Request request)
    {
        internal readonly OriginalTradeOfferCodec.Request Request = request;
        internal bool ResultObserved;
    }
    private readonly ConcurrentDictionary<(string, string), PendingOffer> pendingOffers = new();
    private readonly object lifetime = new();
    private IDisposable endpointLease;
    private volatile bool active;
    public OriginalTradeAdapter(IServerTransport transport, IServerUserManager users, IServerPrincipalAuthorization authorization,
        IServerAuthenticatedSessionLookup sessions, IServerSessionReadiness readiness, IFrameworkTradeServerApi trade,
        Action<string, FrameworkPacket> normalSend, Action<string, LogLevel> log = null)
    {
        this.transport = transport; this.users = users; this.authorization = authorization; this.sessions = sessions;
        this.readiness = readiness; this.trade = trade; this.normalSend = normalSend; this.log = log;
    }
    public void Start()
    {
        lock (lifetime)
        {
            if (active) throw new InvalidOperationException("OriginalTradeAlreadyStarted");
            if (transport is IAuthenticatedServerTransport) throw new NotSupportedException("OriginalTradeRequiresUdpProfile");
            endpointLease = ((ITradeEndpointRegistration)trade).RegisterEndpoints(this, new OriginalTradeBasicItemProjection());
            try
            {
                transport.RegisterPacketHandler("Trading", Receive);
                active = true;
                users.OnLogin += Login; transport.OnConnectionClosed += Closed;
            }
            catch { endpointLease.Dispose(); endpointLease = null; throw; }
        }
    }
    private void Report(string reason) { try { log?.Invoke(reason, LogLevel.WARNING); } catch { } }
    public bool TryGetSession(string connection, out TradeEndpointSession session)
    {
        session = null;
        if (!active || readiness.TryGetReady(connection, out _) || !peers.TryGetValue(connection, out var peer) ||
            !authorization.IsAuthenticatedPrincipal(connection, peer.SessionId, peer.PrincipalId)) return false;
        session = peer; return true;
    }
    private ServerFrameworkContext Context(TradeEndpointSession peer) => new()
    {
        ConnectionId = peer.ConnectionId, SenderUuid = peer.PrincipalId, SessionId = peer.SessionId,
        SendMessage = normalSend,
        IsConnectionFrameworkCapable = connection => readiness.TryGetReady(connection, out _),
        ConnectionHasCapability = (connection, capability) => readiness.TryGetReady(connection, out var ready) && System.Linq.Enumerable.Contains(ready.Capabilities, capability)
    };
    private void Login(object sender, ServerLoginEventArgs args)
    {
        if (!active || readiness.TryGetReady(args.ConnectionId, out _) || !sessions.TryGetAuthenticatedSession(args.ConnectionId, args.Uuid, out var session)) return;
        var peer = new TradeEndpointSession(args.ConnectionId, session, args.Uuid, false);
        if (!peers.TryAdd(args.ConnectionId, peer)) return;
        // Login alone does not identify an original binary. Read-only bootstrap only;
        // never consume a Pending delivery before genuine authenticated old traffic.
        trade.HandleSnapshotRequest(Context(peer));
    }
    private void Closed(object sender, ConnectionEventArgs args) => peers.TryRemove(args.ConnectionId, out _);
    private bool Authenticate(string connection, string session, string principal, out TradeEndpointSession peer)
    {
        peer = null;
        if (!active || readiness.TryGetReady(connection, out _) || !authorization.IsAuthenticatedPrincipal(connection, session, principal)) return false;
        var proposed = new TradeEndpointSession(connection, session, principal, true);
        peers.AddOrUpdate(connection, proposed, (_, __) => proposed);
        peer = proposed; return true;
    }
    private void Receive(string module, string connection, byte[] bytes)
    {
        if (!active || module != "Trading") return;
        bool businessInvoked = false;
        try
        {
            string type = OriginalTradeWireCodec.Type(bytes);
            if (type == "Phinix/Trading.UpdateTradeItemsPacket")
            {
                var request = OriginalTradeOfferCodec.Decode(bytes);
                if (!Authenticate(connection, request.SessionId, request.PrincipalId, out var peer)) { Report("OriginalTradeUnauthorizedOrProfileConflict"); return; }
                // Original Reset is tokenless. Each arrival gets an honest server operation
                // identity; no claim of cross-reconnect application idempotency without a token.
                var packet = OriginalTradeOfferCodec.Translate(request, allowTokenless: true);
                var pending = new PendingOffer(request);
                if (!pendingOffers.TryAdd((connection, packet.MessageId), pending)) { Report("OriginalTradeOfferAlreadyInFlight"); return; }
                try { businessInvoked = true; trade.HandleOfferUpdateRequest(packet, Context(peer)); }
                catch (ServerStorage.OperationConflictException) when (!pending.ResultObserved)
                {
                    SendRaw(connection, OriginalTradeOfferCodec.Response(request, new FrameworkTradeOfferUpdateResponse
                    { TradeId = request.TradeId, FailureReason = FrameworkTradeFailureReason.InternalServerError, FailureMessage = "OriginalOfferOperationConflict" }));
                }
                finally { pendingOffers.TryRemove((connection, packet.MessageId), out _); }
                trade.HandleUserLoggedIn(connection, peer.SessionId, peer.PrincipalId, normalSend);
                return;
            }
            var intent = OriginalTradeWireCodec.Decode(bytes);
            if (!Authenticate(connection, intent.SessionId, intent.PrincipalId, out var endpoint)) { Report("OriginalTradeUnauthorizedOrProfileConflict"); return; }
            var command = new FrameworkPacket { MessageId = "original.request." + Guid.NewGuid().ToString("N"), Flow = global::Phinix.Framework.FrameworkFlow.Command, CommandKind = global::Phinix.Framework.FrameworkCommandKind.Request };
            businessInvoked = true;
            if (type == "Phinix/Trading.CreateTradePacket")
            {
                command.PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeCreateRequest { OtherPartyUuid = intent.TargetId });
                trade.HandleCreateRequest(command, Context(endpoint));
            }
            else
            {
                command.PayloadJson = FrameworkSerialization.SerializePayload(new FrameworkTradeStatusUpdateRequest { TradeId = intent.TargetId, Accepted = intent.Accepted, Cancelled = intent.Cancelled });
                trade.HandleStatusUpdateRequest(command, Context(endpoint));
            }
            trade.HandleUserLoggedIn(connection, endpoint.SessionId, endpoint.PrincipalId, normalSend);
        }
        catch (Exception e) when (e is InvalidDataException || e is InvalidProtocolBufferException || e is ArgumentException)
        { Report(businessInvoked ? "OriginalTradeOutcomeRequiresReconciliation" : "OriginalTradeMalformedRequest"); }
        // Commit uncertainty/send failures are not rewritten into a cancellation/refund.
    }
    private void SendRaw(string connection, byte[] bytes)
    {
        if (!TryGetSession(connection, out _)) return;
        if (transport.TrySendWithOutcome(connection, "Trading", bytes) != TransportSendOutcome.Submitted)
            Report("OriginalTradeSendUnconfirmed");
    }
    public void Send(string connection, FrameworkPacket packet)
    {
        if (!TryGetSession(connection, out var peer)) return;
        try
        {
            switch (packet.MessageType)
            {
                case FrameworkTradeProtocol.CreateResponseType:
                    SendRaw(connection, OriginalTradeWireCodec.Create(FrameworkSerialization.DeserializePayload<FrameworkTradeCreateResponse>(packet.PayloadJson))); break;
                case FrameworkTradeProtocol.OfferUpdateResponseType:
                    if (!pendingOffers.TryGetValue((connection, packet.GetCorrelationId()), out var pending)) throw new InvalidDataException("OriginalOfferCorrelationMissing");
                    pending.ResultObserved = true;
                    SendRaw(connection, OriginalTradeOfferCodec.Response(pending.Request, FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateResponse>(packet.PayloadJson))); break;
                case FrameworkTradeProtocol.StatusUpdateResponseType:
                    // Original has no status response; the following authoritative state or
                    // completion is its actual response. Failure must never look like cancel.
                    if (!FrameworkSerialization.DeserializePayload<FrameworkTradeStatusUpdateResponse>(packet.PayloadJson).Success)
                    { Report("OriginalTradeStatusFailedNoRefund"); transport.DisconnectPeer(connection); }
                    break;
                case FrameworkTradeProtocol.SnapshotType:
                    foreach (var bytes in OriginalTradeWireCodec.Snapshot(FrameworkSerialization.DeserializePayload<FrameworkTradeStateCollectionSnapshot>(packet.PayloadJson), peer.PrincipalId, packet.TryGetStateKind(out var stateKind) && stateKind == FrameworkMetadataStateKinds.Snapshot)) SendRaw(connection, bytes);
                    break;
                case FrameworkTradeProtocol.CompletedEventType:
                case FrameworkTradeProtocol.CancelledEventType:
                    SendRaw(connection, OriginalTradeWireCodec.Complete(FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionEvent>(packet.PayloadJson))); break;
                default: throw new InvalidDataException("OriginalTradeUnexpectedOutput");
            }
        }
        catch (Exception e) when (e is InvalidDataException || e is InvalidProtocolBufferException || e is ArgumentException)
        {
            Report(peer.Confirmed ? "OriginalTradeOutputUnrepresentable" : "OriginalTradeBootstrapDeferred");
            if (peer.Confirmed) transport.DisconnectPeer(connection);
        }
    }
    public void Dispose()
    {
        lock (lifetime)
        {
            if (!active) return;
            active = false;
            try { users.OnLogin -= Login; }
            finally
            {
                try { transport.OnConnectionClosed -= Closed; }
                finally
                {
                    try { transport.UnregisterPacketHandler("Trading"); }
                    finally { endpointLease?.Dispose(); endpointLease = null; peers.Clear(); pendingOffers.Clear(); }
                }
            }
        }
    }
}
