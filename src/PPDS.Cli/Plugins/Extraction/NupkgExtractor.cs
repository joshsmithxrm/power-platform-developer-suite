using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
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
        => Inspect(File.ReadAllBytes(nupkgPath), nupkgPath, referenceDirs);

    /// <summary>
    /// Inspects one immutable package snapshot. The path is used only for diagnostics and for the
    /// relative packagePath written to configuration.
    /// </summary>
    internal static PluginPackageInspection Inspect(
        byte[] nupkgContent,
        string nupkgPath,
        IReadOnlyList<string>? referenceDirs = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ppds-extract-{Guid.NewGuid():N}");

        try
        {
            var packageMetadata = PluginPackageMetadataReader.Read(nupkgContent);
            Directory.CreateDirectory(tempDir);

            // Validate every destination before extraction so containment does not depend on
            // the runtime's ZipFile implementation rejecting traversal entries first.
            AssertExtractedEntriesContained(nupkgContent, nupkgPath, tempDir);

            // Extract the nupkg (it's a zip file). Use the 3-arg overload with
            // overwriteFiles: false as a second layer of zip-slip protection.
            using (var packageStream = new MemoryStream(nupkgContent, writable: false))
                ZipFile.ExtractToDirectory(packageStream, tempDir, overwriteFiles: false);

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

            var validationFailure = failures
                .Select(failure => failure.Error)
                .OfType<PpdsException>()
                .FirstOrDefault(error => error.ErrorCode == ErrorCodes.Validation.InvalidValue);
            if (validationFailure != null)
                throw validationFailure;

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
                PackageContentSha256 = Convert.ToHexString(SHA256.HashData(nupkgContent)).ToLowerInvariant(),
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
        var assemblyImages = new List<BufferedAssemblyImage>();

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

                    assemblyImages.Add(new BufferedAssemblyImage(relativeName, image.ToArray()));
                }
                catch (BadImageFormatException)
                {
                    // Native and resource-only DLLs do not contribute a managed manifest identity.
                }
            }
        }

        using var metadataResolver = new BufferedPackageMetadataResolver(assemblyImages);
        var assemblies = metadataResolver.Assemblies
            .Select(assembly =>
            {
                var evidence = metadataResolver.GetPortablePluginEvidence(assembly);
                return new ManifestAssembly(
                    assembly.FileName,
                    assembly.Name,
                    assembly.Reader.TypeDefinitions
                        .Select(handle => GetTypeDefinitionFullName(assembly.Reader, handle))
                        .ToHashSet(StringComparer.Ordinal),
                    evidence.HasPlausiblePrimary,
                    evidence.HasOfficialRegistrationWithoutRuntimePlugin,
                    evidence.HasInvalidMetadata);
            })
            .ToList();

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
        {
            return EnsureNoAdditionalPortableCandidates(
                nupkgContent,
                nupkgPath,
                config,
                assemblies,
                typeOwners[0]);
        }

        var identityMatches = assemblies
            .Where(assembly => string.Equals(assembly.Name, config.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (identityMatches.Count == 1)
        {
            return EnsureNoAdditionalPortableCandidates(
                nupkgContent,
                nupkgPath,
                config,
                assemblies,
                identityMatches[0]);
        }

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
        byte[] nupkgContent,
        string nupkgPath,
        PluginAssemblyConfig config,
        IReadOnlyList<ManifestAssembly> assemblies,
        ManifestAssembly configuredPrimary)
    {
        var additionalCandidates = assemblies
            .Where(assembly => !ReferenceEquals(assembly, configuredPrimary)
                && (assembly.HasPortablePluginEvidence
                    || assembly.HasOfficialRegistrationWithoutRuntimePlugin
                    || assembly.HasInvalidMetadata))
            .ToList();
        if (additionalCandidates.Count > 0)
        {
            var candidates = new[] { configuredPrimary }.Concat(additionalCandidates);
            throw new PpdsException(
                ErrorCodes.Plugin.PackageAssemblyAmbiguous,
                $"NuGet package '{Path.GetFileName(nupkgPath)}' contains multiple plausible primary plugin " +
                $"assemblies in the buffered package snapshot: {FormatAssemblyNames(candidates)}. " +
                "Re-run 'ppds plugins extract' for the rebuilt package before deploying. No package was uploaded.");
        }

        if (configuredPrimary.HasInvalidMetadata)
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                $"Configured primary assembly '{configuredPrimary.Name}' in '{Path.GetFileName(nupkgPath)}' " +
                "contains cyclic, malformed, or ambiguously resolved package-local type metadata. " +
                "Correct the package and re-run extraction before deploying. No package was uploaded.");
        }

        if (configuredPrimary.HasPortablePluginEvidence)
            return configuredPrimary.Name;

        var contentHash = Convert.ToHexString(SHA256.HashData(nupkgContent)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(config.PackageContentSha256)
            && string.Equals(config.PackageContentSha256, contentHash, StringComparison.OrdinalIgnoreCase))
        {
            // Extraction may have proven runtime IPlugin inheritance through --reference-dir.
            // An unchanged content digest preserves that proof without persisting or reloading
            // the machine-specific dependency path.
            return configuredPrimary.Name;
        }

        if (configuredPrimary.HasOfficialRegistrationWithoutRuntimePlugin)
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                $"Configured primary assembly '{configuredPrimary.Name}' in '{Path.GetFileName(nupkgPath)}' " +
                "contains official PPDS PluginStep or CustomApi metadata on a concrete type that cannot be " +
                "proven to implement Microsoft.Xrm.Sdk.IPlugin. Re-run extraction after correcting the handler. " +
                "No package was uploaded.");
        }

        throw new PpdsException(
            ErrorCodes.Plugin.PackageAssemblyMismatch,
            $"Configured primary assembly '{configuredPrimary.Name}' in '{Path.GetFileName(nupkgPath)}' no " +
            "longer exposes package-local runtime IPlugin evidence, and its content does not match the " +
            "snapshot used for extraction. Re-run 'ppds plugins extract' with any required --reference-dir " +
            "before deploying. No package was uploaded.");
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

    /// <summary>
    /// Resolves inheritance only across the assemblies already present in the buffered package.
    /// References outside the package are deliberately left unresolved so deploy remains portable
    /// for configurations authored with --reference-dir; malformed or ambiguous package-local
    /// references fail closed and make the assembly a plausible candidate.
    /// </summary>
    private sealed class BufferedPackageMetadataResolver : IDisposable
    {
        private static readonly TypeSpecificationProvider TypeSpecificationDecoder = new();
        private readonly Dictionary<string, List<BufferedMetadataAssembly>> _assembliesByName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ResolvedPackageType, bool> _implementsPlugin = [];

        internal BufferedPackageMetadataResolver(IEnumerable<BufferedAssemblyImage> images)
        {
            foreach (var image in images)
            {
                var assembly = new BufferedMetadataAssembly(image);
                Assemblies.Add(assembly);
                if (!_assembliesByName.TryGetValue(assembly.Name, out var matches))
                {
                    matches = [];
                    _assembliesByName.Add(assembly.Name, matches);
                }

                matches.Add(assembly);
            }
        }

        internal List<BufferedMetadataAssembly> Assemblies { get; } = [];

        internal PortablePluginEvidence GetPortablePluginEvidence(BufferedMetadataAssembly assembly)
        {
            try
            {
                var hasPlausiblePrimary = false;
                var hasOfficialRegistrationWithoutRuntimePlugin = false;
                foreach (var handle in assembly.Reader.TypeDefinitions)
                {
                    var definition = assembly.Reader.GetTypeDefinition(handle);
                    var isAbstract = (definition.Attributes & TypeAttributes.Abstract) != 0;
                    var isInterface = (definition.Attributes & TypeAttributes.Interface) != 0;
                    var isOpenGeneric = definition.GetGenericParameters().Count > 0;
                    if (!IsExported(assembly.Reader, handle) || isAbstract || isInterface || isOpenGeneric)
                        continue;

                    var implementsPlugin = ImplementsPlugin(new ResolvedPackageType(assembly, handle), []);
                    var hasOfficialRegistration = definition.GetCustomAttributes().Any(attribute =>
                        AssemblyExtractor.IsOfficialPpdsRegistrationAttribute(assembly.Reader, attribute));
                    hasPlausiblePrimary |= implementsPlugin;
                    hasOfficialRegistrationWithoutRuntimePlugin |= hasOfficialRegistration && !implementsPlugin;
                }

                return new PortablePluginEvidence(
                    hasPlausiblePrimary,
                    hasOfficialRegistrationWithoutRuntimePlugin,
                    HasInvalidMetadata: false);
            }
            catch (BadImageFormatException)
            {
                // A managed package assembly with malformed or ambiguous package-local type
                // metadata cannot be proven safe as a dependency. Treat it as a candidate so
                // the caller rejects the package before any Dataverse request.
                return new PortablePluginEvidence(
                    HasPlausiblePrimary: false,
                    HasOfficialRegistrationWithoutRuntimePlugin: false,
                    HasInvalidMetadata: true);
            }
        }

        private bool ImplementsPlugin(
            ResolvedPackageType type,
            HashSet<ResolvedPackageType> visiting)
        {
            if (_implementsPlugin.TryGetValue(type, out var cached))
                return cached;
            if (!visiting.Add(type))
                throw new BadImageFormatException("Cyclic package-local type inheritance metadata was detected.");

            try
            {
                var definition = type.Assembly.Reader.GetTypeDefinition(type.Handle);
                foreach (var implementationHandle in definition.GetInterfaceImplementations())
                {
                    var implementation = type.Assembly.Reader.GetInterfaceImplementation(implementationHandle);
                    if (AssemblyExtractor.IsDataversePluginInterfaceReference(
                            type.Assembly.Reader,
                            implementation.Interface))
                    {
                        _implementsPlugin[type] = true;
                        return true;
                    }

                    if (TryResolve(type.Assembly, implementation.Interface, [], out var interfaceType)
                        && ImplementsPlugin(interfaceType, visiting))
                    {
                        _implementsPlugin[type] = true;
                        return true;
                    }
                }

                if (!definition.BaseType.IsNil
                    && TryResolve(type.Assembly, definition.BaseType, [], out var baseType)
                    && ImplementsPlugin(baseType, visiting))
                {
                    _implementsPlugin[type] = true;
                    return true;
                }

                _implementsPlugin[type] = false;
                return false;
            }
            finally
            {
                visiting.Remove(type);
            }
        }

        private bool TryResolve(
            BufferedMetadataAssembly context,
            EntityHandle handle,
            HashSet<PackageEntityHandle> visiting,
            out ResolvedPackageType resolved)
        {
            var key = new PackageEntityHandle(context, handle);
            if (!visiting.Add(key))
                throw new BadImageFormatException("Cyclic package-local type reference metadata was detected.");

            try
            {
                switch (handle.Kind)
                {
                    case HandleKind.TypeDefinition:
                        resolved = new ResolvedPackageType(context, (TypeDefinitionHandle)handle);
                        return true;
                    case HandleKind.TypeReference:
                        return TryResolveTypeReference(
                            context,
                            (TypeReferenceHandle)handle,
                            visiting,
                            out resolved);
                    case HandleKind.TypeSpecification:
                        var namedType = context.Reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                            .DecodeSignature(TypeSpecificationDecoder, genericContext: null);
                        if (namedType.IsNil)
                            throw new BadImageFormatException("Package type specification has no named type.");
                        return TryResolve(context, namedType, visiting, out resolved);
                    default:
                        resolved = default;
                        return false;
                }
            }
            finally
            {
                visiting.Remove(key);
            }
        }

        private bool TryResolveTypeReference(
            BufferedMetadataAssembly context,
            TypeReferenceHandle handle,
            HashSet<PackageEntityHandle> visiting,
            out ResolvedPackageType resolved)
        {
            var reference = context.Reader.GetTypeReference(handle);
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                if (!TryResolve(context, reference.ResolutionScope, visiting, out var declaringType))
                {
                    resolved = default;
                    return false;
                }

                foreach (var nestedHandle in declaringType.Assembly.Reader
                    .GetTypeDefinition(declaringType.Handle)
                    .GetNestedTypes())
                {
                    var nested = declaringType.Assembly.Reader.GetTypeDefinition(nestedHandle);
                    if (StringComparer.Ordinal.Equals(
                            declaringType.Assembly.Reader.GetString(nested.Name),
                            context.Reader.GetString(reference.Name)))
                    {
                        resolved = new ResolvedPackageType(declaringType.Assembly, nestedHandle);
                        return true;
                    }
                }

                throw new BadImageFormatException("A package-local nested type reference could not be resolved.");
            }

            BufferedMetadataAssembly target;
            if (reference.ResolutionScope.Kind is HandleKind.ModuleDefinition or HandleKind.ModuleReference)
            {
                target = context;
            }
            else if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                var assemblyReference = context.Reader.GetAssemblyReference(
                    (AssemblyReferenceHandle)reference.ResolutionScope);
                var assemblyName = context.Reader.GetString(assemblyReference.Name);
                if (!_assembliesByName.TryGetValue(assemblyName, out var matches))
                {
                    // The dependency is outside the package (for example a --reference-dir
                    // base class). Portable preflight deliberately does not reload it.
                    resolved = default;
                    return false;
                }

                var identityMatches = matches
                    .Where(match => AssemblyExtractor.AssemblyReferenceMatchesDefinition(
                        context.Reader,
                        (AssemblyReferenceHandle)reference.ResolutionScope,
                        match.Reader))
                    .ToList();
                if (identityMatches.Count != 1)
                {
                    throw new BadImageFormatException(
                        $"Package-local assembly reference '{assemblyName}' has no single full-identity match.");
                }

                target = identityMatches[0];
            }
            else
            {
                resolved = default;
                return false;
            }

            foreach (var typeHandle in target.Reader.TypeDefinitions)
            {
                var definition = target.Reader.GetTypeDefinition(typeHandle);
                if (StringComparer.Ordinal.Equals(
                        target.Reader.GetString(definition.Name),
                        context.Reader.GetString(reference.Name))
                    && StringComparer.Ordinal.Equals(
                        target.Reader.GetString(definition.Namespace),
                        context.Reader.GetString(reference.Namespace)))
                {
                    resolved = new ResolvedPackageType(target, typeHandle);
                    return true;
                }
            }

            throw new BadImageFormatException(
                $"Package-local type reference '{GetTypeReferenceFullName(context.Reader, handle)}' could not be resolved.");
        }

        public void Dispose()
        {
            foreach (var assembly in Assemblies)
                assembly.Dispose();
        }

        private sealed class TypeSpecificationProvider : ISignatureTypeProvider<EntityHandle, object?>
        {
            public EntityHandle GetArrayType(EntityHandle elementType, ArrayShape shape) => default;
            public EntityHandle GetByReferenceType(EntityHandle elementType) => default;
            public EntityHandle GetFunctionPointerType(MethodSignature<EntityHandle> signature) => default;
            public EntityHandle GetGenericInstantiation(
                EntityHandle genericType,
                ImmutableArray<EntityHandle> typeArguments) => genericType;
            public EntityHandle GetGenericMethodParameter(object? genericContext, int index) => default;
            public EntityHandle GetGenericTypeParameter(object? genericContext, int index) => default;
            public EntityHandle GetModifiedType(
                EntityHandle modifier,
                EntityHandle unmodifiedType,
                bool isRequired) => unmodifiedType;
            public EntityHandle GetPinnedType(EntityHandle elementType) => default;
            public EntityHandle GetPointerType(EntityHandle elementType) => default;
            public EntityHandle GetPrimitiveType(PrimitiveTypeCode typeCode) => default;
            public EntityHandle GetSZArrayType(EntityHandle elementType) => default;
            public EntityHandle GetTypeFromDefinition(
                MetadataReader reader,
                TypeDefinitionHandle handle,
                byte rawTypeKind) => handle;
            public EntityHandle GetTypeFromReference(
                MetadataReader reader,
                TypeReferenceHandle handle,
                byte rawTypeKind) => handle;
            public EntityHandle GetTypeFromSpecification(
                MetadataReader reader,
                object? genericContext,
                TypeSpecificationHandle handle,
                byte rawTypeKind) => handle;
        }
    }

    private static bool IsPlausiblePluginAssembly(PluginAssemblyConfig config)
        => config.RuntimePluginTypeNames.Count > 0
            || config.Types.Count > 0
            || config.CustomApis is { Count: > 0 };

    private static bool IsExported(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
        if (definition.GetDeclaringType().IsNil)
            return visibility == TypeAttributes.Public;

        return visibility == TypeAttributes.NestedPublic
            && IsExported(reader, definition.GetDeclaringType());
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

    private sealed record InspectedAssembly(string FileName, PluginAssemblyConfig Config);

    private sealed record BufferedAssemblyImage(string FileName, byte[] Content);

    private sealed class BufferedMetadataAssembly : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly PEReader _peReader;

        internal BufferedMetadataAssembly(BufferedAssemblyImage image)
        {
            FileName = image.FileName;
            _stream = new MemoryStream(image.Content, writable: false);
            _peReader = new PEReader(_stream);
            if (!_peReader.HasMetadata)
                throw new BadImageFormatException($"Assembly '{image.FileName}' has no managed metadata.");

            Reader = _peReader.GetMetadataReader();
            if (!Reader.IsAssembly)
                throw new BadImageFormatException($"File '{image.FileName}' is not an assembly manifest.");

            Name = Reader.GetString(Reader.GetAssemblyDefinition().Name);
        }

        internal string FileName { get; }

        internal string Name { get; }

        internal MetadataReader Reader { get; }

        public void Dispose()
        {
            _peReader.Dispose();
            _stream.Dispose();
        }
    }

    private readonly record struct ResolvedPackageType(
        BufferedMetadataAssembly Assembly,
        TypeDefinitionHandle Handle);

    private readonly record struct PackageEntityHandle(
        BufferedMetadataAssembly Assembly,
        EntityHandle Handle);

    private readonly record struct PortablePluginEvidence(
        bool HasPlausiblePrimary,
        bool HasOfficialRegistrationWithoutRuntimePlugin,
        bool HasInvalidMetadata);

    private sealed record ManifestAssembly(
        string FileName,
        string Name,
        HashSet<string> TypeNames,
        bool HasPortablePluginEvidence,
        bool HasOfficialRegistrationWithoutRuntimePlugin,
        bool HasInvalidMetadata);

    /// <summary>
    /// Verifies that every entry in <paramref name="archiveContent"/> extracts to a location under
    /// <paramref name="destinationDir"/>. Guards against zip-slip entries that escape the temp
    /// directory via ".." or absolute path segments, even if the platform's default mitigation
    /// regresses.
    /// </summary>
    private static void AssertExtractedEntriesContained(
        byte[] archiveContent,
        string archivePath,
        string destinationDir)
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

        using var archiveStream = new MemoryStream(archiveContent, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
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
