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
}
