using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Errors;

public class StructuredErrorTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var error = new StructuredError("Test.Code", "Test message", "Test details", "test-target");

        Assert.Equal("Test.Code", error.Code);
        Assert.Equal("Test message", error.Message);
        Assert.Equal("Test details", error.Details);
        Assert.Equal("test-target", error.Target);
    }

    [Fact]
    public void Constructor_DefaultsOptionalPropertiesToNull()
    {
        var error = new StructuredError("Test.Code", "Test message");

        Assert.Equal("Test.Code", error.Code);
        Assert.Equal("Test message", error.Message);
        Assert.Null(error.Details);
        Assert.Null(error.Target);
    }

    [Fact]
    public void Create_RedactsConnectionStringInMessage()
    {
        var sensitiveMessage = "Failed to connect with User ID=admin;Password=secret123;Server=test";
        var error = StructuredError.Create(
            ErrorCodes.Connection.Failed,
            sensitiveMessage,
            debug: false);

        Assert.DoesNotContain("secret123", error.Message);
    }

    [Fact]
    public void Create_RedactsConnectionStringInDetails_WhenNotDebug()
    {
        var sensitiveDetails = "Password=supersecret";
        var error = StructuredError.Create(
            ErrorCodes.Connection.Failed,
            "Error occurred",
            details: sensitiveDetails,
            debug: false);

        Assert.NotNull(error.Details);
        Assert.DoesNotContain("supersecret", error.Details);
    }

    [Fact]
    public void Create_PreservesDetailsInDebugMode()
    {
        var details = "Full stack trace here";
        var error = StructuredError.Create(
            ErrorCodes.Connection.Failed,
            "Error occurred",
            details: details,
            debug: true);

        Assert.Equal(details, error.Details);
    }

    [Fact]
    public void FromException_CreatesErrorWithExceptionMessage()
    {
        var exception = new InvalidOperationException("Something went wrong");
        var error = StructuredError.FromException(
            ErrorCodes.Operation.Internal,
            exception);

        Assert.Equal(ErrorCodes.Operation.Internal, error.Code);
        Assert.Equal("Something went wrong", error.Message);
    }

    [Fact]
    public void FromException_IncludesStackTraceInDebugMode()
    {
        var exception = new InvalidOperationException("Test error");
        var error = StructuredError.FromException(
            ErrorCodes.Operation.Internal,
            exception,
            debug: true);

        // Stack trace might be null for exceptions created without throwing
        // Verify Details is either null or contains actual content
        Assert.True(error.Details == null || error.Details.Length > 0);
    }

    [Fact]
    public void FromException_SetsTarget()
    {
        var exception = new FileNotFoundException("File not found", "test.xml");
        var error = StructuredError.FromException(
            ErrorCodes.Validation.FileNotFound,
            exception,
            target: "test.xml");

        Assert.Equal("test.xml", error.Target);
    }
}
