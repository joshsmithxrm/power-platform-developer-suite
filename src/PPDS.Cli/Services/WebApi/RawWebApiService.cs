using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;

namespace PPDS.Cli.Services.WebApi;

public sealed class RawWebApiService : IRawWebApiService, IDisposable
{
    private readonly IPowerPlatformTokenProvider _tokenProvider;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RawWebApiService> _logger;

    public RawWebApiService(
        IPowerPlatformTokenProvider tokenProvider,
        HttpClient httpClient,
        ILogger<RawWebApiService> logger)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RawWebApiResponse> SendAsync(
        RawWebApiRequest request,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (WebApiWriteGuard.IsBlocked(request.Method, request.ProtectionLevel, request.IsConfirmed))
        {
            throw new PpdsException(
                "Api.WriteBlocked",
                $"Mutating request blocked on Production environment '{request.EnvironmentUrl}'. Add --confirm to proceed.");
        }

        var baseUrl = request.EnvironmentUrl.TrimEnd('/');
        var path = request.Path.StartsWith('/') ? request.Path : "/" + request.Path;
        var url = baseUrl + path;

        PowerPlatformToken token;
        try
        {
            token = await _tokenProvider.GetTokenForResourceAsync(request.EnvironmentUrl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PpdsException(
                "Api.AuthFailed",
                $"Failed to acquire token for '{request.EnvironmentUrl}': {ex.Message}",
                ex);
        }

        using var httpRequest = new HttpRequestMessage(request.Method, url);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // Merge defaults with user-supplied headers; user wins on conflict.
        var effectiveHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OData-Version"] = "4.0",
            ["OData-MaxVersion"] = "4.0",
            ["Accept"] = "application/json"
        };
        if (request.Headers != null)
        {
            foreach (var (key, value) in request.Headers)
                effectiveHeaders[key] = value;
        }

        // Set body before applying content headers so Content-Type lands on HttpContent.
        if (!string.IsNullOrEmpty(request.Body))
        {
            var contentType = effectiveHeaders.TryGetValue("Content-Type", out var ct)
                ? ct
                : "application/json";
            httpRequest.Content = new StringContent(request.Body, Encoding.UTF8);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        foreach (var (key, value) in effectiveHeaders)
        {
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            // Try request headers first; fall back to content headers for content-specific ones
            // (e.g., Content-Encoding, Content-Language) that TryAddWithoutValidation rejects.
            if (!httpRequest.Headers.TryAddWithoutValidation(key, value))
                httpRequest.Content?.Headers.TryAddWithoutValidation(key, value);
        }

        progress?.ReportPhase("Sending request");
        _logger.LogDebug("Sending {Method} {Url}", request.Method, url);

        var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var body = response.Content != null
            ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : string.Empty;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
            headers[header.Key] = string.Join(", ", header.Value);
        if (response.Content != null)
        {
            foreach (var header in response.Content.Headers)
                headers[header.Key] = string.Join(", ", header.Value);
        }

        return new RawWebApiResponse
        {
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Headers = headers,
            Body = body
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _tokenProvider.Dispose();
    }
}
