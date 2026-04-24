using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Mcp.Infrastructure;
using Xunit;

namespace PPDS.Mcp.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="McpToolErrorHelper"/>.
/// Verifies finding H7: structured MCP tool errors (shakedown 2026-04-20).
/// </summary>
[Trait("Category", "Unit")]
public sealed class McpToolErrorHelperTests
{
    // ─── ThrowStructuredError(PpdsException) ────────────────────────────────

    [Fact]
    public void ThrowStructuredError_PpdsException_ThrowsMcpException()
    {
        // Arrange
        var ppds = new PpdsException("Solution.ListFailed", "Failed to list solutions.");

        // Act
        var act = () => McpToolErrorHelper.ThrowStructuredError(ppds);

        // Assert
        act.Should().Throw<McpException>();
    }

    [Fact]
    public void ThrowStructuredError_PpdsException_MessageIsValidJson()
    {
        // Arrange
        var ppds = new PpdsException("Solution.ListFailed", "Failed to list solutions.");

        // Act & Assert — message must parse as JSON
        var ex = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError(ppds));

        var json = ex.Message;
        var doc = JsonDocument.Parse(json); // throws if invalid JSON
        doc.Should().NotBeNull();
    }

    [Fact]
    public void ThrowStructuredError_PpdsException_JsonContainsErrorCode()
    {
        // Arrange
        var ppds = new PpdsException("Solution.GetFailed", "Failed to get solution.");

        // Act
        var ex = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError(ppds));

        // Assert
        var doc = JsonDocument.Parse(ex.Message);
        doc.RootElement.GetProperty("errorCode").GetString()
            .Should().Be("Solution.GetFailed");
    }

    [Fact]
    public void ThrowStructuredError_PpdsException_JsonContainsUserMessage()
    {
        // Arrange
        var ppds = new PpdsException("Solution.GetFailed", "The solution was not found.");

        // Act
        var ex = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError(ppds));

        // Assert
        var doc = JsonDocument.Parse(ex.Message);
        doc.RootElement.GetProperty("userMessage").GetString()
            .Should().Be("The solution was not found.");
    }

    [Fact]
    public void ThrowStructuredError_PpdsException_WithContext_JsonContainsContext()
    {
        // Arrange
        var ppds = new PpdsException(
            "Solution.ListFailed",
            "Failed.",
            new Dictionary<string, object> { ["filter"] = "my-solution" });

        // Act
        var ex = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError(ppds));

        // Assert
        var doc = JsonDocument.Parse(ex.Message);
        doc.RootElement.TryGetProperty("context", out var contextEl).Should().BeTrue();
        contextEl.GetProperty("filter").GetString().Should().Be("my-solution");
    }

    [Fact]
    public void ThrowStructuredError_PpdsException_WithoutContext_JsonHasNoContextProperty()
    {
        // Arrange
        var ppds = new PpdsException("Solution.ListFailed", "Failed.");

        // Act
        var ex = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError(ppds));

        // Assert — context key must be absent when null
        var doc = JsonDocument.Parse(ex.Message);
        doc.RootElement.TryGetProperty("context", out _).Should().BeFalse();
    }

    // ─── ThrowStructuredError(Exception) ────────────────────────────────────

    [Fact]
    public void ThrowStructuredError_GenericException_ThrowsMcpException()
    {
        // Arrange
        var inner = new InvalidOperationException("network failure");

        // Act
        var act = () => McpToolErrorHelper.ThrowStructuredError(inner);

        // Assert
        act.Should().Throw<McpException>();
    }

    [Fact]
    public void ThrowStructuredError_GenericException_ErrorCodeIsOperationInternal()
    {
        // Arrange
        var inner = new InvalidOperationException("network failure");

        // Act
        var ex = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError(inner));

        // Assert
        var doc = JsonDocument.Parse(ex.Message);
        doc.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.Operation.Internal);
    }

    [Fact]
    public void ThrowStructuredError_GenericException_DoesNotLeakInnerMessage()
    {
        // Arrange — inner exception detail must not appear in the MCP error payload
        var inner = new InvalidOperationException("very sensitive internal detail");

        // Act
        var ex = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError(inner));

        // Assert
        ex.Message.Should().NotContain("very sensitive internal detail");
    }

    [Fact]
    public void ThrowStructuredError_GenericException_JsonContainsSafeUserMessage()
    {
        // Arrange
        var inner = new Exception("some error");

        // Act
        var ex = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError(inner));

        // Assert
        var doc = JsonDocument.Parse(ex.Message);
        doc.RootElement.GetProperty("userMessage").GetString()
            .Should().NotBeNullOrEmpty();
    }
}
