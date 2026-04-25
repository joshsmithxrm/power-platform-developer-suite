using System.Threading.Tasks;
using FluentAssertions;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Discovery;

/// <summary>
/// Verifies the routing logic in <see cref="EnvironmentResolutionService"/>:
/// non-interactive auth + name → BAP discovery (AC-26);
/// interactive auth + name → Global Discovery (AC-27);
/// URL identifier → direct connection, no discovery (AC-28).
/// </summary>
public class EnvironmentResolutionTests
{
    [Theory]
    [InlineData(AuthMethod.ClientSecret)]
    [InlineData(AuthMethod.CertificateFile)]
    [InlineData(AuthMethod.CertificateStore)]
    [InlineData(AuthMethod.ManagedIdentity)]
    [InlineData(AuthMethod.GitHubFederated)]
    [InlineData(AuthMethod.AzureDevOpsFederated)]
    [InlineData(AuthMethod.UsernamePassword)]
    public async Task NonInteractive_NameIdentifier_RoutesBapNotGds(AuthMethod authMethod)
    {
        var profile = new AuthProfile { AuthMethod = authMethod };
        using var service = new EnvironmentResolutionService(profile);

        var result = await service.ResolveAsync("MyEnvironment");

        result.Success.Should().BeFalse(because: "BAP discovery will fail without real credentials");
        result.ErrorMessage.Should().NotContain("Service principals require a full environment URL",
            because: "non-interactive auth should route to BAP discovery, not reject name-based resolution");
    }

    [Theory]
    [InlineData(AuthMethod.InteractiveBrowser)]
    [InlineData(AuthMethod.DeviceCode)]
    public void Interactive_SupportsGlobalDiscovery(AuthMethod authMethod)
    {
        GlobalDiscoveryService.SupportsGlobalDiscovery(authMethod).Should().BeTrue(
            because: $"{authMethod} is interactive and should use Global Discovery");
    }

    [Theory]
    [InlineData(AuthMethod.ClientSecret)]
    [InlineData(AuthMethod.CertificateFile)]
    [InlineData(AuthMethod.CertificateStore)]
    [InlineData(AuthMethod.ManagedIdentity)]
    [InlineData(AuthMethod.GitHubFederated)]
    [InlineData(AuthMethod.AzureDevOpsFederated)]
    public void NonInteractive_DoesNotSupportGlobalDiscovery(AuthMethod authMethod)
    {
        GlobalDiscoveryService.SupportsGlobalDiscovery(authMethod).Should().BeFalse(
            because: $"{authMethod} is non-interactive and should use BAP discovery");
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
        result.ErrorMessage.Should().Contain("Direct connection failed",
            because: "URL identifiers should attempt direct connection first");
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
