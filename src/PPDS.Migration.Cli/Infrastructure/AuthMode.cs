namespace PPDS.Migration.Cli.Infrastructure;

/// <summary>
/// Authentication modes supported by the CLI.
/// </summary>
public enum AuthMode
{
    /// <summary>
    /// Auto-detect: tries environment variables first, then configuration files.
    /// </summary>
    Auto,

    /// <summary>
    /// Use appsettings.json + User Secrets (default for development).
    /// </summary>
    Config,

    /// <summary>
    /// Use environment variables only (DATAVERSE__* prefix).
    /// </summary>
    Env,

    /// <summary>
    /// Interactive device code flow - opens browser for authentication.
    /// </summary>
    Interactive,

    /// <summary>
    /// Azure Managed Identity - for Azure-hosted workloads.
    /// </summary>
    Managed
}
