using FluentAssertions;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services;
using Xunit;

namespace PPDS.Cli.Tests.Services;

[Trait("Category", "Unit")]
public class ProfileResolutionServiceTests
{
    [Fact]
    public void Resolve_ExistingLabel_ReturnsConfig()
    {
        var configs = new List<EnvironmentConfig>
        {
            new() { Url = "https://uat.crm.dynamics.com/", Label = "UAT", Type = EnvironmentType.Sandbox },
            new() { Url = "https://prod.crm.dynamics.com/", Label = "PROD", Type = EnvironmentType.Production }
        };

        var service = new ProfileResolutionService(configs);
        var result = service.ResolveByLabel("UAT");

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://uat.crm.dynamics.com/");
    }

    [Fact]
    public void Resolve_CaseInsensitive()
    {
        var configs = new List<EnvironmentConfig>
        {
            new() { Url = "https://uat.crm.dynamics.com/", Label = "UAT" }
        };

        var service = new ProfileResolutionService(configs);
        service.ResolveByLabel("uat").Should().NotBeNull();
        service.ResolveByLabel("Uat").Should().NotBeNull();
    }

    [Fact]
    public void Resolve_NotFound_ReturnsNull()
    {
        var configs = new List<EnvironmentConfig>
        {
            new() { Url = "https://uat.crm.dynamics.com/", Label = "UAT" }
        };

        var service = new ProfileResolutionService(configs);
        service.ResolveByLabel("STAGING").Should().BeNull();
    }
}
