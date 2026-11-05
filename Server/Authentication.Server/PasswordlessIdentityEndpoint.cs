// EXPERIMENTAL: TCP/TLS and modern identity are not recommended for deployment.
// Production identity migration is deferred (TODO); UDP/ClientKey remains the default.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Connections;
using ServerTransport;

namespace Authentication;

// TLS-only wire boundary. Previous identity proof is interpreted by the supplied adapter only.
public sealed class PasswordlessIdentityEndpoint : IDisposable
{
    private readonly IServerTransport transport;
    private readonly ServerAuthenticator facade;
    private readonly PasswordlessIdentityStore store;
    private readonly PasswordlessSessionAuthority sessions;
    private readonly Func<string, string?> resolvePrevious;
    private readonly string name;
    private readonly Dictionary<string, (long At, int Count)> budgets = new();
    private readonly Dictionary<string, string> active = new();
    private readonly Dictionary<string, string> principals = new();
    private readonly HashSet<string> changingCredential = new();
    private readonly object gate = new();
    private long window = Environment.TickCount64;
    private int writes;
    private readonly int registrationBudget;
    private bool stopped, reportedUnavailable;
    private readonly Action<string>? barrier;
    public PasswordlessIdentityEndpoint(IServerTransport transport, ServerAuthenticator facade, PasswordlessIdentityStore store,
        Func<string, string?> resolvePrevious, string serverName, int registrationBudget = 10) : this(transport, facade, store, resolvePrevious, serverName, null, registrationBudget) { }
    internal PasswordlessIdentityEndpoint(IServerTransport transport, ServerAuthenticator facade, PasswordlessIdentityStore store,
        Func<string, string?> resolvePrevious, string serverName, Action<string>? barrier) : this(transport, facade, store, resolvePrevious, serverName, barrier, 10) { }
    private PasswordlessIdentityEndpoint(IServerTransport transport, ServerAuthenticator facade, PasswordlessIdentityStore store,
        Func<string, string?> resolvePrevious, string serverName, Action<string>? barrier, int registrationBudget)
    {
        if (registrationBudget < 1 || registrationBudget > 1000) throw new ArgumentOutOfRangeException(nameof(registrationBudget));
        this.registrationBudget = registrationBudget;
        this.barrier = barrier;
        if (transport is not IAuthenticatedServerTransport) throw new ArgumentException("Modern identity requires an authenticated transport.");
        ArgumentNullException.ThrowIfNull(facade); ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(resolvePrevious);
        if (string.IsNullOrWhiteSpace(serverName) || System.Text.Encoding.UTF8.GetByteCount(serverName) > 1024) throw new ArgumentException("Identity server title exceeds the bootstrap budget.");
        this.transport = transport; this.facade = facade; this.store = store; this.resolvePrevious = resolvePrevious; name = serverName;
        sessions = new PasswordlessSessionAuthority(store, transport);
        bool registered = false;
        try
        {
            transport.RegisterPacketHandler(IdentityWire.Module, Handle); registered = true;
            facade.UseModernIdentity(sessions);
            transport.OnConnectionEstablished += Connected; transport.OnConnectionClosed += Closed; sessions.Invalidated += Invalidated;
        }
        catch { sessions.Dispose(); if (registered) transport.UnregisterPacketHandler(IdentityWire.Module); throw; }
    }
    private void Connected(object? _, ConnectionEventArgs args) => Send(args.ConnectionId, new() { Kind = "Hello", ServerId = store.ServerId, ServerName = name });
    private void Closed(object? _, ConnectionEventArgs args) { lock (gate) { budgets.Remove(args.ConnectionId); active.Remove(args.ConnectionId); principals.Remove(args.ConnectionId); changingCredential.Remove(args.ConnectionId); } }
    private void Invalidated(object? _, IdentitySessionInvalidated args)
    {
        lock (gate)
            if (args.Reason == IdentitySessionInvalidation.Replaced || args.Reason == IdentitySessionInvalidation.Stopped ||
                (args.Reason == IdentitySessionInvalidation.CredentialChanged && changingCredential.Contains(args.ConnectionId))) return;
        transport.DisconnectPeer(args.ConnectionId);
    }
    private bool Budget(string connection, bool mutation)
    {
        long now = Environment.TickCount64;
        if (now - window >= 60000) { window = now; writes = 0; }
        if (!budgets.TryGetValue(connection, out var b) || now - b.At >= 60000) b = (now, 0);
        if (b.Count >= 60 || (mutation && writes >= registrationBudget)) return false;
        budgets[connection] = (b.At, b.Count + 1); if (mutation) writes++; return true;
    }
    private void Send(string connection, IdentityEnvelope packet)
    { if (!transport.TrySend(connection, IdentityWire.Module, IdentityWire.Encode(packet))) transport.DisconnectPeer(connection); }
    private static byte[] Secret(string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length != 32) { CryptographicOperations.ZeroMemory(bytes); throw new ArgumentException("Invalid credential material."); }
        return bytes;
    }
    private void Handle(string module, string connection, byte[] bytes)
    {
        lock (gate)
        {
            if (stopped) return;
            if (!((IAuthenticatedServerTransport)transport).TryGetAuthenticatedConnection(connection, out var lease) || !lease.IsLive) { transport.DisconnectPeer(connection); return; }
            IdentityEnvelope request;
            try { request = IdentityWire.Decode(bytes); }
            catch { transport.DisconnectPeer(connection); return; }
            var response = new IdentityEnvelope { Kind = "Result", RequestId = request.RequestId, ServerId = store.ServerId, OperationId = request.OperationId };
            try
            {
                if (!Guid.TryParseExact(request.RequestId, "D", out var requestId) || requestId == Guid.Empty || request.ServerId != store.ServerId)
                    throw new ArgumentException("Invalid request binding.");
                bool mutation = request.Kind is "Enroll" or "Rotate" or "Revoke";
                if (!Budget(connection, mutation)) { response.Failure = "RateLimited"; Send(connection, response); return; }
                IdentityWire.ValidateRequest(request);
                if (request.Kind == "Resolve")
                {
                    response.PrincipalId = resolvePrevious(request.PreviousProof) ?? "";
                }
                else if (request.Kind == "Enroll")
                {
                    byte[] secret = Secret(request.Secret);
                    try
                    {
                        string? principal = string.IsNullOrEmpty(request.PreviousProof) ? null : resolvePrevious(request.PreviousProof);
                        if (request.PrincipalId != (principal ?? "")) throw new InvalidOperationException("Identity binding differs.");
                        var result = principal == null ? store.Register(request.OperationId, request.CredentialId, secret) :
                            store.BindAuthorizedPrincipal(request.OperationId, principal, request.CredentialId, secret);
                        response.PrincipalId = result.PrincipalId; response.CredentialId = result.CredentialId;
                    }
                    finally { CryptographicOperations.ZeroMemory(secret); }
                }
                else if (request.Kind == "Authenticate")
                {
                    byte[] secret = Secret(request.Secret);
                    try
                    {
                        // A connection cannot switch principals, including before business negotiation.
                        var facts = store.Authenticate(request.CredentialId, secret);
                        if (facts == null) { response.Failure = "InvalidCredential"; Send(connection, response); return; }
                        if (principals.TryGetValue(connection, out var pinnedPrincipal) && pinnedPrincipal != facts.PrincipalId)
                            throw new InvalidOperationException("Credential was refused.");
                        var issued = sessions.Authenticate(connection, request.CredentialId, secret, out var refusal);
                        if (issued == null) { response.Failure = refusal.ToString(); Send(connection, response); return; }
                        Apply(connection, response, issued);
                    }
                    finally { CryptographicOperations.ZeroMemory(secret); }
                }
                else if (request.Kind == "RevocationStatus")
                {
                    byte[] secret = Secret(request.Secret);
                    try
                    {
                        var result = store.QueryOwnRevocation(request.OperationId, request.CredentialId, secret);
                        if (result == null) response.Failure = "OperationNotFound";
                        else { response.PrincipalId = result.PrincipalId; response.CredentialId = result.CredentialId; }
                    }
                    finally { CryptographicOperations.ZeroMemory(secret); }
                }
                else if (request.Kind == "Renew")
                {
                    var issued = sessions.Renew(connection, request.SessionId);
                    if (issued == null) throw new InvalidOperationException("Session renewal was refused.");
                    Apply(connection, response, issued);
                }
                else if (request.Kind is "Rotate" or "Revoke")
                {
                    if (!sessions.TryAuthorize(connection, request.SessionId, out var facts)) throw new InvalidOperationException("Session is not authorized.");
                    if (request.CredentialId != facts!.CredentialId) throw new InvalidOperationException("Credential owner differs.");
                    changingCredential.Add(connection);
                    PasswordlessIdentityStore.MutationResult changed;
                    if (request.Kind == "Rotate")
                    {
                        byte[] next = Secret(request.NextSecret);
                        try { changed = store.RotateAuthorizedCredential(request.OperationId, facts.PrincipalId, facts.CredentialId, request.NextCredentialId, next); }
                        finally { CryptographicOperations.ZeroMemory(next); }
                    }
                    else changed = store.RevokeAuthorizedCredential(request.OperationId, facts.PrincipalId, facts.CredentialId);
                    response.PrincipalId = changed.PrincipalId; response.CredentialId = changed.CredentialId;
                }
                else throw new ArgumentException("Unknown identity operation.");
            }
            catch (ServerStorage.OperationConflictException) { changingCredential.Remove(connection); response.Failure = "Conflict"; }
            catch (ServerStorage.JournalCapacityException) { changingCredential.Remove(connection); response.Failure = "Capacity"; }
            catch (ServerStorage.JournalBusyException) { changingCredential.Remove(connection); response.Failure = "Busy"; }
            catch (Exception error) when (error is ArgumentException || error is FormatException || error is InvalidOperationException)
            { changingCredential.Remove(connection); response.Failure = "Refused"; }
            catch (Exception error)
            {
                changingCredential.Remove(connection); response.Failure = "AuthorityUnavailable";
                if (!reportedUnavailable)
                {
                    reportedUnavailable = true;
                    try { transport.RaiseLogEntry(new Utils.LogEventArgs("Identity authority unavailable: " + error.GetType().Name, Utils.LogLevel.ERROR)); } catch { }
                }
            }
            if (response.Failure == "" && request.Kind != "Resolve") reportedUnavailable = false;
            if (response.Failure == "")
            {
                if (request.Kind == "Enroll") barrier?.Invoke("identity.enroll.after-commit-before-response");
                if (request.Kind == "Rotate") barrier?.Invoke("identity.rotate.after-commit-before-response");
                if (request.Kind == "Revoke") barrier?.Invoke("identity.revoke.after-commit-before-response");
            }
            Send(connection, response);
        }
    }
    private void Apply(string connection, IdentityEnvelope response, IssuedIdentitySession issued)
    {
        active.TryGetValue(connection, out var previous); active[connection] = issued.SessionId; principals[connection] = issued.PrincipalId; changingCredential.Remove(connection);
        response.PrincipalId = issued.PrincipalId; response.SessionId = issued.SessionId;
        response.ExpiresInMilliseconds = Math.Max(1, (long)(issued.ExpiresAt - DateTimeOffset.UtcNow).TotalMilliseconds);
        if (previous != null) facade.ReplaceSession(connection, previous, issued.SessionId);
    }
    public void Dispose()
    {
        lock (gate) { if (stopped) return; stopped = true; active.Clear(); principals.Clear(); changingCredential.Clear(); budgets.Clear(); }
        transport.OnConnectionEstablished -= Connected; transport.OnConnectionClosed -= Closed; sessions.Invalidated -= Invalidated;
        transport.UnregisterPacketHandler(IdentityWire.Module); sessions.Dispose();
    }
}
