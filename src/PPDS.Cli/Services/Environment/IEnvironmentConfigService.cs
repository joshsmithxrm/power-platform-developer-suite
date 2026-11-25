using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Application service for managing environment configuration (labels, types, colors).
/// Shared across CLI, TUI, and RPC interfaces.
/// </summary>
public interface IEnvironmentConfigService
{
    /// <summary>
    /// Gets the configuration for a specific environment, or null if not configured.
    /// </summary>
    Task<EnvironmentConfig?> GetConfigAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Gets all configured environments.
    /// </summary>
    Task<IReadOnlyList<EnvironmentConfig>> GetAllConfigsAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves or merges configuration for a specific environment.
    /// Only non-null parameters are updated (existing values preserved).
    /// </summary>
    /// <param name="confirmLowerProtection">
    /// The environment URL, given only after the user confirmed a change that lowers write protection
    /// (see <see cref="AssessConfigChangeAsync"/>). Without it such a change is refused with
    /// <see cref="EnvironmentConfirmationErrorCodes.ProtectionLoweringConfirmationRequired"/>; a different
    /// URL is refused with <see cref="EnvironmentConfirmationErrorCodes.TargetMismatch"/>; a host that never
    /// lowers protection refuses it with <see cref="EnvironmentConfirmationErrorCodes.ProtectionLoweringRefused"/>.
    /// </param>
    Task<EnvironmentConfig> SaveConfigAsync(string url, string? label = null, EnvironmentType? type = null, EnvironmentColor? color = null, bool clearColor = false, string? confirmLowerProtection = null, CancellationToken ct = default);

    /// <summary>
    /// Reports whether saving <paramref name="type"/> for the environment would lower its write protection,
    /// so a surface can ask for confirmation before calling <see cref="SaveConfigAsync"/>. Saves nothing.
    /// </summary>
    Task<ProtectionChangeAssessment> AssessConfigChangeAsync(string url, EnvironmentType? type, CancellationToken ct = default);

    /// <summary>
    /// Removes configuration for a specific environment. Never lowers protection: every writer treats
    /// an environment without saved configuration as Production.
    /// </summary>
    Task<bool> RemoveConfigAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Adds or updates a type definition with a default color.
    /// </summary>
    Task SaveTypeDefaultAsync(EnvironmentType typeName, EnvironmentColor color, CancellationToken ct = default);

    /// <summary>
    /// Removes a type definition override.
    /// </summary>
    Task<bool> RemoveTypeDefaultAsync(EnvironmentType typeName, CancellationToken ct = default);

    /// <summary>
    /// Gets all type definitions (built-in + custom overrides) with their default colors.
    /// </summary>
    Task<IReadOnlyDictionary<EnvironmentType, EnvironmentColor>> GetAllTypeDefaultsAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves the effective color for an environment.
    /// Priority: per-env color > type default color (custom then built-in) > Gray fallback.
    /// </summary>
    Task<EnvironmentColor> ResolveColorAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Resolves the effective environment type for an environment.
    /// Priority: user config type > discovery API type > URL heuristics > Unknown.
    /// </summary>
    Task<EnvironmentType> ResolveTypeAsync(string url, string? discoveredType = null, CancellationToken ct = default);

    /// <summary>
    /// Resolves the display label for an environment.
    /// Priority: user config label > environment DisplayName from profile.
    /// </summary>
    Task<string?> ResolveLabelAsync(string url, CancellationToken ct = default);
}
