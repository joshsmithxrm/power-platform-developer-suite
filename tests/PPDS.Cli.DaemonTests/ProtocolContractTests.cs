using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using PPDS.Cli.Commands.Serve.Handlers;
using StreamJsonRpc;
using Xunit;

namespace PPDS.Cli.DaemonTests;

/// <summary>
/// Protocol contract tests to verify JSON response shapes don't change unexpectedly.
/// These tests catch breaking protocol changes.
/// </summary>
[Collection("Daemon")]
[Trait("Category", "Integration")]
public class ProtocolContractTests : IClassFixture<DaemonTestFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Checked-in inventory of all RPC methods exposed by the daemon.
    /// If a method is added or removed, this list must be updated intentionally.
    /// </summary>
    private static readonly string[] ExpectedMethods =
    [
        "_heartbeat",
        "auth/list",
        "auth/select",
        "auth/who",
        "connectionReferences/analyze",
        "connectionReferences/get",
        "connectionReferences/list",
        "customApis/addParameter",
        "customApis/get",
        "customApis/list",
        "customApis/register",
        "customApis/removeParameter",
        "customApis/setPlugin",
        "customApis/unregister",
        "customApis/update",
        "customApis/updateParameter",
        "dataProviders/get",
        "dataProviders/list",
        "dataProviders/register",
        "dataProviders/unregister",
        "dataProviders/update",
        "dataSources/get",
        "dataSources/list",
        "dataSources/register",
        "dataSources/unregister",
        "dataSources/update",
        "env/config/get",
        "env/config/remove",
        "env/config/set",
        "env/list",
        "env/select",
        "env/who",
        "environmentVariables/get",
        "environmentVariables/list",
        "environmentVariables/set",
        "environmentVariables/syncDeploymentSettings",
        "importJobs/get",
        "importJobs/list",
        "metadata/createColumn",
        "metadata/createGlobalChoice",
        "metadata/createKey",
        "metadata/createManyToMany",
        "metadata/createOneToMany",
        "metadata/createTable",
        "metadata/deleteColumn",
        "metadata/deleteGlobalChoice",
        "metadata/deleteKey",
        "metadata/deleteRelationship",
        "metadata/deleteTable",
        "metadata/entities",
        "metadata/entity",
        "metadata/globalOptionSet",
        "metadata/globalOptionSets",
        "metadata/updateColumn",
        "metadata/updateTable",
        "pluginTraces/delete",
        "pluginTraces/get",
        "pluginTraces/list",
        "pluginTraces/setTraceLevel",
        "pluginTraces/timeline",
        "pluginTraces/traceLevel",
        "plugins/downloadBinary",
        "plugins/entityAttributes",
        "plugins/get",
        "plugins/list",
        "plugins/messages",
        "plugins/registerAssembly",
        "plugins/registerImage",
        "plugins/registerPackage",
        "plugins/registerStep",
        "plugins/toggleStep",
        "plugins/unregister",
        "plugins/updateImage",
        "plugins/updateStep",
        "profiles/create",
        "profiles/delete",
        "profiles/invalidate",
        "profiles/rename",
        "query/complete",
        "query/explain",
        "query/export",
        "query/fetch",
        "query/history/delete",
        "query/history/list",
        "query/sql",
        "query/validate",
        "serviceEndpoints/get",
        "serviceEndpoints/list",
        "serviceEndpoints/register",
        "serviceEndpoints/unregister",
        "serviceEndpoints/update",
        "solutions/components",
        "solutions/list",
        "webResources/get",
        "webResources/getModifiedOn",
        "webResources/list",
        "webResources/publish",
        "webResources/publishAll",
        "webResources/update",
    ];

    private readonly DaemonTestFixture _fixture;

    public ProtocolContractTests(DaemonTestFixture fixture)
    {
        _fixture = fixture;
    }

    #region Method Inventory

    [Fact]
    public void AllRpcMethods_MatchCheckedInInventory()
    {
        var methods = typeof(RpcMethodHandler)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes<JsonRpcMethodAttribute>())
            .Select(a => a.Name)
            .OrderBy(n => n)
            .ToList();

        methods.Should().BeEquivalentTo(ExpectedMethods,
            "RPC method inventory changed — update ExpectedMethods if this is intentional");
    }

    #endregion

    #region Auth Shape Tests

    [Fact]
    public async Task AuthListResponse_HasExpectedShape()
    {
        // Act
        var response = await _fixture.Rpc.InvokeWithCancellationAsync<AuthListResponse>(
            "auth/list",
            cancellationToken: CancellationToken.None);

        // Assert - Verify expected properties exist
        var json = JsonSerializer.Serialize(response, JsonOptions);

        // Required fields
        json.Should().Contain("\"profiles\"");

        // Optional fields may be null/omitted
        response.ActiveProfile.Should().BeNull("no profiles in empty store");
    }

    [Fact]
    public async Task AuthSelectError_HasExpectedShape()
    {
        // Act & Assert — auth/select with no args should fail with validation error
        try
        {
            await _fixture.Rpc.InvokeWithCancellationAsync<AuthSelectResponse>(
                "auth/select",
                cancellationToken: CancellationToken.None);

            Assert.Fail("Expected exception was not thrown");
        }
        catch (RemoteInvocationException ex)
        {
            ex.Message.Should().NotBeNullOrEmpty();

            if (ex.ErrorData is JsonElement jsonElement)
            {
                jsonElement.ValueKind.Should().Be(JsonValueKind.Object, "error data should be an object");
                jsonElement.TryGetProperty("code", out var codeElement).Should().BeTrue(
                    "error data should have a 'code' property");
                codeElement.GetString().Should().NotBeNullOrEmpty();
            }
        }
    }

    #endregion

    #region Environment Shape Tests

    [Fact]
    public async Task EnvListError_HasExpectedShape()
    {
        // Act & Assert — env/list with no active profile should fail
        try
        {
            await _fixture.Rpc.InvokeWithCancellationAsync<EnvListResponse>(
                "env/list",
                cancellationToken: CancellationToken.None);

            Assert.Fail("Expected exception was not thrown");
        }
        catch (RemoteInvocationException ex)
        {
            ex.Message.Should().NotBeNullOrEmpty();

            if (ex.ErrorData is JsonElement jsonElement)
            {
                jsonElement.ValueKind.Should().Be(JsonValueKind.Object, "error data should be an object");
                jsonElement.TryGetProperty("code", out var codeElement).Should().BeTrue(
                    "error data should have a 'code' property");
                codeElement.GetString().Should().NotBeNullOrEmpty();
            }
        }
    }

    [Fact]
    public async Task EnvWhoError_HasExpectedShape()
    {
        // Act & Assert — env/who with no active profile should fail
        try
        {
            await _fixture.Rpc.InvokeWithCancellationAsync<EnvWhoResponse>(
                "env/who",
                cancellationToken: CancellationToken.None);

            Assert.Fail("Expected exception was not thrown");
        }
        catch (RemoteInvocationException ex)
        {
            ex.Message.Should().NotBeNullOrEmpty();

            if (ex.ErrorData is JsonElement jsonElement)
            {
                jsonElement.ValueKind.Should().Be(JsonValueKind.Object, "error data should be an object");
                jsonElement.TryGetProperty("code", out var codeElement).Should().BeTrue(
                    "error data should have a 'code' property");
                codeElement.GetString().Should().NotBeNullOrEmpty();
            }
        }
    }

    [Fact]
    public async Task EnvSelectError_HasExpectedShape()
    {
        // Act & Assert — env/select with invalid environment should fail
        try
        {
            await _fixture.Rpc.InvokeWithCancellationAsync<EnvSelectResponse>(
                "env/select",
                new object[] { "nonexistent-env-12345" },
                cancellationToken: CancellationToken.None);

            Assert.Fail("Expected exception was not thrown");
        }
        catch (RemoteInvocationException ex)
        {
            ex.Message.Should().NotBeNullOrEmpty();

            if (ex.ErrorData is JsonElement jsonElement)
            {
                jsonElement.ValueKind.Should().Be(JsonValueKind.Object, "error data should be an object");
                jsonElement.TryGetProperty("code", out var codeElement).Should().BeTrue(
                    "error data should have a 'code' property");
                codeElement.GetString().Should().NotBeNullOrEmpty();
            }
        }
    }

    #endregion

    #region Query Shape Tests

    [Fact]
    public async Task QueryValidateResponse_HasExpectedShape()
    {
        // Act — query/validate is parse-only, no profile required
        var response = await _fixture.Rpc.InvokeWithCancellationAsync<QueryValidateResponse>(
            "query/validate",
            new object[] { new QueryValidateRequest { Sql = "SELECT name FROM account", Language = "sql" } },
            CancellationToken.None);

        // Assert
        var json = JsonSerializer.Serialize(response, JsonOptions);
        json.Should().Contain("\"diagnostics\"");
        response.Diagnostics.Should().NotBeNull();
        response.Diagnostics.Should().BeEmpty("valid SQL should produce no diagnostics");
    }

    [Fact]
    public async Task QueryValidateResponse_InvalidSql_ReturnsDiagnostics()
    {
        // Act — "FORM" is invalid SQL, should produce diagnostics
        var response = await _fixture.Rpc.InvokeWithCancellationAsync<QueryValidateResponse>(
            "query/validate",
            new object[] { new QueryValidateRequest { Sql = "SELECT name FORM account", Language = "sql" } },
            CancellationToken.None);

        // Assert
        response.Diagnostics.Should().NotBeNull();
        response.Diagnostics.Should().NotBeEmpty("invalid SQL should produce diagnostics");

        var diag = response.Diagnostics[0];
        var diagJson = JsonSerializer.Serialize(diag, JsonOptions);
        diagJson.Should().Contain("\"start\"");
        diagJson.Should().Contain("\"length\"");
        diagJson.Should().Contain("\"severity\"");
        diagJson.Should().Contain("\"message\"");

        diag.Message.Should().NotBeNullOrEmpty();
        diag.Severity.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task QueryCompleteResponse_HasExpectedShape()
    {
        // Act — query/complete requires a profile for metadata; expect error shape if no profile
        try
        {
            var response = await _fixture.Rpc.InvokeWithCancellationAsync<QueryCompleteResponse>(
                "query/complete",
                new object[] { new QueryCompleteRequest { Sql = "SELECT ", CursorOffset = 7, Language = "sql" } },
                CancellationToken.None);

            // If it succeeds (unlikely without a profile), verify shape
            var json = JsonSerializer.Serialize(response, JsonOptions);
            json.Should().Contain("\"items\"");
            response.Items.Should().NotBeNull();
        }
        catch (RemoteInvocationException ex)
        {
            // Expected without an active profile — verify error shape
            ex.Message.Should().NotBeNullOrEmpty();

            if (ex.ErrorData is JsonElement jsonElement)
            {
                jsonElement.ValueKind.Should().Be(JsonValueKind.Object, "error data should be an object");
                jsonElement.TryGetProperty("code", out var codeElement).Should().BeTrue(
                    "error data should have a 'code' property");
                codeElement.GetString().Should().NotBeNullOrEmpty();
            }
        }
    }

    [Fact]
    public async Task QuerySqlError_HasExpectedShape()
    {
        // Act & Assert — query/sql requires a profile, should fail
        try
        {
            await _fixture.Rpc.InvokeWithCancellationAsync<QueryResultResponse>(
                "query/sql",
                new object[] { new QuerySqlRequest { Sql = "SELECT name FROM account" } },
                CancellationToken.None);

            Assert.Fail("Expected exception was not thrown");
        }
        catch (RemoteInvocationException ex)
        {
            ex.Message.Should().NotBeNullOrEmpty();

            if (ex.ErrorData is JsonElement jsonElement)
            {
                jsonElement.ValueKind.Should().Be(JsonValueKind.Object, "error data should be an object");
                jsonElement.TryGetProperty("code", out var codeElement).Should().BeTrue(
                    "error data should have a 'code' property");
                codeElement.GetString().Should().NotBeNullOrEmpty();

                // Verify message property exists in error data
                if (jsonElement.TryGetProperty("message", out var messageElement))
                {
                    messageElement.GetString().Should().NotBeNullOrEmpty();
                }
            }
        }
    }

    #endregion

    #region Existing Contract Tests

    [Fact]
    public async Task ProfilesInvalidateResponse_HasExpectedShape()
    {
        // Act
        var response = await _fixture.Rpc.InvokeWithCancellationAsync<ProfilesInvalidateResponse>(
            "profiles/invalidate",
            new object[] { "test-profile" },
            CancellationToken.None);

        // Assert
        var json = JsonSerializer.Serialize(response, JsonOptions);

        json.Should().Contain("\"profileName\"");
        json.Should().Contain("\"invalidated\"");
        json.Should().Contain("\"test-profile\"");
        json.Should().Contain("true");
    }

    [Fact]
    public async Task ErrorResponse_ContainsStructuredErrorCode()
    {
        // Act & Assert
        try
        {
            await _fixture.Rpc.InvokeWithCancellationAsync<AuthWhoResponse>(
                "auth/who",
                cancellationToken: CancellationToken.None);

            Assert.Fail("Expected exception was not thrown");
        }
        catch (RemoteInvocationException ex)
        {
            // Assert - Error should contain structured information
            ex.Message.Should().NotBeNullOrEmpty();
            ex.Message.Should().Contain("No active profile");

            // ErrorData contains the structured error from LocalRpcException.ErrorData
            // With StreamJsonRpc's System.Text.Json formatter, this comes back as a JsonElement
            if (ex.ErrorData is JsonElement jsonElement)
            {
                // The JsonElement should contain our RpcErrorData structure
                jsonElement.ValueKind.Should().Be(JsonValueKind.Object, "error data should be an object");

                // Verify the code property exists and contains our error code
                jsonElement.TryGetProperty("code", out var codeElement).Should().BeTrue(
                    "error data should have a 'code' property (camelCase via [JsonPropertyName])");
                codeElement.GetString().Should().Contain("Auth", "error code should indicate auth error");
            }
            else if (ex.ErrorData != null)
            {
                // Fallback for other formatter types - just verify it serializes with error info
                var errorJson = JsonSerializer.Serialize(ex.ErrorData, JsonOptions);
                errorJson.Should().ContainAny("Code", "code", "Auth");
            }
        }
    }

    [Fact]
    public async Task AuthListResponse_ProfilesArrayIsAlwaysPresent()
    {
        // Act
        var response = await _fixture.Rpc.InvokeWithCancellationAsync<AuthListResponse>(
            "auth/list",
            cancellationToken: CancellationToken.None);

        // Assert - profiles should never be null, even when empty
        response.Profiles.Should().NotBeNull();
        response.Profiles.Should().BeAssignableTo<IEnumerable<ProfileInfo>>();
    }

    [Fact]
    public async Task ValidationError_HasExpectedFormat()
    {
        // Act & Assert - Validation errors should have consistent format
        try
        {
            await _fixture.Rpc.InvokeWithCancellationAsync<AuthSelectResponse>(
                "auth/select",
                cancellationToken: CancellationToken.None);  // Missing required params

            Assert.Fail("Expected exception was not thrown");
        }
        catch (RemoteInvocationException ex)
        {
            // Validation errors should indicate what's missing
            ex.Message.Should().NotBeNullOrEmpty();
            // Should mention the required field
            ex.Message.Should().ContainAny("index", "name", "required");

            // Enhanced: Also check structured error data
            if (ex.ErrorData is JsonElement jsonElement)
            {
                jsonElement.ValueKind.Should().Be(JsonValueKind.Object, "error data should be an object");

                // Check for validationErrors array if present and non-null
                if (jsonElement.TryGetProperty("validationErrors", out var validationErrors)
                    && validationErrors.ValueKind != JsonValueKind.Null)
                {
                    validationErrors.ValueKind.Should().Be(JsonValueKind.Array,
                        "validationErrors should be an array");

                    foreach (var error in validationErrors.EnumerateArray())
                    {
                        error.TryGetProperty("field", out _).Should().BeTrue(
                            "each validation error should have a 'field' property");
                        error.TryGetProperty("message", out _).Should().BeTrue(
                            "each validation error should have a 'message' property");
                    }
                }
            }
        }
    }

    #endregion
}
