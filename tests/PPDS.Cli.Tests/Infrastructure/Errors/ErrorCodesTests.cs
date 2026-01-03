using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Errors;

public class ErrorCodesTests
{
    [Fact]
    public void Auth_ProfileNotFound_HasCorrectFormat()
    {
        Assert.Equal("Auth.ProfileNotFound", ErrorCodes.Auth.ProfileNotFound);
    }

    [Fact]
    public void Connection_Failed_HasCorrectFormat()
    {
        Assert.Equal("Connection.Failed", ErrorCodes.Connection.Failed);
    }

    [Fact]
    public void Validation_RequiredField_HasCorrectFormat()
    {
        Assert.Equal("Validation.RequiredField", ErrorCodes.Validation.RequiredField);
    }

    [Fact]
    public void Operation_Cancelled_HasCorrectFormat()
    {
        Assert.Equal("Operation.Cancelled", ErrorCodes.Operation.Cancelled);
    }

    [Fact]
    public void AllCodesFollowHierarchicalFormat()
    {
        var allCodes = new[]
        {
            // Auth codes
            ErrorCodes.Auth.ProfileNotFound,
            ErrorCodes.Auth.Expired,
            ErrorCodes.Auth.InvalidCredentials,
            ErrorCodes.Auth.InsufficientPermissions,
            ErrorCodes.Auth.NoActiveProfile,
            ErrorCodes.Auth.ProfileExists,
            ErrorCodes.Auth.CertificateError,

            // Connection codes
            ErrorCodes.Connection.Failed,
            ErrorCodes.Connection.Throttled,
            ErrorCodes.Connection.Timeout,
            ErrorCodes.Connection.EnvironmentNotFound,
            ErrorCodes.Connection.AmbiguousEnvironment,
            ErrorCodes.Connection.InvalidEnvironmentUrl,

            // Validation codes
            ErrorCodes.Validation.RequiredField,
            ErrorCodes.Validation.InvalidValue,
            ErrorCodes.Validation.FileNotFound,
            ErrorCodes.Validation.DirectoryNotFound,
            ErrorCodes.Validation.SchemaInvalid,
            ErrorCodes.Validation.InvalidArguments,

            // Operation codes
            ErrorCodes.Operation.NotFound,
            ErrorCodes.Operation.Duplicate,
            ErrorCodes.Operation.Dependency,
            ErrorCodes.Operation.PartialFailure,
            ErrorCodes.Operation.Cancelled,
            ErrorCodes.Operation.Internal,
            ErrorCodes.Operation.NotSupported
        };

        foreach (var code in allCodes)
        {
            // All codes should contain a dot (hierarchical format)
            Assert.Contains(".", code);

            // Should have exactly one dot
            Assert.Equal(2, code.Split('.').Length);
        }
    }

    [Fact]
    public void AllCodesAreUnique()
    {
        var allCodes = new[]
        {
            ErrorCodes.Auth.ProfileNotFound,
            ErrorCodes.Auth.Expired,
            ErrorCodes.Auth.InvalidCredentials,
            ErrorCodes.Auth.InsufficientPermissions,
            ErrorCodes.Auth.NoActiveProfile,
            ErrorCodes.Auth.ProfileExists,
            ErrorCodes.Auth.CertificateError,
            ErrorCodes.Connection.Failed,
            ErrorCodes.Connection.Throttled,
            ErrorCodes.Connection.Timeout,
            ErrorCodes.Connection.EnvironmentNotFound,
            ErrorCodes.Connection.AmbiguousEnvironment,
            ErrorCodes.Connection.InvalidEnvironmentUrl,
            ErrorCodes.Validation.RequiredField,
            ErrorCodes.Validation.InvalidValue,
            ErrorCodes.Validation.FileNotFound,
            ErrorCodes.Validation.DirectoryNotFound,
            ErrorCodes.Validation.SchemaInvalid,
            ErrorCodes.Validation.InvalidArguments,
            ErrorCodes.Operation.NotFound,
            ErrorCodes.Operation.Duplicate,
            ErrorCodes.Operation.Dependency,
            ErrorCodes.Operation.PartialFailure,
            ErrorCodes.Operation.Cancelled,
            ErrorCodes.Operation.Internal,
            ErrorCodes.Operation.NotSupported
        };

        Assert.Equal(allCodes.Length, allCodes.Distinct().Count());
    }

    [Fact]
    public void AuthCodes_StartWithAuth()
    {
        Assert.StartsWith("Auth.", ErrorCodes.Auth.ProfileNotFound);
        Assert.StartsWith("Auth.", ErrorCodes.Auth.Expired);
        Assert.StartsWith("Auth.", ErrorCodes.Auth.InvalidCredentials);
    }

    [Fact]
    public void ConnectionCodes_StartWithConnection()
    {
        Assert.StartsWith("Connection.", ErrorCodes.Connection.Failed);
        Assert.StartsWith("Connection.", ErrorCodes.Connection.Throttled);
        Assert.StartsWith("Connection.", ErrorCodes.Connection.Timeout);
    }

    [Fact]
    public void ValidationCodes_StartWithValidation()
    {
        Assert.StartsWith("Validation.", ErrorCodes.Validation.RequiredField);
        Assert.StartsWith("Validation.", ErrorCodes.Validation.InvalidValue);
        Assert.StartsWith("Validation.", ErrorCodes.Validation.FileNotFound);
    }

    [Fact]
    public void OperationCodes_StartWithOperation()
    {
        Assert.StartsWith("Operation.", ErrorCodes.Operation.NotFound);
        Assert.StartsWith("Operation.", ErrorCodes.Operation.Cancelled);
        Assert.StartsWith("Operation.", ErrorCodes.Operation.Internal);
    }
}
