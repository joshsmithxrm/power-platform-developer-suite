# Solutions

**Status:** Draft
**Last Updated:** 2026-03-18
**Code:** [src/PPDS.Dataverse/Services/SolutionService.cs](../src/PPDS.Dataverse/Services/SolutionService.cs) | [src/PPDS.Extension/src/panels/SolutionsPanel.ts](../src/PPDS.Extension/src/panels/SolutionsPanel.ts)
**Surfaces:** CLI, TUI, Extension

---

## Overview

Solutions management across all PPDS surfaces — browsing, component inspection, export, import monitoring. The Solutions domain covers listing Dataverse solutions, resolving component types and names to human-readable labels, exporting solutions, and tracking import job progress. Surface-specific behavior (TUI screen layout, Extension webview panel, CLI commands) is defined in dedicated sections below.

### Goals

- **Solution Browsing**: List all solutions with filtering by name, publisher, and managed status across CLI, TUI, and Extension
- **Component Inspection**: View solution component breakdown by type with resolved names instead of raw GUIDs
- **Component Type Resolution**: Resolve all component types including the 10000+ range via runtime metadata queries
- **Export Workflow**: Trigger solution export with progress tracking and file output (TUI, CLI)
- **Import Monitoring**: Poll active import jobs and display real-time progress (TUI)

### Non-Goals

- Solution creation or deletion (use Power Platform admin center)
- Solution layering or patch management
- Component-level editing (navigate to dedicated screens for plugin registration, flows, etc.)
- Deployment settings generation (use CLI `ppds deploy settings`)
- Metadata browser panel (future spec — component name resolution warms the cache for it)
- Extension-side metadata caching (daemon is the single cache)
- Eager metadata preload on environment connect

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Application Services                         │
│                                                                  │
│  ┌──────────────────────┐     ┌──────────────────────┐          │
│  │   SolutionService     │────▶│ ComponentNameResolver │         │
│  │   - ListAsync         │     │ (type-to-table map)   │         │
│  │   - GetComponentsAsync│     └──────────┬───────────┘          │
│  │   - ExportAsync       │                │                      │
│  └──────────┬───────────┘   ┌─────────────┼─────────────┐       │
│             │               │             │             │       │
│             │         ┌─────┴─────┐ ┌─────┴─────┐ ┌────┴────┐  │
│             │         │ Metadata  │ │ Dataverse  │ │ Logger  │  │
│             │         │ Provider  │ │ Table      │ │ Timing  │  │
│             │         │ (cached)  │ │ Queries    │ │         │  │
│             │         └───────────┘ └───────────┘ └─────────┘  │
│             │                                                    │
│  ┌──────────┴───────────┐                                       │
│  │ IImportJobService     │                                      │
│  │ WaitForCompletionAsync│                                      │
│  └──────────────────────┘                                       │
│             │                                                    │
│  ┌──────────┴───────────┐                                       │
│  │ IDataverseConnectionPool                                     │
│  └──────────────────────┘                                       │
└─────────────────────────────────────────────────────────────────┘
         │                    │                    │
         ▼                    ▼                    ▼
┌─────────────┐    ┌──────────────────┐   ┌────────────────────┐
│  CLI         │    │  TUI              │   │  VS Code Extension │
│  ppds        │    │  SolutionScreen   │   │  SolutionsPanel    │
│  solutions   │    │  (DataTableView)  │   │  (webview)         │
│  list/export │    │                   │   │                    │
└─────────────┘    └──────────────────┘   └────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `SolutionService` | Orchestrates solution listing, component loading, name resolution, export |
| `ComponentNameResolver` | Maps component objectId GUIDs to names via type-specific table queries |
| `CachedMetadataProvider` | Provides entity names for type 1 components (existing, cached) |
| `IImportJobService` | Import job polling and progress reporting |
| `SolutionScreen` | TUI: solution table, component panel, export/import actions |
| `SolutionsPanel.ts` | Extension: webview panel for solution browsing and component inspection |

### Dependencies

- Depends on: [dataverse-services.md](./dataverse-services.md) for `ISolutionService`, `IImportJobService`, `IMetadataService`
- Depends on: [connection-pooling.md](./connection-pooling.md) for `IDataverseConnectionPool`
- Depends on: [tui.md](./tui.md) for `ITuiScreen`, `IHotkeyRegistry`, `ITuiErrorService`
- Uses patterns from: [architecture.md](./architecture.md) for Application Service boundary
- Depends on: [per-panel-environment-scoping.md](./per-panel-environment-scoping.md) for Extension environment context

---

## Specification

### Core Service Behavior

#### Solution Listing

1. `ISolutionService.ListAsync()` returns solutions with: Name, UniqueName, Version, Publisher, IsManaged, ModifiedOn, CreatedOn, InstalledOn, Description
2. All service calls use `IDataverseConnectionPool` — never create `ServiceClient` directly (Constitution D1)
3. Pooled clients disposed after each operation (Constitution D2)
4. `CancellationToken` threaded through entire async chain (Constitution R2)
5. Maximum 500 solutions per query (practical limit for table views)

#### Component Loading

1. `GetComponentsAsync(solutionId)` queries `solutioncomponent` table
2. Components grouped by type for display
3. Component counts may be expensive for large solutions; load on demand

#### Solution Export

1. `ExportAsync(uniqueName, managed)` runs on background thread
2. Accepts `IProgressReporter` for operations >1 second (Constitution A3)
3. Export can produce files >100MB; progress feedback is essential
4. Output defaults to `{uniqueName}_{managed/unmanaged}.zip`

### Component Name Resolution

`ComponentNameResolver` resolves component objectId GUIDs to human-readable names by querying type-specific Dataverse tables.

#### Interface

```csharp
public interface IComponentNameResolver
{
    Task<IReadOnlyDictionary<Guid, ComponentNames>> ResolveAsync(
        int componentType,
        IReadOnlyList<Guid> objectIds,
        CancellationToken cancellationToken = default);
}

public record ComponentNames(
    string? LogicalName,
    string? SchemaName,
    string? DisplayName);
```

#### Resolution Flow

1. **Load components**: `GetComponentsAsync` queries `solutioncomponent` table (existing)
2. **Group by type**: Group returned components by `componentType`
3. **Resolve names**: For each type with a mapping, call `ComponentNameResolver.ResolveAsync(type, objectIds)`
4. **Entity shortcut**: Type 1 uses `CachedMetadataProvider.GetEntitiesAsync()` (cached, no Dataverse call). Matches `solutioncomponent.objectId` against `EntitySummary.MetadataId`
5. **Table queries**: Other types batch-query their table: `WHERE <primarykey> IN (id1, id2, ...)`
6. **Merge**: Attach resolved names to `SolutionComponentInfo` records
7. **Log timing**: Log per-type and total resolution time: "Resolved {Count} {TypeName} names in {ElapsedMs}ms"

Type resolution is sequential (not parallel) to avoid connection pool exhaustion — each `ResolveAsync` call acquires a pooled client.

#### Component Type to Table Mapping

| Type Code | Type Name | Table | Name Fields |
|-----------|-----------|-------|-------------|
| 1 | Entity | `CachedMetadataProvider` | logicalName, schemaName, displayName |
| 26 | SavedQuery | `savedquery` | name |
| 29 | Workflow | `workflow` | name, uniquename |
| 60 | SystemForm | `systemform` | name |
| 61 | WebResource | `webresource` | name |
| 66 | CustomControl | `customcontrol` | name |
| 90 | PluginType | `plugintype` | name |
| 91 | PluginAssembly | `pluginassembly` | name |
| 92 | SDKMessageProcessingStep | `sdkmessageprocessingstep` | name |
| 300 | CanvasApp | `canvasapp` | name, displayname |
| 380 | EnvironmentVariableDefinition | `environmentvariabledefinition` | schemaname, displayname |
| 381 | EnvironmentVariableValue | `environmentvariablevalue` | schemaname |
| 371 | Connector | `connector` | name, displayname |
| 372 | Custom Connector | `connector` | name, displayname |

Note: Types 371 (Connector) and 372 (Custom Connector) are distinct component types in the `componenttype` enum (`Connector` and `Connector1` respectively) but both map to the same `connector` table.

For tables with a single `name` field, that value maps to `LogicalName` on `SolutionComponentInfo`. For tables with distinct schema/display fields, map accordingly.

#### Prerequisite: EntitySummary.MetadataId

`EntitySummary` currently lacks a `MetadataId` field. For entity-type solution components, `solutioncomponent.objectId` is the entity's `MetadataId` (a GUID). Without this field, there is no way to match objectIds to entities from the cached entity list.

```csharp
// Add to EntitySummary.cs
[JsonPropertyName("metadataId")]
public Guid MetadataId { get; init; }
```

Populate in `DataverseMetadataService.MapToEntitySummary`:
```csharp
MetadataId = e.MetadataId ?? Guid.Empty,
```

The SDK's `EntityMetadata.MetadataId` is already available in the `RetrieveAllEntitiesRequest` response — it's just not currently mapped.

#### Updated SolutionComponentInfo

Three nullable name fields are appended to the existing positional record. The existing `Id` field name is preserved.

```csharp
public record SolutionComponentInfo(
    Guid Id,
    Guid ObjectId,
    int ComponentType,
    string ComponentTypeName,
    int RootComponentBehavior,
    bool IsMetadata,
    string? DisplayName = null,
    string? LogicalName = null,
    string? SchemaName = null);
```

The new fields use default values so existing construction sites continue to compile. The `SolutionComponentInfoDto` class in `RpcMethodHandler.cs` also needs three new nullable string fields to carry names through to the JSON-RPC response.

#### Batch Constraints

- Max 100 IDs per IN clause — split into multiple queries if exceeded
- Component type must be non-negative integer (log warning, skip resolution for invalid)
- Resolved name null/empty allowed — falls through to next priority

#### Graceful Degradation

- Unmapped component types display objectId GUID as fallback
- Resolution failure for one type does not block other types
- `ComponentNameResolver` wraps failures in `PpdsException` with `ErrorCode` (Constitution D4), but `SolutionService` catches these per-type and degrades gracefully — the panel never breaks due to a name resolution failure
- No retry on failed queries — the next panel load will retry naturally

### Component Type Resolution

The `componenttype` option set metadata query resolves type codes (including the 10000+ range) to display names.

#### Core Requirements

1. `SolutionService` delegates to `IMetadataService.GetOptionSetAsync("componenttype")` to query the global option set metadata at runtime
2. Returns `Dictionary<int, string>` mapping all type codes to display names, including the 10000+ range
3. Results cached in-memory per environment URL in `SolutionService` (keyed by normalized, lowercased, trailing-slash-stripped URL)
4. Cache lifetime: duration of the daemon process (component types don't change during a session)
5. On failure, falls back to corrected hardcoded `ComponentTypeNames` dictionary
6. `GetComponentsAsync()` uses resolved names from cache instead of only the hardcoded dictionary

**Note:** The existing hardcoded `ComponentTypeNames` dictionary in `SolutionService.cs` has incorrect values across a significant portion of entries — the entire 65-98+ range is shifted relative to the generated `componenttype` enum. The runtime metadata query supersedes the hardcoded dictionary, but the hardcoded values must also be corrected to match the generated enum to ensure the fallback path is accurate.

#### Resolution Flow

1. **First `solutions/components` call for an environment** triggers metadata query
2. **`IMetadataService.GetOptionSetAsync("componenttype")`** returns option set metadata with all values
3. **Parse** option values into `Dictionary<int, string>` and **cache** in a `ConcurrentDictionary<string, Dictionary<int, string>>` keyed by environment URL
4. **Subsequent calls** for the same environment use cached mapping
5. **Component type name resolution** in `GetComponentsAsync()` checks runtime cache first, then corrected hardcoded dictionary, then falls back to `Unknown ({type})`

#### Component Type Bug Fix

1. `client.ConnectedOrgUniqueName` (line 311) may return null — use the `environmentUrl` parameter already passed to the RPC handler
2. The bare `catch` at `GetComponentsAsync` line 319 swallows exceptions from `GetComponentTypeNamesAsync`. Replace with `catch (Exception ex)` and log the exception
3. `GetComponentTypeNamesAsync` itself (line 428) already has proper `catch (Exception ex)` with `_logger.LogWarning` — the fix is specifically the outer catch

### Extension Surface

The Solutions panel is a VS Code webview providing solution browsing and component inspection.

#### Detail Card

1. When a solution is expanded, a styled detail card appears above the component groups
2. Detail card fields: Unique Name, Publisher, Type (Managed/Unmanaged), Installed date, Modified date, Description
3. Dates formatted as locale-appropriate short dates
4. Description truncated to 3 lines with "..." if longer
5. Styled with `var(--vscode-textBlockQuote-background)` background, subtle left border using `var(--vscode-textBlockQuote-border)`, label-value pairs in CSS grid

**Data flow change:** `SolutionsPanel.loadSolutions()` must include `createdOn`, `modifiedOn`, `installedOn` in the `solutionsLoaded` webview message payload (currently dropped at lines ~162-174; description is already included).

#### Component Detail Card

1. Clicking a component item expands an inline detail card below it
2. Single expand — clicking another component collapses the previously expanded one
3. Detail card fields: Logical Name, Schema Name, Display Name, Object ID (with copy button), Root Behavior, Metadata flag
4. Copy button uses `navigator.clipboard.writeText()` for Object ID; briefly swaps to checkmark on success
5. Keyboard: component items are focusable (`tabindex="0"`), Enter/Space toggles the detail card

#### Search/Filter

1. Text input field in the toolbar, between the Managed button and the environment picker
2. Client-side filtering — no daemon round-trip
3. Filters on: friendly name, unique name, publisher name (case-insensitive contains)
4. Debounced at 150ms on input
5. Status bar shows filtered count: "5 of 23 solutions" when filtered, "23 solutions" when unfiltered
6. Empty state: "No solutions match filter" with italicized styling
7. Filter clears when solutions reload (refresh or environment switch)

#### Managed Toggle Persistence

1. On panel creation, read `context.globalState.get<boolean>('ppds.solutionsPanel.includeManaged')` to restore toggle state
2. On toggle, write the new value to `globalState`
3. Panel initializes with the persisted value (or `false` if not set)
4. The `globalState` key is shared across all Solutions panel instances

#### Display Name Format

1. Component type group headers: display the resolved type name as-is (e.g., `Entity`, `WebResource`, `CanvasApp`)
2. Individual component items: show `logicalName (DisplayName)` when both available, otherwise first available name, otherwise objectId GUID
3. Display priority: logicalName > schemaName > displayName > objectId
4. All name fields go through `escapeHtml()` before rendering (Constitution S1)

#### Host-Side Changes

The extension host `SolutionsPanel.ts` must pass the three new name fields from `SolutionComponentInfoDto` through to the webview in the `componentsLoaded` message. Currently only `objectId` and `isMetadata` are forwarded — add `logicalName`, `schemaName`, and `displayName`.

#### Known Issue

`SolutionsPanel.loadSolutions()` makes a redundant second RPC call when `includeManaged` is false solely to count managed solutions. This should be addressed: either have the daemon return `totalCount` alongside the filtered list, or accept the double-call as a known cost.

### TUI Surface

The Solutions screen provides an interactive browser for Dataverse solutions in the TUI.

#### Screen Design

```
┌─────────────────────────────────────────────────────────────────┐
│                        TuiShell                                  │
│    Tools > Solutions  (replaces "Coming Soon" placeholder)       │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                     SolutionScreen                                │
│           (ITuiScreen + ITuiStateCapture)                        │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │ DataTableView: Solutions list                              │   │
│  │   Name | UniqueName | Version | Publisher | Managed | Mod  │   │
│  ├──────────────────────────────────────────────────────────┤   │
│  │ Component Panel (on Enter):                                │   │
│  │   Plugin Assemblies: 3  | Flows: 12  | Env Vars: 5        │   │
│  │   Connection Refs: 2   | Web Resources: 45  | ...          │   │
│  ├──────────────────────────────────────────────────────────┤   │
│  │ Status: 24 solutions | Selected: MySolution v1.2.0         │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

#### Core Requirements

1. Screen implements `ITuiScreen` and `ITuiStateCapture<SolutionScreenState>`
2. Solution list displays in `DataTableView` with columns: Name, UniqueName, Version, Publisher, IsManaged, ModifiedOn
3. Enter on a solution row shows component breakdown in a detail panel below the table
4. Export action triggers `ISolutionService.ExportAsync()` with progress feedback
5. Import monitor shows active import job progress using `IImportJobService.WaitForCompletionAsync()`
6. Filter bar for searching solutions by name
7. All service calls run on background thread; UI updates via `Application.MainLoop.Invoke()`

#### Flows

**Browse Solutions:**

1. **Screen opens**: Load solutions via `ISolutionService.ListAsync()`
2. **Filter**: Type in filter bar to search by solution name (debounced)
3. **Toggle managed**: Checkbox or hotkey to include/exclude managed solutions
4. **Select solution**: Arrow keys navigate table; status line shows selected solution details
5. **Refresh**: F5 reloads solution list

**View Solution Components:**

1. **Select solution**: Navigate to solution row
2. **Open components**: Enter loads components via `ISolutionService.GetComponentsAsync(solutionId)`
3. **Display**: Component panel shows grouped counts by component type
4. **Detail table**: Optional second DataTableView listing individual components (name, type, managed)
5. **Navigate**: Future integration point — select a component to navigate to its dedicated screen

**Export Solution:**

1. **Select solution**: Navigate to solution row
2. **Trigger export**: Ctrl+E or menu item
3. **Choose type**: Dialog asks managed/unmanaged export
4. **Export**: `ISolutionService.ExportAsync(uniqueName, managed)` runs on background thread
5. **Save**: File save dialog for output path (defaults to `{uniqueName}_{managed/unmanaged}.zip`)
6. **Progress**: Status line shows "Exporting..." with spinner
7. **Complete**: Status line shows "Exported to {path}" with file size

**Monitor Import:**

1. **Trigger**: Ctrl+I or menu item to check for active imports
2. **List jobs**: `IImportJobService.ListAsync()` shows recent import jobs
3. **Poll active**: If active job found, poll via `WaitForCompletionAsync` with onProgress callback
4. **Display**: Progress bar shows import percentage; status line shows current phase
5. **Complete**: Status line shows final result; refresh solution list to see imported solution

#### State Capture

```csharp
public sealed record SolutionScreenState(
    int SolutionCount,
    string? SelectedSolutionName,
    string? SelectedSolutionVersion,
    bool? SelectedIsManaged,
    int? ComponentCount,
    bool IsLoading,
    bool IsExporting,
    bool IsMonitoringImport,
    double? ImportProgress,
    bool ShowManaged,
    string? FilterText,
    string? ErrorMessage);
```

#### TUI Constraints

- Import monitoring is read-only — this screen does not trigger imports (use CLI or admin center)
- Component counts may be expensive for large solutions; load on demand
- Maximum 500 solutions per query (practical limit for DataTableView)
- Solution export can produce files >100MB; status line feedback is essential

#### TUI Configuration

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| ShowManaged | bool | true | Include managed solutions in list |
| ImportPollInterval | TimeSpan | 5s | Import job polling interval |

---

## Acceptance Criteria

### Core Service

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Entity-type (1) components display logicalName from cached metadata | `ComponentNameResolverTests.ResolveAsync_EntityType_UsesMetadataProvider` | |
| AC-02 | WebResource components display name field from webresource table | `ComponentNameResolverTests.ResolveAsync_WebResource_QueriesTable` | |
| AC-03 | Unmapped component types display objectId GUID as fallback | `ComponentNameResolverTests.ResolveAsync_UnmappedType_ReturnsNull` | |
| AC-04 | Component types in 10000+ range display resolved labels, not "Unknown (N)" | `SolutionServiceTests.GetComponentsAsync_HighRangeTypes_Resolved` | |
| AC-05 | Component name resolution logs per-type timing | `ComponentNameResolverTests.ResolveAsync_LogsTiming` | |
| AC-06 | Batch queries split at 100 IDs to avoid query length limits | `ComponentNameResolverTests.ResolveAsync_LargeBatch_Splits` | |
| AC-07 | Name resolution failure for one type does not block other types | `ComponentNameResolverTests.ResolveAsync_PartialFailure_ContinuesOtherTypes` | |
| AC-08 | EntitySummary includes MetadataId populated from RetrieveAllEntitiesRequest | `DataverseMetadataServiceTests.GetEntitiesAsync_IncludesMetadataId` | |
| AC-09 | Component type metadata cached per environment URL | `SolutionServiceTests.cs` | |
| AC-10 | Component type resolution falls back to hardcoded dictionary on metadata query failure | `SolutionServiceTests.cs` | |
| AC-11 | Hardcoded `ComponentTypeNames` dictionary values corrected to match generated enum | `SolutionServiceTests.cs` | |
| AC-12 | Daemon serve mode emits Information-level logs to stderr | `ServeCommandTests.ServeMode_EmitsInfoLogs` | |

### Extension Surface

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-13 | Solution detail card shows Unique Name, Publisher, Type, Installed, Modified, Description | Manual verification | |
| AC-14 | SolutionsPanel webview message includes createdOn, modifiedOn, installedOn from existing RPC data | `SolutionsPanel` manual verification | |
| AC-15 | Webview displays names with priority: logicalName > schemaName > displayName > objectId | `ext:test SolutionsPanel component name priority` | |
| AC-16 | Clicking a component expands an inline detail card; clicking another collapses the first | `ext:test SolutionsPanel detail card toggle` | |
| AC-17 | Detail card shows Object ID with copy button | `ext:test SolutionsPanel detail card copy` | |
| AC-18 | All resolved name strings are escaped via escapeHtml before innerHTML | `ext:test SolutionsPanel escapes names` | |
| AC-19 | Enter/Space on a focused component item toggles the detail card | `ext:test SolutionsPanel detail card keyboard` | |
| AC-20 | Search input filters solution list by friendly name, unique name, and publisher | Manual verification | |
| AC-21 | Search shows "5 of 23 solutions" count in status bar when active | Manual verification | |
| AC-22 | Managed toggle state persists across panel close/reopen via globalState | Manual verification | |
| AC-23 | Component type group headers show resolved type names for 10000+ range | Manual verification | |

### TUI Surface

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-24 | Screen loads solutions from ISolutionService.ListAsync on activation | TuiUnit test | |
| AC-25 | Filter bar searches solutions by name (debounced) | TuiUnit test | |
| AC-26 | Managed toggle includes/excludes managed solutions | TuiUnit test | |
| AC-27 | Enter on solution loads component breakdown via GetComponentsAsync | TuiUnit test | |
| AC-28 | Ctrl+E triggers export with managed/unmanaged choice | TuiUnit test | |
| AC-29 | Export saves .zip file to selected path | TuiUnit test | |
| AC-30 | Import monitor polls active jobs with progress callback | TuiUnit test | |
| AC-31 | Status line shows solution count and selected solution info | TuiUnit test | |
| AC-32 | State capture returns accurate SolutionScreenState | TuiUnit test | |
| AC-33 | F5 refreshes solution list | TuiUnit test | |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| No solutions in environment | Empty table/panel with "No solutions found" message |
| Solution with 0 components | Component panel shows "No components" |
| Very large solution (>100MB) | Export shows progress; no timeout on export itself |
| Multiple active imports | Show most recent import; list view for all |
| Managed solution selected | Export button still available; unmanaged export option hidden |
| Component type has no mapping | Display GUID, no error |
| Entity not found in metadata cache | Display GUID |
| Batch > 100 components of one type | Two queries: 100 + 50 |
| Name resolution query fails | Log warning, display GUIDs for that type, other types unaffected |
| Component name is empty string | Display GUID (treat empty as absent) |
| All name fields null | Display objectId GUID |
| Metadata service unavailable | Fall back to hardcoded dictionary, log warning |
| Filter matches none | Empty table/panel, status "0 solutions matching filter" |
| Export cancelled | No export, return to table |
| No active imports | "No active import jobs" message |
| Solution has no description | Detail card omits description row |
| All solutions are managed | Managed toggle Off shows empty state with "N managed hidden" in status bar |

---

## API/Contracts

### RPC: solutions/list

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

### RPC: solutions/components

```json
{
  "components": [
    {
      "id": "...",
      "objectId": "d8f9e4c2-3b1a-4e8c-9f7d-2c1a5e8b9f4d",
      "componentType": 61,
      "componentTypeName": "WebResource",
      "rootComponentBehavior": 0,
      "isMetadata": false,
      "logicalName": "new_scripts/account_form.js",
      "schemaName": null,
      "displayName": null
    },
    {
      "id": "...",
      "objectId": "a1b2c3d4-...",
      "componentType": 1,
      "componentTypeName": "Entity",
      "rootComponentBehavior": 0,
      "isMetadata": true,
      "logicalName": "account",
      "schemaName": "Account",
      "displayName": "Account"
    }
  ]
}
```

No new RPC methods. New fields are nullable and additive — no breaking changes.

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Service unavailable | No connection to environment | Status line/panel error; retry on refresh |
| Authentication expired | Token expired during operation | Re-authentication dialog; retry operation |
| Export failed | Solution too large or insufficient privileges | Error dialog with Dataverse error details |
| Import timeout | Import job exceeds 30-minute timeout | Status line warning; manual check suggested |
| Solution not found | Solution deleted between list and export | Refresh list; show warning |
| Table query failure | Network error, auth failure, table not found | Log warning, return empty dict for that type, other types unaffected |
| Metadata provider failure | CachedMetadataProvider throws | Log warning, entity-type components fall back to GUID |
| Option set query failure | GetOptionSetAsync throws for componenttype | Log warning, fall back to hardcoded dictionary |
| Batch too large | > 100 objectIds for one type | Split into sub-batches automatically |

### Recovery Strategies

- **Per-type isolation**: Each component type resolves independently. Failure in one type does not affect others.
- **Graceful degradation**: Any resolution failure results in GUID display — the panel never breaks, just shows less information.
- **Connection errors**: Status line error with retry hint (F5 in TUI, refresh in Extension)
- **Auth errors**: Re-authentication dialog, retry pending operation
- **Export errors**: Full error dialog with Dataverse error details
- **Import stall**: Option to stop monitoring; suggest checking admin center
- **No retry**: Failed name resolution queries are not retried. The next load retries naturally.

---

## Extension Points

### Adding a New Component Type Mapping

1. **Add entry**: Add the component type code, table name, and name field(s) to the mapping dictionary in `ComponentNameResolver`
2. **Test**: Add a test case to `ComponentNameResolverTests` for the new type
3. **No registration needed**: The resolver auto-discovers mappings from its internal dictionary

---

## Design Decisions

### Why DataTableView (TUI) instead of TreeView?

**Context:** Solutions contain components, which could be shown as a tree (Solution > Component Type > Components). However, the primary action is browsing solutions, not drilling into component hierarchies.

**Decision:** Use DataTableView for solutions list with an expandable component panel below. Component drill-down navigates to dedicated screens.

**Alternatives considered:**
- TreeView (Solution > Components): Rejected — solution list is the primary view; tree adds unnecessary depth
- Separate component screen: Possible but adds navigation friction for a quick overview

**Consequences:**
- Positive: Familiar table UI, consistent with SqlQueryScreen
- Positive: Component panel provides overview without losing solution list context
- Negative: Cannot see multiple solutions' components simultaneously

### Why read-only import monitoring (TUI)?

**Context:** Import could be triggered from the TUI, but solution import requires a .zip file and has complex options (overwrite, publish workflows, etc.).

**Decision:** Monitor-only. Import is triggered via CLI or admin center. The TUI shows progress of active imports.

**Alternatives considered:**
- Full import workflow with file picker: Rejected — complex UX for an infrequent operation
- No import monitoring: Rejected — users want to see import progress in-context

**Consequences:**
- Positive: Simpler screen; import complexity stays in CLI
- Negative: Users must use CLI or admin center to start imports

### Why daemon-centric caching (no extension-side cache)?

**Context:** Metadata is needed by the solution panel now and the metadata browser later. Where should the cache live?

**Decision:** All metadata caching stays in the daemon's existing `CachedMetadataProvider`. The extension calls RPC endpoints with no client-side caching.

**Alternatives considered:**
- Extension-host TypeScript cache: Rejected — creates two caches with synchronization/invalidation complexity for marginal benefit. RPC is local IPC (sub-millisecond).
- Eager preload on connect: Rejected — slows initial connection, fetches data that may never be needed.

**Consequences:**
- Positive: Single source of truth, no stale-data bugs, existing infrastructure
- Negative: Each panel load incurs RPC round-trips, but these hit the daemon cache and are negligible

### Why no additional caching for component name resolution?

**Context:** Should resolved component names be cached?

**Decision:** No separate cache. Queries are simple IN-clause lookups on indexed primary keys. Entity-type names are already cached via `CachedMetadataProvider`.

**Alternatives considered:**
- Per-solution name cache with TTL: Rejected — adds invalidation complexity for component renames, and the queries are fast enough without caching.

**Consequences:**
- Positive: No stale names, no cache invalidation logic, simpler code
- Negative: Re-queries on each panel load (~5-10 small queries). Acceptable for correctness.

### Why click-to-expand instead of hover tooltip or side panel?

**Context:** How should component details be displayed in the Extension?

**Decision:** Click-to-expand inline detail card, consistent with existing solution card pattern.

**Alternatives considered:**
- Hover tooltip: Rejected — can't copy text, not keyboard accessible, disappears on mouse-out.
- Side panel / master-detail: Rejected — overkill for 4-6 fields. Save master-detail for the metadata browser.

**Consequences:**
- Positive: Consistent UX pattern, copiable text, keyboard accessible, simple implementation
- Negative: Expands the list vertically (mitigated by single-expand behavior)

### Why runtime metadata query via existing IMetadataService?

**Context:** Component types 10000+ show as "Unknown" because they're not in the hardcoded dictionary. The hardcoded dictionary also has incorrect values for several standard types.

**Decision:** Query the `componenttype` global option set via `IMetadataService.GetOptionSetAsync()`, cache in `SolutionService`, and correct the hardcoded fallback dictionary.

**Alternatives considered:**
- Expanding the hardcoded dictionary only: Rejected — 10000+ codes vary by environment version and installed solutions.
- New standalone metadata method: Rejected — `IMetadataService` already implements `RetrieveOptionSetRequest`.
- Client-side (extension) metadata query: Rejected — daemon already has authenticated connection and caching.

**Consequences:**
- Positive: Resolves all component types for any environment, no maintenance burden, reuses existing service
- Negative: First call per environment adds ~200-500ms latency for metadata query

### Why logicalName (DisplayName) format?

**Context:** Need a consistent display format for component names throughout the Solutions panel.

**Decision:** `logicalName (DisplayName)` — technical name first, human-readable in parentheses.

**Alternatives considered:**
- `DisplayName (logicalName)`: Rejected — PPDS is a developer tool. Developers write code using logical names. Sorting by logical name groups by publisher prefix.

**Consequences:**
- Positive: Copy-paste friendly for code, consistent with CLI/Data Explorer, useful sort order
- Negative: Less immediately readable for non-technical users (acceptable — not the audience)

---

## Related Specs

- [dataverse-services.md](./dataverse-services.md) — ISolutionService, IImportJobService, IMetadataService
- [connection-pooling.md](./connection-pooling.md) — Connection pool used for batch queries
- [tui.md](./tui.md) — TUI framework: ITuiScreen, DataTableView, state capture
- [plugins.md](./plugins.md) — Navigate from solution component to plugin registrations
- [environment-dashboard.md](./environment-dashboard.md) — Navigate from solution component to env vars, flows
- [architecture.md](./architecture.md) — Application Service boundary pattern
- [per-panel-environment-scoping.md](./per-panel-environment-scoping.md) — Environment picker pattern for Extension

---

## Roadmap

- Metadata browser panel — will use the same `CachedMetadataProvider` and `schema/*` RPC endpoints
- Component name resolution for additional types as users report gaps
- Open-in-maker-portal action from component detail card
- Component-level navigation: select a plugin assembly to open PluginRegistrationScreen filtered to it
- Solution comparison: diff two solutions' component lists
- Solution publisher management
- Solution history timeline
- Drag-and-drop profile reordering (Extension)
- Column customization in the detail card (Extension)

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-18 | Created from tui-solutions.md, solution-component-names.md, and solutions content from vscode-persistence-and-solutions-polish.md per SL1/SL3 |
