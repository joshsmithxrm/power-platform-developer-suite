using Microsoft.Extensions.Logging;
using Moq;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
using Xunit;

namespace PPDS.Cli.Tests.Services;

public class ConnectionServiceTests
{
    private readonly Mock<IPowerPlatformTokenProvider> _mockTokenProvider;
    private readonly Mock<ILogger<ConnectionService>> _mockLogger;
    private readonly string _environmentId;
    private readonly CloudEnvironment _cloud;

    public ConnectionServiceTests()
    {
        _mockTokenProvider = new Mock<IPowerPlatformTokenProvider>();
        _mockLogger = new Mock<ILogger<ConnectionService>>();
        _environmentId = "test-environment-id";
        _cloud = CloudEnvironment.Public;
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidParameters_CreatesInstance()
    {
        var service = new ConnectionService(
            _mockTokenProvider.Object,
            _cloud,
            _environmentId,
            _mockLogger.Object);

        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_WithNullTokenProvider_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConnectionService(
            null!,
            _cloud,
            _environmentId,
            _mockLogger.Object));
    }

    [Fact]
    public void Constructor_WithNullEnvironmentId_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConnectionService(
            _mockTokenProvider.Object,
            _cloud,
            null!,
            _mockLogger.Object));
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConnectionService(
            _mockTokenProvider.Object,
            _cloud,
            _environmentId,
            null!));
    }

    #endregion

    #region ListAsync Tests

    [Fact]
    public async Task ListAsync_CallsGetFlowApiTokenAsync()
    {
        // Arrange
        _mockTokenProvider
            .Setup(x => x.GetFlowApiTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerPlatformToken
            {
                AccessToken = "test-token",
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
                Resource = "https://service.powerapps.com"
            });

        var service = new ConnectionService(
            _mockTokenProvider.Object,
            _cloud,
            _environmentId,
            _mockLogger.Object);

        // Act & Assert (will fail due to HTTP call, but we verify the token was requested)
        try
        {
            await service.ListAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or PpdsException)
        {
            // Expected - no mock HTTP client (may throw HttpRequestException, PpdsException, or PpdsAuthException)
        }

        _mockTokenProvider.Verify(x => x.GetFlowApiTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetAsync Tests

    [Fact]
    public async Task GetAsync_CallsGetFlowApiTokenAsync()
    {
        // Arrange
        _mockTokenProvider
            .Setup(x => x.GetFlowApiTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerPlatformToken
            {
                AccessToken = "test-token",
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
                Resource = "https://service.powerapps.com"
            });

        var service = new ConnectionService(
            _mockTokenProvider.Object,
            _cloud,
            _environmentId,
            _mockLogger.Object);

        // Act & Assert (will fail due to HTTP call, but we verify the token was requested)
        try
        {
            await service.GetAsync("test-connection-id");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or PpdsException)
        {
            // Expected - no mock HTTP client (may throw HttpRequestException, PpdsException, or PpdsAuthException)
        }

        _mockTokenProvider.Verify(x => x.GetFlowApiTokenAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Cloud Environment Tests

    [Theory]
    [InlineData(CloudEnvironment.Public)]
    [InlineData(CloudEnvironment.UsGov)]
    [InlineData(CloudEnvironment.UsGovHigh)]
    [InlineData(CloudEnvironment.UsGovDod)]
    [InlineData(CloudEnvironment.China)]
    public void Constructor_AcceptsAllCloudEnvironments(CloudEnvironment cloud)
    {
        var service = new ConnectionService(
            _mockTokenProvider.Object,
            cloud,
            _environmentId,
            _mockLogger.Object);

        Assert.NotNull(service);
    }

    #endregion
}
