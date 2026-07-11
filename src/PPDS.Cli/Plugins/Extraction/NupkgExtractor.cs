using System.IO.Compression;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;

namespace PPDS.Cli.Plugins.Extraction;

/// <summary>
/// Extracts plugin registration information from NuGet packages.
/// </summary>
public static class NupkgExtractor
{
    /// <summary>
    /// Extracts plugin configuration from a NuGet package.
    /// </summary>
    /// <param name="nupkgPath">Path to the .nupkg file.</param>
    /// <param name="referenceDirs">
    /// Optional additional directories to search for referenced assemblies, forwarded to
    /// <see cref="AssemblyExtractor.Create(string, IReadOnlyList{string})"/> for each candidate
    /// assembly (the <c>--reference-dir</c> option).
    /// </param>
    /// <returns>Assembly configuration from the package.</returns>
    /// <exception cref="PpdsException">
    /// Thrown when no plugin types could be extracted and at least one candidate assembly failed
    /// to load, so the failure is surfaced instead of silently returning an empty configuration.
    /// </exception>
    public static PluginAssemblyConfig Extract(string nupkgPath, IReadOnlyList<string>? referenceDirs = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ppds-extract-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);

            // Extract the nupkg (it's a zip file). Use the 3-arg overload with
            // overwriteFiles: false — .NET 8 applies a zip-slip mitigation by default, but we
            // also re-assert every entry's canonical path stays under tempDir to guard against
            // any future regression or custom .nupkg entries with traversal segments.
            ZipFile.ExtractToDirectory(nupkgPath, tempDir, overwriteFiles: false);
            AssertExtractedEntriesContained(nupkgPath, tempDir);

            // Find plugin DLLs in the lib folder
            // Plugin packages target net462 typically
            var libDir = Path.Combine(tempDir, "lib");
            if (!Directory.Exists(libDir))
            {
                throw new InvalidOperationException(
                    $"NuGet package does not contain a 'lib' folder: {nupkgPath}");
            }

            // Look for the most specific framework folder
            var frameworkDirs = Directory.GetDirectories(libDir)
                .OrderByDescending(d => Path.GetFileName(d)) // Prefer higher versions
                .ToList();

            if (frameworkDirs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"NuGet package 'lib' folder is empty: {nupkgPath}");
            }

            // Prefer net462 for plugins (Dataverse requirement), fallback to first available
            var targetDir = frameworkDirs.FirstOrDefault(d =>
                Path.GetFileName(d).Equals("net462", StringComparison.OrdinalIgnoreCase))
                ?? frameworkDirs[0];

            // Get all DLLs in the target framework folder
            var dlls = Directory.GetFiles(targetDir, "*.dll");
            if (dlls.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No DLL files found in package framework folder: {targetDir}");
            }

            // Scan all DLLs to find those with plugin registrations
            var allTypes = new List<PluginTypeConfig>();
            var allTypeNames = new List<string>();
            string? primaryAssemblyName = null;
            var failures = new List<(string Assembly, Exception Error)>();

            foreach (var dllPath in dlls)
            {
                try
                {
                    using var extractor = AssemblyExtractor.Create(dllPath, referenceDirs);
                    var assemblyConfig = extractor.Extract();

                    if (assemblyConfig.Types.Count > 0)
                    {
                        // Found plugins in this assembly
                        primaryAssemblyName ??= assemblyConfig.Name;
                        allTypes.AddRange(assemblyConfig.Types);
                        allTypeNames.AddRange(assemblyConfig.AllTypeNames);
                    }
                }
                catch (Exception ex)
                {
                    // Some DLLs legitimately can't be loaded as plugin assemblies (native,
                    // resource-only, or unrelated dependencies). Collect the failure so we can
                    // decide whether it is fatal (nothing extracted) or merely worth a warning.
                    failures.Add((Path.GetFileName(dllPath), ex));
                }
            }

            // If nothing was extracted AND at least one assembly failed to load, surface the
            // failure instead of silently returning an empty config. This is exactly the
            // single-file "could not find core assembly" symptom that the embedded reference
            // assemblies fix (#1294) addresses — should it recur for any reason, the user must
            // see the cause rather than a misleading "0 plugin types" result.
            if (allTypes.Count == 0 && failures.Count > 0)
            {
                var first = failures[0];
                throw new PpdsException(
                    ErrorCodes.Operation.Dependency,
                    $"Could not extract plugin registrations from '{Path.GetFileName(nupkgPath)}': " +
                    $"{failures.Count} of {dlls.Length} assembl{(dlls.Length == 1 ? "y" : "ies")} failed to " +
                    $"load and no plugin types were found. First failure ({first.Assembly}): {first.Error.Message}",
                    first.Error);
            }

            // Partial failure: at least one assembly yielded plugins, but others failed to load.
            // Warn per failed assembly (stderr — stdout is reserved for data) and continue.
            foreach (var (assemblyName, error) in failures)
            {
                Console.Error.WriteLine(
                    $"Warning: skipped assembly '{assemblyName}' during extraction: {error.Message}");
            }

            // Build the combined config
            var config = new PluginAssemblyConfig
            {
                Name = primaryAssemblyName ?? Path.GetFileNameWithoutExtension(nupkgPath),
                Type = "Nuget",
                PackagePath = Path.GetFileName(nupkgPath),
                AllTypeNames = allTypeNames,
                Types = allTypes
            };

            return config;
        }
        finally
        {
            // Clean up temp directory
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Verifies that every entry in <paramref name="archivePath"/> extracts to a location under
    /// <paramref name="destinationDir"/>. Guards against zip-slip entries that escape the temp
    /// directory via ".." or absolute path segments, even if the platform's default mitigation
    /// regresses.
    /// </summary>
    private static void AssertExtractedEntriesContained(string archivePath, string destinationDir)
    {
        var canonicalDest = Path.GetFullPath(destinationDir);
        if (!canonicalDest.EndsWith(Path.DirectorySeparatorChar))
        {
            canonicalDest += Path.DirectorySeparatorChar;
        }

        // Use case-insensitive comparison on Windows and macOS where filesystems are
        // case-insensitive by default (NTFS, APFS-default); case-sensitive on Linux (ext4).
        // Path.GetFullPath does not normalize case, so a case mismatch between the entry
        // path and the base directory would otherwise cause spurious extraction failures
        // on Windows/macOS. The comparison only ever rejects, never accepts, so loosening
        // the comparison cannot create a zip-slip bypass — it only avoids false negatives.
        var pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            // Directory entries have an empty Name and end with a separator. Still validate them
            // so a traversal path like "../evil/" cannot slip past.
            var combined = Path.Combine(destinationDir, entry.FullName);
            var canonicalEntry = Path.GetFullPath(combined);

            if (!canonicalEntry.StartsWith(canonicalDest, pathComparison)
                && !string.Equals(canonicalEntry + Path.DirectorySeparatorChar, canonicalDest, pathComparison))
            {
                throw new PpdsException(
                    ErrorCodes.Validation.InvalidValue,
                    $"NuGet package '{archivePath}' contains an entry that escapes the extraction directory: '{entry.FullName}'.");
            }
        }
    }
}
