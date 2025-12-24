using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.Pooling;

public class DataverseConnectionTests
{
    [Fact]
    public void ToString_ExcludesCredentials()
    {
        var connection = new DataverseConnection("Primary")
        {
            Url = "https://org.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012",
            ClientSecret = "supersecret123",
            AuthType = DataverseAuthType.ClientSecret
        };

        var result = connection.ToString();

        Assert.DoesNotContain("supersecret", result);
        Assert.Contains("Primary", result);
        Assert.Contains("Url", result);
        Assert.Contains("AuthType", result);
    }

    [Fact]
    public void ToString_IncludesNameUrlAndAuthType()
    {
        var connection = new DataverseConnection("TestConnection")
        {
            Url = "https://test.crm.dynamics.com",
            AuthType = DataverseAuthType.ClientSecret
        };

        var result = connection.ToString();

        Assert.Contains("TestConnection", result);
        Assert.Contains("https://test.crm.dynamics.com", result);
        Assert.Contains("ClientSecret", result);
    }

    [Fact]
    public void Constructor_WithName_SetsNameProperty()
    {
        var connection = new DataverseConnection("TestName");

        Assert.Equal("TestName", connection.Name);
        Assert.Equal(10, connection.MaxPoolSize); // Default
        Assert.Equal(DataverseAuthType.ClientSecret, connection.AuthType); // Default
    }

    [Fact]
    public void DefaultConstructor_SetsDefaults()
    {
        var connection = new DataverseConnection();

        Assert.Equal(string.Empty, connection.Name);
        Assert.Equal(10, connection.MaxPoolSize);
        Assert.Equal(DataverseAuthType.ClientSecret, connection.AuthType);
        Assert.Null(connection.Url);
        Assert.Null(connection.ClientId);
        Assert.Null(connection.ClientSecret);
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        var connection = new DataverseConnection
        {
            Name = "Primary",
            Url = "https://contoso.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012",
            ClientSecret = "my-secret",
            TenantId = "87654321-4321-4321-4321-210987654321",
            AuthType = DataverseAuthType.ClientSecret,
            MaxPoolSize = 25
        };

        Assert.Equal("Primary", connection.Name);
        Assert.Equal("https://contoso.crm.dynamics.com", connection.Url);
        Assert.Equal("12345678-1234-1234-1234-123456789012", connection.ClientId);
        Assert.Equal("my-secret", connection.ClientSecret);
        Assert.Equal("87654321-4321-4321-4321-210987654321", connection.TenantId);
        Assert.Equal(DataverseAuthType.ClientSecret, connection.AuthType);
        Assert.Equal(25, connection.MaxPoolSize);
    }

    [Fact]
    public void CertificateProperties_CanBeSet()
    {
        var connection = new DataverseConnection("CertAuth")
        {
            AuthType = DataverseAuthType.Certificate,
            Url = "https://contoso.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012",
            CertificateThumbprint = "1234567890ABCDEF",
            CertificateStoreName = "My",
            CertificateStoreLocation = "CurrentUser"
        };

        Assert.Equal(DataverseAuthType.Certificate, connection.AuthType);
        Assert.Equal("1234567890ABCDEF", connection.CertificateThumbprint);
        Assert.Equal("My", connection.CertificateStoreName);
        Assert.Equal("CurrentUser", connection.CertificateStoreLocation);
    }

    [Fact]
    public void OAuthProperties_CanBeSet()
    {
        var connection = new DataverseConnection("OAuth")
        {
            AuthType = DataverseAuthType.OAuth,
            Url = "https://contoso.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012",
            RedirectUri = "http://localhost:8080",
            LoginPrompt = OAuthLoginPrompt.Always
        };

        Assert.Equal(DataverseAuthType.OAuth, connection.AuthType);
        Assert.Equal("http://localhost:8080", connection.RedirectUri);
        Assert.Equal(OAuthLoginPrompt.Always, connection.LoginPrompt);
    }

    [Fact]
    public void SecretResolutionProperties_CanBeSet()
    {
        var connection = new DataverseConnection("KeyVault")
        {
            ClientSecretKeyVaultUri = "https://myvault.vault.azure.net/secrets/dataverse-secret",
            ClientSecret = "DATAVERSE_SECRET"
        };

        Assert.Equal("https://myvault.vault.azure.net/secrets/dataverse-secret", connection.ClientSecretKeyVaultUri);
        Assert.Equal("DATAVERSE_SECRET", connection.ClientSecret);
    }
}
