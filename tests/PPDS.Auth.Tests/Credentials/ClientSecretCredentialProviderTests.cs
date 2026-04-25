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

    [Fact]
    public void Constructor_NullApplicationId_Throws()
    {
        var act = () => new ClientSecretCredentialProvider(null!, "secret-value", "tenant-id");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("applicationId");
    }

    [Fact]
    public void Constructor_NullClientSecret_Throws()
    {
        var act = () => new ClientSecretCredentialProvider("app-id", null!, "tenant-id");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("clientSecret");
    }

    [Fact]
    public void Constructor_NullTenantId_Throws()
    {
        var act = () => new ClientSecretCredentialProvider("app-id", "secret-value", null!);

        act.Should().Throw<ArgumentNullException>()
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
