using System.Text.Json;
using PPDS.Auth.Credentials;
using PPDS.Cli.Commands.Serve.Handlers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Tests for DaemonDeviceCodeHandler and DeviceCodeNotification.
/// </summary>
public class DaemonDeviceCodeHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region DeviceCodeNotification Tests

    [Fact]
    public void DeviceCodeNotification_DefaultValues_AreEmptyStrings()
    {
        var notification = new DeviceCodeNotification();

        Assert.Equal("", notification.UserCode);
        Assert.Equal("", notification.VerificationUrl);
        Assert.Equal("", notification.Message);
    }

    [Fact]
    public void DeviceCodeNotification_WithValues_SerializesCorrectly()
    {
        var notification = new DeviceCodeNotification
        {
            UserCode = "ABC123",
            VerificationUrl = "https://microsoft.com/devicelogin",
            Message = "To sign in, use a web browser to open the page"
        };

        var json = JsonSerializer.Serialize(notification, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<DeviceCodeNotification>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("ABC123", deserialized.UserCode);
        Assert.Equal("https://microsoft.com/devicelogin", deserialized.VerificationUrl);
        Assert.Equal("To sign in, use a web browser to open the page", deserialized.Message);
    }

    [Fact]
    public void DeviceCodeNotification_UsesJsonPropertyNames()
    {
        var notification = new DeviceCodeNotification
        {
            UserCode = "XYZ789",
            VerificationUrl = "https://example.com/login",
            Message = "Enter the code"
        };

        var json = JsonSerializer.Serialize(notification, JsonOptions);

        Assert.Contains("\"userCode\"", json);
        Assert.Contains("\"verificationUrl\"", json);
        Assert.Contains("\"message\"", json);
    }

    #endregion

    #region CreateCallback Tests

    [Fact]
    public void CreateCallback_NullRpc_ReturnsCallbackThatDoesNotThrow()
    {
        var callback = DaemonDeviceCodeHandler.CreateCallback(null);

        var deviceCodeInfo = new DeviceCodeInfo(
            "ABC123",
            "https://microsoft.com/devicelogin",
            "Enter code ABC123");

        // Should not throw when invoked with null RPC
        var exception = Record.Exception(() => callback(deviceCodeInfo));
        Assert.Null(exception);
    }

    [Fact]
    public void CreateCallback_ReturnsNonNullCallback()
    {
        var callback = DaemonDeviceCodeHandler.CreateCallback(null);

        Assert.NotNull(callback);
    }

    #endregion
}
