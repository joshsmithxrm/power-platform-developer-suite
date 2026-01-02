using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.Cli.Plugins.Models;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Models;

public class PluginRegistrationConfigTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    #region Serialization Tests

    [Fact]
    public void Serialize_EmptyConfig_ProducesValidJson()
    {
        var config = new PluginRegistrationConfig();
        var json = JsonSerializer.Serialize(config, JsonOptions);

        Assert.Contains("\"version\"", json);
        Assert.Contains("\"assemblies\"", json);
    }

    [Fact]
    public void Serialize_ConfigWithSchema_IncludesSchemaProperty()
    {
        var config = new PluginRegistrationConfig
        {
            Schema = "https://example.com/schema.json"
        };
        var json = JsonSerializer.Serialize(config, JsonOptions);

        Assert.Contains("\"$schema\"", json);
        Assert.Contains("https://example.com/schema.json", json);
    }

    [Fact]
    public void Serialize_ConfigWithGeneratedAt_IncludesTimestamp()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var config = new PluginRegistrationConfig
        {
            GeneratedAt = timestamp
        };
        var json = JsonSerializer.Serialize(config, JsonOptions);

        Assert.Contains("\"generatedAt\"", json);
    }

    [Fact]
    public void Serialize_FullConfig_ProducesCorrectStructure()
    {
        var config = new PluginRegistrationConfig
        {
            Version = "1.0",
            Assemblies =
            [
                new PluginAssemblyConfig
                {
                    Name = "MyPlugins",
                    Type = "Assembly",
                    Path = "bin/MyPlugins.dll",
                    Types =
                    [
                        new PluginTypeConfig
                        {
                            TypeName = "MyPlugins.AccountPlugin",
                            Steps =
                            [
                                new PluginStepConfig
                                {
                                    Name = "AccountPlugin: Update of account",
                                    Message = "Update",
                                    Entity = "account",
                                    Stage = "PostOperation",
                                    Mode = "Asynchronous",
                                    Images =
                                    [
                                        new PluginImageConfig
                                        {
                                            Name = "PreImage",
                                            ImageType = "PreImage",
                                            Attributes = "name,telephone1"
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);

        Assert.Contains("\"name\": \"MyPlugins\"", json);
        Assert.Contains("\"typeName\": \"MyPlugins.AccountPlugin\"", json);
        Assert.Contains("\"message\": \"Update\"", json);
        Assert.Contains("\"entity\": \"account\"", json);
        Assert.Contains("\"stage\": \"PostOperation\"", json);
        Assert.Contains("\"mode\": \"Asynchronous\"", json);
        Assert.Contains("\"imageType\": \"PreImage\"", json);
    }

    #endregion

    #region Deserialization Tests

    [Fact]
    public void Deserialize_MinimalJson_Succeeds()
    {
        var json = """
        {
            "version": "1.0",
            "assemblies": []
        }
        """;

        var config = JsonSerializer.Deserialize<PluginRegistrationConfig>(json, JsonOptions);

        Assert.NotNull(config);
        Assert.Equal("1.0", config.Version);
        Assert.Empty(config.Assemblies);
    }

    [Fact]
    public void Deserialize_FullJson_Succeeds()
    {
        var json = """
        {
            "$schema": "https://example.com/schema.json",
            "version": "1.0",
            "generatedAt": "2024-01-15T10:30:00Z",
            "assemblies": [
                {
                    "name": "MyPlugins",
                    "type": "Assembly",
                    "path": "bin/MyPlugins.dll",
                    "allTypeNames": ["MyPlugins.AccountPlugin"],
                    "types": [
                        {
                            "typeName": "MyPlugins.AccountPlugin",
                            "steps": [
                                {
                                    "name": "AccountPlugin: Update of account",
                                    "message": "Update",
                                    "entity": "account",
                                    "stage": "PostOperation",
                                    "mode": "Asynchronous",
                                    "executionOrder": 1,
                                    "filteringAttributes": "name,telephone1",
                                    "images": [
                                        {
                                            "name": "PreImage",
                                            "imageType": "PreImage",
                                            "attributes": "name,telephone1"
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        }
        """;

        var config = JsonSerializer.Deserialize<PluginRegistrationConfig>(json, JsonOptions);

        Assert.NotNull(config);
        Assert.Equal("1.0", config.Version);
        Assert.Single(config.Assemblies);

        var assembly = config.Assemblies[0];
        Assert.Equal("MyPlugins", assembly.Name);
        Assert.Equal("Assembly", assembly.Type);
        Assert.Single(assembly.Types);

        var type = assembly.Types[0];
        Assert.Equal("MyPlugins.AccountPlugin", type.TypeName);
        Assert.Single(type.Steps);

        var step = type.Steps[0];
        Assert.Equal("Update", step.Message);
        Assert.Equal("account", step.Entity);
        Assert.Equal("PostOperation", step.Stage);
        Assert.Equal("Asynchronous", step.Mode);
        Assert.Single(step.Images);

        var image = step.Images[0];
        Assert.Equal("PreImage", image.Name);
        Assert.Equal("PreImage", image.ImageType);
    }

    [Fact]
    public void Deserialize_NugetPackage_Succeeds()
    {
        var json = """
        {
            "version": "1.0",
            "assemblies": [
                {
                    "name": "MyPlugins",
                    "type": "Nuget",
                    "packagePath": "bin/MyPlugins.1.0.0.nupkg",
                    "solution": "my_solution",
                    "types": []
                }
            ]
        }
        """;

        var config = JsonSerializer.Deserialize<PluginRegistrationConfig>(json, JsonOptions);

        Assert.NotNull(config);
        var assembly = config.Assemblies[0];
        Assert.Equal("Nuget", assembly.Type);
        Assert.Equal("bin/MyPlugins.1.0.0.nupkg", assembly.PackagePath);
        Assert.Equal("my_solution", assembly.Solution);
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public void RoundTrip_PreservesAllProperties()
    {
        var original = new PluginRegistrationConfig
        {
            Schema = "https://example.com/schema.json",
            Version = "1.0",
            GeneratedAt = DateTimeOffset.Parse("2024-01-15T10:30:00Z"),
            Assemblies =
            [
                new PluginAssemblyConfig
                {
                    Name = "TestPlugins",
                    Type = "Assembly",
                    Path = "bin/TestPlugins.dll",
                    Solution = "test_solution",
                    AllTypeNames = ["TestPlugins.Plugin1", "TestPlugins.Plugin2"],
                    Types =
                    [
                        new PluginTypeConfig
                        {
                            TypeName = "TestPlugins.Plugin1",
                            Steps =
                            [
                                new PluginStepConfig
                                {
                                    Name = "Plugin1: Create of contact",
                                    Message = "Create",
                                    Entity = "contact",
                                    SecondaryEntity = null,
                                    Stage = "PreOperation",
                                    Mode = "Synchronous",
                                    ExecutionOrder = 10,
                                    FilteringAttributes = null,
                                    UnsecureConfiguration = "some config",
                                    StepId = "step1",
                                    Images = []
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<PluginRegistrationConfig>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Schema, deserialized.Schema);
        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.Assemblies.Count, deserialized.Assemblies.Count);

        var origAssembly = original.Assemblies[0];
        var deserAssembly = deserialized.Assemblies[0];
        Assert.Equal(origAssembly.Name, deserAssembly.Name);
        Assert.Equal(origAssembly.Type, deserAssembly.Type);
        Assert.Equal(origAssembly.Solution, deserAssembly.Solution);
        Assert.Equal(origAssembly.AllTypeNames.Count, deserAssembly.AllTypeNames.Count);

        var origStep = origAssembly.Types[0].Steps[0];
        var deserStep = deserAssembly.Types[0].Steps[0];
        Assert.Equal(origStep.Name, deserStep.Name);
        Assert.Equal(origStep.Message, deserStep.Message);
        Assert.Equal(origStep.ExecutionOrder, deserStep.ExecutionOrder);
        Assert.Equal(origStep.UnsecureConfiguration, deserStep.UnsecureConfiguration);
        Assert.Equal(origStep.StepId, deserStep.StepId);
    }

    #endregion

    #region Validation Tests

    [Fact]
    public void Validate_ValidExecutionOrder_DoesNotThrow()
    {
        var config = CreateConfigWithExecutionOrder(1);
        config.Validate(); // Should not throw
    }

    [Fact]
    public void Validate_MaxExecutionOrder_DoesNotThrow()
    {
        var config = CreateConfigWithExecutionOrder(999999);
        config.Validate(); // Should not throw
    }

    [Fact]
    public void Validate_ExecutionOrderBelowMinimum_Throws()
    {
        var config = CreateConfigWithExecutionOrder(0);

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("invalid executionOrder", ex.Message);
        Assert.Contains("0", ex.Message);
    }

    [Fact]
    public void Validate_ExecutionOrderAboveMaximum_Throws()
    {
        var config = CreateConfigWithExecutionOrder(1000000);

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("invalid executionOrder", ex.Message);
        Assert.Contains("1000000", ex.Message);
    }

    [Fact]
    public void Validate_MultipleInvalidSteps_ReportsAll()
    {
        var config = new PluginRegistrationConfig
        {
            Assemblies =
            [
                new PluginAssemblyConfig
                {
                    Name = "TestPlugins",
                    Types =
                    [
                        new PluginTypeConfig
                        {
                            TypeName = "Plugin1",
                            Steps =
                            [
                                new PluginStepConfig { Name = "Step1", ExecutionOrder = 0 },
                                new PluginStepConfig { Name = "Step2", ExecutionOrder = 1000000 }
                            ]
                        }
                    ]
                }
            ]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("Step1", ex.Message);
        Assert.Contains("Step2", ex.Message);
    }

    [Fact]
    public void Validate_EmptyConfig_DoesNotThrow()
    {
        var config = new PluginRegistrationConfig();
        config.Validate(); // Should not throw
    }

    private static PluginRegistrationConfig CreateConfigWithExecutionOrder(int executionOrder)
    {
        return new PluginRegistrationConfig
        {
            Assemblies =
            [
                new PluginAssemblyConfig
                {
                    Name = "TestPlugins",
                    Types =
                    [
                        new PluginTypeConfig
                        {
                            TypeName = "TestPlugin",
                            Steps =
                            [
                                new PluginStepConfig
                                {
                                    Name = "TestStep",
                                    Message = "Create",
                                    Entity = "account",
                                    ExecutionOrder = executionOrder
                                }
                            ]
                        }
                    ]
                }
            ]
        };
    }

    #endregion
}
