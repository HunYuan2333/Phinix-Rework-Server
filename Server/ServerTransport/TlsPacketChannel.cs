// EXPERIMENTAL: TCP/TLS and modern identity are not recommended for deployment.
// Production identity migration is deferred (TODO); UDP/ClientKey remains the default.
using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ServerTransport;

/// <summary>Owns one authenticated stream. One read and one write may run concurrently; no implicit queue/reconnect.</summary>
public sealed class TlsPacketChannel : IAsyncDisposable
{
    private static readonly SslApplicationProtocol Protocol = new("phinix-stream/1");
    private readonly SslStream stream;
    private readonly int maximum;
    private readonly TimeSpan frameTimeout;
    private readonly SemaphoreSlim reader = new(1, 1), writer = new(1, 1);
    private int closed;
    public SslProtocols NegotiatedProtocol => stream.SslProtocol;

    private TlsPacketChannel(SslStream stream, int maximum, TimeSpan frameTimeout)
    { this.stream = stream; this.maximum = maximum; this.frameTimeout = frameTimeout; }

    private static void Limits(int maximum, TimeSpan timeout)
    {
        if (maximum < 1 || maximum > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    private static void Authenticated(SslStream stream)
    {
        if (!stream.IsAuthenticated || !stream.IsEncrypted || !stream.IsSigned ||
            stream.SslProtocol is not (SslProtocols.Tls12 or SslProtocols.Tls13) || stream.NegotiatedApplicationProtocol != Protocol)
            throw new AuthenticationException("Required authenticated packet transport was not negotiated.");
    }

    // Transport ownership transfers after argument validation, including authentication failure.
    public static async Task<TlsPacketChannel> AcceptAsync(Stream transport, X509Certificate2 certificate,
        int maximum = 1024 * 1024, TimeSpan? handshakeTimeout = null, TimeSpan? frameTimeout = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(transport); ArgumentNullException.ThrowIfNull(certificate);
        var handshake = handshakeTimeout ?? TimeSpan.FromSeconds(10); var frame = frameTimeout ?? TimeSpan.FromSeconds(30);
        Limits(maximum, handshake); Limits(maximum, frame);
        if (!certificate.HasPrivateKey) throw new ArgumentException("Server certificate requires its private key.", nameof(certificate));
        var ssl = new SslStream(transport, false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(handshake);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = certificate, ClientCertificateRequired = false, AllowRenegotiation = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, ApplicationProtocols = [Protocol]
            }, deadline.Token).ConfigureAwait(false);
            Authenticated(ssl); return new TlsPacketChannel(ssl, maximum, frame);
        }
        catch { ssl.Dispose(); throw; }
    }

    // Pin is a trusted, externally provisioned SPKI SHA-256, never learned from an unverified peer.
    // Optional root is a pretrusted private CA. Online revocation is the default; synthetic tests explicitly opt out.
    public static async Task<TlsPacketChannel> ConnectAsync(Stream transport, string targetHost, byte[] expectedSpkiSha256,
        X509Certificate2? trustedRoot = null, X509RevocationMode revocation = X509RevocationMode.Online,
        int maximum = 1024 * 1024, TimeSpan? handshakeTimeout = null, TimeSpan? frameTimeout = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(transport); ArgumentNullException.ThrowIfNull(expectedSpkiSha256);
        if (string.IsNullOrWhiteSpace(targetHost) || targetHost.Length > 253 || expectedSpkiSha256.Length != 32)
            throw new ArgumentException("Explicit server name and trusted SHA-256 pin required.");
        var handshake = handshakeTimeout ?? TimeSpan.FromSeconds(10); var frame = frameTimeout ?? TimeSpan.FromSeconds(30);
        Limits(maximum, handshake); Limits(maximum, frame);
        byte[] pin = (byte[])expectedSpkiSha256.Clone();
        var ssl = new SslStream(transport, false);
        try
        {
            var policy = new X509ChainPolicy { RevocationMode = revocation, VerificationFlags = X509VerificationFlags.NoFlag };
            policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            if (trustedRoot != null) { policy.TrustMode = X509ChainTrustMode.CustomRootTrust; policy.CustomTrustStore.Add(trustedRoot); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(handshake);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = targetHost, AllowRenegotiation = false, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [Protocol], CertificateChainPolicy = policy,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => errors == SslPolicyErrors.None &&
                    certificate is X509Certificate2 cert && CryptographicOperations.FixedTimeEquals(pin, SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()))
            }, deadline.Token).ConfigureAwait(false);
            Authenticated(ssl); return new TlsPacketChannel(ssl, maximum, frame);
        }
        catch { ssl.Dispose(); throw; }
    }

    private void Open() { if (Volatile.Read(ref closed) != 0) throw new IOException("Packet channel is closed; original operation outcome may be unknown."); }
    private void Abort() { if (Interlocked.Exchange(ref closed, 1) == 0) stream.Dispose(); }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellation = default)
    {
        Open(); if (packet.Length < 1 || packet.Length > maximum) throw new ArgumentOutOfRangeException(nameof(packet));
        if (!await writer.WaitAsync(0, cancellation).ConfigureAwait(false)) throw new InvalidOperationException("A packet write is already active.");
        try
        {
            Open(); byte[] owned = packet.ToArray();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(frameTimeout);
            byte[] header = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(header, (uint)owned.Length);
            await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(owned, deadline.Token).ConfigureAwait(false);
        }
        catch { Abort(); throw; } // A partial frame must never be followed by a new packet on this stream.
        finally { writer.Release(); }
    }

    public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellation = default)
    {
        Open(); if (!await reader.WaitAsync(0, cancellation).ConfigureAwait(false)) throw new InvalidOperationException("A packet read is already active.");
        try
        {
            Open(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(frameTimeout);
            byte[] header = new byte[4];
            if (await stream.ReadAsync(header.AsMemory(0, 1), deadline.Token).ConfigureAwait(false) == 0) { Abort(); return null; }
            await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size == 0 || size > maximum) throw new InvalidDataException("Packet frame exceeds negotiated bounds.");
            byte[] packet = new byte[(int)size]; await stream.ReadExactlyAsync(packet, deadline.Token).ConfigureAwait(false); return packet;
        }
        catch { Abort(); throw; }
        finally { reader.Release(); }
    }
    public ValueTask DisposeAsync() { Abort(); return ValueTask.CompletedTask; }
}
