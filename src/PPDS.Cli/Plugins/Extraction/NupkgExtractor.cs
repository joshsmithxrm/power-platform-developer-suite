using System.IO.Compression;
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
    /// <returns>Assembly configuration from the package.</returns>
    public static PluginAssemblyConfig Extract(string nupkgPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ppds-extract-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);

            // Extract the nupkg (it's a zip file)
            ZipFile.ExtractToDirectory(nupkgPath, tempDir);

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

            foreach (var dllPath in dlls)
            {
                try
                {
                    using var extractor = AssemblyExtractor.Create(dllPath);
                    var assemblyConfig = extractor.Extract();

                    if (assemblyConfig.Types.Count > 0)
                    {
                        // Found plugins in this assembly
                        primaryAssemblyName ??= assemblyConfig.Name;
                        allTypes.AddRange(assemblyConfig.Types);
                        allTypeNames.AddRange(assemblyConfig.AllTypeNames);
                    }
                }
                catch
                {
                    // Skip DLLs that can't be loaded (dependencies, native, etc.)
                }
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
}
