using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PPDS.Cli.Plugins.Extraction;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Extraction;

/// <summary>
/// Tests for AssemblyExtractor — verifies that attribute properties are correctly
/// read from compiled assemblies and mapped to PluginRegistrationConfig models.
///
/// Uses Roslyn to compile test assemblies at runtime so we don't need a separate
/// fixture project. Each test compiles a minimal C# snippet decorated with plugin
/// attributes, then runs the extractor against the resulting DLL.
/// </summary>
public sealed class AssemblyExtractorTests : IDisposable
{
    // Track temp files created per test for cleanup
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch { /* best-effort */ }
        }
    }

    #region Helpers

    /// <summary>
    /// Compiles a C# source snippet that references PPDS.Plugins and writes the
    /// resulting assembly to a dedicated temp directory. Returns the path to the DLL.
    ///
    /// PPDS.Plugins targets net462, so we must reference mscorlib (not System.Runtime)
    /// when compiling test assemblies against it.
    ///
    /// AssemblyExtractor.Create() resolves dependencies by directory — so we copy
    /// PPDS.Plugins.dll into the same temp directory as the compiled test DLL.
    /// </summary>
    private string CompileTestAssembly(string pluginClassSource)
    {
        var pluginsAssemblyPath = typeof(PPDS.Plugins.PluginStepAttribute).Assembly.Location;

        var fullSource = $$"""
            using PPDS.Plugins;

            {{pluginClassSource}}
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(fullSource);

        // PPDS.Plugins targets net462, which requires mscorlib (not System.Runtime).
        var mscorlibPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319", "mscorlib.dll");

        if (!File.Exists(mscorlibPath))
        {
            mscorlibPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Microsoft.NET", "Framework", "v4.0.30319", "mscorlib.dll");
        }

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(mscorlibPath),
            MetadataReference.CreateFromFile(pluginsAssemblyPath),
        };

        var compilation = CSharpCompilation.Create(
            assemblyName: $"TestPlugins_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Use a dedicated temp subdirectory so AssemblyExtractor can find PPDS.Plugins.dll
        var tempDir = Path.Combine(Path.GetTempPath(), $"ppds-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempFiles.Add(tempDir);

        // Copy PPDS.Plugins.dll alongside our test DLL so the MetadataLoadContext resolver finds it
        var pluginsFileName = Path.GetFileName(pluginsAssemblyPath);
        File.Copy(pluginsAssemblyPath, Path.Combine(tempDir, pluginsFileName), overwrite: true);

        var tempPath = Path.Combine(tempDir, "TestPlugin.dll");

        using var stream = File.Create(tempPath);
        var result = compilation.Emit(stream);

        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage()));
            throw new InvalidOperationException($"Test assembly compilation failed:\n{errors}");
        }

        return tempPath;
    }

    #endregion

    #region Deployment extraction tests

    [Fact]
    public void Extract_DefaultDeployment_OmitsDeploymentFromConfig()
    {
        // Default is ServerOnly — should be omitted (only write non-default values is not
        // relevant here since Deployment is always written as it helps document the config).
        // Actually: per spec, Deployment is always written. Let's verify the value is correct.
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        // Default is ServerOnly, so value should be null (omitted, since ServerOnly is default)
        Assert.Null(step.Deployment);
    }

    [Fact]
    public void Extract_OfflineDeployment_WritesDeploymentToConfig()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation,
                Deployment = PluginDeployment.Offline)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Equal("Offline", step.Deployment);
    }

    [Fact]
    public void Extract_BothDeployment_WritesDeploymentToConfig()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation,
                Deployment = PluginDeployment.Both)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Equal("Both", step.Deployment);
    }

    #endregion

    #region RunAsUser extraction tests

    [Fact]
    public void Extract_NoRunAsUser_RunAsUserIsNull()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Null(step.RunAsUser);
    }

    [Fact]
    public void Extract_RunAsUserSet_WritesRunAsUserToConfig()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation,
                RunAsUser = "admin@contoso.com")]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Equal("admin@contoso.com", step.RunAsUser);
    }

    [Fact]
    public void Extract_RunAsUserSystem_WritesSystemToConfig()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation,
                RunAsUser = "System")]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Equal("System", step.RunAsUser);
    }

    #endregion

    #region CanBeBypassed extraction tests

    [Fact]
    public void Extract_CanBeBypassedDefault_OmitsFromConfig()
    {
        // Default is true — should be omitted (null) from config
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Null(step.CanBeBypassed);
    }

    [Fact]
    public void Extract_CanBeBypassedFalse_WritesToConfig()
    {
        // When explicitly set to false (non-default), it must be written
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation,
                CanBeBypassed = false)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Equal(false, step.CanBeBypassed);
    }

    #endregion

    #region CanUseReadOnlyConnection extraction tests

    [Fact]
    public void Extract_CanUseReadOnlyConnectionDefault_OmitsFromConfig()
    {
        // Default is false — should be omitted (null) from config
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Null(step.CanUseReadOnlyConnection);
    }

    [Fact]
    public void Extract_CanUseReadOnlyConnectionTrue_WritesToConfig()
    {
        // When explicitly set to true (non-default), it must be written
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Retrieve", EntityLogicalName = "account", Stage = PluginStage.PreOperation,
                CanUseReadOnlyConnection = true)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Equal(true, step.CanUseReadOnlyConnection);
    }

    #endregion

    #region InvocationSource extraction tests

    [Fact]
    public void Extract_InvocationSourceDefault_OmitsFromConfig()
    {
        // Default is Parent — should be omitted (null) from config
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Null(step.InvocationSource);
    }

    [Fact]
    public void Extract_InvocationSourceChild_WritesToConfig()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation,
                InvocationSource = PluginInvocationSource.Child)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Equal("Child", step.InvocationSource);
    }

    #endregion

    #region Image Description and MessagePropertyName extraction tests

    [Fact]
    public void Extract_ImageNoDescription_DescriptionIsNull()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Update", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            [PluginImage(ImageType = PluginImageType.PreImage, Name = "PreImage")]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var image = config.Types[0].Steps[0].Images[0];
        Assert.Null(image.Description);
    }

    [Fact]
    public void Extract_ImageWithDescription_WritesDescriptionToConfig()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Update", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            [PluginImage(ImageType = PluginImageType.PreImage, Name = "PreImage",
                Description = "Captures account before update")]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var image = config.Types[0].Steps[0].Images[0];
        Assert.Equal("Captures account before update", image.Description);
    }

    [Fact]
    public void Extract_ImageNoMessagePropertyName_MessagePropertyNameIsNull()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Update", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            [PluginImage(ImageType = PluginImageType.PreImage, Name = "PreImage")]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var image = config.Types[0].Steps[0].Images[0];
        Assert.Null(image.MessagePropertyName);
    }

    [Fact]
    public void Extract_ImageWithMessagePropertyName_WritesMessagePropertyNameToConfig()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            [PluginImage(ImageType = PluginImageType.PostImage, Name = "PostImage",
                MessagePropertyName = "Id")]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var image = config.Types[0].Steps[0].Images[0];
        Assert.Equal("Id", image.MessagePropertyName);
    }

    #endregion

    #region Existing property regression tests

    [Fact]
    public void Extract_BasicStep_MapsAllCoreProperties()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(
                Message = "Update",
                EntityLogicalName = "contact",
                Stage = PluginStage.PreOperation,
                Mode = PluginMode.Synchronous,
                ExecutionOrder = 5,
                FilteringAttributes = "firstname,lastname",
                Name = "ContactPreUpdate")]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        Assert.Single(config.Types);
        var step = config.Types[0].Steps[0];
        Assert.Equal("Update", step.Message);
        Assert.Equal("contact", step.Entity);
        Assert.Equal("PreOperation", step.Stage);
        Assert.Equal("Synchronous", step.Mode);
        Assert.Equal(5, step.ExecutionOrder);
        Assert.Equal("firstname,lastname", step.FilteringAttributes);
        Assert.Equal("ContactPreUpdate", step.Name);
    }

    [Fact]
    public void Extract_StepWithImage_MapsImageProperties()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Update", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            [PluginImage(ImageType = PluginImageType.PreImage, Name = "PreImage", Attributes = "name,telephone1")]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var step = config.Types[0].Steps[0];
        Assert.Single(step.Images);
        var image = step.Images[0];
        Assert.Equal("PreImage", image.Name);
        Assert.Equal("PreImage", image.ImageType);
        Assert.Equal("name,telephone1", image.Attributes);
    }

    #endregion
}
