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
        => CompileTestAssembly(pluginClassSource, copyPluginsAssemblyToOutputDir: true);

    /// <summary>
    /// Overload that controls whether PPDS.Plugins.dll is copied alongside the compiled test
    /// DLL. Pass <c>false</c> to leave the dependency out of the target directory so a test can
    /// prove it is resolved from an explicit <c>--reference-dir</c> seed instead. See #1294.
    /// </summary>
    private string CompileTestAssembly(string pluginClassSource, bool copyPluginsAssemblyToOutputDir)
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
        if (copyPluginsAssemblyToOutputDir)
        {
            var pluginsFileName = Path.GetFileName(pluginsAssemblyPath);
            File.Copy(pluginsAssemblyPath, Path.Combine(tempDir, pluginsFileName), overwrite: true);
        }

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

    /// <summary>
    /// Creates a unique, tracked cache base directory so self-heal tests never touch the
    /// real per-user reference-assembly cache in the temp directory and never observe
    /// state left behind by other tests. See #1326.
    /// </summary>
    private string CreateIsolatedCacheBaseDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ppds-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempFiles.Add(dir);
        return dir;
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

    #region CustomApi extraction tests

    [Fact]
    public void Extract_CustomApiAttribute_ExtractsBasicProperties()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api")]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        Assert.NotNull(config.CustomApis);
        Assert.Single(config.CustomApis);

        var api = config.CustomApis[0];
        Assert.Equal("ppds_TestApi", api.UniqueName);
        Assert.Equal("Test Api", api.DisplayName);
        Assert.Equal("TestApiPlugin", api.PluginTypeName);
    }

    [Fact]
    public void Extract_CustomApiAttribute_PluginTypeNameIsFullyQualified()
    {
        var dllPath = CompileTestAssembly("""
            namespace MyCompany.Plugins
            {
                [CustomApi(UniqueName = "ppds_MyApi", DisplayName = "My Api")]
                public class MyApiPlugin { }
            }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        Assert.NotNull(config.CustomApis);
        Assert.Single(config.CustomApis);
        Assert.Equal("MyCompany.Plugins.MyApiPlugin", config.CustomApis[0].PluginTypeName);
    }

    [Fact]
    public void Extract_CustomApiAttribute_WithOptionalProperties()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(
                UniqueName = "ppds_TestApi",
                DisplayName = "Test Api",
                Name = "test_api",
                Description = "Does something useful",
                IsFunction = true,
                IsPrivate = true,
                ExecutePrivilegeName = "prvTestApi")]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var api = config.CustomApis![0];
        Assert.Equal("test_api", api.Name);
        Assert.Equal("Does something useful", api.Description);
        Assert.True(api.IsFunction);
        Assert.True(api.IsPrivate);
        Assert.Equal("prvTestApi", api.ExecutePrivilegeName);
    }

    [Fact]
    public void Extract_CustomApiAttribute_BindingTypeGlobal_OmitsBindingType()
    {
        // Global is the default — should be omitted (null) from config
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api",
                BindingType = ApiBindingType.Global)]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var api = config.CustomApis![0];
        Assert.Null(api.BindingType);
    }

    [Fact]
    public void Extract_CustomApiAttribute_BindingTypeEntity_WritesBindingType()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api",
                BindingType = ApiBindingType.Entity,
                BoundEntity = "account")]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var api = config.CustomApis![0];
        Assert.Equal("Entity", api.BindingType);
        Assert.Equal("account", api.BoundEntity);
    }

    [Fact]
    public void Extract_CustomApiAttribute_BindingTypeEntityCollection_WritesBindingType()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api",
                BindingType = ApiBindingType.EntityCollection,
                BoundEntity = "contact")]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var api = config.CustomApis![0];
        Assert.Equal("EntityCollection", api.BindingType);
        Assert.Equal("contact", api.BoundEntity);
    }

    [Fact]
    public void Extract_CustomApiAttribute_AllowedProcessingStepTypeNone_OmitsFromConfig()
    {
        // None is the default — should be omitted (null) from config
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api",
                AllowedProcessingStepType = ApiProcessingStepType.None)]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var api = config.CustomApis![0];
        Assert.Null(api.AllowedProcessingStepType);
    }

    [Fact]
    public void Extract_CustomApiAttribute_AllowedProcessingStepTypeAsyncOnly_WritesToConfig()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api",
                AllowedProcessingStepType = ApiProcessingStepType.AsyncOnly)]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var api = config.CustomApis![0];
        Assert.Equal("AsyncOnly", api.AllowedProcessingStepType);
    }

    [Fact]
    public void Extract_CustomApiAttribute_AllowedProcessingStepTypeSyncAndAsync_WritesToConfig()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api",
                AllowedProcessingStepType = ApiProcessingStepType.SyncAndAsync)]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var api = config.CustomApis![0];
        Assert.Equal("SyncAndAsync", api.AllowedProcessingStepType);
    }

    [Fact]
    public void Extract_CustomApiWithParameter_ExtractsParameters()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api")]
            [CustomApiParameter(Name = "OrderId", Type = ApiParameterType.Guid, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "Result", Type = ApiParameterType.String, Direction = ParameterDirection.Output)]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var api = config.CustomApis![0];
        Assert.NotNull(api.Parameters);
        Assert.Equal(2, api.Parameters.Count);

        var input = api.Parameters.First(p => p.Direction == "Input");
        Assert.Equal("OrderId", input.Name);
        Assert.Equal("Guid", input.Type);

        var output = api.Parameters.First(p => p.Direction == "Output");
        Assert.Equal("Result", output.Name);
        Assert.Equal("String", output.Type);
    }

    [Fact]
    public void Extract_CustomApiParameter_AllApiParameterTypes_MapCorrectly()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api")]
            [CustomApiParameter(Name = "BoolParam", Type = ApiParameterType.Boolean, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "DateParam", Type = ApiParameterType.DateTime, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "DecParam", Type = ApiParameterType.Decimal, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "EntParam", Type = ApiParameterType.Entity, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "EntColParam", Type = ApiParameterType.EntityCollection, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "EntRefParam", Type = ApiParameterType.EntityReference, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "FloatParam", Type = ApiParameterType.Float, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "IntParam", Type = ApiParameterType.Integer, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "MoneyParam", Type = ApiParameterType.Money, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "PicklistParam", Type = ApiParameterType.Picklist, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "StrParam", Type = ApiParameterType.String, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "StrArrParam", Type = ApiParameterType.StringArray, Direction = ParameterDirection.Input)]
            [CustomApiParameter(Name = "GuidParam", Type = ApiParameterType.Guid, Direction = ParameterDirection.Input)]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var parameters = config.CustomApis![0].Parameters!;
        Assert.Equal(13, parameters.Count);

        Assert.Equal("Boolean", parameters.First(p => p.Name == "BoolParam").Type);
        Assert.Equal("DateTime", parameters.First(p => p.Name == "DateParam").Type);
        Assert.Equal("Decimal", parameters.First(p => p.Name == "DecParam").Type);
        Assert.Equal("Entity", parameters.First(p => p.Name == "EntParam").Type);
        Assert.Equal("EntityCollection", parameters.First(p => p.Name == "EntColParam").Type);
        Assert.Equal("EntityReference", parameters.First(p => p.Name == "EntRefParam").Type);
        Assert.Equal("Float", parameters.First(p => p.Name == "FloatParam").Type);
        Assert.Equal("Integer", parameters.First(p => p.Name == "IntParam").Type);
        Assert.Equal("Money", parameters.First(p => p.Name == "MoneyParam").Type);
        Assert.Equal("Picklist", parameters.First(p => p.Name == "PicklistParam").Type);
        Assert.Equal("String", parameters.First(p => p.Name == "StrParam").Type);
        Assert.Equal("StringArray", parameters.First(p => p.Name == "StrArrParam").Type);
        Assert.Equal("Guid", parameters.First(p => p.Name == "GuidParam").Type);
    }

    [Fact]
    public void Extract_CustomApiParameter_WithOptionalProperties()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api")]
            [CustomApiParameter(
                Name = "Notes",
                UniqueName = "ppds_Notes",
                DisplayName = "Notes Display",
                Description = "Optional notes",
                Type = ApiParameterType.String,
                Direction = ParameterDirection.Input,
                IsOptional = true,
                LogicalEntityName = null)]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var param = config.CustomApis![0].Parameters![0];
        Assert.Equal("Notes", param.Name);
        Assert.Equal("ppds_Notes", param.UniqueName);
        Assert.Equal("Notes Display", param.DisplayName);
        Assert.Equal("Optional notes", param.Description);
        Assert.True(param.IsOptional);
    }

    [Fact]
    public void Extract_CustomApiParameter_WithLogicalEntityName()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api")]
            [CustomApiParameter(Name = "Target", Type = ApiParameterType.EntityReference,
                Direction = ParameterDirection.Input, LogicalEntityName = "account")]
            public class TestApiPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        var param = config.CustomApis![0].Parameters![0];
        Assert.Equal("account", param.LogicalEntityName);
    }

    [Fact]
    public void Extract_NoCustomApiAttribute_CustomApisIsNull()
    {
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        Assert.Null(config.CustomApis);
    }

    [Fact]
    public void Extract_TypeWithCustomApiAndPluginStep_ExtractsBoth()
    {
        var dllPath = CompileTestAssembly("""
            [CustomApi(UniqueName = "ppds_TestApi", DisplayName = "Test Api")]
            public class ApiPlugin { }

            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            public class StepPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(dllPath);
        var config = extractor.Extract();

        Assert.NotNull(config.CustomApis);
        Assert.Single(config.CustomApis);
        Assert.Single(config.Types);
    }

    #endregion

    #region Single-file / embedded reference assembly tests (#1294)

    [Fact]
    public void GetNet462ReferenceAssemblyDirectory_ReturnsPopulatedDirectory()
    {
        // The embedded reference assemblies must extract to a real directory containing the
        // core assembly (mscorlib) and the System.Runtime facade — the assemblies a net462
        // plugin needs and that a single-file publish does not carry as loose DLLs on disk.
        var dir = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory();

        Assert.NotNull(dir);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir!, "mscorlib.dll")),
            "Embedded net462 reference assemblies must include mscorlib.dll (the core assembly).");
        Assert.True(File.Exists(Path.Combine(dir!, "System.Runtime.dll")),
            "Embedded net462 reference assemblies must include the System.Runtime facade.");
    }

    [Fact]
    public void Extract_WithoutRuntimeDirectory_SucceedsViaEmbeddedReferenceAssemblies()
    {
        // Reproduces the single-file packaging bug (#1294): a self-contained single-file CLI has
        // no loose BCL DLLs in its runtime directory, so seeding the resolver from that directory
        // alone cannot find the core assembly. Suppressing runtime-directory seeding here proves
        // the embedded net462 reference assemblies (plus the target directory) are sufficient on
        // their own to extract a net462-referencing plugin assembly.
        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
            public class TestPlugin { }
            """);

        using var extractor = AssemblyExtractor.Create(
            dllPath, referenceDirs: null, includeRuntimeDirectory: false);
        var config = extractor.Extract();

        Assert.Single(config.Types);
        var step = config.Types[0].Steps[0];
        Assert.Equal("Create", step.Message);
        Assert.Equal("account", step.Entity);
        Assert.Equal("PostOperation", step.Stage);
    }

    [Fact]
    public void Extract_WithReferenceDir_ResolvesDependencyFromSeedDirectory()
    {
        // The plugin's dependency (PPDS.Plugins.dll) is deliberately NOT placed in the target
        // assembly's directory. With runtime-directory seeding suppressed, extraction can only
        // succeed if the explicit --reference-dir seed is honored.
        var pluginsDir = Path.GetDirectoryName(
            typeof(PPDS.Plugins.PluginStepAttribute).Assembly.Location)!;

        var dllPath = CompileTestAssembly("""
            [PluginStep(Message = "Update", EntityLogicalName = "contact", Stage = PluginStage.PreOperation)]
            public class TestPlugin { }
            """,
            copyPluginsAssemblyToOutputDir: false);

        // Sanity check: without the reference dir, the dependency is unresolvable and extraction
        // fails — proving the success below is due to the seed, not incidental resolution.
        Assert.ThrowsAny<Exception>(() =>
        {
            using var withoutSeed = AssemblyExtractor.Create(
                dllPath, referenceDirs: null, includeRuntimeDirectory: false);
            withoutSeed.Extract();
        });

        using var extractor = AssemblyExtractor.Create(
            dllPath, referenceDirs: new[] { pluginsDir }, includeRuntimeDirectory: false);
        var config = extractor.Extract();

        Assert.Single(config.Types);
        Assert.Equal("Update", config.Types[0].Steps[0].Message);
    }

    #endregion

    #region Cache self-healing tests (#1326)

    [Fact]
    public void GetNet462ReferenceAssemblyDirectory_CacheMissingSentinel_SelfHealsAndReExtracts()
    {
        // A temp cleaner can delete individual cache files while leaving the directory and
        // other DLLs behind. The cache key is fixed per CLI version, so a corrupt directory
        // that still passed validation would break every subsequent run. A cache without the
        // completion sentinel must be treated as invalid, deleted, and re-extracted.
        var baseDir = CreateIsolatedCacheBaseDir();

        var first = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(baseDir);
        Assert.NotNull(first);
        Assert.True(File.Exists(Path.Combine(first!, AssemblyExtractor.CompletionSentinelFileName)),
            "A freshly extracted cache must contain the completion sentinel.");

        File.Delete(Path.Combine(first!, AssemblyExtractor.CompletionSentinelFileName));
        File.Delete(Path.Combine(first!, "mscorlib.dll"));
        Assert.True(Directory.EnumerateFiles(first!, "*.dll").Any(),
            "Corruption setup must leave at least one DLL so the old any-DLL check would have passed.");

        var healed = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(baseDir);

        Assert.Equal(first, healed);
        Assert.True(File.Exists(Path.Combine(healed!, "mscorlib.dll")),
            "Self-healed cache must contain the core assembly again.");
        Assert.True(File.Exists(Path.Combine(healed!, AssemblyExtractor.CompletionSentinelFileName)),
            "Self-healed cache must contain the completion sentinel.");
    }

    [Fact]
    public void GetNet462ReferenceAssemblyDirectory_CacheMissingCoreAssembly_SelfHealsAndReExtracts()
    {
        // Failure mode 1 of #1326: a cleaner deletes mscorlib.dll but leaves other DLLs (and
        // even the sentinel). The old "any *.dll" validation accepted such a cache forever,
        // permanently reproducing the #1294 "could not find core assembly" failure.
        var baseDir = CreateIsolatedCacheBaseDir();

        var first = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(baseDir);
        Assert.NotNull(first);

        File.Delete(Path.Combine(first!, "mscorlib.dll"));

        var healed = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(baseDir);

        Assert.Equal(first, healed);
        Assert.True(File.Exists(Path.Combine(healed!, "mscorlib.dll")),
            "Self-healed cache must contain the core assembly again.");
    }

    [Fact]
    public void GetNet462ReferenceAssemblyDirectory_EmptyCacheDirectory_SelfHealsAndReExtracts()
    {
        // Failure mode 2 of #1326: a cleaner deletes every file but keeps the directory.
        // Before self-healing, Directory.Move into the existing-but-empty destination threw
        // IOException on every run, silently disabling the embedded reference assemblies.
        var baseDir = CreateIsolatedCacheBaseDir();

        var first = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(baseDir);
        Assert.NotNull(first);

        Directory.Delete(first!, recursive: true);
        Directory.CreateDirectory(first!);

        var healed = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(baseDir);

        Assert.Equal(first, healed);
        Assert.True(File.Exists(Path.Combine(healed!, "mscorlib.dll")),
            "Self-healed cache must contain the core assembly.");
        Assert.True(File.Exists(Path.Combine(healed!, AssemblyExtractor.CompletionSentinelFileName)),
            "Self-healed cache must contain the completion sentinel.");
    }

    #endregion
}
