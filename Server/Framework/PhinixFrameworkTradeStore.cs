using System;
using System.Collections.Generic;
using System.Linq;
using Utils.Framework;

namespace PhinixServer.Framework
{
    internal sealed class PhinixFrameworkTradeStore
    {
        private readonly Dictionary<string, MutableTrade> activeTrades = new Dictionary<string, MutableTrade>(StringComparer.OrdinalIgnoreCase);
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

        public bool TryGet(string tradeId, out FrameworkTradeStateSnapshot snapshot)
        {
            lock (syncLock)
            {
                if (activeTrades.TryGetValue(tradeId, out MutableTrade trade))
                {
                    snapshot = trade.ToSnapshot();
                    return true;
                }
            }

            snapshot = null;
            return false;
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

        public bool TrySetStatus(string tradeId, string participantUuid, bool? accepted, bool? cancelled, out FrameworkTradeStateSnapshot snapshot, out bool completed, out bool wasCancelled, out FrameworkItemPayload[] completionItems, out string otherPartyUuid, out FrameworkTradeFailureReason failureReason)
        {
            lock (syncLock)
            {
                completed = false;
                wasCancelled = false;
                completionItems = Array.Empty<FrameworkItemPayload>();
                otherPartyUuid = null;

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
                    completed = true;
                    wasCancelled = true;
                    otherPartyUuid = trade.ParticipantUuids.Single(uuid => uuid != participantUuid);
                    completionItems = trade.GetItemsFor(participantUuid).ToArray();
                    snapshot = trade.ToSnapshot();
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
                    otherPartyUuid = trade.ParticipantUuids.Single(uuid => uuid != participantUuid);
                    completionItems = trade.GetItemsFor(otherPartyUuid).ToArray();
                }

                return true;
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

            public string TradeId { get; }

            public string[] ParticipantUuids { get; }

            public long Version { get; private set; }

            public bool AllAccepted => ParticipantUuids.All(uuid => acceptedParticipants.Contains(uuid));

            public bool TrySetOffer(string participantUuid, IEnumerable<FrameworkItemPayload> items)
            {
                if (!ParticipantUuids.Contains(participantUuid))
                {
                    return false;
                }

                offers[participantUuid] = cloneItems(items);
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

                return cloneItems(items);
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
                            ItemsOnOffer = cloneItems(GetItemsFor(uuid)).ToList()
                        })
                        .ToList()
                };
            }

            private static List<FrameworkItemPayload> cloneItems(IEnumerable<FrameworkItemPayload> items)
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
