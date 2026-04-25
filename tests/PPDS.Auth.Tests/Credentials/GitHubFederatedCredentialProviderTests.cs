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

    [Fact]
    public void Constructor_NullApplicationId_Throws()
    {
        var act = () => new GitHubFederatedCredentialProvider(null!, "tenant-id");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("applicationId");
    }

    [Fact]
    public void Constructor_NullTenantId_Throws()
    {
        var act = () => new GitHubFederatedCredentialProvider("app-id", null!);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("tenantId");
    }

    [Fact]
    public void AuthMethod_ReturnsGitHubFederated()
    {
        using var provider = new GitHubFederatedCredentialProvider("app-id", "tenant-id");
        provider.AuthMethod.Should().Be(AuthMethod.GitHubFederated);
    }
}
