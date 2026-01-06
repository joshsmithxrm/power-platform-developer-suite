using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Provides access tokens for Power Platform REST APIs using MSAL.
/// Supports user-delegated (interactive, device code) and application (client credentials) flows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>SPN Limitations:</strong> Service principals (client credentials) can access Power Platform
/// Admin APIs but have limited functionality compared to user-delegated tokens:
/// </para>
/// <list type="bullet">
/// <item>Connections API requires user context - SPNs cannot list or manage connections</item>
/// <item>Some flow operations require the owning user's context</item>
/// </list>
/// <para>
/// For full functionality, use interactive or device code authentication.
/// </para>
/// </remarks>
public sealed class PowerPlatformTokenProvider : IPowerPlatformTokenProvider
{
    private readonly CloudEnvironment _cloud;
    private readonly string? _tenantId;
    private readonly string? _username;
    private readonly string? _homeAccountId;

    // Client credentials for SPN auth
    private readonly string? _applicationId;
    private readonly string? _clientSecret;
    private readonly TokenCredential? _clientCredential;

    // MSAL for user-delegated auth
    private IPublicClientApplication? _msalClient;
    private MsalCacheHelper? _cacheHelper;

    private bool _disposed;

    /// <summary>
    /// Creates a provider for user-delegated authentication (interactive/device code).
    /// </summary>
    /// <param name="cloud">The cloud environment.</param>
    /// <param name="tenantId">Optional tenant ID.</param>
    /// <param name="username">Optional username for silent auth lookup.</param>
    /// <param name="homeAccountId">Optional MSAL home account identifier.</param>
    public PowerPlatformTokenProvider(
        CloudEnvironment cloud = CloudEnvironment.Public,
        string? tenantId = null,
        string? username = null,
        string? homeAccountId = null)
    {
        _cloud = cloud;
        _tenantId = tenantId;
        _username = username;
        _homeAccountId = homeAccountId;
    }

    /// <summary>
    /// Creates a provider for service principal (client credentials) authentication.
    /// </summary>
    /// <param name="applicationId">The application (client) ID.</param>
    /// <param name="clientSecret">The client secret.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cloud">The cloud environment.</param>
    /// <remarks>
    /// Service principals have limited Power Platform API access.
    /// See class remarks for details on limitations.
    /// </remarks>
    public PowerPlatformTokenProvider(
        string applicationId,
        string clientSecret,
        string tenantId,
        CloudEnvironment cloud = CloudEnvironment.Public)
    {
        _applicationId = applicationId ?? throw new ArgumentNullException(nameof(applicationId));
        _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
        _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        _cloud = cloud;

        // Create Azure.Identity credential for client credentials flow
        var options = new ClientSecretCredentialOptions
        {
            AuthorityHost = CloudEndpoints.GetAuthorityHost(cloud)
        };
        _clientCredential = new ClientSecretCredential(tenantId, applicationId, clientSecret, options);
    }

    /// <summary>
    /// Creates a provider from an auth profile for user-delegated authentication.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <returns>A new provider instance.</returns>
    /// <exception cref="ArgumentException">If the profile uses an unsupported auth method.</exception>
    public static PowerPlatformTokenProvider FromProfile(AuthProfile profile)
    {
        return profile.AuthMethod switch
        {
            AuthMethod.InteractiveBrowser or AuthMethod.DeviceCode =>
                new PowerPlatformTokenProvider(profile.Cloud, profile.TenantId, profile.Username, profile.HomeAccountId),

            AuthMethod.ClientSecret =>
                throw new ArgumentException(
                    "Cannot create user-delegated token provider from ClientSecret profile. " +
                    "Use FromProfileWithSecret() for SPN authentication.",
                    nameof(profile)),

            _ => throw new ArgumentException(
                $"Auth method {profile.AuthMethod} is not supported for Power Platform API tokens. " +
                "Supported methods: InteractiveBrowser, DeviceCode, ClientSecret.",
                nameof(profile))
        };
    }

    /// <summary>
    /// Creates a provider from an auth profile with client secret for SPN authentication.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <param name="clientSecret">The client secret.</param>
    /// <returns>A new provider instance.</returns>
    /// <remarks>
    /// Service principals have limited Power Platform API access.
    /// See class remarks for details on limitations.
    /// </remarks>
    public static PowerPlatformTokenProvider FromProfileWithSecret(AuthProfile profile, string clientSecret)
    {
        if (profile.AuthMethod != AuthMethod.ClientSecret)
            throw new ArgumentException($"Profile auth method must be ClientSecret, got {profile.AuthMethod}", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.ApplicationId))
            throw new ArgumentException("Profile ApplicationId is required", nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.TenantId))
            throw new ArgumentException("Profile TenantId is required", nameof(profile));

        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new ArgumentException("Client secret is required", nameof(clientSecret));

        return new PowerPlatformTokenProvider(profile.ApplicationId, clientSecret, profile.TenantId, profile.Cloud);
    }

    /// <inheritdoc />
    public Task<PowerPlatformToken> GetPowerAppsTokenAsync(CancellationToken cancellationToken = default)
    {
        var resource = CloudEndpoints.GetPowerAppsApiUrl(_cloud);
        return GetTokenForResourceAsync(resource, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PowerPlatformToken> GetPowerAutomateTokenAsync(CancellationToken cancellationToken = default)
    {
        var resource = CloudEndpoints.GetPowerAutomateApiUrl(_cloud);
        return GetTokenForResourceAsync(resource, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PowerPlatformToken> GetFlowApiTokenAsync(CancellationToken cancellationToken = default)
    {
        // The Flow API and Connections API require tokens with the service.powerapps.com scope,
        // not the api.flow.microsoft.com scope. This is a Microsoft API design quirk.
        var resource = CloudEndpoints.GetPowerAppsServiceScope(_cloud);
        return GetTokenForResourceAsync(resource, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PowerPlatformToken> GetTokenForResourceAsync(string resource, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resource))
            throw new ArgumentNullException(nameof(resource));

        // Use client credentials flow for SPN
        if (_clientCredential != null)
        {
            return await GetTokenWithClientCredentialsAsync(resource, cancellationToken).ConfigureAwait(false);
        }

        // Use MSAL for user-delegated auth
        return await GetTokenWithMsalAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PowerPlatformToken> GetTokenWithClientCredentialsAsync(string resource, CancellationToken cancellationToken)
    {
        var scopes = new[] { $"{resource}/.default" };

        try
        {
            var tokenRequest = new TokenRequestContext(scopes);
            var token = await _clientCredential!.GetTokenAsync(tokenRequest, cancellationToken).ConfigureAwait(false);

            return new PowerPlatformToken
            {
                AccessToken = token.Token,
                ExpiresOn = token.ExpiresOn,
                Resource = resource,
                Identity = _applicationId
            };
        }
        catch (AuthenticationFailedException ex)
        {
            throw new AuthenticationException($"Failed to acquire token for {resource}: {ex.Message}", ex);
        }
    }

    private async Task<PowerPlatformToken> GetTokenWithMsalAsync(string resource, CancellationToken cancellationToken)
    {
        await EnsureMsalClientInitializedAsync().ConfigureAwait(false);

        var scopes = new[] { $"{resource}/.default" };
        AuthenticationResult result;

        // Try silent acquisition first
        var account = await MsalAccountHelper.FindAccountAsync(_msalClient!, _homeAccountId, _tenantId, _username).ConfigureAwait(false);

        if (account != null)
        {
            try
            {
                result = await _msalClient!
                    .AcquireTokenSilent(scopes, account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);

                return new PowerPlatformToken
                {
                    AccessToken = result.AccessToken,
                    ExpiresOn = result.ExpiresOn,
                    Resource = resource,
                    Identity = result.Account?.Username
                };
            }
            catch (MsalUiRequiredException)
            {
                // Silent failed, need interactive
            }
        }

        // Try interactive browser if available
        if (InteractiveBrowserCredentialProvider.IsAvailable())
        {
            AuthenticationOutput.WriteLine();
            AuthenticationOutput.WriteLine($"Opening browser for authentication to {resource}...");

            try
            {
                result = await _msalClient!
                    .AcquireTokenInteractive(scopes)
                    .WithUseEmbeddedWebView(false)
                    .WithPrompt(Prompt.SelectAccount)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);

                AuthenticationOutput.WriteLine($"Authenticated as: {result.Account?.Username}");
                AuthenticationOutput.WriteLine();

                return new PowerPlatformToken
                {
                    AccessToken = result.AccessToken,
                    ExpiresOn = result.ExpiresOn,
                    Resource = resource,
                    Identity = result.Account?.Username
                };
            }
            catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
            {
                throw new OperationCanceledException("Authentication was canceled by the user.", ex);
            }
        }

        // Fall back to device code
        AuthenticationOutput.WriteLine();
        AuthenticationOutput.WriteLine($"Authentication required for {resource}");

        result = await _msalClient!
            .AcquireTokenWithDeviceCode(scopes, deviceCodeResult =>
            {
                AuthenticationOutput.WriteLine();
                AuthenticationOutput.WriteLine("To sign in, use a web browser to open the page:");
                AuthenticationOutput.WriteLine($"  {deviceCodeResult.VerificationUrl}");
                AuthenticationOutput.WriteLine();
                AuthenticationOutput.WriteLine("Enter the code:");
                AuthenticationOutput.WriteLine($"  {deviceCodeResult.UserCode}");
                AuthenticationOutput.WriteLine();
                AuthenticationOutput.WriteLine("Waiting for authentication...");
                return Task.CompletedTask;
            })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        AuthenticationOutput.WriteLine($"Authenticated as: {result.Account?.Username}");
        AuthenticationOutput.WriteLine();

        return new PowerPlatformToken
        {
            AccessToken = result.AccessToken,
            ExpiresOn = result.ExpiresOn,
            Resource = resource,
            Identity = result.Account?.Username
        };
    }

    private async Task EnsureMsalClientInitializedAsync()
    {
        if (_msalClient != null)
            return;

        _msalClient = MsalClientBuilder.CreateClient(_cloud, _tenantId, MsalClientBuilder.RedirectUriOption.Localhost);
        _cacheHelper = await MsalClientBuilder.CreateAndRegisterCacheAsync(_msalClient).ConfigureAwait(false);
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
