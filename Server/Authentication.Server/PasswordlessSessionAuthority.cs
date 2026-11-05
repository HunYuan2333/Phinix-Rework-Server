#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Connections;

namespace Authentication;

public enum IdentitySessionRefusal { None, Stopped, SecureConnectionRequired, RateLimited, InvalidCredential, AuthorityUnavailable, Capacity }
public enum IdentitySessionInvalidation { Disconnected, Expired, Replaced, CredentialChanged, AuthorityUnavailable, Stopped }
public sealed class IssuedIdentitySession
{
    public string SessionId { get; }
    public string PrincipalId { get; }
    public DateTimeOffset ExpiresAt { get; }
    internal IssuedIdentitySession(string session, string principal, DateTimeOffset expires) { SessionId = session; PrincipalId = principal; ExpiresAt = expires; }
    public override string ToString() => "Identity session (secret omitted)";
}
public sealed record IdentitySessionInvalidated(string ConnectionId, string PrincipalId, IdentitySessionInvalidation Reason);

// Authentication/session boundary only. Facts stay server-owned; IsCurrent is never proof for client-supplied claims.
// The modern TLS identity endpoint owns this component; it is not a business or management capability.
public sealed class PasswordlessSessionAuthority : IDisposable
{
    private sealed record Entry(string Key, AuthenticatedTransportConnection Connection, PasswordlessIdentityStore.AuthenticationFacts Facts,
        long AuthenticatedAt, long IssuedAt, TimeSpan Lifetime);
    private sealed record Attempts(AuthenticatedTransportConnection Connection, long StartedAt, int Count);
    private readonly object gate = new();
    private readonly PasswordlessIdentityStore store;
    private readonly IServerTransport transport;
    private readonly TimeProvider clock;
    private readonly TimeSpan lifetime, maximumAge;
    private readonly int capacity;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> byConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Attempts> attempts = new(StringComparer.Ordinal);
    private bool stopped;
    private long lastSweep;
    public event EventHandler<IdentitySessionInvalidated>? Invalidated;
    public PasswordlessSessionAuthority(PasswordlessIdentityStore store, IServerTransport transport, TimeProvider? clock = null,
        TimeSpan? lifetime = null, TimeSpan? maximumAge = null, int capacity = 128)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store)); this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.clock = clock ?? TimeProvider.System;
        this.lifetime = lifetime ?? TimeSpan.FromMinutes(5); this.maximumAge = maximumAge ?? TimeSpan.FromMinutes(30);
        if (this.lifetime <= TimeSpan.Zero || this.maximumAge < this.lifetime || this.maximumAge > TimeSpan.FromHours(1) || capacity < 1 || capacity > 1024)
            throw new ArgumentOutOfRangeException();
        this.capacity = capacity; lastSweep = this.clock.GetTimestamp();
        transport.OnConnectionClosed += Closed;
        transport.PollCompleted += Poll;
    }
    private static string Key(string session)
    {
        if (session == null || session.Length != 43 || session.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')))
            return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(session)));
    }
    private bool LiveConnection(string id, out AuthenticatedTransportConnection? connection)
    {
        connection = null;
        return transport is IAuthenticatedServerTransport secure && secure.TryGetAuthenticatedConnection(id, out connection) && connection.IsLive;
    }
    private void Publish(Entry? entry, IdentitySessionInvalidation reason)
    {
        var observers = Invalidated;
        if (entry == null || observers == null) return;
        var notification = new IdentitySessionInvalidated(entry.Connection.ConnectionId, entry.Facts.PrincipalId, reason);
        foreach (EventHandler<IdentitySessionInvalidated> observer in observers.GetInvocationList())
        {
            try { observer(this, notification); }
            catch (Exception error)
            {
                // No credential/session values or arbitrary exception messages in diagnostics.
                try { transport.RaiseLogEntry(new Utils.LogEventArgs("Identity session observer failed: " + error.GetType().Name, Utils.LogLevel.ERROR)); } catch { }
            }
        }
    }
    private Entry? Remove(string key)
    {
        if (!entries.Remove(key, out var entry)) return null;
        if (byConnection.TryGetValue(entry.Connection.ConnectionId, out var active) && active == key) byConnection.Remove(entry.Connection.ConnectionId);
        return entry;
    }
    private IssuedIdentitySession Issue(AuthenticatedTransportConnection connection, PasswordlessIdentityStore.AuthenticationFacts facts,
        long authenticatedAt, TimeSpan duration, out Entry? previous)
    {
        previous = byConnection.TryGetValue(connection.ConnectionId, out var old) ? Remove(old) : null;
        string session;
        string key;
        do { session = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); key = Key(session); } while (entries.ContainsKey(key));
        entries.Add(key, new(key, connection, facts, authenticatedAt, clock.GetTimestamp(), duration)); byConnection[connection.ConnectionId] = key;
        return new(session, facts.PrincipalId, clock.GetUtcNow() + duration);
    }
    public IssuedIdentitySession? Authenticate(string connectionId, string credentialId, byte[] secret, out IdentitySessionRefusal refusal)
    {
        refusal = IdentitySessionRefusal.Stopped;
        lock (gate) if (stopped) return null;
        if (!LiveConnection(connectionId, out var connection)) { refusal = IdentitySessionRefusal.SecureConnectionRequired; return null; }
        lock (gate)
        {
            if (stopped) return null;
            long now = clock.GetTimestamp();
            if (!attempts.TryGetValue(connectionId, out var window) || !ReferenceEquals(window.Connection, connection) || clock.GetElapsedTime(window.StartedAt, now) >= TimeSpan.FromMinutes(1))
            {
                if (!attempts.ContainsKey(connectionId) && attempts.Count >= capacity * 2) { refusal = IdentitySessionRefusal.RateLimited; return null; }
                window = new(connection!, now, 0);
            }
            if (window.Count >= 10) { refusal = IdentitySessionRefusal.RateLimited; return null; }
            attempts[connectionId] = window with { Count = window.Count + 1 };
        }
        PasswordlessIdentityStore.AuthenticationFacts? facts;
        try { facts = store.Authenticate(credentialId, secret); }
        catch (ArgumentException) { refusal = IdentitySessionRefusal.InvalidCredential; return null; }
        catch (Exception) { refusal = IdentitySessionRefusal.AuthorityUnavailable; return null; }
        if (facts == null) { refusal = IdentitySessionRefusal.InvalidCredential; return null; }
        IssuedIdentitySession issued; Entry? previous;
        lock (gate)
        {
            if (stopped || !connection!.IsLive) { refusal = stopped ? IdentitySessionRefusal.Stopped : IdentitySessionRefusal.SecureConnectionRequired; return null; }
            if (!byConnection.ContainsKey(connectionId) && entries.Count >= capacity) { refusal = IdentitySessionRefusal.Capacity; return null; }
            issued = Issue(connection, facts, clock.GetTimestamp(), lifetime, out previous);
        }
        Publish(previous, IdentitySessionInvalidation.Replaced); refusal = IdentitySessionRefusal.None; return issued;
    }
    private bool Valid(Entry entry, out IdentitySessionInvalidation reason)
    {
        reason = IdentitySessionInvalidation.Disconnected;
        if (!entry.Connection.IsLive) return false;
        reason = IdentitySessionInvalidation.Expired;
        long now = clock.GetTimestamp();
        if (clock.GetElapsedTime(entry.IssuedAt, now) >= entry.Lifetime || clock.GetElapsedTime(entry.AuthenticatedAt, now) >= maximumAge) return false;
        reason = IdentitySessionInvalidation.CredentialChanged;
        try { return store.IsCurrent(entry.Facts); }
        catch (Exception) { reason = IdentitySessionInvalidation.AuthorityUnavailable; return false; }
    }
    public bool TryAuthorize(string connectionId, string sessionId, out PasswordlessIdentityStore.AuthenticationFacts? facts)
    {
        facts = null; Entry? invalid = null; IdentitySessionInvalidation reason = default;
        lock (gate)
        {
            if (stopped || !entries.TryGetValue(Key(sessionId), out var entry) || entry.Connection.ConnectionId != connectionId) return false;
            if (!Valid(entry, out reason)) invalid = Remove(entry.Key);
            else { facts = entry.Facts; return true; }
        }
        Publish(invalid, reason); return false;
    }
    public IssuedIdentitySession? Renew(string connectionId, string sessionId)
    {
        if (!TryAuthorize(connectionId, sessionId, out _)) return null;
        IssuedIdentitySession? issued = null; Entry? old = null; IdentitySessionInvalidation reason = IdentitySessionInvalidation.Replaced;
        lock (gate)
        {
            if (stopped || !entries.TryGetValue(Key(sessionId), out var entry) || entry.Connection.ConnectionId != connectionId) return null;
            // Verify again inside the ownership gate; an intervening rotation/expiry never extends the old proof.
            if (!Valid(entry, out reason)) old = Remove(entry.Key);
            else
            {
                var remaining = maximumAge - clock.GetElapsedTime(entry.AuthenticatedAt, clock.GetTimestamp());
                issued = Issue(entry.Connection, entry.Facts, entry.AuthenticatedAt, remaining < lifetime ? remaining : lifetime, out old);
                reason = IdentitySessionInvalidation.Replaced;
            }
        }
        Publish(old, reason); return issued;
    }
    public void Sweep()
    {
        var invalid = new List<(Entry, IdentitySessionInvalidation)>();
        lock (gate)
        {
            if (stopped || clock.GetElapsedTime(lastSweep, clock.GetTimestamp()) < TimeSpan.FromSeconds(1)) return;
            lastSweep = clock.GetTimestamp();
            foreach (var entry in entries.Values.ToArray()) if (!Valid(entry, out var reason)) { Remove(entry.Key); invalid.Add((entry, reason)); }
            foreach (var pair in attempts.ToArray()) if (!pair.Value.Connection.IsLive || clock.GetElapsedTime(pair.Value.StartedAt, lastSweep) >= TimeSpan.FromMinutes(1)) attempts.Remove(pair.Key);
        }
        foreach (var item in invalid) Publish(item.Item1, item.Item2);
    }
    private void Poll(object? sender, EventArgs args) => Sweep();
    private void Closed(object? sender, ConnectionEventArgs args)
    {
        Entry? removed = null;
        lock (gate) { if (byConnection.TryGetValue(args.ConnectionId, out var key)) removed = Remove(key); attempts.Remove(args.ConnectionId); }
        Publish(removed, IdentitySessionInvalidation.Disconnected);
    }
    public void Dispose()
    {
        Entry[] removed;
        lock (gate) { if (stopped) return; stopped = true; removed = entries.Values.ToArray(); entries.Clear(); byConnection.Clear(); attempts.Clear(); }
        transport.OnConnectionClosed -= Closed; transport.PollCompleted -= Poll;
        foreach (var entry in removed) Publish(entry, IdentitySessionInvalidation.Stopped);
    }
}
