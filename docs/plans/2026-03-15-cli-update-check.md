# CLI Update Version Check Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a non-blocking update check to the CLI that notifies users when a newer version of PPDS is available on NuGet, with an explicit `ppds version --check` command for on-demand comparison.

**Architecture:** An `UpdateCheckService` Application Service queries the NuGet flat container API for available versions, caches results to `~/.ppds/update-check.json` for 24 hours, and compares against the current assembly version. At CLI startup, a non-blocking read of cached results displays a one-liner notification to stderr. The `ppds version --check` command forces a fresh fetch.

**Tech Stack:** .NET 8+, System.CommandLine, System.Text.Json, HttpClient, xUnit + FluentAssertions + Moq

**Issue:** [#564](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/564)

---

## Acceptance Criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | `ppds version` displays current CLI version, SDK version, .NET version, and platform | `VersionCommandTests.Create_ReturnsCommandWithCorrectName` |
| AC-02 | `ppds version --check` fetches latest versions from NuGet and displays current vs latest (stable and pre-release) | `UpdateCheckServiceTests.CheckAsync_ReturnsLatestStableAndPreRelease` |
| AC-03 | When current version is older than latest stable, suggests `dotnet tool update PPDS.Cli -g` | `UpdateCheckServiceTests.CheckAsync_StableAvailable_SuggestsPlainUpdate` |
| AC-04 | When user is on pre-release and newer pre-release exists (no newer stable), suggests command with `--prerelease` | `UpdateCheckServiceTests.CheckAsync_PreReleaseUser_NewerPreRelease_SuggestsPreReleaseFlag` |
| AC-05 | Check result is cached to `~/.ppds/update-check.json` for 24 hours | `UpdateCheckServiceTests.CheckAsync_CachesResult` |
| AC-06 | CLI startup reads cached result and shows one-liner notification to stderr if update available | `StartupUpdateNotifierTests.GetNotificationMessage_UpdateAvailable_ReturnsMessage` |
| AC-07 | Startup notification never blocks or slows CLI execution | `StartupUpdateNotifierTests.GetNotificationMessage_NeverThrows` |
| AC-08 | Network failures during check are handled gracefully (no crash, no output to stdout) | `UpdateCheckServiceTests.CheckAsync_NetworkError_ReturnsNull` |
| AC-09 | Startup notification suppressed when `--quiet` or `-q` is passed | `StartupUpdateNotifierTests.ShouldShow_QuietFlag_ReturnsFalse` |
| AC-10 | Version parsing handles SemVer with pre-release suffixes correctly | `NuGetVersionTests.Parse_ValidVersion_ExtractsComponents` |

---

## File Structure

### New Files

| File | Responsibility |
|------|----------------|
| `src/PPDS.Cli/Services/UpdateCheck/NuGetVersion.cs` | SemVer value type — parse, compare, detect pre-release |
| `src/PPDS.Cli/Services/UpdateCheck/IUpdateCheckService.cs` | Service interface for version checking |
| `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs` | Implementation — NuGet API, caching, comparison logic |
| `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckResult.cs` | Result records returned by service |
| `src/PPDS.Cli/Commands/VersionCommand.cs` | `ppds version [--check]` command |
| `tests/PPDS.Cli.Tests/Services/UpdateCheck/NuGetVersionTests.cs` | Version parsing/comparison tests |
| `tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs` | Service logic tests |
| `tests/PPDS.Cli.Tests/Commands/VersionCommandTests.cs` | Command structure tests |

### Modified Files

| File | Change |
|------|--------|
| `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs` | Add `UpdateCheck` error category |
| `src/PPDS.Cli/Services/ServiceRegistration.cs` | Register `IUpdateCheckService` |
| `src/PPDS.Cli/Program.cs` | Add `VersionCommand`, add startup notification call |

---

## Chunk 1: Version Parsing Foundation

### Task 1: NuGetVersion — SemVer Value Type

**Files:**
- Create: `src/PPDS.Cli/Services/UpdateCheck/NuGetVersion.cs`
- Test: `tests/PPDS.Cli.Tests/Services/UpdateCheck/NuGetVersionTests.cs`

- [ ] **Step 1: Write failing tests for NuGetVersion parsing**

```csharp
// tests/PPDS.Cli.Tests/Services/UpdateCheck/NuGetVersionTests.cs
using FluentAssertions;
using PPDS.Cli.Services.UpdateCheck;
using Xunit;

namespace PPDS.Cli.Tests.Services.UpdateCheck;

public class NuGetVersionTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0, "")]
    [InlineData("0.5.3-beta.1", 0, 5, 3, "beta.1")]
    [InlineData("2.10.0-alpha.0", 2, 10, 0, "alpha.0")]
    [InlineData("1.0.0-rc.1", 1, 0, 0, "rc.1")]
    public void Parse_ValidVersion_ExtractsComponents(
        string input, int major, int minor, int patch, string preRelease)
    {
        var version = NuGetVersion.Parse(input);

        version.Major.Should().Be(major);
        version.Minor.Should().Be(minor);
        version.Patch.Should().Be(patch);
        version.PreReleaseLabel.Should().Be(preRelease);
    }

    [Theory]
    [InlineData("0.5.3-beta.1")]
    [InlineData("1.0.0-alpha.0")]
    [InlineData("0.3.0-rc.1")]
    public void IsPreRelease_WithPreReleaseLabel_ReturnsTrue(string input)
    {
        NuGetVersion.Parse(input).IsPreRelease.Should().BeTrue();
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.6.0")]
    [InlineData("2.0.0")]
    public void IsPreRelease_StableVersion_ReturnsFalse(string input)
    {
        NuGetVersion.Parse(input).IsPreRelease.Should().BeFalse();
    }

    [Theory]
    [InlineData("0.5.0")]
    [InlineData("0.3.0")]
    [InlineData("1.1.0")]
    public void IsOddMinor_OddMinorVersion_ReturnsTrue(string input)
    {
        NuGetVersion.Parse(input).IsOddMinor.Should().BeTrue();
    }

    [Theory]
    [InlineData("0.6.0")]
    [InlineData("1.0.0")]
    [InlineData("2.2.0")]
    public void IsOddMinor_EvenMinorVersion_ReturnsFalse(string input)
    {
        NuGetVersion.Parse(input).IsOddMinor.Should().BeFalse();
    }

    [Fact]
    public void CompareTo_HigherMajor_IsGreater()
    {
        var v1 = NuGetVersion.Parse("2.0.0");
        var v2 = NuGetVersion.Parse("1.0.0");

        v1.CompareTo(v2).Should().BePositive();
    }

    [Fact]
    public void CompareTo_StableBeatsPreRelease_SameBase()
    {
        var stable = NuGetVersion.Parse("1.0.0");
        var preRelease = NuGetVersion.Parse("1.0.0-beta.1");

        stable.CompareTo(preRelease).Should().BePositive();
    }

    [Fact]
    public void CompareTo_HigherPreRelease_IsGreater()
    {
        var v1 = NuGetVersion.Parse("1.0.0-beta.2");
        var v2 = NuGetVersion.Parse("1.0.0-beta.1");

        v1.CompareTo(v2).Should().BePositive();
    }

    [Fact]
    public void CompareTo_CrossMinorPreRelease_StableWins()
    {
        // 0.6.0 stable > 0.5.3-beta.1 pre-release
        var stable = NuGetVersion.Parse("0.6.0");
        var preRelease = NuGetVersion.Parse("0.5.3-beta.1");

        stable.CompareTo(preRelease).Should().BePositive();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.0")]
    public void TryParse_InvalidInput_ReturnsFalse(string input)
    {
        NuGetVersion.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_ValidInput_ReturnsTrueAndVersion()
    {
        NuGetVersion.TryParse("1.2.3", out var version).Should().BeTrue();
        version!.Major.Should().Be(1);
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        var version = NuGetVersion.Parse("1.2.3-beta.1");
        version.ToString().Should().Be("1.2.3-beta.1");
    }

    [Fact]
    public void Parse_VersionWithBuildMetadata_IgnoresBuildMetadata()
    {
        // InformationalVersion includes "+commitHash" — must be stripped
        var version = NuGetVersion.Parse("1.2.3-beta.1+abc1234");

        version.Major.Should().Be(1);
        version.Minor.Should().Be(2);
        version.Patch.Should().Be(3);
        version.PreReleaseLabel.Should().Be("beta.1");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~NuGetVersionTests" --no-restore -v q`
Expected: Build error — `NuGetVersion` type does not exist

- [ ] **Step 3: Implement NuGetVersion**

```csharp
// src/PPDS.Cli/Services/UpdateCheck/NuGetVersion.cs
namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Lightweight SemVer version type for NuGet version comparison.
/// Handles major.minor.patch[-prerelease][+build] format.
/// </summary>
public sealed class NuGetVersion : IComparable<NuGetVersion>, IEquatable<NuGetVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>
    /// Pre-release label (e.g., "beta.1", "alpha.0"). Empty string for stable versions.
    /// </summary>
    public string PreReleaseLabel { get; }

    /// <summary>
    /// True if this version has a pre-release label.
    /// </summary>
    public bool IsPreRelease => !string.IsNullOrEmpty(PreReleaseLabel);

    /// <summary>
    /// True if this version has an odd minor number (PPDS convention: odd = pre-release line).
    /// </summary>
    public bool IsOddMinor => Minor % 2 != 0;

    private NuGetVersion(int major, int minor, int patch, string preReleaseLabel)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreReleaseLabel = preReleaseLabel;
    }

    /// <summary>
    /// Parses a SemVer version string. Strips build metadata (+hash) if present.
    /// </summary>
    /// <exception cref="FormatException">Thrown when the version string is invalid.</exception>
    public static NuGetVersion Parse(string version)
    {
        if (!TryParse(version, out var result))
            throw new FormatException($"Invalid version format: '{version}'");
        return result!;
    }

    /// <summary>
    /// Attempts to parse a SemVer version string.
    /// </summary>
    public static bool TryParse(string? version, out NuGetVersion? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(version))
            return false;

        // Strip build metadata (+commitHash)
        var buildIndex = version.IndexOf('+');
        if (buildIndex >= 0)
            version = version[..buildIndex];

        // Split pre-release label
        var preRelease = string.Empty;
        var dashIndex = version.IndexOf('-');
        if (dashIndex >= 0)
        {
            preRelease = version[(dashIndex + 1)..];
            version = version[..dashIndex];
        }

        // Parse major.minor.patch
        var parts = version.Split('.');
        if (parts.Length != 3)
            return false;

        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch))
            return false;

        result = new NuGetVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(NuGetVersion? other)
    {
        if (other is null) return 1;

        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;

        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;

        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        // Stable (no pre-release) > pre-release for same base version
        if (!IsPreRelease && other.IsPreRelease) return 1;
        if (IsPreRelease && !other.IsPreRelease) return -1;

        // Compare pre-release labels lexicographically
        return string.Compare(PreReleaseLabel, other.PreReleaseLabel, StringComparison.OrdinalIgnoreCase);
    }

    public bool Equals(NuGetVersion? other) =>
        other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) =>
        obj is NuGetVersion other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, PreReleaseLabel.ToLowerInvariant());

    public override string ToString() =>
        IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreReleaseLabel}" : $"{Major}.{Minor}.{Patch}";

    public static bool operator >(NuGetVersion left, NuGetVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(NuGetVersion left, NuGetVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(NuGetVersion left, NuGetVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(NuGetVersion left, NuGetVersion right) => left.CompareTo(right) <= 0;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~NuGetVersionTests" --no-restore -v q`
Expected: All tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Cli/Services/UpdateCheck/NuGetVersion.cs tests/PPDS.Cli.Tests/Services/UpdateCheck/NuGetVersionTests.cs
git commit -m "feat(cli): add NuGetVersion SemVer value type for update check (#564)"
```

---

### Task 2: Result Records and Service Interface

**Files:**
- Create: `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckResult.cs`
- Create: `src/PPDS.Cli/Services/UpdateCheck/IUpdateCheckService.cs`

- [ ] **Step 1: Create result records**

```csharp
// src/PPDS.Cli/Services/UpdateCheck/UpdateCheckResult.cs
using System.Text.Json.Serialization;

namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Result of a version update check against NuGet.
/// </summary>
public sealed record UpdateCheckResult
{
    /// <summary>Current installed version.</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>Latest stable version on NuGet (null if none found).</summary>
    public string? LatestStableVersion { get; init; }

    /// <summary>Latest pre-release version on NuGet (null if none found).</summary>
    public string? LatestPreReleaseVersion { get; init; }

    /// <summary>Whether a newer stable version is available.</summary>
    public bool StableUpdateAvailable { get; init; }

    /// <summary>Whether a newer pre-release version is available.</summary>
    public bool PreReleaseUpdateAvailable { get; init; }

    /// <summary>Suggested dotnet tool update command, or null if up-to-date.</summary>
    public string? UpdateCommand { get; init; }

    /// <summary>When this result was fetched from NuGet.</summary>
    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>True if any update (stable or pre-release) is available.</summary>
    [JsonIgnore]
    public bool UpdateAvailable => StableUpdateAvailable || PreReleaseUpdateAvailable;
}
```

- [ ] **Step 2: Create service interface**

```csharp
// src/PPDS.Cli/Services/UpdateCheck/IUpdateCheckService.cs
namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Checks for available updates by querying the NuGet package registry.
/// </summary>
/// <remarks>
/// Constitution A1/A2: Single code path for CLI, TUI, and RPC version checks.
/// </remarks>
public interface IUpdateCheckService
{
    /// <summary>
    /// Performs a fresh update check against the NuGet API.
    /// Caches the result for subsequent calls.
    /// </summary>
    /// <param name="currentVersion">The currently installed version string (from assembly).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Update check result, or null if the check failed.</returns>
    Task<UpdateCheckResult?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the cached update check result without making a network request.
    /// Returns null if no cached result exists or the cache is expired.
    /// </summary>
    Task<UpdateCheckResult?> GetCachedResultAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Cli/Services/UpdateCheck/UpdateCheckResult.cs src/PPDS.Cli/Services/UpdateCheck/IUpdateCheckService.cs
git commit -m "feat(cli): add UpdateCheckResult records and IUpdateCheckService interface (#564)"
```

---

## Chunk 2: Service Implementation

### Task 3: UpdateCheckService Implementation

**Files:**
- Create: `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs`
- Test: `tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs`
- Modify: `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs`

- [ ] **Step 1: Add UpdateCheck error codes**

Add to `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs`, after the `Plugin` class:

```csharp
    /// <summary>
    /// Update check errors.
    /// </summary>
    public static class UpdateCheck
    {
        /// <summary>Failed to reach NuGet API.</summary>
        public const string NetworkError = "UpdateCheck.NetworkError";

        /// <summary>NuGet API returned unexpected data.</summary>
        public const string ParseError = "UpdateCheck.ParseError";

        /// <summary>Cache file is corrupt or unreadable.</summary>
        public const string CacheError = "UpdateCheck.CacheError";
    }
```

- [ ] **Step 2: Write failing tests for UpdateCheckService**

```csharp
// tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using PPDS.Cli.Services.UpdateCheck;
using Xunit;

namespace PPDS.Cli.Tests.Services.UpdateCheck;

public class UpdateCheckServiceTests : IDisposable
{
    private readonly string _tempCachePath;
    private readonly Mock<ILogger<UpdateCheckService>> _mockLogger;

    public UpdateCheckServiceTests()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        _tempCachePath = Path.Combine(Path.GetTempPath(), $"ppds-test-{testId}", "update-check.json");
        _mockLogger = new Mock<ILogger<UpdateCheckService>>();
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_tempCachePath)!;
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* ignore */ }
        }
    }

    private static HttpClient CreateMockHttpClient(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
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
                Content = new StringContent(responseJson)
            });
        return new HttpClient(handler.Object);
    }

    private UpdateCheckService CreateService(HttpClient? httpClient = null)
    {
        return new UpdateCheckService(
            httpClient ?? CreateMockHttpClient("""{"versions":["0.5.0","0.5.1-beta.1","0.6.0"]}"""),
            _mockLogger.Object,
            _tempCachePath);
    }

    [Fact]
    public async Task CheckAsync_ReturnsLatestStableAndPreRelease()
    {
        var json = """{"versions":["0.4.0","0.5.0-beta.1","0.5.1-beta.2","0.6.0","0.7.0-alpha.0"]}""";
        var service = CreateService(CreateMockHttpClient(json));

        var result = await service.CheckAsync("0.4.0");

        result.Should().NotBeNull();
        result!.LatestStableVersion.Should().Be("0.6.0");
        result.LatestPreReleaseVersion.Should().Be("0.7.0-alpha.0");
    }

    [Fact]
    public async Task CheckAsync_CurrentIsLatest_NoUpdateAvailable()
    {
        var json = """{"versions":["0.4.0","0.5.0-beta.1","0.6.0"]}""";
        var service = CreateService(CreateMockHttpClient(json));

        var result = await service.CheckAsync("0.6.0");

        result.Should().NotBeNull();
        result!.StableUpdateAvailable.Should().BeFalse();
        result.UpdateCommand.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_StableAvailable_SuggestsPlainUpdate()
    {
        var json = """{"versions":["0.4.0","0.6.0"]}""";
        var service = CreateService(CreateMockHttpClient(json));

        var result = await service.CheckAsync("0.4.0");

        result.Should().NotBeNull();
        result!.StableUpdateAvailable.Should().BeTrue();
        result.UpdateCommand.Should().Be("dotnet tool update PPDS.Cli -g");
    }

    [Fact]
    public async Task CheckAsync_PreReleaseUser_NewerPreRelease_SuggestsPreReleaseFlag()
    {
        // No stable version higher than current — only pre-release updates available
        var json = """{"versions":["0.4.0","0.5.0-beta.1","0.5.0-beta.2"]}""";
        var service = CreateService(CreateMockHttpClient(json));

        // User is on pre-release, newer pre-release exists, no newer stable
        var result = await service.CheckAsync("0.5.0-beta.1");

        result.Should().NotBeNull();
        result!.PreReleaseUpdateAvailable.Should().BeTrue();
        result.StableUpdateAvailable.Should().BeFalse();
        // Should suggest --prerelease since user is on pre-release line and no stable upgrade path
        result.UpdateCommand.Should().Contain("--prerelease");
    }

    [Fact]
    public async Task CheckAsync_PreReleaseUser_StableAvailable_SuggestsStableUpdate()
    {
        // User on 0.5.x-beta, stable 0.6.0 available — suggest plain update
        var json = """{"versions":["0.5.0-beta.1","0.6.0"]}""";
        var service = CreateService(CreateMockHttpClient(json));

        var result = await service.CheckAsync("0.5.0-beta.1");

        result.Should().NotBeNull();
        result!.StableUpdateAvailable.Should().BeTrue();
        // Stable update takes priority — no --prerelease needed
        result.UpdateCommand.Should().Be("dotnet tool update PPDS.Cli -g");
    }

    [Fact]
    public async Task CheckAsync_CachesResult()
    {
        var service = CreateService();

        await service.CheckAsync("0.4.0");

        File.Exists(_tempCachePath).Should().BeTrue();
        var cached = await service.GetCachedResultAsync();
        cached.Should().NotBeNull();
    }

    [Fact]
    public async Task GetCachedResultAsync_NoCacheFile_ReturnsNull()
    {
        var service = CreateService();

        var result = await service.GetCachedResultAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCachedResultAsync_ExpiredCache_ReturnsNull()
    {
        var service = CreateService();
        // Write a cache file with old timestamp
        Directory.CreateDirectory(Path.GetDirectoryName(_tempCachePath)!);
        var oldResult = new UpdateCheckResult
        {
            CurrentVersion = "0.4.0",
            LatestStableVersion = "0.6.0",
            StableUpdateAvailable = true,
            CheckedAt = DateTimeOffset.UtcNow.AddHours(-25) // Expired
        };
        await File.WriteAllTextAsync(_tempCachePath, JsonSerializer.Serialize(oldResult));

        var result = await service.GetCachedResultAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_NetworkError_ReturnsNull()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var service = CreateService(new HttpClient(handler.Object));

        var result = await service.CheckAsync("0.4.0");

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_NonSuccessStatusCode_ReturnsNull()
    {
        var service = CreateService(CreateMockHttpClient("{}", HttpStatusCode.NotFound));

        var result = await service.CheckAsync("0.4.0");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCachedResultAsync_CorruptCacheFile_ReturnsNull()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tempCachePath)!);
        await File.WriteAllTextAsync(_tempCachePath, "not valid json{{{");

        var service = CreateService();
        var result = await service.GetCachedResultAsync();

        result.Should().BeNull();
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~UpdateCheckServiceTests" --no-restore -v q`
Expected: Build error — `UpdateCheckService` class does not exist

- [ ] **Step 4: Implement UpdateCheckService**

```csharp
// src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Checks for available PPDS CLI updates via the NuGet flat container API.
/// Caches results to avoid repeated network calls.
/// </summary>
/// <remarks>
/// When created via <see cref="Create"/>, this instance owns the HttpClient
/// and must be disposed. When created via constructor with an injected HttpClient,
/// the caller owns the HttpClient lifetime.
/// </remarks>
public sealed class UpdateCheckService : IUpdateCheckService, IDisposable
{
    /// <summary>NuGet flat container API URL for the PPDS.Cli package.</summary>
    internal const string NuGetIndexUrl = "https://api.nuget.org/v3-flatcontainer/ppds.cli/index.json";

    /// <summary>NuGet package ID used in update commands.</summary>
    internal const string PackageId = "PPDS.Cli";

    /// <summary>Cache duration before a fresh check is needed. Shared with StartupUpdateNotifier.</summary>
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<UpdateCheckService>? _logger;
    private readonly string _cachePath;

    /// <summary>
    /// Creates a new UpdateCheckService with an externally-managed HttpClient.
    /// The caller owns the HttpClient lifetime.
    /// </summary>
    /// <param name="httpClient">HTTP client for NuGet API requests.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cachePath">
    /// Path to the cache file. Defaults to ~/.ppds/update-check.json.
    /// </param>
    public UpdateCheckService(
        HttpClient httpClient,
        ILogger<UpdateCheckService>? logger = null,
        string? cachePath = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = false;
        _logger = logger;
        _cachePath = cachePath ?? DefaultCachePath;
    }

    private UpdateCheckService(
        HttpClient httpClient,
        bool ownsHttpClient,
        ILogger<UpdateCheckService>? logger)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _logger = logger;
        _cachePath = DefaultCachePath;
    }

    /// <summary>
    /// Default cache file path: ~/.ppds/update-check.json
    /// </summary>
    internal static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ppds",
        "update-check.json");

    /// <summary>
    /// Factory method for creating a service with default HttpClient configuration.
    /// The returned instance owns the HttpClient and must be disposed (Constitution R1).
    /// Used by VersionCommand and StartupUpdateNotifier where full DI is not available.
    /// </summary>
    public static UpdateCheckService Create(ILogger<UpdateCheckService>? logger = null)
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return new UpdateCheckService(httpClient, ownsHttpClient: true, logger);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    public async Task<UpdateCheckResult?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var versions = await FetchVersionsAsync(cancellationToken);
            if (versions == null || versions.Count == 0)
                return null;

            var result = BuildResult(currentVersion, versions);
            await WriteCacheAsync(result, cancellationToken);
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogDebug(ex, "Update check network error");
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger?.LogDebug(ex, "Update check cancelled or timed out");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Update check failed");
            return null;
        }
    }

    public async Task<UpdateCheckResult?> GetCachedResultAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var json = await File.ReadAllTextAsync(_cachePath, cancellationToken);
            var result = JsonSerializer.Deserialize<UpdateCheckResult>(json, JsonOptions);

            if (result == null)
                return null;

            // Check if cache has expired
            if (DateTimeOffset.UtcNow - result.CheckedAt > CacheDuration)
                return null;

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to read update check cache");
            return null;
        }
    }

    private async Task<List<NuGetVersion>?> FetchVersionsAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(NuGetIndexUrl, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger?.LogDebug("NuGet API returned {StatusCode}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var index = JsonSerializer.Deserialize<NuGetVersionIndex>(json, JsonOptions);

        if (index?.Versions == null)
            return null;

        var parsed = new List<NuGetVersion>();
        foreach (var v in index.Versions)
        {
            if (NuGetVersion.TryParse(v, out var version))
                parsed.Add(version!);
        }

        return parsed;
    }

    internal static UpdateCheckResult BuildResult(string currentVersionString, List<NuGetVersion> allVersions)
    {
        NuGetVersion.TryParse(currentVersionString, out var currentVersion);

        // Find latest stable (no pre-release label)
        var latestStable = allVersions
            .Where(v => !v.IsPreRelease)
            .OrderDescending()
            .FirstOrDefault();

        // Find latest pre-release
        var latestPreRelease = allVersions
            .Where(v => v.IsPreRelease)
            .OrderDescending()
            .FirstOrDefault();

        var stableAvailable = currentVersion != null && latestStable != null && latestStable > currentVersion;
        var preReleaseAvailable = currentVersion != null && latestPreRelease != null && latestPreRelease > currentVersion;

        // Build update command
        string? updateCommand = null;
        if (stableAvailable)
        {
            // Stable update available — plain command (works for both stable and pre-release users)
            updateCommand = $"dotnet tool update {PackageId} -g";
        }
        else if (preReleaseAvailable && currentVersion?.IsPreRelease == true)
        {
            // User is on pre-release, newer pre-release available, no newer stable
            updateCommand = $"dotnet tool update {PackageId} -g --prerelease";
        }

        return new UpdateCheckResult
        {
            CurrentVersion = currentVersionString,
            LatestStableVersion = latestStable?.ToString(),
            LatestPreReleaseVersion = latestPreRelease?.ToString(),
            StableUpdateAvailable = stableAvailable,
            PreReleaseUpdateAvailable = preReleaseAvailable,
            UpdateCommand = updateCommand,
            CheckedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task WriteCacheAsync(UpdateCheckResult result, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(result, JsonOptions);
            await File.WriteAllTextAsync(_cachePath, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to write update check cache");
        }
    }

    /// <summary>
    /// Internal record for deserializing the NuGet flat container API response.
    /// </summary>
    private sealed record NuGetVersionIndex
    {
        public List<string>? Versions { get; init; }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~UpdateCheckServiceTests" --no-restore -v q`
Expected: All tests PASS

- [ ] **Step 6: Commit**

```bash
git add src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs
git commit -m "feat(cli): implement UpdateCheckService with NuGet API and caching (#564)"
```

---

## Chunk 3: CLI Command and Startup Integration

### Task 4: Version Command

**Files:**
- Create: `src/PPDS.Cli/Commands/VersionCommand.cs`
- Test: `tests/PPDS.Cli.Tests/Commands/VersionCommandTests.cs`
- Modify: `src/PPDS.Cli/Program.cs`

- [ ] **Step 1: Write failing tests for VersionCommand structure**

```csharp
// tests/PPDS.Cli.Tests/Commands/VersionCommandTests.cs
using System.CommandLine;
using FluentAssertions;
using PPDS.Cli.Commands;
using Xunit;

namespace PPDS.Cli.Tests.Commands;

public class VersionCommandTests
{
    private readonly Command _command;

    public VersionCommandTests()
    {
        _command = VersionCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        _command.Name.Should().Be("version");
    }

    [Fact]
    public void Create_HasCheckOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--check");
        option.Should().NotBeNull();
    }

    [Fact]
    public void Create_CheckOptionIsOptional()
    {
        var option = _command.Options.First(o => o.Name == "--check");
        option.Required.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~VersionCommandTests" --no-restore -v q`
Expected: Build error — `VersionCommand` does not exist

- [ ] **Step 3: Implement VersionCommand**

The command creates an `UpdateCheckService` via a factory method that encapsulates the HttpClient setup.
This keeps the command as a thin wrapper (Constitution A1) while avoiding full DI since the version
command doesn't need a Dataverse connection or profile resolution. The `UpdateCheckService` contains
all the business logic (A2).

```csharp
// src/PPDS.Cli/Commands/VersionCommand.cs
using System.CommandLine;
using System.Runtime.InteropServices;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.UpdateCheck;

namespace PPDS.Cli.Commands;

/// <summary>
/// The 'ppds version' command — shows version info and optionally checks for updates.
/// </summary>
public static class VersionCommand
{
    /// <summary>
    /// Creates the 'version' command with optional --check flag.
    /// </summary>
    public static Command Create()
    {
        var checkOption = new Option<bool>("--check", "Check NuGet for available updates");

        var command = new Command("version", "Show version information and check for updates")
        {
            checkOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var check = parseResult.GetValue(checkOption);
            return await ExecuteAsync(check, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(bool check, CancellationToken cancellationToken)
    {
        // Always show version info (thin UI wrapper — no business logic here)
        var cliVersion = ErrorOutput.Version;
        var sdkVersion = ErrorOutput.SdkVersion;
        var runtimeVersion = Environment.Version.ToString();
        var platform = RuntimeInformation.OSDescription;

        Console.Error.WriteLine($"PPDS CLI v{cliVersion}");
        Console.Error.WriteLine($"SDK:      v{sdkVersion}");
        Console.Error.WriteLine($".NET:     {runtimeVersion}");
        Console.Error.WriteLine($"Platform: {platform}");

        if (!check)
            return ExitCodes.Success;

        // Delegate to Application Service for update check logic (Constitution A1/A2)
        Console.Error.WriteLine();
        Console.Error.WriteLine("Checking for updates...");

        using var service = UpdateCheckService.Create();
        var result = await service.CheckAsync(cliVersion, cancellationToken);

        if (result == null)
        {
            Console.Error.WriteLine("Could not check for updates. Try again later.");
            return ExitCodes.Success; // Non-fatal
        }

        Console.Error.WriteLine();

        if (result.LatestStableVersion != null)
            Console.Error.WriteLine($"Latest stable:      {result.LatestStableVersion}");

        if (result.LatestPreReleaseVersion != null)
            Console.Error.WriteLine($"Latest pre-release: {result.LatestPreReleaseVersion}");

        Console.Error.WriteLine();

        if (result.UpdateAvailable)
        {
            Console.Error.WriteLine($"Update available! Run: {result.UpdateCommand}");
        }
        else
        {
            Console.Error.WriteLine("You are up to date.");
        }

        return ExitCodes.Success;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~VersionCommandTests" --no-restore -v q`
Expected: All tests PASS

- [ ] **Step 5: Add VersionCommand to Program.cs**

In `src/PPDS.Cli/Program.cs`, add after `rootCommand.Subcommands.Add(InteractiveCommand.Create());`:

```csharp
        rootCommand.Subcommands.Add(VersionCommand.Create());
```

Also add the using statement at the top if not already present (it should be — `DocsCommand` is in the same namespace).

And add `"version"` to `SkipVersionHeaderArgs`:

```csharp
    private static readonly HashSet<string> SkipVersionHeaderArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "--help", "-h", "-?", "--version", "version"
    };
```

- [ ] **Step 6: Commit**

```bash
git add src/PPDS.Cli/Commands/VersionCommand.cs tests/PPDS.Cli.Tests/Commands/VersionCommandTests.cs src/PPDS.Cli/Program.cs
git commit -m "feat(cli): add 'ppds version [--check]' command (#564)"
```

---

### Task 5: Startup Update Notification

**Files:**
- Create: `src/PPDS.Cli/Infrastructure/StartupUpdateNotifier.cs`
- Test: `tests/PPDS.Cli.Tests/Infrastructure/StartupUpdateNotifierTests.cs`
- Modify: `src/PPDS.Cli/Program.cs`

- [ ] **Step 1: Write failing tests for StartupUpdateNotifier**

```csharp
// tests/PPDS.Cli.Tests/Infrastructure/StartupUpdateNotifierTests.cs
using System.Text.Json;
using FluentAssertions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.UpdateCheck;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

public class StartupUpdateNotifierTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cachePath;

    public StartupUpdateNotifierTests()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        _tempDir = Path.Combine(Path.GetTempPath(), $"ppds-test-{testId}");
        _cachePath = Path.Combine(_tempDir, "update-check.json");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* ignore */ }
        }
    }

    [Fact]
    public void GetNotificationMessage_UpdateAvailable_ReturnsMessage()
    {
        WriteCacheFile(new UpdateCheckResult
        {
            CurrentVersion = "0.4.0",
            LatestStableVersion = "0.6.0",
            StableUpdateAvailable = true,
            UpdateCommand = "dotnet tool update PPDS.Cli -g",
            CheckedAt = DateTimeOffset.UtcNow
        });

        var message = StartupUpdateNotifier.GetNotificationMessage(_cachePath);

        message.Should().NotBeNull();
        message.Should().Contain("0.6.0");
        message.Should().Contain("dotnet tool update");
    }

    [Fact]
    public void GetNotificationMessage_NoUpdate_ReturnsNull()
    {
        WriteCacheFile(new UpdateCheckResult
        {
            CurrentVersion = "0.6.0",
            LatestStableVersion = "0.6.0",
            StableUpdateAvailable = false,
            CheckedAt = DateTimeOffset.UtcNow
        });

        var message = StartupUpdateNotifier.GetNotificationMessage(_cachePath);

        message.Should().BeNull();
    }

    [Fact]
    public void GetNotificationMessage_NoCacheFile_ReturnsNull()
    {
        var message = StartupUpdateNotifier.GetNotificationMessage(
            Path.Combine(_tempDir, "nonexistent.json"));

        message.Should().BeNull();
    }

    [Fact]
    public void GetNotificationMessage_ExpiredCache_ReturnsNull()
    {
        WriteCacheFile(new UpdateCheckResult
        {
            CurrentVersion = "0.4.0",
            LatestStableVersion = "0.6.0",
            StableUpdateAvailable = true,
            UpdateCommand = "dotnet tool update PPDS.Cli -g",
            CheckedAt = DateTimeOffset.UtcNow.AddHours(-25) // Expired
        });

        var message = StartupUpdateNotifier.GetNotificationMessage(_cachePath);

        message.Should().BeNull();
    }

    [Fact]
    public void GetNotificationMessage_CorruptFile_ReturnsNull()
    {
        File.WriteAllText(_cachePath, "corrupt{{{json");

        var message = StartupUpdateNotifier.GetNotificationMessage(_cachePath);

        message.Should().BeNull();
    }

    [Fact]
    public void GetNotificationMessage_NeverThrows()
    {
        // Even with totally broken path, should return null, not throw
        var act = () => StartupUpdateNotifier.GetNotificationMessage(
            "/nonexistent/path/that/cant/exist/file.json");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("--quiet")]
    [InlineData("-q")]
    public void ShouldShow_QuietFlag_ReturnsFalse(string flag)
    {
        StartupUpdateNotifier.ShouldShow(new[] { "data", "export", flag }).Should().BeFalse();
    }

    [Fact]
    public void ShouldShow_NoQuietFlag_ReturnsTrue()
    {
        StartupUpdateNotifier.ShouldShow(new[] { "data", "export" }).Should().BeTrue();
    }

    private void WriteCacheFile(UpdateCheckResult result)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_cachePath, json);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~StartupUpdateNotifierTests" --no-restore -v q`
Expected: Build error — `StartupUpdateNotifier` does not exist

- [ ] **Step 3: Implement StartupUpdateNotifier**

```csharp
// src/PPDS.Cli/Infrastructure/StartupUpdateNotifier.cs
using System.Text.Json;
using PPDS.Cli.Services.UpdateCheck;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Reads cached update check results at startup and returns a notification message
/// if an update is available. Designed to never throw or block.
/// </summary>
public static class StartupUpdateNotifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Determines whether the startup update notification should be shown,
    /// based on CLI arguments. Suppressed when --quiet or -q is present.
    /// </summary>
    public static bool ShouldShow(string[] args)
    {
        return !args.Any(a => a is "--quiet" or "-q");
    }

    /// <summary>
    /// Reads the cached update check result and returns a notification message
    /// if an update is available. Returns null if no update, cache expired, or any error.
    /// This method is synchronous and designed to never throw.
    /// </summary>
    public static string? GetNotificationMessage(string? cachePath = null)
    {
        try
        {
            cachePath ??= UpdateCheckService.DefaultCachePath;

            if (!File.Exists(cachePath))
                return null;

            var json = File.ReadAllText(cachePath);
            var result = JsonSerializer.Deserialize<UpdateCheckResult>(json, JsonOptions);

            if (result == null || !result.UpdateAvailable)
                return null;

            // Check if cache has expired (uses same duration as UpdateCheckService)
            if (DateTimeOffset.UtcNow - result.CheckedAt > UpdateCheckService.CacheDuration)
                return null;

            var version = result.StableUpdateAvailable
                ? result.LatestStableVersion
                : result.LatestPreReleaseVersion;

            return $"Update available: {version} (run: {result.UpdateCommand})";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fires a background update check that writes to the cache file for next startup.
    /// Best-effort: does not block, does not throw. The background task is untracked
    /// because the CLI process may exit before it completes — that's acceptable since
    /// the cache will be refreshed on the next run.
    /// </summary>
    public static void RefreshCacheInBackground(string currentVersion)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var service = UpdateCheckService.Create();
                await service.CheckAsync(currentVersion);
            }
            catch
            {
                // Swallow all errors — this is best-effort background work
            }
        });
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~StartupUpdateNotifierTests" --no-restore -v q`
Expected: All tests PASS

- [ ] **Step 5: Integrate startup notification into Program.cs**

In `src/PPDS.Cli/Program.cs`, add after the `ErrorOutput.WriteVersionHeader()` call (inside the `if` block at ~line 50):

```csharp
        // Show cached update notification (non-blocking, reads local file only)
        // Suppressed when --quiet or -q is passed
        if (StartupUpdateNotifier.ShouldShow(args))
        {
            var updateMessage = StartupUpdateNotifier.GetNotificationMessage();
            if (updateMessage != null)
            {
                Console.Error.WriteLine(updateMessage);
            }
        }

        // Fire-and-forget background cache refresh for next startup
        StartupUpdateNotifier.RefreshCacheInBackground(ErrorOutput.Version);
```

Add the `using PPDS.Cli.Infrastructure;` statement if not already present.

- [ ] **Step 6: Commit**

```bash
git add src/PPDS.Cli/Infrastructure/StartupUpdateNotifier.cs tests/PPDS.Cli.Tests/Infrastructure/StartupUpdateNotifierTests.cs src/PPDS.Cli/Program.cs
git commit -m "feat(cli): add non-blocking startup update notification (#564)"
```

---

### Task 6: DI Registration and Final Wiring

**Files:**
- Modify: `src/PPDS.Cli/Services/ServiceRegistration.cs`

- [ ] **Step 1: Register UpdateCheckService in DI**

In `src/PPDS.Cli/Services/ServiceRegistration.cs`, add at the end of `AddCliApplicationServices` (before `return services;`):

```csharp
        // Update check service — singleton since it manages its own cache file.
        // Uses Create() factory which owns the HttpClient (R1 compliance).
        services.AddSingleton<IUpdateCheckService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<UpdateCheckService>>();
            return UpdateCheckService.Create(logger);
        });
```

Add the using statement:
```csharp
using PPDS.Cli.Services.UpdateCheck;
```

- [ ] **Step 2: Build the solution to verify compilation**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore -v q`
Expected: Build succeeded

- [ ] **Step 3: Run all update check tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~UpdateCheck|FullyQualifiedName~VersionCommand|FullyQualifiedName~StartupUpdateNotifier" -v q`
Expected: All tests PASS

- [ ] **Step 4: Commit**

```bash
git add src/PPDS.Cli/Services/ServiceRegistration.cs
git commit -m "feat(cli): register UpdateCheckService in DI container (#564)"
```

---

### Task 7: Verify Full Build and Existing Tests

- [ ] **Step 1: Build entire solution**

Run: `dotnet build PPDS.sln --no-restore -v q`
Expected: Build succeeded with no errors

- [ ] **Step 2: Run all CLI unit tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "Category!=Integration" -v q`
Expected: All tests PASS (both new and existing)

- [ ] **Step 3: Manual smoke test**

Run: `ppds version` — should show version info
Run: `ppds version --check` — should check NuGet and display update status

- [ ] **Step 4: Final commit if any cleanup needed**

```bash
git commit -m "chore: verify full build and tests pass for update check feature (#564)"
```

---

## Design Decisions

### Why NuGet flat container API?
The flat container API (`/v3-flatcontainer/{id}/index.json`) returns a simple JSON array of version strings. It requires no authentication, no API key, and no complex query parameters. It's the lightest-weight NuGet endpoint available and perfect for a version-check use case.

### Why file-based caching instead of in-memory?
The CLI is a short-lived process — in-memory caching wouldn't persist across invocations. File-based caching in `~/.ppds/` follows the existing pattern used by `QueryHistoryService` and ensures the 24-hour TTL works across process lifetimes.

### Why synchronous cache read at startup?
The startup notification reads a single small JSON file from local disk. Making this async would add complexity (async Main, await before parse) with no measurable benefit. The file read is sub-millisecond. The network refresh is fire-and-forget in the background.

### Why no IHttpClientFactory?
The existing codebase uses direct `HttpClient` instantiation (see `ConnectionService`). Adding `IHttpClientFactory` would be an unrelated infrastructure change. The update check creates one `HttpClient` per check (infrequent — at most once per 24h), so socket exhaustion is not a concern.

### Why lexicographic pre-release comparison?
SemVer spec says numeric pre-release identifiers should be compared as integers (so `beta.10 > beta.2`). Our implementation uses lexicographic comparison, which would order `beta.10 < beta.2`. This is acceptable because PPDS pre-release labels use single-digit numeric segments (e.g., `alpha.0`, `beta.1`). If multi-digit segments are ever needed, upgrade to proper SemVer numeric comparison.

### Why stable update command takes priority?
When a user is on pre-release `0.5.x-beta` and stable `0.6.0` is available, we suggest the plain `dotnet tool update` command (without `--prerelease`). This aligns with the PPDS odd/even versioning convention: odd-minor is the pre-release line leading to the next even-minor stable release. The user's intent is almost always to reach stable.
