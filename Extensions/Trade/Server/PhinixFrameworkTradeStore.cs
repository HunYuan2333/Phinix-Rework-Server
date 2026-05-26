using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Xml;
using Utils.Framework;

namespace Phinix.TradeExtension.Server
{
    internal sealed class PhinixFrameworkTradeStore
    {
        private readonly Dictionary<string, MutableTrade> activeTrades = new Dictionary<string, MutableTrade>(StringComparer.OrdinalIgnoreCase);
        private readonly List<PendingCompletionNotification> pendingCompletionNotifications = new List<PendingCompletionNotification>();
        private readonly object syncLock = new object();

        public bool TryCreate(string partyAUuid, string partyBUuid, out FrameworkTradeStateSnapshot snapshot, out FrameworkTradeFailureReason failureReason)
        {
            snapshot = null;
            failureReason = FrameworkTradeFailureReason.None;

            lock (syncLock)
            {
                if (activeTrades.Values.Any(existingTrade => existingTrade.ParticipantUuids.Contains(partyAUuid) && existingTrade.ParticipantUuids.Contains(partyBUuid)))
                {
                    failureReason = FrameworkTradeFailureReason.AlreadyTrading;
                    return false;
                }

                MutableTrade createdTrade = new MutableTrade(partyAUuid, partyBUuid);
                activeTrades[createdTrade.TradeId] = createdTrade;
                snapshot = createdTrade.ToSnapshot();
                return true;
            }
        }

        public FrameworkTradeStateSnapshot[] GetByParticipant(string participantUuid)
        {
            lock (syncLock)
            {
                return activeTrades.Values
                    .Where(trade => trade.ParticipantUuids.Contains(participantUuid))
                    .Select(trade => trade.ToSnapshot())
                    .ToArray();
            }
        }

        public bool TrySetOffer(string tradeId, string participantUuid, IEnumerable<FrameworkItemPayload> items, out FrameworkTradeStateSnapshot snapshot, out FrameworkTradeFailureReason failureReason)
        {
            lock (syncLock)
            {
                if (!activeTrades.TryGetValue(tradeId, out MutableTrade trade))
                {
                    snapshot = null;
                    failureReason = FrameworkTradeFailureReason.TradeDoesNotExist;
                    return false;
                }

                if (!trade.TrySetOffer(participantUuid, items))
                {
                    snapshot = null;
                    failureReason = FrameworkTradeFailureReason.NotTradeParticipant;
                    return false;
                }

                snapshot = trade.ToSnapshot();
                failureReason = FrameworkTradeFailureReason.None;
                return true;
            }
        }

        public bool TrySetStatus(string tradeId, string participantUuid, bool? accepted, bool? cancelled, out FrameworkTradeStateSnapshot snapshot, out bool completed, out bool wasCancelled, out FrameworkTradeFailureReason failureReason)
        {
            lock (syncLock)
            {
                completed = false;
                wasCancelled = false;

                if (!activeTrades.TryGetValue(tradeId, out MutableTrade trade))
                {
                    snapshot = null;
                    failureReason = FrameworkTradeFailureReason.TradeDoesNotExist;
                    return false;
                }

                if (!trade.ParticipantUuids.Contains(participantUuid))
                {
                    snapshot = null;
                    failureReason = FrameworkTradeFailureReason.NotTradeParticipant;
                    return false;
                }

                if (cancelled == true)
                {
                    activeTrades.Remove(tradeId);
                    snapshot = trade.ToSnapshot();
                    completed = true;
                    wasCancelled = true;
                    failureReason = FrameworkTradeFailureReason.None;
                    return true;
                }

                if (accepted.HasValue)
                {
                    trade.SetAccepted(participantUuid, accepted.Value);
                }

                snapshot = trade.ToSnapshot();
                failureReason = FrameworkTradeFailureReason.None;

                if (trade.AllAccepted)
                {
                    activeTrades.Remove(tradeId);
                    completed = true;
                    wasCancelled = false;
                }

                return true;
            }
        }

        public void QueueCompletionNotifications(FrameworkTradeStateSnapshot snapshot, bool cancelled)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.TradeId))
            {
                return;
            }

            lock (syncLock)
            {
                pendingCompletionNotifications.RemoveAll(notification => string.Equals(notification.TradeId, snapshot.TradeId, StringComparison.OrdinalIgnoreCase));
                pendingCompletionNotifications.AddRange(PendingCompletionNotification.CreateMany(snapshot, cancelled));
            }
        }

        public PendingCompletionNotification[] GetPendingCompletionNotificationsFor(string recipientUuid)
        {
            lock (syncLock)
            {
                return pendingCompletionNotifications
                    .Where(notification => string.Equals(notification.RecipientUuid, recipientUuid, StringComparison.OrdinalIgnoreCase))
                    .Select(notification => notification.Clone())
                    .ToArray();
            }
        }

        public void MarkCompletionNotificationDelivered(string tradeId, string recipientUuid)
        {
            if (string.IsNullOrEmpty(tradeId) || string.IsNullOrEmpty(recipientUuid))
            {
                return;
            }

            lock (syncLock)
            {
                pendingCompletionNotifications.RemoveAll(notification =>
                    string.Equals(notification.TradeId, tradeId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(notification.RecipientUuid, recipientUuid, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void Save(string path)
        {
            lock (syncLock)
            {
                FrameworkTradeStoreState state = new FrameworkTradeStoreState
                {
                    ActiveTrades = activeTrades.Values.Select(trade => trade.ToSnapshot()).ToList(),
                    PendingCompletionNotifications = pendingCompletionNotifications.Select(notification => notification.ToState()).ToList()
                };

                XmlWriterSettings settings = new XmlWriterSettings { Indent = true };
                using (XmlWriter writer = XmlWriter.Create(path, settings))
                {
                    new DataContractSerializer(typeof(FrameworkTradeStoreState)).WriteObject(writer, state);
                }
            }
        }

        public void Load(string path)
        {
            lock (syncLock)
            {
                if (!File.Exists(path))
                {
                    activeTrades.Clear();
                    pendingCompletionNotifications.Clear();
                    Save(path);
                    return;
                }

                FrameworkTradeStoreState state;
                using (XmlReader reader = XmlReader.Create(path))
                {
                    state = new DataContractSerializer(typeof(FrameworkTradeStoreState)).ReadObject(reader) as FrameworkTradeStoreState ?? new FrameworkTradeStoreState();
                }

                activeTrades.Clear();
                foreach (FrameworkTradeStateSnapshot snapshot in state.ActiveTrades ?? new List<FrameworkTradeStateSnapshot>())
                {
                    MutableTrade trade = MutableTrade.FromSnapshot(snapshot);
                    activeTrades[trade.TradeId] = trade;
                }

                pendingCompletionNotifications.Clear();
                foreach (PendingCompletionNotificationState notificationState in state.PendingCompletionNotifications ?? new List<PendingCompletionNotificationState>())
                {
                    pendingCompletionNotifications.Add(PendingCompletionNotification.FromState(notificationState));
                }
            }
        }

        [DataContract]
        private sealed class FrameworkTradeStoreState
        {
            [DataMember(Order = 0)]
            public List<FrameworkTradeStateSnapshot> ActiveTrades { get; set; } = new List<FrameworkTradeStateSnapshot>();

            [DataMember(Order = 1)]
            public List<PendingCompletionNotificationState> PendingCompletionNotifications { get; set; } = new List<PendingCompletionNotificationState>();
        }

        [DataContract]
        internal sealed class PendingCompletionNotificationState
        {
            [DataMember(Order = 0)]
            public string TradeId { get; set; }

            [DataMember(Order = 1)]
            public string RecipientUuid { get; set; }

            [DataMember(Order = 2)]
            public string OtherPartyUuid { get; set; }

            [DataMember(Order = 3)]
            public List<FrameworkItemPayload> Items { get; set; } = new List<FrameworkItemPayload>();

            [DataMember(Order = 4)]
            public bool Cancelled { get; set; }
        }

        internal sealed class PendingCompletionNotification
        {
            public string TradeId { get; private set; }
            public string RecipientUuid { get; private set; }
            public string OtherPartyUuid { get; private set; }
            public List<FrameworkItemPayload> Items { get; private set; } = new List<FrameworkItemPayload>();
            public bool Cancelled { get; private set; }

            public PendingCompletionNotification Clone()
            {
                return new PendingCompletionNotification
                {
                    TradeId = TradeId,
                    RecipientUuid = RecipientUuid,
                    OtherPartyUuid = OtherPartyUuid,
                    Items = MutableTrade.CloneItems(Items),
                    Cancelled = Cancelled
                };
            }

            public PendingCompletionNotificationState ToState()
            {
                return new PendingCompletionNotificationState
                {
                    TradeId = TradeId,
                    RecipientUuid = RecipientUuid,
                    OtherPartyUuid = OtherPartyUuid,
                    Items = MutableTrade.CloneItems(Items),
                    Cancelled = Cancelled
                };
            }

            public static IEnumerable<PendingCompletionNotification> CreateMany(FrameworkTradeStateSnapshot snapshot, bool cancelled)
            {
                foreach (FrameworkTradeParticipantSnapshot participant in snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
                {
                    FrameworkTradeParticipantSnapshot otherParty = snapshot.Participants.SingleOrDefault(candidate => candidate.Uuid != participant.Uuid);
                    yield return new PendingCompletionNotification
                    {
                        TradeId = snapshot.TradeId,
                        RecipientUuid = participant.Uuid,
                        OtherPartyUuid = otherParty?.Uuid,
                        Items = cancelled
                            ? MutableTrade.CloneItems(participant.ItemsOnOffer)
                            : MutableTrade.CloneItems(otherParty?.ItemsOnOffer),
                        Cancelled = cancelled
                    };
                }
            }

            public static PendingCompletionNotification FromState(PendingCompletionNotificationState state)
            {
                return new PendingCompletionNotification
                {
                    TradeId = state?.TradeId,
                    RecipientUuid = state?.RecipientUuid,
                    OtherPartyUuid = state?.OtherPartyUuid,
                    Items = MutableTrade.CloneItems(state?.Items),
                    Cancelled = state?.Cancelled ?? false
                };
            }
        }

        private sealed class MutableTrade
        {
            private readonly Dictionary<string, List<FrameworkItemPayload>> offers = new Dictionary<string, List<FrameworkItemPayload>>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> acceptedParticipants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public MutableTrade(string partyAUuid, string partyBUuid)
            {
                TradeId = Guid.NewGuid().ToString();
                ParticipantUuids = new[] { partyAUuid, partyBUuid };
                Version = 1;
            }

            public string TradeId { get; private set; }
            public string[] ParticipantUuids { get; private set; }
            public long Version { get; private set; }
            public bool AllAccepted => ParticipantUuids.All(uuid => acceptedParticipants.Contains(uuid));

            public bool TrySetOffer(string participantUuid, IEnumerable<FrameworkItemPayload> items)
            {
                if (!ParticipantUuids.Contains(participantUuid))
                {
                    return false;
                }

                offers[participantUuid] = CloneItems(items);
                acceptedParticipants.Clear();
                Version++;
                return true;
            }

            public void SetAccepted(string participantUuid, bool accepted)
            {
                if (accepted)
                {
                    acceptedParticipants.Add(participantUuid);
                }
                else
                {
                    acceptedParticipants.Remove(participantUuid);
                }

                Version++;
            }

            public IEnumerable<FrameworkItemPayload> GetItemsFor(string participantUuid)
            {
                if (participantUuid == null || !offers.TryGetValue(participantUuid, out List<FrameworkItemPayload> items))
                {
                    return Enumerable.Empty<FrameworkItemPayload>();
                }

                return CloneItems(items);
            }

            public FrameworkTradeStateSnapshot ToSnapshot()
            {
                return new FrameworkTradeStateSnapshot
                {
                    TradeId = TradeId,
                    SnapshotVersion = Version,
                    Participants = ParticipantUuids
                        .Select(uuid => new FrameworkTradeParticipantSnapshot
                        {
                            Uuid = uuid,
                            Accepted = acceptedParticipants.Contains(uuid),
                            ItemsOnOffer = CloneItems(GetItemsFor(uuid)).ToList()
                        })
                        .ToList()
                };
            }

            public static MutableTrade FromSnapshot(FrameworkTradeStateSnapshot snapshot)
            {
                string[] participantUuids = (snapshot?.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
                    .Select(participant => participant.Uuid)
                    .Where(uuid => !string.IsNullOrEmpty(uuid))
                    .ToArray();

                MutableTrade trade = new MutableTrade(
                    participantUuids.ElementAtOrDefault(0) ?? string.Empty,
                    participantUuids.ElementAtOrDefault(1) ?? string.Empty)
                {
                    TradeId = snapshot?.TradeId ?? Guid.NewGuid().ToString(),
                    ParticipantUuids = participantUuids,
                    Version = snapshot?.SnapshotVersion ?? 0
                };

                trade.offers.Clear();
                trade.acceptedParticipants.Clear();
                foreach (FrameworkTradeParticipantSnapshot participant in snapshot?.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
                {
                    trade.offers[participant.Uuid] = CloneItems(participant.ItemsOnOffer);
                    if (participant.Accepted)
                    {
                        trade.acceptedParticipants.Add(participant.Uuid);
                    }
                }

                return trade;
            }

            internal static List<FrameworkItemPayload> CloneItems(IEnumerable<FrameworkItemPayload> items)
            {
                return (items ?? Enumerable.Empty<FrameworkItemPayload>())
                    .Select(item => new FrameworkItemPayload
                    {
                        CodecId = item.CodecId,
                        PayloadJson = item.PayloadJson,
                        PayloadBytes = item.PayloadBytes?.ToArray() ?? Array.Empty<byte>(),
                        Metadata = (item.Metadata ?? new List<FrameworkMetadataEntry>())
                            .Select(entry => new FrameworkMetadataEntry { Key = entry.Key, Value = entry.Value })
                            .ToList()
                    })
                    .ToList();
            }
        }
    }
}
