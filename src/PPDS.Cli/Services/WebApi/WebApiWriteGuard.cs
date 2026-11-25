using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.WebApi;

public static class WebApiWriteGuard
{
    public static bool IsMutating(HttpMethod method)
        => method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;

    public static bool IsBlocked(HttpMethod method, ProtectionLevel level, bool isConfirmed)
        => IsMutating(method) && IsBlocked(level, isConfirmed);

    /// <summary>
    /// Guard for SDK-based writes (no HTTP method to inspect — the operation is always mutating).
    /// Blocks on a Production-flagged environment unless the caller confirmed with --confirm. A value that is not
    /// a defined level is Production (#1829).
    /// </summary>
    public static bool IsBlocked(ProtectionLevel level, bool isConfirmed)
        => (level == ProtectionLevel.Production || !Enum.IsDefined(level)) && !isConfirmed;
}
