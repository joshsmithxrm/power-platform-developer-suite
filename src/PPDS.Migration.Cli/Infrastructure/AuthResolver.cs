using Microsoft.Extensions.Configuration;

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
    /// Alternative prefix with PPDS namespace.
    /// </summary>
    public const string AltEnvVarPrefix = "PPDS__DATAVERSE__";

    /// <summary>
    /// Result of authentication resolution.
    /// </summary>
    public record AuthResult(
        AuthMode Mode,
        string? Url,
        string? ClientId,
        string? ClientSecret,
        string? TenantId,
        string? EnvironmentName);

    /// <summary>
    /// Resolves authentication based on the specified mode.
    /// </summary>
    /// <param name="mode">The authentication mode to use.</param>
    /// <param name="url">Direct URL from --url option (highest priority).</param>
    /// <param name="environmentName">Environment name for config-based auth.</param>
    /// <param name="configuration">Configuration (for Config mode).</param>
    /// <returns>The resolved auth configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown when auth cannot be resolved.</exception>
    public static AuthResult Resolve(
        AuthMode mode,
        string? url,
        string? environmentName,
        IConfiguration? configuration)
    {
        return mode switch
        {
            AuthMode.Config => ResolveFromConfig(environmentName, configuration),
            AuthMode.Env => ResolveFromEnvironmentVariables(),
            AuthMode.Interactive => ResolveForInteractive(url, environmentName, configuration),
            AuthMode.Managed => ResolveForManagedIdentity(url, environmentName, configuration),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown auth mode")
        };
    }

    /// <summary>
    /// Resolves auth from direct environment variables (not config-based).
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
            tenantId,
            EnvironmentName: null);
    }

    /// <summary>
    /// Resolves auth from configuration files (appsettings.json + User Secrets).
    /// </summary>
    private static AuthResult ResolveFromConfig(string? environmentName, IConfiguration? configuration)
    {
        if (string.IsNullOrEmpty(environmentName))
        {
            throw new InvalidOperationException(
                "Environment name is required when using --auth config. " +
                "Use --env <name> to specify which environment to use.");
        }

        if (configuration == null)
        {
            throw new InvalidOperationException(
                "Configuration is required when using --auth config. " +
                "Ensure appsettings.json exists or provide --config path.");
        }

        // Delegate to existing config-based resolution
        // The actual resolution happens in ConnectionResolver.ResolveFromConfig
        // We just return a marker result here indicating config mode
        return new AuthResult(
            AuthMode.Config,
            Url: null, // Will be resolved by ConnectionResolver
            ClientId: null,
            ClientSecret: null,
            TenantId: null,
            EnvironmentName: environmentName);
    }

    /// <summary>
    /// Resolves configuration for interactive (device code) auth.
    /// Only URL is needed; auth happens interactively.
    /// Priority: --url option > --env + config > DATAVERSE__URL env var.
    /// </summary>
    private static AuthResult ResolveForInteractive(string? url, string? environmentName, IConfiguration? configuration)
    {
        // 1. Try --url option first (highest priority)
        if (!string.IsNullOrEmpty(url))
        {
            return new AuthResult(
                AuthMode.Interactive,
                url,
                ClientId: null,
                ClientSecret: null,
                TenantId: null,
                EnvironmentName: null);
        }

        // 2. Try --env + config
        if (!string.IsNullOrEmpty(environmentName) && configuration != null)
        {
            var configUrl = configuration[$"Dataverse:Environments:{environmentName}:Url"]
                            ?? configuration["Dataverse:Url"];
            if (!string.IsNullOrEmpty(configUrl))
            {
                return new AuthResult(
                    AuthMode.Interactive,
                    configUrl,
                    ClientId: null,
                    ClientSecret: null,
                    TenantId: null,
                    EnvironmentName: environmentName);
            }
        }

        // 3. Try DATAVERSE__URL env var
        var envUrl = GetEnvVar("URL");
        if (!string.IsNullOrEmpty(envUrl))
        {
            return new AuthResult(
                AuthMode.Interactive,
                envUrl,
                ClientId: null,
                ClientSecret: null,
                TenantId: null,
                EnvironmentName: null);
        }

        throw new InvalidOperationException(
            "Dataverse URL required. Provide --url, --env with config, or set DATAVERSE__URL environment variable.");
    }

    /// <summary>
    /// Resolves configuration for managed identity auth.
    /// Only URL is needed; identity comes from Azure.
    /// Priority: --url option > --env + config > DATAVERSE__URL env var.
    /// </summary>
    private static AuthResult ResolveForManagedIdentity(string? url, string? environmentName, IConfiguration? configuration)
    {
        // 1. Try --url option first (highest priority)
        if (!string.IsNullOrEmpty(url))
        {
            return new AuthResult(
                AuthMode.Managed,
                url,
                ClientId: null,
                ClientSecret: null,
                TenantId: null,
                EnvironmentName: null);
        }

        // 2. Try --env + config
        if (!string.IsNullOrEmpty(environmentName) && configuration != null)
        {
            var configUrl = configuration[$"Dataverse:Environments:{environmentName}:Url"]
                            ?? configuration["Dataverse:Url"];
            if (!string.IsNullOrEmpty(configUrl))
            {
                return new AuthResult(
                    AuthMode.Managed,
                    configUrl,
                    ClientId: null,
                    ClientSecret: null,
                    TenantId: null,
                    EnvironmentName: environmentName);
            }
        }

        // 3. Try DATAVERSE__URL env var
        var envUrl = GetEnvVar("URL");
        if (!string.IsNullOrEmpty(envUrl))
        {
            return new AuthResult(
                AuthMode.Managed,
                envUrl,
                ClientId: null,
                ClientSecret: null,
                TenantId: null,
                EnvironmentName: null);
        }

        throw new InvalidOperationException(
            "Dataverse URL required. Provide --url, --env with config, or set DATAVERSE__URL environment variable.");
    }

    /// <summary>
    /// Checks if direct environment variables are set (for --auth env mode).
    /// </summary>
    public static bool HasDirectEnvVars()
    {
        return !string.IsNullOrEmpty(GetEnvVar("URL"))
               && !string.IsNullOrEmpty(GetEnvVar("CLIENTID"))
               && !string.IsNullOrEmpty(GetEnvVar("CLIENTSECRET"));
    }

    /// <summary>
    /// Gets an environment variable with either DATAVERSE__ or PPDS__DATAVERSE__ prefix.
    /// </summary>
    private static string? GetEnvVar(string name)
    {
        // Try primary prefix first
        var value = Environment.GetEnvironmentVariable($"{EnvVarPrefix}{name}");
        if (!string.IsNullOrEmpty(value))
            return value;

        // Try alternative prefix
        return Environment.GetEnvironmentVariable($"{AltEnvVarPrefix}{name}");
    }

    /// <summary>
    /// Gets a helpful message about auth configuration.
    /// </summary>
    public static string GetAuthHelpMessage(AuthMode mode)
    {
        return mode switch
        {
            AuthMode.Config => "Configure via appsettings.json + User Secrets (--secrets-id). Requires --env.",
            AuthMode.Env => "Set DATAVERSE__URL, DATAVERSE__CLIENTID, and DATAVERSE__CLIENTSECRET environment variables.",
            AuthMode.Interactive => "Opens browser for device code authentication. Provide --url, --env, or set DATAVERSE__URL.",
            AuthMode.Managed => "Uses Azure Managed Identity. Provide --url, --env, or set DATAVERSE__URL. Works in Azure VMs, App Service, AKS.",
            _ => "Unknown auth mode."
        };
    }
}
