using System.Threading.Tasks;
using FluentAssertions;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Discovery;

public class EnvironmentResolutionTests
{
    [Theory]
    [InlineData(AuthMethod.ClientSecret)]
    [InlineData(AuthMethod.CertificateFile)]
    [InlineData(AuthMethod.CertificateStore)]
    public async Task SpnAuth_NameIdentifier_RoutesBapDiscovery(AuthMethod authMethod)
    {
        var profile = new AuthProfile { AuthMethod = authMethod };
        using var service = new EnvironmentResolutionService(profile);

        var result = await service.ResolveAsync("MyEnvironment");

        result.Success.Should().BeFalse(because: "BAP discovery will fail without real credentials");
        result.ErrorMessage.Should().Contain("BAP",
            because: "SPN auth should route to BAP discovery for name-based resolution");
    }

    [Theory]
    [InlineData(AuthMethod.ManagedIdentity)]
    [InlineData(AuthMethod.GitHubFederated)]
    [InlineData(AuthMethod.AzureDevOpsFederated)]
    [InlineData(AuthMethod.UsernamePassword)]
    public async Task UnsupportedAuth_NameIdentifier_ReturnsHelpfulError(AuthMethod authMethod)
    {
        var profile = new AuthProfile { AuthMethod = authMethod };
        using var service = new EnvironmentResolutionService(profile);

        var result = await service.ResolveAsync("MyEnvironment");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not support name-based environment resolution",
            because: $"{authMethod} cannot use either GDS or BAP for discovery");
    }

    [Theory]
    [InlineData(AuthMethod.ClientSecret)]
    [InlineData(AuthMethod.CertificateFile)]
    [InlineData(AuthMethod.CertificateStore)]
    public void BapDiscovery_SupportsSpnAuthMethods(AuthMethod authMethod)
    {
        BapEnvironmentService.SupportsAuthMethod(authMethod).Should().BeTrue();
    }

    [Theory]
    [InlineData(AuthMethod.InteractiveBrowser)]
    [InlineData(AuthMethod.DeviceCode)]
    [InlineData(AuthMethod.ManagedIdentity)]
    [InlineData(AuthMethod.GitHubFederated)]
    [InlineData(AuthMethod.AzureDevOpsFederated)]
    public void BapDiscovery_DoesNotSupportOtherAuthMethods(AuthMethod authMethod)
    {
        BapEnvironmentService.SupportsAuthMethod(authMethod).Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthMethod.InteractiveBrowser)]
    [InlineData(AuthMethod.DeviceCode)]
    public void Interactive_SupportsGlobalDiscovery(AuthMethod authMethod)
    {
        GlobalDiscoveryService.SupportsGlobalDiscovery(authMethod).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://org.crm.dynamics.com")]
    [InlineData("https://myorg.crm4.dynamics.com")]
    public async Task UrlIdentifier_AttemptsDirectConnection(string url)
    {
        var profile = new AuthProfile { AuthMethod = AuthMethod.ClientSecret };
        using var service = new EnvironmentResolutionService(profile);

        var result = await service.ResolveAsync(url);

        result.Success.Should().BeFalse(because: "no real credentials provided");
        result.ErrorMessage.Should().Contain("Direct connection failed");
    }

    [Fact]
    public void BapDiscovery_EnumValue_Exists()
    {
        ResolutionMethod.BapDiscovery.Should().BeDefined();
        ((int)ResolutionMethod.BapDiscovery).Should().Be(2);
    }

    [Fact]
    public async Task EmptyIdentifier_ReturnsError()
    {
        var profile = new AuthProfile { AuthMethod = AuthMethod.ClientSecret };
        using var service = new EnvironmentResolutionService(profile);

        var result = await service.ResolveAsync("");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("required");
    }
}
