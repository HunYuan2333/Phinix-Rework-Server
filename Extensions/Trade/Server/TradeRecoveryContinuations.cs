using System;
using System.Collections.Generic;
using System.Linq;
using Utils;

namespace Phinix.TradeExtension.Server;

public sealed partial class PhinixFrameworkTradeServerService
{
    private sealed class RecoveryContinuation
    {
        internal string Connection;
        internal Func<bool> Current;
        internal Action Work;
        internal bool Queued;
        internal long Order;
        internal long DueAt;
    }
    private readonly object recoveryGate = new();
    private readonly Dictionary<string, RecoveryContinuation> recoveryContinuations = new(StringComparer.Ordinal);
    private long recoveryOrder, nextRecoveryWarning;
    private Func<long> recoveryClock = () => Environment.TickCount64;

    // One continuation per connection; only unattempted deliveries (or the existing
    // negotiated receipt contract) may progress. This never resets delivery evidence.
    private void QueueRecoveryContinuation(string connection, Func<bool> current, Action work, int delayMilliseconds = 0)
    {
        var pending = new RecoveryContinuation { Connection = connection, Current = current, Work = work, DueAt = recoveryClock() + delayMilliseconds };
        lock (recoveryGate)
        {
            if (!current()) return;
            pending.Order = ++recoveryOrder;
            recoveryContinuations[connection] = pending;
        }
        TryQueueRecovery(pending);
    }

    private void TryQueueRecovery(RecoveryContinuation pending)
    {
        lock (recoveryGate)
        {
            if (!recoveryContinuations.TryGetValue(pending.Connection, out var latest) || latest != pending || pending.Queued) return;
            bool current;
            try { current = pending.Current(); } catch { current = false; }
            if (!current) { recoveryContinuations.Remove(pending.Connection); return; }
            if (recoveryClock() < pending.DueAt) return;
            pending.Order = ++recoveryOrder;
            pending.Queued = true;
            bool accepted = false;
            try { accepted = workQueue != null && workQueue.TryEnqueue(() =>
            {
                lock (recoveryGate)
                {
                    if (!recoveryContinuations.TryGetValue(pending.Connection, out var owned) || owned != pending) return;
                    recoveryContinuations.Remove(pending.Connection);
                }
                if (pending.Current()) pending.Work();
            }); } catch { /* A faulty queue port retains the continuation for the next poll. */ }
            if (accepted) return;
            pending.Queued = false;
        }
        long now = recoveryClock();
        if (now >= nextRecoveryWarning)
        {
            nextRecoveryWarning = now + 30000;
            RaiseLogEntry(new LogEventArgs("Trade recovery continuation unavailable; unattempted deliveries remain Pending and will resume when the work queue is available.", LogLevel.WARNING));
        }
    }

    // Called by the plugin's ordinary poll subscription, never a timer/thread with game/business work.
    internal void RetryPendingRecovery()
    {
        RecoveryContinuation[] pending;
        lock (recoveryGate) pending = recoveryContinuations.Values.Where(p => !p.Queued && recoveryClock() >= p.DueAt).OrderBy(p => p.Order).Take(4).ToArray();
        foreach (var item in pending) TryQueueRecovery(item);
    }
}
