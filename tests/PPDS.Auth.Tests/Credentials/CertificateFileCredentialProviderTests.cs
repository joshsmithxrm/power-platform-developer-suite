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

    [Fact]
    public void Constructor_NullApplicationId_Throws()
    {
        var act = () => new CertificateFileCredentialProvider(null!, "/path/to/cert.pfx", null, "tenant-id");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("applicationId");
    }

    [Fact]
    public void Constructor_NullCertificatePath_Throws()
    {
        var act = () => new CertificateFileCredentialProvider("app-id", null!, null, "tenant-id");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("certificatePath");
    }

    [Fact]
    public void Constructor_NullTenantId_Throws()
    {
        var act = () => new CertificateFileCredentialProvider("app-id", "/path/to/cert.pfx", null, null!);

        act.Should().Throw<ArgumentNullException>()
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
