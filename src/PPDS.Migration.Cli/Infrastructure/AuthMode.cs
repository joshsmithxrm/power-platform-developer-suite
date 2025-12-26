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
    /// Use appsettings.json + User Secrets. Requires --env to specify environment.
    /// </summary>
    Config,

    /// <summary>
    /// Use environment variables only (DATAVERSE__* prefix).
    /// </summary>
    Env,

    /// <summary>
    /// Azure Managed Identity - for Azure-hosted workloads.
    /// </summary>
    Managed
}
