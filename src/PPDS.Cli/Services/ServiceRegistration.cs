using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Services;

/// <summary>
/// Extension methods for registering CLI application services.
/// </summary>
/// <remarks>
/// Application services encapsulate business logic shared between
/// CLI commands, TUI wizards, and daemon RPC handlers.
/// See ADR-0015 for architectural context.
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
        // Query services
        services.AddTransient<ISqlQueryService, SqlQueryService>();

        // Plugin registration service - requires connection pool
        services.AddTransient<IPluginRegistrationService>(sp =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var logger = sp.GetRequiredService<ILogger<PluginRegistrationService>>();
            return new PluginRegistrationService(pool, logger);
        });

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
