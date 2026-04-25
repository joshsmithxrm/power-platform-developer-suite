using System;
using FluentAssertions;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

public class GitHubFederatedCredentialProviderTests
{
    [Fact]
    public void Constructor_WithValidInputs_SetsProperties()
    {
        using var provider = new GitHubFederatedCredentialProvider(
            "app-id", "tenant-id", CloudEnvironment.Public);

        provider.AuthMethod.Should().Be(AuthMethod.GitHubFederated);
        provider.Identity.Should().Be("app-id");
        provider.TenantId.Should().Be("tenant-id");
        provider.HomeAccountId.Should().BeNull();
        provider.AccessToken.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidApplicationId_Throws(string? appId)
    {
        var act = () => new GitHubFederatedCredentialProvider(appId!, "tenant-id");

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("ApplicationId"))
            .And.ParamName.Should().Be("applicationId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidTenantId_Throws(string? tenantId)
    {
        var act = () => new GitHubFederatedCredentialProvider("app-id", tenantId!);

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("TenantId"))
            .And.ParamName.Should().Be("tenantId");
    }

    [Fact]
    public void AuthMethod_ReturnsGitHubFederated()
    {
        using var provider = new GitHubFederatedCredentialProvider("app-id", "tenant-id");
        provider.AuthMethod.Should().Be(AuthMethod.GitHubFederated);
    }
}
