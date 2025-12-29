namespace PPDS.Auth.Profiles;

/// <summary>
/// Authentication method for a profile.
/// </summary>
public enum AuthMethod
{
    /// <summary>
    /// Interactive browser flow (default for desktop).
    /// Opens system browser for authentication.
    /// </summary>
    InteractiveBrowser,

    /// <summary>
    /// Device code flow (fallback for headless environments).
    /// User visits URL and enters code to authenticate.
    /// </summary>
    DeviceCode,

    /// <summary>
    /// Client ID and client secret (Service Principal).
    /// For production server-to-server scenarios.
    /// </summary>
    ClientSecret,

    /// <summary>
    /// Client ID and certificate from file (Service Principal).
    /// More secure than ClientSecret.
    /// </summary>
    CertificateFile,

    /// <summary>
    /// Client ID and certificate from Windows certificate store (Service Principal).
    /// Windows only.
    /// </summary>
    CertificateStore,

    /// <summary>
    /// Azure Managed Identity.
    /// For Azure-hosted workloads (VMs, App Service, AKS, etc.).
    /// </summary>
    ManagedIdentity,

    /// <summary>
    /// GitHub Actions OIDC (workload identity federation).
    /// For GitHub Actions CI/CD pipelines.
    /// </summary>
    GitHubFederated,

    /// <summary>
    /// Azure DevOps OIDC (workload identity federation).
    /// For Azure DevOps CI/CD pipelines.
    /// </summary>
    AzureDevOpsFederated,

    /// <summary>
    /// Username and password (ROPC flow).
    /// </summary>
    UsernamePassword
}
