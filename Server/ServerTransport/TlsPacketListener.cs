// EXPERIMENTAL: TCP/TLS and modern identity are not recommended for deployment.
// Production identity migration is deferred (TODO); UDP/ClientKey remains the default.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace ServerTransport;

public enum TlsConnectionFailure { ConnectionLimit, HandshakeLimit, HandshakeFailed, ApplicationFailed }

/// <summary>Bounded TLS listener. Owns sockets/channels; the caller keeps its certificate alive until Completion.</summary>
public sealed class TlsPacketListener : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private readonly Func<TlsPacketChannel, CancellationToken, Task> application;
    private readonly Action<TlsConnectionFailure>? diagnostic;
    private readonly int maximumConnections, maximumHandshakes, maximumPacket;
    private readonly TimeSpan handshakeTimeout, frameTimeout, shutdownTimeout;
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<long, Connection> connections = [];
    private readonly Task acceptLoop;
    private Task? completion;
    private long nextId;
    private int handshakes;
    private bool stopping;

    private sealed class Connection(TcpClient socket)
    {
        internal readonly TcpClient Socket = socket;
        internal Task Worker = Task.CompletedTask;
        internal bool Handshaking = true;
    }
    public IPEndPoint LocalEndpoint { get; }
    public Task AcceptCompletion => acceptLoop;
    public int ConnectionCount { get { lock (gate) return connections.Count; } }
    public int PendingHandshakes { get { lock (gate) return handshakes; } }
    // A timed-out Dispose does not certify termination or discard worker ownership.
    public Task Completion { get { lock (gate) return completion ?? throw new InvalidOperationException("Listener has not been stopped."); } }

    public TlsPacketListener(IPEndPoint endpoint, X509Certificate2 certificate,
        Func<TlsPacketChannel, CancellationToken, Task> application,
        int maximumConnections = 128, int maximumHandshakes = 8, int maximumPacket = 1024 * 1024,
        TimeSpan? handshakeTimeout = null, TimeSpan? frameTimeout = null, TimeSpan? shutdownTimeout = null,
        Action<TlsConnectionFailure>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint); ArgumentNullException.ThrowIfNull(certificate); ArgumentNullException.ThrowIfNull(application);
        if (!certificate.HasPrivateKey) throw new ArgumentException("TLS server certificate requires a private key.", nameof(certificate));
        if (maximumConnections < 1 || maximumConnections > 10000) throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        if (maximumHandshakes < 1 || maximumHandshakes > maximumConnections) throw new ArgumentOutOfRangeException(nameof(maximumHandshakes));
        if (maximumPacket < 1 || maximumPacket > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumPacket));
        this.handshakeTimeout = ValidTimeout(handshakeTimeout ?? TimeSpan.FromSeconds(10));
        this.frameTimeout = ValidTimeout(frameTimeout ?? TimeSpan.FromSeconds(30));
        this.shutdownTimeout = ValidTimeout(shutdownTimeout ?? TimeSpan.FromSeconds(10));
        this.maximumConnections = maximumConnections; this.maximumHandshakes = maximumHandshakes; this.maximumPacket = maximumPacket;
        this.certificate = certificate; this.application = application; this.diagnostic = diagnostic;
        listener = new TcpListener(endpoint);
        try { listener.Start(Math.Min(maximumConnections, 128)); LocalEndpoint = (IPEndPoint)listener.LocalEndpoint; acceptLoop = AcceptAsync(); }
        catch { listener.Stop(); stop.Dispose(); throw; }
    }
    private static TimeSpan ValidTimeout(TimeSpan value) => value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(2)
        ? value : throw new ArgumentOutOfRangeException(nameof(value));

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient socket = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                TlsConnectionFailure? refusal = null;
                lock (gate)
                {
                    if (stopping) socket.Dispose();
                    else if (connections.Count >= maximumConnections) refusal = TlsConnectionFailure.ConnectionLimit;
                    else if (handshakes >= maximumHandshakes) refusal = TlsConnectionFailure.HandshakeLimit;
                    else
                    {
                        var connection = new Connection(socket);
                        long id = ++nextId;
                        connections.Add(id, connection); handshakes++;
                        connection.Worker = ServeAsync(id, connection);
                    }
                }
                if (refusal.HasValue) { socket.Dispose(); Report(refusal.Value); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        // Unexpected listener failures remain observable through Completion; never silently restart/fallback.
    }
    private async Task ServeAsync(long id, Connection connection)
    {
        // Admission stores the worker under gate before any synchronous handshake failure can release it.
        await Task.Yield();
        bool authenticated = false;
        try
        {
            await using var channel = await TlsPacketChannel.AcceptAsync(connection.Socket.GetStream(), certificate,
                maximumPacket, handshakeTimeout, frameTimeout, stop.Token).ConfigureAwait(false);
            lock (gate) { connection.Handshaking = false; handshakes--; }
            authenticated = true;
            stop.Token.ThrowIfCancellationRequested();
            await application(channel, stop.Token).ConfigureAwait(false);
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch (Exception) { Report(authenticated ? TlsConnectionFailure.ApplicationFailed : TlsConnectionFailure.HandshakeFailed); }
        finally
        {
            connection.Socket.Dispose();
            lock (gate)
            {
                if (connection.Handshaking) handshakes--;
                connections.Remove(id);
            }
        }
    }
    private void Report(TlsConnectionFailure failure)
    {
        try { diagnostic?.Invoke(failure); }
        catch { /* Diagnostics cannot retain a socket or interrupt admission/cleanup. */ }
    }
    private async Task StopCoreAsync()
    {
        await Task.Yield();
        try
        {
            // Application cancellation callbacks must not prevent socket cleanup.
            Task callbacks = stop.CancelAsync();
            try
            {
                listener.Stop();
                Connection[] active; lock (gate) active = connections.Values.ToArray();
                foreach (var connection in active) connection.Socket.Dispose();
            }
            finally { await callbacks.ConfigureAwait(false); }
        }
        finally
        {
            try { await acceptLoop.ConfigureAwait(false); }
            finally
            {
                Task[] workers; lock (gate) workers = connections.Values.Select(c => c.Worker).ToArray();
                try { await Task.WhenAll(workers).ConfigureAwait(false); }
                finally { stop.Dispose(); }
            }
        }
    }
    public ValueTask DisposeAsync()
    {
        Task draining;
        lock (gate)
        {
            stopping = true;
            completion ??= StopCoreAsync();
            draining = completion;
        }
        return new ValueTask(draining.WaitAsync(shutdownTimeout));
    }
}
