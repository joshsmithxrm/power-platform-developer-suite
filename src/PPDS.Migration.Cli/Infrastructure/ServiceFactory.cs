using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using PPDS.Migration.DependencyInjection;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Cli.Infrastructure;

/// <summary>
/// Factory for creating configured service providers for CLI commands.
/// </summary>
public static class ServiceFactory
{
    /// <summary>
    /// Creates a service provider based on the auth result.
    /// </summary>
    /// <param name="authResult">The resolved auth configuration.</param>
    /// <param name="verbose">Enable verbose logging.</param>
    /// <param name="debug">Enable debug logging.</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProviderForAuthMode(
        AuthResolver.AuthResult authResult,
        bool verbose = false,
        bool debug = false)
    {
        return authResult.Mode switch
        {
            AuthMode.Env => CreateProviderFromEnvVars(authResult, verbose, debug),
            AuthMode.Interactive => CreateProviderWithInteractiveAuth(authResult.Url, verbose, debug),
            AuthMode.Managed => CreateProviderWithManagedIdentity(authResult.Url, verbose, debug),
            _ => throw new InvalidOperationException($"Cannot create provider for auth mode {authResult.Mode}")
        };
    }

    /// <summary>
    /// Creates a service provider from environment variables.
    /// </summary>
    private static ServiceProvider CreateProviderFromEnvVars(
        AuthResolver.AuthResult authResult,
        bool verbose,
        bool debug)
    {
        var services = new ServiceCollection();
        ConfigureLogging(services, verbose, debug);

        // Add Dataverse connection pool with client credentials
        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection("EnvVars")
            {
                Url = authResult.Url,
                ClientId = authResult.ClientId,
                ClientSecret = authResult.ClientSecret,
                TenantId = authResult.TenantId,
                AuthType = DataverseAuthType.ClientSecret
            });
            options.Pool.Enabled = true;
            options.Pool.MinPoolSize = 0;
            options.Pool.MaxConnectionsPerUser = Math.Max(Environment.ProcessorCount * 4, 16);
            options.Pool.DisableAffinityCookie = true;
        });

        services.AddDataverseMigration();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a service provider with interactive device code flow authentication.
    /// </summary>
    private static ServiceProvider CreateProviderWithInteractiveAuth(
        string url,
        bool verbose,
        bool debug)
    {
        var services = new ServiceCollection();
        ConfigureLogging(services, verbose, debug);

        // Create device code token provider for interactive authentication
        var tokenProvider = new DeviceCodeTokenProvider(url);

        // Create ServiceClient with device code authentication
        var serviceClient = new ServiceClient(
            new Uri(url),
            tokenProvider.GetTokenAsync,
            useUniqueInstance: true);

        if (!serviceClient.IsReady)
        {
            var error = serviceClient.LastError ?? "Unknown error";
            serviceClient.Dispose();
            throw new InvalidOperationException($"Failed to establish connection 'Interactive'. Error: {error}");
        }

        // Wrap in ServiceClientSource for the connection pool
        var source = new ServiceClientSource(
            serviceClient,
            "Interactive",
            maxPoolSize: Math.Max(Environment.ProcessorCount * 4, 16));

        // Create pool options
        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = true,
            MinPoolSize = 0,
            MaxConnectionsPerUser = Math.Max(Environment.ProcessorCount * 4, 16),
            DisableAffinityCookie = true
        };

        // Register services
        services.AddSingleton<IThrottleTracker, ThrottleTracker>();
        services.AddSingleton<IAdaptiveRateController, AdaptiveRateController>();

        // Register the connection pool with the source
        services.AddSingleton<IDataverseConnectionPool>(sp =>
            new DataverseConnectionPool(
                new[] { source },
                sp.GetRequiredService<IThrottleTracker>(),
                sp.GetRequiredService<IAdaptiveRateController>(),
                poolOptions,
                sp.GetRequiredService<ILogger<DataverseConnectionPool>>()));

        services.AddTransient<IBulkOperationExecutor, BulkOperationExecutor>();

        services.AddDataverseMigration();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a service provider with Azure Managed Identity authentication.
    /// </summary>
    private static ServiceProvider CreateProviderWithManagedIdentity(
        string url,
        bool verbose,
        bool debug)
    {
        var services = new ServiceCollection();
        ConfigureLogging(services, verbose, debug);

        // Add Dataverse connection pool with managed identity auth
        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection("ManagedIdentity")
            {
                Url = url,
                AuthType = DataverseAuthType.ManagedIdentity
            });
            options.Pool.Enabled = true;
            options.Pool.MinPoolSize = 0;
            options.Pool.MaxConnectionsPerUser = Math.Max(Environment.ProcessorCount * 4, 16);
            options.Pool.DisableAffinityCookie = true;
        });

        services.AddDataverseMigration();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Configures logging for a service collection.
    /// </summary>
    private static void ConfigureLogging(IServiceCollection services, bool verbose, bool debug)
    {
        services.AddLogging(builder =>
        {
            if (debug)
            {
                builder.SetMinimumLevel(LogLevel.Debug);
            }
            else if (verbose)
            {
                builder.SetMinimumLevel(LogLevel.Information);
            }
            else
            {
                builder.SetMinimumLevel(LogLevel.Warning);
            }

            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "[HH:mm:ss] ";
            });
        });
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

    /// <summary>
    /// Creates a service provider for offline analysis (no Dataverse connection needed).
    /// </summary>
    /// <returns>A service provider with analysis services registered.</returns>
    public static ServiceProvider CreateAnalysisProvider()
    {
        var services = new ServiceCollection();
        services.AddDataverseMigration();
        return services.BuildServiceProvider();
    }
}
