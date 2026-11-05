using System;
using Authentication;

namespace UserManagement;

// All historical credential-to-player rules stay at this boundary, never in the modern identity authority.
public sealed class LegacyIdentityMappingAdapter
{
    private readonly ServerAuthenticator auth;
    private readonly ServerUserManager users;
    public LegacyIdentityMappingAdapter(ServerAuthenticator auth, ServerUserManager users) { this.auth = auth; this.users = users; }
    public string ResolveSession(string connection, string session, string displayName, bool create)
    {
        if (!auth.IsAuthenticated(connection, session) || !auth.TryGetUsername(connection, session, out var key)) return null;
        if (users.TryGetUserUuid(key, out var principal)) return principal;
        return create ? users.CreateUser(key, displayName, true) : null;
    }
    public string ResolveProof(string proof)
    {
        if (string.IsNullOrEmpty(proof) || proof.Length > 256) throw new ArgumentException("Invalid previous identity proof.");
        bool known = auth.HasExistingClientKey(proof), mapped = users.TryGetUserUuid(proof, out var principal);
        if (known != mapped) throw new InvalidOperationException("Previous identity mapping is incomplete.");
        return known ? principal : null;
    }
}
