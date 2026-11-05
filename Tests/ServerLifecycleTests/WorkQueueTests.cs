using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Connections;
using ServerRuntime;

internal static class WorkQueueTests
{
    private static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
    public static void Run()
    {
        var calls = new List<int>(); var faults = new List<string>();
        using var queue = new ServerWorkQueue(2, 1, faults.Add);
        Check(queue.TryEnqueue(() => { calls.Add(1); queue.TryEnqueue(() => calls.Add(3)); queue.Drain(); }) &&
            queue.TryEnqueue(() => calls.Add(2)), "Queue initial admission");
        Check(!queue.TryEnqueue(() => calls.Add(99)), "Full queue did not refuse admission");
        queue.Drain(); Check(string.Join(",", calls) == "1", "Reentrant work exceeded the poll budget");
        queue.Drain(); Check(string.Join(",", calls) == "1,2", "Queue lost FIFO fairness");
        queue.Drain(); Check(string.Join(",", calls) == "1,2,3", "Continuation never progressed");
        queue.TryEnqueue(() => throw new InvalidOperationException("synthetic-secret-must-not-log"));
        queue.Drain(); Check(faults.Count == 1 && faults[0] == "InvalidOperationException", "Queue failure leaked exception message");
        queue.TryEnqueue(() => calls.Add(4)); queue.Dispose(); queue.Drain();
        Check(!queue.TryEnqueue(() => calls.Add(5)) && calls.Count == 3, "Disposed queue executed retained jobs");

        // More than one allowed callback must still defer newly enqueued work.
        using var multi = new ServerWorkQueue(4, 4);
        multi.TryEnqueue(() => { calls.Add(6); multi.TryEnqueue(() => calls.Add(7)); });
        multi.Drain(); Check(calls[^1] == 6, "Fresh continuation ran in the same poll");
        multi.Drain(); Check(calls[^1] == 7, "Next poll did not run continuation");

        // Real transport worker, no client simulation or ThreadPool fallback.
        using var server = new NetServer(new IPEndPoint(IPAddress.Any, 0), 1);
        using var transportQueue = new ServerWorkQueue();
        using var ran = new ManualResetEventSlim(); int executingThread = 0;
        transportQueue.TryEnqueue(() => { executingThread = Environment.CurrentManagedThreadId; ran.Set(); });
        server.PollCompleted += (_, _) => transportQueue.Drain();
        server.Start();
        try { Check(ran.Wait(5000) && executingThread != Environment.CurrentManagedThreadId, "Transport did not drain work on its poll worker"); }
        finally { server.Stop(); }
        Console.WriteLine("WORK QUEUE RESULT passed; bounded/fifo/reentrancy/cancellation/fault-isolation/real-poll-worker");
    }
}
