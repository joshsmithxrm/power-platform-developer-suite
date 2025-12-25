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
    /// <param name="debug">Enable debug logging output to console.</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProvider(
        ConnectionResolver.ConnectionConfig config,
        string connectionName = "Primary",
        bool debug = false)
    {
        return CreateProvider(config.Url, config.ClientId, config.ClientSecret, config.TenantId, connectionName, debug);
    }

    /// <summary>
    /// Creates a service provider configured with a single Dataverse connection.
    /// </summary>
    /// <param name="url">The Dataverse environment URL.</param>
    /// <param name="clientId">The Azure AD application (client) ID.</param>
    /// <param name="clientSecret">The client secret value.</param>
    /// <param name="tenantId">Optional Azure AD tenant ID.</param>
    /// <param name="connectionName">Optional name for the connection. Default: "Primary"</param>
    /// <param name="debug">Enable debug logging output to console.</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProvider(
        string url,
        string clientId,
        string clientSecret,
        string? tenantId = null,
        string connectionName = "Primary",
        bool debug = false)
    {
        var services = new ServiceCollection();

        // Add logging - console output only when debug mode is enabled
        // This keeps CLI output clean; progress reporter handles user-facing output
        services.AddLogging(builder =>
        {
            if (debug)
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "[HH:mm:ss] ";
                });
            }
            else
            {
                // Suppress all console output - progress reporter handles user output
                builder.SetMinimumLevel(LogLevel.None);
            }
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
            // CLI is short-lived; don't eagerly create connections (avoids silent hangs on auth issues)
            options.Pool.MinPoolSize = 0;
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
    /// <param name="debug">Enable debug logging output to console.</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProviderFromConfig(
        IConfiguration configuration,
        string environmentName,
        bool debug = false)
    {
        var services = new ServiceCollection();

        // Add logging - console output only when debug mode is enabled
        // This keeps CLI output clean; progress reporter handles user-facing output
        services.AddLogging(builder =>
        {
            if (debug)
            {
                builder.SetMinimumLevel(LogLevel.Debug);
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "[HH:mm:ss] ";
                });
            }
            else
            {
                // Suppress all console output - progress reporter handles user output
                builder.SetMinimumLevel(LogLevel.None);
            }
        });

        // Use SDK's config-based overload with environment selection
        services.AddDataverseConnectionPool(configuration, environment: environmentName);

        // Override pool settings for CLI usage (high parallelism, no affinity cookie)
        services.Configure<DataverseOptions>(options =>
        {
            options.Pool.Enabled = true;
            // CLI is short-lived; don't eagerly create connections (avoids silent hangs on auth issues)
            options.Pool.MinPoolSize = 0;
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
