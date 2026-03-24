# Session Persistence

**Status:** Draft
**Last Updated:** 2026-03-23
**Code:** [src/PPDS.Cli/Tui/](../src/PPDS.Cli/Tui/), [src/PPDS.Extension/src/panels/](../src/PPDS.Extension/src/panels/)
**Surfaces:** TUI | Extension

---

## Overview

Persists user filter selections across sessions so users don't have to re-configure their view every time they open a screen. TUI screens remember solution filters and settings per environment in a dedicated state file. The Extension's EnvironmentVariablesPanel is updated to persist its solution filter to `globalState`, matching the existing WebResourcesPanel pattern.

### Goals

- **Reduce friction**: Filter selections survive TUI restarts — screens open pre-filtered to the user's last selection for that environment
- **Extension parity**: EnvironmentVariablesPanel persists solution filter like WebResourcesPanel already does

### Non-Goals

- Persisting open tab layout or tab order across TUI sessions
- TUI profile switch persistence (`activeProfileIndex` from Alt+P) — separate concern
- CLI session persistence (CLI is stateless by design)
- MCP session persistence (locked at startup per Constitution SS2)
- Persisting scroll positions, selected rows, or cursor state
- Query history persistence (separate feature)

---

## Architecture

```
TUI Screen                    TuiStateStore
┌──────────────────┐         ┌──────────────────────┐
│ WebResourcesScreen│────────▶│ LoadAsync(screenKey,  │
│ _selectedSolutionId│        │           envUrl)     │
│ _textOnly          │◀───────│ SaveAsync(screenKey,  │
└──────────────────┘         │           envUrl,     │
                             │           state)      │
┌──────────────────┐         │                       │
│ PluginTracesScreen│────────▶│                       │
│ _currentFilter    │◀───────│  ~/.ppds/tui-state.json│
└──────────────────┘         └──────────────────────┘

Extension
┌──────────────────────────┐
│ EnvironmentVariablesPanel │──▶ context.globalState
│ solutionFilter            │     ppds.environmentVariables.solutionFilter
└──────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `TuiStateStore` | Read/write `tui-state.json` with async I/O and semaphore locking |
| `TuiScreenState` | Serializable state models per screen type |
| Screen integration | Each screen saves state on filter change, restores on construction |
| `EnvironmentVariablesPanel` | Extension panel updated to persist solution filter to `globalState` |

### Dependencies

- Uses patterns from: [architecture.md](./architecture.md) (Application Services, `IProgressReporter`)
- Related: [web-resources.md](./web-resources.md), [connection-references.md](./connection-references.md), [environment-variables.md](./environment-variables.md), [plugin-traces.md](./plugin-traces.md)

---

## Specification

### Core Requirements

1. TUI filter selections persist to `~/.ppds/tui-state.json` (or `%LOCALAPPDATA%\PPDS\tui-state.json` on Windows), keyed by `(screenType, environmentUrl)`
2. State is loaded when a screen is constructed and applied before first data load
3. State is saved whenever the user changes a filter selection
4. Persistence is always on — no setting to enable/disable
5. Stale references (persisted solution no longer exists) produce a visible status message, clear the persisted value, and show unfiltered results
6. Extension EnvironmentVariablesPanel persists `solutionFilter` to `context.globalState` following the WebResourcesPanel pattern

### State Per Screen

| Screen | Persisted Fields | Storage Key |
|--------|-----------------|-------------|
| WebResourcesScreen | `selectedSolutionId` (Guid?), `textOnly` (bool) | `WebResources` |
| ConnectionReferencesScreen | `solutionFilter` (string?) | `ConnectionReferences` |
| EnvironmentVariablesScreen | `solutionFilter` (string?) | `EnvironmentVariables` |
| PluginTracesScreen | `currentFilter` (PluginTraceFilter?) | `PluginTraces` |

### File Format

```json
{
  "version": 1,
  "screens": {
    "https://contoso-dev.crm.dynamics.com": {
      "WebResources": {
        "selectedSolutionId": "a1b2c3d4-...",
        "textOnly": true
      },
      "ConnectionReferences": {
        "solutionFilter": "ContosoCore"
      },
      "EnvironmentVariables": {
        "solutionFilter": "ContosoCore"
      },
      "PluginTraces": {
        "currentFilter": {
          "typeName": "ContosoPlugins.AccountHandler",
          "messageName": "Update",
          "hasException": true
        }
      }
    },
    "https://contoso-qa.crm.dynamics.com": {
      "WebResources": {
        "selectedSolutionId": "e5f6g7h8-...",
        "textOnly": false
      }
    }
  }
}
```

Environment URLs are normalized (lowercase, trailing slash) to match `EnvironmentConfigStore` conventions.

### Primary Flows

**Save flow (on filter change):**

1. **User changes filter**: User selects a solution in the filter dropdown or applies a PluginTraceFilter
2. **Screen updates in-memory state**: Existing behavior, unchanged
3. **Screen saves to store**: `await _stateStore.SaveAsync(screenKey, envUrl, state)`
4. **Store writes file**: Merges into existing JSON, writes atomically

**Restore flow (on screen construction):**

1. **Screen constructs**: TuiScreenBase passes `TuiStateStore` to screen
2. **Screen loads state**: `var state = await _stateStore.LoadAsync<T>(screenKey, envUrl)`
3. **Screen applies state**: Sets `_solutionFilter` / `_selectedSolutionId` / `_currentFilter` from loaded state
4. **Screen loads data**: Existing `LoadDataAsync()` runs with restored filter applied

**Stale reference flow:**

1. **Screen restores filter**: Loads persisted `solutionFilter = "DeletedSolution"`
2. **Screen loads data**: Service returns results; filter yields 0 matches or solution not found in dropdown
3. **Screen detects staleness**: Solution name/ID not present in the fetched solutions list
4. **Screen shows message**: Status label shows "Previously filtered solution 'DeletedSolution' not found — showing all"
5. **Screen clears filter**: Resets to unfiltered view and saves cleared state to store

### Surface-Specific Behavior

#### TUI Surface

**TuiStateStore** follows `EnvironmentConfigStore` patterns:
- JSON file at `ProfilePaths.TuiStateFile` (new property)
- `SemaphoreSlim` for concurrent access safety
- `LoadAsync()` / `SaveAsync()` with cancellation support
- File created on first write (not on TUI startup)

**Screen integration pattern** (each screen):
```csharp
// In constructor or OnActivated, after environment is known
var state = await stateStore.LoadScreenStateAsync<WebResourcesState>(
    "WebResources", EnvironmentUrl);
if (state != null)
{
    _selectedSolutionId = state.SelectedSolutionId;
    _textOnly = state.TextOnly;
}

// After filter change
await stateStore.SaveScreenStateAsync(
    "WebResources", EnvironmentUrl,
    new WebResourcesState { SelectedSolutionId = _selectedSolutionId, TextOnly = _textOnly });
```

**State is saved fire-and-forget** via `ErrorService.FireAndForget()` — filter persistence should never block UI interaction.

#### Extension Surface

**EnvironmentVariablesPanel** changes:
1. Add `context: vscode.ExtensionContext` parameter to constructor and `show()` factory
2. Restore `solutionFilter` from `context.globalState.get<string | null>('ppds.environmentVariables.solutionFilter', null)` in constructor
3. Save on change: `context.globalState.update('ppds.environmentVariables.solutionFilter', this.solutionFilter)` in `filterBySolution` handler
4. Clear on environment change: `context.globalState.update('ppds.environmentVariables.solutionFilter', null)` in `onEnvironmentChanged`

This matches the WebResourcesPanel pattern exactly.

### Constraints

- `tui-state.json` must not contain secrets — it stores only solution names, IDs, and filter criteria
- File writes are atomic (write to temp file, rename) to prevent corruption from crashes
- State store must tolerate missing or malformed files gracefully (treat as empty state)
- PluginTraceFilter is serialized as-is — all fields including dates and GUIDs. Stale date filters are visible to the user in the filter dialog; they can clear them.
- Constitution SS1 applies: persisted state is a default for new screens, not a live binding. Opening a second screen of the same type on the same environment does NOT share filter state with the first.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | WebResourcesScreen restores `selectedSolutionId` and `textOnly` from `tui-state.json` on construction | `TuiStateStoreTests.RestoresWebResourcesState` | 🔲 |
| AC-02 | WebResourcesScreen saves state to `tui-state.json` when solution filter changes | `TuiStateStoreTests.SavesWebResourcesState` | 🔲 |
| AC-03 | ConnectionReferencesScreen restores `solutionFilter` from `tui-state.json` on construction | `TuiStateStoreTests.RestoresConnectionReferencesState` | 🔲 |
| AC-04 | ConnectionReferencesScreen saves state when solution filter changes | `TuiStateStoreTests.SavesConnectionReferencesState` | 🔲 |
| AC-05 | EnvironmentVariablesScreen restores `solutionFilter` from `tui-state.json` on construction | `TuiStateStoreTests.RestoresEnvironmentVariablesState` | 🔲 |
| AC-06 | EnvironmentVariablesScreen saves state when solution filter changes | `TuiStateStoreTests.SavesEnvironmentVariablesState` | 🔲 |
| AC-07 | PluginTracesScreen restores full `PluginTraceFilter` from `tui-state.json` on construction | `TuiStateStoreTests.RestoresPluginTracesState` | 🔲 |
| AC-08 | PluginTracesScreen saves full `PluginTraceFilter` when filter is applied | `TuiStateStoreTests.SavesPluginTracesState` | 🔲 |
| AC-09 | State is scoped per environment — same screen on different environments has independent state | `TuiStateStoreTests.StateIsScopedPerEnvironment` | 🔲 |
| AC-10 | Stale solution filter shows visible message and clears to unfiltered view | `TuiStateStoreTests.StaleFilterShowsMessageAndClears` | 🔲 |
| AC-11 | Missing or malformed `tui-state.json` is handled gracefully (treated as empty) | `TuiStateStoreTests.HandlesMissingFile` | 🔲 |
| AC-12 | Extension EnvironmentVariablesPanel restores solution filter from `globalState` on creation | `EnvironmentVariablesPanelTests.RestoresSolutionFilter` | 🔲 |
| AC-13 | Extension EnvironmentVariablesPanel saves solution filter to `globalState` on change | `EnvironmentVariablesPanelTests.SavesSolutionFilter` | 🔲 |
| AC-14 | Extension EnvironmentVariablesPanel clears solution filter on environment change | `EnvironmentVariablesPanelTests.ClearsSolutionFilterOnEnvChange` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| First launch (no tui-state.json) | File doesn't exist | All screens start unfiltered, file created on first filter change |
| Corrupted JSON | Malformed file content | Log warning, treat as empty state, overwrite on next save |
| Environment URL casing | `HTTPS://Contoso.CRM.dynamics.com` | Normalized to lowercase before lookup |
| Concurrent TUI instances | Two TUI processes write simultaneously | SemaphoreSlim prevents corruption within process; last-write-wins across processes |
| Unknown screen key in file | Future screen type added, old file lacks it | Returns null state, screen starts unfiltered |
| Extra keys in file | Old screen type removed, file still has its state | Ignored on load, preserved on save (forward compatibility) |

---

## Core Types

### TuiStateStore

Persistent storage for TUI screen state, following `EnvironmentConfigStore` patterns.

```csharp
internal sealed class TuiStateStore
{
    Task<T?> LoadScreenStateAsync<T>(string screenKey, string environmentUrl, CancellationToken ct = default);
    Task SaveScreenStateAsync<T>(string screenKey, string environmentUrl, T state, CancellationToken ct = default);
    Task ClearScreenStateAsync(string screenKey, string environmentUrl, CancellationToken ct = default);
}
```

### Screen State Records

```csharp
internal sealed record WebResourcesScreenState
{
    public Guid? SelectedSolutionId { get; init; }
    public bool TextOnly { get; init; } = true;
}

internal sealed record SolutionFilterScreenState
{
    public string? SolutionFilter { get; init; }
}

// PluginTraceFilter is already a serializable record — used directly
```

`SolutionFilterScreenState` is shared by ConnectionReferencesScreen and EnvironmentVariablesScreen since they have identical state shape.

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| File not found | First launch or file deleted | Return null state; file created on first save |
| JSON parse error | Corrupted or hand-edited file | Log warning, return null state, overwrite on next save |
| IO exception on save | Disk full, permissions | Log error via `ErrorService`, don't block UI. State lost for this change only. |
| Stale solution reference | Persisted solution not in environment | Show status message, clear filter, save cleared state |

### Recovery Strategies

- **Read failures**: Always recoverable — return empty state, let user start fresh
- **Write failures**: Non-blocking — fire-and-forget save, log error, user's in-memory state is unaffected

---

## Design Decisions

### Why a separate `tui-state.json` file?

**Context:** Need to persist TUI screen filter state. Could extend `profiles.json`, `environments.json`, or create a new file.

**Decision:** New dedicated file `tui-state.json` in the PPDS config directory.

**Alternatives considered:**
- Extend `environments.json`: Rejected — mixes UI state with environment metadata (labels, colors, safety settings). TUI state is irrelevant to Extension and CLI consumers of this file.
- Extend `profiles.json`: Rejected — filters are per-environment, not per-profile. Data model mismatch.
- Per-screen files: Rejected — unnecessary complexity. One file with nested keys is sufficient.

**Consequences:**
- Positive: Clean separation of concerns. Existing config files unchanged. Schema can evolve independently.
- Negative: One more file in `~/.ppds/`. Acceptable — it's a config directory.

### Why always-on (no setting)?

**Context:** Original issue proposed `tui.rememberSession` defaulting to false.

**Decision:** Persistence is always on. No setting.

**Alternatives considered:**
- Opt-in setting: Rejected — adds config surface area for a feature nobody would turn off. Remembering a dropdown selection is table-stakes UX, not a behavioral change that needs a toggle.

**Consequences:**
- Positive: Zero configuration. Every user benefits immediately.
- Negative: No escape hatch. Acceptable — clearing a filter is one keypress.

### Why visible fallback for stale references?

**Context:** Persisted solution may no longer exist in the environment.

**Decision:** Show a status message ("Previously filtered solution 'X' not found — showing all"), clear the stale value, and show unfiltered results.

**Alternatives considered:**
- Silent fallback: Rejected — violates the spirit of Constitution I4. Silently changing the user's view without explanation is confusing.
- Keep stale filter (show empty): Rejected — "0 results" with no explanation of why is worse UX than the brief notification.

**Consequences:**
- Positive: User knows why their view changed. Stale state self-heals.
- Negative: Requires staleness detection logic per screen. Acceptable — it's a simple "is my filter value in the loaded list?" check.

### Why persist full PluginTraceFilter including dates?

**Context:** PluginTraceFilter has ephemeral fields (dates, correlation IDs) that may be stale on next launch.

**Decision:** Persist the full object. All fields including dates and GUIDs.

**Alternatives considered:**
- Persist only stable fields: Rejected — adds complexity (partial serialization), and the filter dialog already shows all fields visibly. Users can see stale dates and clear them.

**Consequences:**
- Positive: Simple serialization. No field-by-field logic.
- Negative: Users may see 0 results from stale date filters. Acceptable — the filter is visible in the dialog and easy to modify.

---

## Extension Points

### Adding Persistence to a New TUI Screen

1. **Define state record**: Create a `record` with the fields to persist (or reuse `SolutionFilterScreenState`)
2. **Choose screen key**: Add a constant string key (e.g., `"NewScreen"`)
3. **Load on construction**: Call `stateStore.LoadScreenStateAsync<T>(key, envUrl)` and apply
4. **Save on change**: Call `stateStore.SaveScreenStateAsync(key, envUrl, state)` after filter updates
5. **Handle staleness**: Check if restored filter value exists in loaded data; show message and clear if not

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| N/A | — | — | — | No configuration. Persistence is always on. |

**File location:** `ProfilePaths.TuiStateFile` → `{configDir}/tui-state.json`

---

## Related Specs

- [web-resources.md](./web-resources.md) - WebResourcesScreen filter persistence
- [connection-references.md](./connection-references.md) - ConnectionReferencesScreen filter persistence
- [environment-variables.md](./environment-variables.md) - EnvironmentVariablesScreen filter persistence (TUI + Extension)
- [plugin-traces.md](./plugin-traces.md) - PluginTracesScreen filter persistence
- [tui.md](./tui.md) - TUI architecture and screen lifecycle

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-23 | Initial spec |

---

## Roadmap

- Persist TUI profile switch (`activeProfileIndex` on Alt+P) — currently session-only
- Persist open tab layout across TUI sessions
- Persist additional screen settings (e.g., SolutionsScreen managed/unmanaged toggle)
