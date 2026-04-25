using System.Net.Http;
using FluentAssertions;
using Microsoft.Identity.Client;
using PPDS.Auth.Cloud;
using PPDS.Auth.Discovery;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Authentication;

[Trait("Category", "Integration")]
public class BapDiscoveryIntegrationTests : LiveTestBase, IDisposable
{
    [SkipIfNoClientSecret]
    public async Task BapEnvironmentService_DiscoversEnvironments()
    {
        var bapApiUrl = CloudEndpoints.GetBapApiUrl(CloudEnvironment.Public);
        var scope = $"{bapApiUrl}/.default";
        var authority = CloudEndpoints.GetAuthorityUrl(CloudEnvironment.Public, Configuration.TenantId);

        var msalClient = ConfidentialClientApplicationBuilder
            .Create(Configuration.ApplicationId)
            .WithAuthority(authority)
            .WithClientSecret(Configuration.ClientSecret)
            .Build();

        using var httpClient = new HttpClient();
        var service = new BapEnvironmentService(
            httpClient,
            bapApiUrl,
            async ct =>
            {
                var result = await msalClient
                    .AcquireTokenForClient(new[] { scope })
                    .ExecuteAsync(ct);
                return result.AccessToken;
            });

        var environments = await service.DiscoverEnvironmentsAsync();

        environments.Should().NotBeEmpty("the SPN should have access to at least one environment");

        foreach (var env in environments)
        {
            env.FriendlyName.Should().NotBeNullOrWhiteSpace();
            env.ApiUrl.Should().NotBeNullOrWhiteSpace();
        }
    }

    public void Dispose()
    {
    }
}
