using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.WebApi;

public sealed class RawWebApiRequest
{
    public required string EnvironmentUrl { get; init; }
    public required string Path { get; init; }
    public HttpMethod Method { get; init; } = HttpMethod.Get;
    public string? Body { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public bool IsConfirmed { get; init; }
    public ProtectionLevel ProtectionLevel { get; init; } = ProtectionLevel.Production;
}
