using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using Xunit;

namespace PPDS.Auth.Tests.Discovery;

public class BapEnvironmentServiceTests
{
    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, string content)
    {
        var handler = new MockHttpMessageHandler(statusCode, content);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task DiscoverEnvironments_MapsJsonToDiscoveredEnvironments()
    {
        var json = @"{ ""value"": [{ ""name"": ""env-id-1"", ""properties"": { ""displayName"": ""PPDS Demo - Dev"", ""azureRegion"": ""westus"", ""environmentSku"": ""Developer"", ""tenantId"": ""34502e2f-89bb-4550-8a28-1d734e433e88"", ""linkedEnvironmentMetadata"": { ""resourceId"": ""3a504f43-85d7-f011-95c7-000d3a5cc636"", ""friendlyName"": ""PPDS Demo - Dev"", ""uniqueName"": ""unq3a504f43"", ""domainName"": ""orgcabef92d"", ""version"": ""9.2.26033.179"", ""instanceUrl"": ""https://orgcabef92d.crm.dynamics.com/"", ""instanceState"": ""Ready"" } } }] }";

        var client = CreateMockHttpClient(HttpStatusCode.OK, json);
        var service = new BapEnvironmentService(client, "https://api.bap.microsoft.com", _ => Task.FromResult("fake-token"));

        var result = await service.DiscoverEnvironmentsAsync();

        result.Should().HaveCount(1);
        var env = result[0];
        env.FriendlyName.Should().Be("PPDS Demo - Dev");
        env.UniqueName.Should().Be("unq3a504f43");
        env.ApiUrl.Should().Be("https://orgcabef92d.crm.dynamics.com/");
        env.UrlName.Should().Be("orgcabef92d");
        env.EnvironmentId.Should().Be("env-id-1");
        env.Region.Should().Be("westus");
        env.State.Should().Be(0); // Ready
        env.OrganizationType.Should().Be(6); // Developer
        env.Version.Should().Be("9.2.26033.179");
        env.Id.Should().Be(System.Guid.Parse("3a504f43-85d7-f011-95c7-000d3a5cc636"));
        env.TenantId.Should().Be(System.Guid.Parse("34502e2f-89bb-4550-8a28-1d734e433e88"));
    }

    [Fact]
    public async Task DiscoverEnvironments_SkipsNonDataverseEnvironments()
    {
        var json = @"{ ""value"": [
            { ""name"": ""env-1"", ""properties"": { ""displayName"": ""With Dataverse"", ""azureRegion"": ""westus"", ""environmentSku"": ""Developer"", ""linkedEnvironmentMetadata"": { ""resourceId"": ""3a504f43-85d7-f011-95c7-000d3a5cc636"", ""friendlyName"": ""With Dataverse"", ""uniqueName"": ""unq1"", ""domainName"": ""org1"", ""instanceUrl"": ""https://org1.crm.dynamics.com/"", ""instanceState"": ""Ready"" } } },
            { ""name"": ""env-2"", ""properties"": { ""displayName"": ""Default (no Dataverse)"", ""environmentSku"": ""Default"" } }
        ] }";

        var client = CreateMockHttpClient(HttpStatusCode.OK, json);
        var service = new BapEnvironmentService(client, "https://api.bap.microsoft.com", _ => Task.FromResult("fake-token"));

        var result = await service.DiscoverEnvironmentsAsync();

        result.Should().HaveCount(1);
        result[0].FriendlyName.Should().Be("With Dataverse");
    }

    [Fact]
    public async Task DiscoverEnvironments_Throws_OnForbidden()
    {
        var json = @"{ ""error"": { ""code"": ""Forbidden"", ""message"": ""Not registered"" } }";
        var client = CreateMockHttpClient(HttpStatusCode.Forbidden, json);
        var service = new BapEnvironmentService(client, "https://api.bap.microsoft.com", _ => Task.FromResult("fake-token"));

        var act = () => service.DiscoverEnvironmentsAsync();

        var ex = await act.Should().ThrowAsync<AuthenticationException>();
        ex.Which.ErrorCode.Should().Be("Auth.BapApiForbidden");
    }

    [Fact]
    public async Task DiscoverEnvironments_Throws_OnUnauthorized()
    {
        var client = CreateMockHttpClient(HttpStatusCode.Unauthorized, "");
        var service = new BapEnvironmentService(client, "https://api.bap.microsoft.com", _ => Task.FromResult("fake-token"));

        var act = () => service.DiscoverEnvironmentsAsync();

        var ex = await act.Should().ThrowAsync<AuthenticationException>();
        ex.Which.ErrorCode.Should().Be("Auth.BapApiUnauthorized");
    }

    [Fact]
    public async Task DiscoverEnvironments_Throws_OnServerError()
    {
        var client = CreateMockHttpClient(HttpStatusCode.InternalServerError, "Internal Server Error");
        var service = new BapEnvironmentService(client, "https://api.bap.microsoft.com", _ => Task.FromResult("fake-token"));

        var act = () => service.DiscoverEnvironmentsAsync();

        var ex = await act.Should().ThrowAsync<AuthenticationException>();
        ex.Which.ErrorCode.Should().Be("Auth.BapApiError");
    }

    [Fact]
    public async Task DiscoverEnvironments_ReturnsEmpty_WhenNoValueProperty()
    {
        var json = @"{ }";
        var client = CreateMockHttpClient(HttpStatusCode.OK, json);
        var service = new BapEnvironmentService(client, "https://api.bap.microsoft.com", _ => Task.FromResult("fake-token"));

        var result = await service.DiscoverEnvironmentsAsync();

        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Production", 0)]
    [InlineData("Sandbox", 5)]
    [InlineData("Developer", 6)]
    [InlineData("Trial", 11)]
    [InlineData("Default", 12)]
    public async Task DiscoverEnvironments_MapsEnvironmentSku(string sku, int expectedOrgType)
    {
        var json = $@"{{ ""value"": [{{ ""name"": ""env-1"", ""properties"": {{ ""displayName"": ""Test"", ""environmentSku"": ""{sku}"", ""linkedEnvironmentMetadata"": {{ ""resourceId"": ""3a504f43-85d7-f011-95c7-000d3a5cc636"", ""friendlyName"": ""Test"", ""uniqueName"": ""unq1"", ""domainName"": ""org1"", ""instanceUrl"": ""https://org1.crm.dynamics.com/"", ""instanceState"": ""Ready"" }} }} }}] }}";

        var client = CreateMockHttpClient(HttpStatusCode.OK, json);
        var service = new BapEnvironmentService(client, "https://api.bap.microsoft.com", _ => Task.FromResult("fake-token"));

        var result = await service.DiscoverEnvironmentsAsync();

        result.Should().HaveCount(1);
        result[0].OrganizationType.Should().Be(expectedOrgType);
    }

    [Fact]
    public async Task DiscoverEnvironments_MapsNonReadyState()
    {
        var json = @"{ ""value"": [{ ""name"": ""env-1"", ""properties"": { ""displayName"": ""Test"", ""environmentSku"": ""Sandbox"", ""linkedEnvironmentMetadata"": { ""resourceId"": ""3a504f43-85d7-f011-95c7-000d3a5cc636"", ""friendlyName"": ""Test"", ""uniqueName"": ""unq1"", ""domainName"": ""org1"", ""instanceUrl"": ""https://org1.crm.dynamics.com/"", ""instanceState"": ""Provisioning"" } } }] }";

        var client = CreateMockHttpClient(HttpStatusCode.OK, json);
        var service = new BapEnvironmentService(client, "https://api.bap.microsoft.com", _ => Task.FromResult("fake-token"));

        var result = await service.DiscoverEnvironmentsAsync();

        result.Should().HaveCount(1);
        result[0].State.Should().Be(1); // Not Ready
    }

    [Fact]
    public async Task DiscoverEnvironments_ResultsOrderedByFriendlyName()
    {
        var json = @"{ ""value"": [
            { ""name"": ""env-2"", ""properties"": { ""displayName"": ""Zebra"", ""environmentSku"": ""Sandbox"", ""linkedEnvironmentMetadata"": { ""resourceId"": ""3a504f43-85d7-f011-95c7-000d3a5cc636"", ""friendlyName"": ""Zebra"", ""uniqueName"": ""unq2"", ""domainName"": ""org2"", ""instanceUrl"": ""https://org2.crm.dynamics.com/"", ""instanceState"": ""Ready"" } } },
            { ""name"": ""env-1"", ""properties"": { ""displayName"": ""Alpha"", ""environmentSku"": ""Developer"", ""linkedEnvironmentMetadata"": { ""resourceId"": ""4b604f43-85d7-f011-95c7-000d3a5cc636"", ""friendlyName"": ""Alpha"", ""uniqueName"": ""unq1"", ""domainName"": ""org1"", ""instanceUrl"": ""https://org1.crm.dynamics.com/"", ""instanceState"": ""Ready"" } } }
        ] }";

        var client = CreateMockHttpClient(HttpStatusCode.OK, json);
        var service = new BapEnvironmentService(client, "https://api.bap.microsoft.com", _ => Task.FromResult("fake-token"));

        var result = await service.DiscoverEnvironmentsAsync();

        result.Should().HaveCount(2);
        result[0].FriendlyName.Should().Be("Alpha");
        result[1].FriendlyName.Should().Be("Zebra");
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public MockHttpMessageHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
