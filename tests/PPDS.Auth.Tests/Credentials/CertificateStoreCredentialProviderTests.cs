using System;
using System.Runtime.InteropServices;
using FluentAssertions;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

public class CertificateStoreCredentialProviderTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void Constructor_WithValidInputs_SetsProperties()
    {
        if (!IsWindows) return;

        using var provider = new CertificateStoreCredentialProvider(
            "app-id", "AABBCCDDEE", "tenant-id");

        provider.AuthMethod.Should().Be(AuthMethod.CertificateStore);
        provider.Identity.Should().Be("app-id");
        provider.TenantId.Should().Be("tenant-id");
        provider.HomeAccountId.Should().BeNull();
        provider.AccessToken.Should().BeNull();
    }

    [Fact]
    public void Constructor_NullApplicationId_Throws()
    {
        if (!IsWindows) return;

        var act = () => new CertificateStoreCredentialProvider(null!, "AABBCCDDEE", "tenant-id");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("applicationId");
    }

    [Fact]
    public void Constructor_NullThumbprint_Throws()
    {
        if (!IsWindows) return;

        var act = () => new CertificateStoreCredentialProvider("app-id", null!, "tenant-id");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("thumbprint");
    }

    [Fact]
    public void Constructor_NullTenantId_Throws()
    {
        if (!IsWindows) return;

        var act = () => new CertificateStoreCredentialProvider("app-id", "AABBCCDDEE", null!);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("tenantId");
    }

    [Fact]
    public void AuthMethod_ReturnsCertificateStore()
    {
        if (!IsWindows) return;

        using var provider = new CertificateStoreCredentialProvider(
            "app-id", "AABBCCDDEE", "tenant-id");

        provider.AuthMethod.Should().Be(AuthMethod.CertificateStore);
    }

    [Fact]
    public void Constructor_OnNonWindows_ThrowsPlatformNotSupported()
    {
        if (IsWindows) return;

        var act = () => new CertificateStoreCredentialProvider(
            "app-id", "AABBCCDDEE", "tenant-id");

        act.Should().Throw<PlatformNotSupportedException>()
            .WithMessage("*Windows*");
    }
}
