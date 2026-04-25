using FluentAssertions;
using PPDS.Auth;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Discovery;

public class EnvironmentResolutionResultTests
{
    [Fact]
    public void Failed_WithoutErrorCode_LeavesErrorCodeNull()
    {
        var result = EnvironmentResolutionResult.Failed("oops");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("oops");
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Failed_PreservesErrorCode()
    {
        var result = EnvironmentResolutionResult.Failed("not found", AuthErrorCodes.EnvironmentNotFound);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(AuthErrorCodes.EnvironmentNotFound);
    }

    [Fact]
    public void Succeeded_HasNoErrorCode()
    {
        var info = new EnvironmentInfo { Url = "https://x", DisplayName = "X" };

        var result = EnvironmentResolutionResult.Succeeded(info, ResolutionMethod.DirectConnection);

        result.Success.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.Method.Should().Be(ResolutionMethod.DirectConnection);
    }
}
