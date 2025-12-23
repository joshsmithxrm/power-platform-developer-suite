using System;
using System.Collections.Generic;
using FluentAssertions;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.Configuration;

/// <summary>
/// Tests for EnvironmentResolver.
/// </summary>
public class EnvironmentResolverTests
{
    #region GetEnvironment Tests

    [Fact]
    public void GetEnvironment_ReturnsEnvironment_WhenExists()
    {
        // Arrange
        var options = new DataverseOptions
        {
            Environments = new Dictionary<string, DataverseEnvironmentOptions>
            {
                ["dev"] = new DataverseEnvironmentOptions
                {
                    Name = "dev",
                    Url = "https://contoso-dev.crm.dynamics.com"
                }
            }
        };

        // Act
        var result = EnvironmentResolver.GetEnvironment(options, "dev");

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("dev");
        result.Url.Should().Be("https://contoso-dev.crm.dynamics.com");
    }

    [Fact]
    public void GetEnvironment_Throws_WhenNotFound()
    {
        // Arrange
        var options = new DataverseOptions
        {
            Environments = new Dictionary<string, DataverseEnvironmentOptions>
            {
                ["dev"] = new DataverseEnvironmentOptions { Name = "dev" }
            }
        };

        // Act & Assert
        var act = () => EnvironmentResolver.GetEnvironment(options, "prod");
        act.Should().Throw<KeyNotFoundException>()
            .WithMessage("*prod*not found*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GetEnvironment_Throws_WhenNameEmpty(string? name)
    {
        // Arrange
        var options = new DataverseOptions();

        // Act & Assert
        var act = () => EnvironmentResolver.GetEnvironment(options, name!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetEnvironment_Throws_WhenOptionsNull()
    {
        // Act & Assert
        var act = () => EnvironmentResolver.GetEnvironment(null!, "dev");
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region GetDefaultEnvironment Tests

    [Fact]
    public void GetDefaultEnvironment_ReturnsSpecifiedDefault_WhenSet()
    {
        // Arrange
        var options = new DataverseOptions
        {
            DefaultEnvironment = "prod",
            Environments = new Dictionary<string, DataverseEnvironmentOptions>
            {
                ["dev"] = new DataverseEnvironmentOptions { Name = "dev" },
                ["prod"] = new DataverseEnvironmentOptions
                {
                    Name = "prod",
                    Url = "https://contoso.crm.dynamics.com"
                }
            }
        };

        // Act
        var result = EnvironmentResolver.GetDefaultEnvironment(options);

        // Assert
        result.Name.Should().Be("prod");
    }

    [Fact]
    public void GetDefaultEnvironment_ReturnsFirstEnvironment_WhenNoDefaultSet()
    {
        // Arrange
        var options = new DataverseOptions
        {
            Environments = new Dictionary<string, DataverseEnvironmentOptions>
            {
                ["dev"] = new DataverseEnvironmentOptions { Name = "dev" }
            }
        };

        // Act
        var result = EnvironmentResolver.GetDefaultEnvironment(options);

        // Assert
        result.Name.Should().Be("dev");
    }

    [Fact]
    public void GetDefaultEnvironment_ReturnsVirtualEnvironment_WhenNoEnvironmentsDefined()
    {
        // Arrange
        var connection = new DataverseConnection("Primary", "AuthType=...");
        var options = new DataverseOptions
        {
            Url = "https://contoso.crm.dynamics.com",
            TenantId = "tenant-123",
            Connections = new List<DataverseConnection> { connection }
        };

        // Act
        var result = EnvironmentResolver.GetDefaultEnvironment(options);

        // Assert
        result.Name.Should().Be("default");
        result.Url.Should().Be("https://contoso.crm.dynamics.com");
        result.TenantId.Should().Be("tenant-123");
        result.Connections.Should().ContainSingle().Which.Should().Be(connection);
    }

    [Fact]
    public void GetDefaultEnvironment_Throws_WhenOptionsNull()
    {
        // Act & Assert
        var act = () => EnvironmentResolver.GetDefaultEnvironment(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region HasEnvironment Tests

    [Fact]
    public void HasEnvironment_ReturnsTrue_WhenExists()
    {
        // Arrange
        var options = new DataverseOptions
        {
            Environments = new Dictionary<string, DataverseEnvironmentOptions>
            {
                ["dev"] = new DataverseEnvironmentOptions { Name = "dev" }
            }
        };

        // Act & Assert
        EnvironmentResolver.HasEnvironment(options, "dev").Should().BeTrue();
    }

    [Fact]
    public void HasEnvironment_ReturnsFalse_WhenNotExists()
    {
        // Arrange
        var options = new DataverseOptions
        {
            Environments = new Dictionary<string, DataverseEnvironmentOptions>
            {
                ["dev"] = new DataverseEnvironmentOptions { Name = "dev" }
            }
        };

        // Act & Assert
        EnvironmentResolver.HasEnvironment(options, "prod").Should().BeFalse();
    }

    [Fact]
    public void HasEnvironment_ReturnsFalse_WhenOptionsNull()
    {
        // Act & Assert
        EnvironmentResolver.HasEnvironment(null!, "dev").Should().BeFalse();
    }

    #endregion

    #region GetEnvironmentNames Tests

    [Fact]
    public void GetEnvironmentNames_ReturnsAllNames()
    {
        // Arrange
        var options = new DataverseOptions
        {
            Environments = new Dictionary<string, DataverseEnvironmentOptions>
            {
                ["dev"] = new DataverseEnvironmentOptions { Name = "dev" },
                ["qa"] = new DataverseEnvironmentOptions { Name = "qa" },
                ["prod"] = new DataverseEnvironmentOptions { Name = "prod" }
            }
        };

        // Act
        var result = EnvironmentResolver.GetEnvironmentNames(options);

        // Assert
        result.Should().BeEquivalentTo(new[] { "dev", "qa", "prod" });
    }

    [Fact]
    public void GetEnvironmentNames_ReturnsEmpty_WhenNoEnvironments()
    {
        // Arrange
        var options = new DataverseOptions();

        // Act
        var result = EnvironmentResolver.GetEnvironmentNames(options);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region DataverseEnvironmentOptions Tests

    [Fact]
    public void DataverseEnvironmentOptions_HasConnections_ReturnsTrue_WhenConnectionsExist()
    {
        // Arrange
        var env = new DataverseEnvironmentOptions
        {
            Name = "dev",
            Connections = new List<DataverseConnection>
            {
                new DataverseConnection("Primary", "...")
            }
        };

        // Assert
        env.HasConnections.Should().BeTrue();
    }

    [Fact]
    public void DataverseEnvironmentOptions_HasConnections_ReturnsFalse_WhenEmpty()
    {
        // Arrange
        var env = new DataverseEnvironmentOptions
        {
            Name = "dev"
        };

        // Assert
        env.HasConnections.Should().BeFalse();
    }

    #endregion

    #region DataverseOptions Multi-Environment Tests

    [Fact]
    public void DataverseOptions_Environments_DefaultsToEmpty()
    {
        // Arrange & Act
        var options = new DataverseOptions();

        // Assert
        options.Environments.Should().NotBeNull();
        options.Environments.Should().BeEmpty();
    }

    [Fact]
    public void DataverseOptions_SupportsMultipleEnvironments()
    {
        // Arrange
        var options = new DataverseOptions
        {
            DefaultEnvironment = "source",
            Environments = new Dictionary<string, DataverseEnvironmentOptions>
            {
                ["source"] = new DataverseEnvironmentOptions
                {
                    Name = "source",
                    Url = "https://source.crm.dynamics.com",
                    Connections = new List<DataverseConnection>
                    {
                        new DataverseConnection("SourceApp", "AuthType=...")
                    }
                },
                ["target"] = new DataverseEnvironmentOptions
                {
                    Name = "target",
                    Url = "https://target.crm.dynamics.com",
                    Connections = new List<DataverseConnection>
                    {
                        new DataverseConnection("TargetApp", "AuthType=...")
                    }
                }
            }
        };

        // Assert
        options.Environments.Should().HaveCount(2);
        options.DefaultEnvironment.Should().Be("source");
    }

    #endregion
}
