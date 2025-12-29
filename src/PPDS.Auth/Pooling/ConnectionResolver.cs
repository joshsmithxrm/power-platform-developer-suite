using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Pooling;

/// <summary>
/// Resolves profile names to authenticated connections.
/// </summary>
public sealed class ConnectionResolver : IDisposable
{
    private readonly ProfileStore _store;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly List<ProfileConnectionSource> _sources = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new ConnectionResolver.
    /// </summary>
    /// <param name="store">The profile store (optional, uses default if null).</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    public ConnectionResolver(
        ProfileStore? store = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        _store = store ?? new ProfileStore();
        _deviceCodeCallback = deviceCodeCallback;
    }

    /// <summary>
    /// Resolves the active profile to a ServiceClient.
    /// </summary>
    /// <param name="environmentOverride">Optional environment URL override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An authenticated ServiceClient.</returns>
    public async Task<ServiceClient> ResolveActiveAsync(
        string? environmentOverride = null,
        CancellationToken cancellationToken = default)
    {
        var collection = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

        var profile = collection.ActiveProfile
            ?? throw new InvalidOperationException(
                "No active profile. Use 'ppds auth create' to create a profile, " +
                "or 'ppds auth select' to select one.");

        return await ResolveProfileAsync(profile, environmentOverride, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a named profile to a ServiceClient.
    /// </summary>
    /// <param name="profileName">The profile name.</param>
    /// <param name="environmentOverride">Optional environment URL override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An authenticated ServiceClient.</returns>
    public async Task<ServiceClient> ResolveByNameAsync(
        string profileName,
        string? environmentOverride = null,
        CancellationToken cancellationToken = default)
    {
        var collection = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

        var profile = collection.GetByName(profileName)
            ?? throw new InvalidOperationException($"Profile '{profileName}' not found.");

        return await ResolveProfileAsync(profile, environmentOverride, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves multiple profile names to connection sources for pooling.
    /// </summary>
    /// <param name="profileNames">The profile names (comma-separated or array).</param>
    /// <param name="environmentOverride">Optional environment URL override.</param>
    /// <param name="maxPoolSizePerProfile">Maximum pool size per profile.</param>
    /// <param name="environmentDisplayName">Optional environment display name for connection naming.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of connection sources. Caller takes ownership and is responsible for disposal.</returns>
    public async Task<IReadOnlyList<ProfileConnectionSource>> ResolveMultipleAsync(
        IEnumerable<string> profileNames,
        string? environmentOverride = null,
        int maxPoolSizePerProfile = 52,
        string? environmentDisplayName = null,
        CancellationToken cancellationToken = default)
    {
        var names = profileNames.ToList();
        if (names.Count == 0)
            throw new ArgumentException("At least one profile name is required.", nameof(profileNames));

        var collection = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var sources = new List<ProfileConnectionSource>();
        string? resolvedEnvironment = null;

        foreach (var name in names)
        {
            var profile = collection.GetByName(name)
                ?? throw new InvalidOperationException($"Profile '{name}' not found.");

            // Determine environment URL
            var envUrl = environmentOverride ?? profile.Environment?.Url;
            if (string.IsNullOrWhiteSpace(envUrl))
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.DisplayIdentifier}' has no environment selected. " +
                    "Use 'ppds env select' to select an environment, or provide --environment.");
            }

            // Validate all profiles target the same environment
            if (resolvedEnvironment == null)
            {
                resolvedEnvironment = envUrl.TrimEnd('/').ToLowerInvariant();
            }
            else
            {
                var normalizedUrl = envUrl.TrimEnd('/').ToLowerInvariant();
                if (normalizedUrl != resolvedEnvironment)
                {
                    throw new InvalidOperationException(
                        $"Profile '{profile.DisplayIdentifier}' targets a different environment.\n" +
                        $"  Expected: {resolvedEnvironment}\n" +
                        $"  Got: {normalizedUrl}\n\n" +
                        "All profiles must target the same environment for pooling. " +
                        "Use --environment to specify a common target.");
                }
            }

            // Use provided display name, or fall back to profile's environment display name
            var envDisplayName = environmentDisplayName ?? profile.Environment?.DisplayName;

            var source = new ProfileConnectionSource(
                profile,
                envUrl,
                maxPoolSizePerProfile,
                _deviceCodeCallback,
                envDisplayName);

            sources.Add(source);
            // Note: NOT tracking for disposal - caller takes ownership of returned sources
        }

        return sources;
    }

    /// <summary>
    /// Resolves a profile or uses the active profile if none specified.
    /// </summary>
    /// <param name="profileName">Optional profile name (uses active if null).</param>
    /// <param name="environmentOverride">Optional environment URL override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An authenticated ServiceClient.</returns>
    public async Task<ServiceClient> ResolveAsync(
        string? profileName,
        string? environmentOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return await ResolveActiveAsync(environmentOverride, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ResolveByNameAsync(profileName, environmentOverride, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a comma-separated profile string into individual names.
    /// </summary>
    /// <param name="profileString">Comma-separated profile names.</param>
    /// <returns>List of profile names.</returns>
    public static IReadOnlyList<string> ParseProfileString(string? profileString)
    {
        if (string.IsNullOrWhiteSpace(profileString))
            return Array.Empty<string>();

        return profileString
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private async Task<ServiceClient> ResolveProfileAsync(
        AuthProfile profile,
        string? environmentOverride,
        CancellationToken cancellationToken)
    {
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

        // Create credential provider and get client
        using var provider = CredentialProviderFactory.Create(profile, _deviceCodeCallback);
        return await provider.CreateServiceClientAsync(envUrl, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _sources.Clear();
        _store.Dispose();
        _disposed = true;
    }
}
