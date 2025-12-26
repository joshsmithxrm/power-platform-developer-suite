using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
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
    /// Microsoft's well-known public client ID for development/prototyping with Dataverse.
    /// See: https://learn.microsoft.com/en-us/power-apps/developer/data-platform/xrm-tooling/use-connection-strings-xrm-tooling-connect
    /// </summary>
    public const string MicrosoftPublicClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    /// <summary>
    /// Microsoft's well-known redirect URI for the public client ID.
    /// </summary>
    public const string MicrosoftPublicRedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

    /// <summary>
    /// Creates a service provider from resolved connection configuration.
    /// </summary>
    /// <param name="config">The connection configuration resolved from environment variables.</param>
    /// <param name="connectionName">Optional name for the connection. Default: "Primary"</param>
    /// <param name="verbose">Enable verbose logging output (LogLevel.Information).</param>
    /// <param name="debug">Enable debug logging output (LogLevel.Debug).</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProvider(
        ConnectionResolver.ConnectionConfig config,
        string connectionName = "Primary",
        bool verbose = false,
        bool debug = false)
    {
        return CreateProvider(config.Url, config.ClientId, config.ClientSecret, config.TenantId, connectionName, verbose, debug);
    }

    /// <summary>
    /// Creates a service provider configured with a single Dataverse connection.
    /// </summary>
    /// <param name="url">The Dataverse environment URL.</param>
    /// <param name="clientId">The Azure AD application (client) ID.</param>
    /// <param name="clientSecret">The client secret value.</param>
    /// <param name="tenantId">Optional Azure AD tenant ID.</param>
    /// <param name="connectionName">Optional name for the connection. Default: "Primary"</param>
    /// <param name="verbose">Enable verbose logging output (LogLevel.Information).</param>
    /// <param name="debug">Enable debug logging output (LogLevel.Debug).</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProvider(
        string url,
        string clientId,
        string clientSecret,
        string? tenantId = null,
        string connectionName = "Primary",
        bool verbose = false,
        bool debug = false)
    {
        var services = new ServiceCollection();

        // Add logging with console output
        // --debug: LogLevel.Debug (diagnostic detail)
        // --verbose: LogLevel.Information (operational info)
        // Default: LogLevel.Warning (errors and warnings only)
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
    /// <param name="verbose">Enable verbose logging output (LogLevel.Information).</param>
    /// <param name="debug">Enable debug logging output (LogLevel.Debug).</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProviderFromConfig(
        IConfiguration configuration,
        string environmentName,
        bool verbose = false,
        bool debug = false)
    {
        var services = new ServiceCollection();

        // Add logging with console output
        // --debug: LogLevel.Debug (diagnostic detail)
        // --verbose: LogLevel.Information (operational info)
        // Default: LogLevel.Warning (errors and warnings only)
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
    /// Creates a service provider from environment variables (--auth env mode).
    /// </summary>
    /// <param name="authResult">The resolved auth configuration from AuthResolver.</param>
    /// <param name="verbose">Enable verbose logging output.</param>
    /// <param name="debug">Enable debug logging output.</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProviderFromEnvVars(
        AuthResolver.AuthResult authResult,
        bool verbose = false,
        bool debug = false)
    {
        if (authResult.Mode != AuthMode.Env)
            throw new ArgumentException("AuthResult must be from env mode", nameof(authResult));

        return CreateProvider(
            authResult.Url!,
            authResult.ClientId!,
            authResult.ClientSecret!,
            authResult.TenantId,
            "EnvVars",
            verbose,
            debug);
    }

    /// <summary>
    /// Creates a service provider with interactive (browser) OAuth authentication.
    /// Uses Microsoft's well-known public client ID for development/prototyping.
    /// </summary>
    /// <param name="url">The Dataverse environment URL.</param>
    /// <param name="verbose">Enable verbose logging output.</param>
    /// <param name="debug">Enable debug logging output.</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProviderWithInteractiveAuth(
        string url,
        bool verbose = false,
        bool debug = false)
    {
        var services = new ServiceCollection();

        // Add logging
        ConfigureLogging(services, verbose, debug);

        // Add Dataverse connection pool with interactive OAuth auth
        // Uses Microsoft's well-known public client ID for interactive login
        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection("Interactive")
            {
                Url = url,
                AuthType = DataverseAuthType.OAuth,
                ClientId = MicrosoftPublicClientId,
                RedirectUri = MicrosoftPublicRedirectUri,
                LoginPrompt = OAuthLoginPrompt.SelectAccount // Always show account picker
            });
            options.Pool.Enabled = true;
            options.Pool.MinPoolSize = 0;
            options.Pool.MaxConnectionsPerUser = Math.Max(Environment.ProcessorCount * 4, 16);
            options.Pool.DisableAffinityCookie = true;
        });

        // Add migration services
        services.AddDataverseMigration();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a service provider with Azure Managed Identity authentication.
    /// </summary>
    /// <param name="url">The Dataverse environment URL.</param>
    /// <param name="verbose">Enable verbose logging output.</param>
    /// <param name="debug">Enable debug logging output.</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProviderWithManagedIdentity(
        string url,
        bool verbose = false,
        bool debug = false)
    {
        var services = new ServiceCollection();

        // Add logging
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

        // Add migration services
        services.AddDataverseMigration();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a service provider based on the auth mode.
    /// </summary>
    /// <param name="authMode">The authentication mode.</param>
    /// <param name="authResult">The resolved auth configuration.</param>
    /// <param name="configuration">Configuration for config mode.</param>
    /// <param name="environmentName">Environment name for config mode.</param>
    /// <param name="verbose">Enable verbose logging.</param>
    /// <param name="debug">Enable debug logging.</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProviderForAuthMode(
        AuthMode authMode,
        AuthResolver.AuthResult authResult,
        IConfiguration? configuration,
        string? environmentName,
        bool verbose = false,
        bool debug = false)
    {
        return authMode switch
        {
            AuthMode.Env => CreateProviderFromEnvVars(authResult, verbose, debug),
            AuthMode.Interactive => CreateProviderWithInteractiveAuth(authResult.Url!, verbose, debug),
            AuthMode.Managed => CreateProviderWithManagedIdentity(authResult.Url!, verbose, debug),
            AuthMode.Config when configuration != null && !string.IsNullOrEmpty(environmentName)
                => CreateProviderFromConfig(configuration, environmentName, verbose, debug),
            _ => throw new InvalidOperationException($"Cannot create provider for auth mode {authMode}")
        };
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
}
