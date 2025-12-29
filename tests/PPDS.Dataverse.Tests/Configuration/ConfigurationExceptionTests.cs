using FluentAssertions;
using PPDS.Dataverse.Configuration;
using Xunit;

namespace PPDS.Dataverse.Tests.Configuration;

public class ConfigurationExceptionTests
{
    #region MissingRequiredWithHints Tests

    [Fact]
    public void MissingRequiredWithHints_WithEnvironment_FormatsMessageCorrectly()
    {
        // Act
        var exception = ConfigurationException.MissingRequiredWithHints(
            propertyName: "Url",
            connectionName: "Secondary",
            connectionIndex: 1,
            environmentName: "Dev");

        // Assert
        exception.PropertyName.Should().Be("Url");
        exception.ConnectionName.Should().Be("Secondary");
        exception.ConnectionIndex.Should().Be(1);
        exception.EnvironmentName.Should().Be("Dev");

        exception.Message.Should().Contain("Dataverse Configuration Error");
        exception.Message.Should().Contain("Missing required property: Url"); // base message
        exception.Message.Should().Contain("Connection: Secondary (index: 1)");
        exception.Message.Should().Contain("Environment: Dev");
    }

    [Fact]
    public void MissingRequiredWithHints_WithEnvironment_GeneratesCorrectHints()
    {
        // Act
        var exception = ConfigurationException.MissingRequiredWithHints(
            propertyName: "Url",
            connectionName: "Secondary",
            connectionIndex: 1,
            environmentName: "Dev");

        // Assert
        exception.ResolutionHints.Should().HaveCount(3);
        exception.ResolutionHints[0].Should().Be("Dataverse:Environments:Dev:Connections:1:Url");
        exception.ResolutionHints[1].Should().Be("Dataverse:Environments:Dev:Url");
        exception.ResolutionHints[2].Should().Be("Dataverse:Url");
    }

    [Fact]
    public void MissingRequiredWithHints_WithoutEnvironment_GeneratesCorrectHints()
    {
        // Act
        var exception = ConfigurationException.MissingRequiredWithHints(
            propertyName: "ClientId",
            connectionName: "Primary",
            connectionIndex: 0,
            environmentName: null);

        // Assert
        exception.ResolutionHints.Should().HaveCount(2);
        exception.ResolutionHints[0].Should().Be("Dataverse:Connections:0:ClientId");
        exception.ResolutionHints[1].Should().Be("Dataverse:ClientId");
    }

    [Fact]
    public void MissingRequiredWithHints_WithoutEnvironment_DoesNotIncludeEnvironmentInMessage()
    {
        // Act
        var exception = ConfigurationException.MissingRequiredWithHints(
            propertyName: "ClientId",
            connectionName: "Primary",
            connectionIndex: 0,
            environmentName: null);

        // Assert
        exception.Message.Should().NotContain("Environment:");
        exception.Message.Should().Contain("Connection: Primary");
    }

    [Fact]
    public void MissingRequiredWithHints_MessageIncludesResolutionHints()
    {
        // Act
        var exception = ConfigurationException.MissingRequiredWithHints(
            propertyName: "Url",
            connectionName: "Secondary",
            connectionIndex: 1,
            environmentName: "Dev");

        // Assert
        exception.Message.Should().Contain("Configure Url at any of these levels");
        exception.Message.Should().Contain("1. Dataverse:Environments:Dev:Connections:1:Url");
        exception.Message.Should().Contain("2. Dataverse:Environments:Dev:Url");
        exception.Message.Should().Contain("3. Dataverse:Url");
    }

    [Fact]
    public void MissingRequiredWithHints_CustomSectionName_UsesInHints()
    {
        // Act
        var exception = ConfigurationException.MissingRequiredWithHints(
            propertyName: "Url",
            connectionName: "Primary",
            connectionIndex: 0,
            environmentName: "Dev",
            sectionName: "PowerPlatform");

        // Assert
        exception.ResolutionHints[0].Should().Be("PowerPlatform:Environments:Dev:Connections:0:Url");
        exception.ResolutionHints[1].Should().Be("PowerPlatform:Environments:Dev:Url");
        exception.ResolutionHints[2].Should().Be("PowerPlatform:Url");
    }

    #endregion

    #region NoConnectionsConfigured Tests

    [Fact]
    public void NoConnectionsConfigured_WithEnvironment_FormatsCorrectly()
    {
        // Act
        var exception = ConfigurationException.NoConnectionsConfigured("Dev");

        // Assert
        exception.PropertyName.Should().Be("Connections");
        exception.EnvironmentName.Should().Be("Dev");
        exception.ConnectionName.Should().BeNull();
        exception.ConnectionIndex.Should().BeNull();

        exception.Message.Should().Contain("At least one connection must be configured");
        exception.Message.Should().Contain("Environment: Dev");
    }

    [Fact]
    public void NoConnectionsConfigured_WithEnvironment_GeneratesCorrectHints()
    {
        // Act
        var exception = ConfigurationException.NoConnectionsConfigured("Dev");

        // Assert
        exception.ResolutionHints.Should().HaveCount(2);
        exception.ResolutionHints[0].Should().Be("Dataverse:Environments:Dev:Connections");
        exception.ResolutionHints[1].Should().Be("Dataverse:Connections");
    }

    [Fact]
    public void NoConnectionsConfigured_WithoutEnvironment_GeneratesCorrectHints()
    {
        // Act
        var exception = ConfigurationException.NoConnectionsConfigured(environmentName: null);

        // Assert
        exception.ResolutionHints.Should().HaveCount(1);
        exception.ResolutionHints[0].Should().Be("Dataverse:Connections");
    }

    #endregion

    #region Legacy Factory Method Tests (Backwards Compatibility)

    [Fact]
    public void MissingRequired_LegacyMethod_StillWorks()
    {
        // Act
        var exception = ConfigurationException.MissingRequired("Primary", "ClientId");

        // Assert
        exception.ConnectionName.Should().Be("Primary");
        exception.PropertyName.Should().Be("ClientId");
        exception.Message.Should().Contain("Connection 'Primary'");
        exception.Message.Should().Contain("'ClientId' is required");
    }

    [Fact]
    public void InvalidValue_LegacyMethod_StillWorks()
    {
        // Act
        var exception = ConfigurationException.InvalidValue("Primary", "Url", "Must be a valid URI");

        // Assert
        exception.ConnectionName.Should().Be("Primary");
        exception.PropertyName.Should().Be("Url");
        exception.Message.Should().Contain("'Url' is invalid");
        exception.Message.Should().Contain("Must be a valid URI");
    }

    [Fact]
    public void SecretResolutionFailed_PreservesInnerException()
    {
        // Arrange
        var innerException = new InvalidOperationException("Key Vault access denied");

        // Act
        var exception = ConfigurationException.SecretResolutionFailed(
            "Primary",
            "ClientSecret",
            "KeyVault",
            innerException);

        // Assert
        exception.InnerException.Should().BeSameAs(innerException);
        exception.Message.Should().Contain("Primary");
        exception.Message.Should().Contain("ClientSecret");
        exception.Message.Should().Contain("KeyVault");
    }

    #endregion

    #region Message Formatting Tests

    [Fact]
    public void Message_ContainsHeaderFollowedByBlankLine()
    {
        // Act
        var exception = ConfigurationException.MissingRequiredWithHints(
            propertyName: "Url",
            connectionName: "Primary",
            connectionIndex: 0,
            environmentName: "Dev");

        // Assert - Header should be followed by blank line (no ====== separator)
        exception.Message.Should().Contain("Dataverse Configuration Error" + Environment.NewLine + Environment.NewLine);
    }

    [Fact]
    public void Message_StartsWithNewline_ForCleanConsoleOutput()
    {
        // Act
        var exception = ConfigurationException.MissingRequiredWithHints(
            propertyName: "Url",
            connectionName: "Primary",
            connectionIndex: 0,
            environmentName: "Dev");

        // Assert - Message should start with newline for clean console display
        exception.Message.Should().StartWith(Environment.NewLine);
    }

    [Fact]
    public void Message_WithMissingConnectionName_ShowsIndexOnly()
    {
        // Arrange - Connection name is empty (validation catches this before Url)
        var exception = ConfigurationException.MissingRequiredWithHints(
            propertyName: "Name",
            connectionName: "[index 2]",
            connectionIndex: 2,
            environmentName: "Dev");

        // Assert
        exception.Message.Should().Contain("[index 2]");
    }

    #endregion

    #region Simple Constructor Tests

    [Fact]
    public void Constructor_WithMessage_SetsMessage()
    {
        // Act
        var exception = new ConfigurationException("Test error message");

        // Assert
        exception.Message.Should().Be("Test error message");
    }

    [Fact]
    public void Constructor_WithMessageAndInnerException_PreservesBoth()
    {
        // Arrange
        var inner = new InvalidOperationException("Inner error");

        // Act
        var exception = new ConfigurationException("Outer error", inner);

        // Assert
        exception.Message.Should().Be("Outer error");
        exception.InnerException.Should().BeSameAs(inner);
    }

    #endregion
}
