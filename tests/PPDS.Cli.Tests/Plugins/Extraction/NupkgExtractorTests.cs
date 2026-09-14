using System.IO.Compression;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Extraction;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Extraction;

[Trait("Category", "Unit")]
public class NupkgExtractorTests : IDisposable
{
    private readonly string _scratch;

    public NupkgExtractorTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), $"ppds-nupkg-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Extract_NupkgMissingLibFolder_Throws()
    {
        // Build a minimal .nupkg-like zip with only a .nuspec at the root and no lib/ folder.
        var nupkgPath = Path.Combine(_scratch, "empty.nupkg");
        using (var stream = File.Create(nupkgPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("empty.nuspec");
            using var entryStream = entry.Open();
            var xml = Encoding.UTF8.GetBytes("""
                <?xml version="1.0"?>
                <package><metadata><id>empty</id><version>1.0.0</version></metadata></package>
                """);
            entryStream.Write(xml, 0, xml.Length);
        }

        var ex = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, ex.ErrorCode);
        Assert.Contains("lib/net462", ex.Message);
    }

    [Fact]
    public void Extract_NupkgWithZipSlipEntry_ThrowsPpdsException()
    {
        // Handcraft a zip archive that contains an entry whose full name traverses up out of the
        // extraction directory (classic zip-slip payload). The SUT validates every canonical
        // destination before calling ExtractToDirectory so containment does not depend on the
        // runtime's built-in protection.
        var nupkgPath = Path.Combine(_scratch, "evil.nupkg");
        using (var stream = File.Create(nupkgPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // Benign nuspec for completeness.
            var nuspec = archive.CreateEntry("evil.nuspec");
            using (var entryStream = nuspec.Open())
            {
                var xml = Encoding.UTF8.GetBytes("""
                    <?xml version="1.0"?>
                    <package><metadata><id>evil</id><version>1.0.0</version></metadata></package>
                    """);
                entryStream.Write(xml, 0, xml.Length);
            }

            // Hostile entry — path escapes the temp dir via ".." segments.
            var hostile = archive.CreateEntry("../../escaped.txt");
            using (var hostileStream = hostile.Open())
            {
                var payload = Encoding.UTF8.GetBytes("pwned");
                hostileStream.Write(payload, 0, payload.Length);
            }

            var pluginAssembly = archive.CreateEntry("lib/net462/Evil.dll");
            using var pluginStream = pluginAssembly.Open();
            pluginStream.WriteByte(0);
        }

        var ex = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, ex.ErrorCode);
        Assert.Contains("escapes the extraction directory", ex.Message);
        Assert.Contains("../../escaped.txt", ex.Message);
    }

    [Fact]
    public void Extract_NupkgWithUnloadableAssembly_ThrowsInsteadOfReturningEmpty()
    {
        // A lib/net462 folder whose only DLL is not a valid assembly. Previously every
        // per-assembly load failure was swallowed and Extract returned an empty config (a
        // misleading "0 plugin types" result). The extractor now surfaces the failure so the
        // user sees the real cause instead of a silent no-op (#1294).
        var nupkgPath = Path.Combine(_scratch, "broken.nupkg");
        using (var stream = File.Create(nupkgPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var nuspec = archive.CreateEntry("broken.nuspec");
            using (var entryStream = nuspec.Open())
            {
                var xml = Encoding.UTF8.GetBytes("""
                    <?xml version="1.0"?>
                    <package><metadata><id>broken</id><version>1.0.0</version></metadata></package>
                    """);
                entryStream.Write(xml, 0, xml.Length);
            }

            // Not a valid PE image — LoadFromAssemblyPath fails for this "assembly".
            var dll = archive.CreateEntry("lib/net462/Broken.dll");
            using var dllStream = dll.Open();
            var payload = Encoding.UTF8.GetBytes("this is not a PE file");
            dllStream.Write(payload, 0, payload.Length);
        }

        var ex = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));
        Assert.Equal(ErrorCodes.Operation.Dependency, ex.ErrorCode);
        Assert.Contains("Broken.dll", ex.Message);
    }

    [Fact]
    public void Extract_Net48OnlyPackage_ThrowsStructuredValidationError()
    {
        var nupkgPath = Path.Combine(_scratch, "net48-plugin.nupkg");
        CreatePluginPackage(nupkgPath, "net48");

        var ex = Assert.Throws<PpdsException>(() => NupkgExtractor.Extract(nupkgPath));

        Assert.Equal(ErrorCodes.Validation.InvalidValue, ex.ErrorCode);
        Assert.Contains("Dataverse cannot register this NuGet plugin package", ex.Message);
        Assert.Contains("lib/net462", ex.Message);
        Assert.Contains("lib/net471", ex.Message);
        Assert.Contains("lib/net48", ex.Message);
        Assert.Contains("loose plugin assembly", ex.Message);
    }

    [Fact]
    public void Extract_Net471Package_SelectsSupportedPluginAssembly()
    {
        var nupkgPath = Path.Combine(_scratch, "net471-plugin.nupkg");
        CreatePluginPackage(nupkgPath, "net471");

        var config = NupkgExtractor.Extract(nupkgPath);

        Assert.Equal("Nuget", config.Type);
        var type = Assert.Single(config.Types);
        Assert.Equal("Net48Plugin", type.TypeName);
        var step = Assert.Single(type.Steps);
        Assert.Equal("Create", step.Message);
        Assert.Equal("account", step.Entity);
    }

    [Fact]
    public void Extract_EmptyNet462GroupAndNet471DllAssets_SelectsNet471()
    {
        var nupkgPath = Path.Combine(_scratch, "multi-target-plugin.nupkg");
        CreatePluginPackage(nupkgPath, "net471", emptyFramework: "net462");

        var config = NupkgExtractor.Extract(nupkgPath);

        Assert.Equal("Nuget", config.Type);
        var type = Assert.Single(config.Types);
        Assert.Equal("Net48Plugin", type.TypeName);
    }

    private static void CreatePluginPackage(
        string nupkgPath,
        string framework,
        string? emptyFramework = null)
    {
        var pluginAssembly = CompilePluginAssembly();
        var pluginsAssemblyPath = typeof(PPDS.Plugins.PluginStepAttribute).Assembly.Location;

        using var stream = File.Create(nupkgPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var nuspec = archive.CreateEntry("plugin.nuspec");
        using (var writer = new StreamWriter(nuspec.Open(), Encoding.UTF8))
        {
            writer.Write("""
                <?xml version="1.0"?>
                <package><metadata><id>plugin</id><version>1.0.0</version></metadata></package>
                """);
        }

        var pluginEntry = archive.CreateEntry($"lib/{framework}/Net48Plugin.dll");
        using (var entryStream = pluginEntry.Open())
        {
            entryStream.Write(pluginAssembly);
        }

        var attributesEntry = archive.CreateEntry($"lib/{framework}/PPDS.Plugins.dll");
        using (var attributesStream = attributesEntry.Open())
        using (var attributesFile = File.OpenRead(pluginsAssemblyPath))
        {
            attributesFile.CopyTo(attributesStream);
        }

        if (emptyFramework != null)
        {
            var marker = archive.CreateEntry($"lib/{emptyFramework}/_._");
            marker.Open().Dispose();
        }
    }

    private static byte[] CompilePluginAssembly()
    {
        var referenceDirectory = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory();
        Assert.NotNull(referenceDirectory);

        var syntaxTree = CSharpSyntaxTree.ParseText("""
            using PPDS.Plugins;

            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PreOperation)]
            public class Net48Plugin { }
            """);

        var compilation = CSharpCompilation.Create(
            "Net48Plugin",
            [syntaxTree],
            [
                MetadataReference.CreateFromFile(Path.Combine(referenceDirectory!, "mscorlib.dll")),
                MetadataReference.CreateFromFile(typeof(PPDS.Plugins.PluginStepAttribute).Assembly.Location)
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        return output.ToArray();
    }
}
