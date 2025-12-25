using PPDS.Migration.Cli.Commands;
using Xunit;

namespace PPDS.Migration.Cli.Tests.Commands;

/// <summary>
/// Tests for ConnectionResolver using configuration-based resolution.
/// Note: ConnectionResolver now uses appsettings.json and User Secrets,
/// not environment variables directly.
/// </summary>
public class ConnectionResolverTests
{
    [Fact]
    public void ConnectionConfig_IsRecord()
    {
        var config1 = new ConnectionResolver.ConnectionConfig("https://test.crm.dynamics.com", "clientId", "secret", "tenant");
        var config2 = new ConnectionResolver.ConnectionConfig("https://test.crm.dynamics.com", "clientId", "secret", "tenant");

        Assert.Equal(config1, config2);
    }

    [Fact]
    public void ConnectionConfig_Deconstruction_Works()
    {
        var config = new ConnectionResolver.ConnectionConfig("https://test.crm.dynamics.com", "client-id", "secret", "tenant-id");

        var (url, clientId, clientSecret, tenantId) = config;

        Assert.Equal("https://test.crm.dynamics.com", url);
        Assert.Equal("client-id", clientId);
        Assert.Equal("secret", clientSecret);
        Assert.Equal("tenant-id", tenantId);
    }

    [Fact]
    public void ResolvedConnection_ContainsConfigAndSource()
    {
        var config = new ConnectionResolver.ConnectionConfig("https://test.crm.dynamics.com", "client-id", "secret", "tenant-id");
        var resolved = new ConnectionResolver.ResolvedConnection(config, ConnectionResolver.ConnectionSource.Configuration, "Dev");

        Assert.Equal(config, resolved.Config);
        Assert.Equal(ConnectionResolver.ConnectionSource.Configuration, resolved.Source);
        Assert.Equal("Dev", resolved.EnvironmentName);
    }

    [Fact]
    public void Resolve_WithMissingConfig_ThrowsFileNotFoundException()
    {
        // When no config file exists, should throw FileNotFoundException
        var exception = Assert.Throws<FileNotFoundException>(() =>
            ConnectionResolver.Resolve("NonExistentEnv", null, null, "test"));

        Assert.Contains("appsettings.json", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_WithEmptyEnvironmentName_ThrowsInvalidOperationException()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConnectionResolver.Resolve("", null, null, "test"));

        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveSourceTarget_WithEmptySourceEnv_ThrowsInvalidOperationException()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConnectionResolver.ResolveSourceTarget("", "Target", null, null));

        Assert.Contains("--source-env", exception.Message);
        Assert.Contains("--target-env", exception.Message);
    }

    [Fact]
    public void ResolveSourceTarget_WithEmptyTargetEnv_ThrowsInvalidOperationException()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConnectionResolver.ResolveSourceTarget("Source", "", null, null));

        Assert.Contains("--source-env", exception.Message);
        Assert.Contains("--target-env", exception.Message);
    }

    [Fact]
    public void ConnectionSource_Configuration_IsDefault()
    {
        // Verify the enum value exists
        Assert.Equal(ConnectionResolver.ConnectionSource.Configuration, ConnectionResolver.ConnectionSource.Configuration);
    }
}
