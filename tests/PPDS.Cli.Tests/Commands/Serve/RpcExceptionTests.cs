using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve;

public class RpcExceptionTests
{
    [Fact]
    public void Constructor_WithCodeAndMessage_SetsProperties()
    {
        var exception = new RpcException(ErrorCodes.Auth.ProfileNotFound, "Profile not found");

        Assert.Equal(ErrorCodes.Auth.ProfileNotFound, exception.StructuredErrorCode);
        Assert.Equal("Profile not found", exception.Message);
    }

    [Fact]
    public void Constructor_SetsErrorData()
    {
        var exception = new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile");

        var errorData = exception.ErrorData as RpcErrorData;
        Assert.NotNull(errorData);
        Assert.Equal(ErrorCodes.Auth.NoActiveProfile, errorData.Code);
        Assert.Equal("No active profile", errorData.Message);
    }

    [Fact]
    public void Constructor_WithInnerException_SetsProperties()
    {
        var inner = new InvalidOperationException("Inner error");
        var exception = new RpcException(ErrorCodes.Operation.Internal, inner);

        Assert.Equal(ErrorCodes.Operation.Internal, exception.StructuredErrorCode);
        Assert.Equal("Inner error", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void Constructor_WithInnerException_HandlesDetailsCorrectly()
    {
        var inner = new InvalidOperationException("Inner error");
        var exception = new RpcException(ErrorCodes.Operation.Internal, inner);

        var errorData = exception.ErrorData as RpcErrorData;
        Assert.NotNull(errorData);

#if DEBUG
        // In debug builds, Details contains the full exception string for debugging
        Assert.Contains("Inner error", errorData.Details);
#else
        // In release builds, Details is not populated (avoid leaking internal details)
        Assert.Null(errorData.Details);
#endif
    }

    [Theory]
    [InlineData(ErrorCodes.Auth.ProfileNotFound)]
    [InlineData(ErrorCodes.Auth.NoActiveProfile)]
    [InlineData(ErrorCodes.Connection.EnvironmentNotFound)]
    [InlineData(ErrorCodes.Validation.RequiredField)]
    [InlineData(ErrorCodes.Operation.NotSupported)]
    public void Constructor_AcceptsVariousErrorCodes(string errorCode)
    {
        var exception = new RpcException(errorCode, "Test message");

        Assert.Equal(errorCode, exception.StructuredErrorCode);
    }
}

public class RpcErrorDataTests
{
    [Fact]
    public void DefaultValues_AreEmptyStrings()
    {
        var errorData = new RpcErrorData();

        Assert.Equal("", errorData.Code);
        Assert.Equal("", errorData.Message);
    }

    [Fact]
    public void OptionalProperties_DefaultToNull()
    {
        var errorData = new RpcErrorData();

        Assert.Null(errorData.Details);
        Assert.Null(errorData.Target);
    }

    [Fact]
    public void AllProperties_CanBeSet()
    {
        var errorData = new RpcErrorData
        {
            Code = "Test.Error",
            Message = "Test message",
            Details = "Stack trace here",
            Target = "profile"
        };

        Assert.Equal("Test.Error", errorData.Code);
        Assert.Equal("Test message", errorData.Message);
        Assert.Equal("Stack trace here", errorData.Details);
        Assert.Equal("profile", errorData.Target);
    }
}
