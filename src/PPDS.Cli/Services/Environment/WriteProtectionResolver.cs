using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Query;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Single source of truth for resolving the effective <see cref="ProtectionLevel"/> used by the
/// production write guard. Shared by <c>ppds api request</c> and the model-driven-app write commands
/// so both gate mutating operations identically (issue #1195).
/// </summary>
public static class WriteProtectionResolver
{
    /// <summary>
    /// Resolves the effective protection level for the write guard.
    /// Fail-safe: an unknown/undetectable environment type resolves to Production so mutating
    /// requests are blocked without --confirm. (<see cref="DmlSafetyGuard.DetectProtectionLevel"/> maps
    /// Unknown to Development for SQL DML, which would fail open here — so we resolve Unknown locally.)
    /// An explicit per-environment protection override always wins.
    /// </summary>
    public static ProtectionLevel Resolve(EnvironmentType envType, ProtectionLevel? configuredProtection)
        => configuredProtection
            ?? (envType == EnvironmentType.Unknown
                ? ProtectionLevel.Production
                : DmlSafetyGuard.DetectProtectionLevel(envType));

    /// <summary>
    /// Resolves the effective protection level for an environment by reading its (local) configuration.
    /// </summary>
    public static async Task<ProtectionLevel> ResolveAsync(
        IEnvironmentConfigService envConfigService,
        string environmentUrl,
        CancellationToken ct)
    {
        var config = await envConfigService.GetConfigAsync(environmentUrl, ct);
        return Resolve(config?.Type ?? EnvironmentType.Unknown, config?.Protection);
    }
}
