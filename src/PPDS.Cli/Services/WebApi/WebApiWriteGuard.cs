using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.WebApi;

public static class WebApiWriteGuard
{
    public static bool IsMutating(HttpMethod method)
        => method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;

    public static bool IsBlocked(HttpMethod method, ProtectionLevel level, bool isConfirmed)
        => IsMutating(method) && level == ProtectionLevel.Production && !isConfirmed;

    /// <summary>
    /// Guard for SDK-based writes (no HTTP method to inspect — the operation is always mutating).
    /// Blocks on a Production-flagged environment unless the caller confirmed with --confirm.
    /// </summary>
    public static bool IsBlocked(ProtectionLevel level, bool isConfirmed)
        => level == ProtectionLevel.Production && !isConfirmed;
}
