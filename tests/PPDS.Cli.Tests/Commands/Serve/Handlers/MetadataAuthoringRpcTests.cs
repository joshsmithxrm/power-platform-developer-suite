using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.DependencyInjection;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Unit tests for metadata authoring RPC handler methods.
/// Tests parameter validation and DTO serialization.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataAuthoringRpcTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static IServiceProvider CreateAuthServices() =>
        new ServiceCollection().AddAuthServices().BuildServiceProvider();

    #region Parameter Validation Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataCreateTable_EmptySolutionUniqueName_ThrowsRpcException(string? solutionUniqueName)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataCreateTableAsync(
            solutionUniqueName!,
            "new_table",
            "New Table",
            "New Tables",
            "Description",
            "UserOwned");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataCreateTable_EmptySchemaName_ThrowsRpcException(string? schemaName)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataCreateTableAsync(
            "MySolution",
            schemaName!,
            "New Table",
            "New Tables",
            "Description",
            "UserOwned");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataCreateTable_EmptyDisplayName_ThrowsRpcException(string? displayName)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataCreateTableAsync(
            "MySolution",
            "new_table",
            displayName!,
            "New Tables",
            "Description",
            "UserOwned");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataDeleteTable_EmptySolutionUniqueName_ThrowsRpcException(string? solutionUniqueName)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataDeleteTableAsync(
            solutionUniqueName!,
            "account");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataDeleteTable_EmptyEntityLogicalName_ThrowsRpcException(string? entityLogicalName)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataDeleteTableAsync(
            "MySolution",
            entityLogicalName!);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataCreateColumn_EmptyColumnType_ThrowsRpcException(string? columnType)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataCreateColumnAsync(
            "MySolution",
            "account",
            "new_col",
            "New Column",
            "Description",
            columnType!);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Fact]
    public async Task MetadataCreateColumn_InvalidColumnType_ThrowsRpcException()
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataCreateColumnAsync(
            "MySolution",
            "account",
            "new_col",
            "New Column",
            "Description",
            "InvalidType");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataDeleteColumn_EmptyColumnLogicalName_ThrowsRpcException(string? columnLogicalName)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataDeleteColumnAsync(
            "MySolution",
            "account",
            columnLogicalName!);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataCreateOneToMany_EmptySchemaName_ThrowsRpcException(string? schemaName)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataCreateOneToManyAsync(
            "MySolution",
            "account",
            "contact",
            schemaName!,
            "new_lookup",
            "Lookup Name");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataCreateKey_EmptyDisplayName_ThrowsRpcException(string? displayName)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataCreateKeyAsync(
            "MySolution",
            "account",
            "new_key",
            displayName!,
            new[] { "name" });

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Fact]
    public async Task MetadataCreateKey_EmptyKeyAttributes_ThrowsRpcException()
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataCreateKeyAsync(
            "MySolution",
            "account",
            "new_key",
            "My Key",
            Array.Empty<string>());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataCreateGlobalChoice_EmptySchemaName_ThrowsRpcException(string? schemaName)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataCreateGlobalChoiceAsync(
            "MySolution",
            schemaName!,
            "Choice Name",
            "Description");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MetadataDeleteGlobalChoice_EmptyName_ThrowsRpcException(string? name)
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());

        var act = () => handler.MetadataDeleteGlobalChoiceAsync(
            "MySolution",
            name!);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    #endregion

    #region Response DTO Serialization Tests

    [Fact]
    public void MetadataAuthoringResponse_Success_SerializesCorrectly()
    {
        var response = new MetadataAuthoringResponse
        {
            Success = true,
            LogicalName = "new_entity",
            MetadataId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            WasDryRun = false,
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MetadataAuthoringResponse>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Success.Should().BeTrue();
        deserialized.LogicalName.Should().Be("new_entity");
        deserialized.MetadataId.Should().Be(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        deserialized.WasDryRun.Should().BeFalse();
        deserialized.Error.Should().BeNull();
    }

    [Fact]
    public void MetadataAuthoringResponse_WithValidationMessages_SerializesCorrectly()
    {
        var response = new MetadataAuthoringResponse
        {
            Success = false,
            Error = "Validation failed",
            ErrorCode = "INVALID_SCHEMA_NAME",
            ValidationMessages =
            [
                new ValidationMessageDto
                {
                    Field = "SchemaName",
                    Rule = "INVALID_SCHEMA_NAME",
                    Message = "Schema name must start with publisher prefix"
                }
            ],
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MetadataAuthoringResponse>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Success.Should().BeFalse();
        deserialized.Error.Should().Be("Validation failed");
        deserialized.ErrorCode.Should().Be("INVALID_SCHEMA_NAME");
        deserialized.ValidationMessages.Should().HaveCount(1);
        deserialized.ValidationMessages![0].Field.Should().Be("SchemaName");
    }

    [Fact]
    public void MetadataAuthoringResponse_DryRun_SerializesCorrectly()
    {
        var response = new MetadataAuthoringResponse
        {
            Success = true,
            WasDryRun = true,
            ValidationMessages =
            [
                new ValidationMessageDto { Field = "SchemaName", Rule = "OK", Message = "Valid" }
            ],
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);

        json.Should().Contain("\"wasDryRun\":true");
        json.Should().Contain("\"success\":true");
    }

    [Fact]
    public void MetadataDeleteResponse_Success_SerializesCorrectly()
    {
        var response = new MetadataDeleteResponse
        {
            Success = true,
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MetadataDeleteResponse>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Success.Should().BeTrue();
        deserialized.Error.Should().BeNull();
        deserialized.Dependencies.Should().BeNull();
    }

    [Fact]
    public void MetadataDeleteResponse_WithDependencies_SerializesCorrectly()
    {
        var response = new MetadataDeleteResponse
        {
            Success = false,
            Error = "Cannot delete due to dependencies",
            ErrorCode = "DEPENDENCY_CONFLICT",
            DependencyCount = 2,
            Dependencies =
            [
                new DependencyDto
                {
                    DependentComponentType = "Form",
                    DependentComponentName = "Account Main Form",
                    DependentComponentSchemaName = "account_main"
                },
                new DependencyDto
                {
                    DependentComponentType = "View",
                    DependentComponentName = "Active Accounts",
                }
            ],
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<MetadataDeleteResponse>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Success.Should().BeFalse();
        deserialized.DependencyCount.Should().Be(2);
        deserialized.Dependencies.Should().HaveCount(2);
        deserialized.Dependencies![0].DependentComponentType.Should().Be("Form");
        deserialized.Dependencies[1].DependentComponentSchemaName.Should().BeNull();
    }

    [Fact]
    public void ValidationMessageDto_SerializesCorrectly()
    {
        var dto = new ValidationMessageDto
        {
            Field = "SchemaName",
            Rule = "INVALID_PREFIX",
            Message = "Schema name must start with 'new_'"
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);

        json.Should().Contain("\"field\":\"SchemaName\"");
        json.Should().Contain("\"rule\":\"INVALID_PREFIX\"");
        json.Should().Contain("\"message\":");
    }

    [Fact]
    public void MetadataAuthoringResponse_NullOptionalFields_OmittedFromJson()
    {
        var response = new MetadataAuthoringResponse
        {
            Success = true,
            WasDryRun = false,
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);

        // Optional fields with null should be omitted (JsonIgnore WhenWritingNull)
        json.Should().NotContain("\"logicalName\"");
        json.Should().NotContain("\"metadataId\"");
        json.Should().NotContain("\"error\"");
        json.Should().NotContain("\"errorCode\"");
        json.Should().NotContain("\"validationMessages\"");
    }

    #endregion
}
