using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PPDS.Cli.Plugins.Extraction;

namespace PPDS.Cli.Tests.Plugins;

internal sealed record TestPackageAssembly(
    string AssemblyName,
    string FileName,
    string Source,
    bool ReferencesSdk = false,
    bool ReferencesPpdsPlugins = false,
    bool IncludeInPackage = true,
    IReadOnlyList<string>? AssemblyReferences = null,
    byte[]? StrongNamePublicKey = null,
    byte[]? PrecompiledImage = null);

internal static class PluginPackageTestFixture
{
    internal static string Create(
        string directory,
        string fileName,
        string packageId,
        params TestPackageAssembly[] assemblies)
    {
        var nupkgPath = Path.Combine(directory, fileName);
        using var stream = File.Create(nupkgPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var nuspec = archive.CreateEntry($"{packageId}.nuspec");
        using (var writer = new StreamWriter(nuspec.Open(), Encoding.UTF8))
        {
            writer.Write($$"""
                <?xml version="1.0"?>
                <package><metadata><id>{{packageId}}</id><version>1.0.0</version></metadata></package>
                """);
        }

        var compiledAssemblies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            var image = Compile(assembly, compiledAssemblies);
            // Replacing an earlier reference image lets adversarial tests compile against one
            // strong-name identity and then package a same-simple-name assembly with a different
            // identity. Normal fixtures still use unique names.
            compiledAssemblies[assembly.AssemblyName] = image;

            if (assembly.IncludeInPackage)
            {
                var entry = archive.CreateEntry($"lib/net462/{assembly.FileName}");
                using var entryStream = entry.Open();
                entryStream.Write(image);
            }
            else
            {
                File.WriteAllBytes(Path.Combine(directory, assembly.FileName), image);
            }
        }

        if (assemblies.Any(assembly => assembly.ReferencesPpdsPlugins))
        {
            var attributesEntry = archive.CreateEntry("lib/net462/PPDS.Plugins.dll");
            using var attributesStream = attributesEntry.Open();
            using var attributesFile = File.OpenRead(typeof(PPDS.Plugins.PluginStepAttribute).Assembly.Location);
            attributesFile.CopyTo(attributesStream);
        }

        return nupkgPath;
    }

    private static byte[] Compile(
        TestPackageAssembly source,
        IReadOnlyDictionary<string, byte[]> compiledAssemblies)
    {
        if (source.PrecompiledImage != null)
            return source.PrecompiledImage;

        var referenceDirectory = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory();
        if (referenceDirectory == null)
            throw new InvalidOperationException("The embedded net462 reference assemblies are unavailable.");

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(Path.Combine(referenceDirectory, "mscorlib.dll"))
        };

        if (source.ReferencesSdk)
        {
            references.Add(MetadataReference.CreateFromFile(Path.Combine(
                AppContext.BaseDirectory,
                "TestAssets",
                "Microsoft.Xrm.Sdk.net462.dll")));
        }
        if (source.ReferencesPpdsPlugins)
            references.Add(MetadataReference.CreateFromFile(typeof(PPDS.Plugins.PluginStepAttribute).Assembly.Location));
        if (source.AssemblyReferences != null)
        {
            foreach (var assemblyName in source.AssemblyReferences)
            {
                if (!compiledAssemblies.TryGetValue(assemblyName, out var image))
                {
                    throw new InvalidOperationException(
                        $"Test assembly reference '{assemblyName}' must be declared before '{source.AssemblyName}'.");
                }

                references.Add(MetadataReference.CreateFromImage(image));
            }
        }

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        if (source.StrongNamePublicKey is { Length: > 0 })
        {
            options = options
                .WithCryptoPublicKey(ImmutableArray.Create(source.StrongNamePublicKey))
                .WithPublicSign(true);
        }

        var compilation = CSharpCompilation.Create(
            source.AssemblyName,
            [CSharpSyntaxTree.ParseText(source.Source)],
            references,
            options);

        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        }

        return output.ToArray();
    }

    /// <summary>
    /// Emits the minimal metadata shape C# cannot preserve: a concrete class whose only
    /// InterfaceImpl is an externally declared derived interface. Roslyn flattens IPlugin onto
    /// the class, which would bypass the external AssemblyRef identity branch this fixture is
    /// intended to exercise.
    /// </summary>
    internal static byte[] CreateExternalDerivedInterfaceConsumerImage(
        string referencedAssemblyName,
        byte[] referencedAssemblyPublicKey)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(
            generation: 0,
            metadata.GetOrAddString("Contoso.RuntimePlugins.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()),
            default,
            default);
        metadata.AddAssembly(
            metadata.GetOrAddString("Contoso.RuntimePlugins"),
            new Version(1, 0, 0, 0),
            default,
            default,
            (AssemblyFlags)0,
            AssemblyHashAlgorithm.None);

        var mscorlib = metadata.AddAssemblyReference(
            metadata.GetOrAddString("mscorlib"),
            new Version(4, 0, 0, 0),
            default,
            metadata.GetOrAddBlob(new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 }),
            (AssemblyFlags)0,
            default);
        var frameworkToken = SHA1.HashData(referencedAssemblyPublicKey)[^8..];
        Array.Reverse(frameworkToken);
        var framework = metadata.AddAssemblyReference(
            metadata.GetOrAddString(referencedAssemblyName),
            new Version(0, 0, 0, 0),
            default,
            metadata.GetOrAddBlob(frameworkToken),
            (AssemblyFlags)0,
            default);
        var objectType = metadata.AddTypeReference(
            mscorlib,
            metadata.GetOrAddString("System"),
            metadata.GetOrAddString("Object"));
        var derivedInterface = metadata.AddTypeReference(
            framework,
            metadata.GetOrAddString("Contoso.Framework"),
            metadata.GetOrAddString("IDerivedPlugin"));

        metadata.AddTypeDefinition(
            TypeAttributes.NotPublic,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        var runtimePlugin = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            metadata.GetOrAddString("Contoso"),
            metadata.GetOrAddString("RuntimePlugin"),
            objectType,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddInterfaceImplementation(runtimePlugin, derivedInterface);

        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(
                imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            new BlobBuilder(),
            flags: CorFlags.ILOnly);
        var image = new BlobBuilder();
        pe.Serialize(image);
        return image.ToArray();
    }
}
