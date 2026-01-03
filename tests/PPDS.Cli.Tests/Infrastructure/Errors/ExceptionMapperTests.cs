using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Resilience;
using PPDS.Dataverse.Security;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Errors;

public class ExceptionMapperTests
{
    #region Map Tests

    [Fact]
    public void Map_AuthenticationException_ReturnsAuthCode()
    {
        var ex = new AuthenticationException("Auth failed");
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Auth.InvalidCredentials, error.Code);
        Assert.Equal("Auth failed", error.Message);
    }

    [Fact]
    public void Map_AuthenticationException_WithErrorCode_UsesProvidedCode()
    {
        var ex = new AuthenticationException("Profile not found", ErrorCodes.Auth.ProfileNotFound);
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Auth.ProfileNotFound, error.Code);
    }

    [Fact]
    public void Map_DataverseConnectionException_ReturnsConnectionFailed()
    {
        var innerEx = new Exception("Inner error");
        var ex = new DataverseConnectionException("TestConnection", "Unable to connect", innerEx);
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Connection.Failed, error.Code);
        Assert.Equal("TestConnection", error.Target);
    }

    [Fact]
    public void Map_ServiceProtectionException_ReturnsThrottled()
    {
        var ex = new ServiceProtectionException("TestConnection", TimeSpan.FromSeconds(60), 429);
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Connection.Throttled, error.Code);
        Assert.Equal("TestConnection", error.Target);
    }

    [Fact]
    public void Map_TimeoutException_ReturnsTimeout()
    {
        var ex = new TimeoutException("Operation timed out");
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Connection.Timeout, error.Code);
    }

    [Fact]
    public void Map_FileNotFoundException_ReturnsFileNotFound()
    {
        var ex = new FileNotFoundException("File not found", "schema.xml");
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Validation.FileNotFound, error.Code);
        Assert.Equal("schema.xml", error.Target);
    }

    [Fact]
    public void Map_DirectoryNotFoundException_ReturnsDirectoryNotFound()
    {
        var ex = new DirectoryNotFoundException("Directory not found");
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Validation.DirectoryNotFound, error.Code);
    }

    [Fact]
    public void Map_ConfigurationException_ReturnsInvalidValue()
    {
        var ex = new ConfigurationException("TestConnection", "ConnectionString", "Invalid config");
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.Code);
        Assert.Equal("ConnectionString", error.Target);
    }

    [Fact]
    public void Map_ArgumentNullException_ReturnsRequiredField()
    {
        var ex = new ArgumentNullException("schema");
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Validation.RequiredField, error.Code);
        Assert.Equal("schema", error.Target);
    }

    [Fact]
    public void Map_ArgumentException_ReturnsInvalidValue()
    {
        var ex = new ArgumentException("Invalid value", "batchSize");
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.Code);
        Assert.Equal("batchSize", error.Target);
    }

    [Fact]
    public void Map_OperationCanceledException_ReturnsCancelled()
    {
        var ex = new OperationCanceledException();
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Operation.Cancelled, error.Code);
    }

    [Fact]
    public void Map_InvalidOperationException_ReturnsInternal()
    {
        var ex = new InvalidOperationException("Something went wrong");
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Operation.Internal, error.Code);
    }

    [Fact]
    public void Map_NotSupportedException_ReturnsNotSupported()
    {
        var ex = new NotSupportedException("Not supported");
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Operation.NotSupported, error.Code);
    }

    [Fact]
    public void Map_UnknownException_ReturnsInternal()
    {
        var ex = new Exception("Unknown error");
        var error = ExceptionMapper.Map(ex);

        Assert.Equal(ErrorCodes.Operation.Internal, error.Code);
    }

    [Fact]
    public void Map_WithContext_IncludesContextInDetails()
    {
        var ex = new Exception("Test error");
        var error = ExceptionMapper.Map(ex, context: "importing records", debug: true);

        Assert.NotNull(error.Details);
        Assert.Contains("importing records", error.Details);
    }

    #endregion

    #region ToExitCode Tests

    [Fact]
    public void ToExitCode_AuthenticationException_ReturnsAuthError()
    {
        var ex = new AuthenticationException("Auth failed");
        var code = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.AuthError, code);
    }

    [Fact]
    public void ToExitCode_DataverseConnectionException_ReturnsConnectionError()
    {
        var ex = new DataverseConnectionException("Connection failed");
        var code = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.ConnectionError, code);
    }

    [Fact]
    public void ToExitCode_ServiceProtectionException_ReturnsConnectionError()
    {
        var ex = new ServiceProtectionException("Throttled");
        var code = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.ConnectionError, code);
    }

    [Fact]
    public void ToExitCode_TimeoutException_ReturnsConnectionError()
    {
        var ex = new TimeoutException();
        var code = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.ConnectionError, code);
    }

    [Fact]
    public void ToExitCode_FileNotFoundException_ReturnsNotFoundError()
    {
        var ex = new FileNotFoundException();
        var code = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.NotFoundError, code);
    }

    [Fact]
    public void ToExitCode_DirectoryNotFoundException_ReturnsNotFoundError()
    {
        var ex = new DirectoryNotFoundException();
        var code = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.NotFoundError, code);
    }

    [Fact]
    public void ToExitCode_ArgumentException_ReturnsInvalidArguments()
    {
        var ex = new ArgumentException("Invalid");
        var code = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.InvalidArguments, code);
    }

    [Fact]
    public void ToExitCode_ConfigurationException_ReturnsInvalidArguments()
    {
        var ex = new ConfigurationException("Invalid config");
        var code = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.InvalidArguments, code);
    }

    [Fact]
    public void ToExitCode_OperationCanceledException_ReturnsFailure()
    {
        var ex = new OperationCanceledException();
        var code = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.Failure, code);
    }

    [Fact]
    public void ToExitCode_UnknownException_ReturnsFailure()
    {
        var ex = new Exception("Unknown");
        var code = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.Failure, code);
    }

    #endregion

    #region MapWithExitCode Tests

    [Fact]
    public void MapWithExitCode_ReturnsBothErrorAndCode()
    {
        var ex = new FileNotFoundException("Not found", "test.xml");
        var (error, exitCode) = ExceptionMapper.MapWithExitCode(ex);

        Assert.Equal(ErrorCodes.Validation.FileNotFound, error.Code);
        Assert.Equal("test.xml", error.Target);
        Assert.Equal(ExitCodes.NotFoundError, exitCode);
    }

    #endregion
}
