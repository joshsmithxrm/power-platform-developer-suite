using System.Text.Json;
using System.Text.Json.Serialization;

namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Application service for checking available updates to the PPDS CLI via the NuGet flat-container API.
/// </summary>
/// <remarks>
/// <para>Results are cached for 24 hours at <c>~/.ppds/update-check.json</c>.</para>
/// <para>
/// The constructor accepts optional <paramref name="handler"/> and <paramref name="cachePath"/>
/// parameters to facilitate testing without live network or filesystem side-effects.
/// </para>
/// </remarks>
public sealed class UpdateCheckService : IUpdateCheckService
{
    private const string NuGetIndexUrl =
        "https://api.nuget.org/v3-flatcontainer/ppds.cli/index.json";

    private const string UpdateCommand = "dotnet tool update PPDS.Cli -g";
    private const string UpdateCommandPreRelease = "dotnet tool update PPDS.Cli -g --prerelease";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpMessageHandler? _handler;
    private readonly string _cachePath;

    /// <summary>
    /// Initializes the service for production use with default HTTP and cache path.
    /// </summary>
    public UpdateCheckService()
        : this(handler: null, cachePath: null)
    {
    }

    /// <summary>
    /// Initializes the service with optional overrides for testing.
    /// </summary>
    /// <param name="handler">
    /// Optional <see cref="HttpMessageHandler"/> for mocking HTTP calls.
    /// Pass <see langword="null"/> to use the default handler.
    /// </param>
    /// <param name="cachePath">
    /// Optional full path to the cache JSON file.
    /// Pass <see langword="null"/> to use the default <c>~/.ppds/update-check.json</c>.
    /// </param>
    public UpdateCheckService(HttpMessageHandler? handler = null, string? cachePath = null)
    {
        _handler = handler;
        _cachePath = cachePath ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".ppds",
            "update-check.json");
    }

    /// <inheritdoc/>
    public async Task<UpdateCheckResult?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        List<string>? versions;

        try
        {
            versions = await FetchVersionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Network or parse failures must not propagate to callers
            return null;
        }

        if (versions is null)
            return null;

        var current = TryParseVersion(currentVersion);

        var latestStable = versions
            .Select(TryParseVersion)
            .Where(v => v is not null && !v.IsPreRelease)
            .OrderDescending()
            .FirstOrDefault();

        var latestPreRelease = versions
            .Select(TryParseVersion)
            .Where(v => v is not null && v.IsPreRelease)
            .OrderDescending()
            .FirstOrDefault();

        var stableUpdateAvailable = latestStable is not null
            && (current is null || latestStable > current);

        var preReleaseUpdateAvailable = latestPreRelease is not null
            && !stableUpdateAvailable
            && (current is null || latestPreRelease > current);

        // Determine command: stable takes priority; fall back to pre-release only if
        // no stable update is available and the user is already on a pre-release track.
        string? command = null;
        if (stableUpdateAvailable)
        {
            command = UpdateCommand;
        }
        else if (preReleaseUpdateAvailable)
        {
            command = UpdateCommandPreRelease;
        }

        var result = new UpdateCheckResult
        {
            CurrentVersion = currentVersion,
            LatestStableVersion = latestStable?.ToString(),
            LatestPreReleaseVersion = latestPreRelease?.ToString(),
            StableUpdateAvailable = stableUpdateAvailable,
            PreReleaseUpdateAvailable = preReleaseUpdateAvailable,
            UpdateCommand = command,
            CheckedAt = DateTimeOffset.UtcNow
        };

        await WriteCacheAsync(result, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc/>
    public async Task<UpdateCheckResult?> GetCachedResultAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var json = await File.ReadAllTextAsync(_cachePath, cancellationToken)
                .ConfigureAwait(false);

            var result = JsonSerializer.Deserialize<UpdateCheckResult>(json, JsonOptions);

            if (result is null)
                return null;

            // Honour TTL
            if (DateTimeOffset.UtcNow - result.CheckedAt > CacheTtl)
                return null;

            return result;
        }
        catch
        {
            // Corrupt, missing, or inaccessible cache is not an error condition
            return null;
        }
    }

    #region Private Helpers

    /// <summary>
    /// Fetches version strings from the NuGet flat-container index.
    /// Returns <see langword="null"/> on non-success HTTP status.
    /// Throws on network/parse errors (caller handles).
    /// </summary>
    private async Task<List<string>?> FetchVersionsAsync(CancellationToken cancellationToken)
    {
        // R1: dispose HttpClient after each call — do not hold as a field
        using var client = _handler is not null
            ? new HttpClient(_handler, disposeHandler: false)
            : new HttpClient();

        client.Timeout = HttpTimeout;

        using var response = await client
            .GetAsync(NuGetIndexUrl, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        var envelope = JsonSerializer.Deserialize<NuGetVersionsEnvelope>(json, JsonOptions);
        return envelope?.Versions;
    }

    /// <summary>
    /// Persists <paramref name="result"/> to the cache file using an atomic write.
    /// Failures are silently swallowed — cache write errors must not surface to callers.
    /// </summary>
    private async Task WriteCacheAsync(
        UpdateCheckResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(result, JsonOptions);
            var tempPath = _cachePath + ".tmp";

            await File.WriteAllTextAsync(tempPath, json, cancellationToken)
                .ConfigureAwait(false);

            // Atomic replace (prevents corrupt reads if the process exits mid-write)
            File.Move(tempPath, _cachePath, overwrite: true);
        }
        catch
        {
            // Cache write failure is not fatal
        }
    }

    private static NuGetVersion? TryParseVersion(string? v)
    {
        NuGetVersion.TryParse(v, out var parsed);
        return parsed;
    }

    #endregion

    #region API Response Model

    /// <summary>Response envelope from <c>https://api.nuget.org/v3-flatcontainer/{id}/index.json</c>.</summary>
    private sealed class NuGetVersionsEnvelope
    {
        [JsonPropertyName("versions")]
        public List<string>? Versions { get; init; }
    }

    #endregion
}
