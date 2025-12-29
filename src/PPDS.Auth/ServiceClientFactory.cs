using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;

namespace PPDS.Auth;

/// <summary>
/// Factory for creating authenticated ServiceClient instances from profiles.
/// </summary>
public sealed class ServiceClientFactory : IDisposable
{
    private readonly ProfileStore _profileStore;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly List<ICredentialProvider> _activeProviders = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new ServiceClientFactory.
    /// </summary>
    /// <param name="profileStore">The profile store to use.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    public ServiceClientFactory(
        ProfileStore? profileStore = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        _profileStore = profileStore ?? new ProfileStore();
        _deviceCodeCallback = deviceCodeCallback;
    }

    /// <summary>
    /// Creates an authenticated ServiceClient for the active profile.
    /// </summary>
    /// <param name="environmentUrl">Optional environment URL override. If not specified, uses the profile's environment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An authenticated ServiceClient.</returns>
    /// <exception cref="InvalidOperationException">If no profile is active or no environment is available.</exception>
    public async Task<ServiceClient> CreateFromActiveProfileAsync(
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        var collection = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = collection.ActiveProfile
            ?? throw new InvalidOperationException("No active profile. Use 'ppds auth create' to create a profile.");

        return await CreateFromProfileAsync(profile, environmentUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an authenticated ServiceClient for a specific profile by name.
    /// </summary>
    /// <param name="profileName">The profile name.</param>
    /// <param name="environmentUrl">Optional environment URL override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An authenticated ServiceClient.</returns>
    /// <exception cref="InvalidOperationException">If the profile is not found or no environment is available.</exception>
    public async Task<ServiceClient> CreateFromProfileNameAsync(
        string profileName,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        var collection = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = collection.GetByName(profileName)
            ?? throw new InvalidOperationException($"Profile '{profileName}' not found.");

        return await CreateFromProfileAsync(profile, environmentUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates authenticated ServiceClients for multiple profiles (for pooling).
    /// </summary>
    /// <param name="profileNames">The profile names.</param>
    /// <param name="environmentUrl">Environment URL (required if any profile has no environment).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of authenticated ServiceClients.</returns>
    /// <exception cref="InvalidOperationException">If profiles have different environments or no environment is available.</exception>
    public async Task<IReadOnlyList<ServiceClient>> CreateFromProfileNamesAsync(
        IEnumerable<string> profileNames,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        var names = profileNames.ToList();
        if (names.Count == 0)
            throw new ArgumentException("At least one profile name is required.", nameof(profileNames));

        var collection = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profiles = new List<AuthProfile>();

        foreach (var name in names)
        {
            var profile = collection.GetByName(name)
                ?? throw new InvalidOperationException($"Profile '{name}' not found.");
            profiles.Add(profile);
        }

        // Determine the environment URL to use
        var resolvedUrl = ResolveEnvironmentUrl(profiles, environmentUrl);

        // Validate all profiles can target the same environment
        ValidatePoolingEnvironments(profiles, resolvedUrl);

        // Create ServiceClients for all profiles
        var clients = new List<ServiceClient>();
        try
        {
            foreach (var profile in profiles)
            {
                var client = await CreateFromProfileAsync(profile, resolvedUrl, cancellationToken)
                    .ConfigureAwait(false);
                clients.Add(client);
            }
        }
        catch
        {
            // Dispose any clients we created before the failure
            foreach (var client in clients)
            {
                client.Dispose();
            }
            throw;
        }

        return clients;
    }

    /// <summary>
    /// Creates an authenticated ServiceClient from a profile.
    /// </summary>
    private async Task<ServiceClient> CreateFromProfileAsync(
        AuthProfile profile,
        string? environmentUrl,
        CancellationToken cancellationToken)
    {
        // Determine environment URL
        var url = environmentUrl ?? profile.Environment?.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                $"No environment selected for profile '{profile.DisplayIdentifier}'.\n\n" +
                "To fix this, either:\n" +
                "  1. Select an environment: ppds env select --environment \"Name\"\n" +
                "  2. Specify on command: --environment \"Name\"\n" +
                "  3. Recreate profile with environment: ppds auth create --environment \"Name\" ...");
        }

        // Apply performance settings
        ApplyPerformanceSettings();

        // Create credential provider
        var provider = CredentialProviderFactory.Create(profile, _deviceCodeCallback);
        _activeProviders.Add(provider);

        // Create ServiceClient
        var client = await provider.CreateServiceClientAsync(url, cancellationToken).ConfigureAwait(false);

        // Update last used time
        profile.LastUsedAt = DateTimeOffset.UtcNow;

        // Update username if available (for device code auth)
        if (string.IsNullOrWhiteSpace(profile.Username) && !string.IsNullOrWhiteSpace(provider.Identity))
        {
            profile.Username = provider.Identity;
        }

        return client;
    }

    /// <summary>
    /// Resolves the environment URL for a set of profiles.
    /// </summary>
    private static string ResolveEnvironmentUrl(IReadOnlyList<AuthProfile> profiles, string? overrideUrl)
    {
        // Override URL takes precedence
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl.TrimEnd('/');
        }

        // Find profiles with environments
        var profilesWithEnv = profiles.Where(p => p.HasEnvironment).ToList();

        if (profilesWithEnv.Count == 0)
        {
            var profileList = string.Join(", ", profiles.Select(p => $"'{p.DisplayIdentifier}'"));
            throw new InvalidOperationException(
                $"No environment specified and none of the profiles have an environment selected: {profileList}\n\n" +
                "Use --environment to specify the target environment.");
        }

        // Use the first profile's environment
        return profilesWithEnv[0].Environment!.Url.TrimEnd('/');
    }

    /// <summary>
    /// Validates that all profiles can target the same environment.
    /// </summary>
    private static void ValidatePoolingEnvironments(IReadOnlyList<AuthProfile> profiles, string targetUrl)
    {
        var normalizedTarget = targetUrl.TrimEnd('/').ToLowerInvariant();
        var mismatches = new List<string>();

        foreach (var profile in profiles)
        {
            if (!profile.HasEnvironment)
                continue;

            var profileUrl = profile.Environment!.Url.TrimEnd('/').ToLowerInvariant();
            if (profileUrl != normalizedTarget)
            {
                mismatches.Add($"  - {profile.DisplayIdentifier}: {profile.Environment.DisplayName} ({profile.Environment.Url})");
            }
        }

        if (mismatches.Count > 0)
        {
            var mismatchList = string.Join("\n", mismatches);
            throw new InvalidOperationException(
                $"Profiles target different environments:\n" +
                $"  Target: {targetUrl}\n" +
                $"  Mismatches:\n{mismatchList}\n\n" +
                "Use --environment to specify a common target, or ensure all profiles target the same environment.");
        }
    }

    /// <summary>
    /// Applies performance settings for optimal Dataverse throughput.
    /// </summary>
    private static void ApplyPerformanceSettings()
    {
        // These settings are recommended by Microsoft for optimal Dataverse performance
        // https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update

        // Increase thread pool minimum (default is typically 4)
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        if (workerThreads < 100)
        {
            ThreadPool.SetMinThreads(100, completionPortThreads);
        }

        // Increase connection limit (default is 2)
        ServicePointManager.DefaultConnectionLimit = 65000;

        // Disable Expect: 100-Continue header
        ServicePointManager.Expect100Continue = false;

        // Disable Nagle algorithm for better latency
        ServicePointManager.UseNagleAlgorithm = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var provider in _activeProviders)
        {
            provider.Dispose();
        }

        _activeProviders.Clear();
        _profileStore.Dispose();
        _disposed = true;
    }
}
