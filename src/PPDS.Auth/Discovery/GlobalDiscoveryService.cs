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
    /// <summary>
    /// Microsoft's well-known public client ID for development/prototyping with Dataverse.
    /// </summary>
    private const string MicrosoftPublicClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    private readonly CloudEnvironment _cloud;
    private readonly string? _tenantId;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;

    private IPublicClientApplication? _msalClient;
    private MsalCacheHelper? _cacheHelper;
    private bool _disposed;

    /// <summary>
    /// Creates a new GlobalDiscoveryService.
    /// </summary>
    /// <param name="cloud">The cloud environment to use.</param>
    /// <param name="tenantId">Optional tenant ID.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    public GlobalDiscoveryService(
        CloudEnvironment cloud = CloudEnvironment.Public,
        string? tenantId = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        _cloud = cloud;
        _tenantId = tenantId;
        _deviceCodeCallback = deviceCodeCallback;
    }

    /// <summary>
    /// Creates a GlobalDiscoveryService from an auth profile.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    /// <returns>A new service instance.</returns>
    public static GlobalDiscoveryService FromProfile(
        AuthProfile profile,
        Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        return new GlobalDiscoveryService(
            profile.Cloud,
            profile.TenantId,
            deviceCodeCallback);
    }

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
            // Get the web API endpoint from the Endpoints dictionary
            string apiUrl = string.Empty;
            if (org.Endpoints.TryGetValue(Microsoft.Xrm.Sdk.Discovery.EndpointType.WebApplication, out var webAppUrl))
            {
                apiUrl = webAppUrl;
            }

            // Get the application URL
            string? appUrl = null;
            if (org.Endpoints.TryGetValue(Microsoft.Xrm.Sdk.Discovery.EndpointType.WebApplication, out var webUrl))
            {
                appUrl = webUrl;
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
                ApiUrl = apiUrl,
                Url = appUrl,
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

            // Try silent acquisition first
            var accounts = await _msalClient!.GetAccountsAsync().ConfigureAwait(false);
            var account = accounts.FirstOrDefault();

            if (account != null)
            {
                try
                {
                    var silentResult = await _msalClient
                        .AcquireTokenSilent(scopes, account)
                        .ExecuteAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return silentResult.AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // Need interactive auth
                }
            }

            // Fall back to device code flow
            var result = await _msalClient
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
                Console.WriteLine($"Authenticated as: {result.Account.Username}");
                Console.WriteLine();
            }

            return result.AccessToken;
        };
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
                .WithUnprotectedFile()
                .Build();

            _cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
            _cacheHelper.RegisterCache(_msalClient.UserTokenCache);
        }
        catch (MsalCachePersistenceException)
        {
            // Continue without persistent cache
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
