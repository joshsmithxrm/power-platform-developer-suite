namespace PPDS.Migration.Cli.Infrastructure;

/// <summary>
/// Authentication modes supported by the CLI.
/// </summary>
public enum AuthMode
{
    /// <summary>
    /// Interactive device code flow - opens browser for authentication.
    /// This is the default mode for development.
    /// </summary>
    Interactive,

    /// <summary>
    /// Use environment variables (DATAVERSE__URL, DATAVERSE__CLIENTID, DATAVERSE__CLIENTSECRET).
    /// Best for CI/CD pipelines.
    /// </summary>
    Env,

    /// <summary>
    /// Azure Managed Identity - for Azure-hosted workloads.
    /// Works in Azure VMs, App Service, AKS, etc.
    /// </summary>
    Managed
}
