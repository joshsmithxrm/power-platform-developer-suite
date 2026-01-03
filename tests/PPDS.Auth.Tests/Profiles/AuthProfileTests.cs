using FluentAssertions;
using PPDS.Auth.Cloud;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

public class AuthProfileTests
{
    [Fact]
    public void Cloud_DefaultsToPublic()
    {
        var profile = new AuthProfile();

        profile.Cloud.Should().Be(CloudEnvironment.Public);
    }

    [Fact]
    public void CreatedAt_DefaultsToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var profile = new AuthProfile();
        var after = DateTimeOffset.UtcNow;

        profile.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void HasEnvironment_WithEnvironment_ReturnsTrue()
    {
        var profile = new AuthProfile
        {
            Environment = EnvironmentInfo.Create("https://test.crm.dynamics.com", "Test")
        };

        profile.HasEnvironment.Should().BeTrue();
    }

    [Fact]
    public void HasEnvironment_NoEnvironment_ReturnsFalse()
    {
        var profile = new AuthProfile();

        profile.HasEnvironment.Should().BeFalse();
    }

    [Fact]
    public void HasName_WithName_ReturnsTrue()
    {
        var profile = new AuthProfile { Name = "test" };

        profile.HasName.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void HasName_NullOrWhitespace_ReturnsFalse(string? name)
    {
        var profile = new AuthProfile { Name = name };

        profile.HasName.Should().BeFalse();
    }

    [Fact]
    public void DisplayIdentifier_WithName_ReturnsName()
    {
        var profile = new AuthProfile { Name = "MyProfile", Index = 5 };

        profile.DisplayIdentifier.Should().Be("MyProfile");
    }

    [Fact]
    public void DisplayIdentifier_NoName_ReturnsIndexInBrackets()
    {
        var profile = new AuthProfile { Index = 5 };

        profile.DisplayIdentifier.Should().Be("[5]");
    }

    [Fact]
    public void IdentityDisplay_WithUsername_ReturnsUsername()
    {
        var profile = new AuthProfile
        {
            Username = "user@example.com",
            ApplicationId = "app-id"
        };

        profile.IdentityDisplay.Should().Be("user@example.com");
    }

    [Fact]
    public void IdentityDisplay_NoUsernameButHasAppId_ReturnsAppId()
    {
        var profile = new AuthProfile
        {
            ApplicationId = "app-id-123"
        };

        profile.IdentityDisplay.Should().Be("app-id-123");
    }

    [Fact]
    public void IdentityDisplay_NoIdentity_ReturnsUnknown()
    {
        var profile = new AuthProfile();

        profile.IdentityDisplay.Should().Be("(unknown)");
    }

    [Fact]
    public void ToString_WithoutEnvironment_ExcludesEnvPart()
    {
        var profile = new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.ClientSecret,
            Cloud = CloudEnvironment.Public
        };

        var result = profile.ToString();

        result.Should().Contain("test");
        result.Should().Contain("ClientSecret");
        result.Should().Contain("Public");
        result.Should().NotContain("Env:");
    }

    [Fact]
    public void ToString_WithEnvironment_IncludesEnvPart()
    {
        var profile = new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.ClientSecret,
            Cloud = CloudEnvironment.Public,
            Environment = EnvironmentInfo.Create("url", "Dev Environment")
        };

        var result = profile.ToString();

        result.Should().Contain("Env: Dev Environment");
    }

    [Fact]
    public void Validate_InteractiveBrowser_DoesNotThrow()
    {
        var profile = new AuthProfile { AuthMethod = AuthMethod.InteractiveBrowser };

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_DeviceCode_DoesNotThrow()
    {
        var profile = new AuthProfile { AuthMethod = AuthMethod.DeviceCode };

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ClientSecret_WithAllFields_DoesNotThrow()
    {
        // ClientSecret is now in secure store, profile only needs ApplicationId and TenantId
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ClientSecret_MissingApplicationId_Throws()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.ClientSecret,
            TenantId = "tenant-id"
        };

        var act = () => profile.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ApplicationId*");
    }

    [Fact]
    public void Validate_ClientSecret_MissingTenantId_Throws()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id"
        };

        var act = () => profile.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TenantId*");
    }

    [Fact]
    public void Validate_CertificateFile_WithAllFields_DoesNotThrow()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.CertificateFile,
            ApplicationId = "app-id",
            CertificatePath = "/path/to/cert.pfx",
            TenantId = "tenant-id"
        };

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_CertificateFile_MissingCertificatePath_Throws()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.CertificateFile,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        var act = () => profile.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CertificatePath*");
    }

    [Fact]
    public void Validate_CertificateStore_WithAllFields_DoesNotThrow()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.CertificateStore,
            ApplicationId = "app-id",
            CertificateThumbprint = "ABC123",
            TenantId = "tenant-id"
        };

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_CertificateStore_MissingCertificateThumbprint_Throws()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.CertificateStore,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        var act = () => profile.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CertificateThumbprint*");
    }

    [Fact]
    public void Validate_ManagedIdentity_DoesNotThrow()
    {
        var profile = new AuthProfile { AuthMethod = AuthMethod.ManagedIdentity };

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_GitHubFederated_WithAllFields_DoesNotThrow()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.GitHubFederated,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_GitHubFederated_MissingApplicationId_Throws()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.GitHubFederated,
            TenantId = "tenant-id"
        };

        var act = () => profile.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ApplicationId*");
    }

    [Fact]
    public void Validate_AzureDevOpsFederated_WithAllFields_DoesNotThrow()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.AzureDevOpsFederated,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_UsernamePassword_WithAllFields_DoesNotThrow()
    {
        // Password is now in secure store, profile only needs Username
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.UsernamePassword,
            Username = "user@example.com"
        };

        var act = () => profile.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_UsernamePassword_MissingUsername_Throws()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.UsernamePassword
        };

        var act = () => profile.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Username*");
    }

    [Fact]
    public void Clone_CreatesDeepCopy()
    {
        var profile = new AuthProfile
        {
            Index = 5,
            Name = "test",
            AuthMethod = AuthMethod.ClientSecret,
            Cloud = CloudEnvironment.UsGov,
            TenantId = "tenant",
            Username = "user",
            ObjectId = "obj-id",
            ApplicationId = "app-id",
            Authority = "https://login.microsoftonline.com/tenant",
            Environment = EnvironmentInfo.Create("https://test.crm.dynamics.com", "Test"),
            CreatedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
            LastUsedAt = DateTimeOffset.Parse("2024-01-02T00:00:00Z"),
            TokenExpiresOn = DateTimeOffset.Parse("2024-01-03T00:00:00Z"),
            Puid = "puid",
            HomeAccountId = "home-id"
        };

        var clone = profile.Clone();

        clone.Should().NotBeSameAs(profile);
        clone.Index.Should().Be(5);
        clone.Name.Should().Be("test");
        clone.AuthMethod.Should().Be(AuthMethod.ClientSecret);
        clone.Cloud.Should().Be(CloudEnvironment.UsGov);
        clone.TenantId.Should().Be("tenant");
        clone.Username.Should().Be("user");
        clone.ObjectId.Should().Be("obj-id");
        clone.ApplicationId.Should().Be("app-id");
        clone.Authority.Should().Be("https://login.microsoftonline.com/tenant");
        clone.Environment.Should().NotBeSameAs(profile.Environment);
        clone.Environment!.Url.Should().Be("https://test.crm.dynamics.com");
        clone.CreatedAt.Should().Be(profile.CreatedAt);
        clone.LastUsedAt.Should().Be(profile.LastUsedAt);
        clone.TokenExpiresOn.Should().Be(profile.TokenExpiresOn);
        clone.Puid.Should().Be("puid");
        clone.HomeAccountId.Should().Be("home-id");
    }

    [Fact]
    public void Clone_ModifyingClone_DoesNotAffectOriginal()
    {
        var profile = new AuthProfile { Name = "original" };
        var clone = profile.Clone();

        clone.Name = "modified";

        profile.Name.Should().Be("original");
    }
}
