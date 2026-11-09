using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using Utils;

namespace Connections
{
    public class NetServer : NetCommon, IServerTransport, IServerTransportFaultSource, IRelayTransport
    {
        /// <inheritdoc />
        public override event EventHandler<LogEventArgs> OnLogEntry;
        /// <inheritdoc />
        public override void RaiseLogEntry(LogEventArgs e)
        {
            var listeners = OnLogEntry;
            if (listeners == null) return;
            foreach (EventHandler<LogEventArgs> listener in listeners.GetInvocationList())
                try { listener(this, e); } catch (Exception) { }
        }

        private void PublishPollCompleted()
        {
            var listeners = PollCompleted;
            if (listeners == null) return;
            foreach (EventHandler listener in listeners.GetInvocationList())
            {
                try { listener(this, EventArgs.Empty); }
                catch (Exception error) { RaiseLogEntry(new LogEventArgs("UDP poll observer failed: " + error.GetType().Name, LogLevel.ERROR)); }
            }
        }

        private bool PublishConnectionEvent(EventHandler<ConnectionEventArgs> listeners, ConnectionEventArgs args)
        {
            bool complete = true;
            if (listeners == null) return complete;
            foreach (EventHandler<ConnectionEventArgs> listener in listeners.GetInvocationList())
            {
                try { listener(this, args); }
                catch (Exception error)
                {
                    complete = false;
                    RaiseLogEntry(new LogEventArgs("UDP connection observer failed: " + error.GetType().Name, LogLevel.ERROR));
                }
            }
            return complete;
        }
        
        /// <summary>
        /// Whether the server is currently listening for connections.
        /// </summary>
        public bool Listening => server != null && server.IsRunning;
        public int LocalPort => server?.LocalPort ?? 0;
        // Neutral host continuation point, on the same worker as packet dispatch.
        public event EventHandler PollCompleted;
        public event Action<string> WorkerFailed;
        private int workerFailed;

        private void ReportWorkerFailure(Exception error)
        {
            if (Interlocked.Exchange(ref workerFailed, 1) != 0) return;
            RaiseLogEntry(new LogEventArgs("UDP worker failed: " + error.GetType().Name, LogLevel.ERROR));
            var listeners = WorkerFailed;
            if (listeners == null) return;
            foreach (Action<string> listener in listeners.GetInvocationList())
                try { listener(error.GetType().Name); } catch (Exception) { }
        }

        /// <summary>
        /// <see cref="IPEndPoint"/> the server is listening on.
        /// </summary>
        public IPEndPoint Endpoint { get; }
        
        /// <summary>
        /// Raised when a new connection is established.
        /// </summary>
        public event EventHandler<ConnectionEventArgs> OnConnectionEstablished;
        /// <summary>
        /// Raised when an existing connection is closed.
        /// </summary>
        public event EventHandler<ConnectionEventArgs> OnConnectionClosed;

        /// <summary>
        /// Server that piggy-backs the listener and communicates with clients.
        /// </summary>
        private NetManager server;
        private readonly PinnedUdpPeerSynchronization peerSynchronization;
        /// <summary>
        /// Collection of currently-connected peers organised by their connection ID.
        /// </summary>
        private Dictionary<string, NetPeer> connectedPeers;
        /// <summary>
        /// Lock object to prevent race conditions when accessing <see cref="connectedPeers"/>.
        /// </summary>
        private readonly object connectedPeersLock = new object();

        /// <summary>
        /// Thread that polls the server backend for incoming packets.
        /// </summary>
        private Thread pollThread;
        /// <summary>
        /// Signals that the polling thread should exit.
        /// </summary>
        private volatile bool pollThreadStopRequested;

        public NetServer(IPEndPoint endpoint, int maxConnections)
        {
            this.Endpoint = endpoint;
            
            this.server = new NetManager(listener, maxConnections, "Phinix")
            {
                PingInterval = 5000,
                DisconnectTimeout = 30000
            };
            peerSynchronization = PinnedUdpPeerSynchronization.InstallServer(server, ReportWorkerFailure);
            this.connectedPeers = new Dictionary<string, NetPeer>();
            
            // Subscribe to events
            listener.PeerConnectedEvent += (peer) =>
            {
                // Add or update the peer's connection
                string connectionId = peer.ConnectId.ToString("X");
                lock (connectedPeersLock)
                {
                    connectedPeers[connectionId] = peer;
                }

                // Raise the connection established event for this peer
                // A partially executed authentication/startup observer cannot leave an
                // admitted connection behind. Other subscribers still receive cleanup.
                if (!PublishConnectionEvent(OnConnectionEstablished, new ConnectionEventArgs(connectionId)))
                    DisconnectPeer(connectionId);
            };
            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                // Remove the peer's connection
                string connectionId = peer.ConnectId.ToString("X");
                lock (connectedPeersLock)
                {
                    connectedPeers.Remove(connectionId);
                }

                // Raise the connection established event for this peer
                PublishConnectionEvent(OnConnectionClosed, new ConnectionEventArgs(connectionId));
            };
        }

        /// <summary>
        /// Starts listening for connections on the given endpoint.
        /// </summary>
        public void Start()
        {
            // This frozen backend exposes Start(port) only; it binds IPv4 wildcard.
            // Refuse unsupported intent instead of silently listening on all interfaces.
            if (!IPAddress.Any.Equals(Endpoint.Address))
                throw new NotSupportedException("The pinned UDP backend supports IPAddress 0.0.0.0 only; the requested bind address cannot be honored.");
            // Stop listening
            Stop();

            // Start listening
            if (!server.Start(Endpoint.Port))
                throw new IOException("Transport could not start listening.");
            
            // Start a polling thread to check for incoming packets
            pollThreadStopRequested = false;
            pollThread = new Thread(() =>
            {
                try
                {
                    while (!pollThreadStopRequested && Volatile.Read(ref workerFailed) == 0)
                    {
                        server.PollEvents();
                        PublishPollCompleted();
                        Thread.Sleep(10);
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    ReportWorkerFailure(error);
                }
            });
            pollThread.IsBackground = true;
            pollThread.Start();
        }

        /// <summary>
        /// Stops listening for new connections and terminates any active ones.
        /// </summary>
        public void Stop()
        {
            server.Stop();
            lock (connectedPeersLock)
            {
                connectedPeers.Clear();
            }

            pollThreadStopRequested = true;
            Thread activePollThread = pollThread;
            if (activePollThread == Thread.CurrentThread)
                throw new InvalidOperationException("Cannot confirm poll worker termination from that worker.");
            if (activePollThread != null && !activePollThread.Join(1000))
                throw new TimeoutException("Poll worker did not stop within the shutdown deadline.");
            // Keep the reference on failure so a later cleanup can still join the worker.
            pollThread = null;
        }

        /// <summary>
        /// Sends a message to a module through the given connection.
        /// </summary>
        /// <param name="connectionId">Connection ID of recipient</param>
        /// <param name="module">Target module</param>
        /// <param name="serialisedMessage">Serialised message</param>
        /// <exception cref="ArgumentException"><see cref="connectionId"/> cannot be null or empty</exception>
        /// <exception cref="ArgumentException"><see cref="module"/> cannot be null or empty</exception>
        /// <exception cref="ArgumentNullException"><see cref="serialisedMessage"/> cannot be null</exception>
        /// <exception cref="NotConnectedException">Recipient must be connected to send a message</exception>
        private readonly object sendLock = new object();

        private (NetPeer Peer, NetDataWriter Writer) PrepareSend(string connectionId, string module, byte[] serialisedMessage)
        {
            // Disallow null parameters
            if (string.IsNullOrEmpty(connectionId)) throw new ArgumentException("Connection ID cannot be null or empty", nameof(connectionId));
            if (string.IsNullOrEmpty(module)) throw new ArgumentException("Module cannot be null or empty", nameof(module));
            if (serialisedMessage == null) throw new ArgumentNullException(nameof(serialisedMessage), "Serialised message cannot be null");

            NetPeer peer;
            lock (connectedPeersLock)
            {
                // Check the connection exists
                if (!connectedPeers.ContainsKey(connectionId))
                {
                    throw new NotConnectedException();
                }

                // Get the connection by it's ID
                peer = connectedPeers[connectionId];
            }

            // Make sure the connection is open
            if (peer.ConnectionState != ConnectionState.Connected)
            {
                throw new NotConnectedException(peer);
            }

            // Write the module and message data to a NetDataWriter stream
            NetDataWriter writer = new NetDataWriter();
            writer.Put(module);
            writer.Put(serialisedMessage);

            return (peer, writer);
        }

        public void Send(string connectionId, string module, byte[] serialisedMessage)
        {
            var prepared = PrepareSend(connectionId, module, serialisedMessage);
            lock (sendLock)
            lock (peerSynchronization.PeerGate)
            {
                if (prepared.Peer.ConnectionState != ConnectionState.Connected)
                    throw new NotConnectedException(prepared.Peer);
                prepared.Peer.Send(prepared.Writer, SendOptions.ReliableOrdered);
            }
        }

        /// <summary>
        /// Reports local transport evidence, never remote receipt or business commitment.
        /// Preparation failures are NotSent; exceptions/races after handoff remain Unknown.
        /// This method never retries or switches protocols and does not log private payloads.
        /// </summary>
        public TransportSendOutcome TrySendWithOutcome(string connectionId, string module, byte[] serialisedMessage)
        {
            (NetPeer Peer, NetDataWriter Writer) prepared;
            try { prepared = PrepareSend(connectionId, module, serialisedMessage); }
            catch (Exception) { return TransportSendOutcome.NotSent; }

            lock (sendLock)
            lock (peerSynchronization.PeerGate)
            {
                return AttemptTransportSend(
                    () => prepared.Peer.ConnectionState == ConnectionState.Connected,
                    () => prepared.Peer.Send(prepared.Writer, SendOptions.ReliableOrdered));
            }
        }

        public TransportSendOutcome TrySendRelay(string connectionId, string module, byte[] payload)
        {
            if (payload == null || payload.Length > 64 * 1024) return TransportSendOutcome.NotSent;
            (NetPeer Peer, NetDataWriter Writer) prepared;
            try { prepared = PrepareSend(connectionId, module, payload); }
            catch { return TransportSendOutcome.NotSent; }
            lock (sendLock)
            lock (peerSynchronization.PeerGate)
            {
                // Count queued fragments before allocating another reliable payload.
                if (prepared.Peer.PacketsCountInReliableOrderedQueue >= 2048) return TransportSendOutcome.NotSent;
                lock (connectedPeersLock)
                {
                    long queued = connectedPeers.Values.Sum(peer => (long)peer.PacketsCountInReliableOrderedQueue);
                    if (queued >= 8192) return TransportSendOutcome.NotSent;
                }
                return AttemptTransportSend(() => prepared.Peer.ConnectionState == ConnectionState.Connected,
                    () => prepared.Peer.Send(prepared.Writer, SendOptions.ReliableOrdered));
            }
        }

        // A local handoff boundary, not a remotely triggerable fault hook. Keep uncertain
        // exceptions after Send starts distinct from errors known to precede any Send call.
        private static TransportSendOutcome AttemptTransportSend(Func<bool> isConnected, Action handoff)
        {
            bool attempted = false;
            try
            {
                if (!isConnected()) return TransportSendOutcome.NotSent;
                attempted = true;
                handoff();
                // The pinned LiteNetLib Send can silently return for a disconnected peer.
                return isConnected() ? TransportSendOutcome.Submitted : TransportSendOutcome.Unknown;
            }
            catch (Exception)
            {
                return attempted ? TransportSendOutcome.Unknown : TransportSendOutcome.NotSent;
            }
        }

        /// <summary>
        /// Tries to send a message to a module through the given connection.
        /// </summary>
        /// <param name="connectionId">Connection ID of recipient</param>
        /// <param name="module">Target module</param>
        /// <param name="serialisedMessage">Serialised message</param>
        /// <returns>Whether the message was sent successfully</returns>
        public bool TrySend(string connectionId, string module, byte[] serialisedMessage)
        {
            try
            {
                // Try to send the message
                Send(connectionId, module, serialisedMessage);
                
                return true;
            }
            catch (ArgumentException e)
            {
                // Catch more argument exceptions
                RaiseLogEntry(new LogEventArgs(string.Format("Cannot send message, argument {0} is null or empty", e.ParamName), LogLevel.ERROR));

                return false;
            }
            catch (NotConnectedException e)
            {
                // Catch connection exceptions
                RaiseLogEntry(new LogEventArgs("Cannot send message, " + e.Message, LogLevel.ERROR));

                return false;
            }
            catch (Exception e)
            {
                // Catch anything else — but log it, don't silently swallow
                RaiseLogEntry(new LogEventArgs("Got an unusual exception when sending a message: " + e, LogLevel.ERROR));

                return false;
            }
        }

        /// <summary>
        /// Disconnects a specific peer by connection ID and removes it from the connected peers dictionary.
        /// </summary>
        /// <param name="connectionId">Connection ID of the peer to disconnect</param>
        public void DisconnectPeer(string connectionId)
        {
            NetPeer peer;
            lock (connectedPeersLock)
            {
                if (!connectedPeers.TryGetValue(connectionId, out peer)) return;
                connectedPeers.Remove(connectionId);
            }
            lock (peerSynchronization.PeerGate) server.DisconnectPeer(peer);
        }

        /// <summary>
        /// Disposes managed resources: server NetManager, poll thread.
        /// </summary>
        public void Dispose()
        {
            Stop();
            server = null;
        }

    }
}
