using System;
using System.Net;
using Utils;

namespace Connections
{
    // Optional notification from a transport worker. The host owns process shutdown.
    public interface IServerTransportFaultSource
    {
        event Action<string> WorkerFailed;
    }

    // Host transport boundary. Submitted is local ownership only, never a business or delivery ACK.
    public interface IServerTransport : ILoggable, IDisposable
    {
        IPEndPoint Endpoint { get; }
        int LocalPort { get; }
        bool Listening { get; }
        event EventHandler PollCompleted;
        event EventHandler<ConnectionEventArgs> OnConnectionEstablished;
        event EventHandler<ConnectionEventArgs> OnConnectionClosed;
        void RegisterPacketHandler(string module, NetCommon.PacketHandlerDelegate handler);
        void UnregisterPacketHandler(string module);
        void UnregisterAllPacketHandlers();
        void Start();
        void Stop();
        void Send(string connectionId, string module, byte[] payload);
        bool TrySend(string connectionId, string module, byte[] payload);
        TransportSendOutcome TrySendWithOutcome(string connectionId, string module, byte[] payload);
        void DisconnectPeer(string connectionId);
    }
}
