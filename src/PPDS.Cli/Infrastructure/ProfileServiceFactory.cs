using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PPDS.Auth.Credentials;
using PPDS.Auth.Pooling;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services;
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

    /// <summary>
    /// Gets the Power Platform environment ID if available.
    /// Required for Power Apps Admin API operations.
    /// </summary>
    public string? EnvironmentId { get; init; }
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
    /// <param name="environmentOverride">Environment override - accepts URL, friendly name, unique name, or ID.</param>
    /// <param name="verbose">Enable verbose logging.</param>
    /// <param name="debug">Enable debug logging.</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured service provider.</returns>
    public static async Task<ServiceProvider> CreateFromProfileAsync(
        string? profileName,
        string? environmentOverride,
        bool verbose = false,
        bool debug = false,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default)
    {
        var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

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

        // Resolve environment - handles URL, name, ID, or uses profile's saved environment
        var (envUrl, envDisplayName, envId) = await ResolveEnvironmentAsync(
            profile, environmentOverride, cancellationToken).ConfigureAwait(false);

        // Create credential store for secure secret lookups (registered in DI for disposal)
        var credentialStore = new SecureCredentialStore();

        var source = new ProfileConnectionSource(
            profile, envUrl, 52, deviceCodeCallback, envDisplayName, credentialStore);
        var adapter = new ProfileConnectionSourceAdapter(source);

        var connectionInfo = new ResolvedConnectionInfo
        {
            Profile = profile,
            EnvironmentUrl = envUrl,
            EnvironmentDisplayName = envDisplayName,
            EnvironmentId = envId
        };

        return CreateProviderFromSources(new[] { adapter }, connectionInfo, credentialStore, verbose, debug);
    }

    /// <summary>
    /// Resolves an environment identifier to a URL, display name, and environment ID.
    /// </summary>
    private static async Task<(string Url, string? DisplayName, string? EnvironmentId)> ResolveEnvironmentAsync(
        AuthProfile profile,
        string? environmentOverride,
        CancellationToken cancellationToken)
    {
        // No override - use profile's saved environment
        if (string.IsNullOrWhiteSpace(environmentOverride))
        {
            if (string.IsNullOrWhiteSpace(profile.Environment?.Url))
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.DisplayIdentifier}' has no environment selected.\n\n" +
                    "To fix this, either:\n" +
                    "  1. Select an environment: ppds env select <name>\n" +
                    "  2. Specify on command: --environment <url>");
            }
            return (profile.Environment.Url, profile.Environment.DisplayName, profile.Environment.EnvironmentId);
        }

        // Check if it's already a URL
        if (Uri.TryCreate(environmentOverride, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "https" || uri.Scheme == "http"))
        {
            // URL override - we don't have the environment ID
            return (environmentOverride.TrimEnd('/'), uri.Host, null);
        }

        // Resolve name/ID to URL using GlobalDiscoveryService
        var resolved = await EnvironmentResolverHelper.ResolveAsync(
            profile, environmentOverride, cancellationToken).ConfigureAwait(false);

        return (resolved.Url, resolved.DisplayName, resolved.EnvironmentId);
    }

    /// <summary>
    /// Creates a service provider using multiple profiles for pooling.
    /// </summary>
    /// <param name="profileNames">Comma-separated profile names.</param>
    /// <param name="environmentOverride">Environment override - accepts URL, friendly name, unique name, or ID.</param>
    /// <param name="verbose">Enable verbose logging.</param>
    /// <param name="debug">Enable debug logging.</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A configured service provider.</returns>
    public static async Task<ServiceProvider> CreateFromProfilesAsync(
        string? profileNames,
        string? environmentOverride,
        bool verbose = false,
        bool debug = false,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default)
    {
        var names = ConnectionResolver.ParseProfileString(profileNames);

        // Single or no profile - delegate to single profile method (which handles resolution)
        if (names.Count <= 1)
        {
            return await CreateFromProfileAsync(
                names.Count == 0 ? null : names[0],
                environmentOverride, verbose, debug, deviceCodeCallback,
                cancellationToken)
                .ConfigureAwait(false);
        }

        // Multiple profiles - need to resolve environment first using the first profile
        var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        var firstProfile = collection.GetByName(names[0])
            ?? throw new InvalidOperationException($"Profile '{names[0]}' not found.");

        var (envUrl, envDisplayName, envId) = await ResolveEnvironmentAsync(
            firstProfile, environmentOverride, cancellationToken).ConfigureAwait(false);

        // Create credential store for secure secret lookups (registered in DI for disposal)
        var credentialStore = new SecureCredentialStore();

        // Now resolve all profiles with the resolved URL
        using var resolver = new ConnectionResolver(
            deviceCodeCallback: deviceCodeCallback, credentialStore: credentialStore);
        var sources = await resolver.ResolveMultipleAsync(
                names, envUrl, environmentDisplayName: envDisplayName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var adapters = sources.Select(s => new ProfileConnectionSourceAdapter(s)).ToArray();

        var connectionInfo = new ResolvedConnectionInfo
        {
            Profile = firstProfile,
            EnvironmentUrl = envUrl,
            EnvironmentDisplayName = envDisplayName,
            EnvironmentId = envId
        };

        return CreateProviderFromSources(adapters, connectionInfo, credentialStore, verbose, debug);
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
        var sourceProfileName = sourceProfile ?? profileName;
        var targetProfileName = targetProfile ?? profileName;

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
        ISecureCredentialStore credentialStore,
        bool verbose,
        bool debug)
    {
        var services = new ServiceCollection();
        ConfigureLogging(services, verbose, debug);

        services.AddSingleton(connectionInfo);

        // Register credential store for disposal with service provider
        services.AddSingleton<ISecureCredentialStore>(credentialStore);

        var dataverseOptions = new DataverseOptions();
        services.AddSingleton<IOptions<DataverseOptions>>(new OptionsWrapper<DataverseOptions>(dataverseOptions));

        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = true,
            DisableAffinityCookie = true
        };

        // Register shared services (IThrottleTracker, IBulkOperationExecutor, IMetadataService)
        // This method is shared with PPDS.Dataverse to prevent DI registration divergence
        services.RegisterDataverseServices();

        // Register CLI application services (ISqlQueryService, etc.)
        // See ADR-0015 for architectural context
        services.AddCliApplicationServices();

        // Connection pool - CLI uses factory delegate because it gets IConnectionSource[] from auth profiles
        // (Library uses direct type registration from configuration)
        services.AddSingleton<IDataverseConnectionPool>(sp =>
            new DataverseConnectionPool(
                sources,
                sp.GetRequiredService<IThrottleTracker>(),
                poolOptions,
                sp.GetRequiredService<ILogger<DataverseConnectionPool>>()));

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
        Console.Error.WriteLine();
        Console.Error.WriteLine(info.Message);
        Console.Error.WriteLine();
    }
}
