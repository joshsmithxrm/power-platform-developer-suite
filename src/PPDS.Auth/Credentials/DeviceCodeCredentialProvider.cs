using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides authentication using device code flow.
/// User visits a URL and enters a code to authenticate.
/// </summary>
public sealed class DeviceCodeCredentialProvider : ICredentialProvider
{
    /// <summary>
    /// Microsoft's well-known public client ID for development/prototyping with Dataverse.
    /// See: https://learn.microsoft.com/en-us/power-apps/developer/data-platform/xrm-tooling/use-connection-strings-xrm-tooling-connect
    /// </summary>
    private const string MicrosoftPublicClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    private readonly CloudEnvironment _cloud;
    private readonly string? _tenantId;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;

    private IPublicClientApplication? _msalClient;
    private MsalCacheHelper? _cacheHelper;
    private AuthenticationResult? _cachedResult;
    private bool _disposed;

    /// <inheritdoc />
    public AuthMethod AuthMethod => AuthMethod.DeviceCode;

    /// <inheritdoc />
    public string? Identity => _cachedResult?.Account?.Username;

    /// <inheritdoc />
    public DateTimeOffset? TokenExpiresAt => _cachedResult?.ExpiresOn;

    /// <summary>
    /// Creates a new device code credential provider.
    /// </summary>
    /// <param name="cloud">The cloud environment.</param>
    /// <param name="tenantId">Optional tenant ID (defaults to "organizations" for multi-tenant).</param>
    /// <param name="deviceCodeCallback">Optional callback for displaying device code (defaults to console output).</param>
    public DeviceCodeCredentialProvider(
        CloudEnvironment cloud = CloudEnvironment.Public,
        string? tenantId = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        _cloud = cloud;
        _tenantId = tenantId;
        _deviceCodeCallback = deviceCodeCallback;
    }

    /// <summary>
    /// Creates a provider from an auth profile.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <param name="deviceCodeCallback">Optional callback for displaying device code.</param>
    /// <returns>A new provider instance.</returns>
    public static DeviceCodeCredentialProvider FromProfile(
        AuthProfile profile,
        Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        return new DeviceCodeCredentialProvider(
            profile.Cloud,
            profile.TenantId,
            deviceCodeCallback);
    }

    /// <inheritdoc />
    public async Task<ServiceClient> CreateServiceClientAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            throw new ArgumentNullException(nameof(environmentUrl));

        // Normalize URL
        environmentUrl = environmentUrl.TrimEnd('/');

        // Ensure MSAL client is initialized
        await EnsureMsalClientInitializedAsync().ConfigureAwait(false);

        // Get token
        var token = await GetTokenAsync(environmentUrl, cancellationToken).ConfigureAwait(false);

        // Create ServiceClient with token provider
        var client = new ServiceClient(
            new Uri(environmentUrl),
            _ => Task.FromResult(token),
            useUniqueInstance: true);

        if (!client.IsReady)
        {
            var error = client.LastError ?? "Unknown error";
            client.Dispose();
            throw new AuthenticationException($"Failed to connect to Dataverse: {error}");
        }

        return client;
    }

    /// <summary>
    /// Gets an access token for the specified Dataverse URL.
    /// </summary>
    private async Task<string> GetTokenAsync(string environmentUrl, CancellationToken cancellationToken)
    {
        var scopes = new[] { $"{environmentUrl}/.default" };

        // Try to get token silently from cache first
        if (_cachedResult != null && _cachedResult.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _cachedResult.AccessToken;
        }

        // Try silent acquisition from MSAL cache
        var accounts = await _msalClient!.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();

        if (account != null)
        {
            try
            {
                _cachedResult = await _msalClient
                    .AcquireTokenSilent(scopes, account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                return _cachedResult.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Silent acquisition failed, need interactive
            }
        }

        // Fall back to device code flow
        _cachedResult = await _msalClient
            .AcquireTokenWithDeviceCode(scopes, deviceCodeResult =>
            {
                if (_deviceCodeCallback != null)
                {
                    _deviceCodeCallback(new DeviceCodeInfo(
                        deviceCodeResult.UserCode,
                        deviceCodeResult.VerificationUrl,
                        deviceCodeResult.Message));
                }
                else
                {
                    // Default console output
                    Console.WriteLine();
                    Console.WriteLine("To sign in, use a web browser to open the page:");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"  {deviceCodeResult.VerificationUrl}");
                    Console.ResetColor();
                    Console.WriteLine();
                    Console.WriteLine("Enter the code:");
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  {deviceCodeResult.UserCode}");
                    Console.ResetColor();
                    Console.WriteLine();
                    Console.WriteLine("Waiting for authentication...");
                }
                return Task.CompletedTask;
            })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (_deviceCodeCallback == null)
        {
            Console.WriteLine($"Authenticated as: {_cachedResult.Account.Username}");
            Console.WriteLine();
        }

        return _cachedResult.AccessToken;
    }

    /// <summary>
    /// Ensures the MSAL client is initialized with token cache.
    /// </summary>
    private async Task EnsureMsalClientInitializedAsync()
    {
        if (_msalClient != null)
            return;

        var cloudInstance = CloudEndpoints.GetAzureCloudInstance(_cloud);
        var tenant = string.IsNullOrWhiteSpace(_tenantId) ? "organizations" : _tenantId;

        _msalClient = PublicClientApplicationBuilder
            .Create(MicrosoftPublicClientId)
            .WithAuthority(cloudInstance, tenant)
            .WithDefaultRedirectUri()
            .Build();

        // Set up persistent cache
        try
        {
            ProfilePaths.EnsureDirectoryExists();

            var storageProperties = new StorageCreationPropertiesBuilder(
                    ProfilePaths.TokenCacheFileName,
                    ProfilePaths.DataDirectory)
                .WithUnprotectedFile() // Fallback for Linux without libsecret
                .Build();

            _cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
            _cacheHelper.RegisterCache(_msalClient.UserTokenCache);
        }
        catch (MsalCachePersistenceException ex)
        {
            // Cache persistence failed - continue without persistent cache
            Console.Error.WriteLine($"Warning: Token cache persistence unavailable ({ex.Message}). You may need to re-authenticate each session.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }
}

/// <summary>
/// Information about a device code for authentication.
/// </summary>
public sealed class DeviceCodeInfo
{
    /// <summary>
    /// Gets the user code to enter at the verification URL.
    /// </summary>
    public string UserCode { get; }

    /// <summary>
    /// Gets the verification URL to open in a browser.
    /// </summary>
    public string VerificationUrl { get; }

    /// <summary>
    /// Gets the full message to display to the user.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Creates a new DeviceCodeInfo.
    /// </summary>
    public DeviceCodeInfo(string userCode, string verificationUrl, string message)
    {
        UserCode = userCode;
        VerificationUrl = verificationUrl;
        Message = message;
    }
}
