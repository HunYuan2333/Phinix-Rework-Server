using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using ServerStorage;
using Utils.Framework;

namespace Phinix.TradeExtension.Server;

[DataContract]
internal sealed class TradeOperationResult
{
    [DataMember(Order = 0)] public bool Success { get; set; }
    [DataMember(Order = 1)] public FrameworkTradeStateSnapshot Snapshot { get; set; }
    [DataMember(Order = 2)] public FrameworkTradeFailureReason Failure { get; set; }
    [DataMember(Order = 3)] public bool Completed { get; set; }
    [DataMember(Order = 4)] public bool Cancelled { get; set; }
    [DataMember(Order = 5)] public List<FrameworkItemPayload> Items { get; set; }
    [DataMember(Order = 6)] public string FailureMessage { get; set; }
    [DataMember(Order = 7, EmitDefaultValue = false)] public List<string> SourceItemFingerprints { get; set; }
    public bool Replayed { get; set; }
}

internal sealed partial class PhinixFrameworkTradeStore
{
    private SqliteOperationJournal journal;
    private bool persistenceFailed;
    private bool consentMigrationRequired;
    private void EnsurePersistence()
    {
        if (persistenceFailed) throw new IOException("Trade persistence unavailable; outcome unknown.");
        if (journal != null)
        {
            try
            {
                VerifyAuthority(authorityPath);
                VerifyRevision(journal.ReadState(RevisionKey));
            }
            catch { persistenceFailed = true; throw; }
        }
    }
    public bool IsDurable => journal != null;
    private string authorityPath;
    private const string StateKey = "trade-ledger-v1";
    private const int MaxStateBytes = 4 * 1024 * 1024;
    private const string PendingIndexKey = "trade-pending-index-v1";
    private const string RevisionKey = "trade-live-revision-v1";
    private byte[] liveRevision;
    private Dictionary<string, PendingCompletionNotification> pendingByDelivery = new(StringComparer.Ordinal);

    private static byte[] Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractSerializer(typeof(T)).WriteObject(stream, value); return stream.ToArray();
    }
    private static T Deserialize<T>(byte[] bytes)
    {
        if (bytes == null || bytes.Length > MaxStateBytes) throw new InvalidDataException("Trade ledger missing or oversized.");
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 16 * 1024 * 1024 });
        return (T)new DataContractSerializer(typeof(T)).ReadObject(reader);
    }
    private byte[] ExportState() => Serialize(new FrameworkTradeStoreState
    {
        ActiveTrades = activeTrades.Values.Select(t => t.ToSnapshot()).ToList(),
        PendingCompletionNotifications = pendingCompletionNotifications.Select(n => n.ToState()).ToList(),
        TerminalTrades = terminalTrades.ToList(),
        ConsentSchemaVersion = 1,
        NegotiationTerms = negotiationTerms.Count == 0 ? null : new(negotiationTerms, StringComparer.OrdinalIgnoreCase),
        OfferVersions = activeTrades.Values.ToDictionary(t => t.TradeId, t => t.OfferVersion, StringComparer.OrdinalIgnoreCase)
    });
    private void ImportState(byte[] bytes)
    {
        var state = Deserialize<FrameworkTradeStoreState>(bytes) ?? throw new InvalidDataException("Missing trade state.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in (state.ActiveTrades ?? new()).Concat(state.TerminalTrades ?? new()))
        {
            ValidId(snapshot?.TradeId);
            if (!ids.Add(snapshot.TradeId) || snapshot.SnapshotVersion < 1 || snapshot.Participants?.Count != 2 ||
                string.Equals(snapshot.Participants[0]?.Uuid, snapshot.Participants[1]?.Uuid, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid trade snapshot.");
            foreach (var participant in snapshot.Participants) { ValidId(participant?.Uuid); ValidateItems(participant.ItemsOnOffer); }
        }
        var deliveries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deliveryCursors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var notification in state.PendingCompletionNotifications ?? new())
        {
            ValidId(notification?.TradeId); ValidId(notification.RecipientUuid); ValidId(notification.OtherPartyUuid);
            if (notification.RecipientUuid == notification.OtherPartyUuid || !deliveries.Add(notification.TradeId + ":" + notification.RecipientUuid) ||
                (notification.DeliveryState != null && notification.DeliveryState != "Pending" && notification.DeliveryState != "OutcomeUnknown" && notification.DeliveryState != "Confirmed")) throw new InvalidDataException("Invalid pending delivery.");
            if (notification.DeliveryId != null)
            {
                ValidId(notification.DeliveryId);
                if (notification.DeliveryId.Length > 120 ||
                    !deliveryCursors.Add(notification.RecipientUuid.ToUpperInvariant() + "\0" + notification.DeliveryId))
                    throw new InvalidDataException("Invalid or duplicate delivery cursor.");
            }
            else if (notification.DeliveryState == "Pending") throw new InvalidDataException("Pending delivery cursor missing.");
            if(notification.ReceiptContract!=null && notification.ReceiptContract!=TradeDeliveryProtocol.Contract)throw new InvalidDataException("Unsupported receipt contract.");
            if(notification.ReceiptContract==null && (notification.ReceiptKind!=null||notification.ReceiptSave!=null||notification.ReceiptCheckpoint!=null||notification.LastDispatchEpoch!=null||notification.DeliveryState=="Confirmed"))throw new InvalidDataException("Receipt contract missing.");
            if(notification.ReceiptContract!=null && (!Guid.TryParseExact(notification.DeliveryId,"N",out _)||string.IsNullOrWhiteSpace(notification.LastDispatchEpoch)))throw new InvalidDataException("Invalid modern delivery identity.");
            if(notification.ReceiptKind!=null && notification.ReceiptKind!="InboxStored"&&notification.ReceiptKind!="SavedInventory")throw new InvalidDataException("Invalid receipt kind.");
            if(notification.ReceiptKind=="SavedInventory" ? !TradeDeliveryProtocol.Hash(notification.ReceiptSave)||!TradeDeliveryProtocol.Hash(notification.ReceiptCheckpoint)||notification.DeliveryState!="Confirmed" : notification.ReceiptSave!=null||notification.ReceiptCheckpoint!=null)throw new InvalidDataException("Invalid saved receipt evidence.");
            if(notification.DeliveryState=="Confirmed" && notification.ReceiptKind!="SavedInventory" && !(notification.ReceiptKind=="InboxStored"&&notification.Items?.Count==0))throw new InvalidDataException("Confirmation evidence missing.");
            ValidateItems(notification.Items);
        }
        // Materialize everything before replacing the current view; invalid records are never filtered.
        var active = (state.ActiveTrades ?? new()).Select(MutableTrade.FromSnapshot).ToArray();
        if (state.ConsentSchemaVersion != 0 && state.ConsentSchemaVersion != 1) throw new InvalidDataException("Unsupported consent schema.");
        consentMigrationRequired = state.ConsentSchemaVersion == 0;
        if (!consentMigrationRequired && (state.OfferVersions == null || state.OfferVersions.Count != active.Length)) throw new InvalidDataException("Missing offer revisions.");
        foreach (var trade in active)
        {
            if (consentMigrationRequired) trade.ResetUnboundConsent();
            else
            {
                if (!state.OfferVersions.TryGetValue(trade.TradeId, out long version) || version < 1 || version > trade.Version) throw new InvalidDataException("Invalid offer revision.");
                trade.OfferVersion = version;
            }
        }
        var terms = new Dictionary<string, TradeNegotiationTerms>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in state.NegotiationTerms ?? new())
        {
            if (!ids.Contains(pair.Key) || pair.Value != TradeNegotiationTerms.BasicItemsCurrentQuote || !terms.TryAdd(pair.Key, pair.Value))
                throw new InvalidDataException("Invalid trade negotiation terms.");
        }
        var pending = (state.PendingCompletionNotifications ?? new()).Select(PendingCompletionNotification.FromState).ToArray();
        var indexed = new Dictionary<string, PendingCompletionNotification>(StringComparer.Ordinal);
        foreach (var notification in pending.Where(n => n.Recoverable))
            if (!indexed.TryAdd(notification.DeliveryId, notification)) throw new InvalidDataException("Duplicate pending delivery identity.");
        activeTrades.Clear(); foreach (var trade in active) activeTrades.Add(trade.TradeId, trade);
        pendingCompletionNotifications.Clear(); pendingCompletionNotifications.AddRange(pending);
        terminalTrades.Clear(); terminalTrades.AddRange(state.TerminalTrades ?? new());
        pendingByDelivery = indexed;
        negotiationTerms.Clear(); foreach (var pair in terms) negotiationTerms.Add(pair.Key, pair.Value);
    }
    private static void ValidId(string id)
    { if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Contains('\0')) throw new InvalidDataException("Invalid trade identity."); }
    private static void ValidateItems(IEnumerable<FrameworkItemPayload> items)
    {
        foreach (var item in items ?? Enumerable.Empty<FrameworkItemPayload>())
            if (item == null || string.IsNullOrWhiteSpace(item.CodecId) || item.Metadata?.Any(e => e == null) == true) throw new InvalidDataException("Invalid opaque item.");
    }

    public void LoadDurable(string path)
    {
        lock (syncLock)
        {
            persistenceFailed = true;
            path = Path.GetFullPath(path);
            string database = path + ".sqlite3", marker = path + ".authority";
            if (!File.Exists(database) && !File.Exists(marker))
            {
                if (!File.Exists(path)) new PhinixFrameworkTradeStore().Save(path); // New installation only; never truncate existing input.
                if (new FileInfo(path).Length > MaxStateBytes) throw new InvalidDataException("Trade import exceeds journal budget.");
                byte[] source = File.ReadAllBytes(path); var imported = new PhinixFrameworkTradeStore(); imported.ImportState(source);
                foreach (var pending in imported.pendingCompletionNotifications) { pending.DeliveryState = "OutcomeUnknown"; pending.AttemptId = null; }
                string temporary = database + ".importing";
                var candidate = SqliteOperationJournal.CreateNew(temporary);
                byte[] hash = SHA256.HashData(source);
                candidate.Execute("system.import", "import-v1", hash, tx =>
                { tx.WriteState(StateKey, imported.ExportState()); tx.WriteState("source-hash", hash); return hash; });
                // Marker first: a crash before rename fails closed and preserves the candidate for inspection.
                AtomicFileSnapshot.Write(marker, s => s.Write(Encoding.ASCII.GetBytes(Convert.ToHexString(hash))));
                File.Move(temporary, database);
            }
            authorityPath = path;
            journal = SqliteOperationJournal.OpenExisting(database);
            VerifyAuthority(path);
            ImportState(journal.ReadState(StateKey));
            if (consentMigrationRequired)
            {
                journal.Execute("system.migration", "consent-version-v1", Encoding.UTF8.GetBytes("consent-version-v1"), tx =>
                {
                    var upgraded = new PhinixFrameworkTradeStore(); upgraded.ImportState(tx.ReadState(StateKey));
                    tx.WriteState(StateKey, upgraded.ExportState()); return new byte[] { 1 };
                });
                ImportState(journal.ReadState(StateKey));
                if (consentMigrationRequired) throw new InvalidDataException("Consent migration did not persist.");
            }
            InitializePendingIndex();
            liveRevision = journal.ReadState(RevisionKey);
            if (liveRevision == null)
            {
                byte[] initial = RandomNumberGenerator.GetBytes(32);
                journal.Execute("system.migration", "live-revision-v1", new byte[] { 1 }, tx =>
                { tx.WriteState(RevisionKey, initial); return new byte[] { 1 }; });
                liveRevision = journal.ReadState(RevisionKey);
            }
            if (liveRevision?.Length != 32) throw new InvalidDataException("Invalid live authority revision.");
            persistenceFailed = false;
        }
    }
    private void VerifyAuthority(string path)
    {
        if (Path.GetFullPath(path) != authorityPath || !File.Exists(path + ".authority") || !File.Exists(path) ||
            new FileInfo(path + ".authority").Length > 128) throw new IOException("Trade authority evidence missing.");
        if (new FileInfo(path).Length > MaxStateBytes) throw new IOException("Trade import evidence oversized.");
        byte[] expected = journal.ReadState("source-hash");
        if (expected == null || !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(File.ReadAllBytes(path))) ||
            File.ReadAllText(path + ".authority") != Convert.ToHexString(expected)) throw new IOException("Trade import evidence changed.");
    }
    public TradeOperationResult TryReplay(string principal, string operation, byte[] intent)
    {
        lock (syncLock)
        {
            EnsurePersistence();
            if (journal == null) return null;
            var committed = journal.TryReadCommitted(principal, operation, intent);
            if (committed == null) return null;
            var result = Deserialize<TradeOperationResult>(committed.Result); result.Replayed = true; return result;
        }
    }
    public TradeOperationResult Execute(string principal, string operation, byte[] intent, Func<PhinixFrameworkTradeStore, TradeOperationResult> apply,bool recovery=false)
    {
        lock (syncLock)
        {
            EnsurePersistence();
            if (journal == null) return apply(this); // Existing component-only, unhosted API; no durability claim.
            byte[] nextRevision = RandomNumberGenerator.GetBytes(32);
            var committed = journal.Execute(principal, operation, intent, tx =>
            {
                // Recheck under the SQLite writer lock, closing the gap between
                // admission and BEGIN when another process changes/replaces the DB.
                VerifyRevision(tx.ReadState(RevisionKey));
                var candidate = new PhinixFrameworkTradeStore(admissionPolicy); candidate.ImportState(tx.ReadState(StateKey));
                var before = PendingEvidence(candidate);
                var result = apply(candidate);
                candidate.ArchiveConfirmed(tx);
                byte[] state = candidate.ExportState();
                new PhinixFrameworkTradeStore().ImportState(state); // Validate the entire proposed ledger before commit.
                tx.WriteState(StateKey, state);
                UpdatePendingIndex(tx, before, PendingEvidence(candidate));
                tx.WriteState(RevisionKey, nextRevision);
                byte[] resultBytes=Serialize(result);
                if(admissionPolicy.StorageBytes>0)tx.EnsureStorageBudget(admissionPolicy.StorageBytes-(recovery?0:16L*1024*1024),resultBytes.Length+principal.Length*4+operation.Length*4+32);
                return resultBytes;
            },admissionPolicy.StorageBytes);
            if (!committed.Replayed) liveRevision = nextRevision;
            EnsurePersistence();
            ImportState(journal.ReadState(StateKey));
            var response = Deserialize<TradeOperationResult>(committed.Result); response.Replayed = committed.Replayed; return response;
        }
    }

    private void VerifyRevision(byte[] persisted)
    {
        if (liveRevision == null || persisted == null || !CryptographicOperations.FixedTimeEquals(liveRevision, persisted))
        {
            persistenceFailed = true;
            throw new IOException("Live trade authority changed; reload after inspection.");
        }
    }

    // Only a derived hash/recipient index lives in the neutral outbox. The opaque
    // asset batch, Unknown and weak ACK evidence remain in the authoritative state.
    private static Dictionary<string, (string Recipient, byte[] Hash)> PendingEvidence(PhinixFrameworkTradeStore store)
    {
        var entries = new Dictionary<string, (string, byte[])>(StringComparer.Ordinal);
        foreach (var notification in store.pendingCompletionNotifications.Where(n => n.Recoverable))
            if (!entries.TryAdd(notification.DeliveryId, (notification.RecipientUuid.ToUpperInvariant(), SHA256.HashData(Serialize(notification.ToState())))))
                throw new InvalidDataException("Duplicate pending delivery identity.");
        return entries;
    }

    private static void UpdatePendingIndex(JournalTransaction tx,
        Dictionary<string, (string Recipient, byte[] Hash)> before, Dictionary<string, (string Recipient, byte[] Hash)> after)
    {
        foreach (var entry in before)
            if (!after.TryGetValue(entry.Key, out var next) || next.Recipient != entry.Value.Recipient || !next.Hash.SequenceEqual(entry.Value.Hash))
                tx.RemovePending(entry.Key, entry.Value.Recipient, entry.Value.Hash);
        foreach (var entry in after)
            if (!before.TryGetValue(entry.Key, out var previous) || previous.Recipient != entry.Value.Recipient || !previous.Hash.SequenceEqual(entry.Value.Hash))
                tx.Enqueue(entry.Key, entry.Value.Recipient, entry.Value.Hash);
    }

    private void InitializePendingIndex()
    {
        var expected = PendingEvidence(this);
        byte[] version = journal.ReadState(PendingIndexKey);
        if (version == null)
        {
            // Bounded, restartable bootstrap before activation. Each chunk has an
            // immutable intent/result; a crash never makes an incomplete index live.
            var ordered = expected.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray();
            for (int offset = 0; offset < ordered.Length; offset += 64)
            {
                var chunk = ordered.Skip(offset).Take(64).ToArray();
                byte[] intent = Serialize(chunk.Select(e => new PendingCompletionNotificationState
                { DeliveryId = e.Key, RecipientUuid = e.Value.Recipient, Items = new(), AttemptId = Convert.ToHexString(e.Value.Hash) }).ToList());
                journal.Execute("system.migration", "pending-index-v1-" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture), intent, tx =>
                {
                    foreach (var entry in chunk) tx.Enqueue(entry.Key, entry.Value.Recipient, entry.Value.Hash);
                    return new byte[] { 1 };
                });
            }
        }
        else if (!version.SequenceEqual(new byte[] { 1 })) throw new InvalidDataException("Unsupported pending index version.");

        if (journal.PendingCount() != expected.Count) throw new InvalidDataException("Pending index count differs from authority.");
        long validated = 0;
        foreach (string recipient in expected.Values.Select(e => e.Recipient).Distinct(StringComparer.Ordinal))
        {
            string cursor = null;
            while (true)
            {
                var page = journal.ReadPendingPage(recipient, cursor, 64);
                foreach (var entry in page) { ValidatePendingEntry(entry); validated++; }
                if (page.Length < 64) break;
                cursor = page[^1].DeliveryId;
            }
        }
        if (validated != expected.Count) throw new InvalidDataException("Pending index recipient differs from authority.");
        if (version == null)
            journal.Execute("system.migration", "pending-index-v1-ready", new byte[] { 1 }, tx =>
            { tx.WriteState(PendingIndexKey, new byte[] { 1 }); return new byte[] { 1 }; });
    }

    private PendingCompletionNotification ValidatePendingEntry(PendingJournalEntry entry)
    {
        if (!pendingByDelivery.TryGetValue(entry.DeliveryId, out var notification) ||
            notification.RecipientUuid.ToUpperInvariant() != entry.Recipient ||
            !CryptographicOperations.FixedTimeEquals(entry.Payload, SHA256.HashData(Serialize(notification.ToState()))))
            throw new InvalidDataException("Pending index evidence differs from authority.");
        return notification;
    }
    // Soft admission ceiling below the existing 4 MiB ledger limit. Retained evidence is
    // counted, never evicted; terminalization and delivery attempts do not use this gate.
    private const int MaxAdmissionBytes = 3 * 1024 * 1024;
    private bool FitsAdmissionReservation(MutableTrade proposed, TradeNegotiationTerms? newTerms = null)
    {
        var active = activeTrades.Values.Where(t => t.TradeId != proposed.TradeId)
            .Append(proposed).Select(t => t.ToSnapshot()).ToList();
        var pending = pendingCompletionNotifications.Select(n => n.ToState()).ToList();
        if (!FitsPrincipalReservation(proposed.ParticipantUuids, active.Concat(terminalTrades), pending)) return false;
        var terms = new Dictionary<string, TradeNegotiationTerms>(negotiationTerms, StringComparer.OrdinalIgnoreCase);
        if (newTerms == TradeNegotiationTerms.BasicItemsCurrentQuote) terms[proposed.TradeId] = newTerms.Value;
        var state = new FrameworkTradeStoreState
        {
            ActiveTrades = active,
            TerminalTrades = terminalTrades.ToList(),
            PendingCompletionNotifications = pending,
            ConsentSchemaVersion = 1,
            NegotiationTerms = terms.Count == 0 ? null : terms,
            OfferVersions = activeTrades.Values.Where(t => t.TradeId != proposed.TradeId)
                .Append(proposed).ToDictionary(t => t.TradeId, t => t.OfferVersion, StringComparer.OrdinalIgnoreCase)
        };
        if (Serialize(state).Length > MaxAdmissionBytes) return false;

        // Reserve a terminal snapshot plus both recipients' full opaque payloads for
        // EVERY active trade, not just the operation being admitted. Completing or
        // cancelling transfers the same total payload bytes to the two recipients.
        foreach (var snapshot in active)
        {
            if (snapshot.SnapshotVersion > long.MaxValue - 2) return false;
            snapshot.SnapshotVersion += 2;
            foreach (var participant in snapshot.Participants) participant.Accepted = true;
            state.TerminalTrades.Add(snapshot);
            pending.AddRange(PendingCompletionNotification.CreateMany(snapshot, false).Select(n => n.ToState()));
        }
        state.ActiveTrades = new(); state.OfferVersions = new();
        foreach (var notification in pending)
        {
            notification.DeliveryState = "OutcomeUnknown";
            notification.AttemptId = new string('0', 32);
            // false is longer on disk than true; weak ACK cannot free this reserve.
            notification.AckObserved = false;
        }
        return Serialize(state).Length <= MaxAdmissionBytes;
    }

    // Development admission policy. Count source payload evidence once per trade,
    // not once per serialized copy; the separate global serialized limit still applies.
    private bool FitsPrincipalReservation(IEnumerable<string> principals,
        IEnumerable<FrameworkTradeStateSnapshot> snapshots, IEnumerable<PendingCompletionNotificationState> notifications)
    {
        var trades = snapshots.ToArray(); var pending = notifications.ToArray();
        foreach (string principal in principals)
        {
            var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var charges = new Dictionary<string, (long Body, long Auxiliary)>(StringComparer.OrdinalIgnoreCase);
            void Charge(string trade, IEnumerable<FrameworkItemPayload> items)
            {
                long body = 0, auxiliary = 0;
                foreach (var item in items ?? Enumerable.Empty<FrameworkItemPayload>())
                {
                    body += (item.PayloadBytes?.LongLength ?? 0) + (long)Encoding.UTF8.GetByteCount(item.PayloadJson ?? "");
                    auxiliary += Encoding.UTF8.GetByteCount(item.CodecId ?? "");
                    foreach (var entry in item.Metadata ?? Enumerable.Empty<FrameworkMetadataEntry>())
                        auxiliary += (long)Encoding.UTF8.GetByteCount(entry.Key ?? "") + Encoding.UTF8.GetByteCount(entry.Value ?? "");
                    if ((admissionPolicy.PayloadBytes != 0 && body > admissionPolicy.PayloadBytes) || (admissionPolicy.AuxiliaryBytes != 0 && auxiliary > admissionPolicy.AuxiliaryBytes)) break;
                }
                charges.TryGetValue(trade, out var previous);
                // A terminal quote and its delivery are evidence of the same source
                // payload. Take component-wise maxima to cover orphan/imported rows.
                charges[trade] = (Math.Max(previous.Body, body), Math.Max(previous.Auxiliary, auxiliary));
            }
            foreach (var trade in trades)
            {
                var owner = trade.Participants.SingleOrDefault(p => string.Equals(p.Uuid, principal, StringComparison.OrdinalIgnoreCase));
                if (owner == null) continue;
                related.Add(trade.TradeId); Charge(trade.TradeId, owner.ItemsOnOffer);
            }
            foreach (var notification in pending)
            {
                if (string.Equals(notification.RecipientUuid, principal, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(notification.OtherPartyUuid, principal, StringComparison.OrdinalIgnoreCase)) related.Add(notification.TradeId);
                string source = notification.Cancelled ? notification.RecipientUuid : notification.OtherPartyUuid;
                if (string.Equals(source, principal, StringComparison.OrdinalIgnoreCase)) Charge(notification.TradeId, notification.Items);
            }
            if ((admissionPolicy.RetainedTrades != 0 && related.Count > admissionPolicy.RetainedTrades) ||
                (admissionPolicy.PayloadBytes != 0 && charges.Values.Sum(c => c.Body) > admissionPolicy.PayloadBytes) ||
                (admissionPolicy.AuxiliaryBytes != 0 && charges.Values.Sum(c => c.Auxiliary) > admissionPolicy.AuxiliaryBytes)) return false;
        }
        return true;
    }

    public bool TryBeginDelivery(string trade,string recipient,bool cancelled,string receiptContract=null,string epoch=null)
    {
        var notification=GetCompletionNotificationFor(trade,recipient);
        if(notification==null || notification.Cancelled!=cancelled || !notification.Recoverable)return false;
        bool retry=notification.DeliveryState!="Pending";
        if(retry && (receiptContract!=notification.ReceiptContract || string.IsNullOrEmpty(epoch) || epoch==notification.LastDispatchEpoch))return false;
        string operation=receiptContract==TradeDeliveryProtocol.Contract?"dispatch-"+notification.DeliveryId+"-"+epoch:"attempt-"+notification.DeliveryId;
        var result=Execute(recipient,operation,Encoding.UTF8.GetBytes(receiptContract==null?notification.DeliveryId:notification.DeliveryId+"\n"+receiptContract),candidate=>
        {
            var pending=candidate.pendingCompletionNotifications.Single(n=>n.DeliveryId==notification.DeliveryId);
            bool allowed=pending.Recoverable && (pending.DeliveryState=="Pending" || pending.LastDispatchEpoch!=epoch);
            if(allowed){pending.DeliveryState="OutcomeUnknown";pending.AttemptId=Guid.NewGuid().ToString("N");pending.LastDispatchEpoch=receiptContract==null?null:epoch;
                if(!retry)pending.ReceiptContract=receiptContract;}
            return new TradeOperationResult {Success=allowed};
        },recovery:true);
        return result.Success&&!result.Replayed;
    }
}

internal sealed partial class PhinixFrameworkTradeStore
{
    public TradeOperationResult QueryOperation(string principal, string operation, byte[] fingerprint)
    {
        lock (syncLock)
        {
            EnsurePersistence();
            if (journal == null) throw new IOException("Durable trade journal required.");
            // A missing/corrupt database fails; it must never become a negative lookup.
            var committed = journal.TryReadCommittedFingerprint(principal, operation, fingerprint);
            return committed == null ? null : Deserialize<TradeOperationResult>(committed.Result);
        }
    }
}
