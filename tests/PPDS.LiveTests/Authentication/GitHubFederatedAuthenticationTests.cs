using FluentAssertions;
using PPDS.Auth.Credentials;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Authentication;

/// <summary>
/// Integration tests for GitHub OIDC federated authentication.
/// These tests only run inside GitHub Actions with:
/// - 'id-token: write' permission in the workflow
/// - Federated credential configured in Azure AD App Registration
/// </summary>
[Trait("Category", "Integration")]
public class GitHubFederatedAuthenticationTests : LiveTestBase, IDisposable
{
    [SkipIfNoGitHubOidc]
    public async Task GitHubFederatedCredentialProvider_CreatesWorkingServiceClient()
    {
        // Arrange
        using var provider = new GitHubFederatedCredentialProvider(
            Configuration.ApplicationId!,
            Configuration.TenantId!);

        // Act
        using var client = await provider.CreateServiceClientAsync(Configuration.DataverseUrl!);

        // Assert
        client.Should().NotBeNull();
        client.IsReady.Should().BeTrue("ServiceClient should be ready after successful authentication");
    }

    [SkipIfNoGitHubOidc]
    public async Task GitHubFederatedCredentialProvider_CanExecuteWhoAmI()
    {
        // Arrange
        using var provider = new GitHubFederatedCredentialProvider(
            Configuration.ApplicationId!,
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

    [SkipIfNoGitHubOidc]
    public async Task GitHubFederatedCredentialProvider_SetsAccessTokenAfterAuth()
    {
        // Arrange
        using var provider = new GitHubFederatedCredentialProvider(
            Configuration.ApplicationId!,
            Configuration.TenantId!);

        // Act
        using var client = await provider.CreateServiceClientAsync(Configuration.DataverseUrl!);

        // Assert - Access token is available after authentication
        provider.AccessToken.Should().NotBeNullOrWhiteSpace();
        provider.TokenExpiresAt.Should().NotBeNull();
        provider.TokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [SkipIfNoGitHubOidc]
    public void GitHubFederatedCredentialProvider_SetsIdentityProperty()
    {
        // Arrange
        using var provider = new GitHubFederatedCredentialProvider(
            Configuration.ApplicationId!,
            Configuration.TenantId!);

        // Assert - Identity returns the application ID directly
        provider.Identity.Should().Be(Configuration.ApplicationId);
        provider.AuthMethod.Should().Be(PPDS.Auth.Profiles.AuthMethod.GitHubFederated);
    }

    [Fact]
    public void GitHubFederatedCredentialProvider_ThrowsOnNullArguments()
    {
        // Act & Assert
        var act1 = () => new GitHubFederatedCredentialProvider(null!, "tenant");
        var act2 = () => new GitHubFederatedCredentialProvider("app", null!);

        act1.Should().Throw<ArgumentNullException>().WithParameterName("applicationId");
        act2.Should().Throw<ArgumentNullException>().WithParameterName("tenantId");
    }

    [Fact]
    public void Configuration_DetectsGitHubOidcEnvironment()
    {
        // This test verifies the configuration correctly detects OIDC environment
        var hasOidc = Configuration.HasGitHubOidcCredentials;

        // If we're in GitHub Actions with OIDC configured, this should be true
        // Otherwise it should be false (and tests using SkipIfNoGitHubOidc will skip)
        var tokenUrl = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL");
        var expectedHasOidc = !string.IsNullOrWhiteSpace(tokenUrl) &&
                              !string.IsNullOrWhiteSpace(Configuration.GitHubOidcRequestToken) &&
                              !string.IsNullOrWhiteSpace(Configuration.ApplicationId) &&
                              !string.IsNullOrWhiteSpace(Configuration.TenantId) &&
                              !string.IsNullOrWhiteSpace(Configuration.DataverseUrl);

        hasOidc.Should().Be(expectedHasOidc);
    }

    public void Dispose()
    {
        Configuration.Dispose();
    }
}
