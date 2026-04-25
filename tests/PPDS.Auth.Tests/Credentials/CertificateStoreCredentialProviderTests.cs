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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidApplicationId_Throws(string? appId)
    {
        if (!IsWindows) return;

        var act = () => new CertificateStoreCredentialProvider(appId!, "AABBCCDDEE", "tenant-id");

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("ApplicationId"))
            .And.ParamName.Should().Be("applicationId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidThumbprint_Throws(string? thumbprint)
    {
        if (!IsWindows) return;

        var act = () => new CertificateStoreCredentialProvider("app-id", thumbprint!, "tenant-id");

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("Thumbprint"))
            .And.ParamName.Should().Be("thumbprint");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidTenantId_Throws(string? tenantId)
    {
        if (!IsWindows) return;

        var act = () => new CertificateStoreCredentialProvider("app-id", "AABBCCDDEE", tenantId!);

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("TenantId"))
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
