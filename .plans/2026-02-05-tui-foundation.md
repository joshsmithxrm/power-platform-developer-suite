# TUI Foundation Refactoring Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Refactor TUI foundation for multi-environment tabs, screen base class, splash screen, and pattern consolidation before scaling to additional screens.

**Architecture:** Shared `InteractiveSession` caches multiple `ServiceProvider` instances keyed by environment URL. A `TabManager` + custom `TabBar` View replace the screen stack. All screens inherit `TuiScreenBase` for lifecycle boilerplate. A splash screen provides branded startup.

**Tech Stack:** C# (.NET 8/9/10), Terminal.Gui 1.19, xUnit

**Worktree:** `.worktrees/tui-foundation` on branch `feature/tui-foundation`

**Spec:** `specs/tui-foundation.md`

**Important context for implementors:**
- This is a Terminal.Gui 1.19 application (NOT v2 — no TabView control exists)
- All TUI code lives under `src/PPDS.Cli/Tui/`
- Tests live under `tests/PPDS.Cli.Tests/Tui/`
- Tests use `[Trait("Category", "TuiUnit")]` — run with `--filter Category=TuiUnit`
- The `ITuiStateCapture<TState>` pattern enables testing without Terminal.Gui initialization
- Fire-and-forget async uses `#pragma warning disable PPDS013` — the new helper eliminates this
- `ErrorOutput.Version` (from `src/PPDS.Cli/Commands/ErrorOutput.cs`) returns the product version string
- `TuiColorPalette` (at `src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs`) provides all color schemes
- `TuiSpinner` (at `src/PPDS.Cli/Tui/Views/TuiSpinner.cs`) provides animated braille spinner

**Build & test commands:**
```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

---

## Task 1: AsyncHelper — Fire-and-Forget Consolidation

**Files:**
- Create: `src/PPDS.Cli/Tui/Infrastructure/AsyncHelper.cs`
- Test: `tests/PPDS.Cli.Tests/Tui/Infrastructure/AsyncHelperTests.cs`

**Step 1: Write the tests**

Create `tests/PPDS.Cli.Tests/Tui/Infrastructure/AsyncHelperTests.cs`:

```csharp
using PPDS.Cli.Tui.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

[Trait("Category", "TuiUnit")]
public sealed class AsyncHelperTests
{
    [Fact]
    public async Task FireAndForget_ReportsError_OnFaultedTask()
    {
        var errorService = new TuiErrorService();
        var faultedTask = Task.FromException(new InvalidOperationException("test error"));

        errorService.FireAndForget(faultedTask, "TestContext");

        // Allow ContinueWith to execute
        await Task.Delay(100);

        Assert.Single(errorService.RecentErrors);
        Assert.Contains("TestContext", errorService.RecentErrors[0].Context);
    }

    [Fact]
    public async Task FireAndForget_DoesNotReportError_OnSuccessfulTask()
    {
        var errorService = new TuiErrorService();

        errorService.FireAndForget(Task.CompletedTask, "TestContext");

        await Task.Delay(50);

        Assert.Empty(errorService.RecentErrors);
    }

    [Fact]
    public async Task FireAndForget_UnwrapsAggregateException()
    {
        var errorService = new TuiErrorService();
        var inner = new InvalidOperationException("inner");
        var faultedTask = Task.FromException(new AggregateException(inner));

        errorService.FireAndForget(faultedTask, "AggregateTest");

        await Task.Delay(100);

        Assert.Single(errorService.RecentErrors);
    }

    [Fact]
    public void FireAndForget_DoesNotThrow_WhenCalledSynchronously()
    {
        var errorService = new TuiErrorService();
        var exception = Record.Exception(() =>
            errorService.FireAndForget(Task.FromException(new Exception("boom")), "Sync"));

        Assert.Null(exception);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "FullyQualifiedName~AsyncHelperTests" --nologo --verbosity quiet`

Expected: Build failure — `AsyncHelper` / `FireAndForget` does not exist.

**Step 3: Write the implementation**

Create `src/PPDS.Cli/Tui/Infrastructure/AsyncHelper.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Extension methods for fire-and-forget async patterns with centralized error reporting.
/// Replaces scattered #pragma warning disable PPDS013 blocks.
/// </summary>
internal static class AsyncHelper
{
    /// <summary>
    /// Runs a task fire-and-forget with error reporting via the error service.
    /// </summary>
    /// <param name="errorService">The error service to report failures to.</param>
    /// <param name="task">The task to run.</param>
    /// <param name="context">Context string for error reporting (e.g., "SwitchProfile").</param>
    /// <param name="caller">Auto-populated caller method name.</param>
    public static void FireAndForget(
        this ITuiErrorService errorService,
        Task task,
        string context,
        [CallerMemberName] string? caller = null)
    {
#pragma warning disable PPDS013 // Intentional fire-and-forget — this IS the centralized handler
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                errorService.ReportError(
                    "Background operation failed",
                    t.Exception,
                    $"{context} (from {caller})");
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "FullyQualifiedName~AsyncHelperTests" --nologo --verbosity quiet`

Expected: 4 passing.

**Step 5: Run full TUI test suite for regression**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet`

Expected: All 49 existing + 4 new = 53 passing.

**Step 6: Commit**

```bash
git add src/PPDS.Cli/Tui/Infrastructure/AsyncHelper.cs tests/PPDS.Cli.Tests/Tui/Infrastructure/AsyncHelperTests.cs
git commit -m "feat(tui): add AsyncHelper.FireAndForget extension method

Centralizes the fire-and-forget-with-error-reporting pattern that was
copy-pasted ~10 times across TuiShell and SqlQueryScreen. Callers
replace 5 lines of pragma/ContinueWith boilerplate with one call.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 2: Replace Fire-and-Forget Boilerplate in TuiShell

**Files:**
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs` (lines 430-438, 470-478, 496-504, 555-577, 582-590)

**Step 1: Replace all 5 fire-and-forget blocks in TuiShell.cs**

Replace block at lines 430-438 (`ShowProfileSelector`):
```csharp
// Before (lines 430-438):
#pragma warning disable PPDS013
            _ = SetActiveProfileAsync(dialog.SelectedProfile).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _errorService.ReportError("Failed to switch profile", t.Exception, "SwitchProfile");
                }
            }, TaskScheduler.Default);
#pragma warning restore PPDS013

// After:
            _errorService.FireAndForget(SetActiveProfileAsync(dialog.SelectedProfile), "SwitchProfile");
```

Replace block at lines 470-478 (`ShowEnvironmentSelector`):
```csharp
// Before:
#pragma warning disable PPDS013
                _ = SetEnvironmentAsync(url, name).ContinueWith(t => ...
#pragma warning restore PPDS013

// After:
                _errorService.FireAndForget(SetEnvironmentAsync(url, name), "SetEnvironment");
```

Replace block at lines 496-504 (`ShowProfileCreation`):
```csharp
// Before:
#pragma warning disable PPDS013
            _ = SetActiveProfileWithEnvironmentAsync(dialog.CreatedProfile, envUrl, envName).ContinueWith(t => ...
#pragma warning restore PPDS013

// After:
            _errorService.FireAndForget(
                SetActiveProfileWithEnvironmentAsync(dialog.CreatedProfile, envUrl, envName),
                "ProfileCreation");
```

Replace block at lines 555-577 (`RefreshProfileState`):
```csharp
// Before:
#pragma warning disable PPDS013
        _ = Task.Run(async () =>
        {
            var profileService = _session.GetProfileService();
            ...
        });
#pragma warning restore PPDS013

// After:
        _errorService.FireAndForget(Task.Run(async () =>
        {
            var profileService = _session.GetProfileService();
            var profiles = await profileService.GetProfilesAsync();
            var active = profiles.FirstOrDefault(p => p.IsActive);

            Application.MainLoop?.Invoke(() =>
            {
                if (active != null)
                {
                    _environmentName = active.EnvironmentName;
                    _environmentUrl = active.EnvironmentUrl;
                }
                else
                {
                    _environmentName = null;
                    _environmentUrl = null;
                }
                _statusBar.Refresh();
            });
        }), "RefreshProfileState");
```

Replace block at lines 582-590 (`LoadProfileInfoAsync`):
```csharp
// Before:
#pragma warning disable PPDS013
        _ = LoadProfileInfoInternalAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                _errorService.ReportError("Failed to load profile info", t.Exception, "LoadProfileInfo");
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013

// After:
        _errorService.FireAndForget(LoadProfileInfoInternalAsync(), "LoadProfileInfo");
```

**Step 2: Replace fire-and-forget blocks in SqlQueryScreen.cs**

In `SqlQueryScreen.cs`, replace the history save at line 500-502:
```csharp
// Before:
#pragma warning disable PPDS013
            _ = SaveToHistoryAsync(sql, result.Result.Count, result.Result.ExecutionTimeMs);
#pragma warning restore PPDS013

// After (history save failures are non-critical, use error service):
            _errorService.FireAndForget(
                SaveToHistoryAsync(sql, result.Result.Count, result.Result.ExecutionTimeMs),
                "SaveHistory");
```

Replace the FetchXML dialog at lines 722-735:
```csharp
// Before:
#pragma warning disable PPDS013
        _ = ShowFetchXmlDialogAsync(sql).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    MessageBox.ErrorQuery("FetchXML Error", ...);
                });
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013

// After:
        _errorService.FireAndForget(ShowFetchXmlDialogAsync(sql), "ShowFetchXml");
```

Also replace the fire-and-forget in `PpdsApplication.cs` at line 92:
```csharp
// Before:
#pragma warning disable PPDS013
        _ = _session.InitializeAsync(cancellationToken);
#pragma warning restore PPDS013

// After:
        _session.GetErrorService().FireAndForget(_session.InitializeAsync(cancellationToken), "SessionInit");
```

**Step 3: Build and run full TUI test suite**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet`

Then: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet`

Expected: All 53 tests pass, zero build errors.

**Step 4: Commit**

```bash
git add src/PPDS.Cli/Tui/TuiShell.cs src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs src/PPDS.Cli/Tui/PpdsApplication.cs
git commit -m "refactor(tui): replace fire-and-forget boilerplate with AsyncHelper

Replaces 8 instances of #pragma disable PPDS013 / ContinueWith blocks
with single-line _errorService.FireAndForget() calls across TuiShell,
SqlQueryScreen, and PpdsApplication.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 3: Cache Local Services in InteractiveSession

**Files:**
- Modify: `src/PPDS.Cli/Tui/InteractiveSession.cs` (lines 453-523)
- Test: `tests/PPDS.Cli.Tests/Tui/Infrastructure/ServiceCachingTests.cs`

**Step 1: Write the tests**

Create `tests/PPDS.Cli.Tests/Tui/Infrastructure/ServiceCachingTests.cs`:

```csharp
using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

[Trait("Category", "TuiUnit")]
public sealed class ServiceCachingTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly InteractiveSession _session;

    public ServiceCachingTests()
    {
        _tempStore = new TempProfileStore();
        _session = new InteractiveSession(
            null,
            _tempStore.Store,
            new MockServiceProviderFactory());
    }

    public void Dispose()
    {
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tempStore.Dispose();
    }

    [Fact]
    public void GetProfileService_ReturnsSameInstance()
    {
        var first = _session.GetProfileService();
        var second = _session.GetProfileService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetEnvironmentService_ReturnsSameInstance()
    {
        var first = _session.GetEnvironmentService();
        var second = _session.GetEnvironmentService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetThemeService_ReturnsSameInstance()
    {
        var first = _session.GetThemeService();
        var second = _session.GetThemeService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetQueryHistoryService_ReturnsSameInstance()
    {
        var first = _session.GetQueryHistoryService();
        var second = _session.GetQueryHistoryService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetExportService_ReturnsSameInstance()
    {
        var first = _session.GetExportService();
        var second = _session.GetExportService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetErrorService_ReturnsSameInstance()
    {
        // Already cached — verify it still works
        var first = _session.GetErrorService();
        var second = _session.GetErrorService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetHotkeyRegistry_ReturnsSameInstance()
    {
        // Already cached — verify it still works
        var first = _session.GetHotkeyRegistry();
        var second = _session.GetHotkeyRegistry();
        Assert.Same(first, second);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "FullyQualifiedName~ServiceCachingTests" --nologo --verbosity quiet`

Expected: `GetProfileService_ReturnsSameInstance`, `GetEnvironmentService_ReturnsSameInstance`, `GetThemeService_ReturnsSameInstance`, `GetQueryHistoryService_ReturnsSameInstance` FAIL (returns different instances). `GetExportService_ReturnsSameInstance` fails (method doesn't exist). `GetErrorService` and `GetHotkeyRegistry` pass (already cached).

**Step 3: Implement the caching + add GetExportService**

In `src/PPDS.Cli/Tui/InteractiveSession.cs`, add fields after line 48:

```csharp
    private IProfileService? _profileService;
    private IEnvironmentService? _environmentService;
    private ITuiThemeService? _themeService;
    private IQueryHistoryService? _queryHistoryService;
    private IExportService? _exportService;
```

Add a `using` for `PPDS.Cli.Services.Export` at the top of the file.

Replace the local service methods (lines 460-522) with cached versions:

```csharp
    public IProfileService GetProfileService()
    {
        return _profileService ??= new ProfileService(_profileStore, NullLogger<ProfileService>.Instance);
    }

    public IEnvironmentService GetEnvironmentService()
    {
        return _environmentService ??= new EnvironmentService(_profileStore, NullLogger<EnvironmentService>.Instance);
    }

    public ProfileStore GetProfileStore()
    {
        return _profileStore;
    }

    public ITuiThemeService GetThemeService()
    {
        return _themeService ??= new TuiThemeService();
    }

    public ITuiErrorService GetErrorService()
    {
        return _errorService ??= new TuiErrorService();
    }

    public IHotkeyRegistry GetHotkeyRegistry()
    {
        return _hotkeyRegistry ??= new HotkeyRegistry();
    }

    public IQueryHistoryService GetQueryHistoryService()
    {
        return _queryHistoryService ??= new QueryHistoryService(NullLogger<QueryHistoryService>.Instance);
    }

    public IExportService GetExportService()
    {
        return _exportService ??= new ExportService(NullLogger<ExportService>.Instance);
    }
```

**Step 4: Update SqlQueryScreen to use session's ExportService**

In `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs` at line 681, replace:

```csharp
// Before:
        var exportService = new ExportService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ExportService>.Instance);

// After:
        var exportService = _session.GetExportService();
```

Remove the `using PPDS.Cli.Services.Export;` from SqlQueryScreen if it was only used for `new ExportService(...)`. (Check — it's also used for `IExportService` type in the ExportDialog constructor, so keep it if needed.)

**Step 5: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "FullyQualifiedName~ServiceCachingTests" --nologo --verbosity quiet`

Expected: All 7 pass.

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet`

Expected: All 60 pass (53 previous + 7 new).

**Step 6: Commit**

```bash
git add src/PPDS.Cli/Tui/InteractiveSession.cs src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs tests/PPDS.Cli.Tests/Tui/Infrastructure/ServiceCachingTests.cs
git commit -m "refactor(tui): cache local services in InteractiveSession

Apply lazy-singleton pattern to GetProfileService, GetEnvironmentService,
GetThemeService, GetQueryHistoryService. Add GetExportService to session
(was previously instantiated directly in SqlQueryScreen). Consistent with
existing GetErrorService/GetHotkeyRegistry caching.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 4: Multi-Environment ServiceProvider Caching

**Files:**
- Modify: `src/PPDS.Cli/Tui/InteractiveSession.cs` (lines 43-44, 201-251, 317-339, 152-192, 386-451, 527-578)
- Modify: `tests/PPDS.Cli.Tests/Tui/InteractiveSessionLifecycleTests.cs`
- Test: `tests/PPDS.Cli.Tests/Tui/MultiEnvironmentSessionTests.cs`

**Step 1: Write the tests**

Create `tests/PPDS.Cli.Tests/Tui/MultiEnvironmentSessionTests.cs`:

```csharp
using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using Xunit;

namespace PPDS.Cli.Tests.Tui;

[Trait("Category", "TuiUnit")]
public sealed class MultiEnvironmentSessionTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly MockServiceProviderFactory _mockFactory;

    public MultiEnvironmentSessionTests()
    {
        _tempStore = new TempProfileStore();
        _mockFactory = new MockServiceProviderFactory();
    }

    public void Dispose()
    {
        _tempStore.Dispose();
    }

    private InteractiveSession CreateSession() =>
        new(null, _tempStore.Store, _mockFactory);

    [Fact]
    public async Task GetServiceProvider_CachesByUrl_IndependentProviders()
    {
        await using var session = CreateSession();

        var providerDev = await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        var providerProd = await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");
        var providerDev2 = await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");

        Assert.Same(providerDev, providerDev2);
        Assert.NotSame(providerDev, providerProd);
        Assert.Equal(2, _mockFactory.CreationLog.Count);
    }

    [Fact]
    public async Task GetServiceProvider_ThreeEnvironments_ThreeProviders()
    {
        await using var session = CreateSession();

        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://qa.crm4.dynamics.com");
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");

        Assert.Equal(3, _mockFactory.CreationLog.Count);
    }

    [Fact]
    public async Task InvalidateAsync_SpecificUrl_OnlyRemovesThatProvider()
    {
        await using var session = CreateSession();

        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");

        await session.InvalidateAsync("https://dev.crm.dynamics.com");

        // Prod should still be cached
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");
        Assert.Equal(2, _mockFactory.CreationLog.Count); // No new creation for prod

        // Dev should create a new provider
        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        Assert.Equal(3, _mockFactory.CreationLog.Count); // New creation for dev
    }

    [Fact]
    public async Task InvalidateAsync_AllUrls_ClearsCache()
    {
        await using var session = CreateSession();

        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");

        await session.InvalidateAsync(); // No URL = invalidate all

        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");

        Assert.Equal(4, _mockFactory.CreationLog.Count); // 2 original + 2 recreated
    }

    [Fact]
    public async Task SetActiveProfile_InvalidatesAllProviders()
    {
        await using var session = CreateSession();
        await _tempStore.CreateProfileAsync("profile1");
        await _tempStore.CreateProfileAsync("profile2");

        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        await session.GetServiceProviderAsync("https://prod.crm.dynamics.com");

        await session.SetActiveProfileAsync("profile2", "https://dev.crm.dynamics.com");

        // Both should need recreation since credentials changed
        await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        Assert.Equal(3, _mockFactory.CreationLog.Count); // 2 original + 1 after profile switch (warm) already created dev during SetActiveProfile
    }

    [Fact]
    public async Task SetEnvironment_DoesNotInvalidateOtherProviders()
    {
        await using var session = CreateSession();

        var providerDev = await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");

        await session.SetEnvironmentAsync("https://prod.crm.dynamics.com", "PROD");

        // Dev provider should still be cached
        var providerDev2 = await session.GetServiceProviderAsync("https://dev.crm.dynamics.com");
        Assert.Same(providerDev, providerDev2);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "FullyQualifiedName~MultiEnvironmentSessionTests" --nologo --verbosity quiet`

Expected: Multiple failures — current `GetServiceProviderAsync` disposes the old provider when URL changes.

**Step 3: Implement multi-provider caching**

In `src/PPDS.Cli/Tui/InteractiveSession.cs`:

Add `using System.Collections.Concurrent;` at the top.

Replace the single-provider fields (lines 43-44):
```csharp
// Remove these two lines:
    private ServiceProvider? _serviceProvider;
    private string? _currentEnvironmentUrl;

// Add:
    private readonly ConcurrentDictionary<string, ServiceProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
```

Keep `_currentEnvironmentUrl` as a separate field but only for tracking the "active" environment for status bar display:
```csharp
    private string? _activeEnvironmentUrl;
    private string? _activeEnvironmentDisplayName;
```

Update all references from `_currentEnvironmentUrl` to `_activeEnvironmentUrl` and `_currentEnvironmentDisplayName` to `_activeEnvironmentDisplayName`. The public properties `CurrentEnvironmentUrl` and `CurrentEnvironmentDisplayName` continue to work.

Replace `GetServiceProviderAsync` (lines 201-251):
```csharp
    public async Task<ServiceProvider> GetServiceProviderAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Fast path: already cached
        if (_providers.TryGetValue(environmentUrl, out var existing))
        {
            TuiDebugLog.Log($"Reusing existing provider for {environmentUrl}");
            return existing;
        }

        // Slow path: create new provider (serialized to prevent duplicate creation)
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after lock
            if (_providers.TryGetValue(environmentUrl, out existing))
            {
                return existing;
            }

            TuiDebugLog.Log($"Creating new provider for {environmentUrl}, profile={_profileName}");

            var provider = await _serviceProviderFactory.CreateAsync(
                string.IsNullOrEmpty(_profileName) ? null : _profileName,
                environmentUrl,
                _deviceCodeCallback,
                _beforeInteractiveAuth,
                cancellationToken).ConfigureAwait(false);

            _providers[environmentUrl] = provider;
            TuiDebugLog.Log($"Provider created successfully for {environmentUrl}");
            return provider;
        }
        finally
        {
            _lock.Release();
        }
    }
```

Replace `InvalidateAsync` (lines 317-339):
```csharp
    public async Task InvalidateAsync(
        string? environmentUrl = null,
        [CallerMemberName] string? caller = null,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = 0)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var fileName = filePath != null ? Path.GetFileName(filePath) : "unknown";

            if (environmentUrl != null)
            {
                // Invalidate specific environment
                if (_providers.TryRemove(environmentUrl, out var provider))
                {
                    TuiDebugLog.Log($"Invalidating provider for {environmentUrl} (from {caller} at {fileName}:{lineNumber})");
                    await provider.DisposeAsync().ConfigureAwait(false);
                    TuiDebugLog.Log($"Provider for {environmentUrl} invalidated");
                }
            }
            else
            {
                // Invalidate all
                TuiDebugLog.Log($"Invalidating all {_providers.Count} providers (from {caller} at {fileName}:{lineNumber})");
                foreach (var kvp in _providers)
                {
                    try
                    {
                        await kvp.Value.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        TuiDebugLog.Log($"Error disposing provider for {kvp.Key}: {ex.Message}");
                    }
                }
                _providers.Clear();
                TuiDebugLog.Log("All providers invalidated");
            }
        }
        finally
        {
            _lock.Release();
        }
    }
```

Update `SetEnvironmentAsync` (lines 152-192) — remove invalidation:
```csharp
    public async Task SetEnvironmentAsync(
        string environmentUrl,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentUrl);

        TuiDebugLog.Log($"Switching active environment to {displayName ?? environmentUrl}");

        var profileService = GetProfileService();
        var profileName = string.IsNullOrEmpty(_profileName) ? null : _profileName;
        await profileService.SetEnvironmentAsync(profileName, environmentUrl, displayName, cancellationToken)
            .ConfigureAwait(false);

        // Update active environment (for status bar display)
        // Do NOT invalidate — other tabs may still be using old environment's provider
        _activeEnvironmentUrl = environmentUrl;
        _activeEnvironmentDisplayName = displayName;

        // Pre-warm the new environment's provider
        _errorService?.FireAndForget(
            GetServiceProviderAsync(environmentUrl, cancellationToken),
            "WarmNewEnvironment");

        EnvironmentChanged?.Invoke(environmentUrl, displayName);
    }
```

Update `SetActiveProfileAsync` (lines 386-451) — invalidate ALL (credentials changed):
```csharp
        // Profile change invalidates ALL cached providers since credentials differ
        await InvalidateAsync().ConfigureAwait(false);
```

The rest of `SetActiveProfileAsync` stays the same except replace `_currentEnvironmentUrl` → `_activeEnvironmentUrl` and `_currentEnvironmentDisplayName` → `_activeEnvironmentDisplayName`.

Update `DisposeAsync` (lines 527-578):
```csharp
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        TuiDebugLog.Log("Disposing InteractiveSession...");
        _disposed = true;

        if (_providers.Count > 0)
        {
            TuiDebugLog.Log($"Disposing {_providers.Count} ServiceProviders...");
            foreach (var kvp in _providers)
            {
                try
                {
                    using var cts = new CancellationTokenSource(DisposeTimeout);
                    var disposeTask = kvp.Value.DisposeAsync().AsTask();
                    var completed = await Task.WhenAny(disposeTask, Task.Delay(Timeout.Infinite, cts.Token))
                        .ConfigureAwait(false);

                    if (completed == disposeTask)
                    {
                        await disposeTask.ConfigureAwait(false);
                        TuiDebugLog.Log($"Provider for {kvp.Key} disposed");
                    }
                    else
                    {
                        TuiDebugLog.Log($"Provider for {kvp.Key} disposal timed out - abandoning");
                    }
                }
                catch (OperationCanceledException)
                {
                    TuiDebugLog.Log($"Provider for {kvp.Key} disposal timed out - abandoning");
                }
                catch (Exception ex)
                {
                    TuiDebugLog.Log($"Provider for {kvp.Key} disposal error: {ex.Message}");
                }
            }
            _providers.Clear();
        }

        _lock.Dispose();
        TuiDebugLog.Log("InteractiveSession disposed");
    }
```

Update `CurrentEnvironmentUrl` and `CurrentEnvironmentDisplayName` properties to use `_activeEnvironmentUrl` / `_activeEnvironmentDisplayName`.

**Step 4: Update existing lifecycle tests**

In `tests/PPDS.Cli.Tests/Tui/InteractiveSessionLifecycleTests.cs`, some tests assume `SetEnvironmentAsync` invalidates the old provider. Update:

- `SetEnvironmentAsync_InvalidatesOldProvider` (lines 286-310): This test should now verify the old provider is NOT invalidated. The provider for the old URL should still be cached. Update assertion logic accordingly.
- `GetServiceProviderAsync_RecreatesForDifferentUrl` (lines 155-169): With multi-provider caching, a different URL creates a NEW additional provider, it doesn't replace the old one. Update assertions.

**Step 5: Run all tests**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet`

Expected: All tests pass (existing updated + new multi-env tests).

**Step 6: Commit**

```bash
git add src/PPDS.Cli/Tui/InteractiveSession.cs tests/PPDS.Cli.Tests/Tui/MultiEnvironmentSessionTests.cs tests/PPDS.Cli.Tests/Tui/InteractiveSessionLifecycleTests.cs
git commit -m "feat(tui): multi-environment ServiceProvider caching

Replace single ServiceProvider with ConcurrentDictionary keyed by
environment URL. Multiple screens can now hold live connections to
different environments simultaneously. Profile switch invalidates all
providers (credentials changed). Environment switch no longer invalidates.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 5: TuiScreenBase — Abstract Screen Base Class

**Files:**
- Create: `src/PPDS.Cli/Tui/Screens/TuiScreenBase.cs`
- Test: `tests/PPDS.Cli.Tests/Tui/Screens/TuiScreenBaseTests.cs`

**Step 1: Write the tests**

Create `tests/PPDS.Cli.Tests/Tui/Screens/TuiScreenBaseTests.cs`:

```csharp
using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

[Trait("Category", "TuiUnit")]
public sealed class TuiScreenBaseTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly InteractiveSession _session;

    public TuiScreenBaseTests()
    {
        _tempStore = new TempProfileStore();
        _session = new InteractiveSession(null, _tempStore.Store, new MockServiceProviderFactory());
    }

    public void Dispose()
    {
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tempStore.Dispose();
    }

    [Fact]
    public void Constructor_SetsSessionAndErrorService()
    {
        var screen = new StubScreen(_session);
        Assert.NotNull(screen.Content);
        Assert.Equal("Stub", screen.Title);
    }

    [Fact]
    public void Constructor_BindsEnvironmentUrl()
    {
        var screen = new StubScreen(_session, "https://dev.crm.dynamics.com");
        Assert.Equal("https://dev.crm.dynamics.com", screen.EnvironmentUrl);
    }

    [Fact]
    public void Dispose_CancelsCancellationToken()
    {
        var screen = new StubScreen(_session);
        var token = screen.ExposedCancellationToken;
        Assert.False(token.IsCancellationRequested);

        screen.Dispose();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void OnActivated_RegistersHotkeys()
    {
        var screen = new StubScreen(_session);
        var registry = new HotkeyRegistry();

        screen.OnActivated(registry);

        var bindings = registry.GetAllBindings();
        Assert.Contains(bindings, b => b.Description == "Test hotkey");
    }

    [Fact]
    public void OnDeactivating_ClearsHotkeys()
    {
        var screen = new StubScreen(_session);
        var registry = new HotkeyRegistry();

        screen.OnActivated(registry);
        Assert.NotEmpty(registry.GetAllBindings());

        screen.OnDeactivating();
        Assert.Empty(registry.GetAllBindings());
    }

    [Fact]
    public void Dispose_CallsOnDeactivating()
    {
        var screen = new StubScreen(_session);
        var registry = new HotkeyRegistry();
        screen.OnActivated(registry);

        screen.Dispose();

        Assert.Empty(registry.GetAllBindings());
    }

    [Fact]
    public void Dispose_CallsOnDispose()
    {
        var screen = new StubScreen(_session);
        screen.Dispose();
        Assert.True(screen.OnDisposeWasCalled);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var screen = new StubScreen(_session);
        screen.Dispose();
        var exception = Record.Exception(() => screen.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void RequestClose_RaisesCloseRequestedEvent()
    {
        var screen = new StubScreen(_session);
        var raised = false;
        screen.CloseRequested += () => raised = true;

        screen.InvokeRequestClose();

        Assert.True(raised);
    }

    [Fact]
    public void NotifyMenuChanged_RaisesMenuStateChangedEvent()
    {
        var screen = new StubScreen(_session);
        var raised = false;
        screen.MenuStateChanged += () => raised = true;

        screen.InvokeNotifyMenuChanged();

        Assert.True(raised);
    }

    /// <summary>
    /// Concrete test implementation of TuiScreenBase.
    /// </summary>
    private sealed class StubScreen : TuiScreenBase
    {
        public override string Title => "Stub";
        public bool OnDisposeWasCalled { get; private set; }
        public CancellationToken ExposedCancellationToken => ScreenCancellation;

        public StubScreen(InteractiveSession session, string? environmentUrl = null)
            : base(session, environmentUrl)
        {
        }

        protected override void RegisterHotkeys(IHotkeyRegistry registry)
        {
            RegisterHotkey(registry, Key.F5, "Test hotkey", () => { });
        }

        protected override void OnDispose()
        {
            OnDisposeWasCalled = true;
        }

        public void InvokeRequestClose() => RequestClose();
        public void InvokeNotifyMenuChanged() => NotifyMenuChanged();
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "FullyQualifiedName~TuiScreenBaseTests" --nologo --verbosity quiet`

Expected: Build failure — `TuiScreenBase` does not exist.

**Step 3: Write the implementation**

Create `src/PPDS.Cli/Tui/Screens/TuiScreenBase.cs`:

```csharp
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// Abstract base class for TUI screens, providing common lifecycle management.
/// All new screens should inherit from this class.
/// </summary>
/// <remarks>
/// Provides:
/// <list type="bullet">
/// <item>Content View setup with Dim.Fill()</item>
/// <item>Session and ErrorService references</item>
/// <item>Hotkey registration with automatic cleanup</item>
/// <item>Per-screen CancellationToken (fires on Dispose)</item>
/// <item>Environment URL binding</item>
/// <item>Standard Dispose pattern</item>
/// </list>
/// </remarks>
internal abstract class TuiScreenBase : ITuiScreen
{
    protected readonly InteractiveSession Session;
    protected readonly ITuiErrorService ErrorService;
    private readonly List<IDisposable> _hotkeyRegistrations = new();
    private readonly CancellationTokenSource _screenCts = new();
    private bool _disposed;

    /// <inheritdoc />
    public View Content { get; }

    /// <inheritdoc />
    public abstract string Title { get; }

    /// <inheritdoc />
    public virtual MenuBarItem[]? ScreenMenuItems => null;

    /// <inheritdoc />
    public virtual Action? ExportAction => null;

    /// <inheritdoc />
    public event Action? CloseRequested;

    /// <inheritdoc />
    public event Action? MenuStateChanged;

    /// <summary>
    /// The environment URL this screen is bound to.
    /// Screens can operate independently on different environments.
    /// </summary>
    public string? EnvironmentUrl { get; protected set; }

    /// <summary>
    /// Cancellation token that fires when the screen is closed or disposed.
    /// Use this instead of CancellationToken.None for all async operations.
    /// </summary>
    protected CancellationToken ScreenCancellation => _screenCts.Token;

    protected TuiScreenBase(InteractiveSession session, string? environmentUrl = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ErrorService = session.GetErrorService();
        EnvironmentUrl = environmentUrl ?? session.CurrentEnvironmentUrl;

        Content = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = TuiColorPalette.Default
        };
    }

    /// <inheritdoc />
    public void OnActivated(IHotkeyRegistry hotkeyRegistry)
    {
        RegisterHotkeys(hotkeyRegistry);
    }

    /// <inheritdoc />
    public void OnDeactivating()
    {
        foreach (var reg in _hotkeyRegistrations)
        {
            reg.Dispose();
        }
        _hotkeyRegistrations.Clear();
    }

    /// <summary>
    /// Override to register screen-scope hotkeys. Called during OnActivated.
    /// Use <see cref="RegisterHotkey"/> to auto-track registrations for cleanup.
    /// </summary>
    protected abstract void RegisterHotkeys(IHotkeyRegistry registry);

    /// <summary>
    /// Registers a screen-scope hotkey and tracks the registration for automatic cleanup.
    /// </summary>
    protected void RegisterHotkey(IHotkeyRegistry registry, Key key, string description, Action handler)
    {
        _hotkeyRegistrations.Add(
            registry.Register(key, HotkeyScope.Screen, description, handler, owner: this));
    }

    /// <summary>
    /// Raises the <see cref="CloseRequested"/> event.
    /// </summary>
    protected void RequestClose() => CloseRequested?.Invoke();

    /// <summary>
    /// Raises the <see cref="MenuStateChanged"/> event.
    /// </summary>
    protected void NotifyMenuChanged() => MenuStateChanged?.Invoke();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _screenCts.Cancel();
        _screenCts.Dispose();
        OnDeactivating();
        OnDispose();
        Content.Dispose();
    }

    /// <summary>
    /// Override for screen-specific cleanup. Called during Dispose after cancellation and hotkey cleanup.
    /// </summary>
    protected virtual void OnDispose() { }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "FullyQualifiedName~TuiScreenBaseTests" --nologo --verbosity quiet`

Expected: All 10 tests pass.

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet`

Expected: All pass.

**Step 5: Commit**

```bash
git add src/PPDS.Cli/Tui/Screens/TuiScreenBase.cs tests/PPDS.Cli.Tests/Tui/Screens/TuiScreenBaseTests.cs
git commit -m "feat(tui): add TuiScreenBase abstract class

Provides common lifecycle boilerplate for all screens: content View
setup, session/error service refs, hotkey registration with auto-cleanup,
per-screen CancellationToken, environment URL binding, dispose pattern.
SqlQueryScreen migration to use this base class deferred to a follow-up.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 6: Splash Screen

**Files:**
- Create: `src/PPDS.Cli/Tui/Views/SplashView.cs`
- Create: `src/PPDS.Cli/Tui/Testing/States/SplashViewState.cs`
- Test: `tests/PPDS.Cli.Tests/Tui/Views/SplashViewTests.cs`
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs`

**Step 1: Write the state record**

Create `src/PPDS.Cli/Tui/Testing/States/SplashViewState.cs`:

```csharp
namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// State capture for splash view testing.
/// </summary>
public sealed record SplashViewState(
    string StatusMessage,
    bool IsReady,
    string? Version,
    bool SpinnerActive);
```

**Step 2: Write the tests**

Create `tests/PPDS.Cli.Tests/Tui/Views/SplashViewTests.cs`:

```csharp
using PPDS.Cli.Tui.Views;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Views;

[Trait("Category", "TuiUnit")]
public sealed class SplashViewTests
{
    [Fact]
    public void InitialState_ShowsConnecting()
    {
        var splash = new SplashView();
        var state = splash.CaptureState();

        Assert.False(state.IsReady);
        Assert.NotNull(state.Version);
        Assert.NotEmpty(state.StatusMessage);
    }

    [Fact]
    public void SetStatus_UpdatesMessage()
    {
        var splash = new SplashView();

        splash.SetStatus("Loading profile...");
        var state = splash.CaptureState();

        Assert.Equal("Loading profile...", state.StatusMessage);
        Assert.False(state.IsReady);
    }

    [Fact]
    public void SetReady_MarksReady()
    {
        var splash = new SplashView();

        splash.SetReady();
        var state = splash.CaptureState();

        Assert.True(state.IsReady);
    }

    [Fact]
    public void Version_MatchesAssemblyVersion()
    {
        var splash = new SplashView();
        var state = splash.CaptureState();

        Assert.NotNull(state.Version);
        // Version should contain at least a major.minor pattern
        Assert.Matches(@"\d+\.\d+", state.Version);
    }
}
```

**Step 3: Write the implementation**

Create `src/PPDS.Cli/Tui/Views/SplashView.cs`:

```csharp
using PPDS.Cli.Commands;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Branded splash screen shown during TUI startup.
/// Displays PPDS logo, version, and initialization status.
/// </summary>
internal sealed class SplashView : View, ITuiStateCapture<SplashViewState>
{
    // ASCII art logo — compact version for terminal
    private const string Logo = @"
 ██████  ██████  ██████  ███████
 ██   ██ ██   ██ ██   ██ ██
 ██████  ██████  ██   ██ ███████
 ██      ██      ██   ██      ██
 ██      ██      ██████  ███████";

    private const string Tagline = "Power Platform Developer Suite";

    private readonly string _version;
    private readonly Label _statusLabel;
    private readonly TuiSpinner _spinner;
    private string _statusMessage = "Initializing...";
    private bool _isReady;

    public SplashView()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        ColorScheme = TuiColorPalette.Default;

        _version = ErrorOutput.Version;

        // Logo (centered)
        var logoLabel = new Label(Logo)
        {
            X = Pos.Center(),
            Y = Pos.Center() - 5,
            TextAlignment = TextAlignment.Centered
        };

        // Tagline
        var taglineLabel = new Label(Tagline)
        {
            X = Pos.Center(),
            Y = Pos.Center() + 2,
            TextAlignment = TextAlignment.Centered
        };

        // Status spinner + message
        _spinner = new TuiSpinner
        {
            X = Pos.Center() - 15,
            Y = Pos.Center() + 4,
            Width = 30,
            Height = 1
        };

        _statusLabel = new Label(_statusMessage)
        {
            X = Pos.Center(),
            Y = Pos.Center() + 4,
            TextAlignment = TextAlignment.Centered,
            Visible = false // Hidden while spinner is active
        };

        // Version
        var versionLabel = new Label($"v{_version}")
        {
            X = Pos.Center(),
            Y = Pos.Center() + 6,
            TextAlignment = TextAlignment.Centered
        };

        Add(logoLabel, taglineLabel, _spinner, _statusLabel, versionLabel);

        // Start spinner
        _spinner.Start(_statusMessage);
    }

    /// <summary>
    /// Updates the status message shown during initialization.
    /// </summary>
    public void SetStatus(string message)
    {
        _statusMessage = message;
        if (_spinner.Visible)
        {
            _spinner.StopWithMessage(message);
            _spinner.Start(message);
        }
        else
        {
            _statusLabel.Text = message;
        }
    }

    /// <summary>
    /// Marks initialization as complete. Stops the spinner.
    /// </summary>
    public void SetReady()
    {
        _isReady = true;
        _statusMessage = "Ready";
        _spinner.Stop();
        _spinner.Visible = false;
        _statusLabel.Text = "Ready — press Enter or select from the menu";
        _statusLabel.Visible = true;
    }

    /// <inheritdoc />
    public SplashViewState CaptureState() => new(
        StatusMessage: _statusMessage,
        IsReady: _isReady,
        Version: _version,
        SpinnerActive: _spinner.Visible && !_isReady);
}
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "FullyQualifiedName~SplashViewTests" --nologo --verbosity quiet`

Expected: All 4 pass.

**Step 5: Integrate splash into TuiShell**

In `src/PPDS.Cli/Tui/TuiShell.cs`, modify the constructor (around line 83) and `ShowMainMenu`:

Replace the `ShowMainMenu()` call in the constructor (line 83) with:
```csharp
        // Show splash screen during initialization
        ShowSplash();
```

Add the SplashView field after line 37:
```csharp
    private SplashView? _splashView;
```

Add `ShowSplash()` method:
```csharp
    private void ShowSplash()
    {
        _currentScreen = null;
        _hotkeyRegistry.SetActiveScreen(null);
        _contentArea.Title = "PPDS";

        _splashView = new SplashView();
        _contentArea.Add(_splashView);

        RebuildMenuBar();
    }
```

Modify `NavigateToSqlQuery()` (lines 365-402) to clear splash if showing:
```csharp
        // Clear splash or main menu content
        if (_splashView != null)
        {
            _contentArea.Remove(_splashView);
            _splashView = null;
        }
        if (_mainMenuContent != null)
        {
            _contentArea.Remove(_mainMenuContent);
            _mainMenuContent = null;
        }
```

Similarly update `ShowMainMenu()` to clear splash:
```csharp
    private void ShowMainMenu()
    {
        // Clear splash if still showing
        if (_splashView != null)
        {
            _contentArea.Remove(_splashView);
            _splashView = null;
        }

        _currentScreen = null;
        _hotkeyRegistry.SetActiveScreen(null);
        _contentArea.Title = "Main Menu";

        _mainMenuContent = CreateMainMenuContent();
        _contentArea.Add(_mainMenuContent);

        RebuildMenuBar();
    }
```

**Step 6: Run full test suite**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet`

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet`

Expected: All pass.

**Step 7: Commit**

```bash
git add src/PPDS.Cli/Tui/Views/SplashView.cs src/PPDS.Cli/Tui/Testing/States/SplashViewState.cs tests/PPDS.Cli.Tests/Tui/Views/SplashViewTests.cs src/PPDS.Cli/Tui/TuiShell.cs
git commit -m "feat(tui): add branded splash screen on startup

Shows PPDS ASCII logo, version, and initialization spinner during
session startup. Transitions to main menu when user navigates or
initialization completes.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 7: TabManager

**Files:**
- Create: `src/PPDS.Cli/Tui/Infrastructure/TabManager.cs`
- Create: `src/PPDS.Cli/Tui/Testing/States/TabManagerState.cs`
- Test: `tests/PPDS.Cli.Tests/Tui/Infrastructure/TabManagerTests.cs`

**Step 1: Write the state record**

Create `src/PPDS.Cli/Tui/Testing/States/TabManagerState.cs`:

```csharp
namespace PPDS.Cli.Tui.Testing.States;

public sealed record TabManagerState(
    int TabCount,
    int ActiveIndex,
    IReadOnlyList<TabSummary> Tabs);

public sealed record TabSummary(
    string ScreenType,
    string Title,
    string? EnvironmentUrl,
    EnvironmentType EnvironmentType,
    bool IsActive);
```

Add `using PPDS.Cli.Tui.Infrastructure;` to the state file (for `EnvironmentType` enum).

**Step 2: Write the tests**

Create `tests/PPDS.Cli.Tests/Tui/Infrastructure/TabManagerTests.cs`:

```csharp
using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

[Trait("Category", "TuiUnit")]
public sealed class TabManagerTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly InteractiveSession _session;
    private readonly TabManager _manager;

    public TabManagerTests()
    {
        _tempStore = new TempProfileStore();
        _session = new InteractiveSession(null, _tempStore.Store, new MockServiceProviderFactory());
        _manager = new TabManager(new TuiThemeService());
    }

    public void Dispose()
    {
        _manager.Dispose();
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tempStore.Dispose();
    }

    [Fact]
    public void Initial_NoTabs()
    {
        Assert.Equal(0, _manager.TabCount);
        Assert.Null(_manager.ActiveTab);
        Assert.Equal(-1, _manager.ActiveIndex);
    }

    [Fact]
    public void AddTab_SetsAsActive()
    {
        var screen = new StubScreen(_session);
        _manager.AddTab(screen, "https://dev.crm.dynamics.com", "DEV");

        Assert.Equal(1, _manager.TabCount);
        Assert.Equal(0, _manager.ActiveIndex);
        Assert.Same(screen, _manager.ActiveTab?.Screen);
    }

    [Fact]
    public void AddTab_MultipleTabs_LastIsActive()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");

        Assert.Equal(2, _manager.TabCount);
        Assert.Equal(1, _manager.ActiveIndex);
    }

    [Fact]
    public void ActivateTab_SwitchesActive()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");

        _manager.ActivateTab(0);

        Assert.Equal(0, _manager.ActiveIndex);
    }

    [Fact]
    public void CloseTab_RemovesAndActivatesAdjacent()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");
        _manager.AddTab(new StubScreen(_session), "https://qa.crm4.dynamics.com", "QA");

        _manager.ActivateTab(1); // PROD active
        _manager.CloseTab(1);    // Close PROD

        Assert.Equal(2, _manager.TabCount);
        // Should activate the tab that took index 1's place (QA)
        Assert.Equal(1, _manager.ActiveIndex);
    }

    [Fact]
    public void CloseTab_LastTab_ActivatesPrevious()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");

        _manager.ActivateTab(1); // PROD active
        _manager.CloseTab(1);    // Close last tab

        Assert.Equal(1, _manager.TabCount);
        Assert.Equal(0, _manager.ActiveIndex); // Falls back to DEV
    }

    [Fact]
    public void CloseTab_OnlyTab_NoActive()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.CloseTab(0);

        Assert.Equal(0, _manager.TabCount);
        Assert.Equal(-1, _manager.ActiveIndex);
        Assert.Null(_manager.ActiveTab);
    }

    [Fact]
    public void CloseTab_DisposesScreen()
    {
        var screen = new StubScreen(_session);
        _manager.AddTab(screen, "https://dev.crm.dynamics.com", "DEV");

        _manager.CloseTab(0);

        Assert.True(screen.IsDisposed);
    }

    [Fact]
    public void ActivateNext_Cycles()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");
        _manager.AddTab(new StubScreen(_session), "https://qa.crm4.dynamics.com", "QA");
        _manager.ActivateTab(0);

        _manager.ActivateNext();
        Assert.Equal(1, _manager.ActiveIndex);

        _manager.ActivateNext();
        Assert.Equal(2, _manager.ActiveIndex);

        _manager.ActivateNext(); // Wraps
        Assert.Equal(0, _manager.ActiveIndex);
    }

    [Fact]
    public void ActivatePrevious_Cycles()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");
        _manager.ActivateTab(0);

        _manager.ActivatePrevious(); // Wraps
        Assert.Equal(1, _manager.ActiveIndex);
    }

    [Fact]
    public void TabsChanged_FiresOnAddAndClose()
    {
        var count = 0;
        _manager.TabsChanged += () => count++;

        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.CloseTab(0);

        Assert.Equal(2, count);
    }

    [Fact]
    public void ActiveTabChanged_FiresOnSwitch()
    {
        var count = 0;
        _manager.ActiveTabChanged += () => count++;

        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");
        _manager.ActivateTab(0);

        Assert.True(count >= 3); // Add activates + explicit switch
    }

    [Fact]
    public void CaptureState_ReflectsTabs()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");

        var state = _manager.CaptureState();

        Assert.Equal(2, state.TabCount);
        Assert.Equal(1, state.ActiveIndex);
        Assert.Equal(2, state.Tabs.Count);
        Assert.Equal("https://dev.crm.dynamics.com", state.Tabs[0].EnvironmentUrl);
        Assert.Equal("https://prod.crm.dynamics.com", state.Tabs[1].EnvironmentUrl);
        Assert.False(state.Tabs[0].IsActive);
        Assert.True(state.Tabs[1].IsActive);
    }

    private sealed class StubScreen : TuiScreenBase
    {
        public override string Title => "Stub";
        public bool IsDisposed { get; private set; }

        public StubScreen(InteractiveSession session)
            : base(session) { }

        protected override void RegisterHotkeys(IHotkeyRegistry registry) { }

        protected override void OnDispose()
        {
            IsDisposed = true;
        }
    }
}
```

**Step 3: Run tests to verify they fail**

Expected: Build failure — `TabManager` does not exist.

**Step 4: Write the implementation**

Create `src/PPDS.Cli/Tui/Infrastructure/TabManager.cs`:

```csharp
using PPDS.Cli.Tui.Screens;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Manages tab lifecycle: add, close, switch, and cycle through tabs.
/// Each tab owns a screen instance bound to a specific environment.
/// </summary>
internal sealed class TabManager : ITuiStateCapture<TabManagerState>, IDisposable
{
    private readonly List<TabInfo> _tabs = new();
    private readonly ITuiThemeService _themeService;
    private int _activeIndex = -1;

    /// <summary>Raised when a tab is added or removed.</summary>
    public event Action? TabsChanged;

    /// <summary>Raised when the active tab changes.</summary>
    public event Action? ActiveTabChanged;

    public int TabCount => _tabs.Count;
    public int ActiveIndex => _activeIndex;
    public TabInfo? ActiveTab => _activeIndex >= 0 && _activeIndex < _tabs.Count
        ? _tabs[_activeIndex]
        : null;

    public IReadOnlyList<TabInfo> Tabs => _tabs.AsReadOnly();

    public TabManager(ITuiThemeService themeService)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
    }

    /// <summary>
    /// Adds a new tab and makes it active.
    /// </summary>
    public void AddTab(ITuiScreen screen, string? environmentUrl, string? environmentDisplayName = null)
    {
        var envType = _themeService.DetectEnvironmentType(environmentUrl);
        var tab = new TabInfo(screen, environmentUrl, environmentDisplayName, envType);
        _tabs.Add(tab);
        _activeIndex = _tabs.Count - 1;

        TabsChanged?.Invoke();
        ActiveTabChanged?.Invoke();
    }

    /// <summary>
    /// Closes the tab at the given index and disposes its screen.
    /// </summary>
    public void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        var tab = _tabs[index];
        _tabs.RemoveAt(index);
        tab.Screen.Dispose();

        if (_tabs.Count == 0)
        {
            _activeIndex = -1;
        }
        else if (_activeIndex >= _tabs.Count)
        {
            _activeIndex = _tabs.Count - 1;
        }
        else if (_activeIndex > index)
        {
            _activeIndex--;
        }
        // If activeIndex == index and tabs remain, it now points to the next tab (which slid down)
        // or the previous if we were at the end. Clamp just in case.
        _activeIndex = Math.Min(_activeIndex, _tabs.Count - 1);

        TabsChanged?.Invoke();
        ActiveTabChanged?.Invoke();
    }

    /// <summary>
    /// Activates the tab at the given index.
    /// </summary>
    public void ActivateTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        if (index == _activeIndex) return;

        _activeIndex = index;
        ActiveTabChanged?.Invoke();
    }

    /// <summary>Cycles to the next tab (wraps around).</summary>
    public void ActivateNext()
    {
        if (_tabs.Count <= 1) return;
        _activeIndex = (_activeIndex + 1) % _tabs.Count;
        ActiveTabChanged?.Invoke();
    }

    /// <summary>Cycles to the previous tab (wraps around).</summary>
    public void ActivatePrevious()
    {
        if (_tabs.Count <= 1) return;
        _activeIndex = (_activeIndex - 1 + _tabs.Count) % _tabs.Count;
        ActiveTabChanged?.Invoke();
    }

    /// <inheritdoc />
    public TabManagerState CaptureState()
    {
        var tabs = _tabs.Select((t, i) => new TabSummary(
            ScreenType: t.Screen.GetType().Name,
            Title: t.Screen.Title,
            EnvironmentUrl: t.EnvironmentUrl,
            EnvironmentType: t.EnvironmentType,
            IsActive: i == _activeIndex
        )).ToList();

        return new TabManagerState(
            TabCount: _tabs.Count,
            ActiveIndex: _activeIndex,
            Tabs: tabs);
    }

    public void Dispose()
    {
        foreach (var tab in _tabs)
        {
            try { tab.Screen.Dispose(); }
            catch { /* continue */ }
        }
        _tabs.Clear();
        _activeIndex = -1;
    }
}

/// <summary>
/// Information about a single tab.
/// </summary>
internal sealed record TabInfo(
    ITuiScreen Screen,
    string? EnvironmentUrl,
    string? EnvironmentDisplayName,
    EnvironmentType EnvironmentType);
```

**Step 5: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "FullyQualifiedName~TabManagerTests" --nologo --verbosity quiet`

Expected: All 13 pass.

Run full suite: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet`

Expected: All pass.

**Step 6: Commit**

```bash
git add src/PPDS.Cli/Tui/Infrastructure/TabManager.cs src/PPDS.Cli/Tui/Testing/States/TabManagerState.cs tests/PPDS.Cli.Tests/Tui/Infrastructure/TabManagerTests.cs
git commit -m "feat(tui): add TabManager for multi-tab navigation

Manages tab lifecycle with add, close, switch, and cycle operations.
Each tab owns a screen instance bound to a specific environment URL.
Includes environment type detection for color-coded tab labels.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 8: TabBar View Component

**Files:**
- Create: `src/PPDS.Cli/Tui/Views/TabBar.cs`
- Create: `src/PPDS.Cli/Tui/Testing/States/TabBarState.cs`
- Test: `tests/PPDS.Cli.Tests/Tui/Views/TabBarTests.cs`

**Step 1: Write the state record**

Create `src/PPDS.Cli/Tui/Testing/States/TabBarState.cs`:

```csharp
namespace PPDS.Cli.Tui.Testing.States;

public sealed record TabBarState(
    int TabCount,
    int ActiveIndex,
    IReadOnlyList<string> TabLabels,
    bool IsVisible);
```

**Step 2: Write the tests**

Create `tests/PPDS.Cli.Tests/Tui/Views/TabBarTests.cs`:

```csharp
using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using PPDS.Cli.Tui.Views;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Views;

[Trait("Category", "TuiUnit")]
public sealed class TabBarTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly InteractiveSession _session;
    private readonly TabManager _tabManager;
    private readonly TabBar _tabBar;

    public TabBarTests()
    {
        _tempStore = new TempProfileStore();
        _session = new InteractiveSession(null, _tempStore.Store, new MockServiceProviderFactory());
        _tabManager = new TabManager(new TuiThemeService());
        _tabBar = new TabBar(_tabManager);
    }

    public void Dispose()
    {
        _tabBar.Dispose();
        _tabManager.Dispose();
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tempStore.Dispose();
    }

    [Fact]
    public void NoTabs_IsNotVisible()
    {
        var state = _tabBar.CaptureState();
        Assert.Equal(0, state.TabCount);
        Assert.False(state.IsVisible);
    }

    [Fact]
    public void WithTabs_IsVisible()
    {
        _tabManager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        var state = _tabBar.CaptureState();

        Assert.Equal(1, state.TabCount);
        Assert.True(state.IsVisible);
    }

    [Fact]
    public void TabLabels_ReflectScreenTitles()
    {
        _tabManager.AddTab(new StubScreen(_session, "SQL DEV"), "https://dev.crm.dynamics.com", "DEV");
        _tabManager.AddTab(new StubScreen(_session, "SQL PROD"), "https://prod.crm.dynamics.com", "PROD");

        var state = _tabBar.CaptureState();

        Assert.Equal(2, state.TabLabels.Count);
        Assert.Contains("SQL DEV", state.TabLabels[0]);
        Assert.Contains("SQL PROD", state.TabLabels[1]);
    }

    [Fact]
    public void ActiveIndex_MatchesTabManager()
    {
        _tabManager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _tabManager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");
        _tabManager.ActivateTab(0);

        var state = _tabBar.CaptureState();
        Assert.Equal(0, state.ActiveIndex);
    }

    private sealed class StubScreen : TuiScreenBase
    {
        private readonly string _title;
        public override string Title => _title;

        public StubScreen(InteractiveSession session, string title = "Stub")
            : base(session) { _title = title; }

        protected override void RegisterHotkeys(IHotkeyRegistry registry) { }
    }
}
```

**Step 3: Write the implementation**

Create `src/PPDS.Cli/Tui/Views/TabBar.cs`:

```csharp
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Horizontal tab bar View for switching between open tabs.
/// Renders environment-colored tab labels with active tab highlight.
/// </summary>
internal sealed class TabBar : View, ITuiStateCapture<TabBarState>
{
    private readonly TabManager _tabManager;
    private readonly List<Label> _tabLabels = new();

    public TabBar(TabManager tabManager)
    {
        _tabManager = tabManager ?? throw new ArgumentNullException(nameof(tabManager));

        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = 1;
        Visible = false;
        ColorScheme = TuiColorPalette.MenuBar;

        _tabManager.TabsChanged += Rebuild;
        _tabManager.ActiveTabChanged += UpdateHighlight;
    }

    private void Rebuild()
    {
        // Clear existing labels
        foreach (var label in _tabLabels)
        {
            Remove(label);
        }
        _tabLabels.Clear();

        Visible = _tabManager.TabCount > 0;

        if (_tabManager.TabCount == 0) return;

        var xPos = 0;
        for (int i = 0; i < _tabManager.Tabs.Count; i++)
        {
            var tab = _tabManager.Tabs[i];
            var index = i; // Capture for closure
            var text = $" {i + 1}: {tab.Screen.Title} ";

            var label = new Label(text)
            {
                X = xPos,
                Y = 0,
                Width = text.Length,
                Height = 1,
                ColorScheme = i == _tabManager.ActiveIndex
                    ? TuiColorPalette.TabActive
                    : TuiColorPalette.TabInactive
            };

            label.MouseClick += (_) =>
            {
                _tabManager.ActivateTab(index);
            };

            _tabLabels.Add(label);
            Add(label);
            xPos += text.Length;
        }

        // Add [+] button
        var addLabel = new Label(" [+] ")
        {
            X = xPos,
            Y = 0,
            Width = 5,
            Height = 1,
            ColorScheme = TuiColorPalette.MenuBar
        };
        _tabLabels.Add(addLabel);
        Add(addLabel);

        SetNeedsDisplay();
    }

    private void UpdateHighlight()
    {
        for (int i = 0; i < _tabManager.Tabs.Count && i < _tabLabels.Count; i++)
        {
            _tabLabels[i].ColorScheme = i == _tabManager.ActiveIndex
                ? TuiColorPalette.TabActive
                : TuiColorPalette.TabInactive;
        }
        SetNeedsDisplay();
    }

    /// <inheritdoc />
    public TabBarState CaptureState()
    {
        var labels = _tabManager.Tabs
            .Select(t => t.Screen.Title)
            .ToList();

        return new TabBarState(
            TabCount: _tabManager.TabCount,
            ActiveIndex: _tabManager.ActiveIndex,
            TabLabels: labels,
            IsVisible: Visible);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tabManager.TabsChanged -= Rebuild;
            _tabManager.ActiveTabChanged -= UpdateHighlight;
        }
        base.Dispose(disposing);
    }
}
```

**Important:** This references `TuiColorPalette.TabActive` and `TuiColorPalette.TabInactive` which don't exist yet. Add them to `src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs`:

```csharp
    /// <summary>Active tab in tab bar.</summary>
    public static ColorScheme TabActive => CreateScheme(Color.White, Color.DarkGray);

    /// <summary>Inactive tab in tab bar.</summary>
    public static ColorScheme TabInactive => CreateScheme(Color.Gray, Color.Black);
```

Use the existing `CreateScheme` helper method pattern in TuiColorPalette. Read the file first to match the exact pattern.

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "FullyQualifiedName~TabBarTests" --nologo --verbosity quiet`

Expected: All 4 pass.

Run full suite to check no regressions.

**Step 5: Commit**

```bash
git add src/PPDS.Cli/Tui/Views/TabBar.cs src/PPDS.Cli/Tui/Testing/States/TabBarState.cs tests/PPDS.Cli.Tests/Tui/Views/TabBarTests.cs src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs
git commit -m "feat(tui): add TabBar view component

Custom horizontal tab bar for Terminal.Gui 1.19 (no built-in TabView).
Renders numbered, clickable tab labels. Active tab highlighted.
Environment-colored via TuiColorPalette.TabActive/TabInactive schemes.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 9: Integrate Tabs into TuiShell

**Files:**
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs`

This is the integration step. It wires TabManager + TabBar into TuiShell, replacing the screen stack.

**Step 1: Modify TuiShell**

Key changes:
1. Add `TabManager` and `TabBar` fields
2. Remove `_screenStack`
3. Remove `_environmentName`, `_environmentUrl` fields (read from active tab)
4. Fix `_hasError` to not block Alt+E
5. Add tab hotkeys (Ctrl+T, Ctrl+W, Ctrl+Tab, Ctrl+1-9)
6. `NavigateTo` adds a tab instead of pushing to stack
7. `NavigateBack` closes the active tab
8. Layout: TabBar sits between menu bar and content area

**This is the largest change.** The implementor should:
- Read the full `TuiShell.cs` before making changes
- Keep all dialog-showing methods (`ShowProfileSelector`, `ShowEnvironmentSelector`, etc.)
- Replace `_screenStack` usage with `_tabManager` calls
- Wire `_tabManager.ActiveTabChanged` to swap content in `_contentArea`
- Status bar reads environment from `_tabManager.ActiveTab?.EnvironmentUrl`

**Step 2: Register tab hotkeys in `RegisterGlobalHotkeys()`**

```csharp
    _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
        Key.CtrlMask | Key.T,
        HotkeyScope.Global,
        "New tab",
        ShowNewTabDialog));

    _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
        Key.CtrlMask | Key.W,
        HotkeyScope.Global,
        "Close tab",
        () => _tabManager.CloseTab(_tabManager.ActiveIndex)));

    _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
        Key.CtrlMask | Key.Tab,
        HotkeyScope.Global,
        "Next tab",
        () => _tabManager.ActivateNext()));
```

**Step 3: Build and run full test suite**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet`

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet`

Expected: All pass.

**Step 4: Commit**

```bash
git add src/PPDS.Cli/Tui/TuiShell.cs
git commit -m "feat(tui): integrate TabManager into TuiShell

Replace screen stack with TabManager for multi-tab navigation. TabBar
renders between menu bar and content area. Ctrl+T/Ctrl+W/Ctrl+Tab for
tab management. Status bar reflects active tab's environment. Alt+E
always opens environment selector (errors shown via F12 only).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Task 10: Final push

**Step 1: Run full build and test suite one final time**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

**Step 2: Push**

```bash
git push
```
