using System;
using FluentAssertions;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.Configuration;

/// <summary>
/// Tests for ConnectionStringBuilder.
/// </summary>
public class ConnectionStringBuilderTests
{
    #region Raw Connection String Tests

    [Fact]
    public void Build_WithRawConnectionString_ReturnsAsIs()
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            ConnectionString = "AuthType=ClientSecret;Url=https://test.crm.dynamics.com;ClientId=xxx;ClientSecret=yyy"
        };

        // Act
        var result = ConnectionStringBuilder.Build(connection);

        // Assert
        result.Should().Be(connection.ConnectionString);
    }

    [Fact]
    public void Build_WithRawConnectionString_IgnoresTypedConfig()
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            ConnectionString = "AuthType=ClientSecret;Url=https://test.crm.dynamics.com;ClientId=xxx;ClientSecret=yyy",
            // These should be ignored
            Url = "https://other.crm.dynamics.com",
            ClientId = "other-client-id"
        };

        // Act
        var result = ConnectionStringBuilder.Build(connection);

        // Assert
        result.Should().Be(connection.ConnectionString);
        result.Should().Contain("https://test.crm.dynamics.com");
    }

    #endregion

    #region ClientSecret Authentication Tests

    [Fact]
    public void Build_ClientSecret_CreatesValidConnectionString()
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            AuthType = DataverseAuthType.ClientSecret,
            Url = "https://contoso.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012"
        };

        // Act
        var result = ConnectionStringBuilder.Build(connection, resolvedSecret: "my-secret");

        // Assert
        result.Should().Contain("AuthType=ClientSecret");
        result.Should().Contain("Url=https://contoso.crm.dynamics.com");
        result.Should().Contain("ClientId=12345678-1234-1234-1234-123456789012");
        result.Should().Contain("ClientSecret=my-secret");
    }

    [Fact]
    public void Build_ClientSecret_IncludesTenantId()
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            AuthType = DataverseAuthType.ClientSecret,
            Url = "https://contoso.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012",
            TenantId = "87654321-4321-4321-4321-210987654321"
        };

        // Act
        var result = ConnectionStringBuilder.Build(connection, resolvedSecret: "my-secret");

        // Assert
        result.Should().Contain("TenantId=87654321-4321-4321-4321-210987654321");
    }

    [Fact]
    public void Build_ClientSecret_ThrowsOnMissingUrl()
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            AuthType = DataverseAuthType.ClientSecret,
            ClientId = "12345678-1234-1234-1234-123456789012"
        };

        // Act & Assert
        var act = () => ConnectionStringBuilder.Build(connection, resolvedSecret: "my-secret");
        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Message.Contains("Url"));
    }

    [Fact]
    public void Build_ClientSecret_ThrowsOnMissingClientId()
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            AuthType = DataverseAuthType.ClientSecret,
            Url = "https://contoso.crm.dynamics.com"
        };

        // Act & Assert
        var act = () => ConnectionStringBuilder.Build(connection, resolvedSecret: "my-secret");
        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Message.Contains("ClientId"));
    }

    [Fact]
    public void Build_ClientSecret_ThrowsOnMissingSecret()
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            AuthType = DataverseAuthType.ClientSecret,
            Url = "https://contoso.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012"
        };

        // Act & Assert
        var act = () => ConnectionStringBuilder.Build(connection, resolvedSecret: null);
        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Message.Contains("ClientSecret"));
    }

    #endregion

    #region Certificate Authentication Tests

    [Fact]
    public void Build_Certificate_CreatesValidConnectionString()
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            AuthType = DataverseAuthType.Certificate,
            Url = "https://contoso.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012",
            CertificateThumbprint = "1234567890ABCDEF"
        };

        // Act
        var result = ConnectionStringBuilder.Build(connection);

        // Assert
        result.Should().Contain("AuthType=Certificate");
        result.Should().Contain("Url=https://contoso.crm.dynamics.com");
        result.Should().Contain("ClientId=12345678-1234-1234-1234-123456789012");
        result.Should().Contain("Thumbprint=1234567890ABCDEF");
    }

    [Fact]
    public void Build_Certificate_IncludesStoreInfo()
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            AuthType = DataverseAuthType.Certificate,
            Url = "https://contoso.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012",
            CertificateThumbprint = "1234567890ABCDEF",
            CertificateStoreName = "Root",
            CertificateStoreLocation = "LocalMachine"
        };

        // Act
        var result = ConnectionStringBuilder.Build(connection);

        // Assert
        result.Should().Contain("StoreName=Root");
        result.Should().Contain("StoreLocation=LocalMachine");
    }

    #endregion

    #region OAuth Authentication Tests

    [Fact]
    public void Build_OAuth_CreatesValidConnectionString()
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            AuthType = DataverseAuthType.OAuth,
            Url = "https://contoso.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012",
            RedirectUri = "http://localhost:8080"
        };

        // Act
        var result = ConnectionStringBuilder.Build(connection);

        // Assert
        result.Should().Contain("AuthType=OAuth");
        result.Should().Contain("Url=https://contoso.crm.dynamics.com");
        result.Should().Contain("ClientId=12345678-1234-1234-1234-123456789012");
        result.Should().Contain("RedirectUri=http://localhost:8080");
    }

    [Theory]
    [InlineData(OAuthLoginPrompt.Auto, "Auto")]
    [InlineData(OAuthLoginPrompt.Always, "Always")]
    [InlineData(OAuthLoginPrompt.Never, "Never")]
    [InlineData(OAuthLoginPrompt.SelectAccount, "SelectAccount")]
    public void Build_OAuth_IncludesLoginPrompt(OAuthLoginPrompt prompt, string expected)
    {
        // Arrange
        var connection = new DataverseConnection
        {
            Name = "Test",
            AuthType = DataverseAuthType.OAuth,
            Url = "https://contoso.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012",
            RedirectUri = "http://localhost:8080",
            LoginPrompt = prompt
        };

        // Act
        var result = ConnectionStringBuilder.Build(connection);

        // Assert
        result.Should().Contain($"LoginPrompt={expected}");
    }

    #endregion

    #region DataverseConnection Tests

    [Fact]
    public void UsesTypedConfiguration_True_WhenConnectionStringEmpty()
    {
        var connection = new DataverseConnection
        {
            Name = "Test",
            Url = "https://contoso.crm.dynamics.com"
        };

        connection.UsesTypedConfiguration.Should().BeTrue();
    }

    [Fact]
    public void UsesTypedConfiguration_False_WhenConnectionStringSet()
    {
        var connection = new DataverseConnection("Test", "AuthType=ClientSecret;...");

        connection.UsesTypedConfiguration.Should().BeFalse();
    }

    #endregion
}
