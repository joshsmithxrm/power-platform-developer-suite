using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PPDS.Cli.Infrastructure.Safety;

/// <summary>
/// Encapsulates the sentinel-file JSON read used by
/// <see cref="ShakedownGuard"/>. Extracted from the guard so the file-IO
/// flow (open → parse → timestamp check) can be reasoned about in one
/// place and so exception-handling stays uniform.
/// </summary>
internal static class ShakedownSentinelReader
{
    /// <summary>
    /// Reads the sentinel at <paramref name="absolutePath"/> and returns an
    /// <see cref="ActivationState"/>. Returns an inactive state for every
    /// absent / unreadable / stale / corrupt case. Warnings are emitted via
    /// <paramref name="log"/> for conditions that deserve operator visibility
    /// (IO errors, corrupt JSON) but never for the legitimate stale-sentinel
    /// case (AC-04).
    /// </summary>
    /// <param name="fs">File system abstraction.</param>
    /// <param name="log">Logger for warning-level diagnostic messages.</param>
    /// <param name="absolutePath">Absolute path to the sentinel file.</param>
    /// <param name="relativePath">
    /// Project-root-relative display path (forward slashes) used in warnings
    /// and in the resulting <see cref="ActivationState.SentinelRelativePath"/>.
    /// </param>
    /// <param name="now">Current UTC time for freshness comparison.</param>
    /// <param name="staleThreshold">Freshness window; sentinels older than
    /// this are treated as stale (inactive, no warning).</param>
    public static ActivationState Read(
        IFileSystem fs,
        ILogger log,
        string absolutePath,
        string relativePath,
        DateTimeOffset now,
        TimeSpan staleThreshold)
    {
        if (!fs.FileExists(absolutePath))
            return new ActivationState(false, string.Empty, null, null);

        DateTimeOffset startedAt;
        try
        {
            using var stream = fs.OpenRead(absolutePath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("started_at", out var el)
                || el.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(el.GetString(), out startedAt))
            {
                log.LogWarning(
                    "Shakedown sentinel at '{Path}' is corrupt or missing 'started_at'; treating as absent.",
                    relativePath);
                return new ActivationState(false, string.Empty, null, null);
            }
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex,
                "Shakedown sentinel at '{Path}' contained invalid JSON; treating as absent.",
                relativePath);
            return new ActivationState(false, string.Empty, null, null);
        }
        catch (IOException ex)
        {
            log.LogWarning(ex,
                "Could not read shakedown sentinel at '{Path}' ({Error}); treating as absent.",
                relativePath,
                ex.Message);
            return new ActivationState(false, string.Empty, null, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            log.LogWarning(ex,
                "Access denied reading shakedown sentinel at '{Path}'; treating as absent.",
                relativePath);
            return new ActivationState(false, string.Empty, null, null);
        }

        var age = now - startedAt;
        var absAge = age < TimeSpan.Zero ? -age : age;
        if (absAge > staleThreshold)
        {
            // Stale sentinel: inactive, NO warning (AC-04).
            return new ActivationState(false, string.Empty, null, null);
        }

        return new ActivationState(
            IsActive: true,
            Source: $"sentinel:{relativePath}",
            SentinelRelativePath: relativePath,
            SentinelAge: absAge);
    }
}
