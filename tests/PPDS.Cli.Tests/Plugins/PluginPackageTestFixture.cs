using System.IO.Compression;
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
    IReadOnlyList<string>? AssemblyReferences = null);

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
            compiledAssemblies.Add(assembly.AssemblyName, image);

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
        var referenceDirectory = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory();
        if (referenceDirectory == null)
            throw new InvalidOperationException("The embedded net462 reference assemblies are unavailable.");

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(Path.Combine(referenceDirectory, "mscorlib.dll"))
        };

        if (source.ReferencesSdk)
            references.Add(MetadataReference.CreateFromImage(CompileSdkContract(referenceDirectory)));
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

        var compilation = CSharpCompilation.Create(
            source.AssemblyName,
            [CSharpSyntaxTree.ParseText(source.Source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        }

        return output.ToArray();
    }

    private static byte[] CompileSdkContract(string referenceDirectory)
    {
        var compilation = CSharpCompilation.Create(
            "Microsoft.Xrm.Sdk",
            [CSharpSyntaxTree.ParseText("""
                using System;
                namespace Microsoft.Xrm.Sdk
                {
                    public interface IPlugin
                    {
                        void Execute(IServiceProvider serviceProvider);
                    }
                }
                """)],
            [MetadataReference.CreateFromFile(Path.Combine(referenceDirectory, "mscorlib.dll"))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        }

        return output.ToArray();
    }
}
