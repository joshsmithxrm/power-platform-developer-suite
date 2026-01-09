using System.Text.Json;
using FluentAssertions;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Session;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Tests for RpcMethodHandler with pool manager integration.
/// </summary>
public class RpcMethodHandlerPoolTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region ProfilesInvalidate Tests

    [Fact]
    public void ProfilesInvalidate_CallsPoolManagerInvalidate()
    {
        // Arrange
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var mockSessionService = new Mock<ISessionService>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, mockSessionService.Object);

        // Act
        var result = handler.ProfilesInvalidate("dev");

        // Assert
        mockPoolManager.Verify(m => m.InvalidateProfile("dev"), Times.Once);
        result.ProfileName.Should().Be("dev");
        result.Invalidated.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ProfilesInvalidate_EmptyProfileName_ThrowsRpcException(string? profileName)
    {
        // Arrange
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var mockSessionService = new Mock<ISessionService>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, mockSessionService.Object);

        // Act
        var act = () => handler.ProfilesInvalidate(profileName!);

        // Assert
        act.Should().Throw<RpcException>()
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Fact]
    public void ProfilesInvalidate_DoesNotCallPoolManager_WhenProfileNameEmpty()
    {
        // Arrange
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var mockSessionService = new Mock<ISessionService>();
        var handler = new RpcMethodHandler(mockPoolManager.Object, mockSessionService.Object);

        // Act
        try
        {
            handler.ProfilesInvalidate("");
        }
        catch (RpcException)
        {
            // Expected
        }

        // Assert - InvalidateProfile should not have been called
        mockPoolManager.Verify(m => m.InvalidateProfile(It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region ProfilesInvalidateResponse DTO Tests

    [Fact]
    public void ProfilesInvalidateResponse_SerializesCorrectly()
    {
        var response = new ProfilesInvalidateResponse
        {
            ProfileName = "test-profile",
            Invalidated = true
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ProfilesInvalidateResponse>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.ProfileName.Should().Be("test-profile");
        deserialized.Invalidated.Should().BeTrue();
    }

    [Fact]
    public void ProfilesInvalidateResponse_JsonProperties_AreCamelCase()
    {
        var response = new ProfilesInvalidateResponse
        {
            ProfileName = "dev",
            Invalidated = true
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);

        json.Should().Contain("\"profileName\"");
        json.Should().Contain("\"invalidated\"");
    }

    #endregion

    #region DeviceCodeNotification DTO Tests

    [Fact]
    public void DeviceCodeNotification_SerializesCorrectly()
    {
        var notification = new DeviceCodeNotification
        {
            UserCode = "ABC123",
            VerificationUrl = "https://microsoft.com/devicelogin",
            Message = "Enter code ABC123 at https://microsoft.com/devicelogin"
        };

        var json = JsonSerializer.Serialize(notification, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<DeviceCodeNotification>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.UserCode.Should().Be("ABC123");
        deserialized.VerificationUrl.Should().Be("https://microsoft.com/devicelogin");
        deserialized.Message.Should().Contain("ABC123");
    }

    [Fact]
    public void DeviceCodeNotification_JsonProperties_AreCamelCase()
    {
        var notification = new DeviceCodeNotification
        {
            UserCode = "TEST",
            VerificationUrl = "https://example.com",
            Message = "Test message"
        };

        var json = JsonSerializer.Serialize(notification, JsonOptions);

        json.Should().Contain("\"userCode\"");
        json.Should().Contain("\"verificationUrl\"");
        json.Should().Contain("\"message\"");
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenPoolManagerIsNull()
    {
        // Arrange
        var mockSessionService = new Mock<ISessionService>();

        // Act
        var act = () => new RpcMethodHandler(null!, mockSessionService.Object);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("poolManager");
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenSessionServiceIsNull()
    {
        // Arrange
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();

        // Act
        var act = () => new RpcMethodHandler(mockPoolManager.Object, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("sessionService");
    }

    #endregion
}
