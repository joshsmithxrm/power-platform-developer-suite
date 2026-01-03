using FluentAssertions;
using PPDS.Auth.Credentials;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Authentication;

/// <summary>
/// Integration tests for client secret (service principal) authentication.
/// These tests verify that ClientSecretCredentialProvider can successfully
/// authenticate to Dataverse using a client ID and secret.
/// </summary>
[Trait("Category", "Integration")]
public class ClientSecretAuthenticationTests : LiveTestBase, IDisposable
{
    [SkipIfNoClientSecret]
    public async Task ClientSecretCredentialProvider_CreatesWorkingServiceClient()
    {
        // Arrange
        using var provider = new ClientSecretCredentialProvider(
            Configuration.ApplicationId!,
            Configuration.ClientSecret!,
            Configuration.TenantId!);

        // Act
        using var client = await provider.CreateServiceClientAsync(Configuration.DataverseUrl!);

        // Assert
        client.Should().NotBeNull();
        client.IsReady.Should().BeTrue("ServiceClient should be ready after successful authentication");
    }

    [SkipIfNoClientSecret]
    public async Task ClientSecretCredentialProvider_CanExecuteWhoAmI()
    {
        // Arrange
        using var provider = new ClientSecretCredentialProvider(
            Configuration.ApplicationId!,
            Configuration.ClientSecret!,
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

    [SkipIfNoClientSecret]
    public void ClientSecretCredentialProvider_SetsIdentityProperty()
    {
        // Arrange
        using var provider = new ClientSecretCredentialProvider(
            Configuration.ApplicationId!,
            Configuration.ClientSecret!,
            Configuration.TenantId!);

        // Assert - Identity returns the application ID directly
        provider.Identity.Should().Be(Configuration.ApplicationId);
        provider.AuthMethod.Should().Be(PPDS.Auth.Profiles.AuthMethod.ClientSecret);
    }

    [SkipIfNoClientSecret]
    public async Task ClientSecretCredentialProvider_SetsTokenExpirationAfterAuth()
    {
        // Arrange
        using var provider = new ClientSecretCredentialProvider(
            Configuration.ApplicationId!,
            Configuration.ClientSecret!,
            Configuration.TenantId!);

        // Token expiration is null before authentication
        provider.TokenExpiresAt.Should().BeNull();

        // Act
        using var client = await provider.CreateServiceClientAsync(Configuration.DataverseUrl!);

        // Assert - Token expiration is set after authentication
        provider.TokenExpiresAt.Should().NotBeNull();
        provider.TokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ClientSecretCredentialProvider_ThrowsOnNullArguments()
    {
        // Act & Assert
        var act1 = () => new ClientSecretCredentialProvider(null!, "secret", "tenant");
        var act2 = () => new ClientSecretCredentialProvider("app", null!, "tenant");
        var act3 = () => new ClientSecretCredentialProvider("app", "secret", null!);

        act1.Should().Throw<ArgumentNullException>().WithParameterName("applicationId");
        act2.Should().Throw<ArgumentNullException>().WithParameterName("clientSecret");
        act3.Should().Throw<ArgumentNullException>().WithParameterName("tenantId");
    }

    [Fact]
    public async Task ClientSecretCredentialProvider_ThrowsOnInvalidCredentials()
    {
        // Arrange - Use fake credentials that will fail
        using var provider = new ClientSecretCredentialProvider(
            "00000000-0000-0000-0000-000000000000",
            "invalid-secret",
            "00000000-0000-0000-0000-000000000000");

        // Act & Assert
        var act = () => provider.CreateServiceClientAsync("https://fake.crm.dynamics.com");
        await act.Should().ThrowAsync<AuthenticationException>();
    }

    public void Dispose()
    {
        Configuration.Dispose();
    }
}
