using System;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Factory for creating credential providers from auth profiles.
/// </summary>
public static class CredentialProviderFactory
{
    /// <summary>
    /// Creates a credential provider for the specified auth profile.
    /// </summary>
    /// <param name="profile">The auth profile.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display (for DeviceCode auth in headless mode).</param>
    /// <returns>A credential provider for the profile's auth method.</returns>
    /// <exception cref="NotSupportedException">If the auth method is not supported.</exception>
    public static ICredentialProvider Create(
        AuthProfile profile,
        Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));

        return profile.AuthMethod switch
        {
            AuthMethod.InteractiveBrowser => InteractiveBrowserCredentialProvider.FromProfile(profile),
            AuthMethod.DeviceCode => CreateInteractiveProvider(profile, deviceCodeCallback),
            AuthMethod.ClientSecret => ClientSecretCredentialProvider.FromProfile(profile),
            AuthMethod.CertificateFile => CertificateFileCredentialProvider.FromProfile(profile),
            AuthMethod.CertificateStore => CertificateStoreCredentialProvider.FromProfile(profile),
            AuthMethod.ManagedIdentity => ManagedIdentityCredentialProvider.FromProfile(profile),
            AuthMethod.GitHubFederated => new GitHubFederatedCredentialProvider(
                profile.ApplicationId!, profile.TenantId!, profile.Cloud),
            AuthMethod.AzureDevOpsFederated => new AzureDevOpsFederatedCredentialProvider(
                profile.ApplicationId!, profile.TenantId!, profile.Cloud),
            AuthMethod.UsernamePassword => new UsernamePasswordCredentialProvider(
                profile.Username!, profile.Password!, profile.Cloud, profile.TenantId),
            _ => throw new NotSupportedException($"Unknown auth method: {profile.AuthMethod}")
        };
    }

    /// <summary>
    /// Creates the appropriate interactive provider based on environment.
    /// Uses browser authentication by default, falls back to device code for headless environments.
    /// </summary>
    private static ICredentialProvider CreateInteractiveProvider(
        AuthProfile profile,
        Action<DeviceCodeInfo>? deviceCodeCallback)
    {
        // Browser auth when display available, device code for headless (SSH, CI, containers)
        if (InteractiveBrowserCredentialProvider.IsAvailable())
        {
            return InteractiveBrowserCredentialProvider.FromProfile(profile);
        }
        else
        {
            return DeviceCodeCredentialProvider.FromProfile(profile, deviceCodeCallback);
        }
    }

    /// <summary>
    /// Checks if the specified auth method is supported.
    /// </summary>
    /// <param name="authMethod">The auth method to check.</param>
    /// <returns>True if supported, false otherwise.</returns>
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
}
