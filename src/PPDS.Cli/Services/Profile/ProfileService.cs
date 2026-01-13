using System.Text.RegularExpressions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Profile;

/// <summary>
/// Application service for managing authentication profiles.
/// </summary>
/// <remarks>
/// See ADR-0015 for architectural context.
/// </remarks>
public sealed class ProfileService : IProfileService
{
    /// <summary>
    /// Pattern for valid profile names.
    /// </summary>
    private static readonly Regex ProfileNamePattern = new(
        @"^[a-zA-Z0-9](?:[a-zA-Z0-9 _-]*[a-zA-Z0-9])?$",
        RegexOptions.Compiled);

    private readonly ProfileStore _store;
    private readonly ILogger<ProfileService> _logger;

    /// <summary>
    /// Creates a new profile service.
    /// </summary>
    public ProfileService(ProfileStore store, ILogger<ProfileService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var collection = await _store.LoadAsync(cancellationToken);
        var activeIndex = collection.ActiveProfile?.Index;

        return collection.All
            .Select(p => ProfileSummary.FromAuthProfile(p, p.Index == activeIndex))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ProfileSummary?> GetActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        var collection = await _store.LoadAsync(cancellationToken);
        var active = collection.ActiveProfile;

        return active != null
            ? ProfileSummary.FromAuthProfile(active, isActive: true)
            : null;
    }

    /// <inheritdoc />
    public async Task<ProfileSummary> SetActiveProfileAsync(string nameOrIndex, CancellationToken cancellationToken = default)
    {
        var collection = await _store.LoadAsync(cancellationToken);
        var profile = collection.GetByNameOrIndex(nameOrIndex);

        if (profile == null)
        {
            throw new PpdsNotFoundException("Profile", nameOrIndex);
        }

        collection.SetActiveByIndex(profile.Index);
        await _store.SaveAsync(collection, cancellationToken);

        _logger.LogInformation("Set active profile to {ProfileIdentifier}", profile.DisplayIdentifier);

        return ProfileSummary.FromAuthProfile(profile, isActive: true);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteProfileAsync(string nameOrIndex, CancellationToken cancellationToken = default)
    {
        var collection = await _store.LoadAsync(cancellationToken);
        var profile = collection.GetByNameOrIndex(nameOrIndex);

        if (profile == null)
        {
            return false;
        }

        // Clean up stored credentials for this profile (only if no other profiles share them)
        await CleanupCredentialsAsync(collection, profile, cancellationToken);

        collection.RemoveByIndex(profile.Index);
        await _store.SaveAsync(collection, cancellationToken);

        _logger.LogInformation("Deleted profile {ProfileIdentifier}", profile.DisplayIdentifier);

        return true;
    }

    /// <inheritdoc />
    public async Task<ProfileSummary> UpdateProfileAsync(
        string nameOrIndex,
        string? newName = null,
        string? newEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName) && string.IsNullOrWhiteSpace(newEnvironment))
        {
            throw new PpdsValidationException("update", "At least one update (name or environment) must be specified.");
        }

        var collection = await _store.LoadAsync(cancellationToken);
        var profile = collection.GetByNameOrIndex(nameOrIndex);

        if (profile == null)
        {
            throw new PpdsNotFoundException("Profile", nameOrIndex);
        }

        // Update name if specified
        if (!string.IsNullOrWhiteSpace(newName))
        {
            ValidateProfileName(newName);

            if (collection.IsNameInUse(newName, profile.Index))
            {
                throw new PpdsValidationException("name", $"Profile name '{newName}' is already in use.");
            }

            profile.Name = newName;
        }

        // Update environment if specified
        if (!string.IsNullOrWhiteSpace(newEnvironment))
        {
            using var credentialStore = new NativeCredentialStore();
            using var resolver = new EnvironmentResolutionService(profile, credentialStore: credentialStore);
            var result = await resolver.ResolveAsync(newEnvironment, cancellationToken);

            if (!result.Success)
            {
                throw new PpdsException(ErrorCodes.Connection.EnvironmentNotFound, result.ErrorMessage ?? "Environment not found.");
            }

            profile.Environment = result.Environment;
        }

        await _store.SaveAsync(collection, cancellationToken);

        _logger.LogInformation("Updated profile {ProfileIdentifier}", profile.DisplayIdentifier);

        var isActive = collection.ActiveProfile?.Index == profile.Index;
        return ProfileSummary.FromAuthProfile(profile, isActive);
    }

    /// <inheritdoc />
    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        _store.Delete();

        // Clear MSAL token cache in the same directory as the profile store
        // This ensures tests using isolated directories don't nuke the global cache
        var storeDirectory = Path.GetDirectoryName(_store.FilePath);
        var tokenCachePath = storeDirectory != null
            ? Path.Combine(storeDirectory, ProfilePaths.TokenCacheFileName)
            : null; // null = use global default
        await TokenCacheManager.ClearAllCachesAsync(tokenCachePath);

        // Clear secure credential store
        using var credentialStore = new NativeCredentialStore();
        await credentialStore.ClearAsync(cancellationToken);

        _logger.LogInformation("Cleared all profiles and credentials");
    }

    /// <inheritdoc />
    public async Task SetEnvironmentAsync(
        string? nameOrIndex,
        string environmentUrl,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentUrl);

        var collection = await _store.LoadAsync(cancellationToken);

        AuthProfile? profile;
        if (string.IsNullOrWhiteSpace(nameOrIndex))
        {
            profile = collection.ActiveProfile;
            if (profile == null)
            {
                throw new PpdsNotFoundException("Profile", "active profile");
            }
        }
        else
        {
            profile = collection.GetByNameOrIndex(nameOrIndex);
            if (profile == null)
            {
                throw new PpdsNotFoundException("Profile", nameOrIndex);
            }
        }

        profile.Environment = new EnvironmentInfo
        {
            Url = environmentUrl.TrimEnd('/'),
            DisplayName = displayName ?? ExtractEnvironmentName(environmentUrl)
        };

        await _store.SaveAsync(collection, cancellationToken);

        _logger.LogInformation("Set environment for profile {ProfileIdentifier} to {EnvironmentUrl}",
            profile.DisplayIdentifier, environmentUrl);
    }

    /// <inheritdoc />
    public async Task<ProfileSummary> CreateProfileAsync(
        ProfileCreateRequest request,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default)
    {
        // Validate request
        var authMethod = DetermineAuthMethod(request);
        ValidateCreateRequest(request, authMethod);

        // Check if name is in use
        var collection = await _store.LoadAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            ValidateProfileName(request.Name);
            if (collection.IsNameInUse(request.Name))
            {
                throw new PpdsValidationException("name", $"Profile name '{request.Name}' is already in use.");
            }
        }

        // Parse cloud environment
        if (!Enum.TryParse<CloudEnvironment>(request.Cloud, ignoreCase: true, out var cloud))
        {
            cloud = CloudEnvironment.Public;
        }

        // Check for environment variable bypass (PPDS_SPN_SECRET or PPDS_TEST_CLIENT_SECRET)
        var bypassCredentialStore = CredentialProviderFactory.ShouldBypassCredentialStore();

        NativeCredentialStore? credentialStore = null;
        string? storedCredentialKey = null;

        try
        {
            // Build profile
            var profile = new AuthProfile
            {
                Name = request.Name,
                AuthMethod = authMethod,
                Cloud = cloud,
                TenantId = request.TenantId,
                ApplicationId = request.ApplicationId,
                CertificatePath = request.CertificatePath,
                CertificateThumbprint = request.CertificateThumbprint
            };

            // Store credentials if not bypassing
            if (!bypassCredentialStore)
            {
                credentialStore = new NativeCredentialStore(allowCleartextFallback: request.AcceptCleartextCaching);

                if (!string.IsNullOrWhiteSpace(request.ApplicationId))
                {
                    var storedCredential = new StoredCredential
                    {
                        ApplicationId = request.ApplicationId,
                        ClientSecret = request.ClientSecret,
                        CertificatePath = request.CertificatePath,
                        CertificatePassword = request.CertificatePassword
                    };
                    await credentialStore.StoreAsync(storedCredential, cancellationToken);
                    storedCredentialKey = request.ApplicationId;
                }
            }

            // Determine target URL for authentication
            var isServicePrincipal = authMethod is AuthMethod.ClientSecret or AuthMethod.CertificateFile
                or AuthMethod.CertificateStore or AuthMethod.GitHubFederated or AuthMethod.AzureDevOpsFederated;

            string targetUrl;
            if (isServicePrincipal)
            {
                if (string.IsNullOrWhiteSpace(request.Environment))
                {
                    await CleanupStoredCredentialAsync(credentialStore, storedCredentialKey, cancellationToken);
                    throw new PpdsValidationException("environment", $"--environment is required for {authMethod} authentication.");
                }

                if (!request.Environment.Contains("://"))
                {
                    await CleanupStoredCredentialAsync(credentialStore, storedCredentialKey, cancellationToken);
                    throw new PpdsValidationException("environment",
                        "Service principals require a full environment URL (e.g., https://org.crm.dynamics.com).");
                }

                targetUrl = request.Environment;
            }
            else
            {
                targetUrl = "https://globaldisco.crm.dynamics.com";
            }

            // Create credential provider
            using ICredentialProvider provider = CreateCredentialProvider(request, authMethod, cloud, deviceCodeCallback);

            try
            {
                // Authenticate
                using var client = await provider.CreateServiceClientAsync(targetUrl, cancellationToken, forceInteractive: true);

                // Populate profile from auth result
                profile.Username = provider.Identity;
                profile.ObjectId = provider.ObjectId;
                profile.HomeAccountId = provider.HomeAccountId;

                if (string.IsNullOrEmpty(profile.TenantId) && !string.IsNullOrEmpty(provider.TenantId))
                {
                    profile.TenantId = provider.TenantId;
                }

                if (!string.IsNullOrEmpty(profile.TenantId))
                {
                    profile.Authority = CloudEndpoints.GetAuthorityUrl(profile.Cloud, profile.TenantId);
                }

                var claims = JwtClaimsParser.Parse(provider.IdTokenClaims, provider.AccessToken);
                if (claims != null)
                {
                    profile.Puid = claims.Puid;
                }

                // Resolve environment if specified
                if (!string.IsNullOrWhiteSpace(request.Environment))
                {
                    await ResolveEnvironmentAsync(profile, request.Environment, isServicePrincipal, provider, client, cloud, authMethod, cancellationToken);
                }
            }
            catch (AuthenticationException ex)
            {
                await CleanupStoredCredentialAsync(credentialStore, storedCredentialKey, cancellationToken);
                throw new PpdsAuthException(ErrorCodes.Auth.InvalidCredentials, $"Authentication failed: {ex.Message}", ex);
            }

            // Add to collection
            collection.Add(profile);
            await _store.SaveAsync(collection, cancellationToken);

            _logger.LogInformation("Created profile {ProfileIdentifier}", profile.DisplayIdentifier);

            var isActive = collection.ActiveProfile?.Index == profile.Index;
            return ProfileSummary.FromAuthProfile(profile, isActive);
        }
        finally
        {
            credentialStore?.Dispose();
        }
    }

    #region Private Helpers

    private static void ValidateProfileName(string name)
    {
        if (name.Length > 30)
        {
            throw new PpdsValidationException("name", "Profile name cannot exceed 30 characters.");
        }

        if (!ProfileNamePattern.IsMatch(name))
        {
            throw new PpdsValidationException("name",
                "Profile name must start with a letter or number and contain only letters, numbers, spaces, hyphens, or underscores.");
        }
    }

    private static AuthMethod DetermineAuthMethod(ProfileCreateRequest request)
    {
        if (request.UseGitHubFederated)
            return AuthMethod.GitHubFederated;

        if (request.UseAzureDevOpsFederated)
            return AuthMethod.AzureDevOpsFederated;

        if (request.UseManagedIdentity)
            return AuthMethod.ManagedIdentity;

        if (!string.IsNullOrWhiteSpace(request.CertificateThumbprint))
            return AuthMethod.CertificateStore;

        if (!string.IsNullOrWhiteSpace(request.CertificatePath))
            return AuthMethod.CertificateFile;

        if (!string.IsNullOrWhiteSpace(request.ClientSecret))
            return AuthMethod.ClientSecret;

        if (request.UseDeviceCode)
            return AuthMethod.DeviceCode;

        return InteractiveBrowserCredentialProvider.IsAvailable()
            ? AuthMethod.InteractiveBrowser
            : AuthMethod.DeviceCode;
    }

    private static void ValidateCreateRequest(ProfileCreateRequest request, AuthMethod authMethod)
    {
        var errors = new List<ValidationError>();

        switch (authMethod)
        {
            case AuthMethod.ClientSecret:
                if (string.IsNullOrWhiteSpace(request.ApplicationId))
                    errors.Add(new ValidationError("applicationId", "Application ID is required for client secret authentication."));
                if (string.IsNullOrWhiteSpace(request.ClientSecret))
                    errors.Add(new ValidationError("clientSecret", "Client secret is required."));
                if (string.IsNullOrWhiteSpace(request.TenantId))
                    errors.Add(new ValidationError("tenantId", "Tenant ID is required for client secret authentication."));
                break;

            case AuthMethod.CertificateFile:
                if (string.IsNullOrWhiteSpace(request.ApplicationId))
                    errors.Add(new ValidationError("applicationId", "Application ID is required for certificate authentication."));
                if (string.IsNullOrWhiteSpace(request.CertificatePath))
                    errors.Add(new ValidationError("certificatePath", "Certificate path is required."));
                if (string.IsNullOrWhiteSpace(request.TenantId))
                    errors.Add(new ValidationError("tenantId", "Tenant ID is required for certificate authentication."));
                if (!string.IsNullOrWhiteSpace(request.CertificatePath) && !File.Exists(request.CertificatePath))
                    errors.Add(new ValidationError("certificatePath", $"Certificate file not found: {request.CertificatePath}"));
                break;

            case AuthMethod.CertificateStore:
                if (string.IsNullOrWhiteSpace(request.ApplicationId))
                    errors.Add(new ValidationError("applicationId", "Application ID is required for certificate authentication."));
                if (string.IsNullOrWhiteSpace(request.CertificateThumbprint))
                    errors.Add(new ValidationError("certificateThumbprint", "Certificate thumbprint is required."));
                if (string.IsNullOrWhiteSpace(request.TenantId))
                    errors.Add(new ValidationError("tenantId", "Tenant ID is required for certificate authentication."));
                if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                    errors.Add(new ValidationError("certificateThumbprint", "Certificate store authentication is only supported on Windows."));
                break;

            case AuthMethod.GitHubFederated:
            case AuthMethod.AzureDevOpsFederated:
                if (string.IsNullOrWhiteSpace(request.ApplicationId))
                    errors.Add(new ValidationError("applicationId", $"Application ID is required for {authMethod} authentication."));
                if (string.IsNullOrWhiteSpace(request.TenantId))
                    errors.Add(new ValidationError("tenantId", $"Tenant ID is required for {authMethod} authentication."));
                break;
        }

        if (errors.Count > 0)
        {
            throw new PpdsValidationException(errors);
        }
    }

    private static ICredentialProvider CreateCredentialProvider(
        ProfileCreateRequest request,
        AuthMethod authMethod,
        CloudEnvironment cloud,
        Action<DeviceCodeInfo>? deviceCodeCallback)
    {
        return authMethod switch
        {
            AuthMethod.InteractiveBrowser => new InteractiveBrowserCredentialProvider(cloud, request.TenantId),
            AuthMethod.DeviceCode => new DeviceCodeCredentialProvider(cloud, request.TenantId, deviceCodeCallback: deviceCodeCallback),
            AuthMethod.ClientSecret => new ClientSecretCredentialProvider(
                request.ApplicationId!, request.ClientSecret!, request.TenantId!, cloud),
            AuthMethod.CertificateFile => new CertificateFileCredentialProvider(
                request.ApplicationId!, request.CertificatePath!, request.CertificatePassword, request.TenantId!, cloud),
            AuthMethod.CertificateStore => new CertificateStoreCredentialProvider(
                request.ApplicationId!, request.CertificateThumbprint!, request.TenantId!, cloud: cloud),
            AuthMethod.ManagedIdentity => new ManagedIdentityCredentialProvider(request.ApplicationId),
            AuthMethod.GitHubFederated => new GitHubFederatedCredentialProvider(
                request.ApplicationId!, request.TenantId!, cloud),
            AuthMethod.AzureDevOpsFederated => new AzureDevOpsFederatedCredentialProvider(
                request.ApplicationId!, request.TenantId!, cloud),
            _ => throw new PpdsException(ErrorCodes.Operation.NotSupported, $"Auth method {authMethod} is not supported for profile creation.")
        };
    }

    private static async Task ResolveEnvironmentAsync(
        AuthProfile profile,
        string environment,
        bool isServicePrincipal,
        ICredentialProvider provider,
        Microsoft.PowerPlatform.Dataverse.Client.ServiceClient client,
        CloudEnvironment cloud,
        AuthMethod authMethod,
        CancellationToken cancellationToken)
    {
        if (isServicePrincipal)
        {
            // For SPNs, get org info from authenticated client
            profile.Environment = new EnvironmentInfo
            {
                Url = environment.TrimEnd('/'),
                DisplayName = client.ConnectedOrgFriendlyName ?? ExtractEnvironmentName(environment),
                UniqueName = client.ConnectedOrgUniqueName,
                OrganizationId = client.ConnectedOrgId.ToString(),
                EnvironmentId = client.EnvironmentId
            };
        }
        else
        {
            // For interactive auth, use global discovery
            using var gds = new GlobalDiscoveryService(cloud, profile.TenantId, preferredAuthMethod: authMethod);
            var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);

            DiscoveredEnvironment? resolved;
            try
            {
                resolved = EnvironmentResolver.Resolve(environments, environment);
            }
            catch (AmbiguousMatchException ex)
            {
                throw new PpdsException(ErrorCodes.Connection.AmbiguousEnvironment, ex.Message, ex);
            }

            if (resolved == null)
            {
                throw new PpdsNotFoundException("Environment", environment);
            }

            // Validate access
            using var validationClient = await provider.CreateServiceClientAsync(
                resolved.ApiUrl, cancellationToken, forceInteractive: false);
            await validationClient.ExecuteAsync(new WhoAmIRequest(), cancellationToken);

            profile.Environment = new EnvironmentInfo
            {
                Url = resolved.ApiUrl,
                DisplayName = resolved.FriendlyName,
                UniqueName = resolved.UniqueName,
                EnvironmentId = resolved.EnvironmentId,
                OrganizationId = resolved.Id.ToString(),
                Type = resolved.EnvironmentType,
                Region = resolved.Region
            };
        }
    }

    private static string ExtractEnvironmentName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var parts = uri.Host.Split('.');
        return parts.Length > 0 ? parts[0] : url;
    }

    private static async Task CleanupStoredCredentialAsync(
        NativeCredentialStore? credentialStore,
        string? key,
        CancellationToken cancellationToken)
    {
        if (key != null && credentialStore != null)
        {
            await credentialStore.RemoveAsync(key, cancellationToken);
        }
    }

    private static async Task CleanupCredentialsAsync(
        ProfileCollection collection,
        AuthProfile profile,
        CancellationToken cancellationToken)
    {
        string? credentialKey = null;

        if (!string.IsNullOrWhiteSpace(profile.ApplicationId))
        {
            credentialKey = profile.ApplicationId;
        }
        else if (profile.AuthMethod == AuthMethod.UsernamePassword && !string.IsNullOrWhiteSpace(profile.Username))
        {
            credentialKey = profile.Username;
        }

        if (credentialKey == null) return;

        // Check if any other profiles share this credential
        var isShared = collection.All
            .Where(p => p.Index != profile.Index)
            .Any(p =>
            {
                if (!string.IsNullOrWhiteSpace(p.ApplicationId))
                    return string.Equals(credentialKey, p.ApplicationId, StringComparison.OrdinalIgnoreCase);
                if (p.AuthMethod == AuthMethod.UsernamePassword && !string.IsNullOrWhiteSpace(p.Username))
                    return string.Equals(credentialKey, p.Username, StringComparison.OrdinalIgnoreCase);
                return false;
            });

        if (!isShared)
        {
            using var credentialStore = new NativeCredentialStore();
            await credentialStore.RemoveAsync(credentialKey, cancellationToken);
        }
    }

    #endregion
}
