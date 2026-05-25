using System;
using System.Collections.Generic;
using System.Linq;
using Phinix.TradeExtension;
using UserManagement;
using Utils;
using Utils.Framework;

namespace PhinixServer.Framework
{
    public interface IFrameworkTradeServerApi : ILoggable, IPersistent
    {
        void HandleSnapshotRequest(ServerFrameworkContext context);

        void HandleUserLoggedIn(string connectionId, string sessionId, string uuid, Action<string, FrameworkPacket> sendMessage);

        void HandleCreateRequest(FrameworkPacket command, ServerFrameworkContext context);

        void HandleOfferUpdateRequest(FrameworkPacket command, ServerFrameworkContext context);

        void HandleStatusUpdateRequest(FrameworkPacket command, ServerFrameworkContext context);
    }

    public sealed class PhinixFrameworkTradeServerService : IFrameworkTradeServerApi
    {
        private readonly ServerUserManager userManager;
        private readonly PhinixFrameworkTradeStore store = new PhinixFrameworkTradeStore();

        public event EventHandler<LogEventArgs> OnLogEntry;

        public void RaiseLogEntry(LogEventArgs e) => OnLogEntry?.Invoke(this, e);

        public PhinixFrameworkTradeServerService(ServerUserManager userManager)
        {
            this.userManager = userManager;
        }

        public void HandleSnapshotRequest(ServerFrameworkContext context)
        {
            FrameworkTradeStateCollectionSnapshot payload = new FrameworkTradeStateCollectionSnapshot
            {
                Trades = store.GetByParticipant(context.SenderUuid).ToList()
            };

            FrameworkPacket response = new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.State,
                MessageType = FrameworkTradeProtocol.SnapshotType,
                SessionId = context.SessionId,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(payload)
            };
            response.SetCorrelationId(Guid.NewGuid().ToString());
            response.SetStateKind(FrameworkMetadataStateKinds.Snapshot);
            response.SetSnapshotVersion(payload.Trades.Count == 0 ? 0L : payload.Trades.Max(trade => trade.SnapshotVersion));
            context.SendMessage?.Invoke(context.ConnectionId, response);
        }

        public void HandleUserLoggedIn(string connectionId, string sessionId, string uuid, Action<string, FrameworkPacket> sendMessage)
        {
            foreach (PhinixFrameworkTradeStore.PendingCompletionNotification notification in store.GetPendingCompletionNotificationsFor(uuid))
            {
                sendCompletionEvent(connectionId, sessionId, notification, sendMessage);
                store.MarkCompletionNotificationDelivered(notification.TradeId, uuid);
            }
        }

        public void HandleCreateRequest(FrameworkPacket command, ServerFrameworkContext context)
        {
            FrameworkTradeCreateRequest payload = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateRequest>(command.PayloadJson);
            if (payload == null || string.IsNullOrEmpty(payload.OtherPartyUuid))
            {
                sendCreateResponse(context.ConnectionId, context, false, null, null, FrameworkTradeFailureReason.InternalServerError, "Trade request payload was empty.");
                return;
            }

            if (!userManager.TryGetLoggedIn(payload.OtherPartyUuid, out bool otherPartyLoggedIn))
            {
                sendCreateResponse(context.ConnectionId, context, false, null, payload.OtherPartyUuid, FrameworkTradeFailureReason.OtherPartyDoesNotExist, "The other party does not exist.");
                return;
            }

            if (!otherPartyLoggedIn)
            {
                sendCreateResponse(context.ConnectionId, context, false, null, payload.OtherPartyUuid, FrameworkTradeFailureReason.OtherPartyOffline, "The other party is offline.");
                return;
            }

            if (!userManager.TryGetAcceptingTrades(payload.OtherPartyUuid, out bool acceptingTrades) || !acceptingTrades)
            {
                sendCreateResponse(context.ConnectionId, context, false, null, payload.OtherPartyUuid, FrameworkTradeFailureReason.NotAcceptingTrades, "The other party is not accepting trades.");
                return;
            }

            if (!userManager.TryGetConnection(payload.OtherPartyUuid, out string otherPartyConnectionId))
            {
                sendCreateResponse(context.ConnectionId, context, false, null, payload.OtherPartyUuid, FrameworkTradeFailureReason.InternalServerError, "The server could not find the other party connection.");
                return;
            }

            if (!store.TryCreate(context.SenderUuid, payload.OtherPartyUuid, out FrameworkTradeStateSnapshot snapshot, out FrameworkTradeFailureReason failureReason))
            {
                sendCreateResponse(context.ConnectionId, context, false, null, payload.OtherPartyUuid, failureReason, "A trade with this party is already active.");
                return;
            }

            sendCreateResponse(context.ConnectionId, context, true, snapshot.TradeId, payload.OtherPartyUuid, FrameworkTradeFailureReason.None, null, command.GetCorrelationId());
            sendCreateResponse(otherPartyConnectionId, context, true, snapshot.TradeId, context.SenderUuid, FrameworkTradeFailureReason.None, null, command.GetCorrelationId());
            sendSnapshot(context.ConnectionId, context, snapshot, command.GetCorrelationId());
            sendSnapshot(otherPartyConnectionId, context, snapshot, command.GetCorrelationId());
        }

        public void HandleOfferUpdateRequest(FrameworkPacket command, ServerFrameworkContext context)
        {
            FrameworkTradeOfferUpdateRequest payload = FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateRequest>(command.PayloadJson);
            if (payload == null || string.IsNullOrEmpty(payload.TradeId))
            {
                sendOfferUpdateResponse(context.ConnectionId, context, false, null, Array.Empty<FrameworkItemPayload>(), FrameworkTradeFailureReason.TradeDoesNotExist, "Trade does not exist.", command.GetCorrelationId());
                return;
            }

            if (!store.TrySetOffer(payload.TradeId, context.SenderUuid, payload.Items, out FrameworkTradeStateSnapshot snapshot, out FrameworkTradeFailureReason failureReason))
            {
                sendOfferUpdateResponse(context.ConnectionId, context, false, payload.TradeId, payload.Items ?? new List<FrameworkItemPayload>(), failureReason, "Trade offer update failed.", command.GetCorrelationId());
                return;
            }

            sendOfferUpdateResponse(context.ConnectionId, context, true, payload.TradeId, payload.Items ?? new List<FrameworkItemPayload>(), FrameworkTradeFailureReason.None, null, command.GetCorrelationId());
            broadcastSnapshot(snapshot, context, payload.TradeId, command.GetCorrelationId());
        }

        public void HandleStatusUpdateRequest(FrameworkPacket command, ServerFrameworkContext context)
        {
            FrameworkTradeStatusUpdateRequest payload = FrameworkSerialization.DeserializePayload<FrameworkTradeStatusUpdateRequest>(command.PayloadJson);
            if (payload == null || string.IsNullOrEmpty(payload.TradeId))
            {
                sendStatusUpdateResponse(context.ConnectionId, context, false, null, FrameworkTradeFailureReason.TradeDoesNotExist, "Trade does not exist.", command.GetCorrelationId());
                return;
            }

            if (!store.TrySetStatus(payload.TradeId, context.SenderUuid, payload.Accepted, payload.Cancelled, out FrameworkTradeStateSnapshot snapshot, out bool completed, out bool wasCancelled, out FrameworkTradeFailureReason failureReason))
            {
                sendStatusUpdateResponse(context.ConnectionId, context, false, payload.TradeId, failureReason, "Trade status update failed.", command.GetCorrelationId());
                return;
            }

            sendStatusUpdateResponse(
                context.ConnectionId,
                context,
                true,
                payload.TradeId,
                FrameworkTradeFailureReason.None,
                null,
                command.GetCorrelationId() == command.MessageId ? payload.TradeId : command.GetCorrelationId());

            if (completed)
            {
                store.QueueCompletionNotifications(snapshot, wasCancelled);
                sendCompletionEvents(snapshot, context, wasCancelled, command.GetCorrelationId());
                return;
            }

            broadcastSnapshot(snapshot, context, payload.TradeId, command.GetCorrelationId());
        }

        private void broadcastSnapshot(FrameworkTradeStateSnapshot snapshot, ServerFrameworkContext context, string tradeId, string correlationId)
        {
            if (snapshot == null)
            {
                return;
            }

            foreach (FrameworkTradeParticipantSnapshot participant in snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
            {
                if (!userManager.TryGetConnection(participant.Uuid, out string connectionId))
                {
                    continue;
                }

                sendSnapshot(connectionId, context, snapshot, correlationId);
            }
        }

        private void sendSnapshot(string connectionId, ServerFrameworkContext context, FrameworkTradeStateSnapshot snapshot, string correlationId)
        {
            FrameworkTradeStateCollectionSnapshot payload = new FrameworkTradeStateCollectionSnapshot
            {
                Trades = new List<FrameworkTradeStateSnapshot> { snapshot }
            };

            FrameworkPacket packet = new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.State,
                MessageType = FrameworkTradeProtocol.SnapshotType,
                SessionId = context.SessionId,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(payload)
            };
            packet.SetCorrelationId(correlationId);
            packet.SetStateKind(FrameworkMetadataStateKinds.Delta);
            packet.SetSnapshotVersion(snapshot.SnapshotVersion);
            context.SendMessage?.Invoke(connectionId, packet);
        }

        private void sendCreateResponse(string connectionId, ServerFrameworkContext context, bool success, string tradeId, string otherPartyUuid, FrameworkTradeFailureReason failureReason, string failureMessage, string correlationId = null)
        {
            FrameworkTradeCreateResponse payload = new FrameworkTradeCreateResponse
            {
                Success = success,
                TradeId = tradeId,
                OtherPartyUuid = otherPartyUuid,
                FailureReason = failureReason,
                FailureMessage = failureMessage
            };

            FrameworkPacket packet = new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Response,
                MessageType = FrameworkTradeProtocol.CreateResponseType,
                SessionId = context.SessionId,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(payload)
            };
            packet.SetCorrelationId(correlationId ?? context.SenderUuid);
            context.SendMessage?.Invoke(connectionId, packet);
        }

        private void sendOfferUpdateResponse(string connectionId, ServerFrameworkContext context, bool success, string tradeId, IEnumerable<FrameworkItemPayload> items, FrameworkTradeFailureReason failureReason, string failureMessage, string correlationId)
        {
            FrameworkTradeOfferUpdateResponse payload = new FrameworkTradeOfferUpdateResponse
            {
                Success = success,
                TradeId = tradeId,
                Items = (items ?? Enumerable.Empty<FrameworkItemPayload>()).ToList(),
                FailureReason = failureReason,
                FailureMessage = failureMessage
            };

            FrameworkPacket packet = new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Response,
                MessageType = FrameworkTradeProtocol.OfferUpdateResponseType,
                SessionId = context.SessionId,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(payload)
            };
            packet.SetCorrelationId(correlationId);
            context.SendMessage?.Invoke(connectionId, packet);
        }

        private void sendStatusUpdateResponse(string connectionId, ServerFrameworkContext context, bool success, string tradeId, FrameworkTradeFailureReason failureReason, string failureMessage, string correlationId)
        {
            FrameworkTradeStatusUpdateResponse payload = new FrameworkTradeStatusUpdateResponse
            {
                Success = success,
                TradeId = tradeId,
                FailureReason = failureReason,
                FailureMessage = failureMessage
            };

            FrameworkPacket packet = new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Response,
                MessageType = FrameworkTradeProtocol.StatusUpdateResponseType,
                SessionId = context.SessionId,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(payload)
            };
            packet.SetCorrelationId(correlationId);
            context.SendMessage?.Invoke(connectionId, packet);
        }

        private void sendCompletionEvents(FrameworkTradeStateSnapshot snapshot, ServerFrameworkContext context, bool cancelled, string correlationId)
        {
            if (snapshot == null)
            {
                return;
            }

            foreach (FrameworkTradeParticipantSnapshot participant in snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
            {
                if (!userManager.TryGetConnection(participant.Uuid, out string connectionId))
                {
                    continue;
                }

                PhinixFrameworkTradeStore.PendingCompletionNotification notification = store
                    .GetPendingCompletionNotificationsFor(participant.Uuid)
                    .SingleOrDefault(candidate => candidate.TradeId == snapshot.TradeId);
                if (notification == null)
                {
                    continue;
                }

                sendCompletionEvent(connectionId, context.SessionId, notification, context.SendMessage, correlationId);
                store.MarkCompletionNotificationDelivered(notification.TradeId, participant.Uuid);
            }
        }

        public void Save(string path)
        {
            store.Save(path);
            RaiseLogEntry(new LogEventArgs("Saved framework trade state."));
        }

        public void Load(string path)
        {
            store.Load(path);
            RaiseLogEntry(new LogEventArgs("Loaded framework trade state."));
        }

        private void sendCompletionEvent(string connectionId, string sessionId, PhinixFrameworkTradeStore.PendingCompletionNotification notification, Action<string, FrameworkPacket> sendMessage, string correlationId = null)
        {
            FrameworkTradeCompletionEvent payload = new FrameworkTradeCompletionEvent
            {
                TradeId = notification.TradeId,
                OtherPartyUuid = notification.OtherPartyUuid,
                Items = (notification.Items ?? new List<FrameworkItemPayload>()).ToList(),
                Cancelled = notification.Cancelled
            };

            FrameworkPacket packet = new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Event,
                MessageType = notification.Cancelled ? FrameworkTradeProtocol.CancelledEventType : FrameworkTradeProtocol.CompletedEventType,
                SessionId = sessionId,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(payload)
            };
            packet.SetCorrelationId(correlationId ?? Guid.NewGuid().ToString());
            sendMessage?.Invoke(connectionId, packet);
        }
    }
}
