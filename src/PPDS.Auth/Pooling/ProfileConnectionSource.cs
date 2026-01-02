using System;
using System.Threading;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Pooling;

/// <summary>
/// Connection source that creates ServiceClients from an authentication profile.
/// Implements IConnectionSource pattern for use with connection pools.
/// </summary>
public sealed class ProfileConnectionSource : IDisposable
{
    private readonly AuthProfile _profile;
    private readonly string _environmentUrl;
    private readonly string? _environmentDisplayName;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly int _maxPoolSize;

    private volatile ServiceClient? _seedClient;
    private ICredentialProvider? _provider;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Gets the unique name for this connection source.
    /// Includes identity and environment display name when available.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the maximum number of pooled connections for this source.
    /// </summary>
    public int MaxPoolSize => _maxPoolSize;

    /// <summary>
    /// Gets the authentication profile.
    /// </summary>
    public AuthProfile Profile => _profile;

    /// <summary>
    /// Gets the environment URL.
    /// </summary>
    public string EnvironmentUrl => _environmentUrl;

    /// <summary>
    /// Creates a new ProfileConnectionSource.
    /// </summary>
    /// <param name="profile">The authentication profile.</param>
    /// <param name="environmentUrl">The Dataverse environment URL.</param>
    /// <param name="maxPoolSize">Maximum pool size (default: 52 per Microsoft recommendations).</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    /// <param name="environmentDisplayName">Optional environment display name for connection naming.</param>
    public ProfileConnectionSource(
        AuthProfile profile,
        string environmentUrl,
        int maxPoolSize = 52,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        string? environmentDisplayName = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentNullException(nameof(environmentUrl));

        _environmentUrl = environmentUrl.TrimEnd('/');
        _maxPoolSize = maxPoolSize;
        _deviceCodeCallback = deviceCodeCallback;
        _environmentDisplayName = environmentDisplayName;

        // Format: "identity@environment" when environment name is available
        // Token-provider-based auth doesn't populate ConnectedOrgFriendlyName,
        // so we include the environment name here rather than relying on the SDK.
        Name = string.IsNullOrEmpty(environmentDisplayName)
            ? profile.IdentityDisplay
            : $"{profile.IdentityDisplay}@{environmentDisplayName}";
    }

    /// <summary>
    /// Creates a ProfileConnectionSource from a profile, using the profile's environment.
    /// </summary>
    /// <param name="profile">The authentication profile (must have environment set).</param>
    /// <param name="maxPoolSize">Maximum pool size.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    /// <returns>A new connection source.</returns>
    /// <exception cref="InvalidOperationException">If the profile has no environment.</exception>
    public static ProfileConnectionSource FromProfile(
        AuthProfile profile,
        int maxPoolSize = 52,
        Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));

        if (!profile.HasEnvironment)
        {
            throw new InvalidOperationException(
                $"Profile '{profile.DisplayIdentifier}' has no environment selected. " +
                "Use 'ppds env select' to select an environment, or provide --environment.");
        }

        return new ProfileConnectionSource(
            profile,
            profile.Environment!.Url,
            maxPoolSize,
            deviceCodeCallback,
            profile.Environment.DisplayName);
    }

    /// <summary>
    /// Gets the seed ServiceClient for cloning.
    /// </summary>
    /// <returns>An authenticated, ready-to-use ServiceClient.</returns>
    public ServiceClient GetSeedClient()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProfileConnectionSource));

        // Fast path if already created (volatile read)
        if (_seedClient != null)
            return _seedClient;

        _lock.Wait();
        try
        {
            // Double-check after acquiring lock
            if (_seedClient != null)
                return _seedClient;

            // Create credential provider
            _provider = CredentialProviderFactory.Create(_profile, _deviceCodeCallback);

            try
            {
                // Create ServiceClient synchronously (pool expects sync method).
                // Wrap in Task.Run to avoid deadlock in sync contexts (UI/ASP.NET)
                // by running async code on threadpool which has no sync context.
                _seedClient = System.Threading.Tasks.Task.Run(() =>
                    _provider.CreateServiceClientAsync(_environmentUrl, CancellationToken.None))
                    .GetAwaiter()
                    .GetResult();

                return _seedClient;
            }
            catch (Exception ex)
            {
                _provider?.Dispose();
                _provider = null;
                throw new InvalidOperationException(
                    $"Failed to create connection for profile '{_profile.DisplayIdentifier}': {ex.Message}", ex);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets the seed ServiceClient asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An authenticated, ready-to-use ServiceClient.</returns>
    public async System.Threading.Tasks.Task<ServiceClient> GetSeedClientAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProfileConnectionSource));

        // Fast path if already created (volatile read)
        if (_seedClient != null)
            return _seedClient;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (_seedClient != null)
                return _seedClient;

            // Create credential provider
            _provider = CredentialProviderFactory.Create(_profile, _deviceCodeCallback);

            try
            {
                _seedClient = await _provider
                    .CreateServiceClientAsync(_environmentUrl, cancellationToken)
                    .ConfigureAwait(false);

                return _seedClient;
            }
            catch (Exception ex)
            {
                _provider?.Dispose();
                _provider = null;
                throw new InvalidOperationException(
                    $"Failed to create connection for profile '{_profile.DisplayIdentifier}': {ex.Message}", ex);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Invalidates the cached seed client, forcing fresh authentication on next use.
    /// </summary>
    /// <remarks>
    /// Call this when a token failure is detected. The next call to GetSeedClient
    /// will create a new client with fresh authentication instead of returning the cached one.
    /// </remarks>
    public void InvalidateSeed()
    {
        _lock.Wait();
        try
        {
            if (_seedClient == null)
                return;

            _seedClient.Dispose();
            _seedClient = null;

            // Also dispose the credential provider so a fresh one is created
            _provider?.Dispose();
            _provider = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _lock.Wait();
        try
        {
            _seedClient?.Dispose();
            _provider?.Dispose();
            _disposed = true;
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
