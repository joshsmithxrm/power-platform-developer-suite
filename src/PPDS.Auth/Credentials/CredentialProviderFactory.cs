using System;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Factory for creating credential providers from auth profiles.
/// </summary>
public static class CredentialProviderFactory
{
    /// <summary>
    /// Environment variable name for service principal secret bypass.
    /// When set, this value is used instead of looking up from secure store.
    /// </summary>
    public const string SpnSecretEnvVar = "PPDS_SPN_SECRET";

    /// <summary>
    /// Fallback environment variable for test scenarios.
    /// Checked when <see cref="SpnSecretEnvVar"/> is not set.
    /// </summary>
    public const string TestClientSecretEnvVar = "PPDS_TEST_CLIENT_SECRET";

    /// <summary>
    /// Gets the SPN secret from environment variables, checking both production and test variables.
    /// Returns null if neither is set.
    /// </summary>
    public static string? GetSpnSecretFromEnvironment() =>
        Environment.GetEnvironmentVariable(SpnSecretEnvVar)
        ?? Environment.GetEnvironmentVariable(TestClientSecretEnvVar);

    /// <summary>
    /// Returns true if credential store should be bypassed (SPN secret is available via environment).
    /// </summary>
    public static bool ShouldBypassCredentialStore() =>
        !string.IsNullOrWhiteSpace(GetSpnSecretFromEnvironment());

    /// <summary>
    /// Creates a credential provider for the specified auth profile.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <param name="credentialStore">Optional secure credential store for looking up secrets.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    /// <param name="beforeInteractiveAuth">Optional callback invoked before browser opens for interactive auth.
    /// Returns the user's choice (OpenBrowser, UseDeviceCode, or Cancel).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A credential provider for the profile's auth method.</returns>
    public static async Task<ICredentialProvider> CreateAsync(
        AuthProfile profile,
        ISecureCredentialStore? credentialStore = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null,
        CancellationToken cancellationToken = default)
    {
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));

        // Check for environment variable override for SPN secret
        var envSecret = GetSpnSecretFromEnvironment();

        return profile.AuthMethod switch
        {
            AuthMethod.InteractiveBrowser => InteractiveBrowserCredentialProvider.FromProfile(profile, deviceCodeCallback, beforeInteractiveAuth),
            AuthMethod.DeviceCode => DeviceCodeCredentialProvider.FromProfile(profile, deviceCodeCallback),
            AuthMethod.ClientSecret => await CreateClientSecretProviderAsync(
                profile, credentialStore, envSecret, cancellationToken).ConfigureAwait(false),
            AuthMethod.CertificateFile => await CreateCertificateFileProviderAsync(
                profile, credentialStore, cancellationToken).ConfigureAwait(false),
            AuthMethod.CertificateStore => CertificateStoreCredentialProvider.FromProfile(profile),
            AuthMethod.ManagedIdentity => ManagedIdentityCredentialProvider.FromProfile(profile),
            AuthMethod.GitHubFederated => new GitHubFederatedCredentialProvider(
                profile.ApplicationId!, profile.TenantId!, profile.Cloud),
            AuthMethod.AzureDevOpsFederated => new AzureDevOpsFederatedCredentialProvider(
                profile.ApplicationId!, profile.TenantId!, profile.Cloud),
            AuthMethod.UsernamePassword => await CreateUsernamePasswordProviderAsync(
                profile, credentialStore, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unknown auth method: {profile.AuthMethod}")
        };
    }

    /// <summary>
    /// Creates a credential provider synchronously.
    /// Prefer CreateAsync when possible for better performance with secure store lookups.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    /// <param name="beforeInteractiveAuth">Optional callback invoked before browser opens for interactive auth.
    /// Returns the user's choice (OpenBrowser, UseDeviceCode, or Cancel).</param>
    /// <returns>A credential provider for the profile's auth method.</returns>
    /// <remarks>
    /// This overload does not support secure credential store lookups.
    /// Use CreateAsync for full functionality.
    /// </remarks>
    public static ICredentialProvider Create(
        AuthProfile profile,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null)
    {
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));

        // Check for environment variable override for SPN secret
        var envSecret = GetSpnSecretFromEnvironment();

        return profile.AuthMethod switch
        {
            AuthMethod.InteractiveBrowser => InteractiveBrowserCredentialProvider.FromProfile(profile, deviceCodeCallback, beforeInteractiveAuth),
            AuthMethod.DeviceCode => DeviceCodeCredentialProvider.FromProfile(profile, deviceCodeCallback),
            AuthMethod.ClientSecret => CreateClientSecretProviderSync(profile, envSecret),
            AuthMethod.CertificateFile => CertificateFileCredentialProvider.FromProfile(profile, null),
            AuthMethod.CertificateStore => CertificateStoreCredentialProvider.FromProfile(profile),
            AuthMethod.ManagedIdentity => ManagedIdentityCredentialProvider.FromProfile(profile),
            AuthMethod.GitHubFederated => new GitHubFederatedCredentialProvider(
                profile.ApplicationId!, profile.TenantId!, profile.Cloud),
            AuthMethod.AzureDevOpsFederated => new AzureDevOpsFederatedCredentialProvider(
                profile.ApplicationId!, profile.TenantId!, profile.Cloud),
            AuthMethod.UsernamePassword => throw new InvalidOperationException(
                "UsernamePassword requires secure credential store. Use CreateAsync instead."),
            _ => throw new NotSupportedException($"Unknown auth method: {profile.AuthMethod}")
        };
    }

    private static async Task<ICredentialProvider> CreateClientSecretProviderAsync(
        AuthProfile profile,
        ISecureCredentialStore? credentialStore,
        string? envSecret,
        CancellationToken cancellationToken)
    {
        // Environment variable takes priority over secure store
        if (!string.IsNullOrWhiteSpace(envSecret))
        {
            return ClientSecretCredentialProvider.FromProfileWithSecret(profile, envSecret);
        }

        // Look up from secure store
        if (credentialStore != null && !string.IsNullOrWhiteSpace(profile.ApplicationId))
        {
            var credential = await credentialStore.GetAsync(profile.ApplicationId, cancellationToken).ConfigureAwait(false);
            if (credential != null && !string.IsNullOrWhiteSpace(credential.ClientSecret))
            {
                return ClientSecretCredentialProvider.FromProfile(profile, credential);
            }
        }

        throw new InvalidOperationException(
            $"No client secret found for application '{profile.ApplicationId}'. " +
            $"Set {SpnSecretEnvVar} environment variable or create a profile with 'ppds auth create'.");
    }

    private static ICredentialProvider CreateClientSecretProviderSync(AuthProfile profile, string? envSecret)
    {
        // Environment variable takes priority
        if (!string.IsNullOrWhiteSpace(envSecret))
        {
            return ClientSecretCredentialProvider.FromProfileWithSecret(profile, envSecret);
        }

        throw new InvalidOperationException(
            $"No client secret found for application '{profile.ApplicationId}'. " +
            $"Set {SpnSecretEnvVar} environment variable or use CreateAsync with a credential store.");
    }

    private static async Task<ICredentialProvider> CreateCertificateFileProviderAsync(
        AuthProfile profile,
        ISecureCredentialStore? credentialStore,
        CancellationToken cancellationToken)
    {
        StoredCredential? credential = null;

        // Look up password from secure store if available
        if (credentialStore != null && !string.IsNullOrWhiteSpace(profile.ApplicationId))
        {
            credential = await credentialStore.GetAsync(profile.ApplicationId, cancellationToken).ConfigureAwait(false);
        }

        return CertificateFileCredentialProvider.FromProfile(profile, credential);
    }

    private static async Task<ICredentialProvider> CreateUsernamePasswordProviderAsync(
        AuthProfile profile,
        ISecureCredentialStore? credentialStore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.Username))
            throw new ArgumentException("Profile Username is required", nameof(profile));

        string? password = null;

        // Look up password from secure store using username as key
        if (credentialStore != null)
        {
            // For UsernamePassword, we use username as the key since there's no applicationId
            var credential = await credentialStore.GetAsync(profile.Username, cancellationToken).ConfigureAwait(false);
            password = credential?.Password;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"No password found for user '{profile.Username}'. " +
                "Create a profile with 'ppds auth create'.");
        }

        return new UsernamePasswordCredentialProvider(
            profile.Username,
            password,
            profile.Cloud,
            profile.TenantId);
    }

    /// <summary>
    /// Checks if the specified auth method is supported.
    /// </summary>
    public static bool IsSupported(AuthMethod authMethod)
    {
        return authMethod switch
        {
            AuthMethod.InteractiveBrowser => true,
            AuthMethod.DeviceCode => true,
            AuthMethod.ClientSecret => true,
            AuthMethod.CertificateFile => true,
            AuthMethod.CertificateStore => true,
            AuthMethod.ManagedIdentity => true,
            AuthMethod.GitHubFederated => true,
            AuthMethod.AzureDevOpsFederated => true,
            AuthMethod.UsernamePassword => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the specified auth method requires a secure credential store.
    /// </summary>
    public static bool RequiresCredentialStore(AuthMethod authMethod)
    {
        return authMethod switch
        {
            AuthMethod.ClientSecret => true,
            AuthMethod.CertificateFile => true, // For password, though optional
            AuthMethod.UsernamePassword => true,
            _ => false
        };
    }
}
