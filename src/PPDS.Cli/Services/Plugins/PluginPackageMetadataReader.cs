using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Plugins;

/// <summary>
/// Reads the authoritative package identity from the root .nuspec in a NuGet plugin package.
/// </summary>
internal static class PluginPackageMetadataReader
{
    private static readonly string[] DataverseSupportedFrameworks = ["net462", "net471"];

    /// <summary>
    /// Reads and validates the package ID, version, and framework asset group required to
    /// register a Dataverse <c>pluginpackage</c> row.
    /// </summary>
    public static PluginPackageMetadata Read(byte[] nupkgContent)
    {
        if (nupkgContent is not { Length: > 0 })
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                "Plugin package content is empty.");
        }

        try
        {
            using var stream = new MemoryStream(nupkgContent, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var nuspecEntry = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.Contains('/')
                && !entry.FullName.Contains('\\'));

            if (nuspecEntry == null)
            {
                throw new PpdsException(
                    ErrorCodes.Validation.RequiredField,
                    "Plugin package does not contain a root-level .nuspec file.");
            }

            using var nuspecStream = nuspecEntry.Open();
            var document = XDocument.Load(nuspecStream);
            var ns = document.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var metadata = document.Root?.Element(ns + "metadata");
            var id = metadata?.Element(ns + "id")?.Value.Trim();
            var version = metadata?.Element(ns + "version")?.Value.Trim();

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new PpdsException(
                    ErrorCodes.Validation.RequiredField,
                    "Plugin package .nuspec is missing the required <id> element.");
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                throw new PpdsException(
                    ErrorCodes.Validation.RequiredField,
                    "Plugin package .nuspec is missing the required <version> element.");
            }

            var packageFrameworks = archive.Entries
                .Select(entry => new
                {
                    Parts = entry.FullName.Replace('\\', '/').Split('/'),
                    IsDll = entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                })
                .Where(entry => entry.IsDll
                    && entry.Parts.Length >= 3
                    && entry.Parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(entry.Parts[1]))
                .Select(entry => entry.Parts[1])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(framework => framework, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Prefer PPDS's net462 compatibility baseline when a package includes both groups.
            var targetFramework = DataverseSupportedFrameworks
                .Select(supported => packageFrameworks.FirstOrDefault(found =>
                    found.Equals(supported, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(found => found != null);

            if (targetFramework == null)
            {
                var found = packageFrameworks.Length == 0
                    ? "none"
                    : string.Join(", ", packageFrameworks.Select(framework => $"lib/{framework}"));

                throw new PpdsException(
                    ErrorCodes.Validation.InvalidValue,
                    "Plugin package does not contain a Dataverse-supported framework folder. " +
                    "Supported folders are lib/net462 and lib/net471. " +
                    $"Found: {found}. Retarget the plugin package project to net462 or net471.");
            }

            return new PluginPackageMetadata(id, version, targetFramework);
        }
        catch (PpdsException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException)
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                "Plugin package content is not a valid NuGet package.",
                ex);
        }
    }
}

/// <summary>
/// Authoritative identity and deployable framework read from a NuGet plugin package.
/// </summary>
internal sealed record PluginPackageMetadata(string Id, string Version, string TargetFramework);
