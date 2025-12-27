using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Security;
using Xunit;

namespace PPDS.Dataverse.Tests.Pooling;

public class ConnectionStringSourceTests
{
    [Fact]
    public void Constructor_WithNullConfig_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ConnectionStringSource(null!));
    }

    [Fact]
    public void Constructor_WithValidConfig_SetsProperties()
    {
        var config = new DataverseConnection("TestConnection")
        {
            MaxPoolSize = 15
        };

        var source = new ConnectionStringSource(config);

        Assert.Equal("TestConnection", source.Name);
        Assert.Equal(15, source.MaxPoolSize);

        source.Dispose();
    }

    [Fact]
    public void Name_ReturnsConfigName()
    {
        var config = new DataverseConnection("MySource");

        var source = new ConnectionStringSource(config);

        Assert.Equal("MySource", source.Name);

        source.Dispose();
    }

    [Fact]
    public void MaxPoolSize_ReturnsConfigMaxPoolSize()
    {
        var config = new DataverseConnection("Test")
        {
            MaxPoolSize = 25
        };

        var source = new ConnectionStringSource(config);

        Assert.Equal(25, source.MaxPoolSize);

        source.Dispose();
    }

    [Fact]
    public void GetSeedClient_WithInvalidConfig_ThrowsDataverseConnectionException()
    {
        // Create a config with missing required fields
        var config = new DataverseConnection("Invalid")
        {
            Url = "https://test.crm.dynamics.com",
            ClientId = "12345678-1234-1234-1234-123456789012"
            // Missing ClientSecret
        };

        var source = new ConnectionStringSource(config);

        // GetSeedClient should throw because secret is required for ClientSecret auth
        var ex = Assert.Throws<DataverseConnectionException>(() => source.GetSeedClient());
        Assert.Contains("Invalid", ex.ConnectionName);

        source.Dispose();
    }

    [Fact]
    public void GetSeedClient_AfterDispose_ThrowsObjectDisposedException()
    {
        var config = new DataverseConnection("Test");

        var source = new ConnectionStringSource(config);
        source.Dispose();

        Assert.Throws<ObjectDisposedException>(() => source.GetSeedClient());
    }

    [Fact]
    public void Dispose_WhenClientNotCreated_DoesNotThrow()
    {
        var config = new DataverseConnection("Test");

        var source = new ConnectionStringSource(config);

        // Should not throw even if GetSeedClient was never called
        var exception = Record.Exception(() => source.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var config = new DataverseConnection("Test");

        var source = new ConnectionStringSource(config);

        source.Dispose();

        // Second dispose should not throw
        var exception = Record.Exception(() => source.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void GetSeedClient_WithMissingUrl_ThrowsDataverseConnectionException()
    {
        var config = new DataverseConnection("NoUrl")
        {
            ClientId = "12345678-1234-1234-1234-123456789012",
            ClientSecret = "some-secret"
            // Missing Url
        };

        var source = new ConnectionStringSource(config);

        var ex = Assert.Throws<DataverseConnectionException>(() => source.GetSeedClient());
        Assert.Equal("NoUrl", ex.ConnectionName);

        source.Dispose();
    }

    [Fact]
    public void GetSeedClient_WithMissingClientId_ThrowsDataverseConnectionException()
    {
        var config = new DataverseConnection("NoClientId")
        {
            Url = "https://test.crm.dynamics.com",
            ClientSecret = "some-secret"
            // Missing ClientId
        };

        var source = new ConnectionStringSource(config);

        var ex = Assert.Throws<DataverseConnectionException>(() => source.GetSeedClient());
        Assert.Equal("NoClientId", ex.ConnectionName);

        source.Dispose();
    }
}
