using System;
using System.Collections.Generic;
using System.Threading;

namespace ServerRuntime;

// Every plugin has the same bounded continuation service; no business-specific jobs.
public interface IServerWorkQueue
{
    bool TryEnqueue(Action action);
}

public sealed class ServerWorkQueue : IServerWorkQueue, IDisposable
{
    private readonly Queue<Action> work = new();
    private readonly object gate = new();
    private readonly int capacity, perPoll;
    private readonly Action<string> reportFailure;
    private bool stopped;
    private int draining;

    public ServerWorkQueue(int capacity = 1024, int perPoll = 4, Action<string> reportFailure = null)
    {
        if (capacity < 1 || perPoll < 1) throw new ArgumentOutOfRangeException();
        this.capacity = capacity; this.perPoll = perPoll; this.reportFailure = reportFailure;
    }

    public bool TryEnqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            if (stopped || work.Count >= capacity) return false;
            work.Enqueue(action); return true;
        }
    }

    // Called by the host on the poll worker. New continuations wait until the next poll.
    // This bounds callback count, not the execution duration of an arbitrary plugin.
    public void Drain()
    {
        if (Interlocked.CompareExchange(ref draining, 1, 0) != 0) return;
        try
        {
            int count;
            lock (gate) count = stopped ? 0 : Math.Min(perPoll, work.Count);
            for (int i = 0; i < count; i++)
            {
                Action action;
                lock (gate)
                {
                    if (stopped || work.Count == 0) break;
                    action = work.Dequeue();
                }
                try { action(); }
                catch (Exception error)
                {
                    // Exception messages may contain user payloads or credentials.
                    try { reportFailure?.Invoke(error.GetType().Name); } catch { /* Isolate a failed log sink. */ }
                }
            }
        }
        finally { Volatile.Write(ref draining, 0); }
    }

    // Host quiesces the poll worker before disposing its scope. Cancellation never
    // asserts that any corresponding application operation completed or rolled back.
    public void Dispose() { lock (gate) { stopped = true; work.Clear(); } }
}
