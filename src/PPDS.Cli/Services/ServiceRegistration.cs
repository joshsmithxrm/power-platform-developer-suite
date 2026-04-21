using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PPDS.Auth.Credentials;
using PPDS.Auth.DependencyInjection;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Cli.Services.DeploymentSettings;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.EnvironmentVariables;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Services.Flows;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.ImportJobs;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Services.Roles;
using PPDS.Cli.Services.SolutionComponents;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.UpdateCheck;
using PPDS.Cli.Services.Users;
using PPDS.Cli.Services.WebResources;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
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
            var metadataProvider = sp.GetService<ICachedMetadataProvider>();
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var guard = sp.GetRequiredService<IShakedownGuard>();
            return new SqlQueryService(
                queryExecutor,
                guard,
                tdsExecutor,
                bulkExecutor,
                metadataExecutor,
                pool.GetTotalRecommendedParallelism(),
                metadataProvider);
        });

        // TDS Endpoint executor — per-environment, uses same auth pattern as IConnectionService
        services.AddTransient<ITdsQueryExecutor>(sp =>
        {
            var connectionInfo = sp.GetRequiredService<ResolvedConnectionInfo>();
            return TdsQueryExecutorFactory.Create(
                connectionInfo.Profile,
                connectionInfo.EnvironmentUrl,
                sp.GetRequiredService<ISecureCredentialStore>(),
                sp.GetService<ILogger<TdsQueryExecutor>>());
        });

        services.AddSingleton<IQueryHistoryService, QueryHistoryService>();

        // Export services
        services.AddTransient<IExportService, ExportService>();

        // Plugin registration service - requires connection pool + shakedown guard
        services.AddTransient<IPluginRegistrationService>(sp =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var guard = sp.GetRequiredService<IShakedownGuard>();
            var logger = sp.GetRequiredService<ILogger<PluginRegistrationService>>();
            return new PluginRegistrationService(pool, guard, logger);
        });

        // Service endpoint service - manages webhooks and service bus endpoints
        services.AddTransient<IServiceEndpointService>(sp =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var guard = sp.GetRequiredService<IShakedownGuard>();
            var logger = sp.GetRequiredService<ILogger<ServiceEndpointService>>();
            return new ServiceEndpointService(pool, guard, logger);
        });

        // Custom API service - manages Custom APIs and their request/response parameters
        services.AddTransient<ICustomApiService>(sp =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var pluginRegistrationService = sp.GetRequiredService<IPluginRegistrationService>();
            var guard = sp.GetRequiredService<IShakedownGuard>();
            var logger = sp.GetRequiredService<ILogger<CustomApiService>>();
            return new CustomApiService(pool, pluginRegistrationService, guard, logger);
        });

        // Data provider service - manages virtual entity data providers and data sources
        services.AddTransient<IDataProviderService>(sp =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var guard = sp.GetRequiredService<IShakedownGuard>();
            var logger = sp.GetRequiredService<ILogger<DataProviderService>>();
            return new DataProviderService(pool, guard, logger);
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
                if (string.IsNullOrEmpty(profile.ApplicationId))
                {
                    throw new AuthenticationException(
                        $"Profile '{profile.DisplayIdentifier}' is configured for ClientSecret auth but has no ApplicationId.",
                        "Auth.InvalidCredentials");
                }

                // DI factory delegates are synchronous; GetAsync is safe here because
                // credential store uses file I/O, not network calls that would benefit from async.
#pragma warning disable PPDS012 // Sync-over-async: DI factory cannot be async
                var storedCredential = credentialStore.GetAsync(profile.ApplicationId).GetAwaiter().GetResult();
#pragma warning restore PPDS012
                tokenProvider = PowerPlatformTokenProvider.FromProfileWithSecret(profile, storedCredential?.ClientSecret ?? "");
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

        // Update check service — singleton, manages its own cache file
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();

        // Browser launcher — swappable so tests can replace with a no-op (issue #809).
        // Transient so each resolution re-reads BrowserHelper.Launcher; a singleton
        // factory would capture whatever was installed at first resolution and miss
        // per-test swaps.
        services.AddTransient<IBrowserLauncher>(_ => BrowserHelper.Launcher);

        // Shakedown guard infrastructure — singleton cache of activation state.
        services.AddSingleton<IEnvironment, SystemEnvironment>();
        services.AddSingleton<IFileSystem, SystemFileSystem>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IShakedownGuard, ShakedownGuard>();

        // ------------------------------------------------------------------
        // Application services relocated from PPDS.Dataverse (constitution A1).
        // Infrastructure lives in PPDS.Dataverse; domain/application services
        // live here in PPDS.Cli.Services.<Area>.
        // ------------------------------------------------------------------

        // Mutation-owning services (6) — each takes IShakedownGuard between pool and logger.
        services.AddTransient<IPluginTraceService>(sp => new PluginTraceService(
            sp.GetRequiredService<IDataverseConnectionPool>(),
            sp.GetRequiredService<IShakedownGuard>(),
            sp.GetRequiredService<ILogger<PluginTraceService>>()));

        services.AddTransient<IWebResourceService>(sp => new WebResourceService(
            sp.GetRequiredService<IDataverseConnectionPool>(),
            sp.GetRequiredService<ISolutionService>(),
            sp.GetRequiredService<IShakedownGuard>(),
            sp.GetRequiredService<ILogger<WebResourceService>>()));

        services.AddTransient<IEnvironmentVariableService>(sp => new EnvironmentVariableService(
            sp.GetRequiredService<IDataverseConnectionPool>(),
            sp.GetRequiredService<IShakedownGuard>(),
            sp.GetRequiredService<ILogger<EnvironmentVariableService>>()));

        services.AddTransient<ISolutionService>(sp => new SolutionService(
            sp.GetRequiredService<IDataverseConnectionPool>(),
            sp.GetRequiredService<IShakedownGuard>(),
            sp.GetRequiredService<ILogger<SolutionService>>(),
            sp.GetRequiredService<IMetadataQueryService>(),
            sp.GetRequiredService<IComponentNameResolver>(),
            sp.GetRequiredService<ICachedMetadataProvider>()));

        services.AddTransient<IMetadataAuthoringService>(sp => new DataverseMetadataAuthoringService(
            sp.GetRequiredService<IDataverseConnectionPool>(),
            sp.GetRequiredService<SchemaValidator>(),
            sp.GetRequiredService<IShakedownGuard>(),
            sp.GetService<ILogger<DataverseMetadataAuthoringService>>(),
            sp.GetService<ICachedMetadataProvider>()));

        services.AddTransient<IRoleService>(sp => new RoleService(
            sp.GetRequiredService<IDataverseConnectionPool>(),
            sp.GetRequiredService<IShakedownGuard>(),
            sp.GetRequiredService<ILogger<RoleService>>()));

        // Read-only services (6) — unchanged ctor shape, simple type registration.
        services.AddTransient<IImportJobService, ImportJobService>();
        services.AddTransient<IUserService, UserService>();
        services.AddTransient<IFlowService, FlowService>();
        services.AddTransient<IConnectionReferenceService, ConnectionReferenceService>();
        services.AddTransient<IDeploymentSettingsService, DeploymentSettingsService>();
        services.AddTransient<IComponentNameResolver, ComponentNameResolver>();

        return services;
    }
}
