using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Credentials;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// Helper methods for creating Dataverse test infrastructure.
/// </summary>
public static class LiveTestHelpers
{
    /// <summary>
    /// Creates an authenticated ServiceClient using client secret credentials.
    /// </summary>
    /// <param name="config">The live test configuration.</param>
    /// <returns>An authenticated, ready ServiceClient.</returns>
    public static async Task<ServiceClient> CreateServiceClientAsync(LiveTestConfiguration config)
    {
        if (!config.HasClientSecretCredentials)
        {
            throw new InvalidOperationException("Client secret credentials are required.");
        }

        using var provider = new ClientSecretCredentialProvider(
            config.ApplicationId!,
            config.ClientSecret!,
            config.TenantId!);

        return await provider.CreateServiceClientAsync(config.DataverseUrl!);
    }

    /// <summary>
    /// Creates a connection source using client secret authentication.
    /// </summary>
    /// <param name="config">The live test configuration.</param>
    /// <param name="name">The connection source name.</param>
    /// <param name="maxPoolSize">Maximum pool size for this source.</param>
    /// <returns>A ServiceClientSource wrapping an authenticated client.</returns>
    public static async Task<ServiceClientSource> CreateConnectionSourceAsync(
        LiveTestConfiguration config,
        string name = "TestConnection",
        int maxPoolSize = 10)
    {
        var client = await CreateServiceClientAsync(config);
        return new ServiceClientSource(client, name, maxPoolSize);
    }

    /// <summary>
    /// Creates a DataverseConnectionPool with default test options.
    /// </summary>
    /// <param name="sources">Connection sources for the pool.</param>
    /// <param name="options">Optional pool options override.</param>
    /// <returns>A configured connection pool.</returns>
    public static DataverseConnectionPool CreateConnectionPool(
        IEnumerable<IConnectionSource> sources,
        ConnectionPoolOptions? options = null)
    {
        var poolOptions = options ?? new ConnectionPoolOptions
        {
            Enabled = true,
            DisableAffinityCookie = true,
            SelectionStrategy = ConnectionSelectionStrategy.ThrottleAware,
            EnableValidation = false, // Disable background validation for tests
            AcquireTimeout = TimeSpan.FromSeconds(30),
            MaxIdleTime = TimeSpan.FromMinutes(5),
            MaxLifetime = TimeSpan.FromMinutes(60)
        };

        var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        var throttleLogger = loggerFactory.CreateLogger<ThrottleTracker>();
        var poolLogger = loggerFactory.CreateLogger<DataverseConnectionPool>();

        var throttleTracker = new ThrottleTracker(throttleLogger);

        return new DataverseConnectionPool(
            sources,
            throttleTracker,
            poolOptions,
            poolLogger);
    }

    /// <summary>
    /// Creates a ThrottleTracker for testing.
    /// </summary>
    /// <returns>A new ThrottleTracker instance.</returns>
    public static ThrottleTracker CreateThrottleTracker()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        var logger = loggerFactory.CreateLogger<ThrottleTracker>();
        return new ThrottleTracker(logger);
    }
}
