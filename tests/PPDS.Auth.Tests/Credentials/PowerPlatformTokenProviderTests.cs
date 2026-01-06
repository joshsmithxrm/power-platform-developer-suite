using System;
using FluentAssertions;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

public class PowerPlatformTokenProviderTests
{
    [Fact]
    public void Constructor_UserDelegated_SetsProperties()
    {
        using var provider = new PowerPlatformTokenProvider(
            CloudEnvironment.Public,
            "tenant-id",
            "user@example.com",
            "home-account-id");

        // Provider should be created without throwing
        provider.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_ClientCredentials_RequiresApplicationId()
    {
        var act = () => new PowerPlatformTokenProvider(null!, "secret", "tenant-id");

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("applicationId");
    }

    [Fact]
    public void Constructor_ClientCredentials_RequiresClientSecret()
    {
        var act = () => new PowerPlatformTokenProvider("app-id", null!, "tenant-id");

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("clientSecret");
    }

    [Fact]
    public void Constructor_ClientCredentials_RequiresTenantId()
    {
        var act = () => new PowerPlatformTokenProvider("app-id", "secret", null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("tenantId");
    }

    [Theory]
    [InlineData(AuthMethod.InteractiveBrowser)]
    [InlineData(AuthMethod.DeviceCode)]
    public void FromProfile_UserDelegated_CreatesProvider(AuthMethod authMethod)
    {
        var profile = new AuthProfile
        {
            Name = "test",
            AuthMethod = authMethod,
            Cloud = CloudEnvironment.Public,
            TenantId = "tenant-id",
            Username = "user@example.com",
            HomeAccountId = "home-account-id"
        };

        using var provider = PowerPlatformTokenProvider.FromProfile(profile);

        provider.Should().NotBeNull();
    }

    [Fact]
    public void FromProfile_ClientSecret_ThrowsWithMessage()
    {
        var profile = new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        var act = () => PowerPlatformTokenProvider.FromProfile(profile);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Cannot create user-delegated*FromProfileWithSecret*");
    }

    [Fact]
    public void FromProfile_UnsupportedAuthMethod_Throws()
    {
        var profile = new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.UsernamePassword // Not supported for Power Platform tokens
        };

        var act = () => PowerPlatformTokenProvider.FromProfile(profile);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*not supported for Power Platform API tokens*");
    }

    [Fact]
    public void FromProfileWithSecret_ValidProfile_CreatesProvider()
    {
        var profile = new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id",
            TenantId = "tenant-id",
            Cloud = CloudEnvironment.Public
        };

        using var provider = PowerPlatformTokenProvider.FromProfileWithSecret(profile, "client-secret");

        provider.Should().NotBeNull();
    }

    [Fact]
    public void FromProfileWithSecret_WrongAuthMethod_Throws()
    {
        var profile = new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.InteractiveBrowser
        };

        var act = () => PowerPlatformTokenProvider.FromProfileWithSecret(profile, "secret");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*must be ClientSecret*");
    }

    [Fact]
    public void FromProfileWithSecret_MissingApplicationId_Throws()
    {
        var profile = new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.ClientSecret,
            TenantId = "tenant-id"
        };

        var act = () => PowerPlatformTokenProvider.FromProfileWithSecret(profile, "secret");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*ApplicationId is required*");
    }

    [Fact]
    public void FromProfileWithSecret_MissingTenantId_Throws()
    {
        var profile = new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id"
        };

        var act = () => PowerPlatformTokenProvider.FromProfileWithSecret(profile, "secret");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*TenantId is required*");
    }

    [Fact]
    public void FromProfileWithSecret_NullSecret_Throws()
    {
        var profile = new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        var act = () => PowerPlatformTokenProvider.FromProfileWithSecret(profile, null!);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("clientSecret");
    }

    [Fact]
    public void GetTokenForResourceAsync_NullResource_Throws()
    {
        using var provider = new PowerPlatformTokenProvider();

        var act = async () => await provider.GetTokenForResourceAsync(null!);

        act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("resource");
    }

    [Fact]
    public void GetTokenForResourceAsync_EmptyResource_Throws()
    {
        using var provider = new PowerPlatformTokenProvider();

        var act = async () => await provider.GetTokenForResourceAsync("");

        act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("resource");
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var provider = new PowerPlatformTokenProvider();

        provider.Dispose();
        provider.Dispose(); // Should not throw

        // No assertion needed - just verifying no exception
    }
}

public class PowerPlatformTokenTests
{
    [Fact]
    public void IsExpired_FutureExpiration_ReturnsFalse()
    {
        var token = new PowerPlatformToken
        {
            AccessToken = "test-token",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            Resource = "https://api.powerapps.com"
        };

        token.IsExpired().Should().BeFalse();
    }

    [Fact]
    public void IsExpired_PastExpiration_ReturnsTrue()
    {
        var token = new PowerPlatformToken
        {
            AccessToken = "test-token",
            ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(-1),
            Resource = "https://api.powerapps.com"
        };

        token.IsExpired().Should().BeTrue();
    }

    [Fact]
    public void IsExpired_WithinBuffer_ReturnsTrue()
    {
        var token = new PowerPlatformToken
        {
            AccessToken = "test-token",
            ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(3), // Within 5-minute buffer
            Resource = "https://api.powerapps.com"
        };

        token.IsExpired(bufferMinutes: 5).Should().BeTrue();
    }

    [Fact]
    public void IsExpired_CustomBuffer_UsesBuffer()
    {
        var token = new PowerPlatformToken
        {
            AccessToken = "test-token",
            ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(3),
            Resource = "https://api.powerapps.com"
        };

        // With 2-minute buffer, 3-minute expiration should not be expired
        token.IsExpired(bufferMinutes: 2).Should().BeFalse();
    }

    [Fact]
    public void Properties_AreSet()
    {
        var expiresOn = DateTimeOffset.UtcNow.AddHours(1);
        var token = new PowerPlatformToken
        {
            AccessToken = "test-token",
            ExpiresOn = expiresOn,
            Resource = "https://api.powerapps.com",
            Identity = "user@example.com"
        };

        token.AccessToken.Should().Be("test-token");
        token.ExpiresOn.Should().Be(expiresOn);
        token.Resource.Should().Be("https://api.powerapps.com");
        token.Identity.Should().Be("user@example.com");
    }

    [Fact]
    public void Identity_CanBeNull()
    {
        var token = new PowerPlatformToken
        {
            AccessToken = "test-token",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            Resource = "https://api.powerapps.com"
        };

        token.Identity.Should().BeNull();
    }
}
