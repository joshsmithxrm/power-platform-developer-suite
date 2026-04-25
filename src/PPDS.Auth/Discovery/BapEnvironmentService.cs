using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Discovery;

/// <summary>
/// Discovers Dataverse environments via the BAP (Business Application Platform) admin API.
/// Suitable for service principal authentication where Global Discovery is not available.
/// </summary>
public sealed class BapEnvironmentService : IEnvironmentDiscoveryService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _bapApiUrl;
    private readonly Func<CancellationToken, Task<string>> _tokenProvider;
    private bool _disposed;

    private const string EnvironmentsEndpoint =
        "/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01";

    private static readonly AuthMethod[] SupportedAuthMethods =
    {
        AuthMethod.ClientSecret,
        AuthMethod.CertificateFile,
        AuthMethod.CertificateStore
    };

    /// <summary>
    /// Creates a new BapEnvironmentService.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for API calls.</param>
    /// <param name="bapApiUrl">The BAP API base URL (e.g., https://api.bap.microsoft.com).</param>
    /// <param name="tokenProvider">A function that acquires a bearer token for the BAP API.</param>
    public BapEnvironmentService(
        HttpClient httpClient,
        string bapApiUrl,
        Func<CancellationToken, Task<string>> tokenProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _bapApiUrl = bapApiUrl ?? throw new ArgumentNullException(nameof(bapApiUrl));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
    }

    private BapEnvironmentService(
        HttpClient httpClient,
        bool ownsHttpClient,
        string bapApiUrl,
        Func<CancellationToken, Task<string>> tokenProvider)
        : this(httpClient, bapApiUrl, tokenProvider)
    {
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>
    /// Returns whether the given auth method is supported by BAP discovery.
    /// </summary>
    public static bool SupportsAuthMethod(AuthMethod authMethod)
        => Array.IndexOf(SupportedAuthMethods, authMethod) >= 0;

    /// <summary>
    /// Creates a BapEnvironmentService from an auth profile using MSAL confidential client.
    /// </summary>
    /// <param name="profile">The auth profile (must use ClientSecret, CertificateFile, or CertificateStore).</param>
    /// <param name="credentialStore">Optional credential store for retrieving stored secrets.</param>
    /// <returns>A new BapEnvironmentService instance. Caller must dispose.</returns>
    public static async Task<BapEnvironmentService> FromProfileAsync(
        AuthProfile profile,
        ISecureCredentialStore? credentialStore = null)
    {
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));

        if (!SupportsAuthMethod(profile.AuthMethod))
        {
            throw new NotSupportedException(
                $"BAP Environment Service requires service principal authentication. " +
                $"Auth method '{profile.AuthMethod}' is not supported. " +
                $"Use ClientSecret, CertificateFile, or CertificateStore.");
        }

        var cloud = profile.Cloud;
        var bapApiUrl = CloudEndpoints.GetBapApiUrl(cloud);
        var scope = $"{bapApiUrl}/.default";

        var authority = CloudEndpoints.GetAuthorityUrl(cloud, profile.TenantId);
        Microsoft.Identity.Client.IConfidentialClientApplication msalClient;

        switch (profile.AuthMethod)
        {
            case AuthMethod.ClientSecret:
            {
                string? clientSecret = null;
                if (credentialStore != null && profile.ApplicationId != null)
                {
                    var stored = await credentialStore.GetAsync(profile.ApplicationId).ConfigureAwait(false);
                    clientSecret = stored?.ClientSecret;
                }

                clientSecret ??= CredentialProviderFactory.GetSpnSecretFromEnvironment();

                if (string.IsNullOrEmpty(clientSecret))
                {
                    throw new AuthenticationException(
                        "Client secret not found. Store the secret or set PPDS_SPN_SECRET.",
                        AuthErrorCodes.BapApiError);
                }

                msalClient = Microsoft.Identity.Client.ConfidentialClientApplicationBuilder
                    .Create(profile.ApplicationId)
                    .WithAuthority(authority)
                    .WithClientSecret(clientSecret)
                    .Build();
                break;
            }

            case AuthMethod.CertificateFile:
            {
                if (string.IsNullOrEmpty(profile.CertificatePath))
                    throw new AuthenticationException("Certificate path not configured.", AuthErrorCodes.BapApiError);

                string? certPassword = null;
                if (credentialStore != null && profile.ApplicationId != null)
                {
                    var stored = await credentialStore.GetAsync(profile.ApplicationId).ConfigureAwait(false);
                    certPassword = stored?.CertificatePassword;
                }

                // MSAL retains a reference to the cert — it manages its own copy internally
                var cert = new X509Certificate2(profile.CertificatePath, certPassword);

                msalClient = Microsoft.Identity.Client.ConfidentialClientApplicationBuilder
                    .Create(profile.ApplicationId)
                    .WithAuthority(authority)
                    .WithCertificate(cert)
                    .Build();
                break;
            }

            case AuthMethod.CertificateStore:
            {
                if (string.IsNullOrEmpty(profile.CertificateThumbprint))
                    throw new AuthenticationException("Certificate thumbprint not configured.", AuthErrorCodes.BapApiError);

                var storeName = StoreName.My;
                var storeLocation = StoreLocation.CurrentUser;

                if (!string.IsNullOrEmpty(profile.CertificateStoreName))
                    Enum.TryParse(profile.CertificateStoreName, out storeName);
                if (!string.IsNullOrEmpty(profile.CertificateStoreLocation))
                    Enum.TryParse(profile.CertificateStoreLocation, out storeLocation);

                using var store = new X509Store(storeName, storeLocation);
                store.Open(OpenFlags.ReadOnly);
                var certs = store.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    profile.CertificateThumbprint, false);

                if (certs.Count == 0)
                    throw new AuthenticationException(
                        $"Certificate with thumbprint '{profile.CertificateThumbprint}' not found in store.",
                        AuthErrorCodes.BapApiError);

                msalClient = Microsoft.Identity.Client.ConfidentialClientApplicationBuilder
                    .Create(profile.ApplicationId)
                    .WithAuthority(authority)
                    .WithCertificate(certs[0])
                    .Build();
                break;
            }

            default:
                throw new NotSupportedException(
                    $"BAP Environment Service requires service principal authentication. " +
                    $"Auth method '{profile.AuthMethod}' is not supported. " +
                    $"Use ClientSecret, CertificateFile, or CertificateStore.");
        }

        var httpClient = new HttpClient();
        var capturedScope = scope;
        var capturedMsalClient = msalClient;

        return new BapEnvironmentService(
            httpClient,
            ownsHttpClient: true,
            bapApiUrl,
            async ct =>
            {
                var result = await capturedMsalClient
                    .AcquireTokenForClient(new[] { capturedScope })
                    .ExecuteAsync(ct)
                    .ConfigureAwait(false);
                return result.AccessToken;
            });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverEnvironmentsAsync(
        CancellationToken cancellationToken = default)
    {
        var token = await _tokenProvider(cancellationToken).ConfigureAwait(false);

        var requestUrl = $"{_bapApiUrl.TrimEnd('/')}{EnvironmentsEndpoint}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthenticationException(
                "BAP API request timed out.", AuthErrorCodes.BapApiTimeout, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new AuthenticationException(
                    "SPN not registered as Power Platform management app. " +
                    "Run 'New-PowerAppManagementApp -ApplicationId {appId}' in PowerShell to register.",
                    AuthErrorCodes.BapApiForbidden);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AuthenticationException(
                    "Unauthorized. The token may be expired or the SPN lacks permissions for the BAP API.",
                    AuthErrorCodes.BapApiUnauthorized);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(
#if NET8_0_OR_GREATER
                    cancellationToken
#endif
                ).ConfigureAwait(false);
                throw new AuthenticationException(
                    $"BAP API request failed with status {(int)response.StatusCode}: {errorBody}",
                    AuthErrorCodes.BapApiError);
            }

            var json = await response.Content.ReadAsStringAsync(
#if NET8_0_OR_GREATER
                cancellationToken
#endif
            ).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("value", out var valueArray))
            {
                return Array.Empty<DiscoveredEnvironment>();
            }

            var environments = new List<DiscoveredEnvironment>();

            foreach (var item in valueArray.EnumerateArray())
            {
                if (!item.TryGetProperty("properties", out var properties))
                    continue;

                if (!properties.TryGetProperty("linkedEnvironmentMetadata", out var linked))
                    continue;

                var env = new DiscoveredEnvironment
                {
                    EnvironmentId = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null,
                    FriendlyName = linked.TryGetProperty("friendlyName", out var fnEl)
                        ? fnEl.GetString() ?? string.Empty
                        : (properties.TryGetProperty("displayName", out var dnEl) ? dnEl.GetString() ?? string.Empty : string.Empty),
                    ApiUrl = linked.TryGetProperty("instanceUrl", out var urlEl) ? urlEl.GetString() ?? string.Empty : string.Empty,
                    UniqueName = linked.TryGetProperty("uniqueName", out var unEl) ? unEl.GetString() ?? string.Empty : string.Empty,
                    UrlName = linked.TryGetProperty("domainName", out var domEl) ? domEl.GetString() : null,
                    Version = linked.TryGetProperty("version", out var verEl) ? verEl.GetString() : null,
                    Region = properties.TryGetProperty("azureRegion", out var regEl) ? regEl.GetString() : null,
                };

                if (linked.TryGetProperty("resourceId", out var ridEl))
                {
                    var ridStr = ridEl.GetString();
                    if (!string.IsNullOrEmpty(ridStr) && Guid.TryParse(ridStr, out var ridGuid))
                        env.Id = ridGuid;
                }

                if (linked.TryGetProperty("instanceState", out var stateEl))
                {
                    var stateStr = stateEl.GetString();
                    env.State = string.Equals(stateStr, "Ready", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                }

                if (properties.TryGetProperty("tenantId", out var tidEl))
                {
                    var tidStr = tidEl.GetString();
                    if (!string.IsNullOrEmpty(tidStr) && Guid.TryParse(tidStr, out var tidGuid))
                        env.TenantId = tidGuid;
                }

                if (properties.TryGetProperty("environmentSku", out var skuEl))
                {
                    var sku = skuEl.GetString();
                    env.OrganizationType = MapEnvironmentSku(sku);
                }

                environments.Add(env);
            }

            return environments.OrderBy(e => e.FriendlyName).ToList();
        }
    }

    private static int MapEnvironmentSku(string? sku)
    {
        return sku switch
        {
            "Production" => 0,
            "Sandbox" => 5,
            "Developer" => 6,
            "Trial" => 11,
            "Default" => 12,
            _ => 0
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsHttpClient) _httpClient.Dispose();
        _disposed = true;
    }
}
