namespace PPDS.Migration.Cli.Infrastructure;

/// <summary>
/// Resolves authentication configuration based on the specified auth mode.
/// </summary>
public static class AuthResolver
{
    /// <summary>
    /// Environment variable prefix for Dataverse configuration.
    /// </summary>
    public const string EnvVarPrefix = "DATAVERSE__";

    /// <summary>
    /// Result of authentication resolution.
    /// </summary>
    public record AuthResult(
        AuthMode Mode,
        string Url,
        string? ClientId = null,
        string? ClientSecret = null,
        string? TenantId = null);

    /// <summary>
    /// Resolves authentication based on the specified mode.
    /// </summary>
    /// <param name="mode">The authentication mode to use.</param>
    /// <param name="url">Direct URL from --url option.</param>
    /// <returns>The resolved auth configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown when auth cannot be resolved.</exception>
    public static AuthResult Resolve(AuthMode mode, string? url)
    {
        return mode switch
        {
            AuthMode.Env => ResolveFromEnvironmentVariables(),
            AuthMode.Interactive => ResolveForInteractive(url),
            AuthMode.Managed => ResolveForManagedIdentity(url),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown auth mode")
        };
    }

    /// <summary>
    /// Resolves auth from environment variables.
    /// </summary>
    private static AuthResult ResolveFromEnvironmentVariables()
    {
        var url = GetEnvVar("URL");
        var clientId = GetEnvVar("CLIENTID");
        var clientSecret = GetEnvVar("CLIENTSECRET");
        var tenantId = GetEnvVar("TENANTID");

        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException(
                "DATAVERSE__URL environment variable is required when using --auth env. " +
                "Set it to your Dataverse environment URL (e.g., https://org.crm.dynamics.com).");
        }

        if (string.IsNullOrEmpty(clientId))
        {
            throw new InvalidOperationException(
                "DATAVERSE__CLIENTID environment variable is required when using --auth env. " +
                "Set it to your Azure AD application (client) ID.");
        }

        if (string.IsNullOrEmpty(clientSecret))
        {
            throw new InvalidOperationException(
                "DATAVERSE__CLIENTSECRET environment variable is required when using --auth env. " +
                "Set it to your Azure AD client secret.");
        }

        return new AuthResult(
            AuthMode.Env,
            url,
            clientId,
            clientSecret,
            tenantId);
    }

    /// <summary>
    /// Resolves configuration for interactive (device code) auth.
    /// Only URL is needed; auth happens interactively.
    /// </summary>
    private static AuthResult ResolveForInteractive(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException(
                "--url is required for interactive authentication. " +
                "Example: --url https://myorg.crm.dynamics.com");
        }

        return new AuthResult(AuthMode.Interactive, url);
    }

    /// <summary>
    /// Resolves configuration for managed identity auth.
    /// Only URL is needed; identity comes from Azure.
    /// </summary>
    private static AuthResult ResolveForManagedIdentity(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException(
                "--url is required for managed identity authentication. " +
                "Example: --url https://myorg.crm.dynamics.com");
        }

        return new AuthResult(AuthMode.Managed, url);
    }

    /// <summary>
    /// Gets an environment variable with DATAVERSE__ prefix.
    /// </summary>
    private static string? GetEnvVar(string name)
    {
        return Environment.GetEnvironmentVariable($"{EnvVarPrefix}{name}");
    }

    /// <summary>
    /// Gets a helpful message about auth configuration.
    /// </summary>
    public static string GetAuthHelpMessage(AuthMode mode)
    {
        return mode switch
        {
            AuthMode.Env => "Uses DATAVERSE__URL, DATAVERSE__CLIENTID, and DATAVERSE__CLIENTSECRET environment variables.",
            AuthMode.Interactive => "Opens browser for device code authentication. Requires --url.",
            AuthMode.Managed => "Uses Azure Managed Identity. Works in Azure VMs, App Service, AKS. Requires --url.",
            _ => "Unknown auth mode."
        };
    }
}
