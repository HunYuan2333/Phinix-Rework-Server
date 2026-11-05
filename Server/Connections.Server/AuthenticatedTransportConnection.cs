using System;
namespace Connections
{
    // Minted by an authenticated transport, never constructed from a client flag or connection-ID prefix.
    public sealed class AuthenticatedTransportConnection
    {
        private readonly Func<bool> live;
        public string ConnectionId { get; }
        public string Epoch { get; } = Guid.NewGuid().ToString("N");
        internal AuthenticatedTransportConnection(string connectionId, Func<bool> live)
        { ConnectionId = connectionId; this.live = live; }
        public bool IsLive { get { try { return live(); } catch { return false; } } }
    }
    public interface IAuthenticatedServerTransport
    {
        bool TryGetAuthenticatedConnection(string connectionId, out AuthenticatedTransportConnection connection);
    }
}
