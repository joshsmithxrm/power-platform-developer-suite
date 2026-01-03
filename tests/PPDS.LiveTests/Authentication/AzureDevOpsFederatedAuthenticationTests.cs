using FluentAssertions;
using PPDS.Auth.Credentials;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Authentication;

/// <summary>
/// Integration tests for Azure DevOps OIDC federated authentication.
/// These tests only run inside Azure Pipelines with:
/// - Workload identity federation service connection configured
/// - SYSTEM_ACCESSTOKEN available in the pipeline
/// - Federated credential configured in Azure AD App Registration
/// </summary>
[Trait("Category", "Integration")]
public class AzureDevOpsFederatedAuthenticationTests : LiveTestBase, IDisposable
{
    [SkipIfNoAzureDevOpsOidc]
    public async Task AzureDevOpsFederatedCredentialProvider_CreatesWorkingServiceClient()
    {
        // Arrange
        using var provider = new AzureDevOpsFederatedCredentialProvider(
            Configuration.ApplicationId!,
            Configuration.TenantId!);

        // Act
        using var client = await provider.CreateServiceClientAsync(Configuration.DataverseUrl!);

        // Assert
        client.Should().NotBeNull();
        client.IsReady.Should().BeTrue("ServiceClient should be ready after successful authentication");
    }

    [SkipIfNoAzureDevOpsOidc]
    public async Task AzureDevOpsFederatedCredentialProvider_CanExecuteWhoAmI()
    {
        // Arrange
        using var provider = new AzureDevOpsFederatedCredentialProvider(
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

    [SkipIfNoAzureDevOpsOidc]
    public async Task AzureDevOpsFederatedCredentialProvider_SetsAccessTokenAfterAuth()
    {
        // Arrange
        using var provider = new AzureDevOpsFederatedCredentialProvider(
            Configuration.ApplicationId!,
            Configuration.TenantId!);

        // Act
        using var client = await provider.CreateServiceClientAsync(Configuration.DataverseUrl!);

        // Assert - Access token is available after authentication
        provider.AccessToken.Should().NotBeNullOrWhiteSpace();
        provider.TokenExpiresAt.Should().NotBeNull();
        provider.TokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [SkipIfNoAzureDevOpsOidc]
    public void AzureDevOpsFederatedCredentialProvider_SetsIdentityProperty()
    {
        // Arrange
        using var provider = new AzureDevOpsFederatedCredentialProvider(
            Configuration.ApplicationId!,
            Configuration.TenantId!);

        // Assert
        provider.Identity.Should().NotBeNullOrWhiteSpace();
        provider.Identity.Should().StartWith("app:");
        provider.AuthMethod.Should().Be(PPDS.Auth.Profiles.AuthMethod.AzureDevOpsFederated);
    }

    [Fact]
    public void AzureDevOpsFederatedCredentialProvider_ThrowsOnNullArguments()
    {
        // Act & Assert
        var act1 = () => new AzureDevOpsFederatedCredentialProvider(null!, "tenant");
        var act2 = () => new AzureDevOpsFederatedCredentialProvider("app", null!);

        act1.Should().Throw<ArgumentNullException>().WithParameterName("applicationId");
        act2.Should().Throw<ArgumentNullException>().WithParameterName("tenantId");
    }

    [Fact]
    public void Configuration_DetectsAzureDevOpsOidcEnvironment()
    {
        // This test verifies the configuration correctly detects Azure DevOps OIDC environment
        var hasOidc = Configuration.HasAzureDevOpsOidcCredentials;

        // If we're in Azure DevOps with OIDC configured, this should be true
        // Otherwise it should be false (and tests using SkipIfNoAzureDevOpsOidc will skip)
        var oidcUri = Environment.GetEnvironmentVariable("SYSTEM_OIDCREQUESTURI");
        var accessToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
        var serviceConnectionId = Environment.GetEnvironmentVariable("AZURESUBSCRIPTION_SERVICE_CONNECTION_ID");

        var expectedHasOidc = !string.IsNullOrWhiteSpace(oidcUri) &&
                              !string.IsNullOrWhiteSpace(accessToken) &&
                              !string.IsNullOrWhiteSpace(serviceConnectionId) &&
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
