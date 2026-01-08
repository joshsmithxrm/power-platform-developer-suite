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
    /// <summary>
    /// Timeout for credential provider creation.
    /// Includes secure store lookup which may involve DPAPI/Keychain.
    /// </summary>
    private static readonly TimeSpan CredentialProviderTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Timeout for ServiceClient creation/connection to Dataverse.
    /// </summary>
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);

    private readonly AuthProfile _profile;
    private readonly string _environmentUrl;
    private readonly string? _environmentDisplayName;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly ISecureCredentialStore? _credentialStore;
    private readonly Action<AuthProfile>? _onProfileUpdated;
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
    /// <param name="credentialStore">Optional secure credential store for looking up secrets.</param>
    /// <param name="onProfileUpdated">Optional callback invoked when profile metadata is updated (e.g., HomeAccountId after auth).</param>
    public ProfileConnectionSource(
        AuthProfile profile,
        string environmentUrl,
        int maxPoolSize = 52,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        string? environmentDisplayName = null,
        ISecureCredentialStore? credentialStore = null,
        Action<AuthProfile>? onProfileUpdated = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentNullException(nameof(environmentUrl));

        _environmentUrl = environmentUrl.TrimEnd('/');
        _maxPoolSize = maxPoolSize;
        _deviceCodeCallback = deviceCodeCallback;
        _environmentDisplayName = environmentDisplayName;
        _credentialStore = credentialStore;
        _onProfileUpdated = onProfileUpdated;

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
    /// <param name="credentialStore">Optional secure credential store for looking up secrets.</param>
    /// <param name="onProfileUpdated">Optional callback invoked when profile metadata is updated (e.g., HomeAccountId after auth).</param>
    /// <returns>A new connection source.</returns>
    /// <exception cref="InvalidOperationException">If the profile has no environment.</exception>
    public static ProfileConnectionSource FromProfile(
        AuthProfile profile,
        int maxPoolSize = 52,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        ISecureCredentialStore? credentialStore = null,
        Action<AuthProfile>? onProfileUpdated = null)
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
            profile.Environment.DisplayName,
            credentialStore,
            onProfileUpdated);
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

            // Create credential provider using async factory (supports secure store lookups).
            // Wrap in Task.Run to avoid deadlock in sync contexts (UI/ASP.NET)
            // by running async code on threadpool which has no sync context.
            // Add timeout to fail fast if credential store is unresponsive.
            try
            {
                using var credCts = new CancellationTokenSource(CredentialProviderTimeout);
                _provider = System.Threading.Tasks.Task.Run(() =>
                    CredentialProviderFactory.CreateAsync(_profile, _credentialStore, _deviceCodeCallback, credCts.Token))
                    .WaitAsync(credCts.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Credential provider creation timed out after {CredentialProviderTimeout.TotalSeconds}s for profile '{_profile.DisplayIdentifier}'. " +
                    "This may indicate credential store issues. Set PPDS_SPN_SECRET or PPDS_TEST_CLIENT_SECRET environment variable to bypass.");
            }

            try
            {
                // Create ServiceClient with timeout to fail fast if Dataverse is unreachable.
                using var connCts = new CancellationTokenSource(ConnectionTimeout);
                _seedClient = System.Threading.Tasks.Task.Run(() =>
                    _provider.CreateServiceClientAsync(_environmentUrl, connCts.Token))
                    .WaitAsync(connCts.Token)
                    .GetAwaiter()
                    .GetResult();

                // Persist HomeAccountId if it changed after authentication
                // This enables token cache reuse across sessions
                TryUpdateHomeAccountId();

                return _seedClient;
            }
            catch (OperationCanceledException)
            {
                _provider?.Dispose();
                _provider = null;
                throw new TimeoutException(
                    $"Connection to Dataverse timed out after {ConnectionTimeout.TotalSeconds}s for '{_environmentUrl}'. " +
                    "Check network connectivity and environment URL.");
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

            // Create credential provider with timeout to fail fast if credential store is unresponsive
            try
            {
                using var credCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                credCts.CancelAfter(CredentialProviderTimeout);

                _provider = await CredentialProviderFactory.CreateAsync(
                    _profile, _credentialStore, _deviceCodeCallback, credCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Credential provider creation timed out after {CredentialProviderTimeout.TotalSeconds}s for profile '{_profile.DisplayIdentifier}'. " +
                    "This may indicate credential store issues. Set PPDS_SPN_SECRET or PPDS_TEST_CLIENT_SECRET environment variable to bypass.");
            }

            try
            {
                // Create ServiceClient with timeout to fail fast if Dataverse is unreachable
                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connCts.CancelAfter(ConnectionTimeout);

                _seedClient = await _provider
                    .CreateServiceClientAsync(_environmentUrl, connCts.Token)
                    .ConfigureAwait(false);

                // Persist HomeAccountId if it changed after authentication
                // This enables token cache reuse across sessions
                TryUpdateHomeAccountId();

                return _seedClient;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _provider?.Dispose();
                _provider = null;
                throw new TimeoutException(
                    $"Connection to Dataverse timed out after {ConnectionTimeout.TotalSeconds}s for '{_environmentUrl}'. " +
                    "Check network connectivity and environment URL.");
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
    /// Updates the profile's HomeAccountId if it changed after authentication.
    /// This enables MSAL token cache reuse across sessions.
    /// </summary>
    private void TryUpdateHomeAccountId()
    {
        if (_provider == null || _onProfileUpdated == null)
            return;

        var newHomeAccountId = _provider.HomeAccountId;
        if (string.IsNullOrEmpty(newHomeAccountId))
            return;

        // Only update if it's different (avoids unnecessary file writes)
        if (string.Equals(_profile.HomeAccountId, newHomeAccountId, StringComparison.Ordinal))
            return;

        _profile.HomeAccountId = newHomeAccountId;
        _onProfileUpdated(_profile);
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
