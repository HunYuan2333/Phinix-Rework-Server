using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Xml;
using Utils.Framework;

namespace Phinix.TradeExtension.Server
{
    internal sealed partial class PhinixFrameworkTradeStore
    {
        private readonly TradeAdmissionPolicy admissionPolicy;
        public PhinixFrameworkTradeStore() : this(new TradeAdmissionPolicy()) { }
        public PhinixFrameworkTradeStore(TradeAdmissionPolicy policy) { admissionPolicy = policy; }
        private readonly Dictionary<string, MutableTrade> activeTrades = new Dictionary<string, MutableTrade>(StringComparer.OrdinalIgnoreCase);
        private readonly List<PendingCompletionNotification> pendingCompletionNotifications = new List<PendingCompletionNotification>();
        private readonly object syncLock = new object();
        private readonly List<FrameworkTradeStateSnapshot> terminalTrades = new List<FrameworkTradeStateSnapshot>();
        private readonly Dictionary<string, TradeNegotiationTerms> negotiationTerms = new(StringComparer.OrdinalIgnoreCase);

        public TradeNegotiationTerms GetTerms(string tradeId)
        {
            lock (syncLock) return tradeId != null && negotiationTerms.TryGetValue(tradeId, out var terms) ? terms : TradeNegotiationTerms.ObservedQuote;
        }

        public bool TryCreate(string partyAUuid, string partyBUuid, out FrameworkTradeStateSnapshot snapshot, out FrameworkTradeFailureReason failureReason)
            => TryCreateWithTerms(partyAUuid, partyBUuid, out snapshot, out failureReason, TradeNegotiationTerms.ObservedQuote);

        public bool TryCreateWithTerms(string partyAUuid, string partyBUuid, out FrameworkTradeStateSnapshot snapshot, out FrameworkTradeFailureReason failureReason, TradeNegotiationTerms terms)
        {
            if (!Enum.IsDefined(terms)) throw new ArgumentOutOfRangeException(nameof(terms));
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
                if (!FitsAdmissionReservation(createdTrade, terms))
                {
                    failureReason = FrameworkTradeFailureReason.InternalServerError;
                    return false;
                }
                activeTrades[createdTrade.TradeId] = createdTrade;
                if (terms != TradeNegotiationTerms.ObservedQuote) negotiationTerms.Add(createdTrade.TradeId, terms);
                snapshot = createdTrade.ToSnapshot();
                return true;
            }
        }

        public FrameworkTradeStateSnapshot[] GetByParticipant(string participantUuid)
        {
            lock (syncLock)
            {
                EnsurePersistence();
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

                // Check the proposed replacement before changing consent, quote or ownership.
                var replacement = MutableTrade.FromSnapshot(trade.ToSnapshot());
                replacement.OfferVersion = trade.OfferVersion;
                if (!replacement.TrySetOffer(participantUuid, items))
                {
                    snapshot = null;
                    failureReason = FrameworkTradeFailureReason.NotTradeParticipant;
                    return false;
                }

                if (!FitsAdmissionReservation(replacement))
                {
                    snapshot = null;
                    failureReason = FrameworkTradeFailureReason.InternalServerError;
                    return false;
                }
                activeTrades[tradeId] = replacement;
                snapshot = replacement.ToSnapshot();
                failureReason = FrameworkTradeFailureReason.None;
                return true;
            }
        }

        public bool CanSetOffer(string tradeId, string participantUuid, out FrameworkTradeFailureReason failureReason)
        {
            lock (syncLock)
            {
                failureReason = !activeTrades.TryGetValue(tradeId, out MutableTrade trade)
                    ? FrameworkTradeFailureReason.TradeDoesNotExist
                    : !trade.ParticipantUuids.Contains(participantUuid)
                        ? FrameworkTradeFailureReason.NotTradeParticipant
                        : FrameworkTradeFailureReason.None;
                return failureReason == FrameworkTradeFailureReason.None;
            }
        }

        public bool TrySetStatus(string tradeId, string participantUuid, bool? accepted, bool? cancelled, out FrameworkTradeStateSnapshot snapshot, out bool completed, out bool wasCancelled, out FrameworkTradeFailureReason failureReason)
            => TrySetStatusAtVersion(tradeId, participantUuid, accepted, cancelled, null, out snapshot, out completed, out wasCancelled, out failureReason);

        public bool TrySetStatusAtVersion(string tradeId, string participantUuid, bool? accepted, bool? cancelled, long? observedVersion, out FrameworkTradeStateSnapshot snapshot, out bool completed, out bool wasCancelled, out FrameworkTradeFailureReason failureReason)
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
                    terminalTrades.Add(snapshot);
                    completed = true;
                    wasCancelled = true;
                    failureReason = FrameworkTradeFailureReason.None;
                    return true;
                }

                // Consent may lag acceptance-only changes, but never a newer offer.
                if (accepted == true &&
                    (observedVersion.HasValue
                        ? observedVersion < trade.OfferVersion || observedVersion > trade.Version
                        : GetTerms(tradeId) != TradeNegotiationTerms.BasicItemsCurrentQuote))
                {
                    snapshot = null;
                    failureReason = FrameworkTradeFailureReason.InternalServerError;
                    return false;
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
                    terminalTrades.Add(snapshot);
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
                EnsurePersistence();
                return pendingCompletionNotifications
                    .Where(notification => string.Equals(notification.RecipientUuid, recipientUuid, StringComparison.OrdinalIgnoreCase))
                    .Select(notification => notification.Clone())
                    .ToArray();
            }
        }

        public PendingCompletionNotification[] GetPendingCompletionPageFor(string recipientUuid, string afterDeliveryId, int count)
        {
            if (count < 1 || count > 64) throw new ArgumentOutOfRangeException(nameof(count));
            lock (syncLock)
            {
                EnsurePersistence();
                if (journal != null)
                {
                    try
                    {
                        return journal.ReadPendingPage(recipientUuid.ToUpperInvariant(), afterDeliveryId, count)
                            .Select(entry => ValidatePendingEntry(entry).Clone()).ToArray();
                    }
                    catch (System.IO.IOException) { persistenceFailed = true; throw; }
                }
                return pendingCompletionNotifications
                    .Where(n => n.Recoverable && string.Equals(n.RecipientUuid, recipientUuid, StringComparison.OrdinalIgnoreCase) &&
                        (afterDeliveryId == null || StringComparer.Ordinal.Compare(n.DeliveryId, afterDeliveryId) > 0))
                    .OrderBy(n => n.DeliveryId, StringComparer.Ordinal).Take(count)
                    .Select(n => n.Clone()).ToArray();
            }
        }

        public PendingCompletionNotification GetCompletionNotificationFor(string tradeId, string recipientUuid)
        {
            lock (syncLock)
            {
                EnsurePersistence();
                return pendingCompletionNotifications.SingleOrDefault(n => n.TradeId == tradeId &&
                    string.Equals(n.RecipientUuid, recipientUuid, StringComparison.OrdinalIgnoreCase))?.Clone();
            }
        }

        public bool TryAcknowledgeCompletion(string tradeId, string recipientUuid, bool cancelled)
        {
            if (string.IsNullOrEmpty(tradeId) || string.IsNullOrEmpty(recipientUuid))
            {
                return false;
            }

            lock (syncLock)
            {
                PendingCompletionNotification pending = pendingCompletionNotifications.FirstOrDefault(notification =>
                    string.Equals(notification.TradeId, tradeId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(notification.RecipientUuid, recipientUuid, StringComparison.OrdinalIgnoreCase));
                if (pending == null) return false;
                if (pending.Cancelled != cancelled) return false;
                // Historical ACK is receipt evidence only, not proof of a durable game save.
                pending.AckObserved = true;
                return true;
            }
        }

        public void Save(string path)
        {
            lock (syncLock)
            {
                EnsurePersistence();
                if (journal != null)
                {
                    try { VerifyAuthority(path); } catch { persistenceFailed = true; throw; }
                    return;
                }
                ServerStorage.AtomicFileSnapshot.Write(path, stream => stream.Write(ExportState()));
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
                    terminalTrades.Clear();
                    negotiationTerms.Clear();
                    Save(path);
                    return;
                }

                ImportState(File.ReadAllBytes(path));
            }
        }

        [DataContract]
        private sealed class FrameworkTradeStoreState
        {
            [DataMember(Order = 0)]
            public List<FrameworkTradeStateSnapshot> ActiveTrades { get; set; } = new List<FrameworkTradeStateSnapshot>();

            [DataMember(Order = 1)]
            public List<PendingCompletionNotificationState> PendingCompletionNotifications { get; set; } = new List<PendingCompletionNotificationState>();
            [DataMember(Order = 2)] public List<FrameworkTradeStateSnapshot> TerminalTrades { get; set; } = new List<FrameworkTradeStateSnapshot>();
            [DataMember(Order = 3)] public int ConsentSchemaVersion { get; set; }
            [DataMember(Order = 4)] public Dictionary<string, long> OfferVersions { get; set; }
            [DataMember(Order = 5, EmitDefaultValue = false)] public Dictionary<string, TradeNegotiationTerms> NegotiationTerms { get; set; }
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
            [DataMember(Order = 5)] public string DeliveryId { get; set; }
            [DataMember(Order = 6)] public string DeliveryState { get; set; }
            [DataMember(Order = 7)] public string AttemptId { get; set; }
            [DataMember(Order = 8)] public bool AckObserved { get; set; }
            [DataMember(Order = 9, EmitDefaultValue=false)] public string ReceiptContract {get;set;}
            [DataMember(Order = 10, EmitDefaultValue=false)] public string ReceiptKind {get;set;}
            [DataMember(Order = 11, EmitDefaultValue=false)] public string ReceiptSave {get;set;}
            [DataMember(Order = 12, EmitDefaultValue=false)] public string ReceiptCheckpoint {get;set;}
            [DataMember(Order = 13, EmitDefaultValue=false)] public string LastDispatchEpoch {get;set;}
        }

        internal sealed class PendingCompletionNotification
        {
            public string TradeId { get; private set; }
            public string RecipientUuid { get; private set; }
            public string OtherPartyUuid { get; private set; }
            public List<FrameworkItemPayload> Items { get; private set; } = new List<FrameworkItemPayload>();
            public bool Cancelled { get; private set; }
            public string DeliveryId { get; private set; }
            public string ReceiptContract {get;set;}
            public string ReceiptKind {get;set;}
            public string ReceiptSave {get;set;}
            public string ReceiptCheckpoint {get;set;}
            public string LastDispatchEpoch {get;set;}
            internal bool Recoverable => DeliveryState=="Pending" || (DeliveryState=="OutcomeUnknown" && ReceiptContract==TradeDeliveryProtocol.Contract);
            internal string PayloadFingerprint() => TradeDeliveryProtocol.Fingerprint(DeliveryId,RecipientUuid,new FrameworkTradeCompletionEvent
            {TradeId=TradeId,OtherPartyUuid=OtherPartyUuid,Items=Items,Cancelled=Cancelled});

            public string DeliveryState { get; set; }
            public string AttemptId { get; set; }
            public bool AckObserved { get; set; }

            public PendingCompletionNotification Clone()
            {
                return new PendingCompletionNotification
                {
                    TradeId = TradeId,
                    RecipientUuid = RecipientUuid,
                    OtherPartyUuid = OtherPartyUuid,
                    Items = MutableTrade.CloneItems(Items),
                    Cancelled = Cancelled
                    ,DeliveryId = DeliveryId, DeliveryState = DeliveryState, AttemptId = AttemptId, AckObserved = AckObserved, ReceiptContract = ReceiptContract, ReceiptKind = ReceiptKind, ReceiptSave = ReceiptSave, ReceiptCheckpoint = ReceiptCheckpoint, LastDispatchEpoch = LastDispatchEpoch
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
                    ,DeliveryId = DeliveryId, DeliveryState = DeliveryState, AttemptId = AttemptId, AckObserved = AckObserved, ReceiptContract = ReceiptContract, ReceiptKind = ReceiptKind, ReceiptSave = ReceiptSave, ReceiptCheckpoint = ReceiptCheckpoint, LastDispatchEpoch = LastDispatchEpoch
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
                        ,DeliveryId = Guid.NewGuid().ToString("N"), DeliveryState = "Pending"
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
                    ,DeliveryId = state?.DeliveryId ?? Guid.NewGuid().ToString("N"),
                    DeliveryState = state?.DeliveryState ?? "OutcomeUnknown", AttemptId = state?.AttemptId, AckObserved = state?.AckObserved ?? false, ReceiptContract = state?.ReceiptContract, ReceiptKind = state?.ReceiptKind, ReceiptSave = state?.ReceiptSave, ReceiptCheckpoint = state?.ReceiptCheckpoint, LastDispatchEpoch = state?.LastDispatchEpoch
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
            public long OfferVersion { get; set; } = 1;
            public void ResetUnboundConsent() { acceptedParticipants.Clear(); Version++; OfferVersion = Version; }
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
                OfferVersion = Version;
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
