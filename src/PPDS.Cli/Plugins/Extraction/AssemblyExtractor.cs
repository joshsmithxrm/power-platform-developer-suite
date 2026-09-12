using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PPDS.Cli.Plugins.Models;
using PPDS.Plugins;

namespace PPDS.Cli.Plugins.Extraction;

/// <summary>
/// Extracts plugin registration information from assemblies using MetadataLoadContext.
/// </summary>
public sealed class AssemblyExtractor : IDisposable
{
    /// <summary>
    /// Logical name of the embedded resource holding the zipped .NET Framework 4.6.2
    /// reference assemblies. Must stay in sync with the <c>EmbedNet462ReferenceAssemblies</c>
    /// target in <c>PPDS.Cli.csproj</c>.
    /// </summary>
    private const string Net462ResourceName = "PPDS.Cli.Resources.Net462ReferenceAssemblies.zip";

    /// <summary>
    /// Zero-byte marker written into the cache directory as the very last extraction step.
    /// Its presence is the cache-validity contract: the cache key is fixed per CLI version,
    /// so a cache partially deleted by a temp cleaner would otherwise pass an "any DLL"
    /// check forever and could never self-heal. See #1326.
    /// </summary>
    internal const string CompletionSentinelFileName = ".complete";

    /// <summary>
    /// Directory of embedded .NET Framework 4.6.2 reference assemblies, extracted once per
    /// process on first use. Null when the embedded resource is unavailable or extraction
    /// failed — in which case the resolver falls back to runtime-directory seeding.
    /// </summary>
    private static readonly Lazy<string?> Net462ReferenceDirectory =
        new(GetNet462ReferenceAssemblyDirectory);

    private readonly MetadataLoadContext _metadataLoadContext;
    private readonly string _assemblyPath;
    private readonly IReadOnlyList<string> _resolverPaths;
    private bool _disposed;

    private AssemblyExtractor(
        MetadataLoadContext metadataLoadContext,
        string assemblyPath,
        IReadOnlyList<string> resolverPaths)
    {
        _metadataLoadContext = metadataLoadContext;
        _assemblyPath = assemblyPath;
        _resolverPaths = resolverPaths;
    }

    /// <summary>
    /// Creates an extractor for the specified assembly.
    /// </summary>
    /// <param name="assemblyPath">Path to the assembly DLL.</param>
    /// <param name="referenceDirs">
    /// Optional additional directories to search for referenced assemblies, seeded ahead of the
    /// embedded reference assemblies and the process runtime directory. Supplied via the
    /// <c>--reference-dir</c> option for plugins whose dependencies live outside the target
    /// assembly's own directory.
    /// </param>
    /// <returns>An extractor instance that must be disposed.</returns>
    public static AssemblyExtractor Create(string assemblyPath, IReadOnlyList<string>? referenceDirs = null)
        => Create(assemblyPath, referenceDirs, includeRuntimeDirectory: true);

    /// <summary>
    /// Internal factory that allows suppressing runtime-directory seeding so tests can
    /// reproduce the single-file packaging environment — where no loose BCL DLLs exist on
    /// disk — and prove the embedded reference assemblies alone are sufficient. See #1294.
    /// </summary>
    internal static AssemblyExtractor Create(
        string assemblyPath,
        IReadOnlyList<string>? referenceDirs,
        bool includeRuntimeDirectory)
    {
        var assemblyPaths = BuildResolverPaths(assemblyPath, referenceDirs, includeRuntimeDirectory);
        var resolver = new PathAssemblyResolver(assemblyPaths);

        // Leave coreAssemblyName null: MetadataLoadContext infers the core assembly from the
        // target assembly's own references. Dataverse plugin assemblies target .NET Framework
        // 4.6.2, so the core resolves to mscorlib — which BuildResolverPaths seeds from the
        // embedded reference assemblies, ordered ahead of the runtime directory so it is found
        // even in a single-file publish (where no loose BCL DLLs exist on disk). Forcing an
        // explicit "mscorlib" would over-constrain the core for any non-net462 input, so null
        // is the more general choice — MLC binds whatever core the target actually references.
        var mlc = new MetadataLoadContext(resolver);

        return new AssemblyExtractor(mlc, assemblyPath, assemblyPaths);
    }

    /// <summary>
    /// Builds the ordered, de-duplicated list of assembly paths used to seed the
    /// <see cref="PathAssemblyResolver"/>. Ordering encodes priority (earlier wins):
    /// <list type="number">
    /// <item>the target assembly's own directory — user-local assemblies win;</item>
    /// <item>caller-supplied <paramref name="referenceDirs"/> (<c>--reference-dir</c>);</item>
    /// <item>the embedded .NET Framework 4.6.2 reference assemblies (fixes single-file
    /// packaging, where no loose BCL DLLs exist on disk — #1294);</item>
    /// <item>the process runtime directory — helps framework-dependent dev runs and
    /// net8-targeted assemblies; empty of loose DLLs in single-file publishes.</item>
    /// </list>
    /// De-duping by simple assembly name (keeping the first occurrence) preserves that priority
    /// and keeps resolution deterministic. <see cref="PathAssemblyResolver"/> tolerates
    /// duplicate simple names, but de-duping guarantees the ordering above wins.
    /// </summary>
    internal static List<string> BuildResolverPaths(
        string assemblyPath,
        IReadOnlyList<string>? referenceDirs,
        bool includeRuntimeDirectory)
    {
        var candidates = new List<string>();

        // 1. Target assembly's own directory.
        var directory = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrEmpty(directory))
            directory = ".";
        candidates.AddRange(Directory.GetFiles(directory, "*.dll"));

        // 2. Caller-supplied reference directories.
        if (referenceDirs != null)
        {
            foreach (var dir in referenceDirs)
            {
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    candidates.AddRange(Directory.GetFiles(dir, "*.dll"));
            }
        }

        // 3. Embedded .NET Framework 4.6.2 reference assemblies.
        var net462Dir = Net462ReferenceDirectory.Value;
        if (net462Dir != null)
            candidates.AddRange(Directory.GetFiles(net462Dir, "*.dll"));

        // 4. Process runtime directory (empty of loose DLLs in single-file publishes).
        if (includeRuntimeDirectory)
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
                candidates.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(candidates.Count);
        foreach (var path in candidates)
        {
            if (seen.Add(Path.GetFileNameWithoutExtension(path)))
                result.Add(path);
        }

        return result;
    }

    /// <summary>
    /// Extracts the embedded .NET Framework 4.6.2 reference assemblies to a stable, content-
    /// addressed cache directory and returns its path, or <c>null</c> if the resource is
    /// unavailable or extraction fails. Never throws — callers fall back to runtime-directory
    /// seeding, so the fix can never make extraction worse than before. See #1294.
    /// </summary>
    internal static string? GetNet462ReferenceAssemblyDirectory()
        => GetNet462ReferenceAssemblyDirectory(cacheBaseDirOverride: null);

    /// <summary>
    /// Overload that redirects the cache base directory so tests can exercise cache
    /// corruption and self-healing against an isolated location instead of the real
    /// per-user cache in the temp directory. See #1326.
    /// </summary>
    internal static string? GetNet462ReferenceAssemblyDirectory(string? cacheBaseDirOverride)
    {
        try
        {
            var assembly = typeof(AssemblyExtractor).Assembly;
            using var resourceStream = assembly.GetManifestResourceStream(Net462ResourceName);
            if (resourceStream == null)
                return null;

            using var buffer = new MemoryStream();
            resourceStream.CopyTo(buffer);
            var zipBytes = buffer.ToArray();
            if (zipBytes.Length == 0)
                return null;

            // Content-addressed cache key: identical content reuses the cache, while a rebuilt
            // resource (new CLI version) lands in a fresh directory, so there is no stale cache.
            var hash = Convert.ToHexString(SHA256.HashData(zipBytes))[..16].ToLowerInvariant();

            var baseDir = cacheBaseDirOverride;
            if (baseDir == null)
            {
                // Scope the cache to the current user. On multi-user systems Path.GetTempPath() can be
                // a shared directory (e.g. /tmp on Linux), so a fixed "ppds/net462-ref" path would be
                // created by the first user and then throw UnauthorizedAccessException for every other
                // account — which the outer catch swallows, silently disabling the fix for them.
                // Isolating by user name gives each account its own cache. See #1294.
                var rawUser = Environment.UserName;
                var userScope = string.IsNullOrWhiteSpace(rawUser)
                    ? "default"
                    : string.Join("_", rawUser.Split(Path.GetInvalidFileNameChars()));
                baseDir = Path.Combine(Path.GetTempPath(), $"ppds-{userScope}", "net462-ref");
            }

            var cacheDir = Path.Combine(baseDir, hash);

            if (IsPopulated(cacheDir))
                return cacheDir;

            // Self-heal: a cache directory that exists but fails validation is corrupt — e.g. a
            // temp cleaner deleted the sentinel or some DLLs but kept the directory. The cache key
            // is fixed per CLI version, so without deleting it here the corrupt directory would
            // never recover (and Directory.Move below would throw on the existing destination on
            // every run). See #1326.
            TryDeleteDirectory(cacheDir);

            // Extract to a unique staging directory, then atomically move it into place so a
            // concurrent extraction never observes a half-populated cache directory.
            var stagingDir = Path.Combine(baseDir, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);
            try
            {
                using (var zipStream = new MemoryStream(zipBytes, writable: false))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    archive.ExtractToDirectory(stagingDir);
                }

                // Completion sentinel, written only after every entry extracted: validation
                // treats any cache directory without it as incomplete.
                File.WriteAllBytes(Path.Combine(stagingDir, CompletionSentinelFileName), []);

                if (IsPopulated(cacheDir))
                    return cacheDir; // Another process won the race while we were extracting.

                Directory.CreateDirectory(Path.GetDirectoryName(cacheDir)!);
                try
                {
                    Directory.Move(stagingDir, cacheDir);
                }
                catch (IOException) when (IsPopulated(cacheDir))
                {
                    // Race: another process created a complete cache between our check and move.
                    return cacheDir;
                }
                catch (IOException)
                {
                    // The destination exists but is invalid (e.g. re-created mid-run by a temp
                    // cleaner or a concurrent loser): delete it and retry the move once. If the
                    // retry loses to a concurrent winner, its cache is complete — use it.
                    TryDeleteDirectory(cacheDir);
                    try
                    {
                        Directory.Move(stagingDir, cacheDir);
                    }
                    catch (IOException) when (IsPopulated(cacheDir))
                    {
                        return cacheDir;
                    }
                }

                return cacheDir;
            }
            finally
            {
                TryDeleteDirectory(stagingDir);
            }
        }
        catch (Exception ex)
        {
            // Never make things worse — fall back to runtime-directory seeding. But say so
            // (stderr — stdout is reserved for data): in a single-file install that fallback
            // has no loose BCL DLLs, so extraction will likely fail downstream with the
            // confusing "could not find core assembly" error. See #1326.
            Console.Error.WriteLine(
                "Warning: failed to extract embedded .NET Framework 4.6.2 reference assemblies; " +
                $"plugin extraction may fail for single-file installs: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A cache directory is valid only when the completion sentinel (written as the last
    /// extraction step) and the core assembly are both present. Checking mscorlib rather
    /// than "any DLL" lets a partially deleted cache fail validation and self-heal instead
    /// of passing forever. See #1326.
    /// </summary>
    private static bool IsPopulated(string directory)
        => File.Exists(Path.Combine(directory, CompletionSentinelFileName))
           && File.Exists(Path.Combine(directory, "mscorlib.dll"));

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a leftover staging dir is harmless.
        }
    }

    /// <summary>
    /// Extracts plugin registration configuration from the assembly.
    /// </summary>
    /// <returns>Assembly configuration with all plugin types and steps.</returns>
    public PluginAssemblyConfig Extract()
    {
        var assembly = _metadataLoadContext.LoadFromAssemblyPath(_assemblyPath);
        var assemblyName = assembly.GetName();
        var runtimePluginTypeNames = ReadRuntimePluginTypeNames(_assemblyPath, _resolverPaths);

        var config = new PluginAssemblyConfig
        {
            Name = assemblyName.Name ?? Path.GetFileNameWithoutExtension(_assemblyPath),
            Type = "Assembly",
            Path = Path.GetFileName(_assemblyPath),
            AllTypeNames = [],
            Types = []
        };

        var customApis = new List<CustomApiConfig>();

        // Get all exported types (public, non-abstract, non-interface)
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;

            var typeName = type.FullName ?? type.Name;

            // Runtime plugin types are part of the package identity even when they do not use
            // PPDS registration attributes. Read this from raw metadata rather than calling
            // Type.GetInterfaces(): a normal Dataverse package references Microsoft.Xrm.Sdk but
            // does not bundle it, and MetadataLoadContext cannot resolve that interface from a
            // self-contained single-file CLI. This keeps allTypeNames truthful without inventing
            // a PluginTypeConfig (and therefore without inventing a registration step).
            if (runtimePluginTypeNames.Contains(typeName))
            {
                config.AllTypeNames.Add(typeName);
                config.RuntimePluginTypeNames.Add(typeName);
            }

            // Extract Custom API if annotated
            var customApiAttr = GetCustomApiAttribute(type);
            if (customApiAttr != null)
            {
                var parameterAttributes = GetCustomApiParameterAttributes(type);
                var apiConfig = MapCustomApiAttribute(customApiAttr, type, parameterAttributes);
                customApis.Add(apiConfig);
            }

            // Check if type has plugin step attributes
            var stepAttributes = GetPluginStepAttributes(type);
            if (stepAttributes.Count == 0)
                continue;

            // Track all plugin type names for orphan detection
            if (!config.AllTypeNames.Contains(typeName, StringComparer.Ordinal))
                config.AllTypeNames.Add(typeName);

            var pluginType = new PluginTypeConfig
            {
                TypeName = typeName,
                Steps = []
            };

            var imageAttributes = GetPluginImageAttributes(type);

            foreach (var stepAttr in stepAttributes)
            {
                var step = MapStepAttribute(stepAttr, type);

                // Find images for this step
                var stepImages = imageAttributes
                    .Where(img => MatchesStep(img, stepAttr))
                    .Select(MapImageAttribute)
                    .ToList();

                step.Images = stepImages;
                pluginType.Steps.Add(step);
            }

            config.Types.Add(pluginType);
        }

        if (customApis.Count > 0)
            config.CustomApis = customApis;

        return config;
    }

    /// <summary>
    /// Reads public, concrete types that implement <c>Microsoft.Xrm.Sdk.IPlugin</c> directly or
    /// through resolvable base classes and derived interfaces. The SDK interface itself is matched
    /// by metadata name, so a normal Dataverse package does not need to bundle Microsoft.Xrm.Sdk.
    /// Other referenced types are resolved from the same ordered paths as MetadataLoadContext,
    /// including caller-supplied <c>--reference-dir</c> paths.
    /// </summary>
    private static HashSet<string> ReadRuntimePluginTypeNames(
        string assemblyPath,
        IReadOnlyList<string> resolverPaths)
    {
        const string pluginInterfaceName = "Microsoft.Xrm.Sdk.IPlugin";

        using var resolver = new MetadataTypeResolver(resolverPaths);
        var target = resolver.LoadAssembly(assemblyPath);
        var implementsPlugin = new Dictionary<ResolvedMetadataType, bool>();

        bool ImplementsPlugin(
            ResolvedMetadataType type,
            HashSet<ResolvedMetadataType> visiting,
            string exportedTypeName)
        {
            if (implementsPlugin.TryGetValue(type, out var cached))
                return cached;

            if (!visiting.Add(type))
                return false;

            try
            {
                var definition = type.Assembly.Reader.GetTypeDefinition(type.Handle);

                // Interfaces can inherit IPlugin through other interfaces, including interfaces
                // declared in another assembly. Check the well-known SDK metadata name before
                // attempting resolution because Microsoft.Xrm.Sdk is intentionally not bundled.
                var interfaces = definition.GetInterfaceImplementations()
                    .Select(type.Assembly.Reader.GetInterfaceImplementation)
                    .ToList();
                if (interfaces.Any(implementation =>
                        resolver.GetTypeFullName(type.Assembly, implementation.Interface) == pluginInterfaceName))
                {
                    implementsPlugin[type] = true;
                    return true;
                }

                foreach (var implementation in interfaces)
                {
                    if (!resolver.TryResolve(type.Assembly, implementation.Interface, out var interfaceType))
                    {
                        throw resolver.CreateUnresolvedTypeException(
                            exportedTypeName,
                            type.Assembly,
                            implementation.Interface);
                    }

                    if (ImplementsPlugin(interfaceType, visiting, exportedTypeName))
                    {
                        implementsPlugin[type] = true;
                        return true;
                    }
                }

                if (!definition.BaseType.IsNil)
                {
                    if (!resolver.TryResolve(type.Assembly, definition.BaseType, out var baseType))
                    {
                        throw resolver.CreateUnresolvedTypeException(
                            exportedTypeName,
                            type.Assembly,
                            definition.BaseType);
                    }

                    if (ImplementsPlugin(baseType, visiting, exportedTypeName))
                    {
                        implementsPlugin[type] = true;
                        return true;
                    }
                }

                implementsPlugin[type] = false;
                return false;
            }
            finally
            {
                visiting.Remove(type);
            }
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in target.Reader.TypeDefinitions)
        {
            var definition = target.Reader.GetTypeDefinition(handle);
            var isExported = IsExported(target.Reader, handle);
            var isAbstract = (definition.Attributes & TypeAttributes.Abstract) != 0;
            var isInterface = (definition.Attributes & TypeAttributes.Interface) != 0;
            var typeName = GetTypeDefinitionFullName(target.Reader, handle);

            if (isExported
                && !isAbstract
                && !isInterface
                && ImplementsPlugin(new ResolvedMetadataType(target, handle), [], typeName))
            {
                result.Add(typeName);
            }
        }

        return result;
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

    private sealed class MetadataTypeResolver : IDisposable
    {
        private static readonly TypeSpecificationProvider TypeSpecificationDecoder = new();
        private readonly Dictionary<string, string> _pathsByAssemblyName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MetadataAssembly> _assembliesByPath =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly IReadOnlyList<string> _resolverPaths;

        internal MetadataTypeResolver(IEnumerable<string> resolverPaths)
        {
            _resolverPaths = resolverPaths.ToList();
            foreach (var path in _resolverPaths)
            {
                // Assembly references conventionally match the DLL filename. Seed that cheap
                // lookup eagerly; if a dependency DLL was renamed, TryFindAssemblyPath falls
                // back to reading manifest identities lazily. This avoids opening unrelated
                // resolver files merely to inspect one inheritance chain.
                _pathsByAssemblyName.TryAdd(Path.GetFileNameWithoutExtension(path), path);
            }
        }

        internal MetadataAssembly LoadAssembly(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (_assembliesByPath.TryGetValue(fullPath, out var existing))
                return existing;

            var stream = File.OpenRead(fullPath);
            try
            {
                var peReader = new PEReader(stream);
                try
                {
                    if (!peReader.HasMetadata)
                    {
                        throw new BadImageFormatException($"Assembly '{path}' does not contain managed metadata.");
                    }

                    var reader = peReader.GetMetadataReader();
                    if (!reader.IsAssembly)
                    {
                        throw new BadImageFormatException($"File '{path}' is not a managed assembly manifest.");
                    }

                    var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
                    var assembly = new MetadataAssembly(stream, peReader, reader);
                    _assembliesByPath.Add(fullPath, assembly);
                    _pathsByAssemblyName.TryAdd(assemblyName, fullPath);
                    return assembly;
                }
                catch
                {
                    peReader.Dispose();
                    throw;
                }
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal string? GetTypeFullName(MetadataAssembly assembly, EntityHandle handle)
        {
            return GetTypeFullName(assembly, handle, []);
        }

        private string? GetTypeFullName(
            MetadataAssembly assembly,
            EntityHandle handle,
            HashSet<MetadataEntityHandle> visiting)
        {
            var key = new MetadataEntityHandle(assembly, handle);
            if (!visiting.Add(key))
                return null;

            try
            {
                return handle.Kind switch
                {
                    HandleKind.TypeDefinition =>
                        GetTypeDefinitionFullName(assembly.Reader, (TypeDefinitionHandle)handle),
                    HandleKind.TypeReference =>
                        GetTypeReferenceFullName(assembly.Reader, (TypeReferenceHandle)handle),
                    HandleKind.TypeSpecification => GetTypeSpecificationFullName(
                        assembly,
                        (TypeSpecificationHandle)handle,
                        visiting),
                    _ => null
                };
            }
            finally
            {
                visiting.Remove(key);
            }
        }

        internal bool TryResolve(
            MetadataAssembly context,
            EntityHandle handle,
            out ResolvedMetadataType resolved)
        {
            return TryResolve(context, handle, [], out resolved);
        }

        private bool TryResolve(
            MetadataAssembly context,
            EntityHandle handle,
            HashSet<MetadataEntityHandle> visiting,
            out ResolvedMetadataType resolved)
        {
            var key = new MetadataEntityHandle(context, handle);
            if (!visiting.Add(key))
            {
                resolved = default;
                return false;
            }

            try
            {
                if (handle.Kind == HandleKind.TypeDefinition)
                {
                    resolved = new ResolvedMetadataType(context, (TypeDefinitionHandle)handle);
                    return true;
                }

                if (handle.Kind == HandleKind.TypeReference)
                {
                    return TryResolveTypeReference(
                        context,
                        (TypeReferenceHandle)handle,
                        visiting,
                        out resolved);
                }

                if (handle.Kind == HandleKind.TypeSpecification)
                {
                    if (!TryDecodeTypeSpecification(
                            context.Reader,
                            (TypeSpecificationHandle)handle,
                            out var genericType))
                    {
                        resolved = default;
                        return false;
                    }

                    return TryResolve(context, genericType, visiting, out resolved);
                }

                resolved = default;
                return false;
            }
            finally
            {
                visiting.Remove(key);
            }
        }

        private string? GetTypeSpecificationFullName(
            MetadataAssembly context,
            TypeSpecificationHandle handle,
            HashSet<MetadataEntityHandle> visiting)
        {
            return TryDecodeTypeSpecification(context.Reader, handle, out var genericType)
                ? GetTypeFullName(context, genericType, visiting)
                : null;
        }

        private static bool TryDecodeTypeSpecification(
            MetadataReader reader,
            TypeSpecificationHandle handle,
            out EntityHandle namedType)
        {
            try
            {
                namedType = reader.GetTypeSpecification(handle)
                    .DecodeSignature(TypeSpecificationDecoder, genericContext: null);
                return !namedType.IsNil;
            }
            catch (BadImageFormatException)
            {
                namedType = default;
                return false;
            }
        }

        private sealed class TypeSpecificationProvider :
            ISignatureTypeProvider<EntityHandle, object?>
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

        internal InvalidOperationException CreateUnresolvedTypeException(
            string exportedTypeName,
            MetadataAssembly context,
            EntityHandle handle)
        {
            var referencedType = GetTypeFullName(context, handle) ?? $"metadata handle {handle.Kind}";
            return new InvalidOperationException(
                $"Could not determine whether exported type '{exportedTypeName}' implements " +
                $"Microsoft.Xrm.Sdk.IPlugin because referenced type '{referencedType}' could not be resolved. " +
                "Place the dependency beside the plugin assembly or pass its directory with --reference-dir.");
        }

        private bool TryResolveTypeReference(
            MetadataAssembly context,
            TypeReferenceHandle handle,
            HashSet<MetadataEntityHandle> visiting,
            out ResolvedMetadataType resolved)
        {
            var reference = context.Reader.GetTypeReference(handle);
            var name = context.Reader.GetString(reference.Name);

            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                if (!TryResolve(
                        context,
                        reference.ResolutionScope,
                        visiting,
                        out var declaringType))
                {
                    resolved = default;
                    return false;
                }

                var declaringDefinition = declaringType.Assembly.Reader.GetTypeDefinition(declaringType.Handle);
                foreach (var nestedHandle in declaringDefinition.GetNestedTypes())
                {
                    var nested = declaringType.Assembly.Reader.GetTypeDefinition(nestedHandle);
                    if (declaringType.Assembly.Reader.StringComparer.Equals(nested.Name, name))
                    {
                        resolved = new ResolvedMetadataType(declaringType.Assembly, nestedHandle);
                        return true;
                    }
                }

                resolved = default;
                return false;
            }

            MetadataAssembly targetAssembly;
            if (reference.ResolutionScope.Kind is HandleKind.ModuleDefinition or HandleKind.ModuleReference)
            {
                targetAssembly = context;
            }
            else if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                var assemblyReference = context.Reader.GetAssemblyReference(
                    (AssemblyReferenceHandle)reference.ResolutionScope);
                var assemblyName = context.Reader.GetString(assemblyReference.Name);
                if (!TryFindAssemblyPath(assemblyName, out var path))
                {
                    resolved = default;
                    return false;
                }

                targetAssembly = LoadAssembly(path);
            }
            else
            {
                resolved = default;
                return false;
            }

            var @namespace = context.Reader.GetString(reference.Namespace);
            foreach (var candidateHandle in targetAssembly.Reader.TypeDefinitions)
            {
                var candidate = targetAssembly.Reader.GetTypeDefinition(candidateHandle);
                if (!candidate.GetDeclaringType().IsNil)
                    continue;

                if (targetAssembly.Reader.StringComparer.Equals(candidate.Name, name)
                    && targetAssembly.Reader.StringComparer.Equals(candidate.Namespace, @namespace))
                {
                    resolved = new ResolvedMetadataType(targetAssembly, candidateHandle);
                    return true;
                }
            }

            resolved = default;
            return false;
        }

        private bool TryFindAssemblyPath(string assemblyName, out string path)
        {
            if (_pathsByAssemblyName.TryGetValue(assemblyName, out path!))
            {
                var actualName = TryReadAssemblyName(path);
                if (string.Equals(actualName, assemblyName, StringComparison.OrdinalIgnoreCase))
                    return true;

                _pathsByAssemblyName.Remove(assemblyName);
            }

            // A dependency DLL can be renamed independently of its manifest identity. Only when
            // the filename convention fails do we inspect resolver manifests, tolerating native,
            // inaccessible, or resource-only DLLs that are unrelated to the required type.
            foreach (var candidate in _resolverPaths)
            {
                var actualName = TryReadAssemblyName(candidate);
                if (!string.IsNullOrEmpty(actualName))
                    _pathsByAssemblyName.TryAdd(actualName, candidate);

                if (string.Equals(actualName, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    path = candidate;
                    return true;
                }
            }

            path = string.Empty;
            return false;
        }

        private static string? TryReadAssemblyName(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata)
                    return null;

                var reader = peReader.GetMetadataReader();
                return reader.IsAssembly
                    ? reader.GetString(reader.GetAssemblyDefinition().Name)
                    : null;
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            foreach (var assembly in _assembliesByPath.Values)
                assembly.Dispose();
        }
    }

    private sealed class MetadataAssembly : IDisposable
    {
        private readonly Stream _stream;
        private readonly PEReader _peReader;

        internal MetadataAssembly(
            Stream stream,
            PEReader peReader,
            MetadataReader reader)
        {
            _stream = stream;
            _peReader = peReader;
            Reader = reader;
        }

        internal MetadataReader Reader { get; }

        public void Dispose()
        {
            _peReader.Dispose();
            _stream.Dispose();
        }
    }

    private readonly record struct ResolvedMetadataType(
        MetadataAssembly Assembly,
        TypeDefinitionHandle Handle);

    private readonly record struct MetadataEntityHandle(
        MetadataAssembly Assembly,
        EntityHandle Handle);

    private static string GetTypeDefinitionFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        if (!definition.GetDeclaringType().IsNil)
            return $"{GetTypeDefinitionFullName(reader, definition.GetDeclaringType())}+{name}";

        var @namespace = reader.GetString(definition.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
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

    private List<CustomAttributeData> GetPluginStepAttributes(Type type)
    {
        return type.CustomAttributes
            .Where(a => a.AttributeType.FullName == typeof(PluginStepAttribute).FullName)
            .ToList();
    }

    private List<CustomAttributeData> GetPluginImageAttributes(Type type)
    {
        return type.CustomAttributes
            .Where(a => a.AttributeType.FullName == typeof(PluginImageAttribute).FullName)
            .ToList();
    }

    private CustomAttributeData? GetCustomApiAttribute(Type type)
    {
        return type.CustomAttributes
            .FirstOrDefault(a => a.AttributeType.FullName == typeof(CustomApiAttribute).FullName);
    }

    private List<CustomAttributeData> GetCustomApiParameterAttributes(Type type)
    {
        return type.CustomAttributes
            .Where(a => a.AttributeType.FullName == typeof(CustomApiParameterAttribute).FullName)
            .ToList();
    }

    private static CustomApiConfig MapCustomApiAttribute(
        CustomAttributeData attr,
        Type pluginType,
        List<CustomAttributeData> parameterAttrs)
    {
        var api = new CustomApiConfig
        {
            PluginTypeName = pluginType.FullName ?? pluginType.Name
        };

        foreach (var namedArg in attr.NamedArguments)
        {
            var value = namedArg.TypedValue.Value;
            switch (namedArg.MemberName)
            {
                case "UniqueName":
                    api.UniqueName = value?.ToString() ?? string.Empty;
                    break;
                case "DisplayName":
                    api.DisplayName = value?.ToString() ?? string.Empty;
                    break;
                case "Name":
                    api.Name = value?.ToString();
                    break;
                case "Description":
                    api.Description = value?.ToString();
                    break;
                case "BindingType":
                    var bindingStr = MapBindingTypeValue(value);
                    // Only write non-default (Global = 0)
                    if (bindingStr != "Global")
                        api.BindingType = bindingStr;
                    break;
                case "BoundEntity":
                    api.BoundEntity = value?.ToString();
                    break;
                case "IsFunction":
                    api.IsFunction = value is true;
                    break;
                case "IsPrivate":
                    api.IsPrivate = value is true;
                    break;
                case "ExecutePrivilegeName":
                    api.ExecutePrivilegeName = value?.ToString();
                    break;
                case "AllowedProcessingStepType":
                    var stepTypeStr = MapProcessingStepTypeValue(value);
                    // Only write non-default (None = 0)
                    if (stepTypeStr != "None")
                        api.AllowedProcessingStepType = stepTypeStr;
                    break;
            }
        }

        if (parameterAttrs.Count > 0)
        {
            api.Parameters = parameterAttrs
                .Select(MapCustomApiParameterAttribute)
                .ToList();
        }

        return api;
    }

    private static CustomApiParameterConfig MapCustomApiParameterAttribute(CustomAttributeData attr)
    {
        var param = new CustomApiParameterConfig();

        foreach (var namedArg in attr.NamedArguments)
        {
            var value = namedArg.TypedValue.Value;
            switch (namedArg.MemberName)
            {
                case "Name":
                    param.Name = value?.ToString() ?? string.Empty;
                    break;
                case "UniqueName":
                    param.UniqueName = value?.ToString();
                    break;
                case "DisplayName":
                    param.DisplayName = value?.ToString();
                    break;
                case "Description":
                    param.Description = value?.ToString();
                    break;
                case "Type":
                    param.Type = MapParameterTypeValue(value);
                    break;
                case "LogicalEntityName":
                    param.LogicalEntityName = value?.ToString();
                    break;
                case "IsOptional":
                    param.IsOptional = value is true;
                    break;
                case "Direction":
                    param.Direction = MapParameterDirectionValue(value);
                    break;
            }
        }

        return param;
    }

    private static string MapBindingTypeValue(object? value)
    {
        // ApiBindingType enum: Global=0, Entity=1, EntityCollection=2
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "Global",
                1 => "Entity",
                2 => "EntityCollection",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "Global";
    }

    private static string MapProcessingStepTypeValue(object? value)
    {
        // ApiProcessingStepType enum: None=0, AsyncOnly=1, SyncAndAsync=2
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "None",
                1 => "AsyncOnly",
                2 => "SyncAndAsync",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "None";
    }

    private static string MapParameterTypeValue(object? value)
    {
        // ApiParameterType enum: Boolean=0, DateTime=1, Decimal=2, Entity=3,
        // EntityCollection=4, EntityReference=5, Float=6, Integer=7, Money=8,
        // Picklist=9, String=10, StringArray=11, Guid=12
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "Boolean",
                1 => "DateTime",
                2 => "Decimal",
                3 => "Entity",
                4 => "EntityCollection",
                5 => "EntityReference",
                6 => "Float",
                7 => "Integer",
                8 => "Money",
                9 => "Picklist",
                10 => "String",
                11 => "StringArray",
                12 => "Guid",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "String";
    }

    private static string MapParameterDirectionValue(object? value)
    {
        // ParameterDirection enum: Input=0, Output=1
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "Input",
                1 => "Output",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "Input";
    }

    private static PluginStepConfig MapStepAttribute(CustomAttributeData attr, Type pluginType)
    {
        var step = new PluginStepConfig();

        // Handle constructor arguments
        var ctorParams = attr.Constructor.GetParameters();
        for (var i = 0; i < attr.ConstructorArguments.Count; i++)
        {
            var paramName = ctorParams[i].Name;
            var value = attr.ConstructorArguments[i].Value;

            switch (paramName)
            {
                case "message":
                    step.Message = value?.ToString() ?? string.Empty;
                    break;
                case "entityLogicalName":
                    step.Entity = value?.ToString() ?? string.Empty;
                    break;
                case "stage":
                    step.Stage = MapStageValue(value);
                    break;
            }
        }

        // Handle named arguments
        foreach (var namedArg in attr.NamedArguments)
        {
            var value = namedArg.TypedValue.Value;
            switch (namedArg.MemberName)
            {
                case "Message":
                    step.Message = value?.ToString() ?? string.Empty;
                    break;
                case "EntityLogicalName":
                    step.Entity = value?.ToString() ?? string.Empty;
                    break;
                case "SecondaryEntityLogicalName":
                    step.SecondaryEntity = value?.ToString();
                    break;
                case "Stage":
                    step.Stage = MapStageValue(value);
                    break;
                case "Mode":
                    step.Mode = MapModeValue(value);
                    break;
                case "FilteringAttributes":
                    step.FilteringAttributes = value?.ToString();
                    break;
                case "ExecutionOrder":
                    step.ExecutionOrder = value is int order ? order : 1;
                    break;
                case "Name":
                    step.Name = value?.ToString();
                    break;
                case "UnsecureConfiguration":
                    step.UnsecureConfiguration = value?.ToString();
                    break;
                case "Description":
                    step.Description = value?.ToString();
                    break;
                case "AsyncAutoDelete":
                    step.AsyncAutoDelete = value is true;
                    break;
                case "StepId":
                    step.StepId = value?.ToString();
                    break;
                case "Deployment":
                    // Only write non-default value (default is ServerOnly = 0)
                    var deploymentStr = MapDeploymentValue(value);
                    if (deploymentStr != "ServerOnly")
                        step.Deployment = deploymentStr;
                    break;
                case "RunAsUser":
                    step.RunAsUser = value?.ToString();
                    break;
                case "CanBeBypassed":
                    // Default is true — only write when false (non-default)
                    if (value is false)
                        step.CanBeBypassed = false;
                    break;
                case "CanUseReadOnlyConnection":
                    // Default is false — only write when true (non-default)
                    if (value is true)
                        step.CanUseReadOnlyConnection = true;
                    break;
                case "InvocationSource":
                    // Default is Parent = 0 — only write when Child (non-default)
                    var invocationStr = MapInvocationSourceValue(value);
                    if (invocationStr != "Parent")
                        step.InvocationSource = invocationStr;
                    break;
            }
        }

        // Auto-generate name if not specified
        if (string.IsNullOrEmpty(step.Name))
        {
            var typeName = pluginType.Name;
            step.Name = $"{typeName}: {step.Message} of {step.Entity}";
        }

        return step;
    }

    private static PluginImageConfig MapImageAttribute(CustomAttributeData attr)
    {
        var image = new PluginImageConfig();

        // Handle constructor arguments
        var ctorParams = attr.Constructor.GetParameters();
        for (var i = 0; i < attr.ConstructorArguments.Count; i++)
        {
            var paramName = ctorParams[i].Name;
            var value = attr.ConstructorArguments[i].Value;

            switch (paramName)
            {
                case "imageType":
                    image.ImageType = MapImageTypeValue(value);
                    break;
                case "name":
                    image.Name = value?.ToString() ?? string.Empty;
                    break;
                case "attributes":
                    image.Attributes = value?.ToString();
                    break;
            }
        }

        // Handle named arguments
        foreach (var namedArg in attr.NamedArguments)
        {
            var value = namedArg.TypedValue.Value;
            switch (namedArg.MemberName)
            {
                case "ImageType":
                    image.ImageType = MapImageTypeValue(value);
                    break;
                case "Name":
                    image.Name = value?.ToString() ?? string.Empty;
                    break;
                case "Attributes":
                    image.Attributes = value?.ToString();
                    break;
                case "EntityAlias":
                    image.EntityAlias = value?.ToString();
                    break;
                case "Description":
                    image.Description = value?.ToString();
                    break;
                case "MessagePropertyName":
                    image.MessagePropertyName = value?.ToString();
                    break;
            }
        }

        // Default entityAlias to name if not explicitly specified
        image.EntityAlias ??= image.Name;

        return image;
    }

    private static bool MatchesStep(CustomAttributeData imageAttr, CustomAttributeData stepAttr)
    {
        // Get StepId from both attributes using LINQ for clearer intent
        var imageStepId = imageAttr.NamedArguments
            .FirstOrDefault(na => na.MemberName == "StepId")
            .TypedValue.Value?.ToString();

        var stepStepId = stepAttr.NamedArguments
            .FirstOrDefault(na => na.MemberName == "StepId")
            .TypedValue.Value?.ToString();

        // If image has no StepId, it applies to all steps (or the only step)
        if (string.IsNullOrEmpty(imageStepId))
            return true;

        // If image has StepId, it must match the step's StepId
        return string.Equals(imageStepId, stepStepId, StringComparison.Ordinal);
    }

    private static string MapStageValue(object? value)
    {
        // Handle enum by underlying value
        if (value is int intValue)
        {
            return intValue switch
            {
                10 => "PreValidation",
                20 => "PreOperation",
                30 => "MainOperation",
                40 => "PostOperation",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "PostOperation";
    }

    private static string MapModeValue(object? value)
    {
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "Synchronous",
                1 => "Asynchronous",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "Synchronous";
    }

    private static string MapImageTypeValue(object? value)
    {
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "PreImage",
                1 => "PostImage",
                2 => "Both",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "PreImage";
    }

    private static string MapDeploymentValue(object? value)
    {
        // PluginDeployment enum: ServerOnly=0, Offline=1, Both=2
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "ServerOnly",
                1 => "Offline",
                2 => "Both",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "ServerOnly";
    }

    private static string MapInvocationSourceValue(object? value)
    {
        // PluginInvocationSource enum: Parent=0, Child=1
        if (value is int intValue)
        {
            return intValue switch
            {
                0 => "Parent",
                1 => "Child",
                _ => intValue.ToString()
            };
        }

        return value?.ToString() ?? "Parent";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _metadataLoadContext.Dispose();
        _disposed = true;
    }
}
