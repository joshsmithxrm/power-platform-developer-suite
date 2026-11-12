using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.DocsGen.Common;

namespace PPDS.DocsGen.Libs;

public sealed record LibraryTypeInventory(string FullName, bool Hidden, string? PagePath);
public sealed record LibraryGenerationResult(GenerationResult Generation, string AssemblyName,
    string? TargetFramework, IReadOnlyList<LibraryTypeInventory> Types);
public sealed record LibraryCoverageInput(string Package, string Tfm, string Assembly,
    string AssemblySha256, string XmlSha256);
public sealed record LibraryCoverageManifest(int Schema, LibraryCoverageInput[] Inputs);

/// <summary>
/// Validates the complete five-library input contract before reflecting metadata.
/// Input hashes identify supplied bytes; they do not establish package authenticity.
/// </summary>
public static class LibraryCoverageRunner
{
    internal static readonly string[] Packages =
        ["PPDS.Auth", "PPDS.Dataverse", "PPDS.Migration", "PPDS.Plugins", "PPDS.Query"];
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal static string[] Frameworks(string package) => package == "PPDS.Plugins"
        ? ["net462"] : ["net8.0", "net9.0", "net10.0"];

    public static void Run(string manifestPath, string outputRoot, CancellationToken ct = default) =>
        Run(manifestPath, outputRoot, (path, token) =>
            new LibraryReferenceGenerator().GenerateWithCoverage(new GenerationInput(path, outputRoot), token), ct);

    internal static void Run(string manifestPath, string outputRoot,
        Func<string, CancellationToken, LibraryGenerationResult> generate, CancellationToken ct = default)
    {
        manifestPath = Path.GetFullPath(manifestPath);
        outputRoot = Path.GetFullPath(outputRoot);
        RejectLinks(manifestPath);
        RejectLinks(outputRoot, allowMissing: true);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
            throw new InvalidOperationException("Coverage output must be a new directory; existing output is never overlaid.");
        var rawManifest = File.ReadAllBytes(manifestPath);
        var manifest = JsonSerializer.Deserialize<LibraryCoverageManifest>(rawManifest, JsonOptions)
            ?? throw new InvalidDataException("Missing coverage manifest.");
        if (manifest.Schema != 1 || manifest.Inputs is null || manifest.Inputs.Length != 13)
            throw new InvalidDataException("Coverage requires schema 1 and all thirteen package/framework inputs.");
        var expected = Packages.SelectMany(package => Frameworks(package).Select(tfm => package + "/" + tfm))
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var root = Path.GetDirectoryName(manifestPath)!;
        var resolved = new List<(LibraryCoverageInput Input, string Path)>();
        foreach (var input in manifest.Inputs)
        {
            ct.ThrowIfCancellationRequested();
            if (input is null || !expected.Contains(input.Package + "/" + input.Tfm)
                || !seen.Add(input.Package + "/" + input.Tfm))
                throw new InvalidDataException("Unexpected or duplicate package/framework input.");
            if (string.IsNullOrWhiteSpace(input.Assembly) || Path.IsPathRooted(input.Assembly))
                throw new InvalidDataException("Assembly paths must be relative to the manifest directory.");
            var path = Path.GetFullPath(input.Assembly, root);
            var relative = Path.GetRelativePath(root, path);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFileName(path) != input.Package + ".dll")
                throw new InvalidDataException("Assembly path escapes the manifest or does not match its package.");
            RejectLinks(path);
            var xml = Path.ChangeExtension(path, ".xml");
            RejectLinks(xml);
            VerifyHash(path, input.AssemblySha256);
            VerifyHash(xml, input.XmlSha256);
            resolved.Add((input, path));
        }
        if (!seen.SetEquals(expected)) throw new InvalidDataException("Incomplete package/framework coverage.");

        var generated = new List<(LibraryCoverageInput Input, LibraryGenerationResult Result)>();
        foreach (var (input, path) in resolved.OrderBy(value => value.Input.Package, StringComparer.Ordinal)
                     .ThenBy(value => value.Input.Tfm, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var result = generate(path, ct);
            var expectedFramework = input.Tfm == "net462" ? ".NETFramework,Version=v4.6.2"
                : ".NETCoreApp,Version=v" + input.Tfm[3..];
            if (result.AssemblyName != input.Package || result.TargetFramework != expectedFramework)
                throw new InvalidDataException("Reflected assembly identity/framework disagrees with the manifest.");
            VerifyHash(path, input.AssemblySha256);
            VerifyHash(Path.ChangeExtension(path, ".xml"), input.XmlSha256);
            ValidateInventory(result, input.Package);
            generated.Add((input, result));
        }
        // No output is written until every input and reflected inventory has passed.
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (input, result) in generated)
            foreach (var file in result.Generation.Files)
                files.Add("by-framework/" + input.Tfm + "/" + file.RelativePath, file.Contents);
        foreach (var group in generated.SelectMany(item => item.Result.Generation.Files.Select(file =>
                     (item.Input.Tfm, File: file))).GroupBy(item => item.File.RelativePath, StringComparer.Ordinal))
        {
            var variants = group.OrderBy(item => item.Tfm, StringComparer.Ordinal).ToArray();
            var frameworks = string.Join(", ", variants.Select(item => "`" + item.Tfm + "`"));
            if (variants.Select(item => item.File.Contents).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                files.Add(group.Key, variants[0].File.Contents.TrimEnd('\r', '\n')
                    + "\n\n> Available in: " + frameworks + ".\n");
            }
            else
            {
                var page = new StringBuilder(BannerHelper.AutogeneratedBanner("libs-reflect"));
                page.Append("\n\n# Framework-specific reference\n\n")
                    .Append("This page differs across target frameworks. Each version is retained without overwriting another.\n\n");
                foreach (var variant in variants)
                {
                    var target = "by-framework/" + variant.Tfm + "/" + group.Key;
                    var link = Path.GetRelativePath(Path.GetDirectoryName(group.Key) ?? ".", target).Replace('\\', '/');
                    link = string.Join("/", link.Split('/').Select(Uri.EscapeDataString));
                    page.Append("- [").Append(variant.Tfm).Append("](").Append(link).Append(")\n");
                }
                files.Add(group.Key, page.ToString());
            }
        }
        if (files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("Generated paths collide on a case-insensitive filesystem.");
        ct.ThrowIfCancellationRequested();
        RejectLinks(outputRoot, allowMissing: true);
        Directory.CreateDirectory(outputRoot);
        foreach (var (path, contents) in files)
        {
            var target = Path.Combine(outputRoot, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, contents, new UTF8Encoding(false));
        }
        var receipt = new
        {
            schema = 1, kind = "source-reflection-library-coverage", complete = true,
            manifestSha256 = Hash(rawManifest), inputs = generated.Select(item => new
            {
                item.Input, item.Result.AssemblyName, item.Result.TargetFramework,
                item.Result.Types, item.Result.Generation.Diagnostics
            }).ToArray(),
            files = files.Select(file => new { path = file.Key, sha256 = Hash(Encoding.UTF8.GetBytes(file.Value)) }).ToArray(),
            limits = new[]
            {
                "Supplied DLL/XML hashes and reflected identities are verified; public package authenticity and resolver closure are separate evidence.",
                "Exported-type inventory includes hidden types. Pages omit EditorBrowsableNever types and members, accessor methods and compiler-generated/record-synthesized members.",
                "This is metadata reflection, not actual CLI/MCP execution, stable-package publication or complete CLR signature compatibility proof."
            }
        };
        File.WriteAllText(Path.Combine(outputRoot, "coverage-evidence.json"),
            JsonSerializer.Serialize(receipt, JsonOptions) + "\n", new UTF8Encoding(false));
    }

    private static void ValidateInventory(LibraryGenerationResult result, string package)
    {
        var shortName = package[5..];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in result.Generation.Files)
        {
            if (!file.RelativePath.StartsWith(shortName + "/", StringComparison.Ordinal)
                || file.RelativePath.Contains('\\') || file.RelativePath.Split('/').Any(segment => segment is ".." or "." or "")
                || !file.RelativePath.EndsWith(".md", StringComparison.Ordinal) || !paths.Add(file.RelativePath)
                || !file.Contents.StartsWith(BannerHelper.AutogeneratedBanner("libs-reflect") + "\n", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid, duplicate or out-of-package generated path.");
        }
        if (!paths.Contains(shortName + "/_index.md") || result.Types.Count == 0
            || result.Types.Select(type => type.FullName).Distinct(StringComparer.Ordinal).Count() != result.Types.Count)
            throw new InvalidDataException("Missing index or invalid exported-type inventory.");
        var pagePaths = result.Types.Where(type => !type.Hidden).Select(type => type.PagePath).ToArray();
        if (result.Types.Any(type => type.Hidden ? type.PagePath is not null : type.PagePath is null || !paths.Contains(type.PagePath))
            || pagePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pagePaths.Length
            || !paths.SetEquals(pagePaths.Append(shortName + "/_index.md")!))
            throw new InvalidDataException("Exported-type/page inventory does not match generated files.");
    }

    internal static void RejectLinks(string path, bool allowMissing = false,
        Func<string, FileAttributes>? attributes = null)
    {
        attributes ??= File.GetAttributes;
        // Include the file, manifest root, output parent and every existing ancestor.
        // Missing output components are allowed; inaccessible components are not.
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes value;
            try { value = attributes(current); }
            catch (FileNotFoundException) when (allowMissing) { continue; }
            catch (DirectoryNotFoundException) when (allowMissing) { continue; }
            if ((value & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic/reparse paths and ancestors are not accepted.");
        }
    }
    private static void VerifyHash(string path, string expected)
    {
        if (expected is null || expected.Length != 64 || expected.Any(character => !Uri.IsHexDigit(character))
            || !string.Equals(Hash(File.ReadAllBytes(path)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DLL/XML input hash mismatch: " + Path.GetFileName(path));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
