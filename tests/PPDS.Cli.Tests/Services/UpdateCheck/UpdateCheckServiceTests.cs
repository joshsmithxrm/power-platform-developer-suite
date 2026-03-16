using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Protected;
using PPDS.Cli.Services.UpdateCheck;
using Xunit;

namespace PPDS.Cli.Tests.Services.UpdateCheck;

/// <summary>
/// Unit tests for <see cref="UpdateCheckService"/>.
/// </summary>
public class UpdateCheckServiceTests : IDisposable
{
    private readonly string _tempPath;
    private readonly string _cacheFile;

    public UpdateCheckServiceTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"ppds-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempPath);
        _cacheFile = Path.Combine(_tempPath, "update-check.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try { Directory.Delete(_tempPath, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }
    }

    #region Helpers

    private static HttpMessageHandler BuildHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        return mock.Object;
    }

    private static HttpMessageHandler BuildThrowingHandler(Exception ex)
    {
        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(ex);
        return mock.Object;
    }

    private static string MakeVersionsJson(params string[] versions)
    {
        return JsonSerializer.Serialize(new { versions });
    }

    #endregion

    // ─── Scenario 1: CheckAsync returns latest stable and pre-release ──────────

    [Fact]
    public async Task CheckAsync_ReturnsLatestStableAndPreRelease()
    {
        var handler = BuildHandler(MakeVersionsJson(
            "0.1.0", "0.2.0", "0.3.0-alpha.1", "0.4.0", "0.5.0-beta.2"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.1.0");

        result.Should().NotBeNull();
        result!.LatestStableVersion.Should().Be("0.4.0");
        result.LatestPreReleaseVersion.Should().Be("0.5.0-beta.2");
    }

    // ─── Scenario 2: Current is latest → no update available ──────────────────

    [Fact]
    public async Task CheckAsync_CurrentIsLatest_NoUpdateAvailable()
    {
        var handler = BuildHandler(MakeVersionsJson("0.4.0", "0.3.0", "0.2.0"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.4.0");

        result.Should().NotBeNull();
        result!.StableUpdateAvailable.Should().BeFalse();
        result.PreReleaseUpdateAvailable.Should().BeFalse();
        result.UpdateAvailable.Should().BeFalse();
        result.UpdateCommand.Should().BeNull();
    }

    // ─── Scenario 3: Stable available → plain update command ──────────────────

    [Fact]
    public async Task CheckAsync_StableUpdateAvailable_SuggestsPlainCommand()
    {
        var handler = BuildHandler(MakeVersionsJson("0.4.0", "0.5.0", "0.6.0", "0.7.0-alpha.0"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.4.0");

        result.Should().NotBeNull();
        result!.StableUpdateAvailable.Should().BeTrue();
        result.UpdateCommand.Should().Be("dotnet tool update PPDS.Cli -g");
    }

    // ─── Scenario 4: Pre-release user, no newer stable, newer pre-release → --prerelease ─

    [Fact]
    public async Task CheckAsync_PreReleaseUser_NoNewerStable_NewerPreRelease_SuggestsPrereleaseCommand()
    {
        // Current: 0.5.0-beta.1  Latest stable: 0.4.0  Latest pre-release: 0.5.0-beta.2
        var handler = BuildHandler(MakeVersionsJson(
            "0.4.0", "0.5.0-beta.1", "0.5.0-beta.2"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.5.0-beta.1");

        result.Should().NotBeNull();
        result!.StableUpdateAvailable.Should().BeFalse();
        result.PreReleaseUpdateAvailable.Should().BeTrue();
        result.UpdateCommand.Should().Be("dotnet tool update PPDS.Cli -g --prerelease");
    }

    // ─── Scenario 5: Pre-release user, newer stable available → plain command (stable priority) ─

    [Fact]
    public async Task CheckAsync_PreReleaseUser_NewerStableAvailable_SuggestsPlainCommand()
    {
        // Current: 0.5.0-beta.1  Latest stable: 0.6.0  Latest pre-release: 0.5.0-beta.2
        var handler = BuildHandler(MakeVersionsJson(
            "0.4.0", "0.5.0-beta.1", "0.5.0-beta.2", "0.6.0"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.5.0-beta.1");

        result.Should().NotBeNull();
        result!.StableUpdateAvailable.Should().BeTrue();
        result.UpdateCommand.Should().Be("dotnet tool update PPDS.Cli -g");
    }

    // ─── Scenario 6: CheckAsync caches result; GetCachedResult returns it ──────

    [Fact]
    public async Task CheckAsync_CachesResult_GetCachedResultReturnsIt()
    {
        var handler = BuildHandler(MakeVersionsJson("0.4.0", "0.5.0"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var checkResult = await svc.CheckAsync("0.4.0");
        var cachedResult = svc.GetCachedResult();

        cachedResult.Should().NotBeNull();
        cachedResult!.CurrentVersion.Should().Be(checkResult!.CurrentVersion);
        cachedResult.LatestStableVersion.Should().Be(checkResult.LatestStableVersion);
        cachedResult.CheckedAt.Should().Be(checkResult.CheckedAt);
    }

    // ─── Scenario 7: Expired cache returns null ────────────────────────────────

    [Fact]
    public void GetCachedResult_ExpiredCache_ReturnsNull()
    {
        // Write a cache file with a timestamp > 24 hours ago
        var staleResult = new UpdateCheckResult
        {
            CurrentVersion = "0.4.0",
            LatestStableVersion = "0.5.0",
            StableUpdateAvailable = true,
            UpdateCommand = "dotnet tool update PPDS.Cli -g",
            CheckedAt = DateTimeOffset.UtcNow.AddHours(-25)
        };
        var json = JsonSerializer.Serialize(staleResult, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(_cacheFile, json);

        var svc = new UpdateCheckService(cachePath: _cacheFile);

        var result = svc.GetCachedResult();

        result.Should().BeNull();
    }

    // ─── Scenario 8: Network error returns null (no throw) ────────────────────

    [Fact]
    public async Task CheckAsync_NetworkError_ReturnsNull()
    {
        var handler = BuildThrowingHandler(new HttpRequestException("Network unavailable"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.4.0");

        result.Should().BeNull();
    }

    // ─── Scenario 9: Non-success HTTP status returns null ─────────────────────

    [Fact]
    public async Task CheckAsync_NonSuccessHttpStatus_ReturnsNull()
    {
        var handler = BuildHandler("{\"error\":\"not found\"}", HttpStatusCode.NotFound);
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.4.0");

        result.Should().BeNull();
    }

    // ─── Scenario 10: Corrupt cache file returns null (no throw) ──────────────

    [Fact]
    public void GetCachedResult_CorruptCache_ReturnsNull()
    {
        File.WriteAllText(_cacheFile, "this is not valid json {{{{");

        var svc = new UpdateCheckService(cachePath: _cacheFile);

        var result = svc.GetCachedResult();

        result.Should().BeNull();
    }

    // ─── Additional edge-case coverage ────────────────────────────────────────

    [Fact]
    public void GetCachedResult_NoCacheFile_ReturnsNull()
    {
        var svc = new UpdateCheckService(cachePath: _cacheFile);

        var result = svc.GetCachedResult();

        result.Should().BeNull();
    }

    // ─── New GetCachedResult tests (Task 5) ───────────────────────────────────

    [Fact]
    public async Task GetCachedResult_ReturnsCachedResult_WhenCacheIsFresh()
    {
        var handler = BuildHandler(MakeVersionsJson("1.0.0", "2.0.0"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);
        await svc.CheckAsync("1.0.0");

        var cached = svc.GetCachedResult();

        Assert.NotNull(cached);
        Assert.Equal("1.0.0", cached.CurrentVersion);
    }

    [Fact]
    public void GetCachedResult_ReturnsNull_WhenNoCacheFile()
    {
        var svc = new UpdateCheckService(cachePath: _cacheFile);
        var cached = svc.GetCachedResult();
        Assert.Null(cached);
    }

    [Fact]
    public void GetCachedResult_ReturnsNull_WhenCacheCorrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
        File.WriteAllText(_cacheFile, "not json");
        var svc = new UpdateCheckService(cachePath: _cacheFile);
        var cached = svc.GetCachedResult();
        Assert.Null(cached);
    }

    [Fact]
    public async Task CheckAsync_PopulatesCurrentVersion()
    {
        var handler = BuildHandler(MakeVersionsJson("0.4.0"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.4.0");

        result.Should().NotBeNull();
        result!.CurrentVersion.Should().Be("0.4.0");
    }

    [Fact]
    public async Task CheckAsync_CheckedAt_IsRecentUtcTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var handler = BuildHandler(MakeVersionsJson("0.4.0"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.4.0");

        result.Should().NotBeNull();
        result!.CheckedAt.Should().BeOnOrAfter(before);
        result.CheckedAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CheckAsync_VersionListWithOnlyPreReleases_LatestStableIsNull()
    {
        var handler = BuildHandler(MakeVersionsJson("0.1.0-alpha", "0.2.0-beta.1"));
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.1.0-alpha");

        result.Should().NotBeNull();
        result!.LatestStableVersion.Should().BeNull();
        result.LatestPreReleaseVersion.Should().Be("0.2.0-beta.1");
    }

    [Fact]
    public async Task CheckAsync_EmptyVersionList_ReturnsResultWithNulls()
    {
        var handler = BuildHandler(MakeVersionsJson());
        var svc = new UpdateCheckService(handler: handler, cachePath: _cacheFile);

        var result = await svc.CheckAsync("0.4.0");

        result.Should().NotBeNull();
        result!.LatestStableVersion.Should().BeNull();
        result.LatestPreReleaseVersion.Should().BeNull();
        result.UpdateAvailable.Should().BeFalse();
        result.UpdateCommand.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_CancellationToken_PropagatedToHttp()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var mock = new Mock<HttpMessageHandler>();
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException());
        var svc = new UpdateCheckService(handler: mock.Object, cachePath: _cacheFile);

        // Should not throw — cancelled network call returns null
        var result = await svc.CheckAsync("0.4.0", cts.Token);
        result.Should().BeNull();
    }
}
