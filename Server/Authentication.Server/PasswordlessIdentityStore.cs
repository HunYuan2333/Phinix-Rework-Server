#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServerStorage;

namespace Authentication;

// Storage/service boundary only: no wire DTOs, legacy keys, display names or protocol exceptions.
// Bind/Rotate/Revoke require a caller-authorized principal; the TLS endpoint supplies it after boundary validation.
public sealed class PasswordlessIdentityStore
{
    private const int Schema = 1;
    private readonly SqliteOperationJournal journal;
    public string ServerId { get; }
    private sealed record Header(int Version, string ServerId);
    private sealed record Principal(int Version, string PrincipalId, long Epoch, string? ActiveCredentialId);
    private sealed record Credential(int Version, string PrincipalId, string CredentialId, long Epoch, int VerifierVersion, byte[] Verifier, [property: JsonRequired] bool Revoked);
    public sealed record AuthenticationFacts(string ServerId, string PrincipalId, string CredentialId, long Epoch);
    public sealed record MutationResult(string PrincipalId, string CredentialId, long Epoch, string State);
    private PasswordlessIdentityStore(SqliteOperationJournal journal)
    {
        this.journal = journal;
        var header = Decode<Header>(journal.ReadState("identity.header"));
        if (header == null || header.Version != Schema) throw new InvalidDataException("Identity store header is missing or unsupported.");
        if (!Guid.TryParseExact(header.ServerId, "D", out var serverId)) throw new InvalidDataException("Identity server ID is invalid.");
        ServerId = serverId.ToString("D");
    }
    public static PasswordlessIdentityStore CreateNew(string path)
    {
        var journal = SqliteOperationJournal.CreateNew(path);
        journal.Execute("identity.setup", "create-v1", new byte[] { 1 }, tx => {
            tx.WriteState("identity.header", Encode(new Header(Schema, Guid.NewGuid().ToString("D")))); return new byte[] { 1 };
        });
        return new PasswordlessIdentityStore(journal);
    }
    public static PasswordlessIdentityStore OpenExisting(string path) => new(SqliteOperationJournal.OpenExisting(path));
    private static string Id(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed)) throw new ArgumentException("A UUID identifier is required.");
        return parsed.ToString("D");
    }
    private static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static T? Decode<T>(byte[]? bytes) where T : class
    {
        if (bytes == null) return null;
        try { return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Invalid identity record."); }
        catch (JsonException) { throw new InvalidDataException("Invalid identity record."); }
    }
    private static string PrincipalKey(string id) => "identity.principal/" + Id(id);
    private static string CredentialKey(string id) => "identity.credential/" + Id(id);
    private static Principal Validate(Principal? value, string id)
    {
        if (value == null || value.Version != Schema || !Guid.TryParseExact(value.PrincipalId, "D", out var storedId) || storedId.ToString("D") != Id(id) || value.Epoch < 1 ||
            value.ActiveCredentialId != null && Id(value.ActiveCredentialId) != value.ActiveCredentialId)
            throw new InvalidDataException("Identity principal is missing or invalid.");
        return value;
    }
    private static Credential? Validate(Credential? value, string id)
    {
        if (value == null) return null;
        if (value.Version != Schema || value.VerifierVersion != 1 || value.CredentialId != Id(id) || value.Epoch < 1 ||
            value.Verifier == null || value.Verifier.Length != 32 || !Guid.TryParseExact(value.PrincipalId, "D", out _))
            throw new InvalidDataException("Identity credential is invalid.");
        return value;
    }
    // SHA-256 is a verifier for a CSPRNG 256-bit token, never a human password hash.
    // Domain includes stable server identity and non-secret credential ID, not a mutable display name/address.
    private byte[] Verifier(string credentialId, byte[] secret)
    {
        if (secret == null || secret.Length != 32) throw new ArgumentException("A 32-byte random credential is required.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("Phinix:credential:v1\0" + ServerId + "\0" + Id(credentialId) + "\0"));
        hash.AppendData(secret); return hash.GetHashAndReset();
    }
    private MutationResult Execute(string operationId, object request, Func<JournalTransaction, MutationResult> apply)
    {
        var committed = journal.Execute("identity", Id(operationId), Encode(request), tx => Encode(apply(tx)), maxDatabaseBytes: 64L * 1024 * 1024);
        var result = Decode<MutationResult>(committed.Result);
        if (result == null || !Guid.TryParseExact(result.PrincipalId, "D", out _) || !Guid.TryParseExact(result.CredentialId, "D", out _) ||
            result.Epoch < 1 || result.State is not ("Active" or "Rotated" or "Revoked"))
            throw new InvalidDataException("Identity operation result is missing or invalid.");
        return result;
    }
    private static void Commit(JournalTransaction tx, string operationId, Principal principal, Credential credential, string state)
    {
        tx.WriteState(PrincipalKey(principal.PrincipalId), Encode(principal));
        tx.WriteState(CredentialKey(credential.CredentialId), Encode(credential));
        // Neutral durable invalidation evidence; session dispatcher integration is a later slice.
        tx.Enqueue("identity/" + Id(operationId), principal.PrincipalId,
            Encode(new MutationResult(principal.PrincipalId, credential.CredentialId, principal.Epoch, state)));
    }
    public MutationResult Register(string operationId, string credentialId, byte[] secret)
    {
        credentialId = Id(credentialId); byte[] verifier = Verifier(credentialId, secret);
        return Execute(operationId, new { Kind = "register", CredentialId = credentialId, Verifier = verifier }, tx => {
            if (tx.ReadState(CredentialKey(credentialId)) != null) throw new InvalidOperationException("Credential ID is already bound.");
            string principalId = Guid.NewGuid().ToString("D");
            if (tx.ReadState(PrincipalKey(principalId)) != null) throw new InvalidOperationException("Principal ID collision.");
            var principal = new Principal(Schema, principalId, 1, credentialId);
            var credential = new Credential(Schema, principalId, credentialId, 1, 1, verifier, false);
            Commit(tx, operationId, principal, credential, "Active");
            return new MutationResult(principalId, credentialId, 1, "Active");
        });
    }
    public MutationResult BindAuthorizedPrincipal(string operationId, string principalId, string credentialId, byte[] secret)
    {
        string canonicalPrincipal = Id(principalId); credentialId = Id(credentialId); byte[] verifier = Verifier(credentialId, secret);
        return Execute(operationId, new { Kind = "bind", PrincipalId = canonicalPrincipal, CredentialId = credentialId, Verifier = verifier }, tx => {
            if (tx.ReadState(PrincipalKey(principalId)) != null || tx.ReadState(CredentialKey(credentialId)) != null)
                throw new InvalidOperationException("Principal or credential is already bound.");
            var principal = new Principal(Schema, principalId, 1, credentialId); // Retain the original player's exact UUID string.
            var credential = new Credential(Schema, principalId, credentialId, 1, 1, verifier, false);
            Commit(tx, operationId, principal, credential, "Active");
            return new MutationResult(principalId, credentialId, 1, "Active");
        });
    }
    public MutationResult RotateAuthorizedCredential(string operationId, string principalId, string oldCredentialId, string newCredentialId, byte[] newSecret)
    {
        string canonicalPrincipal = Id(principalId); oldCredentialId = Id(oldCredentialId); newCredentialId = Id(newCredentialId);
        byte[] verifier = Verifier(newCredentialId, newSecret);
        return Execute(operationId, new { Kind = "rotate", PrincipalId = canonicalPrincipal, OldCredentialId = oldCredentialId, NewCredentialId = newCredentialId, Verifier = verifier }, tx => {
            var principal = Validate(Decode<Principal>(tx.ReadState(PrincipalKey(principalId))), principalId);
            var old = Validate(Decode<Credential>(tx.ReadState(CredentialKey(oldCredentialId))), oldCredentialId);
            if (old == null || old.Revoked || Id(old.PrincipalId) != canonicalPrincipal || principal.ActiveCredentialId != oldCredentialId ||
                oldCredentialId == newCredentialId || tx.ReadState(CredentialKey(newCredentialId)) != null)
                throw new InvalidOperationException("Credential rotation was refused.");
            long epoch = checked(principal.Epoch + 1);
            var next = new Credential(Schema, principal.PrincipalId, newCredentialId, epoch, 1, verifier, false);
            tx.WriteState(CredentialKey(oldCredentialId), Encode(old with { Revoked = true }));
            Commit(tx, operationId, principal with { Epoch = epoch, ActiveCredentialId = newCredentialId }, next, "Rotated");
            return new MutationResult(principal.PrincipalId, newCredentialId, epoch, "Rotated");
        });
    }
    public MutationResult RevokeAuthorizedCredential(string operationId, string principalId, string credentialId)
    {
        string canonicalPrincipal = Id(principalId); credentialId = Id(credentialId);
        return Execute(operationId, new { Kind = "revoke", PrincipalId = canonicalPrincipal, CredentialId = credentialId }, tx => {
            var principal = Validate(Decode<Principal>(tx.ReadState(PrincipalKey(principalId))), principalId);
            var credential = Validate(Decode<Credential>(tx.ReadState(CredentialKey(credentialId))), credentialId);
            if (credential == null || Id(credential.PrincipalId) != canonicalPrincipal) throw new InvalidOperationException("Credential revocation was refused.");
            if (credential.Revoked) return new MutationResult(principal.PrincipalId, credentialId, principal.Epoch, "Revoked");
            if (principal.ActiveCredentialId != credentialId) throw new InvalidDataException("Identity active credential is inconsistent.");
            long epoch = checked(principal.Epoch + 1);
            Commit(tx, operationId, principal with { Epoch = epoch, ActiveCredentialId = null }, credential with { Revoked = true }, "Revoked");
            return new MutationResult(principal.PrincipalId, credentialId, epoch, "Revoked");
        });
    }
    // Read-only proof for the exact original revocation operation. Never grants a session or restores a credential.
    public MutationResult? QueryOwnRevocation(string operationId, string credentialId, byte[] secret)
    {
        credentialId = Id(credentialId); operationId = Id(operationId);
        var credential = Validate(Decode<Credential>(journal.ReadState(CredentialKey(credentialId))), credentialId);
        byte[] presented = Verifier(credentialId, secret);
        try
        {
            if (credential == null || !CryptographicOperations.FixedTimeEquals(presented, credential.Verifier))
                throw new InvalidOperationException("Credential proof was refused.");
        }
        finally { CryptographicOperations.ZeroMemory(presented); }
        var committed = journal.TryReadCommitted("identity", operationId, Encode(new { Kind = "revoke", PrincipalId = Id(credential.PrincipalId), CredentialId = credentialId }));
        if (committed == null) return null;
        var result = Decode<MutationResult>(committed.Result);
        if (result == null || result.CredentialId != credentialId || !Guid.TryParseExact(result.PrincipalId, "D", out var resultPrincipal) || resultPrincipal.ToString("D") != Id(credential.PrincipalId) || result.State != "Revoked" || !credential.Revoked || result.Epoch < 1)
            throw new InvalidDataException("Revocation result is inconsistent.");
        return result;
    }
    public AuthenticationFacts? Authenticate(string credentialId, byte[] secret)
    {
        credentialId = Id(credentialId); byte[] presented = Verifier(credentialId, secret);
        var credential = Validate(Decode<Credential>(journal.ReadState(CredentialKey(credentialId))), credentialId);
        if (credential == null || credential.Revoked || !CryptographicOperations.FixedTimeEquals(presented, credential.Verifier)) return null;
        var facts = new AuthenticationFacts(ServerId, credential.PrincipalId, credentialId, credential.Epoch);
        return IsCurrent(facts) ? facts : null;
    }
    public bool IsCurrent(AuthenticationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.ServerId != ServerId) return false;
        var principal = Validate(Decode<Principal>(journal.ReadState(PrincipalKey(facts.PrincipalId))), facts.PrincipalId);
        return principal.Epoch == facts.Epoch && principal.ActiveCredentialId == Id(facts.CredentialId);
    }
}
