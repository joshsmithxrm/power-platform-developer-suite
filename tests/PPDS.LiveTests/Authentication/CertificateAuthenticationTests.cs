using FluentAssertions;
using PPDS.Auth.Credentials;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Authentication;

/// <summary>
/// Integration tests for certificate-based (service principal) authentication.
/// These tests verify that CertificateFileCredentialProvider can successfully
/// authenticate to Dataverse using a certificate file.
/// </summary>
[Trait("Category", "Integration")]
public class CertificateAuthenticationTests : LiveTestBase, IDisposable
{
    [SkipIfNoCertificate]
    public async Task CertificateFileCredentialProvider_CreatesWorkingServiceClient()
    {
        // Arrange
        var certPath = Configuration.GetCertificatePath();
        using var provider = new CertificateFileCredentialProvider(
            Configuration.ApplicationId!,
            certPath,
            Configuration.CertificatePassword,
            Configuration.TenantId!);

        // Act
        using var client = await provider.CreateServiceClientAsync(Configuration.DataverseUrl!);

        // Assert
        client.Should().NotBeNull();
        client.IsReady.Should().BeTrue("ServiceClient should be ready after successful authentication");
    }

    [SkipIfNoCertificate]
    public async Task CertificateFileCredentialProvider_CanExecuteWhoAmI()
    {
        // Arrange
        var certPath = Configuration.GetCertificatePath();
        using var provider = new CertificateFileCredentialProvider(
            Configuration.ApplicationId!,
            certPath,
            Configuration.CertificatePassword,
            Configuration.TenantId!);

        using var client = await provider.CreateServiceClientAsync(Configuration.DataverseUrl!);

        // Act
        var response = (Microsoft.Crm.Sdk.Messages.WhoAmIResponse)client.Execute(
            new Microsoft.Crm.Sdk.Messages.WhoAmIRequest());

        // Assert
        response.Should().NotBeNull();
        response.UserId.Should().NotBeEmpty("WhoAmI should return a valid user ID");
        response.OrganizationId.Should().NotBeEmpty("WhoAmI should return a valid organization ID");
    }

    [SkipIfNoCertificate]
    public void CertificateFileCredentialProvider_SetsIdentityProperty()
    {
        // Arrange
        var certPath = Configuration.GetCertificatePath();
        using var provider = new CertificateFileCredentialProvider(
            Configuration.ApplicationId!,
            certPath,
            Configuration.CertificatePassword,
            Configuration.TenantId!);

        // Assert - Identity returns the application ID directly
        provider.Identity.Should().Be(Configuration.ApplicationId);
        provider.AuthMethod.Should().Be(PPDS.Auth.Profiles.AuthMethod.CertificateFile);
    }

    [SkipIfNoCertificate]
    public async Task CertificateFileCredentialProvider_SetsTokenExpirationAfterAuth()
    {
        // Arrange
        var certPath = Configuration.GetCertificatePath();
        using var provider = new CertificateFileCredentialProvider(
            Configuration.ApplicationId!,
            certPath,
            Configuration.CertificatePassword,
            Configuration.TenantId!);

        // Token expiration is null before authentication
        provider.TokenExpiresAt.Should().BeNull();

        // Act
        using var client = await provider.CreateServiceClientAsync(Configuration.DataverseUrl!);

        // Assert - Token expiration is set after authentication
        provider.TokenExpiresAt.Should().NotBeNull();
        provider.TokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [SkipIfNoCertificate]
    public void LiveTestConfiguration_CanLoadCertificate()
    {
        // Arrange & Act
        using var cert = Configuration.LoadCertificate();

        // Assert
        cert.Should().NotBeNull();
        cert.HasPrivateKey.Should().BeTrue("Certificate must have private key for authentication");
        cert.Thumbprint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CertificateFileCredentialProvider_ThrowsOnNullArguments()
    {
        // Act & Assert
        var act1 = () => new CertificateFileCredentialProvider(null!, "path", null, "tenant");
        var act2 = () => new CertificateFileCredentialProvider("app", null!, null, "tenant");
        var act3 = () => new CertificateFileCredentialProvider("app", "path", null, null!);

        act1.Should().Throw<ArgumentNullException>().WithParameterName("applicationId");
        act2.Should().Throw<ArgumentNullException>().WithParameterName("certificatePath");
        act3.Should().Throw<ArgumentNullException>().WithParameterName("tenantId");
    }

    [Fact]
    public async Task CertificateFileCredentialProvider_ThrowsOnMissingFile()
    {
        // Arrange
        using var provider = new CertificateFileCredentialProvider(
            "00000000-0000-0000-0000-000000000000",
            "/nonexistent/path/cert.pfx",
            null,
            "00000000-0000-0000-0000-000000000000");

        // Act & Assert
        var act = () => provider.CreateServiceClientAsync("https://fake.crm.dynamics.com");
        await act.Should().ThrowAsync<AuthenticationException>()
            .WithMessage("*not found*");
    }

    public void Dispose()
    {
        Configuration.Dispose();
    }
}
