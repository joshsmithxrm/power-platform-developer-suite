using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Discovery;

/// <summary>
/// Service for discovering Dataverse environments via the Global Discovery Service.
/// </summary>
public sealed class GlobalDiscoveryService : IGlobalDiscoveryService, IDisposable
{
    private readonly CloudEnvironment _cloud;
    private readonly string? _tenantId;
    private readonly string? _homeAccountId;
    private readonly AuthMethod? _preferredAuthMethod;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;

    private IPublicClientApplication? _msalClient;
    private MsalCacheHelper? _cacheHelper;
    private string? _capturedHomeAccountId;
    private bool _disposed;

    /// <summary>
    /// Creates a new GlobalDiscoveryService.
    /// </summary>
    /// <param name="cloud">The cloud environment to use.</param>
    /// <param name="tenantId">Optional tenant ID.</param>
    /// <param name="homeAccountId">Optional MSAL home account identifier for precise account lookup.</param>
    /// <param name="preferredAuthMethod">Optional preferred auth method from profile.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    public GlobalDiscoveryService(
        CloudEnvironment cloud = CloudEnvironment.Public,
        string? tenantId = null,
        string? homeAccountId = null,
        AuthMethod? preferredAuthMethod = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        _cloud = cloud;
        _tenantId = tenantId;
        _homeAccountId = homeAccountId;
        _preferredAuthMethod = preferredAuthMethod;
        _deviceCodeCallback = deviceCodeCallback;
    }

    /// <summary>
    /// Creates a GlobalDiscoveryService from an auth profile.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    /// <returns>A new service instance.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when the profile uses an authentication method that is not supported by Global Discovery.
    /// Global Discovery requires delegated user authentication (InteractiveBrowser, DeviceCode, or UsernamePassword).
    /// Service principals, managed identities, and federated credentials are not supported.
    /// </exception>
    public static GlobalDiscoveryService FromProfile(
        AuthProfile profile,
        Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        // Global Discovery Service uses MSAL public client which only supports delegated user auth.
        // Service principals, managed identities, and federated credentials use confidential clients
        // which are not supported by Global Discovery.
        if (!SupportsGlobalDiscovery(profile.AuthMethod))
        {
            var authMethodName = profile.AuthMethod.ToString();
            throw new NotSupportedException(
                $"Global Discovery Service requires interactive user authentication. " +
                $"The profile '{profile.DisplayIdentifier}' uses {authMethodName} which is not supported. " +
                $"Use 'ppds env select --environment <url>' to connect directly to an environment, " +
                $"or create an interactive profile with 'ppds auth create'.");
        }

        return new GlobalDiscoveryService(
            profile.Cloud,
            profile.TenantId,
            profile.HomeAccountId,
            profile.AuthMethod,
            deviceCodeCallback);
    }

    /// <summary>
    /// Checks if an authentication method supports Global Discovery.
    /// </summary>
    /// <param name="authMethod">The authentication method to check.</param>
    /// <returns>True if the method can be used with Global Discovery; otherwise false.</returns>
    /// <remarks>
    /// Global Discovery requires delegated user authentication via MSAL public client.
    /// Only InteractiveBrowser and DeviceCode are supported.
    /// </remarks>
    public static bool SupportsGlobalDiscovery(AuthMethod authMethod)
    {
        return authMethod is AuthMethod.InteractiveBrowser or AuthMethod.DeviceCode;
    }

    /// <summary>
    /// Gets the HomeAccountId captured during interactive authentication.
    /// </summary>
    /// <remarks>
    /// This value is only populated after <see cref="DiscoverEnvironmentsAsync"/> completes
    /// and only if interactive authentication was required (not silent).
    /// Callers should persist this value to the profile to enable silent auth on subsequent calls.
    /// </remarks>
    public string? CapturedHomeAccountId => _capturedHomeAccountId;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverEnvironmentsAsync(
        CancellationToken cancellationToken = default)
    {
        // Get the discovery service URI for the cloud
        var discoveryUri = new Uri(CloudEndpoints.GetGlobalDiscoveryUrl(_cloud));

        // Get token provider function
        await EnsureMsalClientInitializedAsync().ConfigureAwait(false);
        var tokenProvider = CreateTokenProviderFunction(discoveryUri, cancellationToken);

        // Discover organizations
        var organizations = await ServiceClient.DiscoverOnlineOrganizationsAsync(
            tokenProvider,
            discoveryUri,
            logger: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Map to our model
        var environments = new List<DiscoveredEnvironment>();
        foreach (var org in organizations)
        {
            // Get the web application URL - used for both API connections and web interface
            // In Dataverse, the WebApplication endpoint is the base URL (e.g., https://org.crm.dynamics.com)
            string? baseUrl = null;
            if (org.Endpoints.TryGetValue(Microsoft.Xrm.Sdk.Discovery.EndpointType.WebApplication, out var webAppUrl))
            {
                baseUrl = webAppUrl;
            }

            // Parse TenantId if present (it may be a string or Guid depending on version)
            Guid? tenantGuid = null;
            if (!string.IsNullOrEmpty(org.TenantId) && Guid.TryParse(org.TenantId, out var parsedTenant))
            {
                tenantGuid = parsedTenant;
            }

            environments.Add(new DiscoveredEnvironment
            {
                Id = org.OrganizationId,
                EnvironmentId = org.EnvironmentId,
                FriendlyName = org.FriendlyName,
                UniqueName = org.UniqueName,
                UrlName = org.UrlName,
                ApiUrl = baseUrl ?? string.Empty,
                Url = baseUrl,
                State = (int)org.State,
                Version = org.OrganizationVersion,
                Region = org.Geo,
                TenantId = tenantGuid,
                OrganizationType = (int)org.OrganizationType
            });
        }

        return environments.OrderBy(e => e.FriendlyName).ToList();
    }

    /// <summary>
    /// Creates a token provider function for the discovery service.
    /// </summary>
    private Func<string, Task<string>> CreateTokenProviderFunction(
        Uri discoveryUri,
        CancellationToken cancellationToken)
    {
        return async (string resource) =>
        {
            var scopes = new[] { $"{discoveryUri.GetLeftPart(UriPartial.Authority)}/.default" };
            AuthDebugLog.WriteLine($"[GlobalDiscovery] GetToken: resource={resource}");

            // Try to find the correct account for silent acquisition
            var account = await FindAccountAsync().ConfigureAwait(false);

            if (account != null)
            {
                AuthDebugLog.WriteLine($"  Found account for silent auth: {account.Username}");
                try
                {
                    var silentResult = await _msalClient!
                        .AcquireTokenSilent(scopes, account)
                        .ExecuteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    AuthDebugLog.WriteLine("  Silent acquisition SUCCEEDED");
                    return silentResult.AccessToken;
                }
                catch (MsalUiRequiredException ex)
                {
                    // Need interactive auth
                    AuthDebugLog.WriteLine($"  Silent acquisition FAILED: MsalUiRequiredException - {ex.Message}");
                }
            }
            else
            {
                AuthDebugLog.WriteLine("  No account found for silent auth - skipping to interactive");
            }

            // Use the explicit auth method - no automatic fallback.
            // The profile's auth method determines which flow to use.
            AuthenticationResult result;

            if (_preferredAuthMethod == AuthMethod.DeviceCode)
            {
                // Device code flow - user explicitly requested this
                AuthDebugLog.WriteLine("  Starting device code authentication...");
                result = await _msalClient!
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
                            AuthenticationOutput.WriteLine();
                            AuthenticationOutput.WriteLine("To sign in, use a web browser to open the page:");
                            AuthenticationOutput.WriteLine($"  {deviceCodeResult.VerificationUrl}");
                            AuthenticationOutput.WriteLine();
                            AuthenticationOutput.WriteLine("Enter the code:");
                            AuthenticationOutput.WriteLine($"  {deviceCodeResult.UserCode}");
                            AuthenticationOutput.WriteLine();
                            AuthenticationOutput.WriteLine("Waiting for authentication...");
                        }
                        return Task.CompletedTask;
                    })
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (_preferredAuthMethod == AuthMethod.InteractiveBrowser)
            {
                // Interactive browser flow
                AuthDebugLog.WriteLine("  Starting interactive browser authentication...");
                if (!InteractiveBrowserCredentialProvider.IsAvailable())
                {
                    throw new InvalidOperationException(
                        "Interactive browser authentication is not available in this environment " +
                        "(SSH session, CI/CD, container, or no display). " +
                        "Create a profile with device code authentication using 'ppds auth create --deviceCode'.");
                }

                if (_deviceCodeCallback == null)
                {
                    AuthenticationOutput.WriteLine("Opening browser for authentication...");
                }

                result = await _msalClient!
                    .AcquireTokenInteractive(scopes)
                    .WithUseEmbeddedWebView(false)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // No valid interactive auth method configured
                // This shouldn't happen if FromProfile() validation is working, but be defensive
                throw new InvalidOperationException(
                    $"No valid authentication method configured for Global Discovery. " +
                    $"Auth method '{_preferredAuthMethod}' is not supported. " +
                    $"Use InteractiveBrowser or DeviceCode authentication.");
            }

            if (_deviceCodeCallback == null)
            {
                AuthenticationOutput.WriteLine($"Authenticated as: {result.Account.Username}");
                AuthenticationOutput.WriteLine();
            }

            // Capture HomeAccountId for persistence by caller
            // This enables silent auth on subsequent discovery calls
            _capturedHomeAccountId = result.Account.HomeAccountId?.Identifier;

            return result.AccessToken;
        };
    }

    /// <summary>
    /// Finds the correct cached account for this profile.
    /// </summary>
    private Task<IAccount?> FindAccountAsync()
        => MsalAccountHelper.FindAccountAsync(_msalClient!, _homeAccountId, _tenantId);

    /// <summary>
    /// Ensures the MSAL client is initialized with token cache.
    /// </summary>
    private async Task EnsureMsalClientInitializedAsync()
    {
        if (_msalClient != null)
            return;

        // Always use "organizations" (multi-tenant) authority for discovery.
        // This ensures tokens cached during profile creation (also using "organizations")
        // can be reused. Tenant-specific authority is only needed for environment connections.
        _msalClient = MsalClientBuilder.CreateClient(_cloud, tenantId: null, MsalClientBuilder.RedirectUriOption.Default);
        _cacheHelper = await MsalClientBuilder.CreateAndRegisterCacheAsync(_msalClient, warnOnFailure: false).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        MsalClientBuilder.UnregisterCache(_cacheHelper, _msalClient);
        _disposed = true;
    }
}
