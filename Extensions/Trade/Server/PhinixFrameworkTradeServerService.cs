using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UserManagement;
using Utils;
using Utils.Framework;
using ServerRuntime;

namespace Phinix.TradeExtension.Server
{
    public interface IFrameworkTradeServerApi : ILoggable, IPersistent
    {
        void HandleSnapshotRequest(ServerFrameworkContext context);
        void HandleUserLoggedIn(string connectionId, string sessionId, string uuid, Action<string, FrameworkPacket> sendMessage);
        void HandleCreateRequest(FrameworkPacket command, ServerFrameworkContext context);
        void HandleOfferUpdateRequest(FrameworkPacket command, ServerFrameworkContext context);
        void HandleStatusUpdateRequest(FrameworkPacket command, ServerFrameworkContext context);
        void HandleCompletionAckRequest(FrameworkPacket command, ServerFrameworkContext context);
        void CacheItemPacket(string packetId, FrameworkItemPayload payload);
        bool TryGetCachedItemPacket(string packetId, out FrameworkItemPayload payload);
    }

    // Additive contract: unscoped APIs remain for ABI compatibility, but fail closed.
    public interface IScopedFrameworkTradeItemCache
    {
        bool TryCacheItemPacket(string packetId, FrameworkItemPayload payload, ServerFrameworkContext context);
    }

    public sealed partial class PhinixFrameworkTradeServerService : IFrameworkTradeServerApi, IScopedFrameworkTradeItemCache
    {
        private readonly IServerUserManager userManager;
        private readonly IServerSessionReadiness readiness;
        private readonly IServerWorkQueue workQueue;
        private int recoveryGeneration;
        private readonly int recoveryPageSize;
        private readonly bool diagnosticLogging;
        private readonly PhinixFrameworkTradeStore store;
        private readonly Dictionary<(string Owner, string Connection, string Session, string Packet), CachedItem> itemPacketCache = new Dictionary<(string, string, string, string), CachedItem>();
        private const int MaxCachedItemPackets = 200;
        // Encoded payload admission ceilings; not a managed heap or production workload budget.
        private const int MaxItemBytes = 1024 * 1024;
        private const int MaxSessionBytes = 4 * MaxItemBytes;
        private const int MaxCacheBytes = 16 * MaxItemBytes;
        private const int MaxKeyCharacters = 256;
        private const int MaxMetadataEntries = 1024;
        private const long ItemCacheLifetimeMilliseconds = 10 * 60 * 1000;
        private Func<long> itemCacheClock = () => Environment.TickCount64;
        private long nextItemCacheWarning;

        private sealed class CachedItem
        {
            public FrameworkItemPayload Payload;
            public int EncodedBytes;
            public long CreatedAt;
            public Func<bool> ScopeIsCurrent;
        }

        public event EventHandler<LogEventArgs> OnLogEntry;

        public void RaiseLogEntry(LogEventArgs e)
        {
            foreach (EventHandler<LogEventArgs> observer in OnLogEntry?.GetInvocationList() ?? Array.Empty<Delegate>())
                try { observer(this, e); } catch { /* Diagnostics cannot interrupt committed trade outcomes. */ }
        }

        public PhinixFrameworkTradeServerService(IServerUserManager userManager) : this(userManager, null) { }

        public PhinixFrameworkTradeServerService(IServerUserManager userManager, IServerSessionReadiness readiness)
            : this(userManager, readiness, null) { }

        public PhinixFrameworkTradeServerService(IServerUserManager userManager, IServerSessionReadiness readiness, IServerWorkQueue workQueue)
            : this(userManager, readiness, workQueue, new TradeServerConfig()) { }

        public PhinixFrameworkTradeServerService(IServerUserManager userManager, IServerSessionReadiness readiness, IServerWorkQueue workQueue, TradeServerConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();
            this.userManager = userManager; this.readiness = readiness; this.workQueue = workQueue;
            store = new PhinixFrameworkTradeStore(new TradeAdmissionPolicy(config.PrincipalRetainedTradeLimit, config.PrincipalPayloadByteLimit, config.PrincipalAuxiliaryByteLimit,(long)config.StorageMiBLimit*1024*1024));
            recoveryPageSize = config.RecoveryPageSize;
            diagnosticLogging = config.DiagnosticLogging;
        }

        public void CancelPendingRecovery()
        {
            System.Threading.Interlocked.Increment(ref recoveryGeneration);
            lock (recoveryGate) { recoveryContinuations.Clear(); nextRecoveryWarning = 0; }
        }

        internal void ClearItemPacketCache()
        {
            lock (itemPacketCache) { itemPacketCache.Clear(); nextItemCacheWarning = 0; }
        }

        // Staging is not durable ownership. Reclaim abandoned packets before admission,
        // but never evict a live, unexpired batch just to admit another one.
        private void PruneItemPacketCache()
        {
            long now = itemCacheClock();
            foreach (var entry in itemPacketCache.ToArray())
                if (now - entry.Value.CreatedAt >= ItemCacheLifetimeMilliseconds || !CacheScopeCurrent(entry.Value))
                    itemPacketCache.Remove(entry.Key);
        }

        private static bool CacheScopeCurrent(CachedItem item)
        {
            // A stopped/failing optional endpoint cannot poison reclamation for every player.
            try { return item.ScopeIsCurrent == null || item.ScopeIsCurrent(); }
            catch { return false; }
        }

        private Func<bool> CaptureCacheScope(ServerFrameworkContext context)
        {
            string connection = context.ConnectionId, owner = context.SenderUuid, session = context.SessionId;
            if (readiness != null && readiness.TryGetReady(connection, out var expected) &&
                expected.PrincipalId == owner && expected.SessionId == session)
                return () => readiness.TryGetReady(connection, out var current) && current.Epoch == expected.Epoch &&
                    current.PrincipalId == owner && current.SessionId == session;
            if (TryEndpoint(connection, out var endpoint) && endpoint.PrincipalId == owner && endpoint.SessionId == session)
                return () => TryEndpoint(connection, out var current) && current == endpoint;
            // Compatibility callers without a session directory still have bounded staging.
            return null;
        }

        private static byte[] Intent<T>(string kind, T payload) => Encoding.UTF8.GetBytes(kind + "\n" + FrameworkSerialization.SerializePayload(payload));

        [Obsolete("Use IScopedFrameworkTradeItemCache.TryCacheItemPacket with an authenticated context.")]
        public void CacheItemPacket(string packetId, FrameworkItemPayload payload)
        {
            throw new NotSupportedException("An authenticated item cache scope is required.");
        }

        [Obsolete("Unscoped cache reads are refused. Submit an offer with an authenticated context.")]
        public bool TryGetCachedItemPacket(string packetId, out FrameworkItemPayload payload)
        {
            payload = null;
            return false;
        }

        private static bool HasCacheScope(ServerFrameworkContext context) =>
            context != null && IsValidCacheKey(context.SenderUuid) &&
            IsValidCacheKey(context.ConnectionId) && IsValidCacheKey(context.SessionId);

        private static bool IsValidCacheKey(string value) =>
            !string.IsNullOrEmpty(value) && value.Length <= MaxKeyCharacters;

        private static bool FitsRawItemBudget(FrameworkItemPayload payload)
        {
            // Bound serialization input before creating JSON/deep copies. Actual encoded
            // bytes are checked separately, including escaping, metadata and binary expansion.
            if ((payload.Metadata?.Count ?? 0) > MaxMetadataEntries) return false;
            long bytes = payload.PayloadBytes?.LongLength ?? 0;
            if (!AddTextBytes(payload.CodecId, ref bytes) || !AddTextBytes(payload.PayloadJson, ref bytes)) return false;
            foreach (FrameworkMetadataEntry entry in payload.Metadata ?? Enumerable.Empty<FrameworkMetadataEntry>())
                if (entry == null || !AddTextBytes(entry.Key, ref bytes) || !AddTextBytes(entry.Value, ref bytes)) return false;
            return bytes <= MaxItemBytes;
        }

        private static bool AddTextBytes(string value, ref long bytes)
        {
            if (value != null)
            {
                if (value.Length > MaxItemBytes) return false;
                bytes += Encoding.UTF8.GetByteCount(value);
            }
            return bytes <= MaxItemBytes;
        }

        private static bool IsValidItem(FrameworkItemPayload item) =>
            item != null && !string.IsNullOrEmpty(item.CodecId) &&
            (item.Metadata == null || item.Metadata.All(entry => entry != null));

        private static (string, string, string, string) CacheKey(ServerFrameworkContext context, string packetId) =>
            (context.SenderUuid, context.ConnectionId, context.SessionId, packetId);

        public bool TryCacheItemPacket(string packetId, FrameworkItemPayload payload, ServerFrameworkContext context)
        {
            if (!IsValidCacheKey(packetId) || !IsValidItem(payload) || !HasCacheScope(context) || !FitsRawItemBudget(payload)) return false;
            string serialized = FrameworkSerialization.SerializePayload(payload);
            int encodedBytes = Encoding.UTF8.GetByteCount(serialized);
            if (encodedBytes > MaxItemBytes) return false;
            lock (itemPacketCache)
            {
                PruneItemPacketCache();
                var key = CacheKey(context, packetId);
                if (itemPacketCache.TryGetValue(key, out CachedItem existing))
                {
                    // Same ID may be retransmitted without charging twice, but never replaced.
                    return FrameworkSerialization.SerializePayload(existing.Payload) == serialized;
                }
                // At most 200 entries: derive accounting from retained entries under the same lock
                // as offer consumption, so rejection cannot leak or prematurely release a budget.
                long globalBytes = 0, sessionBytes = 0;
                foreach (var entry in itemPacketCache)
                {
                    globalBytes += entry.Value.EncodedBytes;
                    if (entry.Key.Owner == context.SenderUuid && entry.Key.Connection == context.ConnectionId && entry.Key.Session == context.SessionId)
                        sessionBytes += entry.Value.EncodedBytes;
                }
                // Refuse new input without evicting any legitimate reference.
                if (itemPacketCache.Count >= MaxCachedItemPackets || globalBytes + encodedBytes > MaxCacheBytes || sessionBytes + encodedBytes > MaxSessionBytes)
                {
                    long now = itemCacheClock();
                    if (now >= nextItemCacheWarning)
                    {
                        nextItemCacheWarning = now + 30000;
                        try { RaiseLogEntry(new LogEventArgs($"Trade item staging capacity reached: packets={itemPacketCache.Count}/{MaxCachedItemPackets}, globalBytes={globalBytes}/{MaxCacheBytes}, sessionBytes={sessionBytes}/{MaxSessionBytes}, incomingBytes={encodedBytes}.", LogLevel.WARNING)); }
                        catch { /* Diagnostics do not change admission or retain resources. */ }
                    }
                    return false;
                }
                itemPacketCache.Add(key, new CachedItem {
                    Payload = FrameworkSerialization.DeserializePayload<FrameworkItemPayload>(serialized),
                    EncodedBytes = encodedBytes, CreatedAt = itemCacheClock(), ScopeIsCurrent = CaptureCacheScope(context)
                });
                return true;
            }
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
            MarkTerms(response, payload.Trades);
            SendPacket(context.ConnectionId, response, context.SendMessage);
        }

        public void HandleUserLoggedIn(string connectionId, string sessionId, string uuid, Action<string, FrameworkPacket> sendMessage)
        {
            if (sendMessage != null && TryEndpoint(connectionId, out var endpoint) && endpoint.PrincipalId == uuid && endpoint.SessionId == sessionId)
            {
                RecoverEndpoint(endpoint, sendMessage, null, System.Threading.Volatile.Read(ref recoveryGeneration));
                return;
            }
            if (sendMessage == null || readiness == null ||
                !readiness.TryGetReady(connectionId, out var ready) || ready.PrincipalId != uuid || ready.SessionId != sessionId) return;
            RecoverPage(ready, sendMessage, null, System.Threading.Volatile.Read(ref recoveryGeneration));
        }

        private void RecoverPage(ServerSessionReadyEventArgs expected, Action<string, FrameworkPacket> sendMessage, string after, int generation)
        {
            bool Current() => generation == System.Threading.Volatile.Read(ref recoveryGeneration) &&
                readiness.TryGetReady(expected.ConnectionId, out var current) && current.Epoch == expected.Epoch;
            if (!Current()) return;
            PhinixFrameworkTradeStore.PendingCompletionNotification[] page;
            try { page = store.GetPendingCompletionPageFor(expected.PrincipalId, after, recoveryPageSize); }
            catch (ServerStorage.JournalBusyException)
            {
                QueueRecoveryContinuation(expected.ConnectionId, Current, () => RecoverPage(expected, sendMessage, after, generation), 1000);
                return;
            }
            Exception failure = null;
            foreach (var notification in page)
            {
                if (!Current()) return;
                try { SendCompletionEvent(expected.ConnectionId, expected.SessionId, notification, sendMessage); }
                catch (Exception error) { failure ??= error; }
            }
            if (failure != null)
            {
                QueueRecoveryContinuation(expected.ConnectionId, Current, () => RecoverPage(expected, sendMessage, after, generation), 5000);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            if (page.Length == recoveryPageSize && Current())
            {
                string cursor = page[page.Length - 1].DeliveryId;
                QueueRecoveryContinuation(expected.ConnectionId, Current, () => RecoverPage(expected, sendMessage, cursor, generation));
            }
        }

        public void HandleCompletionAckRequest(FrameworkPacket command, ServerFrameworkContext context)
        {
            FrameworkTradeCompletionAckRequest request =
                FrameworkSerialization.DeserializePayload<FrameworkTradeCompletionAckRequest>(command.PayloadJson);
            bool accepted = request != null && store.Execute(context.SenderUuid, command.MessageId,
                Intent("ack", request), candidate => new TradeOperationResult { Success = candidate.TryAcknowledgeCompletion(request.TradeId, context.SenderUuid, request.Cancelled) },recovery:true).Success;
            FrameworkTradeCompletionAckResponse response = new FrameworkTradeCompletionAckResponse
            {
                TradeId = request?.TradeId,
                Accepted = accepted,
                FailureMessage = accepted ? null : "No matching pending trade completion was found."
            };
            FrameworkPacket packet = new FrameworkPacket
            {
                Flow = global::Phinix.Framework.FrameworkFlow.Command,
                CommandKind = global::Phinix.Framework.FrameworkCommandKind.Response,
                MessageType = FrameworkTradeProtocol.CompletionAckResponseType,
                SessionId = context.SessionId,
                SenderUuid = FrameworkProtocol.SystemSenderUuid,
                PayloadJson = FrameworkSerialization.SerializePayload(response)
            };
            packet.SetCorrelationId(command.GetCorrelationId());
            SendPacket(context.ConnectionId, packet, context.SendMessage);
        }

        public void HandleCreateRequest(FrameworkPacket command, ServerFrameworkContext context)
            {
            var request = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateRequest>(command.PayloadJson);
            bool basic = (TryEndpoint(context.ConnectionId, out var senderEndpoint) && senderEndpoint.Confirmed) ||
                (request != null && !string.IsNullOrEmpty(request.OtherPartyUuid) && userManager.TryGetConnection(request.OtherPartyUuid, out var other) && TryEndpoint(other, out var otherEndpoint) && otherEndpoint.Confirmed);
            HandleCreateWithTerms(command, context, basic ? TradeNegotiationTerms.BasicItemsCurrentQuote : TradeNegotiationTerms.ObservedQuote);
        }

        public void HandleCreateWithTerms(FrameworkPacket command, ServerFrameworkContext context, TradeNegotiationTerms terms)
        {
            if (!Enum.IsDefined(terms)) throw new ArgumentOutOfRangeException(nameof(terms));
            FrameworkTradeCreateRequest payload = FrameworkSerialization.DeserializePayload<FrameworkTradeCreateRequest>(command.PayloadJson);
            if (payload == null || string.IsNullOrEmpty(payload.OtherPartyUuid))
            {
                SendCreateResponse(context.ConnectionId, context, false, null, null, FrameworkTradeFailureReason.InternalServerError, "Trade request payload was empty.");
                return;
            }

            byte[] intent = Intent("create", payload);
            var replay = store.TryReplay(context.SenderUuid, command.MessageId, intent);
            if (replay != null)
            {
                SendCreateResponse(context.ConnectionId, context, replay.Success, replay.Snapshot?.TradeId, payload.OtherPartyUuid, replay.Failure, replay.FailureMessage, command.GetCorrelationId());
                return;
            }
            if (terms == TradeNegotiationTerms.BasicItemsCurrentQuote && basicItemProjection == null && System.Threading.Volatile.Read(ref endpointBinding) == null)
                throw new InvalidOperationException("BasicItemProjectionUnavailable");
            if (payload.OtherPartyUuid == context.SenderUuid) throw new InvalidOperationException("Trade participants must differ.");

            if (!userManager.TryGetLoggedIn(payload.OtherPartyUuid, out bool otherPartyLoggedIn))
            {
                SendCreateFailureDurably(command, context, payload, intent, FrameworkTradeFailureReason.OtherPartyDoesNotExist, "The other party does not exist.");
                return;
            }

            if (!otherPartyLoggedIn)
            {
                SendCreateFailureDurably(command, context, payload, intent, FrameworkTradeFailureReason.OtherPartyOffline, "The other party is offline.");
                return;
            }

            if (!userManager.TryGetAcceptingTrades(payload.OtherPartyUuid, out bool acceptingTrades) || !acceptingTrades)
            {
                SendCreateFailureDurably(command, context, payload, intent, FrameworkTradeFailureReason.NotAcceptingTrades, "The other party is not accepting trades.");
                return;
            }

            if (!userManager.TryGetConnection(payload.OtherPartyUuid, out string otherPartyConnectionId))
            {
                SendCreateFailureDurably(command, context, payload, intent, FrameworkTradeFailureReason.InternalServerError, "The server could not find the other party connection.");
                return;
            }

            // Transport admission, before creating any asset-bearing state. Capabilities
            // are expressibility facts, never authorization or evidence of game delivery.
            if (!(terms == TradeNegotiationTerms.BasicItemsCurrentQuote && TryEndpoint(otherPartyConnectionId, out var endpoint) && endpoint.PrincipalId == payload.OtherPartyUuid) &&
                ((context.IsConnectionFrameworkCapable != null && !context.IsConnectionFrameworkCapable(otherPartyConnectionId)) ||
                (context.ConnectionHasCapability != null && !context.ConnectionHasCapability(otherPartyConnectionId, FrameworkTradeProtocol.Capability))))
            {
                SendCreateFailureDurably(command, context, payload, intent, FrameworkTradeFailureReason.NotAcceptingTrades,
                    "The other party has not negotiated the required trading capability; update the client and reconnect.");
                return;
            }

            var created = store.Execute(context.SenderUuid, command.MessageId, intent, candidate =>
            {
                bool ok = candidate.TryCreateWithTerms(context.SenderUuid, payload.OtherPartyUuid, out var state, out var failure, terms);
                return new TradeOperationResult { Success = ok, Snapshot = state, Failure = failure,
                    FailureMessage = ok ? null : failure == FrameworkTradeFailureReason.AlreadyTrading
                        ? "A trade with this party is already active." : "Trade creation refused: insufficient durable ledger capacity or participant quota." };
            });
            FrameworkTradeStateSnapshot snapshot = created.Snapshot;
            FrameworkTradeFailureReason failureReason = created.Failure;
            if (!created.Success)
            {
                SendCreateResponse(context.ConnectionId, context, false, null, payload.OtherPartyUuid, failureReason, created.FailureMessage, command.GetCorrelationId());
                return;
            }

            if (created.Replayed)
            { SendCreateResponse(context.ConnectionId, context, true, snapshot.TradeId, payload.OtherPartyUuid, FrameworkTradeFailureReason.None, null, command.GetCorrelationId()); return; }
            NotifyAll(
                () => SendCreateResponse(context.ConnectionId, context, true, snapshot.TradeId, payload.OtherPartyUuid, FrameworkTradeFailureReason.None, null, command.GetCorrelationId()),
                () => SendCreateResponse(otherPartyConnectionId, context, true, snapshot.TradeId, context.SenderUuid, FrameworkTradeFailureReason.None, null, command.GetCorrelationId()),
                () => SendSnapshot(context.ConnectionId, context, snapshot, command.GetCorrelationId()),
                () => SendSnapshot(otherPartyConnectionId, context, snapshot, command.GetCorrelationId()));
        }

        public void HandleOfferUpdateRequest(FrameworkPacket command, ServerFrameworkContext context)
        {
            lock (itemPacketCache)
            {
                PruneItemPacketCache();
                HandleOfferUpdateRequestCore(command, context);
            }
        }

        private void HandleOfferUpdateRequestCore(FrameworkPacket command, ServerFrameworkContext context)
        {
            FrameworkTradeOfferUpdateRequest payload = FrameworkSerialization.DeserializePayload<FrameworkTradeOfferUpdateRequest>(command.PayloadJson);
            if (payload == null || string.IsNullOrEmpty(payload.TradeId))
            {
                SendOfferUpdateResponse(context.ConnectionId, context, false, null, Array.Empty<FrameworkItemPayload>(), FrameworkTradeFailureReason.TradeDoesNotExist, "Trade does not exist.", command.GetCorrelationId());
                return;
            }

            byte[] intent = Intent("offer", payload);
            var replay = store.TryReplay(context.SenderUuid, command.MessageId, intent);
            if (replay != null)
            {
                // Do not consume references staged after the original committed attempt.
                lock (itemPacketCache)
                {
                    int index = 0;
                    foreach (string id in payload.ItemPacketRefs ?? new List<string>())
                    {
                        if (replay.Success && itemPacketCache.TryGetValue(CacheKey(context, id), out var cached) &&
                            !MatchesReplayedItem(replay, index, cached.Payload))
                            throw new ServerStorage.OperationConflictException();
                        index++;
                    }
                }
                SendOfferUpdateResponse(context.ConnectionId, context, replay.Success, payload.TradeId, replay.Items ?? new List<FrameworkItemPayload>(), replay.Failure, replay.FailureMessage, command.GetCorrelationId());
                return;
            }
            FrameworkTradeStateSnapshot snapshot;
            List<FrameworkItemPayload> items;
            FrameworkTradeFailureReason failureReason;
            string failureMessage = null;
            bool accepted;
            // Cache resolution and consumption are serialized. The store revalidates the target
            // at mutation time, so concurrent cancellation also leaves references untouched.
            lock (itemPacketCache)
            {
                accepted = store.CanSetOffer(payload.TradeId, context.SenderUuid, out failureReason);
                items = null;
                if (accepted && !TryResolveOfferItems(payload, context, out items))
                {
                    accepted = false;
                    failureReason = FrameworkTradeFailureReason.InternalServerError;
                    failureMessage = "Trade item batch is missing, expired or invalid. Submit a new complete offer.";
                }
                List<FrameworkItemPayload> sourceItems = items;
                if (accepted)
                {
                    try { items = ProjectOffer(payload.TradeId, items); }
                    catch (System.IO.InvalidDataException)
                    {
                        accepted = false;
                        failureReason = FrameworkTradeFailureReason.InternalServerError;
                        failureMessage = "Trade items could not be converted for the selected trade terms.";
                    }
                }
                snapshot = null;
                if (accepted)
                {
                    var changed = store.Execute(context.SenderUuid, command.MessageId, intent, candidate =>
                    {
                        bool ok = candidate.TrySetOffer(payload.TradeId, context.SenderUuid, items, out var state, out var failure);
                        return new TradeOperationResult { Success = ok, Snapshot = state, Failure = failure, Items = ok ? items : null,
                            SourceItemFingerprints = ok && candidate.GetTerms(payload.TradeId) == TradeNegotiationTerms.BasicItemsCurrentQuote ? sourceItems.Select(ItemFingerprint).ToList() : null,
                            FailureMessage = !ok && failure == FrameworkTradeFailureReason.InternalServerError
                                ? "Trade offer refused: insufficient durable ledger capacity or participant quota." : null };
                    });
                    accepted = changed.Success; snapshot = changed.Snapshot; failureReason = changed.Failure;
                    if (changed.Replayed)
                    {
                        SendOfferUpdateResponse(context.ConnectionId, context, changed.Success, payload.TradeId, changed.Items ?? new List<FrameworkItemPayload>(), changed.Failure, changed.FailureMessage, command.GetCorrelationId());
                        return;
                    }
                }
                if (accepted)
                {
                    foreach (string packetId in payload.ItemPacketRefs ?? new List<string>())
                        itemPacketCache.Remove(CacheKey(context, packetId));
                }
            }

            if (!accepted)
            {
                var rejection = store.Execute(context.SenderUuid, command.MessageId, intent, _ => new TradeOperationResult { Success = false, Failure = failureReason, FailureMessage = failureMessage });
                SendOfferUpdateResponse(context.ConnectionId, context, rejection.Success, payload.TradeId,
                    rejection.Items ?? new List<FrameworkItemPayload>(), rejection.Failure, rejection.FailureMessage ?? "Trade offer rejected: invalid target or incomplete item batch.", command.GetCorrelationId());
                return;
            }

            NotifyAll(
                () => SendOfferUpdateResponse(context.ConnectionId, context, true, payload.TradeId, items, FrameworkTradeFailureReason.None, null, command.GetCorrelationId()),
                () => BroadcastSnapshot(snapshot, context, payload.TradeId, command.GetCorrelationId()));
        }

        // The adapter translates historical wire representations; only this boundary
        // resolves authenticated cache references. The domain receives one validated batch.
        private bool TryResolveOfferItems(FrameworkTradeOfferUpdateRequest payload, ServerFrameworkContext context, out List<FrameworkItemPayload> items)
        {
            FrameworkItemPayload Resolve(string id) => HasCacheScope(context) && itemPacketCache.TryGetValue(CacheKey(context, id), out var cached)
                ? cached.Payload : null;
            return LegacyTradeOfferAdapter.TryTranslate(payload, Resolve, IsValidItem, out items);
        }

        public void HandleStatusUpdateRequest(FrameworkPacket command, ServerFrameworkContext context)
        {
            FrameworkTradeStatusUpdateRequest payload = FrameworkSerialization.DeserializePayload<FrameworkTradeStatusUpdateRequest>(command.PayloadJson);
            if (payload == null || string.IsNullOrEmpty(payload.TradeId))
            {
                SendStatusUpdateResponse(context.ConnectionId, context, false, null, FrameworkTradeFailureReason.TradeDoesNotExist, "Trade does not exist.", command.GetCorrelationId());
                return;
            }


            if (diagnosticLogging) RaiseLogEntry(new LogEventArgs($"[TradeServer] HandleStatusUpdate: tradeId={payload.TradeId}, from={context.SenderUuid}, accepted={payload.Accepted}, cancelled={payload.Cancelled}, connectionId={context.ConnectionId}", LogLevel.INFO));

            long? observedVersion = command.TryGetSnapshotVersion(out long observed) ? observed : (long?)null;
            string statusIntent = observedVersion.HasValue
                ? "status@" + observedVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "status"; // Preserve fingerprints/results already committed before revision binding.
            var changed = store.Execute(context.SenderUuid, command.MessageId, Intent(statusIntent, payload), candidate =>
            {
                bool ok = candidate.TrySetStatusAtVersion(payload.TradeId, context.SenderUuid, payload.Accepted, payload.Cancelled, observedVersion, out var state, out bool done, out bool cancelled, out var failure);
                if (ok && done) candidate.QueueCompletionNotifications(state, cancelled);
                return new TradeOperationResult { Success = ok, Snapshot = state, Failure = failure, Completed = done, Cancelled = cancelled };
            },recovery:true);
            FrameworkTradeStateSnapshot snapshot = changed.Snapshot;
            bool completed = changed.Completed, wasCancelled = changed.Cancelled;
            FrameworkTradeFailureReason failureReason = changed.Failure;
            if (!changed.Success)
            {
                RaiseLogEntry(new LogEventArgs($"[TradeServer] HandleStatusUpdate: FAILED tradeId={payload.TradeId}, reason={failureReason}", LogLevel.WARNING));
                SendStatusUpdateResponse(context.ConnectionId, context, false, payload.TradeId, failureReason, "Trade status update refused. Refresh the quote before confirming.", command.GetCorrelationId());
                if (payload.Accepted == true) HandleSnapshotRequest(context);
                return;
            }


            if (diagnosticLogging) RaiseLogEntry(new LogEventArgs($"[TradeServer] HandleStatusUpdate: SUCCESS tradeId={payload.TradeId}, completed={completed}", LogLevel.INFO));

            NotifyAll(() => SendStatusUpdateResponse(
                context.ConnectionId,
                context,
                true,
                payload.TradeId,
                FrameworkTradeFailureReason.None,
                null,
                command.GetCorrelationId() == command.MessageId ? payload.TradeId : command.GetCorrelationId()),
                () =>
                {
                    if (changed.Replayed) return;
                    if (completed) SendCompletionEvents(snapshot, context, wasCancelled, command.GetCorrelationId());
                    else BroadcastSnapshot(snapshot, context, payload.TradeId, command.GetCorrelationId());
                });

        }

        private static void NotifyAll(params Action[] notifications)
        {
            Exception failure = null;
            foreach (var notify in notifications) try { notify(); } catch (Exception error) { failure ??= error; }
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private void BroadcastSnapshot(FrameworkTradeStateSnapshot snapshot, ServerFrameworkContext context, string tradeId, string correlationId)
        {
            if (snapshot == null)
            {
                return;
            }

            var notifications = new List<Action>();
            foreach (FrameworkTradeParticipantSnapshot participant in snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
            {
                if (!userManager.TryGetConnection(participant.Uuid, out string connectionId))
                {
                    RaiseLogEntry(new LogEventArgs(
                        $"[TradeServer] BroadcastSnapshot: SKIPPED uuid={participant.Uuid} — no connection found",
                        LogLevel.WARNING));
                    continue;
                }

                if (diagnosticLogging) RaiseLogEntry(new LogEventArgs($"[TradeServer] BroadcastSnapshot: sending snapshot to uuid={participant.Uuid} via connectionId={connectionId}", LogLevel.INFO));
                notifications.Add(() => SendSnapshot(connectionId, context, snapshot, correlationId));
            }
            NotifyAll(notifications.ToArray());
        }

        private void SendSnapshot(string connectionId, ServerFrameworkContext context, FrameworkTradeStateSnapshot snapshot, string correlationId)
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
            MarkTerms(packet, payload.Trades);
            SendPacket(connectionId, packet, context.SendMessage);
        }

        private void SendCreateFailureDurably(FrameworkPacket command, ServerFrameworkContext context, FrameworkTradeCreateRequest request, byte[] intent, FrameworkTradeFailureReason failure, string message)
        {
            var result = store.Execute(context.SenderUuid, command.MessageId, intent, _ => new TradeOperationResult { Success = false, Failure = failure, FailureMessage = message });
            SendCreateResponse(context.ConnectionId, context, result.Success, result.Snapshot?.TradeId, request.OtherPartyUuid, result.Failure, result.FailureMessage, command.GetCorrelationId());
        }

        private void SendCreateResponse(string connectionId, ServerFrameworkContext context, bool success, string tradeId, string otherPartyUuid, FrameworkTradeFailureReason failureReason, string failureMessage, string correlationId = null)
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
            SendPacket(connectionId, packet, context.SendMessage);
        }

        private void SendOfferUpdateResponse(string connectionId, ServerFrameworkContext context, bool success, string tradeId, IEnumerable<FrameworkItemPayload> items, FrameworkTradeFailureReason failureReason, string failureMessage, string correlationId)
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
            SendPacket(connectionId, packet, context.SendMessage);
        }

        private void SendStatusUpdateResponse(string connectionId, ServerFrameworkContext context, bool success, string tradeId, FrameworkTradeFailureReason failureReason, string failureMessage, string correlationId)
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
            SendPacket(connectionId, packet, context.SendMessage);
        }

        private void SendCompletionEvents(FrameworkTradeStateSnapshot snapshot, ServerFrameworkContext context, bool cancelled, string correlationId)
        {
            if (snapshot == null)
            {
                return;
            }

            var notifications = new List<Action>();
            foreach (FrameworkTradeParticipantSnapshot participant in snapshot.Participants ?? new List<FrameworkTradeParticipantSnapshot>())
            {
                if (!userManager.TryGetConnection(participant.Uuid, out string connectionId))
                {
                    RaiseLogEntry(new LogEventArgs(
                        $"[TradeServer] SendCompletionEvents: SKIPPED uuid={participant.Uuid} — no connection found",
                        LogLevel.WARNING));
                    continue;
                }

                PhinixFrameworkTradeStore.PendingCompletionNotification notification = store
                    .GetCompletionNotificationFor(snapshot.TradeId, participant.Uuid);
                if (notification == null)
                {
                    RaiseLogEntry(new LogEventArgs(
                        $"[TradeServer] SendCompletionEvents: SKIPPED uuid={participant.Uuid} — no pending notification for tradeId={snapshot.TradeId}",
                        LogLevel.WARNING));
                    continue;
                }

                if (diagnosticLogging) RaiseLogEntry(new LogEventArgs($"[TradeServer] SendCompletionEvents: sending pending completion for tradeId={snapshot.TradeId} to uuid={participant.Uuid}", LogLevel.INFO));
                notifications.Add(() => SendCompletionEvent(connectionId, context.SessionId, notification, context.SendMessage, correlationId));
            }
            NotifyAll(notifications.ToArray());
        }

        public void Save(string path)
        {
            store.Save(path);
            RaiseLogEntry(new LogEventArgs("Saved framework trade state."));
        }

        public void Load(string path)
        {
            store.LoadDurable(path);
            RaiseLogEntry(new LogEventArgs("Loaded framework trade state."));
        }

        private void SendCompletionEvent(string connectionId, string sessionId, PhinixFrameworkTradeStore.PendingCompletionNotification notification, Action<string, FrameworkPacket> sendMessage, string correlationId = null)
        {
            if (sendMessage == null) return;
            string receiptContract=null,epoch=null;
            if (TryEndpoint(connectionId, out var endpoint))
            {
                if (endpoint.PrincipalId != notification.RecipientUuid || (!endpoint.Confirmed && notification.Items.Count != 0)) return;
                sessionId = endpoint.SessionId;
            }
            else if (readiness != null)
            {
                string type = notification.Cancelled ? FrameworkTradeProtocol.CancelledEventType : FrameworkTradeProtocol.CompletedEventType;
                if (!readiness.TryGetReady(connectionId, out var ready) || ready.PrincipalId != notification.RecipientUuid || !ready.Capabilities.Contains(type)) return;
                sessionId = ready.SessionId;epoch=ready.Epoch;
                if(ready.Capabilities.Contains(TradeDeliveryProtocol.Request)&&ready.Capabilities.Contains(TradeDeliveryProtocol.Response))receiptContract=TradeDeliveryProtocol.Contract;
            }
            else if (store.IsDurable) return;
            if (!store.TryBeginDelivery(notification.TradeId, notification.RecipientUuid, notification.Cancelled,receiptContract,epoch)) return;
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
            if(receiptContract!=null){packet.SetMetadataValue(TradeDeliveryProtocol.IdKey,notification.DeliveryId);
                packet.SetMetadataValue(TradeDeliveryProtocol.HashKey,notification.PayloadFingerprint());packet.SetMetadataValue(TradeDeliveryProtocol.ContractKey,receiptContract);}
            packet.SetCorrelationId(correlationId ?? Guid.NewGuid().ToString());
            SendPacket(connectionId, packet, sendMessage);
        }
    }
}
