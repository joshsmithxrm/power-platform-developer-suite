# Session Persistence — Implementation Plan

**Date:** 2026-03-23
**Spec:** [specs/session-persistence.md](../../specs/session-persistence.md)
**Branch:** `feature/session-persistence`
**Issue:** #288

---

## Dependency Graph

```
Step 1: TuiStateStore + state records + ProfilePaths ─────┐
         │                                                  │
Step 2: Wire into InteractiveSession (DI) ─────────────────┤
         │                                                  │
    ┌────┴────┬──────────┬──────────┐                       │
    v         v          v          v                       │
Step 3:   Step 4:    Step 5:    Step 6:                     │
WebRes    ConnRef    EnvVars    PluginTr                    │
                                                            │
Step 7: Extension EnvironmentVariablesPanel (independent) ──┘
         │
Step 8: Cross-surface verification
```

Steps 3–6 are parallel (independent screens).
Step 7 is independent of all TUI steps (can run in parallel with everything).

---

## Step 1: TuiStateStore + State Records

**Files:**
- `src/PPDS.Auth/Profiles/ProfilePaths.cs` — add `TuiStateFile` property
- `src/PPDS.Cli/Services/Settings/TuiStateStore.cs` (new)
- `src/PPDS.Cli/Services/Settings/ScreenState.cs` (new)
- `tests/PPDS.Cli.Tests/Services/Settings/TuiStateStoreTests.cs` (new)

### 1a. Add `TuiStateFile` to ProfilePaths

Add property following the existing `EnvironmentsFile` pattern:

```csharp
public static string TuiStateFile => Path.Combine(ConfigDirectory, "tui-state.json");
```

### 1b. Create state records

```csharp
// ScreenState.cs
internal sealed record WebResourcesScreenState
{
    public Guid? SelectedSolutionId { get; init; }
    public bool TextOnly { get; init; } = true;
}

internal sealed record SolutionFilterScreenState
{
    public string? SolutionFilter { get; init; }
}

// PluginTraceFilter is already a serializable record in PPDS.Dataverse — reuse directly
```

### 1c. Create TuiStateStore

Follow `EnvironmentConfigStore` patterns:
- `SemaphoreSlim` for thread safety
- In-memory cache after first load
- Atomic file writes (write temp, rename)
- Graceful handling of missing/corrupt files

```csharp
internal sealed class TuiStateStore
{
    Task<T?> LoadScreenStateAsync<T>(string screenKey, string environmentUrl, CancellationToken ct = default);
    Task SaveScreenStateAsync<T>(string screenKey, string environmentUrl, T state, CancellationToken ct = default);
    Task ClearScreenStateAsync(string screenKey, string environmentUrl, CancellationToken ct = default);
}
```

Environment URLs normalized to lowercase with trailing slash (match `EnvironmentConfigStore` convention).

JSON structure: `{ "version": 1, "screens": { "<envUrl>": { "<screenKey>": { ... } } } }`

### 1d. Unit tests

| Test | Validates |
|------|-----------|
| `SaveAndLoad_RoundTrips` | Basic save + load returns same state |
| `Load_MissingFile_ReturnsNull` | No file → null, no exception |
| `Load_CorruptFile_ReturnsNull` | Malformed JSON → null, no exception |
| `Save_CreatesFileIfMissing` | First save creates file and directory |
| `StateIsScopedPerEnvironment` | Same screen key, different env URLs → independent state |
| `StateIsScopedPerScreen` | Same env URL, different screen keys → independent state |
| `EnvironmentUrl_Normalized` | Case-insensitive, trailing slash normalized |
| `Save_PreservesOtherScreenState` | Saving one screen doesn't clobber another |
| `ConcurrentSaves_DoNotCorrupt` | Parallel saves don't produce malformed JSON |

**Gate:** `dotnet test --filter "FullyQualifiedName~TuiStateStoreTests"`

---

## Step 2: Wire TuiStateStore into InteractiveSession

**Files:**
- `src/PPDS.Cli/Tui/InteractiveSession.cs` — add `TuiStateStore` field + accessor
- `src/PPDS.Cli/Tui/PpdsApplication.cs` — create `TuiStateStore` and pass to `InteractiveSession`

### 2a. Create TuiStateStore in PpdsApplication.Run()

Construct alongside existing `ProfileStore`:

```csharp
var stateStore = new TuiStateStore(ProfilePaths.TuiStateFile);
```

Pass to `InteractiveSession` constructor.

### 2b. Expose from InteractiveSession

Add a `GetTuiStateStore()` method (or property) so screens can access it. Follow the existing `GetErrorService()` / `GetProfileService()` pattern.

**Gate:** `dotnet build src/PPDS.Cli/PPDS.Cli.csproj`

---

## Step 3: WebResourcesScreen Integration

**Files:**
- `src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs`

### 3a. Restore state on construction

In the constructor (or `OnActivated`), after `EnvironmentUrl` is set:

```csharp
var state = await session.GetTuiStateStore()
    .LoadScreenStateAsync<WebResourcesScreenState>("WebResources", EnvironmentUrl);
if (state != null)
{
    _selectedSolutionId = state.SelectedSolutionId;
    _textOnly = state.TextOnly;
}
```

### 3b. Save state on filter change

After `_selectedSolutionId` changes (in the solution filter dialog handler) and after `_textOnly` toggles (Ctrl+T handler):

```csharp
ErrorService.FireAndForget(
    session.GetTuiStateStore().SaveScreenStateAsync("WebResources", EnvironmentUrl,
        new WebResourcesScreenState { SelectedSolutionId = _selectedSolutionId, TextOnly = _textOnly }),
    "WebResources.SaveState");
```

### 3c. Stale reference handling

After loading solutions list, check if `_selectedSolutionId` is in the list. If not:
- Show status: "Previously filtered solution not found — showing all"
- Clear `_selectedSolutionId` to null
- Save cleared state

**Gate:** `dotnet test --filter "Category=TuiUnit"` + manual verification

---

## Step 4: ConnectionReferencesScreen Integration

**Files:**
- `src/PPDS.Cli/Tui/Screens/ConnectionReferencesScreen.cs`

Same pattern as Step 3 but with `SolutionFilterScreenState` and `_solutionFilter` (string).

### 4a. Restore state on construction

```csharp
var state = await session.GetTuiStateStore()
    .LoadScreenStateAsync<SolutionFilterScreenState>("ConnectionReferences", EnvironmentUrl);
if (state != null)
    _solutionFilter = state.SolutionFilter;
```

### 4b. Save state on filter change

After solution filter dialog sets `_solutionFilter`:

```csharp
ErrorService.FireAndForget(
    session.GetTuiStateStore().SaveScreenStateAsync("ConnectionReferences", EnvironmentUrl,
        new SolutionFilterScreenState { SolutionFilter = _solutionFilter }),
    "ConnectionReferences.SaveState");
```

### 4c. Stale reference handling

After loading solutions list in the filter dialog, check if `_solutionFilter` matches any solution name. If not, show message and clear.

**Gate:** `dotnet test --filter "Category=TuiUnit"` + manual verification

---

## Step 5: EnvironmentVariablesScreen Integration

**Files:**
- `src/PPDS.Cli/Tui/Screens/EnvironmentVariablesScreen.cs`

Identical pattern to Step 4 — uses `SolutionFilterScreenState` with screen key `"EnvironmentVariables"`.

**Gate:** `dotnet test --filter "Category=TuiUnit"` + manual verification

---

## Step 6: PluginTracesScreen Integration

**Files:**
- `src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs`

### 6a. Restore state on construction

```csharp
var filter = await session.GetTuiStateStore()
    .LoadScreenStateAsync<PluginTraceFilter>("PluginTraces", EnvironmentUrl);
if (filter != null)
    _currentFilter = filter;
```

### 6b. Save state on filter change

After `PluginTraceFilterDialog` returns a non-null filter:

```csharp
ErrorService.FireAndForget(
    session.GetTuiStateStore().SaveScreenStateAsync("PluginTraces", EnvironmentUrl, _currentFilter),
    "PluginTraces.SaveState");
```

Also save when filter is cleared (save null/clear state).

### 6c. No stale reference handling needed

PluginTraceFilter fields are query parameters, not references to entities that can be deleted. Stale date ranges simply return 0 results — which is expected and visible in the filter dialog.

**Gate:** `dotnet test --filter "Category=TuiUnit"` + manual verification

---

## Step 7: Extension EnvironmentVariablesPanel

**Files:**
- `src/PPDS.Extension/src/panels/EnvironmentVariablesPanel.ts`
- Callers of `EnvironmentVariablesPanel.show()` (to pass `context`)

This step is fully independent of TUI work.

### 7a. Add `context` parameter

Add `context: vscode.ExtensionContext` to the private constructor and `static show()` factory, matching WebResourcesPanel's signature.

### 7b. Restore on creation

In constructor, after existing initialization:

```typescript
this.solutionFilter = this.context.globalState.get<string | null>(
    'ppds.environmentVariables.solutionFilter', null);
```

### 7c. Save on change

In the `filterBySolution` message handler:

```typescript
case 'filterBySolution':
    this.solutionFilter = message.solutionId;
    void this.context.globalState.update('ppds.environmentVariables.solutionFilter', this.solutionFilter);
    await this.loadEnvironmentVariables();
    break;
```

### 7d. Clear on environment change

In `onEnvironmentChanged()`:

```typescript
this.solutionFilter = null;
void this.context.globalState.update('ppds.environmentVariables.solutionFilter', null);
```

### 7e. Update callers

Find all call sites of `EnvironmentVariablesPanel.show()` and pass `context`. Follow the pattern from WebResourcesPanel call sites.

**Gate:** `npm run ext:test` + `npm run ext:lint`

---

## Step 8: Cross-Surface Verification

Manual verification checklist (not automated):

- [ ] TUI: Open WebResources, filter to a solution, close TUI, reopen — filter restored
- [ ] TUI: Open ConnectionReferences on env A with filter, switch to env B, open ConnectionReferences — no filter (independent)
- [ ] TUI: Open PluginTraces, set filter with date range, close TUI, reopen — full filter restored including dates
- [ ] TUI: Delete a solution, reopen screen that was filtered to it — stale message shown, view unfiltered
- [ ] TUI: First launch (no tui-state.json) — all screens start unfiltered, no errors
- [ ] Extension: Open EnvironmentVariablesPanel, filter to solution, close panel, reopen — filter restored
- [ ] Extension: Switch environment — filter cleared
- [ ] Confirm `tui-state.json` contains no secrets (only solution names/IDs and filter criteria)

---

## Risk Register

| Risk | Mitigation |
|------|------------|
| Async state load delays screen rendering | Load state before `LoadDataAsync()` — state is local file I/O (~1ms), not a Dataverse call |
| TuiStateStore generic deserialization fails for PluginTraceFilter enums | Use `System.Text.Json` with `JsonStringEnumConverter` — test in Step 1 |
| Extension callers of `EnvironmentVariablesPanel.show()` are numerous | Grep for all call sites before starting Step 7 |
| Concurrent TUI instances writing tui-state.json | SemaphoreSlim guards within-process; cross-process is last-write-wins (acceptable for filter state) |
