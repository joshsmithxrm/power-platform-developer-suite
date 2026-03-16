using FluentAssertions;
using PPDS.Mcp.Infrastructure;
using Xunit;

namespace PPDS.Mcp.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="McpSessionOptions"/>.
/// </summary>
public sealed class McpSessionOptionsTests
{
    #region Parse Tests

    [Fact]
    public void Parse_WithProfileFlag_SetsProfile()
    {
        // Arrange
        var args = new[] { "--profile", "Dev" };

        // Act
        var options = McpSessionOptions.Parse(args);

        // Assert
        options.Profile.Should().Be("Dev");
    }

    [Fact]
    public void Parse_WithEnvironmentFlag_SetsEnvironment()
    {
        // Arrange
        var args = new[] { "--environment", "https://org.crm.dynamics.com" };

        // Act
        var options = McpSessionOptions.Parse(args);

        // Assert
        options.Environment.Should().Be("https://org.crm.dynamics.com");
    }

    [Fact]
    public void Parse_WithReadOnlyFlag_SetsReadOnly()
    {
        // Arrange
        var args = new[] { "--read-only" };

        // Act
        var options = McpSessionOptions.Parse(args);

        // Assert
        options.ReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Parse_WithMultipleAllowedEnv_AddsAllUrls()
    {
        // Arrange
        var args = new[]
        {
            "--allowed-env", "https://dev.crm.dynamics.com",
            "--allowed-env", "https://test.crm.dynamics.com",
            "--allowed-env", "https://prod.crm.dynamics.com"
        };

        // Act
        var options = McpSessionOptions.Parse(args);

        // Assert
        options.AllowedEnvironments.Should().HaveCount(3);
        options.AllowedEnvironments.Should().ContainInOrder(
            "https://dev.crm.dynamics.com",
            "https://test.crm.dynamics.com",
            "https://prod.crm.dynamics.com");
    }

    [Fact]
    public void Parse_WithNoArgs_ReturnsDefaults()
    {
        // Arrange
        var args = Array.Empty<string>();

        // Act
        var options = McpSessionOptions.Parse(args);

        // Assert
        options.Profile.Should().BeNull();
        options.Environment.Should().BeNull();
        options.ReadOnly.Should().BeFalse();
        options.AllowedEnvironments.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WithCombinedArgs_SetsAllFields()
    {
        // Arrange
        var args = new[]
        {
            "--profile", "Production",
            "--environment", "https://prod.crm.dynamics.com",
            "--read-only",
            "--allowed-env", "https://prod.crm.dynamics.com",
            "--allowed-env", "https://staging.crm.dynamics.com"
        };

        // Act
        var options = McpSessionOptions.Parse(args);

        // Assert
        options.Profile.Should().Be("Production");
        options.Environment.Should().Be("https://prod.crm.dynamics.com");
        options.ReadOnly.Should().BeTrue();
        options.AllowedEnvironments.Should().HaveCount(2);
        options.AllowedEnvironments.Should().Contain("https://prod.crm.dynamics.com");
        options.AllowedEnvironments.Should().Contain("https://staging.crm.dynamics.com");
    }

    #endregion

    #region IsEnvironmentAllowed Tests

    [Fact]
    public void IsEnvironmentAllowed_EmptyList_ReturnsFalse()
    {
        // Arrange
        var options = new McpSessionOptions();

        // Act
        var result = options.IsEnvironmentAllowed("https://org.crm.dynamics.com");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsEnvironmentAllowed_MatchingUrl_ReturnsTrue()
    {
        // Arrange
        var options = new McpSessionOptions
        {
            AllowedEnvironments = new List<string>
            {
                "https://dev.crm.dynamics.com",
                "https://prod.crm.dynamics.com"
            }
        };

        // Act
        var result = options.IsEnvironmentAllowed("https://prod.crm.dynamics.com");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsEnvironmentAllowed_NonMatchingUrl_ReturnsFalse()
    {
        // Arrange
        var options = new McpSessionOptions
        {
            AllowedEnvironments = new List<string>
            {
                "https://dev.crm.dynamics.com",
                "https://prod.crm.dynamics.com"
            }
        };

        // Act
        var result = options.IsEnvironmentAllowed("https://staging.crm.dynamics.com");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsEnvironmentAllowed_TrailingSlashNormalization_MatchesRegardless()
    {
        // Arrange - allowlist has URL without trailing slash
        var options = new McpSessionOptions
        {
            AllowedEnvironments = new List<string>
            {
                "https://org.crm.dynamics.com"
            }
        };

        // Act - query with trailing slash
        var result = options.IsEnvironmentAllowed("https://org.crm.dynamics.com/");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsEnvironmentAllowed_TrailingSlashInAllowlist_MatchesWithout()
    {
        // Arrange - allowlist has URL with trailing slash
        var options = new McpSessionOptions
        {
            AllowedEnvironments = new List<string>
            {
                "https://org.crm.dynamics.com/"
            }
        };

        // Act - query without trailing slash
        var result = options.IsEnvironmentAllowed("https://org.crm.dynamics.com");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsEnvironmentAllowed_CaseInsensitive_Matches()
    {
        // Arrange
        var options = new McpSessionOptions
        {
            AllowedEnvironments = new List<string>
            {
                "https://ORG.CRM.DYNAMICS.COM"
            }
        };

        // Act
        var result = options.IsEnvironmentAllowed("https://org.crm.dynamics.com");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsEnvironmentAllowed_CaseInsensitive_UpperCaseQuery()
    {
        // Arrange
        var options = new McpSessionOptions
        {
            AllowedEnvironments = new List<string>
            {
                "https://org.crm.dynamics.com"
            }
        };

        // Act
        var result = options.IsEnvironmentAllowed("HTTPS://ORG.CRM.DYNAMICS.COM");

        // Assert
        result.Should().BeTrue();
    }

    #endregion
}
