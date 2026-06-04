using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.WebApi;

public static class WebApiWriteGuard
{
    public static bool IsMutating(HttpMethod method)
        => method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;

    public static bool IsBlocked(HttpMethod method, ProtectionLevel level, bool isConfirmed)
        => IsMutating(method) && level == ProtectionLevel.Production && !isConfirmed;
}
