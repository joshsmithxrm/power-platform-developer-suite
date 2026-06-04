namespace PPDS.Cli.Services.WebApi;

public sealed class RawWebApiResponse
{
    public int StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public string Body { get; init; } = string.Empty;
    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
}
