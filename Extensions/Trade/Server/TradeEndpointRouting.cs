using System;
using ServerRuntime;
using Utils.Framework;

namespace Phinix.TradeExtension.Server;

// A plugin supplies an authenticated alternate route, not invented Framework capabilities.
public sealed record TradeEndpointSession(string ConnectionId, string SessionId, string PrincipalId, bool Confirmed = true)
{
    public override string ToString() => "TradeEndpointSession(redacted)";
}
public interface ITradeEndpointRouter
{
    bool TryGetSession(string connectionId, out TradeEndpointSession session);
    void Send(string connectionId, FrameworkPacket packet);
}
public interface ITradeEndpointRegistration
{
    IDisposable RegisterEndpoints(ITradeEndpointRouter router, ITradeBasicItemProjection projection);
}
public static class TradeTermsProtocol
{
    public const string BasicTradesKey = "trade_basic_terms_v1";
}

public sealed partial class PhinixFrameworkTradeServerService : ITradeEndpointRegistration
{
    private sealed record EndpointBinding(ITradeEndpointRouter Router, ITradeBasicItemProjection Projection);
    private EndpointBinding endpointBinding;
    private sealed class EndpointLease : IDisposable
    {
        private Action release;
        public EndpointLease(Action release) => this.release = release;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref release, null)?.Invoke();
    }
    public IDisposable RegisterEndpoints(ITradeEndpointRouter router, ITradeBasicItemProjection projection)
    {
        var binding = new EndpointBinding(router ?? throw new ArgumentNullException(nameof(router)), projection ?? throw new ArgumentNullException(nameof(projection)));
        if (System.Threading.Interlocked.CompareExchange(ref endpointBinding, binding, null) != null)
            throw new InvalidOperationException("Trade endpoint provider already registered.");
        return new EndpointLease(() => System.Threading.Interlocked.CompareExchange(ref endpointBinding, null, binding));
    }
    private bool TryEndpoint(string connection, out TradeEndpointSession session)
    {
        session = null;
        // A selected Framework profile always wins; an adapter cannot override it.
        if (readiness != null && readiness.TryGetReady(connection, out _)) return false;
        var binding = System.Threading.Volatile.Read(ref endpointBinding);
        return binding != null && binding.Router.TryGetSession(connection, out session) &&
            session != null && session.ConnectionId == connection;
    }
    private void SendPacket(string connection, FrameworkPacket packet, Action<string, FrameworkPacket> normal)
    {
        var binding = System.Threading.Volatile.Read(ref endpointBinding);
        if (binding != null && TryEndpoint(connection, out _)) binding.Router.Send(connection, packet);
        else normal?.Invoke(connection, packet);
    }
    private void MarkTerms(FrameworkPacket packet, System.Collections.Generic.IEnumerable<FrameworkTradeStateSnapshot> snapshots)
    {
        var ids = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(
            System.Linq.Enumerable.Where(snapshots, s => store.GetTerms(s.TradeId) == TradeNegotiationTerms.BasicItemsCurrentQuote), s => s.TradeId));
        if (ids.Length != 0) packet.SetMetadataValue(TradeTermsProtocol.BasicTradesKey, string.Join(",", ids));
    }
    private void RecoverEndpoint(TradeEndpointSession expected, Action<string, FrameworkPacket> send, string after, int generation)
    {
        bool Current() => generation == System.Threading.Volatile.Read(ref recoveryGeneration) &&
            expected.Confirmed && TryEndpoint(expected.ConnectionId, out var current) && current == expected;
        if (!Current()) return;
        PhinixFrameworkTradeStore.PendingCompletionNotification[] page;
        try { page = store.GetPendingCompletionPageFor(expected.PrincipalId, after, recoveryPageSize); }
        catch (ServerStorage.JournalBusyException)
        {
            QueueRecoveryContinuation(expected.ConnectionId, Current, () => RecoverEndpoint(expected, send, after, generation), 1000);
            return;
        }
        Exception failure = null;
        foreach (var notification in page)
        {
            if (!Current()) return;
            try { SendCompletionEvent(expected.ConnectionId, expected.SessionId, notification, send); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure != null)
        {
            QueueRecoveryContinuation(expected.ConnectionId, Current, () => RecoverEndpoint(expected, send, after, generation), 5000);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (page.Length == recoveryPageSize && Current())
        {
            string cursor = page[page.Length - 1].DeliveryId;
            QueueRecoveryContinuation(expected.ConnectionId, Current, () => RecoverEndpoint(expected, send, cursor, generation));
        }
    }
}
