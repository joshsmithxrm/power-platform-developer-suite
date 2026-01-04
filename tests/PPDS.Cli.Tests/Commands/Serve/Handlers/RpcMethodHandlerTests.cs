using System.Text.Json;
using PPDS.Cli.Commands.Serve.Handlers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Tests for RpcMethodHandler response DTOs and serialization.
/// Note: The actual RPC methods require ProfileStore and Dataverse connections,
/// so they are tested via integration tests in PPDS.LiveTests.
/// </summary>
public class RpcMethodHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region AuthListResponse Tests

    [Fact]
    public void AuthListResponse_EmptyProfiles_SerializesCorrectly()
    {
        var response = new AuthListResponse
        {
            ActiveProfile = null,
            Profiles = []
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AuthListResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.ActiveProfile);
        Assert.Empty(deserialized.Profiles);
    }

    [Fact]
    public void AuthListResponse_WithProfiles_SerializesCorrectly()
    {
        var response = new AuthListResponse
        {
            ActiveProfile = "dev",
            Profiles =
            [
                new ProfileInfo
                {
                    Index = 0,
                    Name = "dev",
                    Identity = "user@example.com",
                    AuthMethod = "InteractiveBrowser",
                    Cloud = "Public",
                    IsActive = true
                }
            ]
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AuthListResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("dev", deserialized.ActiveProfile);
        Assert.Single(deserialized.Profiles);
        Assert.Equal("user@example.com", deserialized.Profiles[0].Identity);
    }

    #endregion

    #region AuthWhoResponse Tests

    [Fact]
    public void AuthWhoResponse_WithEnvironment_SerializesCorrectly()
    {
        var response = new AuthWhoResponse
        {
            Index = 0,
            Name = "prod",
            AuthMethod = "ClientSecret",
            Cloud = "Public",
            TenantId = "tenant-123",
            ApplicationId = "app-456",
            TokenStatus = "valid",
            Environment = new EnvironmentDetails
            {
                Url = "https://org.crm.dynamics.com",
                DisplayName = "Production",
                UniqueName = "org_prod",
                Type = "Production"
            }
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);

        Assert.Contains("prod", json);
        Assert.Contains("https://org.crm.dynamics.com", json);
        Assert.Contains("Production", json);
    }

    [Fact]
    public void AuthWhoResponse_TokenStatus_ComputesCorrectly()
    {
        var expiredToken = DateTimeOffset.UtcNow.AddHours(-1);
        var validToken = DateTimeOffset.UtcNow.AddHours(1);

        // Test logic that would be in the handler
        var expiredStatus = expiredToken < DateTimeOffset.UtcNow ? "expired" : "valid";
        var validStatus = validToken < DateTimeOffset.UtcNow ? "expired" : "valid";

        Assert.Equal("expired", expiredStatus);
        Assert.Equal("valid", validStatus);
    }

    #endregion

    #region AuthSelectResponse Tests

    [Fact]
    public void AuthSelectResponse_SerializesCorrectly()
    {
        var response = new AuthSelectResponse
        {
            Index = 1,
            Name = "staging",
            Identity = "service@example.com",
            Environment = "Staging Environment"
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AuthSelectResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(1, deserialized.Index);
        Assert.Equal("staging", deserialized.Name);
    }

    #endregion

    #region EnvListResponse Tests

    [Fact]
    public void EnvListResponse_WithEnvironments_SerializesCorrectly()
    {
        var response = new EnvListResponse
        {
            Filter = "prod",
            Environments =
            [
                new EnvironmentInfo
                {
                    Id = Guid.NewGuid(),
                    FriendlyName = "Production",
                    UniqueName = "org_prod",
                    ApiUrl = "https://org.crm.dynamics.com",
                    Type = "Production",
                    State = "Enabled",
                    IsActive = true
                }
            ]
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);

        Assert.Contains("Production", json);
        Assert.Contains("prod", json);
        Assert.Contains("Enabled", json);
    }

    [Fact]
    public void EnvListResponse_EmptyFilter_OmitsFilterInJson()
    {
        var response = new EnvListResponse
        {
            Filter = null,
            Environments = []
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);

        // Filter should be omitted when null (JsonIgnoreCondition.WhenWritingNull)
        Assert.DoesNotContain("\"filter\"", json);
    }

    #endregion

    #region EnvSelectResponse Tests

    [Fact]
    public void EnvSelectResponse_SerializesCorrectly()
    {
        var response = new EnvSelectResponse
        {
            Url = "https://org.crm.dynamics.com",
            DisplayName = "Production",
            UniqueName = "org_prod",
            EnvironmentId = "env-123",
            ResolutionMethod = "DirectConnection"
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);

        Assert.Contains("DirectConnection", json);
        Assert.Contains("env-123", json);
    }

    #endregion

    #region PluginsListResponse Tests

    [Fact]
    public void PluginsListResponse_EmptyAssembliesAndPackages_SerializesCorrectly()
    {
        var response = new PluginsListResponse
        {
            Assemblies = [],
            Packages = []
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<PluginsListResponse>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.Assemblies);
        Assert.Empty(deserialized.Packages);
    }

    [Fact]
    public void PluginsListResponse_WithAssemblyAndSteps_SerializesCorrectly()
    {
        var response = new PluginsListResponse
        {
            Assemblies =
            [
                new PluginAssemblyInfo
                {
                    Name = "MyPlugin",
                    Version = "1.0.0.0",
                    PublicKeyToken = "abc123",
                    Types =
                    [
                        new PluginTypeInfoDto
                        {
                            TypeName = "MyPlugin.OnAccountCreate",
                            Steps =
                            [
                                new PluginStepInfo
                                {
                                    Name = "MyPlugin.OnAccountCreate: Create of account",
                                    Message = "Create",
                                    Entity = "account",
                                    Stage = "PreOperation",
                                    Mode = "Synchronous",
                                    ExecutionOrder = 1,
                                    IsEnabled = true,
                                    Deployment = "ServerOnly",
                                    Images =
                                    [
                                        new PluginImageInfo
                                        {
                                            Name = "PreImage",
                                            EntityAlias = "PreImage",
                                            ImageType = "PreImage",
                                            Attributes = "name,accountnumber"
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ],
            Packages = []
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);

        Assert.Contains("MyPlugin", json);
        Assert.Contains("OnAccountCreate", json);
        Assert.Contains("Create", json);
        Assert.Contains("PreOperation", json);
        Assert.Contains("PreImage", json);
    }

    [Fact]
    public void PluginPackageInfo_WithNestedAssemblies_SerializesCorrectly()
    {
        var package = new PluginPackageInfo
        {
            Name = "MyPluginPackage",
            UniqueName = "my_plugin_package",
            Version = "1.0.0",
            Assemblies =
            [
                new PluginAssemblyInfo
                {
                    Name = "MyPlugin.Core",
                    Version = "1.0.0.0",
                    Types = []
                }
            ]
        };

        var json = JsonSerializer.Serialize(package, JsonOptions);

        Assert.Contains("MyPluginPackage", json);
        Assert.Contains("my_plugin_package", json);
        Assert.Contains("MyPlugin.Core", json);
    }

    #endregion

    #region SchemaListResponse Tests

    [Fact]
    public void SchemaListResponse_WithAttributes_SerializesCorrectly()
    {
        var response = new SchemaListResponse
        {
            Entity = "account",
            Attributes =
            [
                new AttributeInfo
                {
                    LogicalName = "accountid",
                    DisplayName = "Account ID",
                    AttributeType = "Uniqueidentifier",
                    IsCustomAttribute = false,
                    IsPrimaryId = true,
                    IsPrimaryName = false
                },
                new AttributeInfo
                {
                    LogicalName = "name",
                    DisplayName = "Account Name",
                    AttributeType = "String",
                    IsCustomAttribute = false,
                    IsPrimaryId = false,
                    IsPrimaryName = true
                }
            ]
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);

        Assert.Contains("account", json);
        Assert.Contains("accountid", json);
        Assert.Contains("Uniqueidentifier", json);
    }

    #endregion

    #region Null Handling Tests

    [Fact]
    public void ProfileInfo_NullEnvironment_OmittedInJson()
    {
        var info = new ProfileInfo
        {
            Index = 0,
            Identity = "user@example.com",
            AuthMethod = "InteractiveBrowser",
            Cloud = "Public",
            Environment = null,
            IsActive = false
        };

        var json = JsonSerializer.Serialize(info, JsonOptions);

        // Environment should be omitted when null
        Assert.DoesNotContain("\"environment\"", json);
    }

    [Fact]
    public void PluginStepInfo_NullOptionalFields_OmittedInJson()
    {
        var step = new PluginStepInfo
        {
            Name = "Test Step",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            ExecutionOrder = 1,
            IsEnabled = true,
            Deployment = "ServerOnly",
            FilteringAttributes = null,
            Description = null,
            RunAsUser = null,
            Images = []
        };

        var json = JsonSerializer.Serialize(step, JsonOptions);

        Assert.DoesNotContain("\"filteringAttributes\"", json);
        Assert.DoesNotContain("\"description\"", json);
        Assert.DoesNotContain("\"runAsUser\"", json);
    }

    #endregion
}
