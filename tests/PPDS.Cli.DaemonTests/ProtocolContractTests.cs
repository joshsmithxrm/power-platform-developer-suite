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

    private readonly DaemonTestFixture _fixture;

    public ProtocolContractTests(DaemonTestFixture fixture)
    {
        _fixture = fixture;
    }

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

                // Verify the Code property exists and contains our error code
                jsonElement.TryGetProperty("Code", out var codeElement).Should().BeTrue(
                    "error data should have a 'Code' property (PascalCase from server)");
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
        }
    }
}
