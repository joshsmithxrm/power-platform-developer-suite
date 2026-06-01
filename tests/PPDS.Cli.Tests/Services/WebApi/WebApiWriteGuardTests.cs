using PPDS.Auth.Profiles;
using PPDS.Cli.Services.WebApi;
using Xunit;

namespace PPDS.Cli.Tests.Services.WebApi;

public class WebApiWriteGuardTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void IsMutating_MutatingMethods_ReturnsTrue(string methodName)
    {
        var method = new HttpMethod(methodName);
        Assert.True(WebApiWriteGuard.IsMutating(method));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void IsMutating_SafeMethods_ReturnsFalse(string methodName)
    {
        var method = new HttpMethod(methodName);
        Assert.False(WebApiWriteGuard.IsMutating(method));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void IsBlocked_MutatingOnProduction_NoConfirm_ReturnsTrue(string methodName)
    {
        var method = new HttpMethod(methodName);
        Assert.True(WebApiWriteGuard.IsBlocked(method, ProtectionLevel.Production, isConfirmed: false));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void IsBlocked_MutatingOnProduction_WithConfirm_ReturnsFalse(string methodName)
    {
        var method = new HttpMethod(methodName);
        Assert.False(WebApiWriteGuard.IsBlocked(method, ProtectionLevel.Production, isConfirmed: true));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void IsBlocked_MutatingOnDevelopment_ReturnsFalse(string methodName)
    {
        var method = new HttpMethod(methodName);
        Assert.False(WebApiWriteGuard.IsBlocked(method, ProtectionLevel.Development, isConfirmed: false));
    }

    [Fact]
    public void IsBlocked_GetOnProduction_ReturnsFalse()
    {
        Assert.False(WebApiWriteGuard.IsBlocked(HttpMethod.Get, ProtectionLevel.Production, isConfirmed: false));
    }
}
