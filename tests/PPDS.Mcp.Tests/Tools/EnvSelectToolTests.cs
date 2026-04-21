using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="EnvSelectTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvSelectToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new EnvSelectTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Parameter Validation Tests

    [Fact]
    public async Task ExecuteAsync_NullEnvironment_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new EnvSelectTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("environment");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyEnvironment_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new EnvSelectTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("environment");
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceEnvironment_ThrowsArgumentException()
    {
        // Arrange
        var context = CreateContext();
        var tool = new EnvSelectTool(context);

        // Act
        Func<Task> act = () => tool.ExecuteAsync("   ");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("environment");
    }

    #endregion

    #region Allowlist No-Op Tests (L15 — same-env re-select bypasses allowlist)

    [Fact]
    public void McpSessionOptions_EmptyAllowlist_BlocksAllSwitching()
    {
        // Constitution SS2: if no allowlist is configured, the session is locked to
        // its initial environment. IsEnvironmentAllowed returns false for ALL URLs.
        var options = new McpSessionOptions();

        options.IsEnvironmentAllowed("https://org.crm.dynamics.com").Should().BeFalse(
            because: "empty allowlist means no switching is permitted");
    }

    [Fact]
    public void McpSessionOptions_EmptyAllowlist_StillReturnsFalseForCurrentEnv()
    {
        // The no-op bypass (L15) is handled in EnvSelectTool.ExecuteAsync — it checks
        // isSameEnvironment BEFORE calling ValidateEnvironmentSwitch.
        // IsEnvironmentAllowed itself still returns false; the short-circuit is higher up.
        var options = new McpSessionOptions
        {
            Environment = "https://org.crm.dynamics.com"  // session-locked env
        };

        // IsEnvironmentAllowed returns false even for the locked URL — that is correct;
        // the tool bypasses the check via isSameEnvironment comparison, not by
        // relaxing IsEnvironmentAllowed.
        options.IsEnvironmentAllowed("https://org.crm.dynamics.com").Should().BeFalse(
            because: "IsEnvironmentAllowed is unmodified; the bypass lives in EnvSelectTool");
    }

    #endregion

    private static McpToolContext CreateContext()
    {
        return new McpToolContext(
            new Mock<IMcpConnectionPoolManager>().Object,
            new ProfileStore(),
            new Mock<ISecureCredentialStore>().Object,
            new McpSessionOptions());
    }
}
