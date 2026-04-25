using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PPDS.Auth;
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
    [InlineData(AuthMethod.InteractiveBrowser)]
    [InlineData(AuthMethod.DeviceCode)]
    public async Task Interactive_NameIdentifier_RoutesGlobalDiscovery(AuthMethod authMethod)
    {
        // Verifies the routing path actually executes for AC-27: an interactive auth method
        // with a name (not URL) identifier should attempt Global Discovery, not BAP.
        var profile = new AuthProfile { AuthMethod = authMethod };
        using var service = new EnvironmentResolutionService(profile);

        var result = await service.ResolveAsync("MyEnvironment");

        result.Success.Should().BeFalse(because: "GDS will fail without real credentials");
        result.ErrorMessage.Should().Contain("Global Discovery",
            because: $"{authMethod} should route to GDS for name-based resolution");
        result.ErrorMessage.Should().NotContain("BAP",
            because: "interactive auth must not fall through to BAP discovery");
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

    [Fact]
    public void EnvironmentNotFoundMessage_ListsAvailableNames()
    {
        // AC-34: not-found branch lists the available environment names.
        var environments = new[]
        {
            new DiscoveredEnvironment { FriendlyName = "QA Dev" },
            new DiscoveredEnvironment { FriendlyName = "QA Test" },
            new DiscoveredEnvironment { FriendlyName = "Prod" },
        };

        var message = EnvironmentResolutionService.BuildEnvironmentNotFoundMessage(
            "Staging", environments);

        message.Should().Contain("'Staging'");
        message.Should().Contain("not found");
        message.Should().Contain("QA Dev");
        message.Should().Contain("QA Test");
        message.Should().Contain("Prod");
    }

    [Fact]
    public void EnvironmentNotFoundMessage_HandlesEmptyList()
    {
        var message = EnvironmentResolutionService.BuildEnvironmentNotFoundMessage(
            "Staging", Array.Empty<DiscoveredEnvironment>());

        message.Should().Contain("'Staging'");
        message.Should().Contain("not found");
        message.Should().Contain("No environments");
    }

    [Fact]
    public void EnvironmentNotFoundMessage_TruncatesLongLists()
    {
        var environments = Enumerable.Range(0, 25)
            .Select(i => new DiscoveredEnvironment { FriendlyName = $"Env-{i}" })
            .ToArray();

        var message = EnvironmentResolutionService.BuildEnvironmentNotFoundMessage(
            "Staging", environments);

        message.Should().Contain("Env-0");
        message.Should().Contain("Env-9");
        message.Should().Contain("(+15 more)");
    }
}
