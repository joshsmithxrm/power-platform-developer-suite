using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

public class CertificateFileCredentialProviderTests
{
    [Fact]
    public void Constructor_WithValidInputs_SetsProperties()
    {
        using var provider = new CertificateFileCredentialProvider(
            "app-id",
            "/path/to/cert.pfx",
            "cert-password",
            "tenant-id",
            CloudEnvironment.Public);

        provider.AuthMethod.Should().Be(AuthMethod.CertificateFile);
        provider.Identity.Should().Be("app-id");
        provider.TenantId.Should().Be("tenant-id");
        provider.HomeAccountId.Should().BeNull();
        provider.AccessToken.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithNullPassword_IsAllowed()
    {
        using var provider = new CertificateFileCredentialProvider(
            "app-id",
            "/path/to/cert.pfx",
            null,
            "tenant-id");

        provider.AuthMethod.Should().Be(AuthMethod.CertificateFile);
        provider.Identity.Should().Be("app-id");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidApplicationId_Throws(string? appId)
    {
        var act = () => new CertificateFileCredentialProvider(appId!, "/path/to/cert.pfx", null, "tenant-id");

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("ApplicationId"))
            .And.ParamName.Should().Be("applicationId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidCertificatePath_Throws(string? certPath)
    {
        var act = () => new CertificateFileCredentialProvider("app-id", certPath!, null, "tenant-id");

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("CertificatePath"))
            .And.ParamName.Should().Be("certificatePath");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithInvalidTenantId_Throws(string? tenantId)
    {
        var act = () => new CertificateFileCredentialProvider("app-id", "/path/to/cert.pfx", null, tenantId!);

        act.Should().Throw<ArgumentException>()
            .Where(ex => ex.Message.Contains("TenantId"))
            .And.ParamName.Should().Be("tenantId");
    }

    [Fact]
    public void AuthMethod_ReturnsCertificateFile()
    {
        using var provider = new CertificateFileCredentialProvider(
            "app-id", "/path/to/cert.pfx", null, "tenant-id");

        provider.AuthMethod.Should().Be(AuthMethod.CertificateFile);
    }

    [Fact]
    public async Task CreateServiceClientAsync_WithMissingCertificateFile_ThrowsAuthenticationException()
    {
        var nonExistentPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ppds-nonexistent-cert-{Guid.NewGuid():N}.pfx");

        using var provider = new CertificateFileCredentialProvider(
            "app-id", nonExistentPath, null, "tenant-id");

        var act = async () => await provider.CreateServiceClientAsync(
            "https://example.crm.dynamics.com", CancellationToken.None);

        await act.Should().ThrowAsync<AuthenticationException>()
            .WithMessage("*Certificate file not found*");
    }
}
