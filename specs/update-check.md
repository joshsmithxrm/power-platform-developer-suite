# Update Check and Self-Update

**Status:** Implemented (partial — self-update not yet built)
**Last Updated:** 2026-03-15
**Code:** [src/PPDS.Cli/Services/UpdateCheck/](../src/PPDS.Cli/Services/UpdateCheck/), [src/PPDS.Cli/Commands/VersionCommand.cs](../src/PPDS.Cli/Commands/VersionCommand.cs), [src/PPDS.Cli/Infrastructure/StartupUpdateNotifier.cs](../src/PPDS.Cli/Infrastructure/StartupUpdateNotifier.cs)
**Surfaces:** CLI

---

## Overview

The update check system keeps PPDS CLI users informed about new versions and provides a mechanism to update in place. It queries the NuGet flat-container API, caches results locally for 24 hours, and surfaces notifications at startup and on-demand via `ppds version`. Users on pre-release tracks see both stable and pre-release update options.

### Goals

- **Non-disruptive awareness**: Startup notification from cached data (sub-millisecond), never blocking or slowing the CLI
- **Pre-release track support**: Users who opted into pre-release (odd minor) see both stable and pre-release updates, not just stable
- **Self-update**: `ppds version --update` performs the update without copy-paste, respecting Constitution S2 (no shell: true)

### Non-Goals

- Auto-update without user consent (always opt-in)
- Update channel management or pinning to specific versions
- NuGet feed authentication (public feed only — private/authenticated feeds are a different problem requiring feed URL config and credential management)
- Updating non-global tool installs via `ppds version --update` (show instructions only)

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                         Program.cs                               │
│  1. Create UpdateCheckService (lightweight, no HTTP)             │
│  2. Sync cache read → GetCachedResult()                          │
│  3. Format notification → StartupUpdateNotifier                  │
│  4. Fire-and-forget → RefreshCacheInBackgroundIfStale()          │
│  5. Register VersionCommand                                      │
└──────────┬──────────────────────┬────────────────────────────────┘
           │ startup              │ command
           ▼                     ▼
┌─────────────────────┐   ┌─────────────────────┐
│StartupUpdateNotifier│   │   VersionCommand     │
│ • ShouldShow(args)  │   │ • ppds version       │
│ • FormatNotification│   │ • --check (live)     │
│   (pure function)   │   │ • --update (self)    │
└─────────────────────┘   └──────────┬──────────┘
  No file I/O, no cache              │ calls service
  knowledge — just                    │
  result → string                    ▼
┌──────────────────────────────────────────────────────────────────┐
│                   IUpdateCheckService                             │
│                                                                  │
│  Cache:    GetCachedResult() → sync local read                   │
│  Network:  CheckAsync(currentVersion, ct) → NuGet API query      │
│  Refresh:  RefreshCacheInBackgroundIfStale(currentVersion)       │
│  Update:   UpdateAsync(channel, ct) → spawn dotnet tool update   │
│                                                                  │
│  Owns: cache format, TTL, file path, atomic writes               │
└──────────────────────┬───────────────────────────────────────────┘
                       │
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│                       NuGetVersion                               │
│  • SemVer 2.0 parsing and comparison                            │
│  • Pre-release segment logic                                     │
│  • Odd-minor detection                                           │
└──────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `NuGetVersion` | Immutable SemVer value type: parsing, comparison, pre-release segments, odd-minor detection |
| `IUpdateCheckService` | Service interface: cache reads (sync), NuGet queries (async), background refresh, self-update |
| `UpdateCheckService` | Implementation: NuGet flat-container API, 24h cache at ~/.ppds/update-check.json, atomic writes, process spawning for self-update |
| `UpdateCheckResult` | Sealed record: current/latest-stable/latest-pre-release versions, update commands, checked-at timestamp |
| `StartupUpdateNotifier` | Pure presentation: ShouldShow(args) and FormatNotification(result) — no I/O, no cache knowledge |
| `VersionCommand` | `ppds version [--check] [--update]`: delegates entirely to IUpdateCheckService |

### Dependencies

- Depends on: [cli.md](./cli.md) for command registration, GlobalOptions, exit codes
- Depends on: [architecture.md](./architecture.md) for Application Services pattern (A1, A2)
- Constrained by: Constitution S2 (shell: false for process spawn in self-update)

---

## Specification

### Versioning Convention

PPDS uses odd minor versions (0.5.x, 0.7.x) for pre-release lines and even minor versions (0.6.x, 0.8.x) for stable releases. A user running an odd-minor version is considered on the pre-release track. This convention drives notification logic — pre-release track users see both stable and pre-release update options.

### Core Requirements

1. Update checks never block or slow CLI execution — startup uses cached data only
2. NuGet API queries have a 10-second timeout; failures are silent (return null)
3. Cache TTL is 24 hours; atomic writes prevent corruption (temp file + move)
4. All cache format knowledge, TTL logic, and file I/O live in UpdateCheckService (A1)
5. StartupUpdateNotifier is pure presentation — result in, string out, no I/O
6. Pre-release track users see BOTH stable and pre-release updates when available
7. Self-update uses shell: false with direct dotnet executable path (S2)
8. Self-update is global-install only; non-global gets instructions, not automation
9. Status messages go to stderr; stdout is never written to (I1)
10. Unexpected failures from service throw PpdsException with ErrorCode (D4); expected outcomes (non-global install, already current) return result objects

### Primary Flows

**Startup Notification (every CLI invocation):**

1. **Check args**: StartupUpdateNotifier.ShouldShow(args) — false if --quiet/-q. Note: the notification block in Program.cs is already gated by SkipVersionHeaderArgs (--help, -h, --version, version), so ShouldShow only needs to handle --quiet/-q.
2. **Read cache**: UpdateCheckService.GetCachedResult() — sync local file read
3. **Format**: StartupUpdateNotifier.FormatNotification(result) — returns string or null
4. **Display**: Program.cs writes to stderr if non-null
5. **Refresh**: UpdateCheckService.RefreshCacheInBackgroundIfStale(currentVersion) — fire-and-forget if cache is older than TTL

**On-Demand Check (`ppds version --check`):**

1. **Display**: Write current version, SDK version, .NET version, platform to stderr
2. **Status**: Write "Checking for updates..." to stderr before network call
3. **Query**: Call UpdateCheckService.CheckAsync(currentVersion, ct) — hits NuGet API, updates cache
4. **Report**: Show latest stable version, latest pre-release version (if applicable), and update commands to stderr

**Self-Update (`ppds version --update`):**

1. **Check for updates**: Call CheckAsync() to determine target version. If no network and no cache, error with "Cannot determine latest version. Check your network connection." and show manual command as fallback.
2. **Detect install type**: Check AppContext.BaseDirectory against known global tool paths
3. **Non-global**: Print instructions ("Update your tool manifest: `dotnet tool update PPDS.Cli`") and exit with success
4. **Global**: Resolve dotnet executable path via `RuntimeEnvironment.GetRuntimeDirectory()` — navigate up 3 levels from the runtime directory (e.g., `shared/Microsoft.NETCore.App/10.0.4/` → dotnet install root) and append `dotnet[.exe]`. This is reliable because the runtime directory is always 3 levels below the dotnet install root. Fallback to DOTNET_ROOT env var + platform defaults. Note: `Process.MainModule.FileName` returns the apphost shim (`ppds.exe`), not `dotnet.exe` — do NOT use it.
5. **Guard**: Check for existing update-in-progress lock file (`~/.ppds/update.lock`). If present and the PID inside is still running, show "Update already in progress" and exit. Otherwise, delete stale lock and proceed.
6. **Confirm**: Show target version and ask for confirmation (unless --yes/-y flag)
7. **Execute**: Write a platform-specific wrapper script to a temp file. The wrapper: (a) writes lock file with its PID, (b) waits for the parent ppds process to exit (PID-based), (c) runs `dotnet tool update PPDS.Cli -g` with shell: false, (d) captures exit code and output, (e) writes result to `~/.ppds/update-status.json`, (f) deletes lock file. On Windows: `.cmd` script. On Linux/Mac: `.sh` script. The wrapper is spawned as a detached process — the parent ppds exits immediately.
8. **Next run**: Program.cs reads status file, shows result ("Updated to X.Y.Z" or "Update failed: ..."), deletes file

**Why a wrapper script?** `dotnet tool update PPDS.Cli -g` is a standard SDK command that knows nothing about our status file. The running ppds process locks the `.store` DLLs, so `dotnet tool update` fails unless ppds exits first. The wrapper script survives the parent exit, waits for the PID to terminate (unlocking .store), then runs the update and writes feedback. Tested: on Windows, the .store directory is locked while ppds runs, but the shim can be renamed. The update succeeds only after ppds exits.

**Self-Update Track Behavior:**

| Current | `--update` | `--update --stable` | `--update --prerelease` |
|---------|-----------|---------------------|------------------------|
| Stable (even minor) | Latest stable | Latest stable | Latest pre-release |
| Pre-release (odd minor) | Latest pre-release | Latest stable | Latest pre-release |

**Pre-Release Data Model:**

`CheckAsync()` always populates `LatestStableVersion` and `LatestPreReleaseVersion` when they exist on NuGet, regardless of the user's track. `StableUpdateAvailable` and `PreReleaseUpdateAvailable` are computed honestly based on version comparison. Track-based filtering is a **presentation concern**, not a data model concern — this ensures `ppds version --check` can always show all available versions so stable users can discover pre-release.

**Pre-Release Notification Logic (presentation layer):**

`StartupUpdateNotifier.FormatNotification()` and `VersionCommand` apply track filtering:

1. **User on stable** (even minor): Startup notification shows only stable updates. `ppds version --check` shows all versions.
2. **User on pre-release** (odd minor), newer stable + newer pre-release exist: Show BOTH — two lines
3. **User on pre-release** (odd minor), newer stable only: Show stable update
4. **User on pre-release** (odd minor), newer pre-release only: Show pre-release update
5. **Update commands**: Stable → `dotnet tool update PPDS.Cli -g`, Pre-release → `dotnet tool update PPDS.Cli -g --prerelease`

### Constraints

- HttpClient created per CheckAsync() call (using statement) — no IDisposable on service
- Cache path: ~/.ppds/update-check.json (follows existing ~/.ppds/ convention per architecture.md)
- NuGet endpoint: `https://api.nuget.org/v3-flatcontainer/ppds.cli/index.json` (public, unauthenticated)
- Background refresh is fire-and-forget — exceptions swallowed, logged at trace level
- Self-update spawns detached process — parent CLI exits, child completes the update
- No shell: true in process spawn (S2) — resolve dotnet path via RuntimeEnvironment.GetRuntimeDirectory(). Note: Process.MainModule.FileName returns the apphost shim (ppds.exe), NOT dotnet.exe for global tools.
- VersionCommand writes "Checking for updates..." to stderr before calling CheckAsync() — simple status line, no IOperationProgress (single HTTP call doesn't warrant progress interface overhead)
- RefreshCacheInBackgroundIfStale() intentionally takes no CancellationToken — fire-and-forget by design. Uses CancellationToken.None internally. Per R2, not accepting a token is correct; accepting and ignoring would violate R2.
- HttpClient-per-call is acceptable for a CLI tool making at most 1 request per 24 hours. Socket exhaustion (the usual concern with this pattern) requires hundreds of rapid disposals. R1 is satisfied by the `using` statement.
- UpdateAsync() spawns a detached process and the parent exits immediately — there is no long-running operation in the parent's lifetime. A3 (IProgressReporter for >1s operations) does not apply because the parent never waits. The detached child communicates results via the status file.
- The application obtains its current version from the assembly's informational version attribute, surfaced via `ErrorOutput.Version`. This is the single source of truth for the current version throughout the system.

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| Version string | Must parse as SemVer 2.0 (Major.Minor.Patch[-PreRelease][+Build]) | FormatException |
| Cache JSON | Must deserialize to UpdateCheckResult with valid CheckedAt | Silent discard |
| Dotnet path | Resolved via RuntimeEnvironment.GetRuntimeDirectory() (up 3 levels), DOTNET_ROOT, or platform defaults; must exist and be executable | UpdateCheck.DotnetNotFound |
| Install type | AppContext.BaseDirectory checked against global tool paths | Informational (not an error) |
| --stable + --prerelease | Mutually exclusive; providing both is a validation error | InvalidArguments exit code |

---

## Acceptance Criteria

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

### NuGetVersion

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Parses stable versions (1.0.0) into Major, Minor, Patch | `NuGetVersionTests.Parse_StableVersion_*` | ✅ |
| AC-02 | Parses pre-release versions (1.0.0-beta.1) preserving label | `NuGetVersionTests.Parse_PreReleaseVersion_*` | ✅ |
| AC-03 | Strips build metadata (+commitHash) before parsing | `NuGetVersionTests.Parse_BuildMetadata_*` | ✅ |
| AC-04 | Comparison: stable > pre-release at same major.minor.patch | `NuGetVersionTests.CompareTo_StableBeatsPreRelease` | ✅ |
| AC-05 | Comparison: pre-release segments compared per SemVer 2.0 (numeric as integers) | `NuGetVersionTests.CompareTo_MultiDigitNumericSegments` | ✅ |
| AC-06 | IsOddMinor returns true for odd minor versions (0.5.x, 0.7.x) | `NuGetVersionTests.IsOddMinor_*` | ✅ |
| AC-07 | TryParse returns false for null, empty, and malformed input (never throws) | `NuGetVersionTests.TryParse_*` | ✅ |
| AC-08 | Equality ignores build metadata (1.0.0+abc == 1.0.0+def) | `NuGetVersionTests.Equals_BuildMetadataIgnored` | ✅ |

### UpdateCheckService — Cache

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-09 | GetCachedResult() returns cached result when cache exists and is within 24h TTL | `UpdateCheckServiceTests.Cache_*` | ✅ |
| AC-10 | GetCachedResult() returns null when cache is expired (>24h) | `UpdateCheckServiceTests.Cache_Expired_*` | ✅ |
| AC-11 | GetCachedResult() returns null when cache file missing or corrupt (never throws) | `UpdateCheckServiceTests.Cache_Corrupt_*` | ✅ |
| AC-12 | GetCachedResult() is synchronous (no async, no Task) — structural constraint verified by API signature, not runtime test | Code review | 🔲 |
| AC-13 | Cache writes are atomic (temp file + move) to prevent corruption | `UpdateCheckServiceTests.Cache_Persisted_*` | ✅ |

### UpdateCheckService — Network

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-14 | CheckAsync() queries NuGet flat-container API and returns latest stable and pre-release | `UpdateCheckServiceTests.Returns_LatestStable_And_PreRelease` | ✅ |
| AC-15 | CheckAsync() returns null on network error (timeout, DNS, non-2xx) — never throws | `UpdateCheckServiceTests.NetworkError_*` | ✅ |
| AC-16 | CheckAsync() respects CancellationToken | `UpdateCheckServiceTests.CancellationToken_*` | ✅ |
| AC-17 | HttpClient created per call with 10s timeout, disposed after use | `UpdateCheckServiceTests` (structural) | ✅ |
| AC-18 | CheckAsync() updates cache on successful query | `UpdateCheckServiceTests.Cache_Persisted_*` | ✅ |

### UpdateCheckService — Pre-Release Track Logic

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-19 | CheckAsync() always populates both LatestStableVersion and LatestPreReleaseVersion when they exist on NuGet, regardless of user's track. Track-based filtering is a presentation concern (notifier/command), not a data model concern. | TBD | 🔲 |
| AC-20 | User on pre-release (odd minor), newer stable + newer pre-release exist: BOTH populated | TBD | 🔲 |
| AC-21 | User on pre-release (odd minor), newer stable only: LatestStableVersion populated, LatestPreReleaseVersion null | TBD | 🔲 |
| AC-22 | User on pre-release (odd minor), newer pre-release only: LatestPreReleaseVersion populated | TBD | 🔲 |
| AC-23 | UpdateCommand uses `--prerelease` flag only when targeting a pre-release version | TBD | 🔲 |

### UpdateCheckService — Self-Update

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-24 | UpdateAsync() detects global install via AppContext.BaseDirectory | TBD | 🔲 |
| AC-25 | Global install: writes platform-specific wrapper script, spawns it detached. Wrapper waits for parent PID exit, then runs `dotnet tool update`, writes status file, cleans up lock. | TBD | 🔲 |
| AC-26 | Non-global install: returns instructions string, does not spawn process | TBD | 🔲 |
| AC-27 | Resolves dotnet path via RuntimeEnvironment.GetRuntimeDirectory() (navigate up 3 levels) with fallback to DOTNET_ROOT + platform defaults. Does NOT use Process.MainModule (returns apphost shim, not dotnet). | TBD | 🔲 |
| AC-28 | Default behavior: updates to latest on current track (stable→stable, pre-release→pre-release) | TBD | 🔲 |
| AC-29 | --stable flag: switches to latest stable regardless of current track | TBD | 🔲 |
| AC-30 | --prerelease flag: switches to latest pre-release regardless of current track | TBD | 🔲 |
| AC-31 | Writes status file (~/.ppds/update-status.json) after update attempt; next invocation reads, displays, and deletes | TBD | 🔲 |

### StartupUpdateNotifier

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-32 | ShouldShow() returns false when args contain --quiet or -q. Other suppression (--help, --version, version subcommand) is handled by Program.cs SkipVersionHeaderArgs — no duplication. | `StartupUpdateNotifierTests.ShouldShow_*` | ✅ |
| AC-33 | FormatNotification() returns null when no update available | TBD | 🔲 |
| AC-34 | FormatNotification() returns single line for stable-only update | TBD | 🔲 |
| AC-35 | FormatNotification() returns two lines when both stable and pre-release available | TBD | 🔲 |
| AC-36 | FormatNotification() has NO file I/O — takes UpdateCheckResult?, returns string? | TBD | 🔲 |
| AC-37 | RefreshCacheInBackgroundIfStale() lives on UpdateCheckService, not on notifier | TBD | 🔲 |

### VersionCommand

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-38 | `ppds version` displays current version, SDK version, .NET version, platform to stderr | `VersionCommandTests` | ✅ |
| AC-39 | `ppds version --check` writes "Checking for updates..." then calls CheckAsync() and displays results to stderr | TBD | ❌ |
| AC-40 | `ppds version --update` calls UpdateAsync() for current track | TBD | 🔲 |
| AC-41 | `ppds version --update --stable` forces stable track | TBD | 🔲 |
| AC-42 | `ppds version --update --prerelease` forces pre-release track | TBD | 🔲 |
| AC-43 | `ppds version --update --yes` skips confirmation prompt | TBD | 🔲 |
| AC-44 | All output to stderr, nothing to stdout (I1) | TBD | ❌ |

### Integration

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-45 | Startup path is synchronous — no async calls before command dispatch | TBD | 🔲 |
| AC-46 | UpdateCheckService registered as singleton in DI | `ServiceRegistration` (structural) | ✅ |
| AC-47 | Unexpected service failures throw PpdsException with ErrorCode (D4); expected outcomes return result objects | TBD | 🔲 |
| AC-48 | `ppds version --check` writes status message to stderr before network call | TBD | 🔲 |
| AC-49 | `ppds version --update --stable --prerelease` rejected as mutually exclusive (InvalidArguments) | TBD | 🔲 |
| AC-50 | `ppds version --update` with no network and no cache shows error with manual command fallback | TBD | 🔲 |
| AC-51 | Current version obtained from assembly informational version attribute (ErrorOutput.Version) | Code review | 🔲 |
| AC-52 | Concurrent --update guarded by lock file (~/.ppds/update.lock) with PID; stale lock (dead PID) cleaned up automatically | TBD | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No network, no cache | Startup | No notification, no error — silent |
| No network, valid cache | Startup | Show cached notification normally |
| Cache file locked | GetCachedResult() | Return null, don't block |
| Already on latest | `--update` | "Already up to date" message, exit Success |
| NuGet returns empty list | CheckAsync() | Return result with no updates available (UpdateAvailable = false) |
| dotnet not found | `--update` | PpdsException with UpdateCheck.DotnetNotFound, show manual command |
| Update process fails | Detached process | Status file records failure, next run shows error + manual command |
| --stable and --prerelease both provided | `--update --stable --prerelease` | Validation error, InvalidArguments exit code |
| --update with no network, no cache | `--update` (offline, first run) | Error message + manual command fallback |
| --help or --version at startup | `ppds --help` | No update notification shown |
| Concurrent --update while update running | `--update` twice quickly | Second invocation shows "Update already in progress", exits Success |
| Stale lock file (dead PID) | `--update` after crash | Lock file cleaned up, update proceeds |

---

## Core Types

### NuGetVersion

Immutable SemVer 2.0 reference type. Handles parsing, comparison, and pre-release segment logic. Build metadata is stripped on parse and ignored in equality. Implements `IComparable<NuGetVersion>` and `IEquatable<NuGetVersion>` with manual equality for correct null handling.

```csharp
public sealed class NuGetVersion : IComparable<NuGetVersion>, IEquatable<NuGetVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string PreReleaseLabel { get; }  // empty string for stable, non-nullable to avoid null checks in comparison
    public bool IsPreRelease { get; }
    public bool IsOddMinor { get; }
    public static NuGetVersion Parse(string version);
    public static bool TryParse(string? version, out NuGetVersion result);
}
```

Implementation: [`NuGetVersion.cs`](../src/PPDS.Cli/Services/UpdateCheck/NuGetVersion.cs)

### UpdateCheckResult

Sealed record capturing the result of a version check — both what's current and what's available.

```csharp
public sealed record UpdateCheckResult
{
    public string CurrentVersion { get; init; }
    public string? LatestStableVersion { get; init; }
    public string? LatestPreReleaseVersion { get; init; }
    public bool StableUpdateAvailable { get; init; }
    public bool PreReleaseUpdateAvailable { get; init; }
    public string? UpdateCommand { get; init; }
    public string? PreReleaseUpdateCommand { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
}
```

Implementation: [`UpdateCheckResult.cs`](../src/PPDS.Cli/Services/UpdateCheck/UpdateCheckResult.cs)

### IUpdateCheckService

```csharp
public interface IUpdateCheckService
{
    // Cache: sync local file read, returns null if missing/expired/corrupt
    UpdateCheckResult? GetCachedResult();

    // Network: queries NuGet API, updates cache on success, returns null on failure
    Task<UpdateCheckResult?> CheckAsync(string currentVersion,
        CancellationToken ct = default);

    // Background: fire-and-forget cache refresh if stale (no CancellationToken per R2)
    void RefreshCacheInBackgroundIfStale(string currentVersion);

    // Self-update: returns UpdateResult for expected outcomes (non-global, already current);
    // throws PpdsException for unexpected failures (dotnet not found, spawn failed)
    Task<UpdateResult> UpdateAsync(UpdateChannel channel,
        CancellationToken ct = default);
}
```

### UpdateChannel

```csharp
public enum UpdateChannel
{
    Current,      // Stay on current track (default for --update)
    Stable,       // Force stable (--update --stable)
    PreRelease    // Force pre-release (--update --prerelease)
}
```

### UpdateResult

```csharp
public sealed record UpdateResult
{
    public bool Success { get; init; }
    public string? InstalledVersion { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsNonGlobalInstall { get; init; }
    public string? ManualCommand { get; init; }
}
```

---

## Error Handling

### Error Types

| Error | Code | Condition | Recovery |
|-------|------|-----------|----------|
| Network failure | `UpdateCheck.NetworkError` | NuGet API unreachable, timeout, non-2xx | Silent — return null, use cache |
| Cache corrupt | `UpdateCheck.CacheCorrupt` | JSON parse failure on cache file | Silent — delete cache, return null |
| Dotnet not found | `UpdateCheck.DotnetNotFound` | Cannot resolve dotnet executable path | Show manual command, exit with Failure |
| Update failed | `UpdateCheck.UpdateFailed` | dotnet tool update exited non-zero | Write failure to status file, show error on next run |
| Non-global install | `UpdateCheck.NonGlobalInstall` | AppContext.BaseDirectory not in global tool path | Not an error — show instructions, exit Success |

### Recovery Strategies

- **Check/cache failures**: Always silent. The update check system must never degrade the CLI experience. Return null and move on.
- **Self-update failures**: Write to status file. Next invocation shows the error with the manual command as fallback. User is never stuck.

---

## Design Decisions

### Why Sync Cache Reads?

**Context:** Startup notification must not add latency. The cache file is <1KB local JSON.

**Decision:** GetCachedResult() is synchronous. File.ReadAllText on a <1KB file is sub-millisecond. No async overhead, no Task allocation on the hot startup path.

**Alternatives considered:**
- Async with ValueTask: Overhead not justified for a local file read
- Memory-mapped file: Over-engineered for <1KB

### Why Detached Wrapper Script for Self-Update?

**Context:** On Windows, the running ppds process locks DLLs in `~/.dotnet/tools/.store/ppds.cli/<version>/`. `dotnet tool update` fails with "Access to the path is denied" while ppds is running. Tested empirically: the .store directory is locked, but the shim can be renamed. The update succeeds only after ppds exits.

**Decision:** Write a platform-specific wrapper script (`.cmd` on Windows, `.sh` on Linux/Mac) to a temp file, spawn it as a detached process, parent exits immediately. The wrapper: waits for parent PID to exit (unlocking .store), runs `dotnet tool update`, captures exit code, writes status JSON, deletes lock file. Status file provides feedback on next invocation.

**Alternatives considered:**
- In-process wait: Fails on Windows — .store DLLs locked by running process (tested)
- Self-hosted wrapper (`ppds --internal-update`): The new ppds instance also loads from .store
- No wrapper, just spawn dotnet directly: `dotnet tool update` is a standard SDK command that doesn't know about our status file; also fails while ppds holds .store lock
- No feedback: User left wondering if it worked

**Consequences:**
- Positive: Works on all platforms — parent exits, wrapper waits, then updates
- Positive: Status file is deterministic and testable
- Positive: Lock file prevents concurrent update races
- Negative: Feedback delayed until next invocation
- Negative: Platform-specific scripts (`.cmd` vs `.sh`), but each is <20 lines

### Why RuntimeEnvironment.GetRuntimeDirectory for Dotnet Path?

**Context:** Self-update needs to invoke `dotnet tool update` without shell: true (S2). For global tools, `Process.MainModule.FileName` returns the apphost shim (`~/.dotnet/tools/ppds.exe`), NOT `dotnet.exe`. `DOTNET_HOST_PATH` and `DOTNET_ROOT` are not reliably set.

**Decision:** Use `RuntimeEnvironment.GetRuntimeDirectory()` and navigate up 3 levels. The runtime directory is always `<dotnet-root>/shared/Microsoft.NETCore.App/<version>/`, so going up 3 levels reaches the dotnet install root. Append `dotnet.exe` (Windows) or `dotnet` (Linux/Mac). Tested: returns `C:\Program Files\dotnet\dotnet.exe` correctly. Fallback to DOTNET_ROOT env var + platform-specific default paths.

**Test results:**
| Approach | Returns | Correct? |
|----------|---------|----------|
| `Process.MainModule.FileName` | `~/.dotnet/tools/ppds.exe` (apphost shim) | No |
| `DOTNET_HOST_PATH` env var | (not set) | N/A |
| `DOTNET_ROOT` env var | (not set) | N/A |
| `RuntimeEnvironment.GetRuntimeDirectory()` up 3 | `C:\Program Files\dotnet\dotnet.exe` | Yes |

**Alternatives considered:**
- `Process.MainModule.FileName`: Returns the apphost shim, not dotnet (tested)
- `DOTNET_HOST_PATH` env var: Not reliably set for global tools (tested)
- Hardcoded well-known paths only: Fragile, misses custom installs
- PATH search: Reimplements shell behavior, platform-inconsistent
- shell: true: Violates S2

### Why Pure Presentation Notifier?

**Context:** StartupUpdateNotifier originally contained file I/O and cache TTL logic, duplicating knowledge from UpdateCheckService. This violated A1 (business logic in Application Services).

**Decision:** Refactor to pure functions — ShouldShow(args) and FormatNotification(result). All cache/file knowledge lives in UpdateCheckService.

**Consequences:**
- Positive: Testable without file system, single source of truth for cache format
- Positive: Notifier tests are instant (no temp directories)
- Negative: Requires refactoring existing implementation before release

### Why HttpClient Per Call?

**Context:** .NET guidance warns against disposing HttpClient frequently due to socket exhaustion.

**Decision:** Create and dispose HttpClient per `CheckAsync()` call. The CLI makes at most one HTTP request per 24 hours (cache TTL). Socket exhaustion requires hundreds of rapid disposals — this pattern is safe for low-frequency use. R1 is satisfied by the `using` statement.

**Alternatives considered:**
- Singleton HttpClient on the service: Requires IDisposable on the service, complicates DI lifetime
- IHttpClientFactory: Over-engineered for a single endpoint called once per day

### Why No IProgressReporter on UpdateAsync?

**Context:** Constitution A3 requires IProgressReporter for operations >1 second. Self-update spawns a detached process.

**Decision:** The parent process exits immediately after spawning the detached update. There is no long-running operation in the parent's lifetime — the child process does all the work. A3 does not apply because the parent never waits. Results are communicated via the status file on next invocation.

### Why Track-Based Self-Update?

**Context:** When a pre-release user runs `--update`, should they stay on pre-release or switch to stable?

**Decision:** Stay on current track by default (VS Code model). Explicit `--stable`/`--prerelease` flags to switch. The startup notification already informs users about other track options.

**Alternatives considered:**
- Always prefer stable: Surprising for pre-release users, effectively a downgrade in features
- Interactive picker: Breaks `--yes` scripting, over-complicated

**Consequences:**
- Positive: Respects user intent, predictable, scriptable
- Positive: Startup notification handles cross-track awareness
- Negative: User must know to use `--stable` to switch back

---

## Configuration

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| Cache path | string | ~/.ppds/update-check.json | Version check cache location |
| Cache TTL | TimeSpan | 24 hours | How long cached results are valid |
| Status file | string | ~/.ppds/update-status.json | Self-update result for next-run feedback |
| HTTP timeout | TimeSpan | 10 seconds | NuGet API request timeout |
| Lock file | string | ~/.ppds/update.lock | Prevents concurrent self-update races (contains wrapper PID) |

No user-configurable settings in v1. All values are compile-time constants. Future: cache TTL and opt-out could move to ~/.ppds/settings.json if needed.

---

## Test Examples

```csharp
[Fact]
public void CheckAsync_PreReleaseUser_BothUpdatesAvailable_PopulatesBoth()
{
    // Arrange: user on 0.5.0-beta.1, stable 0.6.0 and pre-release 0.7.0-alpha.1 exist
    var handler = BuildHandler(MakeVersionsJson("0.4.0", "0.5.0-beta.1", "0.6.0", "0.7.0-alpha.1"));
    var service = new UpdateCheckService(handler: handler, cachePath: _tempCache);

    // Act
    var result = await service.CheckAsync("0.5.0-beta.1", CancellationToken.None);

    // Assert
    Assert.NotNull(result);
    Assert.True(result.StableUpdateAvailable);
    Assert.True(result.PreReleaseUpdateAvailable);
    Assert.Equal("0.6.0", result.LatestStableVersion);
    Assert.Equal("0.7.0-alpha.1", result.LatestPreReleaseVersion);
    Assert.DoesNotContain("--prerelease", result.UpdateCommand);
    Assert.Contains("--prerelease", result.PreReleaseUpdateCommand);
}

[Fact]
public void FormatNotification_BothUpdatesAvailable_ReturnsTwoLines()
{
    // Arrange
    var result = new UpdateCheckResult
    {
        StableUpdateAvailable = true,
        PreReleaseUpdateAvailable = true,
        LatestStableVersion = "0.6.0",
        LatestPreReleaseVersion = "0.7.0-alpha.1",
        UpdateCommand = "dotnet tool update PPDS.Cli -g",
        PreReleaseUpdateCommand = "dotnet tool update PPDS.Cli -g --prerelease"
    };

    // Act
    var message = StartupUpdateNotifier.FormatNotification(result);

    // Assert: two distinct update lines
    Assert.NotNull(message);
    var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.Equal(2, lines.Length);
}
```

---

## Related Specs

- [cli.md](./cli.md) — Command registration, GlobalOptions, exit codes, stderr/stdout contract (needs update: add `version` command to command groups table, confirm exit code mapping)
- [architecture.md](./architecture.md) — Application Services pattern (A1), DI registration, error handling (needs update: add IUpdateCheckService to service inventory, add update-check.json and update-status.json to shared local state)

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-18 | Added Surfaces frontmatter, Changelog per spec governance |

---

## Roadmap

- User-configurable update check opt-out in ~/.ppds/settings.json
- Notification frequency tuning (e.g., remind once per week, not every invocation)
