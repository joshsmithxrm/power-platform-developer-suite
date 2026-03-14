# VS Code Extension: Persistence & Solutions Panel Polish

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-14
**Code:** [extension/src/](../extension/src/)

---

## Overview

The VS Code extension's profile tree view loses expansion state on every refresh and offers no user-controlled ordering. The Solutions panel is missing solution-level detail, has incomplete component type resolution (most 10000+ types show as "Unknown"), and lacks search/filter. This spec addresses all three areas in a single pass to bring the extension UX to a polished, production-quality state.

### Goals

- **Profile tree persistence**: Expansion state survives refreshes and VS Code restarts; users can reorder profiles
- **Solutions panel completeness**: Surface all solution metadata (dates, description, publisher details) and resolve all component type names including the 10000+ range
- **Solutions panel usability**: Add search/filter, display name formatting, and managed toggle persistence

### Non-Goals

- Resolving individual component objectIds to friendly names (e.g., showing entity display name instead of GUID) — deferred to a future enhancement that would require per-type metadata queries
- Solutions panel column customization or resizable columns
- Export/import of solution data
- Profile drag-and-drop reordering (move-up/move-down commands are sufficient)

---

## Architecture

```
┌────────────────────────────────┐
│  VS Code Extension (TypeScript)│
│                                │
│  ┌──────────────────────────┐  │
│  │ ProfileTreeDataProvider  │  │
│  │  - id on ProfileTreeItem │  │
│  │  - sortOrder from state  │  │
│  │  - move up/down commands │  │
│  └──────────┬───────────────┘  │
│             │                  │
│  ┌──────────┴───────────────┐  │
│  │ globalState persistence  │  │
│  │  - profile sort order    │  │
│  │  - managed toggle        │  │
│  └──────────────────────────┘  │
│                                │
│  ┌──────────────────────────┐  │
│  │ SolutionsPanel (webview) │  │
│  │  - detail card on expand │  │
│  │  - search/filter toolbar │  │
│  │  - resolved type names   │  │
│  └──────────┬───────────────┘  │
│             │ RPC              │
└─────────────┼──────────────────┘
              ▼
┌────────────────────────────────┐
│  PPDS Daemon (C# / .NET)      │
│                                │
│  ┌──────────────────────────┐  │
│  │ SolutionService          │  │
│  │  - componenttype metadata│  │
│  │  - per-env cache         │  │
│  │  - extended solution DTO │  │
│  └──────────────────────────┘  │
│                                │
│  ┌──────────────────────────┐  │
│  │ RPC Handler              │  │
│  │  - solutions/list fields │  │
│  │  - solutions/components  │  │
│  └──────────────────────────┘  │
└────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `ProfileTreeItem` | Set stable `id` for VS Code expansion persistence |
| `ProfileTreeDataProvider` | Apply user sort order from `globalState` |
| `globalState` keys | Persist sort order + managed toggle |
| `IMetadataService.GetOptionSetAsync()` | Existing service — query `componenttype` global option set |
| `SolutionService` component type cache | In-memory cache per environment URL, delegates to `IMetadataService` |
| `SolutionsPanel.loadSolutions()` | Pass through date/description fields already returned by RPC |
| RPC `solutions/components` handler | Return resolved component type names |
| `SolutionsPanel` webview | Detail card, search filter, display format |

### Dependencies

- Depends on: [architecture.md](./architecture.md) — Application Services pattern
- Uses patterns from: old extension's `MoveEnvironmentUseCase` for profile ordering

---

## Specification

### 1. Profile Tree — Expansion Persistence

#### Core Requirements

1. `ProfileTreeItem` sets `this.id` to a stable, unique string: `` `profile://${profile.identity}//${profile.authMethod}//${profile.cloud}` ``
2. VS Code's built-in `TreeView` expansion persistence handles the rest — no custom event handlers needed
3. Expansion state survives `refresh()` calls, window reloads, and VS Code restarts

#### Primary Flow

1. **User expands a profile** in the tree view
2. **VS Code internally tracks** the expansion state keyed by item `id`
3. **On refresh/restart**, VS Code calls `getChildren()` → new `ProfileTreeItem` instances have the same `id` → VS Code restores expansion state automatically

### 2. Profile Tree — Custom Ordering

#### Core Requirements

1. Two new commands: `ppds.moveProfileUp` and `ppds.moveProfileDown`
2. Commands registered in `package.json` with context menu entries on `viewItem == profile`
3. Sort order stored in `context.globalState` under key `ppds.profiles.sortOrder` as `Record<string, number>` (profile ID → sort position)
4. Default order: active profile first, then daemon-returned order
5. Move commands swap sort positions of adjacent profiles and call `refresh()`

#### Primary Flow — Move Profile Up

1. **User right-clicks profile** → selects "Move Up"
2. **Command handler** reads current sort order from `globalState`
3. **Swap** the sort position of the target profile with the one above it
4. **Write** updated sort order to `globalState`
5. **Call** `profileTreeProvider.refresh()`

#### Data Model

```typescript
// globalState key: 'ppds.profiles.sortOrder'
// Value: mapping of profile ID string → numeric sort position
type ProfileSortOrder = Record<string, number>;
```

### 3. Solutions Panel — Detail Card

#### Core Requirements

1. When a solution is expanded, a styled detail card appears above the component groups
2. Detail card fields: Unique Name, Publisher, Type (Managed/Unmanaged), Installed date, Modified date, Description
3. Dates formatted as locale-appropriate short dates
4. Description truncated to 3 lines with "..." if longer

#### Data Flow Changes

The daemon RPC handler and TypeScript `SolutionInfoDto` already include `createdOn`, `modifiedOn`, `installedOn`, and `description`. The gap is only in `SolutionsPanel.loadSolutions()` which drops these fields when constructing the webview message (line ~162-174). Changes needed:

1. **`SolutionsPanel.loadSolutions()`**: include `createdOn`, `modifiedOn`, `installedOn`, `description` in the `solutionsLoaded` webview message payload
2. **Webview `renderSolutions()`**: render detail card HTML in each solution's expanded container, populated on load (data is already available from the message)

#### Detail Card Layout

```
┌──────────────────────────────────────────┐
│  Unique Name   contoso_mysolution        │
│  Publisher      Contoso                   │
│  Type           Unmanaged                 │
│  Installed      2026-01-15                │
│  Modified       2026-03-12                │
│  Description    Core business logic...    │
└──────────────────────────────────────────┘
```

Styled as a `div` with `var(--vscode-textBlockQuote-background)` background, subtle left border using `var(--vscode-textBlockQuote-border)`, and label-value pairs in a CSS grid (label column auto-width, value column flex).

### 4. Solutions Panel — Component Type Resolution

#### Core Requirements

1. `SolutionService` delegates to the existing `IMetadataService.GetOptionSetAsync("componenttype")` to query the global option set metadata at runtime
2. Returns `Dictionary<int, string>` mapping all type codes to display names, including the 10000+ range
3. Results cached in-memory per environment URL in `SolutionService` (keyed by normalized URL)
4. Cache lifetime: duration of the daemon process (component types don't change during a session)
5. On failure, falls back to existing hardcoded `ComponentTypeNames` dictionary
6. `GetComponentsAsync()` uses the resolved names from cache instead of only the hardcoded dictionary

**Note:** The existing hardcoded `ComponentTypeNames` dictionary in `SolutionService.cs` has incorrect values for several component types (e.g., `65 → FieldSecurityProfile` should be `70`, `68 → PluginType` should be `90`). The generated `componenttype` enum is authoritative. The runtime metadata query will supersede the hardcoded dictionary, but the hardcoded values should also be corrected as part of this work to ensure the fallback path is accurate.

#### Primary Flow

1. **First `solutions/components` call for an environment** triggers metadata query
2. **`IMetadataService.GetOptionSetAsync("componenttype")`** returns option set metadata with all values
3. **Parse** option values into `Dictionary<int, string>` and **cache** in a `ConcurrentDictionary<string, Dictionary<int, string>>` keyed by environment URL
4. **Subsequent calls** for the same environment use cached mapping
5. **Component type name resolution** in `GetComponentsAsync()` checks runtime cache first, then corrected hardcoded dictionary, then falls back to `Unknown ({type})`

#### Implementation Detail

```csharp
// In SolutionService — inject IMetadataService via constructor
private readonly IMetadataService _metadataService;
private static readonly ConcurrentDictionary<string, Dictionary<int, string>> _componentTypeCache = new();

private async Task<Dictionary<int, string>> GetComponentTypeNamesAsync(
    CancellationToken cancellationToken = default)
{
    // Check cache by environment URL
    // If miss: call _metadataService.GetOptionSetAsync("componenttype")
    // Parse OptionSetMetadataDto values into Dictionary<int, string>
    // Cache and return
}
```

### 5. Solutions Panel — Search/Filter

#### Core Requirements

1. Text input field in the toolbar, between the Managed button and the environment picker
2. Client-side filtering — no daemon round-trip
3. Filters on: friendly name, unique name, publisher name (case-insensitive contains)
4. Debounced at 150ms on input
5. Status bar shows filtered count: "5 of 23 solutions" when filtered, "23 solutions" when unfiltered
6. Empty state: "No solutions match filter" with italicized styling
7. Filter clears when solutions reload (refresh or environment switch)

### 6. Solutions Panel — Display Name Format

#### Core Requirements

1. Component type group headers: display the resolved type name as-is (e.g., `Entity`, `WebResource`, `CanvasApp`)
2. Individual component items: show the objectId (GUID) as currently — resolving GUIDs to friendly names (e.g., entity display names) is deferred to a future enhancement (see Non-Goals)
3. The `logicalName (DisplayName)` format is the target convention for when individual component name resolution is implemented; this spec establishes the convention but only applies it where data is available (group headers)

### 7. Solutions Panel — Managed Toggle Persistence

#### Core Requirements

1. On panel creation, read `context.globalState.get<boolean>('ppds.solutionsPanel.includeManaged')` to restore toggle state
2. On toggle, write the new value to `globalState`
3. Panel initializes with the persisted value (or `false` if not set)
4. The `globalState` key is shared across all Solutions panel instances

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `ppds.profiles.sortOrder` globalState | Must be `Record<string, number>` or absent | Reset to `{}` on parse error |
| `ppds.solutionsPanel.includeManaged` globalState | Must be `boolean` or absent | Default to `false` on parse error |
| Profile tree item `id` | Must be non-empty string | Fall back to label-based identity (VS Code default) |
| Component type metadata cache | Keyed by normalized (lowercased, trailing-slash-stripped) env URL | Prevents duplicate cache entries for same environment |

### Known Issues to Fix

The current `SolutionsPanel.loadSolutions()` makes a **redundant second RPC call** when `includeManaged` is false (lines 148-159) solely to count managed solutions. This should be addressed: either have the daemon return `totalCount` alongside the filtered list, or accept the double-call as a known cost. The search/filter status bar ("5 of 23 solutions") will interact with this count display.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | ProfileTreeItem has stable `id` based on identity + authMethod + cloud | `profileTreeView.test.ts` | 🔲 |
| AC-02 | `ProfileTreeItem.id` is consistent across multiple `getChildren()` calls for the same profile | `profileTreeView.test.ts` | 🔲 |
| AC-03 | Profile expansion state persists across VS Code restart (depends on AC-01 + AC-02) | Manual verification | 🔲 |
| AC-04 | Move Profile Up swaps sort position with adjacent profile and refreshes tree | `profileTreeView.test.ts` | 🔲 |
| AC-05 | Move Profile Down swaps sort position with adjacent profile and refreshes tree | `profileTreeView.test.ts` | 🔲 |
| AC-06 | Profile sort order persists in globalState and survives VS Code restart | `profileTreeView.test.ts` | 🔲 |
| AC-07 | Solution detail card shows Unique Name, Publisher, Type, Installed, Modified, Description | Manual verification | 🔲 |
| AC-08 | SolutionsPanel webview message includes createdOn, modifiedOn, installedOn, description from existing RPC data | `SolutionsPanel` manual verification | 🔲 |
| AC-09 | Component types in 10000+ range resolve to display names via option set metadata query | `SolutionServiceTests.cs` | 🔲 |
| AC-10 | Component type metadata is cached per environment URL | `SolutionServiceTests.cs` | 🔲 |
| AC-11 | Component type resolution falls back to hardcoded dictionary on metadata query failure | `SolutionServiceTests.cs` | 🔲 |
| AC-12 | Search input filters solution list by friendly name, unique name, and publisher | Manual verification | 🔲 |
| AC-13 | Search shows "5 of 23 solutions" count in status bar when active | Manual verification | 🔲 |
| AC-14 | Managed toggle state persists across panel close/reopen via globalState | Manual verification | 🔲 |
| AC-15 | Component type group headers show resolved type names (not "Unknown") for 10000+ range | Manual verification | 🔲 |
| AC-16 | Hardcoded `ComponentTypeNames` dictionary values corrected to match generated `componenttype` enum | `SolutionServiceTests.cs` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Profile identity contains special characters | `user@domain.com` with `interactive` and `commercial` | ID uses `//` separator to avoid collision: `profile://identity//authMethod//cloud` |
| Move up on first profile | User right-clicks top profile | Command is no-op (or hidden via `when` clause) |
| Move down on last profile | User right-clicks bottom profile | Command is no-op (or hidden via `when` clause) |
| No profiles exist | Empty profile list | No sort order stored, no move commands visible |
| Metadata query fails | Network error or permissions issue | Falls back to hardcoded dictionary, no user-facing error |
| Solution has no components | Expand solution | Shows "No components" message |
| Solution has no description | Description is null | Detail card omits description row |
| Search with no matches | Filter text "zzzzz" | Shows "No solutions match filter" empty state |
| All solutions are managed | Managed: Off | Shows empty state with "N managed hidden" in status bar |

---

## Core Types

### ProfileTreeItem (modified)

```typescript
export class ProfileTreeItem extends vscode.TreeItem {
    constructor(public readonly profile: ProfileInfo) {
        const label = profile.name ?? `Profile ${profile.index}`;
        super(label, vscode.TreeItemCollapsibleState.Collapsed);
        this.id = `profile://${profile.identity}//${profile.authMethod}//${profile.cloud}`;
        // ... existing constructor body
    }
}
```

### SolutionComponentInfo (extended context)

```csharp
// GetComponentsAsync now uses cached runtime metadata
var typeNames = await GetComponentTypeNamesAsync(cancellationToken);
var typeName = typeNames.TryGetValue(type, out var name)
    ? name
    : ComponentTypeNames.TryGetValue(type, out var fallback)
        ? fallback
        : $"Unknown ({type})";
```

---

## API/Contracts

### Modified RPC: solutions/list

Response gains additional fields:

```json
{
  "solutions": [
    {
      "uniqueName": "contoso_core",
      "friendlyName": "Contoso Core",
      "version": "1.0.3.4",
      "publisherName": "Contoso",
      "isManaged": false,
      "description": "Core business logic",
      "createdOn": "2025-06-15T10:30:00Z",
      "modifiedOn": "2026-03-12T14:22:00Z",
      "installedOn": "2025-06-15T10:30:00Z"
    }
  ]
}
```

### Modified RPC: solutions/components

Component type names now include 10000+ range resolved from environment metadata:

```json
{
  "components": [
    {
      "id": "...",
      "objectId": "...",
      "componentType": 10038,
      "componentTypeName": "AI Builder Model",
      "rootComponentBehavior": 0,
      "isMetadata": false
    }
  ]
}
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Metadata query failure | `IMetadataService.GetOptionSetAsync` fails (permissions, network) | Fall back to corrected hardcoded `ComponentTypeNames` dictionary silently |
| globalState read failure | Corrupted state data | Use default values (empty sort order, managed = false) |
| Profile ID collision | Two profiles with identical identity+authMethod+cloud | Extremely unlikely; append index as tiebreaker if needed |

### Recovery Strategies

- **Metadata query failure**: Log warning, continue with hardcoded fallback. No user-facing error — the only symptom is some component types showing as "Unknown"
- **Corrupted globalState**: Catch parse errors, reset to defaults, log warning
- **Component type not in any mapping**: Show `Unknown ({typeCode})` — same as current behavior, but should be rare once metadata query works

---

## Design Decisions

### Why VS Code built-in expansion persistence (Option A) over manual globalState tracking?

**Context:** Profile tree items lose expansion state on every refresh because they lack a stable `id`.

**Decision:** Set `id` on `ProfileTreeItem` and let VS Code handle expansion persistence natively.

**Alternatives considered:**
- Manual `globalState` persistence with `onDidExpandElement`/`onDidCollapseElement`: Rejected because it duplicates VS Code's built-in mechanism, adds ~40-50 lines of code, and introduces state synchronization edge cases.
- `workspaceState` persistence: Rejected because profiles are user-level, not workspace-level.

**Consequences:**
- Positive: ~1 line of code, zero maintenance, battle-tested mechanism
- Negative: Slightly less explicit control; if profile identity changes, orphaned state (harmless)

### Why runtime metadata query via existing IMetadataService?

**Context:** Component types 10000+ show as "Unknown" because they're not in the hardcoded `ComponentTypeNames` dictionary. The hardcoded dictionary also has incorrect values for several standard types (e.g., maps `65 → FieldSecurityProfile` when the actual Dataverse value is `70`).

**Decision:** Query the `componenttype` global option set via the existing `IMetadataService.GetOptionSetAsync()`, cache in `SolutionService`, and correct the hardcoded fallback dictionary.

**Alternatives considered:**
- Expanding the hardcoded dictionary only: Rejected because 10000+ component type codes vary by environment version and installed solutions. A hardcoded mapping would be incomplete and drift over time.
- New standalone metadata method in `SolutionService`: Rejected because `IMetadataService` already implements `RetrieveOptionSetRequest` with proper error handling. Duplicating that infrastructure violates the architecture's service reuse principle.
- Client-side (extension) metadata query: Rejected because the daemon already has the authenticated connection and caching infrastructure.

**Consequences:**
- Positive: Resolves all component types for any environment, no maintenance burden, reuses existing service
- Negative: First call per environment adds latency for the metadata query (~200-500ms); adds `IMetadataService` as a dependency of `SolutionService`

### Why logicalName (DisplayName) format?

**Context:** Need a consistent display format for component names throughout the Solutions panel.

**Decision:** `logicalName (DisplayName)` — technical name first, human-readable in parentheses.

**Alternatives considered:**
- `DisplayName (logicalName)`: Rejected because PPDS is a developer tool. Developers write code using logical names — the technical name should be the primary visual anchor. Sorting by logical name also groups by publisher prefix, which is more useful.

**Consequences:**
- Positive: Copy-paste friendly for code, consistent with CLI/Data Explorer, useful sort order
- Negative: Less immediately readable for non-technical users (acceptable — they're not the audience)

### Why globalState over workspaceState for persistence?

**Context:** Need to persist sort order and managed toggle state.

**Decision:** Use `globalState` for both.

**Alternatives considered:**
- `workspaceState`: Rejected because profiles are user-level (global to the VS Code installation), not workspace-specific. A developer's profile ordering preference shouldn't change when they open a different folder.

**Consequences:**
- Positive: Consistent state across all workspaces
- Negative: Can't have different sort orders per workspace (not a real use case)

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `ppds.profiles.sortOrder` | `globalState` Record | No | `{}` | Profile ID to sort position mapping |
| `ppds.solutionsPanel.includeManaged` | `globalState` boolean | No | `false` | Whether managed solutions are shown by default |

These are `globalState` entries, not user-facing `settings.json` configuration.

---

## Related Specs

- [architecture.md](./architecture.md) — Application Services pattern for daemon business logic
- [per-panel-environment-scoping.md](./per-panel-environment-scoping.md) — Environment picker pattern used in Solutions panel

---

## Roadmap

- Resolve individual component objectIds to display names (requires per-type metadata queries — entity names, web resource names, etc.)
- Drag-and-drop profile reordering
- Solution comparison (diff two solutions' component lists)
- Column customization in the detail card
- Keyboard navigation in the Solutions panel tree view
