using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerTransport;

// Bounded bootstrap DTO shared by the modern client/server. Contains secrets: never log or use record ToString.
public sealed class IdentityEnvelope
{
    public int Version { get; set; } = 1;
    public string Kind { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string CredentialId { get; set; } = "";
    public string Secret { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public long ExpiresInMilliseconds { get; set; }
    public string Failure { get; set; } = "";
    // Compatibility proof is consumed exclusively by the identity adapter, never the identity store/session authority.
    public string PreviousProof { get; set; } = "";
    public string NextCredentialId { get; set; } = "";
    public string NextSecret { get; set; } = "";
    public override string ToString() => "Identity envelope (secrets omitted)";
}

public static class IdentityWire
{
    public const string Module = "Identity.v1";
    public const int MaximumBytes = 8192;
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 };
    public static void ValidateRequest(IdentityEnvelope request)
    {
        if (request.Kind is not ("Resolve" or "Enroll" or "Authenticate" or "Renew" or "Rotate" or "Revoke" or "RevocationStatus")) throw new ArgumentException("Unknown identity operation.");
        void Field(string value, params string[] allowed)
        { if (!string.IsNullOrEmpty(value) && !allowed.Contains(request.Kind, StringComparer.Ordinal)) throw new ArgumentException("Identity field is not expressible for this operation."); }
        Field(request.ServerName); Field(request.Failure);
        if (request.ExpiresInMilliseconds != 0) throw new ArgumentException("Response metadata is not a request.");
        Field(request.OperationId, "Enroll", "Rotate", "Revoke", "RevocationStatus");
        Field(request.CredentialId, "Enroll", "Authenticate", "Rotate", "Revoke", "RevocationStatus");
        Field(request.Secret, "Enroll", "Authenticate", "RevocationStatus");
        Field(request.SessionId, "Renew", "Rotate", "Revoke");
        Field(request.PrincipalId, "Enroll");
        Field(request.PreviousProof, "Resolve", "Enroll");
        Field(request.NextCredentialId, "Rotate"); Field(request.NextSecret, "Rotate");
    }
    public static byte[] Encode(IdentityEnvelope value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Identity message exceeds budget.");
        return bytes;
    }
    public static IdentityEnvelope Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("Identity message length is invalid.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Identity envelope must be an object.");
            var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || !names.Contains("Version") || !names.Contains("Kind"))
                throw new InvalidDataException("Identity envelope fields are invalid.");
            var result = JsonSerializer.Deserialize<IdentityEnvelope>(bytes, Options)!;
            if (result.Version != 1 || string.IsNullOrEmpty(result.Kind) || result.Kind.Length > 32 || result.RequestId == null || result.RequestId.Length > 36)
                throw new InvalidDataException("Identity envelope version is unsupported.");
            return result;
        }
        catch (JsonException) { throw new InvalidDataException("Identity envelope format is invalid."); }
    }
}
