using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Application service for managing environment configuration (labels, types, colors).
/// Shared across CLI, TUI, and RPC interfaces.
/// </summary>
public interface IEnvironmentConfigService
{
    Task<EnvironmentConfig?> GetConfigAsync(string url, CancellationToken ct = default);
    Task<IReadOnlyList<EnvironmentConfig>> GetAllConfigsAsync(CancellationToken ct = default);
    Task<EnvironmentConfig> SaveConfigAsync(string url, string? label = null, string? type = null, EnvironmentColor? color = null, CancellationToken ct = default);
    Task<bool> RemoveConfigAsync(string url, CancellationToken ct = default);
    Task SaveTypeDefaultAsync(string typeName, EnvironmentColor color, CancellationToken ct = default);
    Task<bool> RemoveTypeDefaultAsync(string typeName, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, EnvironmentColor>> GetAllTypeDefaultsAsync(CancellationToken ct = default);
    Task<EnvironmentColor> ResolveColorAsync(string url, CancellationToken ct = default);
    Task<string?> ResolveTypeAsync(string url, string? discoveredType = null, CancellationToken ct = default);
    Task<string?> ResolveLabelAsync(string url, CancellationToken ct = default);
}
