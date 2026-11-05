namespace ServerRuntime
{
    // Neutral proof of the currently authenticated, logged-in principal. No credential material.
    public interface IServerPrincipalAuthorization
    {
        bool IsAuthenticatedPrincipal(string connectionId, string sessionId, string principalId);
    }

    // Trusted local lookup for login-event consumers. No keys or credential mapping.
    public interface IServerAuthenticatedSessionLookup
    {
        bool TryGetAuthenticatedSession(string connectionId, string principalId, out string sessionId);
    }
}
