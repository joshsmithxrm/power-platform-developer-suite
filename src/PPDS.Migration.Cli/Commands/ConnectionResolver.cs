using Microsoft.Extensions.Configuration;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;

namespace PPDS.Migration.Cli.Commands;

/// <summary>
/// Resolves connection configuration from command-line arguments or environment variables.
/// Uses typed configuration properties instead of connection strings.
/// </summary>
public static class ConnectionResolver
{
    /// <summary>
    /// Environment variable name for the Dataverse URL.
    /// </summary>
    public const string UrlEnvVar = "PPDS_URL";

    /// <summary>
    /// Environment variable name for the client ID.
    /// </summary>
    public const string ClientIdEnvVar = "PPDS_CLIENT_ID";

    /// <summary>
    /// Environment variable name for the client secret.
    /// </summary>
    public const string ClientSecretEnvVar = "PPDS_CLIENT_SECRET";

    /// <summary>
    /// Environment variable name for the tenant ID.
    /// </summary>
    public const string TenantIdEnvVar = "PPDS_TENANT_ID";

    /// <summary>
    /// Environment variable prefix for source environment.
    /// </summary>
    public const string SourcePrefix = "PPDS_SOURCE_";

    /// <summary>
    /// Environment variable prefix for target environment.
    /// </summary>
    public const string TargetPrefix = "PPDS_TARGET_";

    /// <summary>
    /// Connection configuration resolved from environment or arguments.
    /// </summary>
    public record ConnectionConfig(string Url, string ClientId, string ClientSecret, string? TenantId);

    /// <summary>
    /// Resolves connection configuration from environment variables.
    /// </summary>
    /// <param name="prefix">Optional prefix for environment variable names (e.g., "PPDS_SOURCE_").</param>
    /// <param name="connectionName">A friendly name for error messages.</param>
    /// <returns>The resolved connection configuration.</returns>
    public static ConnectionConfig Resolve(string? prefix = null, string connectionName = "connection")
    {
        var urlVar = string.IsNullOrEmpty(prefix) ? UrlEnvVar : $"{prefix}URL";
        var clientIdVar = string.IsNullOrEmpty(prefix) ? ClientIdEnvVar : $"{prefix}CLIENT_ID";
        var secretVar = string.IsNullOrEmpty(prefix) ? ClientSecretEnvVar : $"{prefix}CLIENT_SECRET";
        var tenantVar = string.IsNullOrEmpty(prefix) ? TenantIdEnvVar : $"{prefix}TENANT_ID";

        var url = Environment.GetEnvironmentVariable(urlVar);
        var clientId = Environment.GetEnvironmentVariable(clientIdVar);
        var clientSecret = Environment.GetEnvironmentVariable(secretVar);
        var tenantId = Environment.GetEnvironmentVariable(tenantVar);

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                $"No {connectionName} URL provided. Set the {urlVar} environment variable.");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                $"No {connectionName} client ID provided. Set the {clientIdVar} environment variable.");
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                $"No {connectionName} client secret provided. Set the {secretVar} environment variable.");
        }

        return new ConnectionConfig(url, clientId, clientSecret, tenantId);
    }

    /// <summary>
    /// Gets a description of required environment variables for help text.
    /// </summary>
    public static string GetHelpDescription()
    {
        return $"Connection configured via environment variables: {UrlEnvVar}, {ClientIdEnvVar}, {ClientSecretEnvVar}, and optionally {TenantIdEnvVar}.";
    }

    /// <summary>
    /// Gets a description of required environment variables for source connection.
    /// </summary>
    public static string GetSourceHelpDescription()
    {
        return $"Source connection configured via: {SourcePrefix}URL, {SourcePrefix}CLIENT_ID, {SourcePrefix}CLIENT_SECRET, {SourcePrefix}TENANT_ID";
    }

    /// <summary>
    /// Gets a description of required environment variables for target connection.
    /// </summary>
    public static string GetTargetHelpDescription()
    {
        return $"Target connection configured via: {TargetPrefix}URL, {TargetPrefix}CLIENT_ID, {TargetPrefix}CLIENT_SECRET, {TargetPrefix}TENANT_ID";
    }

    /// <summary>
    /// Gets a description for hybrid connection support (config or env vars).
    /// </summary>
    public static string GetHybridHelpDescription()
    {
        return "Use --env to load from appsettings.json, or set PPDS_URL, PPDS_CLIENT_ID, PPDS_CLIENT_SECRET env vars.";
    }

    /// <summary>
    /// Gets a description for hybrid source/target connection support.
    /// </summary>
    public static string GetHybridSourceTargetHelpDescription()
    {
        return "Use --source-env/--target-env to load from appsettings.json, or set PPDS_SOURCE_*/PPDS_TARGET_* env vars.";
    }

    /// <summary>
    /// Indicates where the connection was resolved from.
    /// </summary>
    public enum ConnectionSource
    {
        /// <summary>Connection resolved from environment variables.</summary>
        EnvironmentVariables,

        /// <summary>Connection resolved from configuration file.</summary>
        ConfigFile
    }

    /// <summary>
    /// Result of connection resolution with source information.
    /// </summary>
    /// <param name="Config">The resolved connection configuration.</param>
    /// <param name="Source">Where the connection was resolved from.</param>
    /// <param name="EnvironmentName">The environment name if resolved from config.</param>
    public record ResolvedConnection(
        ConnectionConfig Config,
        ConnectionSource Source,
        string? EnvironmentName = null);

    /// <summary>
    /// Resolves connection preferring --env config-based pattern, falling back to env vars.
    /// </summary>
    /// <param name="environmentName">The environment name from --env option (null to use env vars).</param>
    /// <param name="configPath">The config file path from --config option.</param>
    /// <param name="connectionName">A friendly name for error messages.</param>
    /// <returns>Resolved connection with source information.</returns>
    /// <exception cref="InvalidOperationException">Thrown when connection cannot be resolved.</exception>
    /// <exception cref="FileNotFoundException">Thrown when config file is not found.</exception>
    public static ResolvedConnection ResolveWithFallback(
        string? environmentName,
        string? configPath,
        string connectionName = "connection")
    {
        // If --env specified, use config-based resolution
        if (!string.IsNullOrEmpty(environmentName))
        {
            var configuration = ConfigurationHelper.Build(configPath);
            return ResolveFromConfig(configuration, environmentName, connectionName);
        }

        // Fall back to environment variables
        var config = Resolve(prefix: null, connectionName);
        return new ResolvedConnection(config, ConnectionSource.EnvironmentVariables);
    }

    /// <summary>
    /// Resolves source/target connections for migration, preferring config-based pattern.
    /// </summary>
    /// <param name="sourceEnv">Source environment name from --source-env option.</param>
    /// <param name="targetEnv">Target environment name from --target-env option.</param>
    /// <param name="configPath">The config file path from --config option.</param>
    /// <returns>Tuple of source and target resolved connections.</returns>
    /// <exception cref="InvalidOperationException">Thrown when validation fails or connection cannot be resolved.</exception>
    public static (ResolvedConnection Source, ResolvedConnection Target) ResolveSourceTargetWithFallback(
        string? sourceEnv,
        string? targetEnv,
        string? configPath)
    {
        // Validate: both must be specified together or neither
        if ((sourceEnv != null) != (targetEnv != null))
        {
            throw new InvalidOperationException(
                "Both --source-env and --target-env must be specified together, or use environment variables.");
        }

        if (sourceEnv != null)
        {
            // Config-based resolution
            var configuration = ConfigurationHelper.Build(configPath);
            var source = ResolveFromConfig(configuration, sourceEnv, "source");
            var target = ResolveFromConfig(configuration, targetEnv!, "target");
            return (source, target);
        }

        // Environment variable resolution
        var sourceConfig = Resolve(SourcePrefix, "source");
        var targetConfig = Resolve(TargetPrefix, "target");
        return (
            new ResolvedConnection(sourceConfig, ConnectionSource.EnvironmentVariables),
            new ResolvedConnection(targetConfig, ConnectionSource.EnvironmentVariables));
    }

    /// <summary>
    /// Resolves connection from configuration file using the specified environment.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="environmentName">The environment name to use.</param>
    /// <param name="connectionName">A friendly name for error messages.</param>
    /// <returns>The resolved connection.</returns>
    /// <exception cref="InvalidOperationException">Thrown when environment or connection is not properly configured.</exception>
    public static ResolvedConnection ResolveFromConfig(
        IConfiguration configuration,
        string environmentName,
        string connectionName)
    {
        // Bind configuration to options
        var options = new DataverseOptions();
        configuration.GetSection("Dataverse").Bind(options);

        // Check if environment exists
        if (!EnvironmentResolver.HasEnvironment(options, environmentName))
        {
            var available = EnvironmentResolver.GetEnvironmentNames(options).ToList();
            var availableList = available.Count > 0 ? string.Join(", ", available) : "(none)";
            throw new InvalidOperationException(
                $"Environment '{environmentName}' not found for {connectionName}. " +
                $"Available environments: {availableList}. " +
                "Check your appsettings.json Dataverse:Environments section.");
        }

        var env = EnvironmentResolver.GetEnvironment(options, environmentName);

        // Validate the environment has connections
        if (!env.HasConnections)
        {
            throw new InvalidOperationException(
                $"Environment '{environmentName}' has no connections configured for {connectionName}.");
        }

        // Get the first connection (CLI uses single connection per environment)
        var conn = env.Connections[0];

        // Apply inheritance (URL/TenantId from environment/root if not set on connection)
        var url = conn.Url ?? env.Url ?? options.Url;
        var tenantId = conn.TenantId ?? env.TenantId ?? options.TenantId;

        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException(
                $"No URL configured for environment '{environmentName}' ({connectionName}). " +
                "Set Dataverse:Environments:{env}:Url or Dataverse:Url in your config file.");
        }

        if (string.IsNullOrEmpty(conn.ClientId))
        {
            throw new InvalidOperationException(
                $"No ClientId configured for environment '{environmentName}' ({connectionName}). " +
                "Set Dataverse:Environments:{env}:Connections:0:ClientId in your config file.");
        }

        if (string.IsNullOrEmpty(conn.ClientSecret))
        {
            throw new InvalidOperationException(
                $"No ClientSecret configured for environment '{environmentName}' ({connectionName}). " +
                "Set Dataverse:Environments:{env}:Connections:0:ClientSecret in your config file.");
        }

        return new ResolvedConnection(
            new ConnectionConfig(url, conn.ClientId, conn.ClientSecret, tenantId),
            ConnectionSource.ConfigFile,
            environmentName);
    }
}
