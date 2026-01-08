namespace PPDS.Cli.CsvLoader;

/// <summary>
/// CSV mapping file schema constants and version utilities.
/// This is the single source of truth for schema versioning.
/// </summary>
/// <remarks>
/// Versioning rules:
/// - Major version mismatch: Error (CLI cannot process file)
/// - Higher minor version: Warning (some features may be ignored)
/// - Same or lower version: Silent proceed
///
/// Properties prefixed with underscore (_) are metadata - ignored at runtime but help humans.
/// Unknown properties are ignored via [JsonExtensionData] for forward compatibility.
/// </remarks>
public static class CsvMappingSchema
{
    /// <summary>
    /// Current schema version. Update this when the schema evolves.
    /// </summary>
    /// <remarks>
    /// Follow semver-ish rules:
    /// - Bump major (2.0) for breaking changes requiring CLI update
    /// - Bump minor (1.1) for new optional features
    /// </remarks>
    public const string CurrentVersion = "1.0";

    /// <summary>
    /// URL to the published JSON Schema for validation.
    /// </summary>
    public const string SchemaUrl =
        "https://raw.githubusercontent.com/joshsmithxrm/power-platform-developer-suite/main/schemas/csv-mapping.schema.json";

    /// <summary>
    /// Checks if a mapping file version is compatible with this CLI version.
    /// </summary>
    /// <param name="fileVersion">Version from the mapping file.</param>
    /// <returns>True if compatible (same major version).</returns>
    public static bool IsCompatible(string? fileVersion)
    {
        if (string.IsNullOrEmpty(fileVersion))
        {
            return true; // No version = assume compatible
        }

        var fileParts = ParseVersion(fileVersion);
        var cliParts = ParseVersion(CurrentVersion);

        return fileParts.Major == cliParts.Major;
    }

    /// <summary>
    /// Checks if a mapping file version is newer than CLI version (minor version only).
    /// </summary>
    /// <param name="fileVersion">Version from the mapping file.</param>
    /// <returns>True if file has higher minor version.</returns>
    public static bool IsNewerMinorVersion(string? fileVersion)
    {
        if (string.IsNullOrEmpty(fileVersion))
        {
            return false;
        }

        var fileParts = ParseVersion(fileVersion);
        var cliParts = ParseVersion(CurrentVersion);

        return fileParts.Major == cliParts.Major && fileParts.Minor > cliParts.Minor;
    }

    /// <summary>
    /// Parses a version string into major and minor components.
    /// </summary>
    /// <param name="version">Version string (e.g., "1.0", "2.1").</param>
    /// <returns>Tuple of (Major, Minor).</returns>
    public static (int Major, int Minor) ParseVersion(string version)
    {
        var parts = version.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        return (major, minor);
    }

    /// <summary>
    /// Validates a mapping file version and throws if incompatible.
    /// </summary>
    /// <param name="fileVersion">Version from the mapping file.</param>
    /// <param name="onWarning">Optional callback for warning messages.</param>
    /// <exception cref="SchemaVersionException">Thrown if major version mismatch.</exception>
    public static void ValidateVersion(string? fileVersion, Action<string>? onWarning = null)
    {
        if (string.IsNullOrEmpty(fileVersion))
        {
            return; // No version specified, assume compatible
        }

        if (!IsCompatible(fileVersion))
        {
            throw new SchemaVersionException(fileVersion, CurrentVersion);
        }

        if (IsNewerMinorVersion(fileVersion) && onWarning != null)
        {
            onWarning($"Warning: Mapping file version {fileVersion} is newer than CLI version {CurrentVersion}. " +
                "Some features may be ignored.");
        }
    }
}
