using FluentAssertions;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Auth.Tests.Infrastructure;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

public class CertificateStoreCredentialProviderTests
{
    [WindowsFact]
    public void Constructor_WithValidInputs_SetsProperties()
    {
        using var provider = new CertificateStoreCredentialProvider(
            "app-id", "AABBCCDDEE", "tenant-id");

        provider.AuthMethod.Should().Be(AuthMethod.CertificateStore);
        provider.Identity.Should().Be("app-id");
        provider.TenantId.Should().Be("tenant-id");
        provider.HomeAccountId.Should().BeNull();
        provider.AccessToken.Should().BeNull();
    }

    [WindowsTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidApplicationId_Throws(string? appId)
    {
        var act = () => new CertificateStoreCredentialProvider(appId!, "AABBCCDDEE", "tenant-id");

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("ApplicationId"))
            .And.ParamName.Should().Be("applicationId");
    }

    [WindowsTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidThumbprint_Throws(string? thumbprint)
    {
        var act = () => new CertificateStoreCredentialProvider("app-id", thumbprint!, "tenant-id");

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("Thumbprint"))
            .And.ParamName.Should().Be("thumbprint");
    }

    [WindowsTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidTenantId_Throws(string? tenantId)
    {
        var act = () => new CertificateStoreCredentialProvider("app-id", "AABBCCDDEE", tenantId!);

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("TenantId"))
            .And.ParamName.Should().Be("tenantId");
    }

    [WindowsFact]
    public void AuthMethod_ReturnsCertificateStore()
    {
        using var provider = new CertificateStoreCredentialProvider(
            "app-id", "AABBCCDDEE", "tenant-id");

        provider.AuthMethod.Should().Be(AuthMethod.CertificateStore);
    }

    [NonWindowsFact]
    public void Constructor_OnNonWindows_ThrowsPlatformNotSupported()
    {
        var act = () => new CertificateStoreCredentialProvider(
            "app-id", "AABBCCDDEE", "tenant-id");

        act.Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*Windows*");
    }
}
