using System;
using System.IO;
using Google.Protobuf;
using Phinix.TradeExtension;
using Phinix.TradeExtension.Server;
using ServerRuntime;
using ServerStorage;
using Utils.Framework;

namespace Phinix.LegacyAdapter.Server
{
    // Packet -> authenticated unified request -> SAME transaction owner -> genuine old result.
    // This bridge is not yet registered as a public Trading module. Creation, snapshot/
    // recipient mapping, cancellation delivery and consent expressibility are separate P5 work.
    public sealed class OriginalTradeOfferBridge
    {
        private readonly IFrameworkTradeServerApi trade;
        private readonly IServerPrincipalAuthorization authorization;
        private readonly IServerSessionReadiness readiness;
        public OriginalTradeOfferBridge(IFrameworkTradeServerApi trade, IServerPrincipalAuthorization authorization, IServerSessionReadiness readiness)
        { this.trade = trade ?? throw new ArgumentNullException(nameof(trade)); this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization)); this.readiness = readiness ?? throw new ArgumentNullException(nameof(readiness)); }
        public void Submit(string connection, byte[] bytes, Action<string, byte[]> originalResponse, Action<string, FrameworkPacket> otherPackets)
        {
            if (originalResponse == null || otherPackets == null) throw new ArgumentNullException("Every output route is required");
            var request = OriginalTradeOfferCodec.Decode(bytes);
            void Refuse(FrameworkTradeFailureReason reason, string code) => originalResponse(connection, OriginalTradeOfferCodec.Response(request,
                new FrameworkTradeOfferUpdateResponse { TradeId = request.TradeId, FailureReason = reason, FailureMessage = code }));
            bool authenticated;
            try { authenticated = !readiness.TryGetReady(connection, out _) && authorization.IsAuthenticatedPrincipal(connection, request.SessionId, request.PrincipalId); }
            catch { authenticated = false; }
            if (!authenticated) { Refuse(FrameworkTradeFailureReason.SessionInvalid, "OriginalOfferUnauthorized"); return; }
            FrameworkPacket unified;
            try { unified = OriginalTradeOfferCodec.Translate(request); }
            catch (Exception error) when (error is InvalidDataException || error is InvalidProtocolBufferException || error is ArgumentException)
            { Refuse(FrameworkTradeFailureReason.InternalServerError, "OriginalOfferUnrepresentableOrTokenMissing"); return; }
            unified.SessionId = request.SessionId; unified.SenderUuid = request.PrincipalId;
            bool resultObserved = false;
            var context = new ServerFrameworkContext
            {
                ConnectionId = connection, SessionId = request.SessionId, SenderUuid = request.PrincipalId,
                SendMessage = (target, packet) =>
                {
                    if (target == connection && packet.MessageType == FrameworkTradeProtocol.OfferUpdateResponseType)
                    {
                        resultObserved = true;
                        originalResponse(target, OriginalTradeOfferCodec.Response(request, FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateResponse>(packet.PayloadJson)));
                    }
                    else otherPackets(target, packet);
                }
            };
            try { trade.HandleOfferUpdateRequest(unified, context); }
            catch (OperationConflictException) when (!resultObserved) { Refuse(FrameworkTradeFailureReason.InternalServerError, "OriginalOfferOperationConflict"); }
            // IO, commit uncertainty and send exceptions must escape without a false refusal
            // or refund. A reconnect reuses the original stable token and durable result.
        }
    }
}
