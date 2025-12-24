using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Analysis;
using PPDS.Migration.Cli.Commands;
using PPDS.Migration.DependencyInjection;
using PPDS.Migration.Export;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Cli.Infrastructure;

/// <summary>
/// Factory for creating configured service providers for CLI commands.
/// </summary>
public static class ServiceFactory
{
    /// <summary>
    /// Creates a service provider from resolved connection configuration.
    /// </summary>
    /// <param name="config">The connection configuration resolved from environment variables.</param>
    /// <param name="connectionName">Optional name for the connection. Default: "Primary"</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProvider(
        ConnectionResolver.ConnectionConfig config,
        string connectionName = "Primary")
    {
        return CreateProvider(config.Url, config.ClientId, config.ClientSecret, config.TenantId, connectionName);
    }

    /// <summary>
    /// Creates a service provider configured with a single Dataverse connection.
    /// </summary>
    /// <param name="url">The Dataverse environment URL.</param>
    /// <param name="clientId">The Azure AD application (client) ID.</param>
    /// <param name="clientSecret">The client secret value.</param>
    /// <param name="tenantId">Optional Azure AD tenant ID.</param>
    /// <param name="connectionName">Optional name for the connection. Default: "Primary"</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProvider(
        string url,
        string clientId,
        string clientSecret,
        string? tenantId = null,
        string connectionName = "Primary")
    {
        var services = new ServiceCollection();

        // Add logging (minimal for CLI - no console output to avoid interfering with CLI)
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // Add Dataverse connection pool
        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection(connectionName)
            {
                Url = url,
                ClientId = clientId,
                ClientSecret = clientSecret,
                TenantId = tenantId,
                AuthType = DataverseAuthType.ClientSecret
            });
            options.Pool.Enabled = true;
            options.Pool.MaxConnectionsPerUser = Math.Max(Environment.ProcessorCount * 4, 16);
            options.Pool.DisableAffinityCookie = true;
        });

        // Add migration services
        services.AddDataverseMigration();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a service provider from configuration file using the specified environment.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="environmentName">The environment name to use.</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProviderFromConfig(
        IConfiguration configuration,
        string environmentName)
    {
        var services = new ServiceCollection();

        // Add logging (minimal for CLI - no console output to avoid interfering with CLI)
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // Use SDK's config-based overload with environment selection
        services.AddDataverseConnectionPool(configuration, environment: environmentName);

        // Override pool settings for CLI usage (high parallelism, no affinity cookie)
        services.Configure<DataverseOptions>(options =>
        {
            options.Pool.Enabled = true;
            options.Pool.MaxConnectionsPerUser = Math.Max(Environment.ProcessorCount * 4, 16);
            options.Pool.DisableAffinityCookie = true;
        });

        // Add migration services
        services.AddDataverseMigration();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a service provider for schema analysis (no connection required).
    /// </summary>
    /// <returns>A configured service provider with schema reading capabilities.</returns>
    public static ServiceProvider CreateAnalysisProvider()
    {
        var services = new ServiceCollection();

        // Add logging (minimal for CLI)
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // Register only the analysis components (no Dataverse connection needed)
        services.AddTransient<ICmtSchemaReader, CmtSchemaReader>();
        services.AddTransient<IDependencyGraphBuilder, DependencyGraphBuilder>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a progress reporter based on the output mode.
    /// </summary>
    /// <param name="useJson">Whether to output JSON format.</param>
    /// <returns>An appropriate progress reporter.</returns>
    public static IProgressReporter CreateProgressReporter(bool useJson)
    {
        return useJson
            ? new JsonProgressReporter(Console.Out)
            : new ConsoleProgressReporter();
    }
}
