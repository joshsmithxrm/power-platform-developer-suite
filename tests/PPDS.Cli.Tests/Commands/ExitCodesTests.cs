using PPDS.Cli.Commands;
using Xunit;

namespace PPDS.Cli.Tests.Commands;

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
    public void AllCodesAreUnique()
    {
        var codes = new[]
        {
            ExitCodes.Success,
            ExitCodes.PartialSuccess,
            ExitCodes.Failure,
            ExitCodes.InvalidArguments
        };

        Assert.Equal(codes.Length, codes.Distinct().Count());
    }
}
