using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Errors;

public class ExitCodesTests
{
    [Fact]
    public void Success_IsZero()
    {
        Assert.Equal(0, ExitCodes.Success);
    }

    [Fact]
    public void PartialSuccess_IsOne()
    {
        Assert.Equal(1, ExitCodes.PartialSuccess);
    }

    [Fact]
    public void Failure_IsTwo()
    {
        Assert.Equal(2, ExitCodes.Failure);
    }

    [Fact]
    public void InvalidArguments_IsThree()
    {
        Assert.Equal(3, ExitCodes.InvalidArguments);
    }

    [Fact]
    public void ConnectionError_IsFour()
    {
        Assert.Equal(4, ExitCodes.ConnectionError);
    }

    [Fact]
    public void AuthError_IsFive()
    {
        Assert.Equal(5, ExitCodes.AuthError);
    }

    [Fact]
    public void NotFoundError_IsSix()
    {
        Assert.Equal(6, ExitCodes.NotFoundError);
    }

    [Fact]
    public void AllCodesAreUnique()
    {
        var codes = new[]
        {
            ExitCodes.Success,
            ExitCodes.PartialSuccess,
            ExitCodes.Failure,
            ExitCodes.InvalidArguments,
            ExitCodes.ConnectionError,
            ExitCodes.AuthError,
            ExitCodes.NotFoundError
        };

        Assert.Equal(codes.Length, codes.Distinct().Count());
    }

    [Fact]
    public void AllCodesAreNonNegative()
    {
        var codes = new[]
        {
            ExitCodes.Success,
            ExitCodes.PartialSuccess,
            ExitCodes.Failure,
            ExitCodes.InvalidArguments,
            ExitCodes.ConnectionError,
            ExitCodes.AuthError,
            ExitCodes.NotFoundError
        };

        Assert.All(codes, code => Assert.True(code >= 0));
    }

    [Fact]
    public void AllCodesAreWithinValidRange()
    {
        // Exit codes should be 0-255 for cross-platform compatibility
        var codes = new[]
        {
            ExitCodes.Success,
            ExitCodes.PartialSuccess,
            ExitCodes.Failure,
            ExitCodes.InvalidArguments,
            ExitCodes.ConnectionError,
            ExitCodes.AuthError,
            ExitCodes.NotFoundError
        };

        Assert.All(codes, code => Assert.InRange(code, 0, 255));
    }
}
