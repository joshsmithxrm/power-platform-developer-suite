using System;
using FluentAssertions;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

public class ClientSecretCredentialProviderTests
{
    [Fact]
    public void Constructor_WithValidInputs_SetsProperties()
    {
        using var provider = new ClientSecretCredentialProvider(
            "app-id",
            "secret-value",
            "tenant-id",
            CloudEnvironment.Public);

        provider.AuthMethod.Should().Be(AuthMethod.ClientSecret);
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
        var act = () => new ClientSecretCredentialProvider(appId!, "secret-value", "tenant-id");

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("ApplicationId"))
            .And.ParamName.Should().Be("applicationId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidClientSecret_Throws(string? secret)
    {
        var act = () => new ClientSecretCredentialProvider("app-id", secret!, "tenant-id");

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("ClientSecret"))
            .And.ParamName.Should().Be("clientSecret");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidTenantId_Throws(string? tenantId)
    {
        var act = () => new ClientSecretCredentialProvider("app-id", "secret-value", tenantId!);

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("TenantId"))
            .And.ParamName.Should().Be("tenantId");
    }

    [Fact]
    public void AuthMethod_ReturnsClientSecret()
    {
        using var provider = new ClientSecretCredentialProvider("a", "s", "t");
        provider.AuthMethod.Should().Be(AuthMethod.ClientSecret);
    }

    [Fact]
    public void FromProfile_ValidProfile_CreatesProvider()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id",
            TenantId = "tenant-id",
            Cloud = CloudEnvironment.UsGov
        };

        var credential = new StoredCredential
        {
            ApplicationId = "app-id",
            ClientSecret = "secret-value"
        };

        using var provider = ClientSecretCredentialProvider.FromProfile(profile, credential);

        provider.Should().NotBeNull();
        provider.AuthMethod.Should().Be(AuthMethod.ClientSecret);
        provider.Identity.Should().Be("app-id");
        provider.TenantId.Should().Be("tenant-id");
    }

    [Fact]
    public void FromProfile_WrongAuthMethod_Throws()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.InteractiveBrowser,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        var credential = new StoredCredential
        {
            ApplicationId = "app-id",
            ClientSecret = "secret-value"
        };

        var act = () => ClientSecretCredentialProvider.FromProfile(profile, credential);

        act.Should().Throw<ArgumentException>().WithMessage("*ClientSecret*");
    }

    [Fact]
    public void FromProfileWithSecret_ValidInputs_CreatesProvider()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id",
            TenantId = "tenant-id",
            Cloud = CloudEnvironment.Public
        };

        using var provider = ClientSecretCredentialProvider.FromProfileWithSecret(profile, "env-secret");

        provider.Should().NotBeNull();
        provider.AuthMethod.Should().Be(AuthMethod.ClientSecret);
        provider.Identity.Should().Be("app-id");
        provider.TenantId.Should().Be("tenant-id");
    }
}
