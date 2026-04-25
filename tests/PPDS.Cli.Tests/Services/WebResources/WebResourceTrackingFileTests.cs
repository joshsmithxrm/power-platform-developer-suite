using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using PPDS.Cli.Services.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Services.WebResources;

public class WebResourceTrackingFileTests : IDisposable
{
    private readonly string _folder;

    public WebResourceTrackingFileTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ppds-trackingfile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsAllFields()
    {
        var trackingFile = new WebResourceTrackingFile(
            Version: 1,
            EnvironmentUrl: "https://org.crm.dynamics.com",
            Solution: "core_solution",
            StripPrefix: true,
            PulledAt: new DateTime(2026, 4, 25, 10, 30, 0, DateTimeKind.Utc),
            Resources: new Dictionary<string, TrackedResource>
            {
                ["new_/scripts/app.js"] = new TrackedResource(
                    Id: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ModifiedOn: new DateTime(2026, 4, 20, 14, 22, 0, DateTimeKind.Utc),
                    Hash: "sha256:abc123",
                    LocalPath: "scripts/app.js",
                    WebResourceType: 3),
            });

        await WebResourceTrackingFile.WriteAsync(_folder, trackingFile);

        var roundTripped = await WebResourceTrackingFile.ReadAsync(_folder);

        roundTripped.Should().NotBeNull();
        roundTripped!.Version.Should().Be(1);
        roundTripped.EnvironmentUrl.Should().Be("https://org.crm.dynamics.com");
        roundTripped.Solution.Should().Be("core_solution");
        roundTripped.StripPrefix.Should().BeTrue();
        roundTripped.PulledAt.Should().Be(new DateTime(2026, 4, 25, 10, 30, 0, DateTimeKind.Utc));
        roundTripped.Resources.Should().ContainKey("new_/scripts/app.js");
        var resource = roundTripped.Resources["new_/scripts/app.js"];
        resource.Id.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        resource.ModifiedOn.Should().Be(new DateTime(2026, 4, 20, 14, 22, 0, DateTimeKind.Utc));
        resource.Hash.Should().Be("sha256:abc123");
        resource.LocalPath.Should().Be("scripts/app.js");
        resource.WebResourceType.Should().Be(3);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenFileMissing()
    {
        var result = await WebResourceTrackingFile.ReadAsync(_folder);
        result.Should().BeNull();
    }

    [Fact]
    public async Task WriteAsync_CreatesPpdsSubdirectory()
    {
        var tracking = new WebResourceTrackingFile(
            Version: 1,
            EnvironmentUrl: "https://x",
            Solution: null,
            StripPrefix: false,
            PulledAt: DateTime.UtcNow,
            Resources: new Dictionary<string, TrackedResource>());

        await WebResourceTrackingFile.WriteAsync(_folder, tracking);

        File.Exists(Path.Combine(_folder, ".ppds", "webresources.json")).Should().BeTrue();
    }

    /// <summary>
    /// AC-WR-36: tracking file records SHA256 hash. Verify hash format and content match.
    /// </summary>
    [Fact]
    public async Task TrackingFileContainsHashAndTimestamp()
    {
        var sourcePath = Path.Combine(_folder, "sample.js");
        await File.WriteAllTextAsync(sourcePath, "alert('hello');");
        var hash = await WebResourceTrackingFile.ComputeHashAsync(sourcePath);

        hash.Should().StartWith("sha256:");
        hash.Length.Should().Be("sha256:".Length + 64);

        var modifiedOn = new DateTime(2026, 4, 20, 14, 22, 0, DateTimeKind.Utc);
        var pulledAt = new DateTime(2026, 4, 25, 10, 30, 0, DateTimeKind.Utc);
        var trackingFile = new WebResourceTrackingFile(
            Version: 1,
            EnvironmentUrl: "https://x",
            Solution: null,
            StripPrefix: false,
            PulledAt: pulledAt,
            Resources: new Dictionary<string, TrackedResource>
            {
                ["new_/sample.js"] = new TrackedResource(
                    Id: Guid.NewGuid(),
                    ModifiedOn: modifiedOn,
                    Hash: hash,
                    LocalPath: "sample.js",
                    WebResourceType: 3),
            });

        await WebResourceTrackingFile.WriteAsync(_folder, trackingFile);
        var roundTripped = await WebResourceTrackingFile.ReadAsync(_folder);
        roundTripped!.Resources["new_/sample.js"].Hash.Should().Be(hash);
        roundTripped.Resources["new_/sample.js"].ModifiedOn.Should().Be(modifiedOn);
    }

    [Fact]
    public async Task ComputeHashAsync_MatchesKnownSha256()
    {
        var path = Path.Combine(_folder, "known.txt");
        var content = "abc";
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(content));

        var hash = await WebResourceTrackingFile.ComputeHashAsync(path);

        // Known SHA256 of "abc" (raw bytes)
        var expected = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        hash.Should().Be(expected);
    }

    [Fact]
    public async Task ComputeHashAsync_NegativeCase_DifferentContentProducesDifferentHash()
    {
        var path1 = Path.Combine(_folder, "a.txt");
        var path2 = Path.Combine(_folder, "b.txt");
        await File.WriteAllTextAsync(path1, "alert('a');");
        await File.WriteAllTextAsync(path2, "alert('b');");

        var hash1 = await WebResourceTrackingFile.ComputeHashAsync(path1);
        var hash2 = await WebResourceTrackingFile.ComputeHashAsync(path2);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public async Task WriteAsync_RespectsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var tracking = new WebResourceTrackingFile(
            Version: 1,
            EnvironmentUrl: "https://x",
            Solution: null,
            StripPrefix: false,
            PulledAt: DateTime.UtcNow,
            Resources: new Dictionary<string, TrackedResource>());

        var act = async () => await WebResourceTrackingFile.WriteAsync(_folder, tracking, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
