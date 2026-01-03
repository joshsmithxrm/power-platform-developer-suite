using FluentAssertions;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

public class EnvironmentInfoTests
{
    [Fact]
    public void Create_WithValidArguments_CreatesInstance()
    {
        var info = EnvironmentInfo.Create("https://test.crm.dynamics.com", "Test Env");

        info.Url.Should().Be("https://test.crm.dynamics.com");
        info.DisplayName.Should().Be("Test Env");
    }

    [Fact]
    public void Create_NullUrl_Throws()
    {
        var act = () => EnvironmentInfo.Create(null!, "name");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_NullDisplayName_Throws()
    {
        var act = () => EnvironmentInfo.Create("url", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToString_ReturnsDisplayNameAndUrl()
    {
        var info = EnvironmentInfo.Create("https://test.crm.dynamics.com", "Test Env");

        var result = info.ToString();

        result.Should().Contain("Test Env");
        result.Should().Contain("https://test.crm.dynamics.com");
    }

    [Fact]
    public void Clone_CreatesDeepCopy()
    {
        var info = new EnvironmentInfo
        {
            Url = "https://test.crm.dynamics.com",
            DisplayName = "Test Env",
            UniqueName = "unique",
            OrganizationId = "org-id",
            EnvironmentId = "env-guid",
            Type = "Sandbox",
            Region = "NA"
        };

        var clone = info.Clone();

        clone.Should().NotBeSameAs(info);
        clone.Url.Should().Be("https://test.crm.dynamics.com");
        clone.DisplayName.Should().Be("Test Env");
        clone.UniqueName.Should().Be("unique");
        clone.OrganizationId.Should().Be("org-id");
        clone.EnvironmentId.Should().Be("env-guid");
        clone.Type.Should().Be("Sandbox");
        clone.Region.Should().Be("NA");
    }

    [Fact]
    public void Clone_ModifyingClone_DoesNotAffectOriginal()
    {
        var info = EnvironmentInfo.Create("url", "original");
        var clone = info.Clone();

        clone.DisplayName = "modified";

        info.DisplayName.Should().Be("original");
    }

    [Fact]
    public void DefaultConstructor_InitializesEmptyStrings()
    {
        var info = new EnvironmentInfo();

        info.Url.Should().BeEmpty();
        info.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public void OptionalProperties_DefaultToNull()
    {
        var info = new EnvironmentInfo();

        info.UniqueName.Should().BeNull();
        info.OrganizationId.Should().BeNull();
        info.EnvironmentId.Should().BeNull();
        info.Type.Should().BeNull();
        info.Region.Should().BeNull();
    }
}
