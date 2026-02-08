using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Application service for managing Dataverse environments.
/// </summary>
/// <remarks>
/// This service encapsulates environment discovery and selection logic shared between
/// CLI, TUI, and RPC interfaces.
/// </remarks>
public interface IEnvironmentService
{
    /// <summary>
    /// Discovers all environments accessible to the current profile.
    /// </summary>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered environments.</returns>
    /// <exception cref="PpdsAuthException">If authentication fails.</exception>
    /// <exception cref="PpdsException">If discovery fails.</exception>
    Task<IReadOnlyList<EnvironmentSummary>> DiscoverEnvironmentsAsync(
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current environment from the active profile.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current environment, or null if none is selected.</returns>
    Task<EnvironmentSummary?> GetCurrentEnvironmentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the environment for the active profile by identifier.
    /// </summary>
    /// <param name="identifier">Environment identifier (URL, name, ID, or unique name).</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved and bound environment.</returns>
    /// <exception cref="PpdsNotFoundException">If the environment is not found.</exception>
    /// <exception cref="PpdsAuthException">If authentication fails.</exception>
    Task<EnvironmentSummary> SetEnvironmentAsync(
        string identifier,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the environment from the active profile.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if environment was cleared, false if no environment was set.</returns>
    Task<bool> ClearEnvironmentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the auth method supports Global Discovery (environment listing).
    /// </summary>
    /// <param name="authMethod">The authentication method to check.</param>
    /// <returns>True if Global Discovery is supported.</returns>
    bool SupportsDiscovery(AuthMethod authMethod);
}

/// <summary>
/// Summary information about an environment (safe to expose to UI).
/// </summary>
public sealed record EnvironmentSummary
{
    /// <summary>
    /// Environment URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Unique name.
    /// </summary>
    public string? UniqueName { get; init; }

    /// <summary>
    /// Organization ID.
    /// </summary>
    public string? OrganizationId { get; init; }

    /// <summary>
    /// Power Platform environment ID.
    /// </summary>
    public string? EnvironmentId { get; init; }

    /// <summary>
    /// Environment type (Production, Sandbox, etc.).
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Geographic region.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Environment version.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Whether this environment is enabled.
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Whether this is a trial environment.
    /// </summary>
    public bool IsTrial { get; init; }

    /// <summary>
    /// Creates a summary from a DiscoveredEnvironment.
    /// </summary>
    public static EnvironmentSummary FromDiscovered(DiscoveredEnvironment env)
    {
        return new EnvironmentSummary
        {
            Url = env.ApiUrl,
            DisplayName = env.FriendlyName,
            UniqueName = env.UniqueName,
            OrganizationId = env.Id.ToString(),
            EnvironmentId = env.EnvironmentId,
            Type = env.EnvironmentType,
            Region = env.Region,
            Version = env.Version,
            IsEnabled = env.IsEnabled,
            IsTrial = env.IsTrial
        };
    }

    /// <summary>
    /// Creates a summary from an EnvironmentInfo.
    /// </summary>
    public static EnvironmentSummary FromEnvironmentInfo(EnvironmentInfo info)
    {
        return new EnvironmentSummary
        {
            Url = info.Url,
            DisplayName = info.DisplayName,
            UniqueName = info.UniqueName,
            OrganizationId = info.OrganizationId,
            EnvironmentId = info.EnvironmentId,
            Type = info.Type,
            Region = info.Region
        };
    }
}
