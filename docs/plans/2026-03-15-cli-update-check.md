# CLI Update Version Check Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a non-blocking update check to the CLI that notifies users when a newer version of PPDS is available on NuGet, with an explicit `ppds version --check` command for on-demand comparison.

**Architecture:** An `UpdateCheckService` queries the NuGet flat container API for available versions, caches results to `~/.ppds/update-check.json` for 24 hours, and compares against the current assembly version. At CLI startup, a non-blocking read of cached results displays a one-liner to stderr. The `ppds version --check` command forces a fresh fetch.

**Tech Stack:** .NET 8+, System.CommandLine, System.Text.Json, HttpClient, xUnit + FluentAssertions + Moq

**Issue:** [#564](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/564)

---

## Acceptance Criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | `ppds version` displays current CLI version, SDK version, .NET version, and platform | `VersionCommandTests.Create_ReturnsCommandWithCorrectName` |
| AC-02 | `ppds version --check` fetches latest versions from NuGet and displays current vs latest (stable and pre-release) | `UpdateCheckServiceTests.CheckAsync_ReturnsLatestStableAndPreRelease` |
| AC-03 | When current version is older than latest stable, suggests `dotnet tool update PPDS.Cli -g` | `UpdateCheckServiceTests.CheckAsync_StableAvailable_SuggestsPlainUpdate` |
| AC-04 | When user is on pre-release and newer pre-release exists (no newer stable), suggests command with `--prerelease` | `UpdateCheckServiceTests.CheckAsync_PreReleaseUser_SuggestsPreReleaseFlag` |
| AC-05 | Check result is cached to `~/.ppds/update-check.json` for 24 hours | `UpdateCheckServiceTests.CheckAsync_CachesResult` |
| AC-06 | CLI startup reads cached result and shows one-liner notification to stderr if update available | `StartupUpdateNotifierTests.GetNotificationMessage_UpdateAvailable_ReturnsMessage` |
| AC-07 | Startup notification never blocks or slows CLI execution | `StartupUpdateNotifierTests.GetNotificationMessage_NeverThrows` |
| AC-08 | Network failures during check are handled gracefully (no crash, no output to stdout) | `UpdateCheckServiceTests.CheckAsync_NetworkError_ReturnsNull` |
| AC-09 | Startup notification suppressed when `--quiet` or `-q` is passed | `StartupUpdateNotifierTests.ShouldShow_QuietFlag_ReturnsFalse` |
| AC-10 | SemVer pre-release comparison handles multi-digit numeric segments correctly (`beta.10 > beta.2`) | `NuGetVersionTests.CompareTo_MultiDigitPreRelease` |

---

## File Structure

### New Files

| File | Responsibility |
|------|----------------|
| `src/PPDS.Cli/Services/UpdateCheck/NuGetVersion.cs` | SemVer value type — parse, compare, detect pre-release |
| `src/PPDS.Cli/Services/UpdateCheck/IUpdateCheckService.cs` | Service interface |
| `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs` | NuGet API fetch, caching, comparison logic |
| `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckResult.cs` | Result record |
| `src/PPDS.Cli/Infrastructure/StartupUpdateNotifier.cs` | Startup cache reader + quiet flag check |
| `src/PPDS.Cli/Commands/VersionCommand.cs` | `ppds version [--check]` command |
| `tests/PPDS.Cli.Tests/Services/UpdateCheck/NuGetVersionTests.cs` | Version parsing/comparison tests |
| `tests/PPDS.Cli.Tests/Services/UpdateCheck/UpdateCheckServiceTests.cs` | Service logic tests |
| `tests/PPDS.Cli.Tests/Infrastructure/StartupUpdateNotifierTests.cs` | Startup notifier tests |
| `tests/PPDS.Cli.Tests/Commands/VersionCommandTests.cs` | Command structure tests |

### Modified Files

| File | Change |
|------|--------|
| `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs` | Add `UpdateCheck` error category (before final closing brace) |
| `src/PPDS.Cli/Program.cs` | Add `VersionCommand` to subcommands, add startup notification, add `"version"` to `SkipVersionHeaderArgs` (because `version` command displays its own version info, making the startup header redundant) |

---

## Key Type Signatures

### NuGetVersion

Immutable SemVer value type. Implements `IComparable<NuGetVersion>`, `IEquatable<NuGetVersion>`.

```csharp
public sealed class NuGetVersion : IComparable<NuGetVersion>, IEquatable<NuGetVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string PreReleaseLabel { get; }  // Empty string for stable
    public bool IsPreRelease { get; }
    public bool IsOddMinor { get; }         // PPDS convention: odd minor = pre-release line

    public static NuGetVersion Parse(string version);
    public static bool TryParse(string? version, out NuGetVersion? result);
}
```

**Parsing:** Strip build metadata (`+commitHash`) first, then split pre-release label on `-`, parse `major.minor.patch`. Must handle `InformationalVersion` format from assemblies (e.g., `1.2.3-beta.1+abc1234`).

**Comparison:** Major → Minor → Patch → stable beats pre-release for same base → pre-release labels compared segment-by-segment. **Numeric segments must be compared as integers** (so `beta.10 > beta.2`). Split label on `.`, try `int.TryParse` on each segment; numeric segments sort before string segments per SemVer spec.

### UpdateCheckResult

```csharp
public sealed record UpdateCheckResult
{
    public required string CurrentVersion { get; init; }
    public string? LatestStableVersion { get; init; }
    public string? LatestPreReleaseVersion { get; init; }
    public bool StableUpdateAvailable { get; init; }
    public bool PreReleaseUpdateAvailable { get; init; }
    public string? UpdateCommand { get; init; }    // null if up-to-date
    public DateTimeOffset CheckedAt { get; init; }
    public bool UpdateAvailable => StableUpdateAvailable || PreReleaseUpdateAvailable;
}
```

### IUpdateCheckService

```csharp
public interface IUpdateCheckService
{
    Task<UpdateCheckResult?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);
    Task<UpdateCheckResult?> GetCachedResultAsync(CancellationToken cancellationToken = default);
}
```

---

## Design Constraints

### HttpClient Pattern

The existing codebase creates `HttpClient` internally (see `ConnectionService`). Extend this pattern by accepting an optional `HttpMessageHandler?` parameter for test injection. Constructor also accepts optional `string? cachePath` (defaults to `~/.ppds/update-check.json`) for test isolation.

Do NOT hold `HttpClient` as a field. Create it within `CheckAsync` scope using `using var client = new HttpClient(handler, disposeHandler: false)` so the client is disposed after each call. This satisfies Constitution R1 without requiring `IDisposable` on the service.

```csharp
// Production
var service = new UpdateCheckService();

// Tests — handler for mocking HTTP, cachePath for temp directory isolation
var service = new UpdateCheckService(handler: mockHandler, cachePath: tempPath);
```

### Caching

- **Location:** `~/.ppds/update-check.json` (follows `QueryHistoryService` pattern at `~/.ppds/history/`)
- **TTL:** 24 hours from `CheckedAt` timestamp
- **Atomic writes:** Write to temp file, then `File.Move(temp, target, overwrite: true)`. Prevents corruption if process exits mid-write.
- **Read tolerance:** Catch all exceptions on read, return null. Corrupt/missing cache is not an error.

### Startup Notification

`StartupUpdateNotifier.ShouldShow(string[] args)` accepts the raw `args` array and checks for `"--quiet"` or `"-q"` via simple string matching. This runs before System.CommandLine parsing, matching the existing `SkipVersionHeaderArgs` pattern in `Program.cs`.

`StartupUpdateNotifier.GetNotificationMessage` calls `GetCachedResultAsync` for the notification message (returns null if missing/expired/corrupt). For background refresh, it separately reads the cache file's `CheckedAt` timestamp to determine staleness — this avoids re-parsing the full result.

### Background Refresh

`StartupUpdateNotifier.RefreshCacheInBackground` must:
1. **Check cache age first** — if cache file exists and `CheckedAt` is < 24h old, skip the HTTP call entirely
2. Fire-and-forget `Task.Run` only when cache is stale or missing
3. Since `HttpClient` is scoped within `CheckAsync` (not held as a field), no disposal concern with fire-and-forget
4. Swallow all exceptions — best-effort, never crash the CLI

### Update Command Logic

| Current Version | Latest Stable | Latest Pre-release | Suggested Command |
|----------------|---------------|-------------------|-------------------|
| `0.4.0` | `0.6.0` | `0.7.0-alpha.0` | `dotnet tool update PPDS.Cli -g` |
| `0.5.0-beta.1` | `0.6.0` | `0.5.0-beta.2` | `dotnet tool update PPDS.Cli -g` (stable takes priority) |
| `0.5.0-beta.1` | `0.4.0` | `0.5.0-beta.2` | `dotnet tool update PPDS.Cli -g --prerelease` |
| `0.6.0` | `0.6.0` | `0.5.0-beta.2` | null (up-to-date) |

Stable update always takes priority over pre-release — aligns with PPDS odd/even versioning convention where odd-minor leads to next even-minor stable.

### NuGet API

- **Endpoint:** `https://api.nuget.org/v3-flatcontainer/ppds.cli/index.json`
- **Response:** `{"versions":["0.1.0","0.2.0-beta.1",...]}`
- **No auth required.** Simple GET, returns JSON array of version strings.
- **Timeout:** 10 seconds. Failure returns null, never throws.

### ErrorCodes

Add `UpdateCheck` category to `ErrorCodes.cs`:
- `UpdateCheck.NetworkError` — failed to reach NuGet API
- `UpdateCheck.ParseError` — NuGet API returned unexpected data
- `UpdateCheck.CacheError` — cache file unreadable

---

## Task Breakdown

### Task 1: NuGetVersion Value Type

**Files:** `NuGetVersion.cs`, `NuGetVersionTests.cs`

- [ ] Write tests: parsing valid versions, pre-release detection, `IsOddMinor`, `TryParse` with invalid input, build metadata stripping, `ToString` round-trip
- [ ] Write tests: comparison — higher major/minor/patch wins, stable beats pre-release at same base, **multi-digit numeric pre-release segments** (`beta.10 > beta.2`), cross-minor comparison
- [ ] Implement `NuGetVersion` satisfying all tests
- [ ] Commit

### Task 2: Result Record and Service Interface

**Files:** `UpdateCheckResult.cs`, `IUpdateCheckService.cs`

- [ ] Create `UpdateCheckResult` record and `IUpdateCheckService` interface per signatures above
- [ ] Commit

### Task 3: UpdateCheckService

**Files:** `UpdateCheckService.cs`, `UpdateCheckServiceTests.cs`, `ErrorCodes.cs`

- [ ] Add `UpdateCheck` error codes to `ErrorCodes.cs`
- [ ] Write tests: `CheckAsync` returns latest stable and pre-release from mocked API response
- [ ] Write tests: current is latest → no update available, no command suggested
- [ ] Write tests: stable available → suggests plain update command
- [ ] Write tests: pre-release user, no newer stable, newer pre-release → suggests `--prerelease` command
- [ ] Write tests: pre-release user, newer stable available → suggests plain command (stable priority)
- [ ] Write tests: `CheckAsync` caches result, `GetCachedResultAsync` returns it
- [ ] Write tests: expired cache returns null from `GetCachedResultAsync`
- [ ] Write tests: network error returns null (no throw), non-success status returns null
- [ ] Write tests: corrupt cache file returns null (no throw)
- [ ] Implement `UpdateCheckService` with `HttpMessageHandler` injection, atomic cache writes, proper SemVer comparison
- [ ] Commit

### Task 4: VersionCommand

**Files:** `VersionCommand.cs`, `VersionCommandTests.cs`, `Program.cs`

- [ ] Write tests: command name is `"version"`, has optional `--check` flag
- [ ] Implement `VersionCommand` — thin wrapper that shows version info and delegates to `UpdateCheckService` for `--check`
- [ ] Wire into `Program.cs`: add to subcommands, add `"version"` to `SkipVersionHeaderArgs`
- [ ] Commit

### Task 5: Startup Notification

**Files:** `StartupUpdateNotifier.cs`, `StartupUpdateNotifierTests.cs`, `Program.cs`

- [ ] Write tests: `GetNotificationMessage` — returns message when cache has update, returns null when no update/no file/expired/corrupt
- [ ] Write tests: `GetNotificationMessage` never throws (even with broken paths)
- [ ] Write tests: `ShouldShow` returns false for `--quiet` and `-q`, true otherwise
- [ ] Implement `StartupUpdateNotifier` — synchronous cache read, quiet flag check, background refresh with cache-age guard
- [ ] Wire into `Program.cs`: show notification after version header (guarded by `ShouldShow`), fire background refresh
- [ ] Commit

### Task 6: Full Build and Verification

- [ ] `dotnet build PPDS.sln` — must succeed with no errors
- [ ] `dotnet test tests/PPDS.Cli.Tests --filter "Category!=Integration"` — all tests pass
- [ ] Manual smoke test (see verification checklist below)

---

## Manual Verification Checklist

Run after implementation, before PR:

1. `ppds version` — shows CLI version, SDK version, .NET, platform to stderr
2. `ppds version --check` — hits NuGet, shows latest stable/pre-release, no crash
3. Verify `~/.ppds/update-check.json` exists with valid JSON after step 2
4. Run `ppds auth list` (or any normal command) — startup notification appears if update available, goes to stderr
5. Run `ppds auth list --quiet` — update notification suppressed
6. Delete `~/.ppds/update-check.json`, run `ppds auth list` — no notification (cache gone), cache file reappears after ~5s (background refresh)
7. Corrupt the cache (`echo "broken" > ~/.ppds/update-check.json`), run `ppds version --check` — no crash, fetches fresh, overwrites corrupt file

---

## Design Decisions

### Why NuGet flat container API?
The flat container API (`/v3-flatcontainer/{id}/index.json`) returns a simple JSON array of version strings. No auth, no API key, no complex queries. Lightest-weight NuGet endpoint available.

### Why file-based caching?
The CLI is a short-lived process — in-memory caching doesn't persist. File-based caching in `~/.ppds/` follows the existing `QueryHistoryService` pattern and ensures the 24h TTL works across process lifetimes.

### Why synchronous cache read at startup?
Reading a single small JSON file from local disk. Sub-millisecond. Async would add complexity with no measurable benefit. Network refresh is background fire-and-forget.

### Why HttpMessageHandler injection instead of IHttpClientFactory?
The existing codebase creates `HttpClient` internally (see `ConnectionService`). We extend this pattern with optional `HttpMessageHandler?` injection for testability — the standard .NET pattern. `HttpClient` is scoped within `CheckAsync` (not held as a field), so no `IDisposable` needed on the service and no R1 concern with fire-and-forget background calls.

### Why no IProgressReporter?
Constitution A3 requires `IProgressReporter` for operations >1 second. `ppds version --check` makes a single HTTP GET with a 10-second timeout, but completes sub-second under normal network conditions. Progress reporting for a single HTTP request adds no user value — the "Checking for updates..." message provides sufficient feedback.

### Why no DI registration?
Nothing consumes `IUpdateCheckService` through DI. `VersionCommand` and `StartupUpdateNotifier` both construct the service directly. Adding a DI registration for a hypothetical future consumer is dead code. Add it when something needs it.

### Why stable update takes priority?
When a user is on pre-release `0.5.x-beta` and stable `0.6.0` is available, we suggest the plain `dotnet tool update` command. This aligns with PPDS odd/even versioning: odd-minor is the pre-release line leading to the next even-minor stable release.

### Why atomic cache writes?
`RefreshCacheInBackground` fires a `Task.Run` that writes the cache. If the CLI exits mid-write, the file is corrupted. Write to temp file + `File.Move` with overwrite prevents partial writes. The read path handles corruption gracefully regardless, but no reason to create it.
