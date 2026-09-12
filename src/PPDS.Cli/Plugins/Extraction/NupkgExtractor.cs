using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
    /// Inspects a package and resolves its one plausible primary plugin assembly.
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

    /// <summary>
    /// Resolves the manifest identity of the assembly represented by an existing configuration
    /// without loading its dependency graph. This is the deploy preflight path: registrations
    /// produced with <c>--reference-dir</c> remain portable because machine-local resolver paths
    /// are not persisted in registrations.json or required again during deployment.
    /// </summary>
    internal static string InspectConfiguredAssemblyIdentity(
        byte[] nupkgContent,
        string nupkgPath,
        PluginAssemblyConfig config)
    {
        var packageMetadata = PluginPackageMetadataReader.Read(nupkgContent);
        var frameworkPrefix = $"lib/{packageMetadata.TargetFramework}/";
        var assemblies = new List<ManifestAssembly>();

        using (var packageStream = new MemoryStream(nupkgContent, writable: false))
        using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries.OrderBy(item => item.FullName, StringComparer.OrdinalIgnoreCase))
            {
                var normalizedName = entry.FullName.Replace('\\', '/');
                if (!normalizedName.StartsWith(frameworkPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relativeName = normalizedName[frameworkPrefix.Length..];
                if (relativeName.Contains('/')
                    || !relativeName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var image = new MemoryStream();
                    using (var entryStream = entry.Open())
                        entryStream.CopyTo(image);
                    image.Position = 0;

                    using var peReader = new PEReader(image);
                    if (!peReader.HasMetadata)
                        continue;

                    var reader = peReader.GetMetadataReader();
                    if (!reader.IsAssembly)
                        continue;

                    var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
                    var typeNames = reader.TypeDefinitions
                        .Select(handle => GetTypeDefinitionFullName(reader, handle))
                        .ToHashSet(StringComparer.Ordinal);
                    assemblies.Add(new ManifestAssembly(
                        relativeName,
                        assemblyName,
                        typeNames,
                        HasPortablePluginEvidence(reader)));
                }
                catch (BadImageFormatException)
                {
                    // Native and resource-only DLLs do not contribute a managed manifest identity.
                }
            }
        }

        if (assemblies.Count == 0)
        {
            throw new PpdsException(
                ErrorCodes.Plugin.PackageAssemblyNotFound,
                $"NuGet package '{Path.GetFileName(nupkgPath)}' contains no managed assembly manifests " +
                $"in lib/{packageMetadata.TargetFramework}. No package was uploaded.");
        }

        var configuredTypeNames = config.AllTypeNames
            .Concat(config.Types.Select(type => type.TypeName))
            .Concat(config.CustomApis?.Select(api => api.PluginTypeName) ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var typeOwners = assemblies
            .Where(assembly => configuredTypeNames.Any(assembly.TypeNames.Contains))
            .ToList();

        if (typeOwners.Count > 1)
        {
            throw new PpdsException(
                ErrorCodes.Plugin.PackageAssemblyAmbiguous,
                $"Configured plugin types span multiple package assemblies: {FormatAssemblyNames(typeOwners)}. " +
                "PPDS deploys one primary assembly per package configuration. No package was uploaded.");
        }

        if (typeOwners.Count == 1)
            return EnsureNoAdditionalPortableCandidates(nupkgPath, assemblies, typeOwners[0]);

        var identityMatches = assemblies
            .Where(assembly => string.Equals(assembly.Name, config.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (identityMatches.Count == 1)
            return EnsureNoAdditionalPortableCandidates(nupkgPath, assemblies, identityMatches[0]);

        if (identityMatches.Count > 1)
        {
            throw new PpdsException(
                ErrorCodes.Plugin.PackageAssemblyAmbiguous,
                $"NuGet package '{Path.GetFileName(nupkgPath)}' contains multiple managed DLLs with assembly " +
                $"identity '{config.Name}': {FormatAssemblyNames(identityMatches)}. No package was uploaded.");
        }

        // With one managed DLL its manifest is unambiguous and gives the mismatch diagnostic its
        // actual assembly name. For multi-DLL packages without configured type evidence, fail
        // closed rather than guessing that a dependency is the primary plugin assembly.
        if (assemblies.Count == 1)
            return assemblies[0].Name;

        throw new PpdsException(
            ErrorCodes.Plugin.PackageAssemblyMismatch,
            $"Configured assembly '{config.Name}' does not match a managed assembly manifest in " +
            $"'{Path.GetFileName(nupkgPath)}', and the package primary cannot be inferred from configured " +
            $"plugin types. Package assemblies: {FormatAssemblyNames(assemblies)}. No package was uploaded.");
    }

    private static string EnsureNoAdditionalPortableCandidates(
        string nupkgPath,
        IReadOnlyList<ManifestAssembly> assemblies,
        ManifestAssembly configuredPrimary)
    {
        var additionalCandidates = assemblies
            .Where(assembly => !ReferenceEquals(assembly, configuredPrimary)
                && assembly.HasPortablePluginEvidence)
            .ToList();
        if (additionalCandidates.Count == 0)
            return configuredPrimary.Name;

        var candidates = new[] { configuredPrimary }.Concat(additionalCandidates);
        throw new PpdsException(
            ErrorCodes.Plugin.PackageAssemblyAmbiguous,
            $"NuGet package '{Path.GetFileName(nupkgPath)}' contains multiple plausible primary plugin " +
            $"assemblies in the buffered package snapshot: {FormatAssemblyNames(candidates)}. " +
            "Re-run 'ppds plugins extract' for the rebuilt package before deploying. No package was uploaded.");
    }

    private static string FormatAssemblyNames(IEnumerable<ManifestAssembly> assemblies)
        => string.Join(
            ", ",
            assemblies
                .OrderBy(assembly => assembly.Name, StringComparer.OrdinalIgnoreCase)
                .Select(assembly => $"'{assembly.Name}' ({assembly.FileName})"));

    private static string GetTypeDefinitionFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        if (!definition.GetDeclaringType().IsNil)
            return $"{GetTypeDefinitionFullName(reader, definition.GetDeclaringType())}+{name}";

        var @namespace = reader.GetString(definition.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static bool HasPortablePluginEvidence(MetadataReader reader)
    {
        var implementsPlugin = new Dictionary<TypeDefinitionHandle, bool>();

        bool ImplementsPlugin(TypeDefinitionHandle handle, HashSet<TypeDefinitionHandle> visiting)
        {
            if (implementsPlugin.TryGetValue(handle, out var cached))
                return cached;
            if (!visiting.Add(handle))
                return false;

            try
            {
                var definition = reader.GetTypeDefinition(handle);
                foreach (var implementationHandle in definition.GetInterfaceImplementations())
                {
                    var implementation = reader.GetInterfaceImplementation(implementationHandle);
                    if (AssemblyExtractor.IsDataversePluginInterfaceReference(reader, implementation.Interface))
                    {
                        implementsPlugin[handle] = true;
                        return true;
                    }

                    if (implementation.Interface.Kind == HandleKind.TypeDefinition
                        && ImplementsPlugin((TypeDefinitionHandle)implementation.Interface, visiting))
                    {
                        implementsPlugin[handle] = true;
                        return true;
                    }
                }

                if (definition.BaseType.Kind == HandleKind.TypeDefinition
                    && ImplementsPlugin((TypeDefinitionHandle)definition.BaseType, visiting))
                {
                    implementsPlugin[handle] = true;
                    return true;
                }

                implementsPlugin[handle] = false;
                return false;
            }
            finally
            {
                visiting.Remove(handle);
            }
        }

        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            var isAbstract = (definition.Attributes & TypeAttributes.Abstract) != 0;
            var isInterface = (definition.Attributes & TypeAttributes.Interface) != 0;
            var isOpenGeneric = definition.GetGenericParameters().Count > 0;
            if (!IsExported(reader, handle) || isAbstract || isInterface || isOpenGeneric)
                continue;

            if (ImplementsPlugin(handle, [])
                || definition.GetCustomAttributes().Any(attribute => IsRegistrationAttribute(reader, attribute)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExported(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
        if (definition.GetDeclaringType().IsNil)
            return visibility == TypeAttributes.Public;

        return visibility == TypeAttributes.NestedPublic
            && IsExported(reader, definition.GetDeclaringType());
    }

    private static bool IsRegistrationAttribute(
        MetadataReader reader,
        CustomAttributeHandle handle)
    {
        var attribute = reader.GetCustomAttribute(handle);
        EntityHandle attributeType = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference(
                (MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition(
                (MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default
        };

        var typeName = attributeType.Kind switch
        {
            HandleKind.TypeReference => GetTypeReferenceFullName(
                reader,
                (TypeReferenceHandle)attributeType),
            HandleKind.TypeDefinition => GetTypeDefinitionFullName(
                reader,
                (TypeDefinitionHandle)attributeType),
            _ => null
        };

        return typeName is "PPDS.Plugins.PluginStepAttribute" or "PPDS.Plugins.CustomApiAttribute";
    }

    private static string GetTypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return $"{GetTypeReferenceFullName(reader, (TypeReferenceHandle)reference.ResolutionScope)}+{name}";

        var @namespace = reader.GetString(reference.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static bool IsPlausiblePluginAssembly(PluginAssemblyConfig config)
        => config.RuntimePluginTypeNames.Count > 0
            || config.Types.Count > 0
            || config.CustomApis is { Count: > 0 };

    private sealed record InspectedAssembly(string FileName, PluginAssemblyConfig Config);

    private sealed record ManifestAssembly(
        string FileName,
        string Name,
        HashSet<string> TypeNames,
        bool HasPortablePluginEvidence);

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
