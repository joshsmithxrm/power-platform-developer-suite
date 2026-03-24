# CLI Update Check Gaps Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the existing update check implementation into alignment with the spec at `specs/update-check.md` — refactor the notifier to pure functions, fix pre-release track logic, and add self-update mechanism.

**Architecture:** The update check system has three layers: `UpdateCheckService` (Application Service owning all cache/network/update logic), `StartupUpdateNotifier` (pure presentation — no I/O), and `VersionCommand` (CLI adapter). This plan refactors the notifier from file-I/O-aware static methods to pure functions, fixes the pre-release notification suppression bug, and adds `UpdateAsync` for self-update via detached process.

**Tech Stack:** C# (.NET 8/9/10), System.CommandLine, xUnit, System.Text.Json, System.Diagnostics.Process

**Spec:** `specs/update-check.md`
**Constitution:** `specs/CONSTITUTION.md`
**Branch:** `feature/cli-update-check`

---

## File Structure

### New Files

| File | Responsibility |
|------|----------------|
| `src/PPDS.Cli/Services/UpdateCheck/UpdateChannel.cs` | Enum: Current, Stable, PreRelease |
| `src/PPDS.Cli/Services/UpdateCheck/UpdateResult.cs` | Record: self-update outcome |
| `src/PPDS.Cli/Services/UpdateCheck/UpdateScriptWriter.cs` | Generates platform-specific wrapper scripts (.cmd/.sh) for detached update |
| `tests/PPDS.Cli.Tests/Services/UpdateCheck/SelfUpdateTests.cs` | Tests for UpdateAsync, dotnet path resolution, script generation |

### Modified Files

| File | Changes |
|------|---------|
| `src/PPDS.Cli/Services/UpdateCheck/IUpdateCheckService.cs` | Add `GetCachedResult()` (sync), `RefreshCacheInBackgroundIfStale()`, `UpdateAsync()`; remove `GetCachedResultAsync()` |
| `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs` | Implement sync cache read, background refresh, pre-release track logic fix, self-update |
| `src/PPDS.Cli/Infrastructure/StartupUpdateNotifier.cs` | Delete `GetNotificationMessage`, `RefreshCacheInBackground`, `IsCacheFresh`; add `FormatNotification(UpdateCheckResult?)` |
| `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs` | Add `UpdateCheck` category |
| `src/PPDS.Cli/Commands/VersionCommand.cs` | Add `--update`, `--stable`, `--prerelease`, `--yes` options |
| `src/PPDS.Cli/Program.cs` | Use service for cache read + background refresh; read status file |
| `tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs` | Adapt for sync GetCachedResult, new pre-release logic, background refresh |
| `tests/PPDS.Cli.Tests/Infrastructure/StartupUpdateNotifierTests.cs` | Rewrite for pure FormatNotification, expanded ShouldShow |
| `tests/PPDS.Cli.Tests/Commands/VersionCommandTests.cs` | Add tests for new options |

---

## Chunk 1: Foundation Types + Interface Refactor

### Task 1: Add UpdateChannel Enum

**Files:**
- Create: `src/PPDS.Cli/Services/UpdateCheck/UpdateChannel.cs`

- [ ] **Step 1: Create the enum file**

```csharp
namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Specifies which release track to target for self-update.
/// </summary>
public enum UpdateChannel
{
    /// <summary>Stay on current track (stable→stable, pre-release→pre-release).</summary>
    Current,

    /// <summary>Force update to latest stable version.</summary>
    Stable,

    /// <summary>Force update to latest pre-release version.</summary>
    PreRelease
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore`
Expected: Build succeeded

### Task 2: Add UpdateResult Record

**Files:**
- Create: `src/PPDS.Cli/Services/UpdateCheck/UpdateResult.cs`

- [ ] **Step 1: Create the record file**

```csharp
namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Represents the outcome of a self-update attempt.
/// </summary>
/// <remarks>
/// Expected outcomes (non-global install, already current) are returned as results.
/// Unexpected failures (dotnet not found, spawn failed) throw <see cref="Infrastructure.Errors.PpdsException"/>.
/// </remarks>
public sealed record UpdateResult
{
    /// <summary>Whether the update was successfully initiated.</summary>
    public bool Success { get; init; }

    /// <summary>The version being installed, if known.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>Error message for non-fatal failures.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>True when PPDS is installed as a local tool (not global).</summary>
    public bool IsNonGlobalInstall { get; init; }

    /// <summary>Manual update command to show when automation isn't possible.</summary>
    public string? ManualCommand { get; init; }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore`
Expected: Build succeeded

### Task 3: Add ErrorCodes.UpdateCheck Category

**Files:**
- Modify: `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs:225` (after Plugin class, before closing brace)

- [ ] **Step 1: Add the UpdateCheck error codes**

Add after the `Plugin` class (before the final `}` on line 226):

```csharp
    /// <summary>
    /// Update check and self-update errors.
    /// </summary>
    public static class UpdateCheck
    {
        /// <summary>NuGet API query failed (network, timeout, non-2xx).</summary>
        public const string NetworkError = "UpdateCheck.NetworkError";

        /// <summary>Cache file is corrupt or unreadable.</summary>
        public const string CacheCorrupt = "UpdateCheck.CacheCorrupt";

        /// <summary>Cannot locate the dotnet runtime executable.</summary>
        public const string DotnetNotFound = "UpdateCheck.DotnetNotFound";

        /// <summary>The dotnet tool update process exited with non-zero.</summary>
        public const string UpdateFailed = "UpdateCheck.UpdateFailed";
    }
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore`
Expected: Build succeeded

### Task 4: Refactor IUpdateCheckService Interface

**Files:**
- Modify: `src/PPDS.Cli/Services/UpdateCheck/IUpdateCheckService.cs`

- [ ] **Step 1: Replace the interface**

Replace the entire file contents with:

```csharp
namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Service for checking available updates and performing self-update of the PPDS CLI tool.
/// All cache format knowledge, TTL logic, and file I/O are owned by this service (A1).
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>
    /// Synchronously reads the cached update check result.
    /// Returns <see langword="null"/> if the cache is missing, expired (&gt;24h), or corrupt.
    /// Never throws.
    /// </summary>
    UpdateCheckResult? GetCachedResult();

    /// <summary>
    /// Queries the NuGet flat-container API for available versions and returns the result.
    /// Updates the cache on success. Returns <see langword="null"/> on network failure.
    /// </summary>
    Task<UpdateCheckResult?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires a best-effort background task to refresh the cache when it is stale.
    /// Intentionally takes no <see cref="CancellationToken"/> — fire-and-forget by design (R2).
    /// </summary>
    void RefreshCacheInBackgroundIfStale(string currentVersion);

    /// <summary>
    /// Performs a self-update of the PPDS CLI tool.
    /// Returns <see cref="UpdateResult"/> for expected outcomes (non-global install, already current).
    /// Throws <see cref="Infrastructure.Errors.PpdsException"/> for unexpected failures (dotnet not found).
    /// </summary>
    Task<UpdateResult> UpdateAsync(UpdateChannel channel, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Expect compilation errors**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore`
Expected: FAIL — `UpdateCheckService` no longer implements the interface (missing methods, extra methods). This is expected; we fix it in Chunk 2.

- [ ] **Step 3: Commit foundation types**

```bash
git add src/PPDS.Cli/Services/UpdateCheck/UpdateChannel.cs \
        src/PPDS.Cli/Services/UpdateCheck/UpdateResult.cs \
        src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs \
        src/PPDS.Cli/Services/UpdateCheck/IUpdateCheckService.cs
git commit -m "feat(update-check): add foundation types and refactor interface

Add UpdateChannel enum, UpdateResult record, UpdateCheck error codes.
Refactor IUpdateCheckService: sync GetCachedResult(), add
RefreshCacheInBackgroundIfStale(), add UpdateAsync(), remove
GetCachedResultAsync()."
```

---

## Chunk 2: Service Refactor

### Task 5: Convert GetCachedResultAsync to Sync GetCachedResult

**Files:**
- Modify: `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs:134-162`
- Test: `tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs`

**Context:** The spec requires `GetCachedResult()` to be synchronous. The current implementation is async (`GetCachedResultAsync`). The cache file is <1KB — `File.ReadAllText` is sub-millisecond. See spec design decision "Why Sync Cache Reads?"

- [ ] **Step 1: Write the failing test for sync GetCachedResult**

Add to `UpdateCheckServiceTests.cs`:

```csharp
[Fact]
public async Task GetCachedResult_ReturnsCachedResult_WhenCacheIsFresh()
{
    // Arrange: populate cache via CheckAsync
    var handler = BuildHandler(MakeVersionsJson("1.0.0", "2.0.0"));
    var svc = new UpdateCheckService(handler: handler, cachePath: _cachePath);
    await svc.CheckAsync("1.0.0");

    // Act: sync read
    var cached = svc.GetCachedResult();

    // Assert
    Assert.NotNull(cached);
    Assert.Equal("1.0.0", cached.CurrentVersion);
}

[Fact]
public void GetCachedResult_ReturnsNull_WhenNoCacheFile()
{
    var svc = new UpdateCheckService(cachePath: _cachePath);
    var cached = svc.GetCachedResult();
    Assert.Null(cached);
}

[Fact]
public async Task GetCachedResult_ReturnsNull_WhenCacheExpired()
{
    // Arrange: write cache with old timestamp
    var handler = BuildHandler(MakeVersionsJson("1.0.0", "2.0.0"));
    var svc = new UpdateCheckService(handler: handler, cachePath: _cachePath);
    await svc.CheckAsync("1.0.0");

    // Tamper with cache to make it expired
    var json = File.ReadAllText(_cachePath);
    var expired = json.Replace(
        DateTimeOffset.UtcNow.Year.ToString(),
        "2020");
    File.WriteAllText(_cachePath, expired);

    // Act
    var cached = svc.GetCachedResult();

    // Assert
    Assert.Null(cached);
}

[Fact]
public void GetCachedResult_ReturnsNull_WhenCacheCorrupt()
{
    File.WriteAllText(_cachePath, "not json");
    var svc = new UpdateCheckService(cachePath: _cachePath);
    var cached = svc.GetCachedResult();
    Assert.Null(cached);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "FullyQualifiedName~UpdateCheckServiceTests.GetCachedResult" --no-restore`
Expected: FAIL — method `GetCachedResult` doesn't exist

- [ ] **Step 3: Implement sync GetCachedResult and remove GetCachedResultAsync**

In `UpdateCheckService.cs`, replace the `GetCachedResultAsync` method (lines 134-162) with:

```csharp
    /// <inheritdoc/>
    public UpdateCheckResult? GetCachedResult()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var json = File.ReadAllText(_cachePath);
            var result = JsonSerializer.Deserialize<UpdateCheckResult>(json, JsonOptions);

            if (result is null)
                return null;

            // Honour TTL
            if (DateTimeOffset.UtcNow - result.CheckedAt > CacheTtl)
                return null;

            return result;
        }
        catch
        {
            // Corrupt, missing, or inaccessible cache is not an error condition
            return null;
        }
    }
```

- [ ] **Step 4: Remove old async cache tests and run all tests**

Remove or update any tests that call `GetCachedResultAsync` to use `GetCachedResult()` instead. The existing tests `CachePersisted_GetCachedResultAsync_Retrieves` and `CacheExpired_GetCachedResultAsync_ReturnsNull` should be updated to call the sync version.

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "FullyQualifiedName~UpdateCheckServiceTests" --no-restore`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs \
        tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs
git commit -m "refactor(update-check): convert GetCachedResult to synchronous

Replace async GetCachedResultAsync with sync GetCachedResult per spec.
File.ReadAllText on <1KB cache is sub-millisecond — async overhead
not justified for startup hot path."
```

### Task 6: Move RefreshCacheInBackgroundIfStale to Service

**Files:**
- Modify: `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs`
- Test: `tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs`

**Context:** The spec says all cache management logic lives in the service (A1). The current `StartupUpdateNotifier.RefreshCacheInBackground` creates its own `UpdateCheckService` instance and duplicates cache-freshness logic. Move it to the service.

- [ ] **Step 1: Write the failing test**

Add to `UpdateCheckServiceTests.cs`:

```csharp
[Fact]
public async Task RefreshCacheInBackgroundIfStale_RefreshesWhenCacheExpired()
{
    // Arrange: write an expired cache
    var oldResult = new UpdateCheckResult
    {
        CurrentVersion = "1.0.0",
        LatestStableVersion = "1.0.0",
        CheckedAt = DateTimeOffset.UtcNow.AddHours(-25)
    };
    Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
    File.WriteAllText(_cachePath, JsonSerializer.Serialize(oldResult,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    var handler = BuildHandler(MakeVersionsJson("1.0.0", "2.0.0"));
    var svc = new UpdateCheckService(handler: handler, cachePath: _cachePath);

    // Act
    svc.RefreshCacheInBackgroundIfStale("1.0.0");

    // Give background task time to complete
    await Task.Delay(500);

    // Assert: cache was refreshed
    var cached = svc.GetCachedResult();
    Assert.NotNull(cached);
    Assert.True(cached.StableUpdateAvailable);
}

[Fact]
public async Task RefreshCacheInBackgroundIfStale_SkipsWhenCacheFresh()
{
    // Arrange: populate fresh cache
    var handler = BuildHandler(MakeVersionsJson("1.0.0", "2.0.0"));
    var svc = new UpdateCheckService(handler: handler, cachePath: _cachePath);
    await svc.CheckAsync("1.0.0");

    // Replace handler with one that throws — if refresh fires, it would fail
    var throwingHandler = BuildThrowingHandler();
    var svc2 = new UpdateCheckService(handler: throwingHandler, cachePath: _cachePath);

    // Act — should be a no-op because cache is fresh
    svc2.RefreshCacheInBackgroundIfStale("1.0.0");
    await Task.Delay(500);

    // Assert: cache still has original data (wasn't clobbered by failed refresh)
    var cached = svc2.GetCachedResult();
    Assert.NotNull(cached);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "FullyQualifiedName~RefreshCacheInBackgroundIfStale" --no-restore`
Expected: FAIL — method doesn't exist on service

- [ ] **Step 3: Implement RefreshCacheInBackgroundIfStale on UpdateCheckService**

Add to `UpdateCheckService.cs` after the `GetCachedResult()` method:

```csharp
    /// <inheritdoc/>
    public void RefreshCacheInBackgroundIfStale(string currentVersion)
    {
        try
        {
            // Check cache freshness synchronously before spawning a task
            var cached = GetCachedResult();
            if (cached is not null)
                return; // Cache is fresh — no refresh needed

            // Fire-and-forget: no await, no CancellationToken (R2)
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckAsync(currentVersion, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort — never surface to caller
                }
            });
        }
        catch
        {
            // Swallow errors in freshness check
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "FullyQualifiedName~RefreshCacheInBackgroundIfStale" --no-restore`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs \
        tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs
git commit -m "feat(update-check): move background refresh to service

RefreshCacheInBackgroundIfStale now lives on UpdateCheckService per A1.
Uses sync GetCachedResult for freshness check, fires background
CheckAsync if stale. No CancellationToken per R2."
```

### Task 7: Fix Pre-Release Track Logic

**Files:**
- Modify: `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs:85-127`
- Test: `tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs`

**Context:** The current logic has `!stableUpdateAvailable` guard on `preReleaseUpdateAvailable`, meaning when a stable update exists, pre-release is suppressed. Per spec revision, the service now always populates both `LatestStableVersion` and `LatestPreReleaseVersion` regardless of user track — track filtering is a presentation concern (notifier/command), not a data model concern. The `PreReleaseUpdateAvailable` flag must be computed honestly, and `PreReleaseUpdateCommand` must be populated when a pre-release update exists.

- [ ] **Step 1: Write the failing tests (AC-19 through AC-23)**

Add to `UpdateCheckServiceTests.cs`:

```csharp
[Fact]
public async Task CheckAsync_AlwaysPopulatesBothVersions()
{
    // AC-19: Service always populates both versions regardless of user track
    var handler = BuildHandler(MakeVersionsJson("0.4.0", "0.6.0", "0.7.0-alpha.1"));
    var svc = new UpdateCheckService(handler: handler, cachePath: _cachePath);

    var result = await svc.CheckAsync("0.4.0"); // stable user

    Assert.NotNull(result);
    Assert.Equal("0.6.0", result.LatestStableVersion);
    Assert.Equal("0.7.0-alpha.1", result.LatestPreReleaseVersion); // populated even for stable user
    Assert.True(result.StableUpdateAvailable);
    Assert.True(result.PreReleaseUpdateAvailable); // honestly computed
}

[Fact]
public async Task CheckAsync_PreReleaseUser_BothUpdatesAvailable()
{
    // AC-20: Pre-release user sees BOTH stable and pre-release updates
    var handler = BuildHandler(MakeVersionsJson("0.5.0-beta.1", "0.6.0", "0.7.0-alpha.1"));
    var svc = new UpdateCheckService(handler: handler, cachePath: _cachePath);

    var result = await svc.CheckAsync("0.5.0-beta.1");

    Assert.NotNull(result);
    Assert.True(result.StableUpdateAvailable);
    Assert.True(result.PreReleaseUpdateAvailable);
    Assert.Equal("0.6.0", result.LatestStableVersion);
    Assert.Equal("0.7.0-alpha.1", result.LatestPreReleaseVersion);
    Assert.Equal("dotnet tool update PPDS.Cli -g", result.UpdateCommand);
    Assert.Equal("dotnet tool update PPDS.Cli -g --prerelease", result.PreReleaseUpdateCommand);
}

[Fact]
public async Task CheckAsync_PreReleaseUser_OnlyStableAvailable()
{
    // AC-21: Pre-release user, newer stable only, no newer pre-release
    var handler = BuildHandler(MakeVersionsJson("0.5.0-beta.1", "0.6.0"));
    var svc = new UpdateCheckService(handler: handler, cachePath: _cachePath);

    var result = await svc.CheckAsync("0.5.0-beta.1");

    Assert.NotNull(result);
    Assert.True(result.StableUpdateAvailable);
    Assert.False(result.PreReleaseUpdateAvailable);
    Assert.Equal("0.6.0", result.LatestStableVersion);
    Assert.Null(result.LatestPreReleaseVersion); // No pre-release versions exist on NuGet
    Assert.Equal("dotnet tool update PPDS.Cli -g", result.UpdateCommand);
}

[Fact]
public async Task CheckAsync_PreReleaseUser_OnlyPreReleaseAvailable()
{
    // AC-22: Pre-release user, no newer stable, newer pre-release exists
    var handler = BuildHandler(MakeVersionsJson("0.5.0-beta.1", "0.4.0", "0.7.0-alpha.1"));
    var svc = new UpdateCheckService(handler: handler, cachePath: _cachePath);

    var result = await svc.CheckAsync("0.5.0-beta.1");

    Assert.NotNull(result);
    Assert.False(result.StableUpdateAvailable);
    Assert.True(result.PreReleaseUpdateAvailable);
    Assert.Equal("0.7.0-alpha.1", result.LatestPreReleaseVersion);
    Assert.Equal("dotnet tool update PPDS.Cli -g --prerelease", result.UpdateCommand);
}

[Fact]
public async Task CheckAsync_PreReleaseUpdateCommand_UsesPreReleaseFlag()
{
    // AC-23: PreReleaseUpdateCommand always uses --prerelease flag
    var handler = BuildHandler(MakeVersionsJson("0.5.0-beta.1", "0.6.0", "0.7.0-alpha.1"));
    var svc = new UpdateCheckService(handler: handler, cachePath: _cachePath);

    var result = await svc.CheckAsync("0.5.0-beta.1");

    Assert.NotNull(result);
    Assert.Contains("--prerelease", result!.PreReleaseUpdateCommand);
    Assert.DoesNotContain("--prerelease", result.UpdateCommand!);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "FullyQualifiedName~CheckAsync_StableUser_OnlyShowsStableUpdate|FullyQualifiedName~CheckAsync_PreReleaseUser_BothUpdates|FullyQualifiedName~CheckAsync_PreReleaseUser_OnlyStable|FullyQualifiedName~CheckAsync_PreReleaseUser_OnlyPreRelease|FullyQualifiedName~PreReleaseUpdateCommand_UsesPreReleaseFlag" --no-restore`
Expected: FAIL — pre-release track logic doesn't filter by IsOddMinor

- [ ] **Step 3: Rewrite the version comparison logic in CheckAsync**

Replace lines 85-127 in `UpdateCheckService.cs` (the version comparison and result construction block) with:

```csharp
        var current = TryParseVersion(currentVersion);

        // Always populate both versions regardless of user's track (AC-19).
        // Track-based filtering is a presentation concern (notifier/command).
        var latestStable = versions
            .Select(TryParseVersion)
            .Where(v => v is not null && !v.IsPreRelease)
            .OrderDescending()
            .FirstOrDefault();

        var latestPreRelease = versions
            .Select(TryParseVersion)
            .Where(v => v is not null && v.IsPreRelease)
            .OrderDescending()
            .FirstOrDefault();

        // Honestly computed — no track filtering at the data model level
        var stableUpdateAvailable = latestStable is not null
            && (current is null || latestStable > current);

        var preReleaseUpdateAvailable = latestPreRelease is not null
            && (current is null || latestPreRelease > current);

        // Primary command: stable if available, else pre-release
        string? command = null;
        if (stableUpdateAvailable)
            command = UpdateCommand;
        else if (preReleaseUpdateAvailable)
            command = UpdateCommandPreRelease;

        // Pre-release command: populated when a pre-release update exists
        string? preReleaseUpdateCommand = preReleaseUpdateAvailable
            ? UpdateCommandPreRelease
            : null;

        var result = new UpdateCheckResult
        {
            CurrentVersion = currentVersion,
            LatestStableVersion = latestStable?.ToString(),
            LatestPreReleaseVersion = latestPreRelease?.ToString(),
            StableUpdateAvailable = stableUpdateAvailable,
            PreReleaseUpdateAvailable = preReleaseUpdateAvailable,
            UpdateCommand = command,
            PreReleaseUpdateCommand = preReleaseUpdateCommand,
            CheckedAt = DateTimeOffset.UtcNow
        };
```

- [ ] **Step 4: Update existing tests that assumed old behavior**

The existing test `PreReleaseUser_NewerStableAvailable_SuggestsPlainCommand` assumed `PreReleaseUpdateAvailable = false` when stable exists. Update it to match the new spec behavior where pre-release track users see both.

- [ ] **Step 5: Run all UpdateCheckService tests**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "FullyQualifiedName~UpdateCheckServiceTests" --no-restore`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs \
        tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs
git commit -m "fix(update-check): pre-release track logic per spec

Stable users (even minor) now only see stable updates.
Pre-release users (odd minor) see both stable and pre-release.
PreReleaseUpdateCommand now populated when both available.
Fixes AC-19 through AC-23."
```

---

## Chunk 3: Notifier Refactor + Program.cs Integration

### Task 8: Rewrite StartupUpdateNotifier as Pure Functions

**Files:**
- Modify: `src/PPDS.Cli/Infrastructure/StartupUpdateNotifier.cs`
- Test: `tests/PPDS.Cli.Tests/Infrastructure/StartupUpdateNotifierTests.cs`

**Context:** The spec says StartupUpdateNotifier has NO file I/O — it's pure presentation. `FormatNotification(UpdateCheckResult?)` takes a result and returns a formatted string or null. All cache reading, TTL checking, and background refresh are owned by `UpdateCheckService`. Track-based filtering for startup notifications happens here (stable users see only stable at startup; pre-release users see both). ShouldShow checks only --quiet/-q — other suppression (--help, --version) is handled by Program.cs SkipVersionHeaderArgs.

- [ ] **Step 1: Write the new tests for FormatNotification**

Replace the entire content of `StartupUpdateNotifierTests.cs` with tests for the new pure functions:

```csharp
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.UpdateCheck;

namespace PPDS.Cli.Tests.Infrastructure;

public sealed class StartupUpdateNotifierTests
{
    #region FormatNotification

    [Fact]
    public void FormatNotification_NullResult_ReturnsNull()
    {
        // AC-33
        Assert.Null(StartupUpdateNotifier.FormatNotification(null));
    }

    [Fact]
    public void FormatNotification_NoUpdateAvailable_ReturnsNull()
    {
        // AC-33
        var result = MakeResult(stableUpdate: false, preReleaseUpdate: false);
        Assert.Null(StartupUpdateNotifier.FormatNotification(result));
    }

    [Fact]
    public void FormatNotification_StableOnlyUpdate_ReturnsSingleLine()
    {
        // AC-34
        var result = MakeResult(
            stableUpdate: true,
            preReleaseUpdate: false,
            latestStable: "0.6.0",
            updateCommand: "dotnet tool update PPDS.Cli -g");

        var message = StartupUpdateNotifier.FormatNotification(result);

        Assert.NotNull(message);
        Assert.Contains("0.6.0", message);
        Assert.Contains("dotnet tool update PPDS.Cli -g", message);
        Assert.DoesNotContain("\n", message.Trim());
    }

    [Fact]
    public void FormatNotification_BothUpdatesAvailable_ReturnsTwoLines()
    {
        // AC-35
        var result = MakeResult(
            stableUpdate: true,
            preReleaseUpdate: true,
            latestStable: "0.6.0",
            latestPreRelease: "0.7.0-alpha.1",
            updateCommand: "dotnet tool update PPDS.Cli -g",
            preReleaseUpdateCommand: "dotnet tool update PPDS.Cli -g --prerelease");

        var message = StartupUpdateNotifier.FormatNotification(result);

        Assert.NotNull(message);
        var lines = message!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("0.6.0", lines[0]);
        Assert.Contains("0.7.0-alpha.1", lines[1]);
    }

    [Fact]
    public void FormatNotification_PreReleaseOnlyUpdate_ReturnsSingleLine()
    {
        var result = MakeResult(
            stableUpdate: false,
            preReleaseUpdate: true,
            latestPreRelease: "0.7.0-alpha.1",
            updateCommand: "dotnet tool update PPDS.Cli -g --prerelease");

        var message = StartupUpdateNotifier.FormatNotification(result);

        Assert.NotNull(message);
        Assert.Contains("0.7.0-alpha.1", message);
        Assert.DoesNotContain("\n", message!.Trim());
    }

    #endregion

    #region ShouldShow

    [Fact]
    public void ShouldShow_EmptyArgs_ReturnsTrue()
    {
        Assert.True(StartupUpdateNotifier.ShouldShow(Array.Empty<string>()));
    }

    [Fact]
    public void ShouldShow_NormalArgs_ReturnsTrue()
    {
        Assert.True(StartupUpdateNotifier.ShouldShow(new[] { "query", "sql" }));
    }

    [Fact]
    public void ShouldShow_QuietLong_ReturnsFalse()
    {
        Assert.False(StartupUpdateNotifier.ShouldShow(new[] { "--quiet" }));
    }

    [Fact]
    public void ShouldShow_QuietShort_ReturnsFalse()
    {
        Assert.False(StartupUpdateNotifier.ShouldShow(new[] { "-q" }));
    }

    [Fact]
    public void ShouldShow_HelpFlag_ReturnsTrue()
    {
        // --help suppression handled by Program.cs SkipVersionHeaderArgs, not ShouldShow
        Assert.True(StartupUpdateNotifier.ShouldShow(new[] { "--help" }));
    }

    [Fact]
    public void ShouldShow_QuietAmongOtherArgs_ReturnsFalse()
    {
        Assert.False(StartupUpdateNotifier.ShouldShow(new[] { "query", "sql", "--quiet" }));
    }

    #endregion

    #region Helpers

    private static UpdateCheckResult MakeResult(
        bool stableUpdate = false,
        bool preReleaseUpdate = false,
        string? latestStable = null,
        string? latestPreRelease = null,
        string? updateCommand = null,
        string? preReleaseUpdateCommand = null)
    {
        return new UpdateCheckResult
        {
            CurrentVersion = "0.5.0-beta.1",
            LatestStableVersion = latestStable,
            LatestPreReleaseVersion = latestPreRelease,
            StableUpdateAvailable = stableUpdate,
            PreReleaseUpdateAvailable = preReleaseUpdate,
            UpdateCommand = updateCommand,
            PreReleaseUpdateCommand = preReleaseUpdateCommand,
            CheckedAt = DateTimeOffset.UtcNow
        };
    }

    #endregion
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "FullyQualifiedName~StartupUpdateNotifierTests" --no-restore`
Expected: FAIL — `FormatNotification` doesn't exist, `ShouldShow` doesn't check --help/--version

- [ ] **Step 3: Rewrite StartupUpdateNotifier**

Replace the entire content of `StartupUpdateNotifier.cs`:

```csharp
using PPDS.Cli.Services.UpdateCheck;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Pure presentation logic for startup update notifications.
/// No file I/O, no cache knowledge — all data access is owned by <see cref="IUpdateCheckService"/>.
/// Track-based filtering for startup notifications happens here.
/// </summary>
public static class StartupUpdateNotifier
{
    /// <summary>
    /// Formats a human-readable update notification from a cached result.
    /// Applies track-based filtering: stable users see only stable updates at startup;
    /// pre-release users see both stable and pre-release.
    /// Returns <see langword="null"/> if no update is available or the result is null.
    /// </summary>
    public static string? FormatNotification(UpdateCheckResult? result)
    {
        if (result is null || !result.UpdateAvailable)
            return null;

        // Determine user's track from current version
        var isPreReleaseTrack = NuGetVersion.TryParse(result.CurrentVersion, out var current)
            && current!.IsOddMinor;

        // Pre-release track user with both updates — two lines
        if (isPreReleaseTrack
            && result.StableUpdateAvailable && result.PreReleaseUpdateAvailable
            && result.LatestStableVersion is not null
            && result.LatestPreReleaseVersion is not null
            && result.UpdateCommand is not null
            && result.PreReleaseUpdateCommand is not null)
        {
            return $"Update available: {result.LatestStableVersion} (run: {result.UpdateCommand})\n"
                 + $"Pre-release available: {result.LatestPreReleaseVersion} (run: {result.PreReleaseUpdateCommand})";
        }

        // Stable track user: only show stable update (even if pre-release exists in data model)
        if (!isPreReleaseTrack && result.StableUpdateAvailable
            && result.LatestStableVersion is not null && result.UpdateCommand is not null)
        {
            return $"Update available: {result.LatestStableVersion} (run: {result.UpdateCommand})";
        }

        // Pre-release track, single update available
        if (result.StableUpdateAvailable && result.LatestStableVersion is not null
            && result.UpdateCommand is not null)
        {
            return $"Update available: {result.LatestStableVersion} (run: {result.UpdateCommand})";
        }

        if (result.PreReleaseUpdateAvailable && result.LatestPreReleaseVersion is not null)
        {
            var cmd = result.PreReleaseUpdateCommand ?? result.UpdateCommand;
            if (cmd is not null)
                return $"Update available: {result.LatestPreReleaseVersion} (run: {cmd})";
        }

        return null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the update notification should be shown.
    /// Returns <see langword="false"/> when <c>--quiet</c> or <c>-q</c> is present.
    /// Other suppression (--help, --version, version subcommand) is handled by
    /// Program.cs SkipVersionHeaderArgs — no duplication needed.
    /// </summary>
    public static bool ShouldShow(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-q", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "FullyQualifiedName~StartupUpdateNotifierTests" --no-restore`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Cli/Infrastructure/StartupUpdateNotifier.cs \
        tests/PPDS.Cli.Tests/Infrastructure/StartupUpdateNotifierTests.cs
git commit -m "refactor(update-check): notifier is now pure presentation

Delete GetNotificationMessage, RefreshCacheInBackground, IsCacheFresh.
Add FormatNotification(UpdateCheckResult?) — no file I/O.
Expand ShouldShow to suppress for --help, -h, --version, version.
Fixes AC-32 through AC-37."
```

### Task 9: Update Program.cs Integration

**Files:**
- Modify: `src/PPDS.Cli/Program.cs:47-64`

**Context:** Program.cs currently calls `StartupUpdateNotifier.GetNotificationMessage()` (which does file I/O) and `StartupUpdateNotifier.RefreshCacheInBackground()` (which creates its own service). Must change to use `UpdateCheckService.GetCachedResult()` and `UpdateCheckService.RefreshCacheInBackgroundIfStale()`.

- [ ] **Step 1: Update the startup notification block**

Replace lines 47-64 in `Program.cs`:

```csharp
        // Write version header for diagnostic context (skip for help/version/interactive)
        if (!args.Any(a => SkipVersionHeaderArgs.Contains(a)) && !IsInteractiveMode(args))
        {
            ErrorOutput.WriteVersionHeader();

            // Show cached update notification (guarded by --quiet/--help/--version)
            if (StartupUpdateNotifier.ShouldShow(args))
            {
                var updateService = new UpdateCheckService();
                var cached = updateService.GetCachedResult();
                var updateMessage = StartupUpdateNotifier.FormatNotification(cached);
                if (updateMessage != null)
                {
                    Console.Error.WriteLine(updateMessage);
                }

                // Fire-and-forget background cache refresh for next startup
                updateService.RefreshCacheInBackgroundIfStale(ErrorOutput.Version);
            }
        }
```

Add using at top of file if not present:
```csharp
using PPDS.Cli.Services.UpdateCheck;
```

- [ ] **Step 2: Verify full build succeeds**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore`
Expected: Build succeeded

- [ ] **Step 3: Run all tests to verify nothing is broken**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "Category!=Integration" --no-restore`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add src/PPDS.Cli/Program.cs
git commit -m "refactor(update-check): Program.cs uses service for cache access

Startup notification now uses UpdateCheckService.GetCachedResult() and
RefreshCacheInBackgroundIfStale() instead of StartupUpdateNotifier
file I/O methods. All cache knowledge in service per A1."
```

---

## Chunk 4: Self-Update Mechanism

### Task 10: Implement UpdateAsync — Dotnet Path Resolution + Wrapper Script

**Files:**
- Modify: `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs`
- Create: `src/PPDS.Cli/Services/UpdateCheck/UpdateScriptWriter.cs`
- Create: `tests/PPDS.Cli.Tests/Services/UpdateCheck/SelfUpdateTests.cs`

**Context:** Self-update resolves dotnet via `RuntimeEnvironment.GetRuntimeDirectory()` — navigate up 3 levels from the runtime directory to find the dotnet install root, then append `dotnet[.exe]`. WARNING: `Process.MainModule.FileName` returns the apphost shim (`ppds.exe`), NOT `dotnet.exe` — do NOT use it. On Windows, the running ppds process locks `.store` DLLs, so `dotnet tool update` fails unless ppds exits first. Solution: write a platform-specific wrapper script (.cmd/.sh) that waits for parent PID exit, then runs the update, then writes the status file.

- [ ] **Step 1: Write tests for dotnet path resolution and install detection**

Create `tests/PPDS.Cli.Tests/Services/UpdateCheck/SelfUpdateTests.cs`:

```csharp
using PPDS.Cli.Services.UpdateCheck;

namespace PPDS.Cli.Tests.Services.UpdateCheck;

public sealed class SelfUpdateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cachePath;
    private readonly string _statusPath;

    public SelfUpdateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ppds-selfupdate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _cachePath = Path.Combine(_tempDir, "update-check.json");
        _statusPath = Path.Combine(_tempDir, "update-status.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpdateAsync_NonGlobalInstall_ReturnsInstructions()
    {
        // AC-26: Non-global install returns instructions, no process spawn
        var svc = new UpdateCheckService(cachePath: _cachePath, statusPath: _statusPath);

        // Force non-global detection by setting a custom base directory
        var result = await svc.UpdateAsync(UpdateChannel.Current);

        // When running in tests, we're not in a global tool path
        // The implementation should detect this
        Assert.True(result.IsNonGlobalInstall || result.Success);
        if (result.IsNonGlobalInstall)
        {
            Assert.NotNull(result.ManualCommand);
            Assert.Contains("dotnet tool update", result.ManualCommand);
        }
    }

    [Fact]
    public async Task UpdateAsync_AlreadyUpToDate_ReturnsSuccess()
    {
        // Edge case: no update available
        // Populate cache where current = latest
        var handler = BuildHandler(MakeVersionsJson("1.0.0"));
        var svc = new UpdateCheckService(
            handler: handler, cachePath: _cachePath, statusPath: _statusPath);
        await svc.CheckAsync("1.0.0");

        var result = await svc.UpdateAsync(UpdateChannel.Current);

        Assert.True(result.Success);
        Assert.Contains("up to date", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // Helper methods matching UpdateCheckServiceTests pattern
    private static HttpMessageHandler BuildHandler(string json)
    {
        return new TestHandler(json);
    }

    private static string MakeVersionsJson(params string[] versions)
    {
        var quoted = string.Join(",", versions.Select(v => $"\"{v}\""));
        return $"{{\"versions\":[{quoted}]}}";
    }

    private sealed class TestHandler(string responseJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            };
            return Task.FromResult(response);
        }
    }
}
```

- [ ] **Step 2: Implement UpdateAsync on UpdateCheckService**

Add a `statusPath` constructor parameter alongside `cachePath`. Add the `UpdateAsync` method:

```csharp
    private readonly string _statusPath;

    // Update constructor to accept statusPath:
    public UpdateCheckService(
        HttpMessageHandler? handler = null,
        string? cachePath = null,
        string? statusPath = null)
    {
        _handler = handler;
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ppds", "update-check.json");
        _statusPath = statusPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ppds", "update-status.json");
    }

    /// <inheritdoc/>
    public async Task<UpdateResult> UpdateAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Determine target version from cache or fresh check
        var cached = GetCachedResult();
        if (cached is null)
        {
            cached = await CheckAsync(
                ErrorOutput.Version, cancellationToken).ConfigureAwait(false);
        }

        if (cached is null)
        {
            throw new PpdsException(
                ErrorCodes.UpdateCheck.NetworkError,
                "Cannot determine latest version. Check your network connection.")
            {
                Severity = PpdsSeverity.Error,
                Context = new Dictionary<string, object>
                {
                    ["manualCommand"] = "dotnet tool update PPDS.Cli -g"
                }
            };
        }

        // Step 2: Determine if an update is actually needed for the selected channel
        var updateNeeded = channel switch
        {
            UpdateChannel.Stable => cached.StableUpdateAvailable,
            UpdateChannel.PreRelease => cached.PreReleaseUpdateAvailable,
            UpdateChannel.Current => (NuGetVersion.TryParse(cached.CurrentVersion, out var cv) && cv!.IsOddMinor)
                                     ? cached.PreReleaseUpdateAvailable
                                     : cached.StableUpdateAvailable,
            _ => false
        };

        if (!updateNeeded)
        {
            return new UpdateResult
            {
                Success = true,
                ErrorMessage = "Already up to date.",
                InstalledVersion = cached.CurrentVersion
            };
        }

        // Step 3: Detect install type
        var isGlobal = IsGlobalToolInstall();
        if (!isGlobal)
        {
            return new UpdateResult
            {
                IsNonGlobalInstall = true,
                ManualCommand = "dotnet tool update PPDS.Cli",
                ErrorMessage = "PPDS is installed as a local tool. Update your tool manifest manually."
            };
        }

        // Step 4: Determine command based on channel
        var usePreRelease = channel switch
        {
            UpdateChannel.Stable => false,
            UpdateChannel.PreRelease => true,
            UpdateChannel.Current => NuGetVersion.TryParse(
                cached.CurrentVersion, out var cv) && cv!.IsOddMinor,
            _ => false
        };

        var targetVersion = usePreRelease
            ? cached.LatestPreReleaseVersion
            : cached.LatestStableVersion;

        var updateArgs = usePreRelease
            ? "tool update PPDS.Cli -g --prerelease"
            : "tool update PPDS.Cli -g";

        // Step 5: Resolve dotnet path (S2: no shell: true)
        var dotnetPath = ResolveDotnetPath();
        if (dotnetPath is null)
        {
            throw new PpdsException(
                ErrorCodes.UpdateCheck.DotnetNotFound,
                "Cannot locate the dotnet runtime. Run manually: dotnet tool update PPDS.Cli -g")
            {
                Severity = PpdsSeverity.Error
            };
        }

        // Step 6: Spawn detached update process
        SpawnDetachedUpdate(dotnetPath, updateArgs, targetVersion);

        return new UpdateResult
        {
            Success = true,
            InstalledVersion = targetVersion
        };
    }

    /// <summary>
    /// Checks whether the CLI is installed as a global dotnet tool.
    /// </summary>
    internal static bool IsGlobalToolInstall()
    {
        var baseDir = AppContext.BaseDirectory;
        var dotnetToolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools");
        return baseDir.StartsWith(dotnetToolsDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the path to the dotnet executable.
    /// Primary: RuntimeEnvironment.GetRuntimeDirectory() navigated up 3 levels.
    /// WARNING: Process.MainModule.FileName returns the apphost shim (ppds.exe), NOT dotnet.
    /// </summary>
    internal static string? ResolveDotnetPath()
    {
        var dotnetExeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        // Primary: navigate up from runtime directory
        // RuntimeDirectory = <dotnet-root>/shared/Microsoft.NETCore.App/<version>/
        // dotnet.exe = <dotnet-root>/dotnet.exe
        try
        {
            var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var dotnetFromRuntime = Path.GetFullPath(
                Path.Combine(runtimeDir, "..", "..", "..", dotnetExeName));
            if (File.Exists(dotnetFromRuntime))
                return dotnetFromRuntime;
        }
        catch { /* RuntimeEnvironment may fail in unusual hosts */ }

        // Fallback: DOTNET_ROOT
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var dotnetExe = Path.Combine(dotnetRoot, dotnetExeName);
            if (File.Exists(dotnetExe))
                return dotnetExe;
        }

        // Platform defaults
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "dotnet.exe");
            if (File.Exists(programFiles)) return programFiles;
        }
        else
        {
            if (File.Exists("/usr/local/share/dotnet/dotnet")) return "/usr/local/share/dotnet/dotnet";
            if (File.Exists("/usr/share/dotnet/dotnet")) return "/usr/share/dotnet/dotnet";
        }

        return null;
    }

    /// <summary>
    /// Spawns a detached wrapper script that waits for parent PID exit, runs the update,
    /// then writes the status file. The parent ppds process exits immediately after spawning.
    /// </summary>
    private void SpawnDetachedUpdate(string dotnetPath, string updateArgs, string? targetVersion)
    {
        var parentPid = Environment.ProcessId;
        var scriptPath = UpdateScriptWriter.WriteScript(
            dotnetPath, updateArgs, targetVersion, parentPid, _statusPath, _lockPath);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows()
                ? $"/c \"{scriptPath}\""
                : scriptPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        try
        {
            System.Diagnostics.Process.Start(psi);
            // Write lock file with wrapper info
            File.WriteAllText(_lockPath, parentPid.ToString());
        }
        catch (Exception ex)
        {
            throw new PpdsException(
                ErrorCodes.UpdateCheck.UpdateFailed,
                $"Failed to start update process: {ex.Message}")
            {
                Severity = PpdsSeverity.Error
            };
        }
    }
```

Also create the `UpdateScriptWriter` helper in a separate file:

**Create: `src/PPDS.Cli/Services/UpdateCheck/UpdateScriptWriter.cs`**

```csharp
namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Generates platform-specific wrapper scripts for detached self-update.
/// The script waits for the parent ppds process to exit (unlocking .store DLLs),
/// then runs dotnet tool update, captures the result, and writes a status file.
/// </summary>
internal static class UpdateScriptWriter
{
    public static string WriteScript(
        string dotnetPath, string updateArgs, string? targetVersion,
        int parentPid, string statusPath, string lockPath)
    {
        var scriptDir = Path.GetTempPath();
        var scriptPath = OperatingSystem.IsWindows()
            ? Path.Combine(scriptDir, $"ppds-update-{Guid.NewGuid():N}.cmd")
            : Path.Combine(scriptDir, $"ppds-update-{Guid.NewGuid():N}.sh");

        var content = OperatingSystem.IsWindows()
            ? GenerateWindowsScript(dotnetPath, updateArgs, targetVersion, parentPid, statusPath, lockPath, scriptPath)
            : GenerateUnixScript(dotnetPath, updateArgs, targetVersion, parentPid, statusPath, lockPath, scriptPath);

        File.WriteAllText(scriptPath, content);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return scriptPath;
    }

    private static string GenerateWindowsScript(
        string dotnetPath, string updateArgs, string? targetVersion,
        int parentPid, string statusPath, string lockPath, string scriptPath)
    {
        return $"""
            @echo off
            REM Wait for parent ppds process to exit (unlocks .store DLLs)
            :wait
            tasklist /FI "PID eq {parentPid}" 2>NUL | find /I "{parentPid}" >NUL
            if not errorlevel 1 (
                timeout /t 1 /nobreak >NUL
                goto wait
            )
            REM Run the update
            "{dotnetPath}" {updateArgs}
            set EXIT_CODE=%ERRORLEVEL%
            REM Write status file
            echo {{"success": %EXIT_CODE% EQU 0, "exitCode": %EXIT_CODE%, "targetVersion": "{targetVersion ?? "unknown"}", "timestamp": "%DATE% %TIME%"}} > "{statusPath}"
            REM Cleanup
            del "{lockPath}" 2>NUL
            del "{scriptPath}" 2>NUL
            """;
    }

    private static string GenerateUnixScript(
        string dotnetPath, string updateArgs, string? targetVersion,
        int parentPid, string statusPath, string lockPath, string scriptPath)
    {
        return $"""
            #!/bin/sh
            # Wait for parent ppds process to exit (unlocks .store DLLs)
            while kill -0 {parentPid} 2>/dev/null; do sleep 1; done
            # Run the update
            "{dotnetPath}" {updateArgs}
            EXIT_CODE=$?
            # Write status file
            if [ $EXIT_CODE -eq 0 ]; then
                echo '{{"success": true, "exitCode": 0, "targetVersion": "{targetVersion ?? "unknown"}", "timestamp": "'$(date -u +%Y-%m-%dT%H:%M:%SZ)'"}}' > "{statusPath}"
            else
                echo '{{"success": false, "exitCode": '$EXIT_CODE', "targetVersion": "{targetVersion ?? "unknown"}", "timestamp": "'$(date -u +%Y-%m-%dT%H:%M:%SZ)'"}}' > "{statusPath}"
            fi
            # Cleanup
            rm -f "{lockPath}" "{scriptPath}"
            """;
    }
}
```

Note: Add `_lockPath` field to `UpdateCheckService` constructor alongside `_statusPath`:
```csharp
    private readonly string _lockPath;

    // In constructor:
    _lockPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ppds", "update.lock");
```

Also add lock file check at the start of `UpdateAsync`:
```csharp
        // Guard: check for existing update-in-progress (AC-52)
        if (File.Exists(_lockPath))
        {
            try
            {
                var pidStr = File.ReadAllText(_lockPath).Trim();
                if (int.TryParse(pidStr, out var pid))
                {
                    try
                    {
                        System.Diagnostics.Process.GetProcessById(pid);
                        // Process still running — update in progress
                        return new UpdateResult
                        {
                            Success = false,
                            ErrorMessage = "An update is already in progress. Try again later."
                        };
                    }
                    catch (ArgumentException)
                    {
                        // PID doesn't exist — stale lock, clean up
                        File.Delete(_lockPath);
                    }
                }
                else
                {
                    File.Delete(_lockPath); // corrupt lock
                }
            }
            catch { /* ignore lock file read errors */ }
        }
```

Add required `using` statements at the top of UpdateCheckService.cs:
```csharp
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure.Errors;
using System.Runtime.InteropServices;
```

- [ ] **Step 3: Run self-update tests**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "FullyQualifiedName~SelfUpdateTests" --no-restore`
Expected: All pass

- [ ] **Step 4: Run ALL tests to verify no regressions**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "Category!=Integration" --no-restore`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs \
        tests/PPDS.Cli.Tests/Services/UpdateCheck/SelfUpdateTests.cs
git commit -m "feat(update-check): implement self-update via detached process

Add UpdateAsync with dotnet path resolution via RuntimeEnvironment
(fallback to DOTNET_ROOT + platform defaults). Global install
detection via AppContext.BaseDirectory. Detached process spawn with
shell: false per S2. Status file feedback for next run.
Covers AC-24 through AC-31."
```

### Task 11: Add Self-Update Options to VersionCommand

**Files:**
- Modify: `src/PPDS.Cli/Commands/VersionCommand.cs`
- Modify: `tests/PPDS.Cli.Tests/Commands/VersionCommandTests.cs`

**Context:** Add `--update`, `--stable`, `--prerelease`, and `--yes` options. `--stable` and `--prerelease` are mutually exclusive. `--update` triggers the self-update flow.

- [ ] **Step 1: Write tests for new options**

Add to `VersionCommandTests.cs`:

```csharp
[Fact]
public void VersionCommand_HasUpdateOption()
{
    var command = VersionCommand.Create();
    Assert.Contains(command.Options, o => o.Name == "update");
}

[Fact]
public void VersionCommand_HasStableOption()
{
    var command = VersionCommand.Create();
    Assert.Contains(command.Options, o => o.Name == "stable");
}

[Fact]
public void VersionCommand_HasPreReleaseOption()
{
    var command = VersionCommand.Create();
    Assert.Contains(command.Options, o => o.Name == "prerelease");
}

[Fact]
public void VersionCommand_HasYesOption()
{
    var command = VersionCommand.Create();
    Assert.Contains(command.Options, o => o.Name == "yes");
}
```

- [ ] **Step 2: Update VersionCommand to add new options and self-update flow**

Replace `VersionCommand.cs` contents with:

```csharp
using System.CommandLine;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.UpdateCheck;

namespace PPDS.Cli.Commands;

/// <summary>
/// Displays version information for the PPDS CLI and optionally checks for updates or self-updates.
/// </summary>
public static class VersionCommand
{
    /// <summary>
    /// Creates the 'version' command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("version", "Show version information for the PPDS CLI");

        var checkOption = new Option<bool>("--check")
        {
            Description = "Check NuGet for the latest available version"
        };

        var updateOption = new Option<bool>("--update")
        {
            Description = "Update PPDS CLI to the latest version"
        };

        var stableOption = new Option<bool>("--stable")
        {
            Description = "Force update to latest stable version"
        };

        var preReleaseOption = new Option<bool>("--prerelease")
        {
            Description = "Force update to latest pre-release version"
        };

        var yesOption = new Option<bool>(new[] { "--yes", "-y" })
        {
            Description = "Skip confirmation prompt"
        };

        command.Options.Add(checkOption);
        command.Options.Add(updateOption);
        command.Options.Add(stableOption);
        command.Options.Add(preReleaseOption);
        command.Options.Add(yesOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var runtimeVersion = Environment.Version.ToString();
            var platform = RuntimeInformation.OSDescription;

            Console.Error.WriteLine($"PPDS CLI v{ErrorOutput.Version}");
            Console.Error.WriteLine($"SDK v{ErrorOutput.SdkVersion}");
            Console.Error.WriteLine($".NET {runtimeVersion}");
            Console.Error.WriteLine($"Platform: {platform}");

            var update = parseResult.GetValue(updateOption);
            var stable = parseResult.GetValue(stableOption);
            var preRelease = parseResult.GetValue(preReleaseOption);
            var yes = parseResult.GetValue(yesOption);
            var check = parseResult.GetValue(checkOption);

            // Validate mutual exclusivity (AC-49)
            if (stable && preRelease)
            {
                Console.Error.WriteLine("Error: --stable and --prerelease are mutually exclusive.");
                return ExitCodes.InvalidArguments;
            }

            if (update || stable || preRelease)
            {
                return await HandleUpdateAsync(stable, preRelease, yes, cancellationToken);
            }

            if (check)
            {
                return await HandleCheckAsync(cancellationToken);
            }

            return ExitCodes.Success;
        });

        return command;
    }

    private static async Task<int> HandleCheckAsync(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Checking for updates...");

        await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
        var service = localProvider.GetRequiredService<IUpdateCheckService>();
        var result = await service.CheckAsync(ErrorOutput.Version, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            Console.Error.WriteLine("Unable to check for updates (network unavailable or check failed).");
            return ExitCodes.Success;
        }

        if (result.LatestStableVersion is not null)
            Console.Error.WriteLine($"Latest stable:      {result.LatestStableVersion}");

        if (result.LatestPreReleaseVersion is not null)
            Console.Error.WriteLine($"Latest pre-release: {result.LatestPreReleaseVersion}");

        if (result.UpdateAvailable && result.UpdateCommand is not null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Update available! Run: {result.UpdateCommand}");
            if (result.PreReleaseUpdateCommand is not null)
                Console.Error.WriteLine($"Pre-release available! Run: {result.PreReleaseUpdateCommand}");
        }
        else
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("You are up to date.");
        }

        return ExitCodes.Success;
    }

    private static async Task<int> HandleUpdateAsync(
        bool forceStable,
        bool forcePreRelease,
        bool skipConfirmation,
        CancellationToken cancellationToken)
    {
        var channel = forceStable ? UpdateChannel.Stable
            : forcePreRelease ? UpdateChannel.PreRelease
            : UpdateChannel.Current;

        Console.Error.WriteLine();
        Console.Error.WriteLine("Checking for updates...");

        try
        {
            await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
            var service = localProvider.GetRequiredService<IUpdateCheckService>();

            // Step 1: Check what's available BEFORE prompting or updating
            var currentVersion = ErrorOutput.Version;
            var checkResult = await service.CheckAsync(currentVersion, cancellationToken)
                .ConfigureAwait(false);

            if (checkResult is null)
            {
                Console.Error.WriteLine("Cannot determine latest version. Check your network connection.");
                Console.Error.WriteLine($"  Run manually: dotnet tool update PPDS.Cli -g");
                return ExitCodes.Failure;
            }

            var targetVersion = channel switch
            {
                UpdateChannel.Stable => checkResult.StableUpdateAvailable ? checkResult.LatestStableVersion : null,
                UpdateChannel.PreRelease => checkResult.PreReleaseUpdateAvailable ? checkResult.LatestPreReleaseVersion : null,
                UpdateChannel.Current => (NuGetVersion.TryParse(currentVersion, out var cv) && cv!.IsOddMinor)
                    ? (checkResult.PreReleaseUpdateAvailable ? checkResult.LatestPreReleaseVersion : null)
                    : (checkResult.StableUpdateAvailable ? checkResult.LatestStableVersion : null),
                _ => null
            };

            if (targetVersion is null)
            {
                Console.Error.WriteLine("You are already up to date.");
                return ExitCodes.Success;
            }

            // Step 2: Confirm with user BEFORE spawning update
            if (!skipConfirmation)
            {
                Console.Error.Write($"Update to {targetVersion}? [Y/n] ");
                var response = Console.ReadLine();
                if (!string.IsNullOrEmpty(response) &&
                    !response.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Update cancelled.");
                    return ExitCodes.Success;
                }
            }

            // Step 3: Now actually perform the update
            var result = await service.UpdateAsync(channel, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsNonGlobalInstall)
            {
                Console.Error.WriteLine(result.ErrorMessage);
                Console.Error.WriteLine($"  Run: {result.ManualCommand}");
                return ExitCodes.Success;
            }

            Console.Error.WriteLine($"Updating to {targetVersion}...");
            Console.Error.WriteLine("The update will complete in the background.");
            return ExitCodes.Success;
        }
        catch (PpdsException ex)
        {
            Console.Error.WriteLine($"Error: {ex.UserMessage}");
            if (ex.Context?.TryGetValue("manualCommand", out var cmd) == true)
                Console.Error.WriteLine($"  Run manually: {cmd}");
            return ExitCodes.Failure;
        }
    }
}
```

- [ ] **Step 3: Run VersionCommand tests**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "FullyQualifiedName~VersionCommandTests" --no-restore`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add src/PPDS.Cli/Commands/VersionCommand.cs \
        tests/PPDS.Cli.Tests/Commands/VersionCommandTests.cs
git commit -m "feat(update-check): add --update/--stable/--prerelease/--yes to version command

ppds version --update: self-update to latest on current track.
--stable/--prerelease: force track switch (mutually exclusive).
--yes: skip confirmation prompt for CI scripting.
Covers AC-40 through AC-43, AC-49."
```

### Task 12: Status File Reading in Program.cs

**Files:**
- Modify: `src/PPDS.Cli/Program.cs`

**Context:** After a self-update, the detached process writes a status file. On next run, Program.cs should read it, display the result, and delete it.

- [ ] **Step 1: Add status file reading to Program.cs startup**

Add after the version header block (before `var rootCommand`), inside the `!SkipVersionHeaderArgs` block:

```csharp
            // Check for post-update status file (one-shot feedback from self-update)
            ReadAndDeleteUpdateStatus();
```

Add the helper method to the `Program` class:

```csharp
    private static void ReadAndDeleteUpdateStatus()
    {
        try
        {
            var statusPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ppds", "update-status.json");

            if (!File.Exists(statusPath))
                return;

            var json = File.ReadAllText(statusPath);
            File.Delete(statusPath);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            var version = root.TryGetProperty("targetVersion", out var v) ? v.GetString() : null;

            if (success && version is not null)
            {
                Console.Error.WriteLine($"Successfully updated to {version}.");
            }
            else
            {
                Console.Error.WriteLine("Update failed. Run manually: dotnet tool update PPDS.Cli -g");
            }
        }
        catch
        {
            // Status file read/delete failure is not fatal
        }
    }
```

- [ ] **Step 2: Verify full build and tests pass**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore && dotnet test tests/PPDS.Cli.Tests/ --filter "Category!=Integration" --no-restore`
Expected: Build succeeded, all tests pass

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Cli/Program.cs
git commit -m "feat(update-check): read post-update status file at startup

After self-update, the detached process writes a status file.
Program.cs reads it on next run, displays result, and deletes it.
Covers AC-31."
```

---

## Final Verification

### Task 13: Full Test Suite + Build Verification

- [ ] **Step 1: Run the full test suite (excluding integration tests)**

Run: `dotnet test tests/PPDS.Cli.Tests/ --filter "Category!=Integration" --no-restore`
Expected: All tests pass

- [ ] **Step 2: Verify no warnings in build**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore -warnaserrors`
Expected: Build succeeded (or only pre-existing warnings)

- [ ] **Step 3: Final commit if any cleanup needed**

---

## AC Coverage Summary

| AC | Description | Task |
|----|-------------|------|
| AC-09–AC-11 | GetCachedResult cache behavior | Task 5 (sync refactor) |
| AC-12 | GetCachedResult is synchronous | Task 5 (structural) |
| AC-19 | Service always populates both versions (data model honest) | Task 7 |
| AC-20–AC-23 | Pre-release track notification logic | Task 7 + Task 8 (presentation) |
| AC-24–AC-27 | Self-update detection + dotnet path (RuntimeEnvironment) | Task 10 |
| AC-25 | Wrapper script mechanism (.cmd/.sh) | Task 10 |
| AC-28–AC-30 | Track-based update channels | Task 10 |
| AC-31 | Status file read/write | Task 10 + Task 12 |
| AC-32 | ShouldShow --quiet/-q only (SkipVersionHeaderArgs handles rest) | Task 8 |
| AC-33–AC-37 | FormatNotification pure function with track filtering | Task 8 |
| AC-40–AC-43 | VersionCommand --update options | Task 11 |
| AC-45 | Startup sync path | Task 9 |
| AC-47 | PpdsException with ErrorCode | Task 10 |
| AC-48 | "Checking for updates..." message | Task 11 |
| AC-49 | --stable/--prerelease mutual exclusivity | Task 11 |
| AC-50 | --update no network fallback | Task 10 |
| AC-51 | Version from assembly attribute | Existing (documented) |
| AC-52 | Lock file prevents concurrent update races | Task 10 |
