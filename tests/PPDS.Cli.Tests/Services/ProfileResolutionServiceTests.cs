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

    [Fact]
    public void Constructor_SkipsReservedDboLabel()
    {
        var configs = new List<EnvironmentConfig>
        {
            new() { Url = "https://dbo.crm.dynamics.com/", Label = "dbo" },
            new() { Url = "https://uat.crm.dynamics.com/", Label = "UAT" }
        };

        // Should not throw — "dbo" is silently skipped
        var service = new ProfileResolutionService(configs);
        service.ResolveByLabel("dbo").Should().BeNull();
        service.ResolveByLabel("UAT").Should().NotBeNull();
    }

    [Fact]
    public void Constructor_SkipsReservedDboLabel_CaseInsensitive()
    {
        var configs = new List<EnvironmentConfig>
        {
            new() { Url = "https://dbo.crm.dynamics.com/", Label = "DBO" }
        };

        // Should not throw — "DBO" is silently skipped (case-insensitive)
        var service = new ProfileResolutionService(configs);
        service.ResolveByLabel("DBO").Should().BeNull();
    }

    [Fact]
    public void Constructor_DuplicateLabels_KeepsFirstOccurrence()
    {
        var configs = new[]
        {
            new EnvironmentConfig { Label = "UAT", Url = "https://uat.crm.dynamics.com/" },
            new EnvironmentConfig { Label = "uat", Url = "https://uat2.crm.dynamics.com/" }
        };

        // Should not throw — first occurrence wins, duplicate is skipped
        var service = new ProfileResolutionService(configs);

        var resolved = service.ResolveByLabel("UAT");
        resolved.Should().NotBeNull();
        resolved!.Url.Should().Be("https://uat.crm.dynamics.com/");
    }
}
