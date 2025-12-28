using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PPDS.Auth.Credentials;
using PPDS.Auth.Pooling;
using PPDS.Auth.Profiles;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using PPDS.Migration.DependencyInjection;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Information about the resolved connection used by commands to display connection headers.
/// </summary>
public sealed class ResolvedConnectionInfo
{
    /// <summary>
    /// Gets the authenticated profile.
    /// </summary>
    public required AuthProfile Profile { get; init; }

    /// <summary>
    /// Gets the resolved environment URL.
    /// </summary>
    public required string EnvironmentUrl { get; init; }

    /// <summary>
    /// Gets the environment display name if available.
    /// </summary>
    public string? EnvironmentDisplayName { get; init; }
}

/// <summary>
/// Factory for creating service providers from authentication profiles.
/// </summary>
public static class ProfileServiceFactory
{
    /// <summary>
    /// Creates a service provider using a single profile.
    /// </summary>
    /// <param name="profileName">Profile name (null for active profile).</param>
    /// <param name="environmentOverride">Environment URL override.</param>
    /// <param name="verbose">Enable verbose logging.</param>
    /// <param name="debug">Enable debug logging.</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    /// <param name="ratePreset">Rate control preset for throttle management.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured service provider.</returns>
    public static async Task<ServiceProvider> CreateFromProfileAsync(
        string? profileName,
        string? environmentOverride,
        bool verbose = false,
        bool debug = false,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        RateControlPreset ratePreset = RateControlPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        // Get profile (active or named)
        AuthProfile profile;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            profile = collection.ActiveProfile
                ?? throw new InvalidOperationException(
                    "No active profile. Use 'ppds auth create' to create a profile, " +
                    "or 'ppds auth select' to select one.");
        }
        else
        {
            profile = collection.GetByName(profileName)
                ?? throw new InvalidOperationException($"Profile '{profileName}' not found.");
        }

        // Determine environment URL
        var envUrl = environmentOverride ?? profile.Environment?.Url;
        if (string.IsNullOrWhiteSpace(envUrl))
        {
            throw new InvalidOperationException(
                $"Profile '{profile.DisplayIdentifier}' has no environment selected.\n\n" +
                "To fix this, either:\n" +
                "  1. Select an environment: ppds env select <name>\n" +
                "  2. Specify on command: --environment <url>");
        }

        // Create connection source
        var source = new ProfileConnectionSource(profile, envUrl, 52, deviceCodeCallback);
        var adapter = new ProfileConnectionSourceAdapter(source);

        // Create connection info for header display
        var connectionInfo = new ResolvedConnectionInfo
        {
            Profile = profile,
            EnvironmentUrl = envUrl,
            EnvironmentDisplayName = profile.Environment?.DisplayName
        };

        return CreateProviderFromSources(new[] { adapter }, connectionInfo, verbose, debug, ratePreset);
    }

    /// <summary>
    /// Creates a service provider using multiple profiles for pooling.
    /// </summary>
    /// <param name="profileNames">Comma-separated profile names.</param>
    /// <param name="environmentOverride">Environment URL override (required if profiles have different environments).</param>
    /// <param name="verbose">Enable verbose logging.</param>
    /// <param name="debug">Enable debug logging.</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    /// <param name="ratePreset">Rate control preset for throttle management.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured service provider.</returns>
    public static async Task<ServiceProvider> CreateFromProfilesAsync(
        string? profileNames,
        string? environmentOverride,
        bool verbose = false,
        bool debug = false,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        RateControlPreset ratePreset = RateControlPreset.Balanced,
        CancellationToken cancellationToken = default)
    {
        var names = ConnectionResolver.ParseProfileString(profileNames);

        // No profiles specified - use active profile
        if (names.Count == 0)
        {
            return await CreateFromProfileAsync(
                null, environmentOverride, verbose, debug, deviceCodeCallback, ratePreset, cancellationToken)
                .ConfigureAwait(false);
        }

        // Single profile specified
        if (names.Count == 1)
        {
            return await CreateFromProfileAsync(
                names[0], environmentOverride, verbose, debug, deviceCodeCallback, ratePreset, cancellationToken)
                .ConfigureAwait(false);
        }

        // Multiple profiles - use ConnectionResolver for validation
        using var resolver = new ConnectionResolver(deviceCodeCallback: deviceCodeCallback);
        var sources = await resolver.ResolveMultipleAsync(names, environmentOverride, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Wrap in adapters
        var adapters = sources.Select(s => new ProfileConnectionSourceAdapter(s)).ToArray();

        // For multi-profile, use the first profile's info for header display
        var firstSource = sources[0];
        var connectionInfo = new ResolvedConnectionInfo
        {
            Profile = firstSource.Profile,
            EnvironmentUrl = firstSource.EnvironmentUrl,
            EnvironmentDisplayName = firstSource.Profile.Environment?.DisplayName
        };

        return CreateProviderFromSources(adapters, connectionInfo, verbose, debug, ratePreset);
    }

    /// <summary>
    /// Creates service providers for source and target environments (for copy command).
    /// </summary>
    /// <param name="profileName">Profile name (null for active profile).</param>
    /// <param name="sourceProfile">Source-specific profile override.</param>
    /// <param name="targetProfile">Target-specific profile override.</param>
    /// <param name="sourceEnv">Source environment.</param>
    /// <param name="targetEnv">Target environment.</param>
    /// <param name="verbose">Enable verbose logging.</param>
    /// <param name="debug">Enable debug logging.</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple of (source provider, target provider).</returns>
    public static async Task<(ServiceProvider Source, ServiceProvider Target)> CreateForCopyAsync(
        string? profileName,
        string? sourceProfile,
        string? targetProfile,
        string sourceEnv,
        string targetEnv,
        bool verbose = false,
        bool debug = false,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default)
    {
        // Determine which profile to use for source and target
        var sourceProfileName = sourceProfile ?? profileName;
        var targetProfileName = targetProfile ?? profileName;

        // Create providers for each environment
        var sourceProvider = await CreateFromProfileAsync(
            sourceProfileName, sourceEnv, verbose, debug, deviceCodeCallback,
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var targetProvider = await CreateFromProfileAsync(
                targetProfileName, targetEnv, verbose, debug, deviceCodeCallback,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return (sourceProvider, targetProvider);
        }
        catch
        {
            await sourceProvider.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Creates a service provider from connection sources.
    /// </summary>
    private static ServiceProvider CreateProviderFromSources(
        IConnectionSource[] sources,
        ResolvedConnectionInfo connectionInfo,
        bool verbose,
        bool debug,
        RateControlPreset ratePreset = RateControlPreset.Balanced)
    {
        var services = new ServiceCollection();
        ConfigureLogging(services, verbose, debug);

        // Register connection info for header display
        services.AddSingleton(connectionInfo);

        // Configure DataverseOptions with the rate preset
        var dataverseOptions = new DataverseOptions
        {
            AdaptiveRate = { Preset = ratePreset }
        };
        services.AddSingleton<IOptions<DataverseOptions>>(new OptionsWrapper<DataverseOptions>(dataverseOptions));

        // Pool options
        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = true,
            MinPoolSize = 0,
            MaxConnectionsPerUser = 52,
            DisableAffinityCookie = true
        };

        // Register resilience services
        services.AddSingleton<IThrottleTracker, ThrottleTracker>();
        services.AddSingleton<IAdaptiveRateController, AdaptiveRateController>();

        // Register the connection pool
        services.AddSingleton<IDataverseConnectionPool>(sp =>
            new DataverseConnectionPool(
                sources,
                sp.GetRequiredService<IThrottleTracker>(),
                sp.GetRequiredService<IAdaptiveRateController>(),
                poolOptions,
                sp.GetRequiredService<ILogger<DataverseConnectionPool>>()));

        services.AddTransient<IBulkOperationExecutor, BulkOperationExecutor>();
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
    /// Default device code callback that writes to console.
    /// </summary>
    public static void DefaultDeviceCodeCallback(DeviceCodeInfo info)
    {
        Console.WriteLine();
        Console.WriteLine(info.Message);
        Console.WriteLine();
    }
}
