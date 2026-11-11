using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Phinix.TradeExtension.Server;
using Phinix.TradeExtension;
using Microsoft.Data.Sqlite;
using ServerStorage;
using ServerRuntime;
using Utils.Framework;

public class TradeNotificationProbe : DispatchProxy
{
    public int Recoveries;
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "HandleSnapshotRequest") throw new IOException("synthetic snapshot send failure");
        if (method.Name == "HandleUserLoggedIn") Recoveries++;
        return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
    }
}

public class RecoveryTransportProbe : DispatchProxy
{
    private EventHandler poll;
    public int PollSubscribers => poll?.GetInvocationList().Length ?? 0;
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name == "add_PollCompleted") poll += (EventHandler)args[0];
        if (method.Name == "remove_PollCompleted") poll -= (EventHandler)args[0];
        return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
    }
}

internal static partial class Program
{
    private static void PulseRecovery(PhinixFrameworkTradeServerService service) =>
        typeof(PhinixFrameworkTradeServerService).GetMethod("RetryPendingRecovery", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, null);

    private static void SnapshotFailureStillRecovers()
    {
        var api = DispatchProxy.Create<IFrameworkTradeServerApi, TradeNotificationProbe>();
        var module = new BuiltInTradeServerExtension();
        typeof(BuiltInTradeServerExtension).GetField("tradeApi", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(module, api);
        var registry = new ServerSessionRegistry((_, _, _) => true);
        var host = new ExtensionHostContext(); host.AddService<IServerSessionReadiness>(registry);
        var transport = DispatchProxy.Create<Connections.IServerTransport, RecoveryTransportProbe>();
        host.AddService<Connections.IServerTransport>(transport);
        module.Activate(host);
        Check(((RecoveryTransportProbe)(object)transport).PollSubscribers == 1, "Recovery maintenance was not subscribed through the normal transport port");
        Check(registry.TryBind("snapshot-test", "player", "session", 2, Array.Empty<string>(), Array.Empty<string>(), out var ready, out _), "Snapshot failure readiness fixture");
        registry.MarkReady(ready);
        Check(((TradeNotificationProbe)(object)api).Recoveries == 1, "Failed snapshot prevented independent durable recovery");
        module.Shutdown(host);
        Check(((RecoveryTransportProbe)(object)transport).PollSubscribers == 0, "Stopped trade module retained a recovery poll subscription");
        registry.TryBind("after-stop", "player", "session-2", 2, Array.Empty<string>(), Array.Empty<string>(), out ready, out _);
        registry.MarkReady(ready);
        Check(((TradeNotificationProbe)(object)api).Recoveries == 1, "Stopped trade module still handled ready callbacks");
        module.Activate(host);
        Check(((RecoveryTransportProbe)(object)transport).PollSubscribers == 1, "Reactivation leaked or duplicated the recovery subscription");
        module.Shutdown(host);
    }

    private static void NotificationReliability(string root)
    {
        var f = new Fixture(root); f.Ready("b");
        int logs = 0;
        f.Service.OnLogEntry += (_, _) => throw new IOException("synthetic logger failure");
        f.Service.OnLogEntry += (_, _) => logs++;
        f.Service.RaiseLogEntry(new Utils.LogEventArgs("synthetic diagnostic"));
        Check(logs == 1, "Throwing log observer prevented other diagnostics");
        f.BeforeSend = (connection, packet) =>
        {
            if (connection == "connection-a" && packet.MessageType == FrameworkTradeProtocol.CreateResponseType)
                throw new IOException("synthetic requester unavailable");
        };
        Throws<IOException>(() => f.Create("notification-create"));
        Check(f.SentTargets.Any(p => p.Connection == "connection-b" && p.Packet.MessageType == FrameworkTradeProtocol.CreateResponseType), "Requester response failure suppressed peer creation notification");
        string trade = f.Snapshot().Trades.Single().TradeId;
        f.BeforeSend = (_, packet) => { if (packet.MessageType == FrameworkTradeProtocol.OfferUpdateResponseType) throw new IOException("synthetic quote response failure"); };
        f.SentTargets.Clear(); f.Cache("notification-item", 7);
        Throws<IOException>(() => f.Offer(trade, new[] { "notification-item" }, "notification-offer"));
        Check(f.SentTargets.Count(p => p.Packet.MessageType == FrameworkTradeProtocol.SnapshotType) == 2, "Quote reply failure suppressed participant snapshots");
        f.SentTargets.Clear();
        f.BeforeSend = (connection, _) => { if (connection == "connection-a") throw new IOException("synthetic first participant offline"); };
        Throws<IOException>(() => f.Cancel(trade, "notification-cancel"));
        Check(f.SentTargets.Any(p => p.Connection == "connection-b" && p.Packet.MessageType == FrameworkTradeProtocol.CancelledEventType), "Failed status reply/first completion prevented the other participant's delivery");
        f.BeforeSend = null;
        Check(f.Snapshot().Trades.Count == 0, "Notification failure rolled back an already committed cancellation");
        f.Sent.Clear(); f.Cancel(trade, "notification-cancel");
        Check(f.Sent.All(p => p.MessageType != FrameworkTradeProtocol.CancelledEventType), "Replayed status repeated an uncertain delivery");
        f.WorkQueue.Dispose();
    }

    private static void TransientReadContention(string root)
    {
        var f = new Fixture(root);
        string trade = f.Create("read-lock-create");
        using (var locked = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Database, Pooling = false }.ToString()))
        {
            locked.Open();
            using var command = locked.CreateCommand(); command.CommandText = "BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
            Throws<JournalBusyException>(() => f.Snapshot());
            command.CommandText = "ROLLBACK"; command.ExecuteNonQuery();
        }
        Check(f.Snapshot().Trades.Single().TradeId == trade, "Transient SQLite read contention permanently poisoned trade availability");
        // Model a post-commit refresh interrupted by contention: the durable authority
        // is unchanged, while its in-memory projection needs a refresh before any use.
        object store = typeof(PhinixFrameworkTradeServerService).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.Service);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        ((System.Collections.IDictionary)store.GetType().GetField("activeTrades", flags).GetValue(store)).Clear();
        store.GetType().GetField("refreshRequired", flags).SetValue(store, true);
        Check(f.Snapshot().Trades.Single().TradeId == trade, "Missed post-commit refresh exposed stale in-memory state");
        f.Cancel(trade, "after-read-lock-cancel");
        Check(f.StatusResponse.Success, "Ordinary operation did not resume after contention");
        f.Ready("b"); f.Sent.Clear();
        long now = 0;
        typeof(PhinixFrameworkTradeServerService).GetField("recoveryClock", flags).SetValue(f.Service, (Func<long>)(() => now));
        using (var locked = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Database, Pooling = false }.ToString()))
        {
            locked.Open(); using var command = locked.CreateCommand(); command.CommandText = "BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
            f.Service.HandleUserLoggedIn("connection-b", "session-b", "b", f.Send);
            Check(f.Sent.Count == 0, "Busy recovery fabricated an empty completion or ran inline");
            command.CommandText = "ROLLBACK"; command.ExecuteNonQuery();
        }
        now = 1000; PulseRecovery(f.Service); f.WorkQueue.Drain();
        Check(f.Sent.Count(p => p.MessageType == FrameworkTradeProtocol.CancelledEventType) == 1, "Recovery after transient read lock required another login");
        f.WorkQueue.Dispose();
    }

    private static void DeferredRecoveryFairness(string root)
    {
        var f = new Fixture(root); long now = 0; int delayed = 0, immediate = 0;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(PhinixFrameworkTradeServerService).GetField("recoveryClock", flags).SetValue(f.Service, (Func<long>)(() => now));
        var queue = typeof(PhinixFrameworkTradeServerService).GetMethod("QueueRecoveryContinuation", flags);
        for (int i = 0; i < 64; i++) Check(f.WorkQueue.TryEnqueue(() => { }), "Deferred fairness saturation fixture");
        for (int i = 0; i < 4; i++) queue.Invoke(f.Service, new object[] { "delayed-" + i, (Func<bool>)(() => true), (Action)(() => delayed++), 5000 });
        queue.Invoke(f.Service, new object[] { "immediate", (Func<bool>)(() => true), (Action)(() => immediate++), 0 });
        f.WorkQueue.Drain(); PulseRecovery(f.Service);
        for (int i = 0; i < 65; i++) f.WorkQueue.Drain();
        Check(immediate == 1 && delayed == 0, "Backoff for other connections blocked an eligible user's recovery");
        now = 5000; PulseRecovery(f.Service);
        for (int i = 0; i < 4; i++) f.WorkQueue.Drain();
        Check(delayed == 4, "Delayed recovery did not resume after backoff");
        queue.Invoke(f.Service, new object[] { "stopped", (Func<bool>)(() => true), (Action)(() => immediate++), 0 });
        f.Service.CancelPendingRecovery(); f.WorkQueue.Drain(); PulseRecovery(f.Service);
        Check(immediate == 1, "Stopped recovery retained an executable callback");
        f.WorkQueue.Dispose();
    }
}
