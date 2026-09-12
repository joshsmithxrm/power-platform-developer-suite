using System.IO.Compression;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Services.Plugins;

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
    /// <returns>Assembly configuration for the package's one unambiguous plugin assembly.</returns>
    /// <exception cref="PpdsException">
    /// Thrown when no plugin types could be extracted and at least one candidate assembly failed
    /// to load, so the failure is surfaced instead of silently returning an empty configuration.
    /// </exception>
    public static PluginAssemblyConfig Extract(string nupkgPath, IReadOnlyList<string>? referenceDirs = null)
        => Inspect(nupkgPath, referenceDirs).Assembly;

    /// <summary>
    /// Inspects a package and resolves its one plausible primary plugin assembly. Inspection is
    /// also used by deployment to validate the configured assembly identity before any upload.
    /// </summary>
    internal static PluginPackageInspection Inspect(
        string nupkgPath,
        IReadOnlyList<string>? referenceDirs = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ppds-extract-{Guid.NewGuid():N}");

        try
        {
            var packageMetadata = PluginPackageMetadataReader.Read(File.ReadAllBytes(nupkgPath));
            Directory.CreateDirectory(tempDir);

            // Validate every destination before extraction so containment does not depend on
            // the runtime's ZipFile implementation rejecting traversal entries first.
            AssertExtractedEntriesContained(nupkgPath, tempDir);

            // Extract the nupkg (it's a zip file). Use the 3-arg overload with
            // overwriteFiles: false as a second layer of zip-slip protection.
            ZipFile.ExtractToDirectory(nupkgPath, tempDir, overwriteFiles: false);

            // Find plugin DLLs in the lib folder
            // Plugin packages target a supported .NET Framework version.
            var libDir = Path.Combine(tempDir, "lib");
            if (!Directory.Exists(libDir))
            {
                throw new InvalidOperationException(
                    $"NuGet package does not contain a 'lib' folder: {nupkgPath}");
            }

            var targetDir = Directory.GetDirectories(libDir).FirstOrDefault(directory =>
                Path.GetFileName(directory).Equals(
                    packageMetadata.TargetFramework,
                    StringComparison.OrdinalIgnoreCase));
            if (targetDir == null)
            {
                throw new PpdsException(
                    ErrorCodes.Validation.InvalidValue,
                    $"NuGet package framework folder 'lib/{packageMetadata.TargetFramework}' could not be extracted: {nupkgPath}");
            }

            // Get all DLLs in the target framework folder
            var dlls = Directory.GetFiles(targetDir, "*.dll");
            if (dlls.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No DLL files found in package framework folder: {targetDir}");
            }

            // Inspect every managed DLL independently. An inspected assembly is not necessarily a
            // plugin assembly: packages commonly include dependency DLLs. A plausible primary is
            // one that exposes runtime IPlugin types, PPDS step registrations, or Custom APIs.
            // Keeping those concepts separate is important for zero-attribute IPlugin packages.
            var inspectedAssemblies = new List<InspectedAssembly>();
            var failures = new List<(string Assembly, Exception Error)>();

            foreach (var dllPath in dlls.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var extractor = AssemblyExtractor.Create(dllPath, referenceDirs);
                    var assemblyConfig = extractor.Extract();
                    inspectedAssemblies.Add(new InspectedAssembly(Path.GetFileName(dllPath), assemblyConfig));
                }
                catch (Exception ex)
                {
                    // Some DLLs legitimately can't be loaded as plugin assemblies (native,
                    // resource-only, or unrelated dependencies). Collect the failure so we can
                    // decide whether it is fatal (nothing extracted) or merely worth a warning.
                    failures.Add((Path.GetFileName(dllPath), ex));
                }
            }

            var candidates = inspectedAssemblies
                .Where(candidate => IsPlausiblePluginAssembly(candidate.Config))
                .ToList();
            var annotatedTypeCount = inspectedAssemblies.Sum(item => item.Config.Types.Count);
            var runtimePluginTypeCount = inspectedAssemblies.Sum(item => item.Config.RuntimePluginTypeNames.Count);

            if (candidates.Count == 0 && failures.Count > 0)
            {
                var first = failures[0];
                throw new PpdsException(
                    ErrorCodes.Operation.Dependency,
                    $"Could not extract plugin registrations from '{Path.GetFileName(nupkgPath)}': " +
                    $"successfully inspected {inspectedAssemblies.Count} of {dlls.Length} assemblies, but found " +
                    $"no plausible primary plugin assembly ({annotatedTypeCount} PPDS-annotated types, " +
                    $"{runtimePluginTypeCount} runtime IPlugin types). {failures.Count} assembl" +
                    $"{(failures.Count == 1 ? "y" : "ies")} failed to load. " +
                    $"First failure ({first.Assembly}): {first.Error.Message}",
                    first.Error);
            }

            if (candidates.Count == 0)
            {
                var inspectedNames = string.Join(
                    ", ",
                    inspectedAssemblies.Select(item => $"'{item.Config.Name}'"));
                throw new PpdsException(
                    ErrorCodes.Plugin.PackageAssemblyNotFound,
                    $"NuGet package '{Path.GetFileName(nupkgPath)}' has no plausible primary plugin assembly. " +
                    $"Inspected {inspectedAssemblies.Count} loadable assembl" +
                    $"{(inspectedAssemblies.Count == 1 ? "y" : "ies")} ({inspectedNames}) and found " +
                    $"{annotatedTypeCount} PPDS-annotated types and {runtimePluginTypeCount} runtime IPlugin types. " +
                    "The primary assembly must expose an IPlugin implementation or PPDS declarative registration metadata.");
            }

            if (candidates.Count > 1)
            {
                var candidateNames = string.Join(
                    ", ",
                    candidates
                        .OrderBy(item => item.Config.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(item => $"'{item.Config.Name}' ({item.FileName})"));
                throw new PpdsException(
                    ErrorCodes.Plugin.PackageAssemblyAmbiguous,
                    $"NuGet package '{Path.GetFileName(nupkgPath)}' contains multiple plausible primary plugin assemblies: " +
                    $"{candidateNames}. PPDS deploys one primary assembly per package configuration; " +
                    "remove the ambiguity or deploy the assemblies separately.");
            }

            // Partial failure: at least one assembly yielded plugins, but others failed to load.
            // Warn per failed assembly (stderr — stdout is reserved for data) and continue.
            foreach (var (assemblyName, error) in failures)
            {
                Console.Error.WriteLine(
                    $"Warning: skipped assembly '{assemblyName}' during extraction: {error.Message}");
            }

            // Preserve only the one primary assembly's metadata. Combining registrations from
            // multiple DLLs under the first assembly name would target the wrong Dataverse row.
            var primary = candidates[0].Config;
            var config = new PluginAssemblyConfig
            {
                Name = primary.Name,
                Type = "Nuget",
                PackagePath = Path.GetFileName(nupkgPath),
                AllTypeNames = primary.AllTypeNames.Distinct(StringComparer.Ordinal).ToList(),
                RuntimePluginTypeNames = primary.RuntimePluginTypeNames,
                Types = primary.Types,
                CustomApis = primary.CustomApis
            };

            return new PluginPackageInspection(
                config,
                inspectedAssemblies.Count,
                annotatedTypeCount,
                runtimePluginTypeCount);
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

    private static bool IsPlausiblePluginAssembly(PluginAssemblyConfig config)
        => config.RuntimePluginTypeNames.Count > 0
            || config.Types.Count > 0
            || config.CustomApis is { Count: > 0 };

    private sealed record InspectedAssembly(string FileName, PluginAssemblyConfig Config);

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

/// <summary>
/// Local package inspection facts used to keep extraction and deployment preflight aligned.
/// </summary>
internal sealed record PluginPackageInspection(
    PluginAssemblyConfig Assembly,
    int InspectedAssemblyCount,
    int AnnotatedTypeCount,
    int RuntimePluginTypeCount);
