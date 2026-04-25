using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PPDS.Cli.Services.WebResources;

/// <summary>
/// Local tracking file written to <c>&lt;folder&gt;/.ppds/webresources.json</c>
/// after a pull operation. Used by push to detect local edits and server conflicts.
/// </summary>
/// <param name="Version">Schema version for forward compatibility.</param>
/// <param name="EnvironmentUrl">Environment URL the resources were pulled from.</param>
/// <param name="Solution">Solution unique name used as filter (null if unfiltered).</param>
/// <param name="StripPrefix">Whether publisher prefix was stripped from local paths.</param>
/// <param name="PulledAt">UTC timestamp of the pull operation.</param>
/// <param name="Resources">Map keyed by Dataverse web resource name.</param>
public sealed record WebResourceTrackingFile(
    int Version,
    string EnvironmentUrl,
    string? Solution,
    bool StripPrefix,
    DateTime PulledAt,
    IReadOnlyDictionary<string, TrackedResource> Resources)
{
    /// <summary>The relative tracking-file path within the target folder.</summary>
    public const string TrackingFileRelativePath = ".ppds/webresources.json";

    /// <summary>Current schema version.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Reads the tracking file from the target folder. Returns null if the file does not exist.
    /// </summary>
    public static async Task<WebResourceTrackingFile?> ReadAsync(string folder, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(folder, TrackingFileRelativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WebResourceTrackingFile>(stream, SerializerOptions, cancellationToken);
    }

    /// <summary>
    /// Writes the tracking file to the target folder, creating the <c>.ppds</c> subdirectory if needed.
    /// </summary>
    public static async Task WriteAsync(string folder, WebResourceTrackingFile trackingFile, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(folder, TrackingFileRelativePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, trackingFile, SerializerOptions, cancellationToken);
    }

    /// <summary>
    /// Computes a SHA256 hash of the file's bytes, formatted as <c>sha256:&lt;hex&gt;</c>.
    /// Operates on raw bytes so the hash is platform- and line-ending-independent.
    /// </summary>
    public static async Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Per-resource tracking entry recorded by pull and consumed by push.
/// </summary>
/// <param name="Id">The Dataverse web resource GUID.</param>
/// <param name="ModifiedOn">The server's <c>modifiedon</c> at pull time. Conflict-detection baseline.</param>
/// <param name="Hash">SHA256 hash of the local file content at pull time, in <c>sha256:&lt;hex&gt;</c> format.</param>
/// <param name="LocalPath">Path relative to the folder root (may differ from <c>name</c> if prefix stripped).</param>
/// <param name="WebResourceType">The Dataverse web resource type code (1-12).</param>
public sealed record TrackedResource(
    Guid Id,
    DateTime? ModifiedOn,
    string Hash,
    string LocalPath,
    int WebResourceType);
