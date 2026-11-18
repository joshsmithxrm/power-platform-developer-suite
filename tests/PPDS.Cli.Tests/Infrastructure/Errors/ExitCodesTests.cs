using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Errors;

public class ExitCodesTests
{
    // Every public int constant on ExitCodes, so a future code cannot be missed by the guards below.
    private static int[] AllCodes() => typeof(ExitCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(int))
        .Select(f => (int)f.GetRawConstantValue()!)
        .ToArray();

    [Fact]
    public void AllCodes_CoversEveryConstant()
    {
        Assert.Equal(13, AllCodes().Length); // 0-12; update when a code is added
    }

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
    public void Incomplete_IsTwelve_AndDistinctFromFailure()
    {
        Assert.Equal(12, ExitCodes.Incomplete);
        Assert.NotEqual(ExitCodes.Failure, ExitCodes.Incomplete);
    }

    [Fact]
    public void AllCodesAreUnique()
    {
        var codes = AllCodes();

        Assert.Equal(codes.Length, codes.Distinct().Count());
    }

    [Fact]
    public void AllCodesAreNonNegative()
    {
        var codes = AllCodes();

        Assert.All(codes, code => Assert.True(code >= 0));
    }

    [Fact]
    public void AllCodesAreWithinValidRange()
    {
        // Exit codes should be 0-255 for cross-platform compatibility
        var codes = AllCodes();

        Assert.All(codes, code => Assert.InRange(code, 0, 255));
    }
}
