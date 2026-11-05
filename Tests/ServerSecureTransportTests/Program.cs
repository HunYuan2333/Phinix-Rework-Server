using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ServerTransport;

internal static class Program
{
    private static int checks;
    private static readonly List<string> scenarios = [];
    private static readonly CancellationTokenSource Overall = new(TimeSpan.FromSeconds(60));
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }

    private static X509Certificate2 Root()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Phinix synthetic test CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(3));
    }
    private static X509Certificate2 Leaf(X509Certificate2 root, bool expired = false, bool wrongPurpose = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(wrongPurpose ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        using var publicOnly = request.Create(root, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1), RandomNumberGenerator.GetBytes(16));
        return publicOnly.CopyWithPrivateKey(key);
    }
    private static byte[] Pin(X509Certificate2 cert) => SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo());

    private static async Task WithPair(X509Certificate2 certificate, Func<NetworkStream, Task<TlsPacketChannel>> connect,
        Func<TlsPacketChannel, TlsPacketChannel, Task> action)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); var accept = listener.AcceptTcpClientAsync(Overall.Token).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Overall.Token);
        using var server = await accept;
        var accepting = TlsPacketChannel.AcceptAsync(server.GetStream(), certificate, maximum: 4096, frameTimeout: TimeSpan.FromSeconds(2), cancellation: Overall.Token);
        try
        {
            await using var outgoing = await connect(client.GetStream()); await using var incoming = await accepting;
            await action(incoming, outgoing);
        }
        finally { try { await (await accepting).DisposeAsync(); } catch (AuthenticationException) { } catch (IOException) { } }
    }
    private static async Task WithRaw(X509Certificate2 root, X509Certificate2 certificate, Func<TlsPacketChannel, SslStream, Task> action, SslProtocols versions = SslProtocols.Tls12 | SslProtocols.Tls13, string application = "phinix-stream/1")
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(Overall.Token).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Overall.Token); using var server = await accept;
        var accepting = TlsPacketChannel.AcceptAsync(server.GetStream(), certificate, maximum: 4096, frameTimeout: TimeSpan.FromSeconds(2), cancellation: Overall.Token);
        using var ssl = new SslStream(client.GetStream(), false);
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck }; policy.CustomTrustStore.Add(root);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", CertificateChainPolicy = policy,
                EnabledSslProtocols = versions, ApplicationProtocols = [new(application)] }, Overall.Token);
            await using var incoming = await accepting; await action(incoming, ssl);
        }
        finally { try { await (await accepting).DisposeAsync(); } catch (AuthenticationException) { } catch (IOException) { } }

    }

    private static async Task Main()
    {
        using var root = Root(); using var other = Root(); using var cert = Leaf(root); using var expired = Leaf(root, true); using var wrongPurpose = Leaf(root, wrongPurpose:true);
        Task<TlsPacketChannel> Connect(NetworkStream stream) => TlsPacketChannel.ConnectAsync(stream, "localhost", Pin(cert), root,
            X509RevocationMode.NoCheck, maximum: 4096, frameTimeout: TimeSpan.FromSeconds(2), cancellation: Overall.Token);
        await WithPair(cert, Connect, async (server, client) => {
            Check(client.NegotiatedProtocol is SslProtocols.Tls12 or SslProtocols.Tls13, "Unprotected protocol");
            byte[] body = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
            await client.WriteAsync(body); Check((await server.ReadAsync())!.SequenceEqual(body), "Full opaque packet changed");
            await server.WriteAsync(new byte[] { 7 }); Check((await client.ReadAsync())!.SequenceEqual(new byte[] { 7 }), "Reverse packet changed");
            await Throws<ArgumentOutOfRangeException>(() => client.WriteAsync(new byte[4097]).AsTask());
            await client.WriteAsync(new byte[] { 8 }); Check((await server.ReadAsync())![0] == 8, "Pre-send refusal damaged channel");
        }); scenarios.Add("actual TLS bidirectional bounded packet roundtrip");
        foreach (var name in new[] { "wrong-pin", "wrong-name", "untrusted-root", "expired", "wrong-purpose" })
        {
            var offered = name == "expired" ? expired : name == "wrong-purpose" ? wrongPurpose : cert;
            await Throws<AuthenticationException>(() => WithPair(offered, stream => TlsPacketChannel.ConnectAsync(stream,
                name == "wrong-name" ? "wrong.invalid" : "localhost", name == "wrong-pin" ? new byte[32] : Pin(offered), name == "untrusted-root" ? other : root,
                X509RevocationMode.NoCheck, maximum: 4096, cancellation: Overall.Token), (_, _) => throw new Exception("Application called after trust rejection")));
            scenarios.Add(name + " rejects before application");
        }
        await WithRaw(root, cert, async (server, raw) => {
            // Independent literal network oracle: each byte is a separate TLS write, no product encoder.
            foreach (byte b in new byte[] { 0, 0, 0, 3, 21, 22, 23 }) await raw.WriteAsync(new byte[] { b });
            Check((await server.ReadAsync())!.SequenceEqual(new byte[] { 21, 22, 23 }), "Fragmentation changed frame");
        }); scenarios.Add("independent fragmented frame");
        await WithRaw(root, cert, async (server, raw) => {
            Check(server.NegotiatedProtocol==SslProtocols.Tls12,"TLS 1.2 pilot did not negotiate TLS 1.2");
            await raw.WriteAsync(new byte[] {0,0,0,1,42});Check((await server.ReadAsync())![0]==42,"TLS 1.2 opaque frame differs");
        },SslProtocols.Tls12);scenarios.Add("explicit TLS 1.2 independent client interop");
        await Throws<AuthenticationException>(()=>WithRaw(root,cert,(_,_)=>throw new Exception("Wrong ALPN reached application"),application:"unrelated/1"));
        scenarios.Add("foreign ALPN rejected without application callback");
        foreach (uint size in new uint[] { 0, 4097, uint.MaxValue })
        {
            await WithRaw(root, cert, async (server, raw) => {
                byte[] header = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(header, size); await raw.WriteAsync(header);
                await Throws<InvalidDataException>(() => server.ReadAsync().AsTask());
                await Throws<IOException>(() => server.ReadAsync().AsTask());
            });
        } scenarios.Add("zero oversized and uint-max lengths close channel");
        await WithRaw(root, cert, async (server, raw) => {
            await raw.WriteAsync(new byte[] { 0, 0, 0, 3, 1 }); await raw.ShutdownAsync();
            await Throws<EndOfStreamException>(() => server.ReadAsync().AsTask());
        }); scenarios.Add("truncated body never delivered");
        await WithRaw(root, cert, async (server, raw) => {
            using var stop = new CancellationTokenSource(); var pending = server.ReadAsync(stop.Token).AsTask();
            await Throws<InvalidOperationException>(() => server.ReadAsync().AsTask()); stop.Cancel();
            await Throws<OperationCanceledException>(() => pending); await Throws<IOException>(() => server.WriteAsync(new byte[] { 1 }).AsTask());
        }); scenarios.Add("read concurrency refusal and cancellation close uncertain stream");
        await WithRaw(root, cert, async (server, raw) => { await raw.ShutdownAsync(); Check(await server.ReadAsync() == null, "Clean EOF invented a frame"); });
        scenarios.Add("clean EOF yields no packet");
        await WithRaw(root, cert, async (server, raw) => {
            await raw.WriteAsync(new byte[] { 0, 0 });
            await Throws<OperationCanceledException>(() => server.ReadAsync().AsTask());
            await Throws<IOException>(() => server.ReadAsync().AsTask());
        }); scenarios.Add("partial header deadline terminates channel");
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start(); using var client = new TcpClient(); var accept = listener.AcceptTcpClientAsync(Overall.Token).AsTask();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Overall.Token); using var peer = await accept;
            await Throws<OperationCanceledException>(() => TlsPacketChannel.AcceptAsync(peer.GetStream(), cert, handshakeTimeout: TimeSpan.FromMilliseconds(100), cancellation: Overall.Token));
            byte[] one = new byte[1];Check(await client.GetStream().ReadAsync(one, Overall.Token)==0,"Handshake timeout did not close owned transport");
        } scenarios.Add("silent peer handshake deadline and owned stream disposal");
        ModuleScenarios.Run(Check);
        scenarios.Add("independent module envelope and malformed inputs");
        await ListenerScenarios.Run(cert, root, Pin(cert), Check);
        scenarios.Add("bounded listener admission, recovery and owned shutdown");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { passed = true, checks, scenarios, profile = "TLS packet pilot / phinix-stream/1", secretsWritten = false, productionIntegration = false }));
    }
}
