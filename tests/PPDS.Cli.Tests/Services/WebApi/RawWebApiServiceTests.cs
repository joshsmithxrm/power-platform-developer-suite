using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.WebApi;
using Xunit;

namespace PPDS.Cli.Tests.Services.WebApi;

public class RawWebApiServiceTests
{
    private const string EnvironmentUrl = "https://org.crm.dynamics.com";

    private readonly Mock<IPowerPlatformTokenProvider> _tokenProvider;
    private readonly Mock<ILogger<RawWebApiService>> _logger;

    public RawWebApiServiceTests()
    {
        _tokenProvider = new Mock<IPowerPlatformTokenProvider>();
        _tokenProvider
            .Setup(p => p.GetTokenForResourceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerPlatformToken
            {
                AccessToken = "test-token",
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
                Resource = EnvironmentUrl
            });

        _logger = new Mock<ILogger<RawWebApiService>>();
    }

    private RawWebApiService CreateService(HttpClient httpClient, ProtectionLevel protectionLevel = ProtectionLevel.Development)
        => new(_tokenProvider.Object, httpClient, _logger.Object);

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, string body, string contentType = "application/json")
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        return new HttpClient(handler.Object);
    }

    private static HttpClient CreateCapturingHttpClient(
        HttpStatusCode statusCode,
        string body,
        out Func<HttpRequestMessage?> getLastRequest)
    {
        HttpRequestMessage? captured = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        getLastRequest = () => captured;
        return new HttpClient(handler.Object);
    }

    // AC-01: GET valid path returns response body
    [Fact]
    public async Task Get_ValidPath_ReturnsBody()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, "{\"value\":[]}");
        var service = CreateService(httpClient);

        var result = await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/accounts",
            Method = HttpMethod.Get
        });

        Assert.Equal(200, result.StatusCode);
        Assert.Contains("\"value\":[]", result.Body);
        Assert.True(result.IsSuccess);
    }

    // AC-03: POST production with --confirm sends the request
    [Fact]
    public async Task Post_Production_WithConfirm_Sends()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.Created, "{}");
        var service = CreateService(httpClient);

        var result = await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/accounts",
            Method = HttpMethod.Post,
            Body = "{}",
            IsConfirmed = true,
            ProtectionLevel = ProtectionLevel.Production
        });

        Assert.Equal(201, result.StatusCode);
        Assert.True(result.IsSuccess);
    }

    // Service-side portion of AC-02: write guard throws PpdsException
    [Fact]
    public async Task Post_Production_NoConfirm_ThrowsWriteBlocked()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, "{}");
        var service = CreateService(httpClient);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/accounts",
            Method = HttpMethod.Post,
            Body = "{}",
            IsConfirmed = false,
            ProtectionLevel = ProtectionLevel.Production
        }));

        Assert.Equal("Api.WriteBlocked", ex.ErrorCode);
    }

    // AC-04: Mutating request on development is allowed without --confirm
    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Mutating_Development_Allowed(string methodName)
    {
        var httpClient = CreateHttpClient(HttpStatusCode.NoContent, string.Empty);
        var service = CreateService(httpClient);

        var result = await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/accounts(00000000-0000-0000-0000-000000000001)",
            Method = new HttpMethod(methodName),
            IsConfirmed = false,
            ProtectionLevel = ProtectionLevel.Development
        });

        Assert.True(result.IsSuccess || result.StatusCode == 204);
    }

    // AC-06: Non-2xx returns IsSuccess=false
    [Fact]
    public async Task Non2xx_ReturnsErrorResponse()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"Not Found\"}}");
        var service = CreateService(httpClient);

        var result = await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/accounts(00000000-0000-0000-0000-000000000099)",
            Method = HttpMethod.Get
        });

        Assert.Equal(404, result.StatusCode);
        Assert.False(result.IsSuccess);
        Assert.Contains("Not Found", result.Body);
    }

    // AC-07: User-supplied headers override defaults
    [Fact]
    public async Task UserHeaders_OverrideDefaults()
    {
        var httpClient = CreateCapturingHttpClient(HttpStatusCode.OK, "{}", out var getRequest);
        var service = CreateService(httpClient);

        await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/accounts",
            Method = HttpMethod.Get,
            Headers = new Dictionary<string, string>
            {
                ["Accept"] = "text/plain",
                ["X-Custom-Header"] = "custom-value"
            }
        });

        var request = getRequest();
        Assert.NotNull(request);
        Assert.Contains("text/plain", request!.Headers.GetValues("Accept"));
        Assert.Contains("custom-value", request.Headers.GetValues("X-Custom-Header"));
    }

    // AC-11: Token acquired for the correct environment URL
    [Fact]
    public async Task Token_AcquiredForEnvironmentUrl()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK, "{}");
        var service = CreateService(httpClient);

        await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/WhoAmI",
            Method = HttpMethod.Get
        });

        _tokenProvider.Verify(
            p => p.GetTokenForResourceAsync(EnvironmentUrl, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // AC-12: Default OData headers applied
    [Fact]
    public async Task DefaultHeaders_Applied()
    {
        var httpClient = CreateCapturingHttpClient(HttpStatusCode.OK, "{}", out var getRequest);
        var service = CreateService(httpClient);

        await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/accounts",
            Method = HttpMethod.Get
        });

        var request = getRequest();
        Assert.NotNull(request);
        Assert.Contains("4.0", request!.Headers.GetValues("OData-Version"));
        Assert.Contains("application/json", request.Headers.GetValues("Accept"));
    }

    // Edge: environment URL trailing slash stripped
    [Fact]
    public async Task TrailingSlash_StrippedFromEnvironmentUrl()
    {
        var httpClient = CreateCapturingHttpClient(HttpStatusCode.OK, "{}", out var getRequest);
        var service = CreateService(httpClient);

        await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = "https://org.crm.dynamics.com/",
            Path = "/api/data/v9.2/accounts",
            Method = HttpMethod.Get
        });

        var request = getRequest();
        Assert.NotNull(request);
        Assert.Equal("https://org.crm.dynamics.com/api/data/v9.2/accounts", request!.RequestUri?.ToString());
    }

    // Edge: path with query string preserved
    [Fact]
    public async Task PathWithQueryString_PreservedAsIs()
    {
        var httpClient = CreateCapturingHttpClient(HttpStatusCode.OK, "{}", out var getRequest);
        var service = CreateService(httpClient);

        await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/accounts?$top=1",
            Method = HttpMethod.Get
        });

        var request = getRequest();
        Assert.NotNull(request);
        Assert.Equal("https://org.crm.dynamics.com/api/data/v9.2/accounts?$top=1", request!.RequestUri?.ToString());
    }

    // Edge: 204 empty body returns success with empty body
    [Fact]
    public async Task EmptyBody_204_ReturnsSuccessWithEmptyBody()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NoContent,
                Content = new StringContent(string.Empty)
            });
        var service = CreateService(new HttpClient(handler.Object));

        var result = await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/accounts(00000000-0000-0000-0000-000000000001)",
            Method = HttpMethod.Delete,
            ProtectionLevel = ProtectionLevel.Development
        });

        Assert.Equal(204, result.StatusCode);
        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.Body);
    }

    // AC-08 foundation: body from string is sent in request
    [Fact]
    public async Task Body_SentInRequest()
    {
        const string body = "{\"name\":\"test\"}";
        string? capturedBody = null;

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (req, ct) =>
            {
                // Read body before the request is disposed.
                capturedBody = await req.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.Created,
                    Content = new StringContent("{}")
                };
            });

        var service = CreateService(new HttpClient(handler.Object));

        await service.SendAsync(new RawWebApiRequest
        {
            EnvironmentUrl = EnvironmentUrl,
            Path = "/api/data/v9.2/accounts",
            Method = HttpMethod.Post,
            Body = body,
            ProtectionLevel = ProtectionLevel.Development
        });

        Assert.Equal(body, capturedBody);
    }
}
