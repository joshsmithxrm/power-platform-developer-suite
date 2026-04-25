namespace PPDS.Auth;

/// <summary>
/// Hierarchical error codes used by PPDS.Auth's <see cref="Credentials.AuthenticationException"/>.
/// Values are stable strings consumers (CLI, MCP, RPC) can match programmatically.
/// </summary>
public static class AuthErrorCodes
{
    /// <summary>BAP API returned 403 — SPN is not registered as a Power Platform management application.</summary>
    public const string BapApiForbidden = "Auth.BapApiForbidden";

    /// <summary>BAP API returned 401 — token is invalid or expired.</summary>
    public const string BapApiUnauthorized = "Auth.BapApiUnauthorized";

    /// <summary>BAP API call timed out.</summary>
    public const string BapApiTimeout = "Auth.BapApiTimeout";

    /// <summary>BAP API returned 429/5xx or another unexpected status.</summary>
    public const string BapApiError = "Auth.BapApiError";

    /// <summary>Environment name was not found in BAP discovery results.</summary>
    public const string EnvironmentNotFound = "Auth.EnvironmentNotFound";
}
