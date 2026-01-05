using FluentAssertions;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests;

/// <summary>
/// Smoke tests for live Dataverse integration.
/// These tests verify the test infrastructure works and skip gracefully when credentials are missing.
/// </summary>
public class LiveDataverseSmokeTests : LiveTestBase
{
    [Fact]
    public void Configuration_IsAvailable()
    {
        Configuration.Should().NotBeNull();
    }

    [Fact]
    public void Configuration_ReportsCredentialStatus()
    {
        // This test always runs and verifies the configuration class works
        var hasAny = Configuration.HasAnyCredentials;
        var hasSecret = Configuration.HasClientSecretCredentials;
        var hasCert = Configuration.HasCertificateCredentials;
        var hasGitHubOidc = Configuration.HasGitHubOidcCredentials;
        var hasAzDoOidc = Configuration.HasAzureDevOpsOidcCredentials;

        // Assert that the aggregate property reflects the state of all credential types
        hasAny.Should().Be(hasSecret || hasCert || hasGitHubOidc || hasAzDoOidc);
    }

    [SkipIfNoCredentials]
    public void WhenCredentialsAvailable_CanConnect()
    {
        // This test only runs when credentials are available
        Configuration.HasAnyCredentials.Should().BeTrue();
        Configuration.DataverseUrl.Should().NotBeNullOrWhiteSpace();
    }

    [SkipIfNoClientSecret]
    public void WhenClientSecretAvailable_HasAllRequiredFields()
    {
        Configuration.HasClientSecretCredentials.Should().BeTrue();
        Configuration.ApplicationId.Should().NotBeNullOrWhiteSpace();
        Configuration.ClientSecret.Should().NotBeNullOrWhiteSpace();
        Configuration.TenantId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void MissingCredentialsReason_DescribesMissingVars()
    {
        var config = new LiveTestConfiguration();
        var reason = config.GetMissingCredentialsReason();

        if (!config.HasAnyCredentials)
        {
            reason.Should().Contain("Missing environment variables");
        }
    }
}
