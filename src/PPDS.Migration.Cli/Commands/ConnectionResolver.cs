using Microsoft.Extensions.Configuration;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;

namespace PPDS.Migration.Cli.Commands;

/// <summary>
/// Resolves connection configuration from standard .NET configuration sources.
/// </summary>
/// <remarks>
/// Uses the same DataverseOptions model as the PPDS.Dataverse SDK.
/// Configuration is loaded from appsettings.json, User Secrets, and environment variables.
/// </remarks>
public static class ConnectionResolver
{
    /// <summary>
    /// Connection configuration resolved from configuration.
    /// </summary>
    public record ConnectionConfig(string Url, string ClientId, string ClientSecret, string? TenantId);

    /// <summary>
    /// Indicates where the connection was resolved from.
    /// </summary>
    public enum ConnectionSource
    {
        /// <summary>Connection resolved from configuration (appsettings.json, User Secrets, or environment variables).</summary>
        Configuration
    }

    /// <summary>
    /// Result of connection resolution with source information.
    /// </summary>
    /// <param name="Config">The resolved connection configuration.</param>
    /// <param name="Source">Where the connection was resolved from.</param>
    /// <param name="EnvironmentName">The environment name if resolved from named environment.</param>
    public record ResolvedConnection(
        ConnectionConfig Config,
        ConnectionSource Source,
        string? EnvironmentName = null);

    /// <summary>
    /// Resolves connection from configuration using the specified environment.
    /// </summary>
    /// <param name="environmentName">The environment name (e.g., Dev, QA, Prod). Required.</param>
    /// <param name="configPath">The config file path from --config option.</param>
    /// <param name="secretsId">User Secrets ID for cross-process secret sharing.</param>
    /// <param name="connectionName">A friendly name for error messages.</param>
    /// <returns>Resolved connection with source information.</returns>
    /// <exception cref="InvalidOperationException">Thrown when connection cannot be resolved.</exception>
    /// <exception cref="FileNotFoundException">Thrown when config file is not found.</exception>
    public static ResolvedConnection Resolve(
        string environmentName,
        string? configPath,
        string? secretsId,
        string connectionName = "connection")
    {
        if (string.IsNullOrEmpty(environmentName))
        {
            throw new InvalidOperationException(
                $"Environment name is required for {connectionName}. " +
                "Use --env <name> to specify which environment to use.");
        }

        var configuration = ConfigurationHelper.BuildRequired(configPath, secretsId);
        return ResolveFromConfig(configuration, environmentName, connectionName);
    }

    /// <summary>
    /// Resolves source/target connections for migration from configuration.
    /// </summary>
    /// <param name="sourceEnv">Source environment name (e.g., Dev). Required.</param>
    /// <param name="targetEnv">Target environment name (e.g., Prod). Required.</param>
    /// <param name="configPath">The config file path from --config option.</param>
    /// <param name="secretsId">User Secrets ID for cross-process secret sharing.</param>
    /// <returns>Tuple of source and target resolved connections.</returns>
    /// <exception cref="InvalidOperationException">Thrown when validation fails or connection cannot be resolved.</exception>
    public static (ResolvedConnection Source, ResolvedConnection Target) ResolveSourceTarget(
        string? sourceEnv,
        string? targetEnv,
        string? configPath,
        string? secretsId)
    {
        if (string.IsNullOrEmpty(sourceEnv) || string.IsNullOrEmpty(targetEnv))
        {
            throw new InvalidOperationException(
                "Both --source-env and --target-env are required. " +
                "Example: --source-env Dev --target-env Prod");
        }

        var configuration = ConfigurationHelper.BuildRequired(configPath, secretsId);
        var source = ResolveFromConfig(configuration, sourceEnv, "source");
        var target = ResolveFromConfig(configuration, targetEnv, "target");
        return (source, target);
    }

    /// <summary>
    /// Resolves connection from configuration using the specified environment.
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
                "Check your appsettings.json Dataverse:Environments section, or use environment variables (Dataverse__Environments__Dev__Url, etc.).");
        }

        var env = EnvironmentResolver.GetEnvironment(options, environmentName);

        // Validate the environment has connections
        if (!env.HasConnections)
        {
            throw new InvalidOperationException(
                $"Environment '{environmentName}' has no connections configured for {connectionName}. " +
                $"Add a connection in Dataverse:Environments:{environmentName}:Connections or via environment variables.");
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
                $"Set Dataverse:Environments:{environmentName}:Url in config, User Secrets, or environment variables.");
        }

        if (string.IsNullOrEmpty(conn.ClientId))
        {
            throw new InvalidOperationException(
                $"No ClientId configured for environment '{environmentName}' ({connectionName}). " +
                $"Set Dataverse:Environments:{environmentName}:Connections:0:ClientId in config, User Secrets, or environment variables.");
        }

        if (string.IsNullOrEmpty(conn.ClientSecret))
        {
            throw new InvalidOperationException(
                $"No ClientSecret configured for environment '{environmentName}' ({connectionName}). " +
                $"Set Dataverse:Environments:{environmentName}:Connections:0:ClientSecret in config, User Secrets, or environment variables.");
        }

        return new ResolvedConnection(
            new ConnectionConfig(url, conn.ClientId, conn.ClientSecret, tenantId),
            ConnectionSource.Configuration,
            environmentName);
    }
}
