// EXPERIMENTAL: TCP/TLS and modern identity are not recommended for deployment.
// Production identity migration is deferred (TODO); UDP/ClientKey remains the default.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ServerTransport;
using Utils;

namespace Connections
{
    // TLS adapter uses the same module handlers as UDP. All lifecycle/application callbacks run on one poll worker.
    public sealed class TlsNetServer : IServerTransport, IAuthenticatedServerTransport
    {
        private readonly object gate = new();
        private readonly object lifecycle = new();
        private readonly Dictionary<string, NetCommon.PacketHandlerDelegate> handlers = new();
        private readonly Dictionary<string, Session> sessions = new();
        private readonly X509Certificate2 certificate;
        private readonly int maximumConnections, maximumHandshakes, maximumPacket;
        private readonly bool ownsCertificate;
        private TlsPacketListener listener;
        private Channel<Work> events;
        private Thread worker;
        private long incomingBytes;
        private bool stopping, disposed;
        private const int ByteBudget = 16 * 1024 * 1024;
        private const int OutgoingBudget = 4 * 1024 * 1024;
        private sealed record Work(Session Session, int Kind, string Module = null, byte[] Payload = null);
        private sealed class Session(string id, TlsPacketChannel channel)
        {
            internal readonly string Id = id;
            internal readonly TlsPacketChannel Channel = channel;
            internal readonly Channel<byte[]> Outgoing = System.Threading.Channels.Channel.CreateBounded<byte[]>(16);
            internal readonly object Gate = new();
            internal int Bytes;
            internal bool Closed;
            internal AuthenticatedTransportConnection Security;
        }
        public IPEndPoint Endpoint { get; }
        public int LocalPort => listener?.LocalEndpoint.Port ?? 0;
        public bool Listening { get { lock (gate) return listener != null && !stopping && !listener.AcceptCompletion.IsCompleted; } }
        public event EventHandler<LogEventArgs> OnLogEntry;
        public event EventHandler PollCompleted;
        public event EventHandler<ConnectionEventArgs> OnConnectionEstablished;
        public event EventHandler<ConnectionEventArgs> OnConnectionClosed;
        public void RaiseLogEntry(LogEventArgs args) => OnLogEntry?.Invoke(this, args);
        private void Report(string type) { try { RaiseLogEntry(new LogEventArgs("TLS transport failure: " + type, LogLevel.ERROR)); } catch { } }

        public TlsNetServer(IPEndPoint endpoint, X509Certificate2 certificate, int maximumConnections = 128,
            int maximumHandshakes = 8, int maximumPacket = 1024 * 1024, bool ownsCertificate = false)
        {
            ArgumentNullException.ThrowIfNull(endpoint); ArgumentNullException.ThrowIfNull(certificate);
            if (!certificate.HasPrivateKey) throw new ArgumentException("TLS certificate requires a private key.");
            if (maximumConnections < 1 || maximumConnections > 10000 || maximumHandshakes < 1 || maximumHandshakes > maximumConnections ||
                maximumPacket < 3 || maximumPacket > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException();
            Endpoint = endpoint; this.certificate = certificate; this.maximumConnections = maximumConnections;
            this.maximumHandshakes = maximumHandshakes; this.maximumPacket = maximumPacket; this.ownsCertificate = ownsCertificate;
        }
        public bool TryGetAuthenticatedConnection(string connectionId, out AuthenticatedTransportConnection connection)
        {
            lock (gate)
            {
                connection = null;
                if (stopping || !sessions.TryGetValue(connectionId ?? "", out var session) || !session.Security.IsLive) return false;
                connection = session.Security; return true;
            }
        }
        public void RegisterPacketHandler(string module, NetCommon.PacketHandlerDelegate handler)
        { if (module.StartsWith("$transport.", StringComparison.Ordinal)) throw new ArgumentException("Reserved transport module."); lock (gate) { if (handlers.ContainsKey(module)) throw new PacketHandlerAlreadyRegisteredException(module); handlers.Add(module, handler); } }
        public void UnregisterPacketHandler(string module) { lock (gate) handlers.Remove(module); }
        public void UnregisterAllPacketHandlers() { lock (gate) handlers.Clear(); }
        public void Start() { lock (lifecycle) StartCore(); }
        private void StartCore()
        {
            StopCore();
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(TlsNetServer));
                stopping = false; incomingBytes = 0;
                events = Channel.CreateBounded<Work>(256);
                worker = new Thread(Poll) { IsBackground = true, Name = "Phinix TLS poll" };
            }
            try
            {
                worker.Start();
                var started = new TlsPacketListener(Endpoint, certificate, Serve, maximumConnections, maximumHandshakes, maximumPacket,
                    diagnostic: failure => Report(failure.ToString()));
                lock (gate) listener = started;
            }
            catch
            {
                lock (gate) stopping = true;
                events.Writer.TryComplete();
                // Joining while holding gate would deadlock a queued handler that calls TrySend.
                if ((worker.ThreadState & ThreadState.Unstarted) == 0 && !worker.Join(2000))
                    throw new TimeoutException("TLS startup failed and poll worker has not stopped.");
                lock (gate) { worker = null; events = null; }
                throw;
            }
        }
        private void Poll()
        {
            while (true)
            {
                for (int i = 0; i < 32 && events.Reader.TryRead(out Work work); i++)
                {
                    try
                    {
                        if (work.Kind == 0) OnConnectionEstablished?.Invoke(this, new ConnectionEventArgs(work.Session.Id));
                        else if (work.Kind == 2) OnConnectionClosed?.Invoke(this, new ConnectionEventArgs(work.Session.Id));
                        else
                        {
                            NetCommon.PacketHandlerDelegate handler; lock (gate) handlers.TryGetValue(work.Module, out handler);
                            handler?.Invoke(work.Module, work.Session.Id, work.Payload);
                        }
                    }
                    catch (Exception error) { Report(error.GetType().Name); }
                    finally { if (work.Payload != null) Interlocked.Add(ref incomingBytes, -work.Payload.Length); }
                }
                try { PollCompleted?.Invoke(this, EventArgs.Empty); } catch (Exception error) { Report(error.GetType().Name); }
                if (events.Reader.Completion.IsCompleted) break;
                Thread.Sleep(10);
            }
        }
        private async Task Serve(TlsPacketChannel channel, CancellationToken cancellation)
        {
            var session = new Session("tls-" + Guid.NewGuid().ToString("N"), channel);
            session.Security = new AuthenticatedTransportConnection(session.Id, () => {
                lock (session.Gate) return !session.Closed && !Volatile.Read(ref stopping);
            });
            lock (gate) { if (stopping) return; sessions.Add(session.Id, session); }
            bool connected = false; Task writing = null;
            try
            {
                writing = Write(session, cancellation);
                await events.Writer.WriteAsync(new Work(session, 0), cancellation); connected = true;
                while (true)
                {
                    byte[] body = await channel.ReadAsync(cancellation); if (body == null) break;
                    var packet = TlsModulePacket.Decode(body);
                    if (packet.Module == "$transport.ping")
                    {
                        if (packet.Payload.Length != 0 || TrySendWithOutcome(session.Id, "$transport.pong", Array.Empty<byte>()) != TransportSendOutcome.Submitted)
                            throw new IOException("Transport keepalive was not admitted.");
                        continue;
                    }
                    if (packet.Module.StartsWith("$transport.", StringComparison.Ordinal)) throw new InvalidDataException("Unknown transport control.");
                    long bytes = Interlocked.Add(ref incomingBytes, packet.Payload.Length);
                    if (bytes > ByteBudget || !events.Writer.TryWrite(new Work(session, 1, packet.Module, packet.Payload)))
                    { Interlocked.Add(ref incomingBytes, -packet.Payload.Length); throw new IOException("Incoming transport budget exceeded."); }
                }
            }
            finally
            {
                lock (session.Gate) { session.Closed = true; session.Outgoing.Writer.TryComplete(); }
                lock (gate) sessions.Remove(session.Id);
                await channel.DisposeAsync();
                try { if (writing != null) await writing; }
                finally { if (connected) await events.Writer.WriteAsync(new Work(session, 2)); }
            }
        }
        private async Task Write(Session session, CancellationToken cancellation)
        {
            try
            {
                await foreach (byte[] body in session.Outgoing.Reader.ReadAllAsync(cancellation))
                {
                    try { await session.Channel.WriteAsync(body, cancellation); }
                    finally { lock (session.Gate) session.Bytes -= body.Length; }
                }
            }
            finally { await session.Channel.DisposeAsync(); }
        }
        public TransportSendOutcome TrySendWithOutcome(string connectionId, string module, byte[] payload)
        {
            byte[] body; try { body = TlsModulePacket.Encode(module, payload, maximumPacket); } catch (Exception) { return TransportSendOutcome.NotSent; }
            Session session; lock (gate) { if (stopping || !sessions.TryGetValue(connectionId ?? "", out session)) return TransportSendOutcome.NotSent; }
            lock (session.Gate)
            {
                if (session.Closed || session.Bytes > OutgoingBudget - body.Length) return TransportSendOutcome.NotSent;
                if (!session.Outgoing.Writer.TryWrite(body)) return TransportSendOutcome.NotSent;
                session.Bytes += body.Length;
                return TransportSendOutcome.Submitted; // Bounded local queue owns bytes; not TLS flush or remote/application ACK.
            }
        }
        public void Send(string connectionId, string module, byte[] payload)
        { if (TrySendWithOutcome(connectionId, module, payload) == TransportSendOutcome.NotSent) throw new IOException("TLS packet was not admitted."); }
        public bool TrySend(string connectionId, string module, byte[] payload) => TrySendWithOutcome(connectionId, module, payload) == TransportSendOutcome.Submitted;
        public void DisconnectPeer(string id)
        { Session session; lock (gate) sessions.TryGetValue(id ?? "", out session); if (session != null) session.Channel.DisposeAsync().GetAwaiter().GetResult(); }
        public void Stop() { lock (lifecycle) StopCore(); }
        private void StopCore()
        {
            TlsPacketListener active; Thread polling;
            lock (gate)
            {
                polling = worker;
                if (polling == Thread.CurrentThread) throw new InvalidOperationException("TLS transport must stop outside its poll worker.");
                stopping = true; active = listener;
            }
            // On timeout, preserve listener/worker/event references. Do not certify shutdown or dispose their scope.
            if (active != null) active.DisposeAsync().AsTask().GetAwaiter().GetResult();
            events?.Writer.TryComplete();
            if (polling != null && !polling.Join(2000)) throw new TimeoutException("TLS poll worker did not stop.");
            lock (gate) { listener = null; worker = null; events = null; }
        }
        public void Dispose()
        {
            lock (lifecycle)
            {
                StopCore(); lock (gate) { if (disposed) return; disposed = true; }
                if (ownsCertificate) certificate.Dispose();
            }
        }
    }
}
