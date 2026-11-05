using Authentication;
using Connections;
using ServerTransport;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    private static int sessionChecks;
    // Independent monotonic clock: UTC can move backwards without extending authority.
    private sealed class SessionClock : TimeProvider
    {
        long ticks;
        DateTimeOffset utc = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => utc;
        public void Advance(int seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
        public void RewindUtc() => utc = utc.AddDays(-1);
    }
    private static async Task SessionTests(string directory, X509Certificate2 root, X509Certificate2 certificate)
    {
        int startedChecks = checks;
        string path = Path.Combine(directory, "session.sqlite3");
        var store = PasswordlessIdentityStore.CreateNew(path);
        string credential = Guid.NewGuid().ToString(), nextCredential = Guid.NewGuid().ToString();
        byte[] secret = RandomNumberGenerator.GetBytes(32), replacement = RandomNumberGenerator.GetBytes(32);
        var principal = store.Register(Guid.NewGuid().ToString(), credential, secret).PrincipalId;
        using var udp = new NetServer(new IPEndPoint(IPAddress.Loopback, 0), 2);
        using (var denied = new PasswordlessSessionAuthority(store, udp))
            Check(denied.Authenticate("tls-forged", credential, secret, out var why) == null && why == IdentitySessionRefusal.SecureConnectionRequired,
                "A connection prefix admitted modern authentication on UDP");
        using var server = new TlsNetServer(new IPEndPoint(IPAddress.Loopback, 0), certificate, 4, 2, 4096);
        var connected = new ConcurrentQueue<string>();
        server.OnConnectionEstablished += (_, e) => connected.Enqueue(e.ConnectionId);
        server.Start();
        using var a = new TcpClient();
        await a.ConnectAsync(IPAddress.Loopback, server.LocalPort, Deadline.Token);
        await using var ca = await TlsPacketChannel.ConnectAsync(a.GetStream(), "localhost", SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()), root,
            X509RevocationMode.NoCheck, maximum: 4096, cancellation: Deadline.Token);
        await Wait(() => connected.Count == 1); connected.TryDequeue(out var aid);
        using var b = new TcpClient();
        await b.ConnectAsync(IPAddress.Loopback, server.LocalPort, Deadline.Token);
        await using var cb = await TlsPacketChannel.ConnectAsync(b.GetStream(), "localhost", SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()), root,
            X509RevocationMode.NoCheck, maximum: 4096, cancellation: Deadline.Token);
        await Wait(() => connected.Count == 1); connected.TryDequeue(out var bid);
        var clock = new SessionClock();
        using var sessions = new PasswordlessSessionAuthority(store, server, clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12), capacity: 1);
        var events = new ConcurrentQueue<IdentitySessionInvalidated>();
        using var expiryEntered = new ManualResetEventSlim();
        using var expiryRelease = new ManualResetEventSlim();
        // Sweep removes authority under its gate, then publishes outside it. Force that
        // real interval instead of racing a poll worker's eventual notification enqueue.
        sessions.Invalidated += (_, e) =>
        {
            if (e.Reason != IdentitySessionInvalidation.Expired) return;
            expiryEntered.Set();
            if (!expiryRelease.Wait(5000)) throw new TimeoutException("Synthetic expiry publication barrier");
        };
        sessions.Invalidated += (_, e) => events.Enqueue(e);
        Check(sessions.Authenticate("tls-forged", credential, secret, out var refusal) == null && refusal == IdentitySessionRefusal.SecureConnectionRequired,
            "Nonexistent TLS connection accepted credentials");
        var first = sessions.Authenticate(aid!, credential, secret, out refusal)!;
        Check(first != null && refusal == IdentitySessionRefusal.None && first.PrincipalId == principal, "Valid TLS identity session not issued");
        Check(!first!.ToString().Contains(first.SessionId) && first.SessionId.Length == 43, "Session diagnostic exposed bearer secret");
        Check(sessions.TryAuthorize(aid!, first.SessionId, out var facts) && facts!.PrincipalId == principal, "Session did not preserve principal");
        Check(!sessions.TryAuthorize(bid!, first.SessionId, out _) && !sessions.TryAuthorize(aid!, new string('x', 43), out _), "Cross-connection or unknown session accepted");
        Check(sessions.Authenticate(bid!, credential, secret, out refusal) == null && refusal == IdentitySessionRefusal.Capacity, "Session capacity not enforced");
        clock.Advance(4); clock.RewindUtc();
        var renewed = sessions.Renew(aid!, first.SessionId)!;
        Check(renewed != null && renewed.SessionId != first.SessionId && !sessions.TryAuthorize(aid!, first.SessionId, out _), "Renewal kept old session valid");
        clock.Advance(4);
        var last = sessions.Renew(aid!, renewed!.SessionId)!;
        Check(last != null && sessions.TryAuthorize(aid!, last.SessionId, out _), "Renewal failed before maximum age");
        clock.Advance(4);
        var expirySweep = Task.Run(() => sessions.Sweep());
        try
        {
            await Wait(() => expiryEntered.IsSet);
            Check(!sessions.TryAuthorize(aid!, last!.SessionId, out _) && !events.Any(e => e.Reason == IdentitySessionInvalidation.Expired),
                "Maximum-age authority remained live while expiry notification was in flight");
        }
        finally { expiryRelease.Set(); }
        await expirySweep;
        await Wait(() => events.Any(e => e.Reason == IdentitySessionInvalidation.Expired));
        Check(!sessions.TryAuthorize(aid!, last!.SessionId, out _), "Renewal/UTC rollback extended maximum authentication age");
        var rotatedSession = sessions.Authenticate(aid!, credential, secret, out _) !;
        store.RotateAuthorizedCredential(Guid.NewGuid().ToString(), principal, credential, nextCredential, replacement);
        Check(!sessions.TryAuthorize(aid!, rotatedSession.SessionId, out _) && events.Any(e => e.Reason == IdentitySessionInvalidation.CredentialChanged), "Credential rotation retained old session authority");
        var revokedSession = sessions.Authenticate(aid!, nextCredential, replacement, out _)!;
        store.RevokeAuthorizedCredential(Guid.NewGuid().ToString(), principal, nextCredential);
        clock.Advance(1); sessions.Sweep();
        Check(!sessions.TryAuthorize(aid!, revokedSession.SessionId, out _), "Credential revocation did not invalidate idle session");
        string otherCredential = Guid.NewGuid().ToString();
        store.Register(Guid.NewGuid().ToString(), otherCredential, replacement);
        // The admission budget counts failed and successful attempts, including capacity refusals.
        for (int i = 0; i < 10; i++) sessions.Authenticate(bid!, otherCredential, secret, out _);
        Check(sessions.Authenticate(bid!, otherCredential, replacement, out refusal) == null && refusal == IdentitySessionRefusal.RateLimited, "Authentication attempt budget bypassed");
        clock.Advance(60); sessions.Sweep();
        var active = sessions.Authenticate(bid!, otherCredential, replacement, out refusal)!;
        Check(active != null && refusal == IdentitySessionRefusal.None, "Rate window did not recover with monotonic time");
        await cb.DisposeAsync(); b.Close();
        await Wait(() => events.Any(e => e.ConnectionId == bid && e.Reason == IdentitySessionInvalidation.Disconnected));
        Check(!sessions.TryAuthorize(bid!, active!.SessionId, out _), "Disconnected session remained usable");
        var final = sessions.Authenticate(aid!, otherCredential, replacement, out _)!;
        Check(final != null, "Disconnected session did not release bounded capacity");
        // Corrupt only this synthetic DB, then restore its exact original bytes. An invalidated session must stay invalid.
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "SELECT value FROM state WHERE id=$id";
            command.Parameters.AddWithValue("$id", "identity.principal/" + final!.PrincipalId);
            byte[] original = (byte[])command.ExecuteScalar()!;
            command.CommandText = "UPDATE state SET value=$value WHERE id=$id";
            command.Parameters.AddWithValue("$value", System.Text.Encoding.UTF8.GetBytes("{}")); command.ExecuteNonQuery();
            Check(!sessions.TryAuthorize(aid!, final.SessionId, out _) && events.Any(e => e.Reason == IdentitySessionInvalidation.AuthorityUnavailable),
                "Unavailable identity authority retained session permissions");
            Check(sessions.Authenticate(aid!, otherCredential, replacement, out refusal) == null && refusal == IdentitySessionRefusal.AuthorityUnavailable,
                "Broken identity authority was reported as valid credentials");
            command.Parameters["$value"].Value = original; command.ExecuteNonQuery();
        }
        Check(!sessions.TryAuthorize(aid!, final!.SessionId, out _), "Repair revived a previously invalidated session");
        final = sessions.Authenticate(aid!, otherCredential, replacement, out _)!;
        Check(final != null, "Reauthentication failed after repairing synthetic authority");
        int observerChecks = 0;
        sessions.Invalidated += (_, _) => throw new InvalidOperationException("synthetic observer failure");
        sessions.Invalidated += (_, _) => observerChecks++;
        sessions.Dispose(); sessions.Dispose();
        Check(!sessions.TryAuthorize(aid!, final!.SessionId, out _) && sessions.Renew(aid!, final.SessionId) == null &&
            sessions.Authenticate(aid!, otherCredential, replacement, out refusal) == null && refusal == IdentitySessionRefusal.Stopped,
            "Stopped authority accepted authentication, renewal or authorization");
        Check(events.Any(e => e.Reason == IdentitySessionInvalidation.Stopped), "Shutdown did not invalidate active session");
        Check(observerChecks == 1, "Failed observer prevented other invalidation consumers or duplicate dispose notifications");
        await ca.DisposeAsync(); a.Close(); server.Stop();
        sessionChecks = checks - startedChecks;
    }
}
