using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PPDS.Auth.Credentials;
using PPDS.Auth.DependencyInjection;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Services;

/// <summary>
/// Extension methods for registering CLI application services.
/// </summary>
/// <remarks>
/// Application services encapsulate business logic shared between
/// CLI commands, TUI wizards, and daemon RPC handlers.
/// </remarks>
public static class ServiceRegistration
{
    /// <summary>
    /// Registers CLI application services with the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCliApplicationServices(this IServiceCollection services)
    {
        // Auth services (ProfileStore, EnvironmentConfigStore, NativeCredentialStore)
        services.AddAuthServices();

        // Profile management services
        services.AddTransient<IProfileService, ProfileService>();
        services.AddTransient<IEnvironmentService, EnvironmentService>();

        // Query services — factory delegate provides pool capacity for aggregate partitioning
        services.AddTransient<ISqlQueryService>(sp =>
        {
            var queryExecutor = sp.GetRequiredService<IQueryExecutor>();
            var tdsExecutor = sp.GetService<ITdsQueryExecutor>();
            var bulkExecutor = sp.GetService<IBulkOperationExecutor>();
            var metadataExecutor = sp.GetService<IMetadataQueryExecutor>();
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            return new SqlQueryService(
                queryExecutor,
                tdsExecutor,
                bulkExecutor,
                metadataExecutor,
                pool.GetTotalRecommendedParallelism());
        });
        services.AddSingleton<IQueryHistoryService, QueryHistoryService>();

        // Export services
        services.AddTransient<IExportService, ExportService>();

        // Plugin registration service - requires connection pool
        services.AddTransient<IPluginRegistrationService>(sp =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var logger = sp.GetRequiredService<ILogger<PluginRegistrationService>>();
            return new PluginRegistrationService(pool, logger);
        });

        // SQL language service - uses ICachedMetadataProvider when available
        services.AddTransient<ISqlLanguageService>(sp =>
        {
            var metadataProvider = sp.GetService<ICachedMetadataProvider>();
            return new SqlLanguageService(metadataProvider);
        });

        // TUI theming
        services.AddSingleton<ITuiThemeService, TuiThemeService>();

        // Environment configuration
        services.AddSingleton<IEnvironmentConfigService>(sp =>
            new EnvironmentConfigService(sp.GetRequiredService<EnvironmentConfigStore>()));

        // Connection service - requires profile-based token provider and environment ID
        // Registered as factory because it needs runtime values from ResolvedConnectionInfo
        services.AddTransient<IConnectionService>(sp =>
        {
            var connectionInfo = sp.GetRequiredService<ResolvedConnectionInfo>();
            var credentialStore = sp.GetRequiredService<ISecureCredentialStore>();
            var logger = sp.GetRequiredService<ILogger<ConnectionService>>();

            // Environment ID is required for Power Apps Admin API
            if (string.IsNullOrEmpty(connectionInfo.EnvironmentId))
            {
                throw new InvalidOperationException(
                    "Environment ID is not available. Power Apps Admin API operations require an environment " +
                    "that was resolved through Global Discovery Service. Direct URL connections do not provide " +
                    "the environment ID needed for connection operations.");
            }

            // Create token provider from profile
            var profile = connectionInfo.Profile;
            IPowerPlatformTokenProvider tokenProvider;

            if (profile.AuthMethod == AuthMethod.ClientSecret)
            {
                // SPN - need secret from credential store (keyed by ApplicationId)
                if (string.IsNullOrEmpty(profile.ApplicationId))
                {
                    throw new InvalidOperationException(
                        $"Profile '{profile.DisplayIdentifier}' is configured for ClientSecret auth but has no ApplicationId.");
                }

                // DI factory delegates are synchronous; GetAsync is safe here because
                // credential store uses file I/O, not network calls that would benefit from async.
#pragma warning disable PPDS012 // Sync-over-async: DI factory cannot be async
                var storedCredential = credentialStore.GetAsync(profile.ApplicationId).GetAwaiter().GetResult();
#pragma warning restore PPDS012
                if (storedCredential?.ClientSecret == null)
                {
                    throw new InvalidOperationException(
                        $"Client secret not found for application '{profile.ApplicationId}'. " +
                        "Run 'ppds auth create' to recreate the profile with credentials.");
                }
                tokenProvider = PowerPlatformTokenProvider.FromProfileWithSecret(profile, storedCredential.ClientSecret);
            }
            else
            {
                // User-delegated auth
                tokenProvider = PowerPlatformTokenProvider.FromProfile(profile);
            }

            return new ConnectionService(
                tokenProvider,
                profile.Cloud,
                connectionInfo.EnvironmentId,
                logger);
        });

        return services;
    }
}
