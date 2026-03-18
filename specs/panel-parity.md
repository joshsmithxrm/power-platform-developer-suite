# Panel Parity

**Status:** Implemented (Phases 1–2), In Progress (Phase 3 Polish)
**Version:** 2.0
**Last Updated:** 2026-03-18
**Code:** Multiple — see per-panel sections

---

## Overview

Achieve feature parity with the legacy VS Code extension across all four PPDS surfaces: Daemon RPC, VS Code extension, TUI, and MCP. Six panels — Import Jobs, Connection References, Environment Variables, Plugin Traces, Metadata Browser, and Web Resources — each implemented with consistent patterns established by a pattern-setting first panel.

### Goals

- **Feature parity:** Match legacy extension's 8 panels (6 remaining after Data Explorer + Solutions)
- **Multi-surface consistency:** Same data and operations available via VS Code, TUI, MCP, and CLI
- **Environment theming:** Per-panel environment scoping with color-coded toolbar accents on all webview panels
- **Connection health visibility:** Power Platform Connections API integration for Connection References panel

### Non-Goals

- Plugin Registration (deferred to post-parity, separate spec)
- Connection Picker / rebinding (deferred — tracked as separate issue)
- Bulk operations UI (service infrastructure exists but UI deferred)
- Notebooks (already implemented in MVP branch)
- Solution History expansion (Import Jobs covers import operations; export/publish/uninstall tracking deferred)

---

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                   UI Surfaces (thin)                  │
│  ┌───────────┐  ┌─────────┐  ┌──────┐  ┌─────────┐ │
│  │  VS Code  │  │   TUI   │  │ MCP  │  │   CLI   │ │
│  │  Webview  │  │ Screen  │  │ Tool │  │ Command │ │
│  └─────┬─────┘  └────┬────┘  └──┬───┘  └────┬────┘ │
│        │              │          │            │       │
│   JSON-RPC        Direct     Direct       Direct     │
│        │              │          │            │       │
│  ┌─────▼─────┐       │          │            │       │
│  │  Daemon   │       │          │            │       │
│  │  (serve)  │       │          │            │       │
│  └─────┬─────┘       │          │            │       │
│        │              │          │            │       │
│  ┌─────▼──────────────▼──────────▼────────────▼────┐ │
│  │         Application / Domain Services            │ │
│  │  (PPDS.Dataverse/Services, PPDS.Cli/Services)   │ │
│  └─────────────────────┬───────────────────────────┘ │
│                        │                              │
│  ┌─────────────────────▼───────────────────────────┐ │
│  │         IDataverseConnectionPool                 │ │
│  │         IPowerPlatformTokenProvider              │ │
│  └──────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘
```

VS Code panels communicate through the daemon (JSON-RPC over stdio). TUI, MCP, and CLI call services directly. All surfaces get the same data from the same service methods (Constitution A1, A2).

### Components

| Component | Responsibility |
|-----------|----------------|
| RpcMethodHandler | JSON-RPC endpoint handlers — parameter mapping, service calls, DTO mapping |
| WebviewPanelBase | VS Code webview panel base class — lifecycle, message protocol, environment scoping |
| TuiScreenBase | TUI screen base class — layout, hotkeys, data loading |
| Application Services | Business logic — single code path for all surfaces |
| Domain Services | Dataverse data access — queries, mutations, metadata |

### Shared Patterns (All Panels)

| Pattern | Description |
|---------|-------------|
| Virtual table | `DataTable` component — sortable columns, row selection, keyboard nav (`data-table.ts`) |
| Environment picker | Toolbar dropdown with environment theming via `data-env-type` / `data-env-color` CSS attributes |
| Solution filter | `SolutionFilter` component — dropdown with `storageKey`-based persistence (`solution-filter.ts`) |
| Panel lifecycle | `WebviewPanelBase.initializePanel()` — auth, environment resolution, title update, initial data load |
| Environment switching | `WebviewPanelBase.handleEnvironmentPickerClick()` — picker, state update, reload |
| Environment ID resolution | `WebviewPanelBase.resolveEnvironmentId()` — maps environment URL to GUID for Maker Portal links |
| Maker URL construction | `buildMakerUrl(environmentId)` from `browserCommands.ts` — all panels use this, never inline URL construction |
| RPC method convention | `{domain}/{operation}` with typed request/response DTOs |
| MCP tool convention | `ppds_{domain}_{operation}` with structured input/output |
| TUI hotkey convention | Ctrl+R (refresh), Enter (detail), Ctrl+O (open in Maker), Ctrl+F (filter) |

### Dependencies

- Depends on: [architecture.md](./architecture.md), [connection-pooling.md](./connection-pooling.md)
- Uses patterns from: [CONSTITUTION.md](./CONSTITUTION.md) — A1, A2, A3, D1, D4, I3, R2, S1

---

## Panel 1: Import Jobs

### Purpose

View solution import history for an environment. Shows import status, progress, duration, and allows drilling into the XML import log for troubleshooting failed imports.

### Infrastructure Readiness

| Layer | Status | Location |
|-------|--------|----------|
| Domain Service | ✅ Ready | `IImportJobService` — ListAsync, GetAsync, GetDataAsync |
| Entity Class | ✅ Ready | `importjob.cs` |
| CLI Commands | ✅ Ready | Existing commands |
| RPC | ✅ Implemented | `importJobs/list`, `importJobs/get` |
| VS Code Panel | ✅ Implemented | `ImportJobsPanel.ts` |
| TUI Screen | ✅ Implemented | `ImportJobsScreen.cs` |
| MCP Tools | ✅ Implemented | `ImportJobsListTool.cs`, `ImportJobsGetTool.cs` |

### RPC Endpoints

| Method | Request | Response |
|--------|---------|----------|
| `importJobs/list` | `{ environmentUrl?: string }` | `{ jobs: ImportJobInfo[] }` |
| `importJobs/get` | `{ id: string, environmentUrl?: string }` | `{ job: ImportJobDetail }` |

**ImportJobInfo fields:** id, solutionName, status, progress (%), createdBy, createdOn, startedOn, completedOn, duration (formatted)

**ImportJobDetail fields:** all of ImportJobInfo + data (XML import log)

### VS Code Panel

- **viewType:** `ppds.importJobs`
- **Layout:** Three-zone (toolbar, virtual table, status bar) per @webview-panels pattern
- **Table columns:** Solution Name, Status (color-coded), Progress, Created By, Created On, Duration
- **Default sort:** createdOn descending
- **Actions:** Refresh (Ctrl+R), click row for import log detail, Open in Maker (opens solutionsHistory URL), environment picker with theming

### TUI Screen

- **Class:** `ImportJobsScreen` extending `TuiScreenBase`
- **Layout:** Data table with status bar
- **Hotkeys:** Ctrl+R (refresh), Enter (detail dialog), Ctrl+O (open in Maker)
- **Dialog:** `ImportJobDetailDialog` — scrollable XML import log

### MCP Tools

| Tool | Input | Output |
|------|-------|--------|
| `ppds_import_jobs_list` | `{ environmentUrl?: string }` | Import jobs with status/progress |
| `ppds_import_jobs_get` | `{ id: string }` | Full detail including import log XML |

---

## Panel 2: Connection References

### Purpose

View connection references in an environment, see which Power Platform connections they're bound to (with health status via Connections API), understand flow dependencies, and detect orphaned references.

### Infrastructure Readiness

| Layer | Status | Location |
|-------|--------|----------|
| Domain Service | ✅ Ready | `IConnectionReferenceService` — ListAsync, GetAsync, GetFlowsUsingAsync, AnalyzeAsync |
| Connection Service | ✅ Ready | `IConnectionService` (Power Platform API) — ListAsync, GetAsync |
| Flow Service | ✅ Ready | `IFlowService` — ListAsync |
| Entity Class | ✅ Ready | `connectionreference.cs` |
| CLI Commands | ✅ Ready | `ppds connectionreferences list/get/flows/connections/analyze` |
| RPC | ✅ Implemented | `connectionReferences/list`, `get`, `analyze`, `connections/list` |
| VS Code Panel | ✅ Implemented | `ConnectionReferencesPanel.ts` |
| TUI Screen | ✅ Implemented | `ConnectionReferencesScreen.cs` |
| MCP Tools | ✅ Implemented | `ConnectionReferencesListTool.cs`, `GetTool`, `AnalyzeTool` |

### RPC Endpoints

| Method | Request | Response |
|--------|---------|----------|
| `connectionReferences/list` | `{ solutionId?, environmentUrl? }` | `{ references: ConnectionReferenceInfo[] }` |
| `connectionReferences/get` | `{ logicalName, environmentUrl? }` | `{ reference: ConnectionReferenceDetail, flows: FlowInfo[], connection?: ConnectionInfo }` |
| `connectionReferences/analyze` | `{ environmentUrl? }` | `{ orphanedReferences: [], orphanedFlows: [] }` |
| `connections/list` | `{ connectorId?, environmentUrl? }` | `{ connections: ConnectionInfo[] }` |

**ConnectionReferenceInfo fields:** logicalName, displayName, connectorId, connectionId, isManaged, modifiedOn, connectionStatus (Connected/Error/Unknown), connectorDisplayName

**Note:** `connections/list` uses Power Platform API (`service.powerapps.com`), not Dataverse SDK. Requires `IPowerPlatformTokenProvider`. SPN auth has limited access — graceful degradation: panel loads without connection status, shows "N/A" instead.

### VS Code Panel

- **viewType:** `ppds.connectionReferences`
- **Layout:** Three-zone with virtual table + detail pane on row click
- **Table columns:** Display Name, Logical Name, Connector, Connection Status (color-coded), Managed, Modified On
- **Default sort:** logicalName ascending
- **Solution filter:** Dropdown in toolbar (persisted across sessions)
- **Detail view (on row click):** Connection details (status, owner, shared/personal), dependent flows (clickable), orphan indicator
- **Actions:** Refresh (Ctrl+R), solution filter, analyze (orphan detection), Open in Maker, environment picker with theming
- **Graceful degradation:** SPN auth — connection status column shows "N/A", panel otherwise fully functional

### TUI Screen

- **Class:** `ConnectionReferencesScreen` extending `TuiScreenBase`
- **Hotkeys:** Ctrl+R (refresh), Enter (detail dialog), Ctrl+A (analyze), Ctrl+F (filter by solution), Ctrl+O (open in Maker)
- **Dialogs:** `ConnectionReferenceDetailDialog`, `OrphanAnalysisDialog`

### MCP Tools

| Tool | Input | Output |
|------|-------|--------|
| `ppds_connection_references_list` | `{ solutionId?, environmentUrl? }` | List with connection status |
| `ppds_connection_references_get` | `{ logicalName }` | Full detail with flows and connection info |
| `ppds_connection_references_analyze` | `{ environmentUrl? }` | Orphaned references and flows |

---

## Panel 3: Environment Variables

### Purpose

View environment variable definitions and their current values. Supports viewing default vs current values, updating values, and filtering by solution. Key tool for deployment troubleshooting.

### Infrastructure Readiness

| Layer | Status | Location |
|-------|--------|----------|
| Domain Service | ✅ Ready | `IEnvironmentVariableService` — ListAsync, GetAsync, SetValueAsync, ExportAsync |
| Entity Classes | ✅ Ready | `environmentvariabledefinition.cs`, `environmentvariablevalue.cs` |
| CLI Commands | ✅ Ready | `ppds environmentvariables list/get/set/export/url` |
| RPC | ✅ Implemented | `environmentVariables/list`, `get`, `set` |
| VS Code Panel | ✅ Implemented | `EnvironmentVariablesPanel.ts` |
| TUI Screen | ✅ Implemented | `EnvironmentVariablesScreen.cs` |
| MCP Tools | ✅ Implemented | `EnvironmentVariablesListTool.cs`, `GetTool`, `SetTool` |

### RPC Endpoints

| Method | Request | Response |
|--------|---------|----------|
| `environmentVariables/list` | `{ solutionId?, environmentUrl? }` | `{ variables: EnvironmentVariableInfo[] }` |
| `environmentVariables/get` | `{ schemaName, environmentUrl? }` | `{ variable: EnvironmentVariableDetail }` |
| `environmentVariables/set` | `{ schemaName, value, environmentUrl? }` | `{ success: boolean }` |

**EnvironmentVariableInfo fields:** schemaName, displayName, type (String/Number/Boolean/JSON/DataSource), defaultValue, currentValue, isManaged, isRequired, description, modifiedOn

**Note:** List is two Dataverse queries (definitions + values) joined client-side by definition ID.

### VS Code Panel

- **viewType:** `ppds.environmentVariables`
- **Layout:** Three-zone with virtual table + inline edit capability
- **Table columns:** Schema Name, Display Name, Type, Default Value, Current Value, Managed, Modified On
- **Default sort:** schemaName ascending
- **Solution filter:** Dropdown in toolbar (persisted across sessions)
- **Visual indicators:** Override highlight (current differs from default), missing value warning (required + no value + no default)
- **Edit flow:** Edit action, type-aware input validation (boolean toggle, numeric validation, JSON syntax validation), calls `environmentVariables/set`, refreshes row
- **Actions:** Refresh, solution filter, edit value, export deployment settings (`.deploymentsettings.json`), Open in Maker, environment picker with theming

### TUI Screen

- **Class:** `EnvironmentVariablesScreen` extending `TuiScreenBase`
- **Hotkeys:** Ctrl+R (refresh), Enter (detail/edit dialog), Ctrl+E (export deployment settings), Ctrl+F (filter by solution), Ctrl+O (open in Maker)
- **Dialogs:** `EnvironmentVariableDetailDialog` — type-aware edit with validation

### MCP Tools

| Tool | Input | Output |
|------|-------|--------|
| `ppds_environment_variables_list` | `{ solutionId?, environmentUrl? }` | List with current vs default values |
| `ppds_environment_variables_get` | `{ schemaName }` | Full detail including description and type |
| `ppds_environment_variables_set` | `{ schemaName, value }` | Set value — AI agents can fix misconfigurations |

---

## Panel 4: Plugin Traces

### Purpose

View, filter, and analyze plugin execution trace logs. Multi-pane layout with rich filtering, timeline visualization, trace level management, and batch delete. The primary plugin debugging tool.

### Infrastructure Readiness

| Layer | Status | Location |
|-------|--------|----------|
| Domain Service | ✅ Ready | `IPluginTraceService` — ListAsync, GetAsync, GetRelatedAsync, BuildTimelineAsync, DeleteAsync, DeleteByIdsAsync, DeleteByFilterAsync, DeleteOlderThanAsync |
| Entity Class | ✅ Ready | `plugintracelog.cs` |
| RPC | ✅ Implemented | `pluginTraces/list`, `get`, `timeline`, `delete`, `traceLevel`, `setTraceLevel` |
| VS Code Panel | ✅ Implemented | `PluginTracesPanel.ts` — filter bar, split pane, 5-tab detail, auto-refresh |
| TUI Screen | ✅ Implemented | `PluginTracesScreen.cs` — split pane, filter/timeline/delete/traceLevel dialogs |
| MCP Tools | ✅ Implemented | `PluginTracesListTool`, `GetTool`, `TimelineTool`, `DeleteTool` |

### RPC Endpoints

| Method | Request | Response |
|--------|---------|----------|
| `pluginTraces/list` | `{ filter?: TraceFilter, top?, environmentUrl? }` | `{ traces: PluginTraceInfo[] }` |
| `pluginTraces/get` | `{ id, environmentUrl? }` | `{ trace: PluginTraceDetail }` |
| `pluginTraces/timeline` | `{ correlationId, environmentUrl? }` | `{ nodes: TimelineNode[] }` |
| `pluginTraces/delete` | `{ ids?, olderThanDays?, environmentUrl? }` | `{ deletedCount: number }` |
| `pluginTraces/traceLevel` | `{ environmentUrl? }` | `{ level: string }` |
| `pluginTraces/setTraceLevel` | `{ level, environmentUrl? }` | `{ success: boolean }` |

**TraceFilter fields:** typeName?, messageName?, primaryEntity?, mode? (Sync/Async), hasException?, correlationId?, minDuration?, startDate?, endDate?

**PluginTraceInfo fields (list):** id, createdOn, typeName, primaryEntity, messageName, operationType, mode, depth, duration, hasException

**PluginTraceDetail fields:** all of PluginTraceInfo + exceptionDetails, messageBlock, configuration, secureConfiguration, correlationId, executionStartTime, performanceConstructorDuration, performanceExecutionDuration

**TimelineNode fields:** traceId, typeName, messageName, depth, duration, startTime, hasException, children[]

### VS Code Panel

- **viewType:** `ppds.pluginTraces`
- **Layout:** Three-zone with split pane — top: filter bar + trace list, bottom: detail/timeline (resizable splitter)
- **Filter bar (persistent, collapsible):** Entity filter, message filter, plugin name filter, mode (Sync/Async/All), exceptions only toggle, date range, quick filters (Last Hour, Exceptions Only, Long Running >1s), clear all
- **Table columns:** Status (icon), Time, Duration, Plugin Name, Entity, Message, Depth, Mode
- **Color coding:** Exception rows red, long-running (>1s) yellow
- **Detail pane (5 tabs):** Details, Exception (monospace), Message Block (monospace), Configuration, Timeline (hierarchical tree with timing)
- **Actions:** Refresh, filter, auto-refresh toggle (5/15/30/60/300s), delete (selected/filtered/older than N days with confirmation), set trace level (Off/Exception/All with volume warning), export (CSV/JSON), open related traces (by correlation ID), environment picker with theming

### TUI Screen

- **Class:** `PluginTracesScreen` extending `TuiScreenBase`
- **Layout:** Split pane — top: data table, bottom: detail view (resizable via SplitterView)
- **Hotkeys:** Ctrl+R (refresh), Enter (toggle detail), Ctrl+F (filter dialog), Ctrl+T (timeline), Ctrl+D (delete dialog), Ctrl+L (trace level), Ctrl+E (export), Tab (cycle detail tabs)
- **Dialogs:** `PluginTraceFilterDialog`, `PluginTraceDetailDialog`, `TraceTimelineDialog`, `TraceLevelDialog`, `TraceDeleteDialog`

### MCP Tools

| Tool | Input | Output | Status |
|------|-------|--------|--------|
| `ppds_plugin_traces_list` | `{ filter?, top? }` | Filtered trace list | Exists — may need filter updates |
| `ppds_plugin_traces_get` | `{ id }` | Full trace detail | Exists |
| `ppds_plugin_traces_timeline` | `{ correlationId }` | Execution tree | Exists |
| `ppds_plugin_traces_delete` | `{ ids?, olderThanDays? }` | Deleted count | New |

---

## Panel 5: Metadata Browser

### Purpose

Browse entity definitions, attributes, relationships, keys, and privileges. The schema exploration tool for understanding the data model.

### Infrastructure Readiness

| Layer | Status | Location |
|-------|--------|----------|
| Domain Service | ✅ Ready | `IMetadataService` / `DataverseMetadataService`, `ICachedMetadataProvider` |
| Entity Classes | N/A | Metadata API uses EntityDefinitions endpoint |
| CLI | ✅ Ready | `ppds data schema` for single-entity inspection |
| RPC | ✅ Implemented | `metadata/entities`, `metadata/entity` |
| VS Code Panel | ✅ Implemented | `MetadataBrowserPanel.ts` — split pane, search, 5-tab detail |
| TUI Screen | ✅ Implemented | `MetadataExplorerScreen.cs` |
| MCP Tools | ✅ Implemented | `MetadataEntitiesListTool.cs`, `MetadataEntityTool.cs` |

### RPC Endpoints

| Method | Request | Response |
|--------|---------|----------|
| `metadata/entities` | `{ environmentUrl? }` | `{ entities: EntitySummary[] }` |
| `metadata/entity` | `{ logicalName, environmentUrl? }` | `{ entity: EntityDetail }` |

**EntitySummary fields:** logicalName, schemaName, displayName, isCustomEntity, isManaged, ownershipType

**EntityDetail fields:** all of EntitySummary + attributes[], relationships[] (oneToMany + manyToMany), keys[], privileges[]

**Design decision:** Two endpoints, not five. `metadata/entities` returns the lightweight list. `metadata/entity` returns everything for a selected entity in one call. Avoids chatty round-trips while keeping initial load fast.

### VS Code Panel

- **viewType:** `ppds.metadataBrowser`
- **Layout:** Split pane — left: entity list with search/filter, right: 5-tab detail view
- **Left pane:** Flat entity list (sortable), search box (client-side filter as you type), custom vs system entity icons
- **Right pane tabs:** Attributes (type-specific metadata), Relationships (1:N and N:N with cascade config), Keys (alternate keys with fields), Privileges (access rights), Choices (option set values for selected choice attribute)
- **Caching:** Entity list cached with configurable TTL (default 5 minutes); entity details cached on first load, invalidated on refresh
- **Actions:** Refresh (clears cache), search, Open in Maker, environment picker with theming

### TUI Screen

- **Class:** `MetadataExplorerScreen` extending `TuiScreenBase`
- **Layout:** Split pane — left: entity list, right: tabbed detail
- **Hotkeys:** Ctrl+R (refresh), Ctrl+F (search), Tab (cycle tabs), Enter (select entity), Ctrl+O (open in Maker)
- **Dialogs:** `EntitySearchDialog` (quick pick for large schemas), `ChoiceValuesDialog`

### MCP Tools

| Tool | Input | Output | Status |
|------|-------|--------|--------|
| `ppds_data_schema` | `{ entityName }` | Entity schema | Exists |
| `ppds_metadata_entity` | `{ entityName }` | Full metadata with relationships | Exists |
| `ppds_metadata_entities` | `{ environmentUrl? }` | Entity list for discovery | New |

---

## Panel 6: Web Resources

### Purpose

Browse, view, edit, and publish web resources. Features a FileSystemProvider for in-editor editing with auto-publish, conflict detection, and unpublished change detection. The most complex panel at the VS Code layer.

### Infrastructure Readiness

| Layer | Status | Location |
|-------|--------|----------|
| Domain Service | ✅ Implemented | `IWebResourceService` — ListAsync, GetAsync, GetContentAsync, GetModifiedOnAsync, UpdateContentAsync, PublishAsync, PublishAllAsync |
| Entity Class | ✅ Ready | `webresource.cs` |
| Publish Coordination | ✅ Implemented | Per-environment `SemaphoreSlim` in `PooledClientExtensions.cs:22` — shared by PublishXml and PublishAllXml |
| RPC | ✅ Implemented | `webResources/list`, `get`, `getModifiedOn`, `update`, `publish`, `publishAll` |
| VS Code Panel | ✅ Implemented | `WebResourcesPanel.ts` — virtual table, solution filter, text-only toggle, FSP integration |
| TUI Screen | ✅ Implemented | `WebResourcesScreen.cs` |
| MCP Tools | ✅ Implemented | `WebResourcesListTool`, `GetTool`, `PublishTool` |

### Service Design

`IWebResourceService` in `src/PPDS.Dataverse/Services/`:

| Method | Purpose |
|--------|---------|
| `ListAsync(solutionId?, textOnly?, top?)` | Query web resources with solution and type filters |
| `GetAsync(id)` | Get web resource metadata |
| `GetContentAsync(id, published?)` | Get content — uses RetrieveUnpublished for unpublished, standard query for published |
| `GetModifiedOnAsync(id)` | Lightweight query for conflict detection (modifiedon only) |
| `UpdateContentAsync(id, content)` | Update content (base64 encoded) — does NOT publish |
| `PublishAsync(ids)` | Publish specific web resources via PublishXml (coordinated) |
| `PublishAllAsync()` | Publish all customizations via PublishAllXml (coordinated) |

**Web resource types:** HTML (1), CSS (2), JavaScript (3), XML (4), PNG (5), JPG (6), GIF (7), XAP (8), XSL (9), ICO (10), SVG (11), RESX (12)

**Text types (editable):** 1, 2, 3, 4, 9, 11, 12
**Binary types (view metadata only):** 5, 6, 7, 8, 10

### RPC Endpoints

| Method | Request | Response |
|--------|---------|----------|
| `webResources/list` | `{ solutionId?, textOnly?, environmentUrl? }` | `{ resources: WebResourceInfo[] }` |
| `webResources/get` | `{ id, published?, environmentUrl? }` | `{ resource: WebResourceDetail }` |
| `webResources/getModifiedOn` | `{ id, environmentUrl? }` | `{ modifiedOn: string }` |
| `webResources/update` | `{ id, content, environmentUrl? }` | `{ success: boolean }` |
| `webResources/publish` | `{ ids, environmentUrl? }` | `{ publishedCount: number }` |
| `webResources/publishAll` | `{ environmentUrl? }` | `{ success: boolean }` |

**WebResourceInfo fields:** id, name, displayName, type, typeName, isManaged, createdBy, createdOn, modifiedBy, modifiedOn

**WebResourceDetail fields:** all of WebResourceInfo + content (decoded string for text types, null for binary)

### VS Code Panel

- **viewType:** `ppds.webResources`
- **Layout:** Three-zone with virtual table
- **Table columns:** Name (clickable link for text types), Display Name, Type (with icon), Managed, Created By, Created On, Modified By, Modified On
- **Default sort:** name ascending
- **Solution filter:** Dropdown (persisted). Smart strategy: small solutions (<=100 components) use OData filter, large solutions fetch all + client-side filter (URL length limits)
- **Type filter:** Toggle — "Text only" (default) vs "All"
- **Search:** Client-side substring match with server-side OData fallback
- **Request versioning:** Stale response protection during rapid solution changes

#### FileSystemProvider

- **URI scheme:** `ppds-webresource:///environmentId/webResourceId/filename.ext`
- **Content modes:** unpublished (default, editable), published (read-only for diff), conflict, server-current, local-pending
- **On open flow:**
  1. Fetch published + unpublished content in parallel
  2. If they differ: show diff with "Edit Unpublished" / "Edit Published" / "Cancel"
  3. Open chosen version; set language mode from web resource type
- **On save flow:**
  1. No-change detection — skip if content unchanged
  2. Conflict detection — compare cached modifiedOn with server's current
  3. If conflict: modal (Compare First / Overwrite / Discard My Work)
  4. If Compare First: open diff view (server-current left, local-pending right), then resolution modal (Save My Version / Use Server Version / Cancel)
  5. On success: fire change + save events, show non-modal "Saved: filename" notification with Publish button
  6. Cache refresh: fetch new modifiedOn from server
- **Caching:** serverState (modifiedOn + lastKnownContent), preFetchedContent, pendingFetches (deduplication), pendingSaveContent
- **Publish coordination:** PublishCoordinator prevents concurrent publish operations per environment
- **Auto-refresh:** Panel subscribes to onDidSaveWebResource event, updates row without full reload
- **Language detection:** Maps file extension to VS Code language ID (js->javascript, css->css, html->html, xml->xml, etc.)
- **Binary protection:** NonEditableWebResourceError for binary types (PNG/JPG/GIF/ICO/XAP)

### TUI Screen

- **Class:** `WebResourcesScreen` extending `TuiScreenBase`
- **Hotkeys:** Ctrl+R (refresh), Enter (view content dialog for text types), Ctrl+P (publish selected), Ctrl+F (filter by solution), Ctrl+T (toggle text-only/all), Ctrl+O (open in Maker)
- **Dialogs:** `WebResourceContentDialog` (read-only text view), `PublishConfirmDialog`

### MCP Tools

| Tool | Input | Output |
|------|-------|--------|
| `ppds_web_resources_list` | `{ solutionId?, textOnly?, environmentUrl? }` | Web resource list with type and metadata |
| `ppds_web_resources_get` | `{ id }` | Full detail with decoded content for text types |
| `ppds_web_resources_publish` | `{ ids }` | Publish result |

---

## Acceptance Criteria

### Panel 1: Import Jobs

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-IJ-01 | `importJobs/list` returns all import jobs for active environment sorted by createdOn desc | TBD | ✅ |
| AC-IJ-02 | `importJobs/get` returns full import job detail including XML data field | TBD | ✅ |
| AC-IJ-03 | VS Code panel displays import jobs with status color coding (success=green, failure=red) | TBD | ✅ |
| AC-IJ-04 | VS Code panel supports per-panel environment selection with environment theming | TBD | ✅ |
| AC-IJ-05 | Click row shows XML import log | TBD | ✅ |
| AC-IJ-06 | TUI ImportJobsScreen displays same columns and data | TBD | ✅ |
| AC-IJ-07 | TUI Enter key opens ImportJobDetailDialog with scrollable import log | TBD | ✅ |
| AC-IJ-08 | MCP ppds_import_jobs_list returns structured data | TBD | ✅ |
| AC-IJ-09 | MCP ppds_import_jobs_get returns full detail including import log | TBD | ✅ |
| AC-IJ-10 | All surfaces handle empty state (no import jobs) gracefully | TBD | ✅ |
| AC-IJ-11 | Panel uses `buildMakerUrl()` for Maker Portal links (not inline URL construction) | TBD | 🔲 |

### Panel 2: Connection References

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-CR-01 | `connectionReferences/list` returns references with optional solution filter | TBD | ✅ |
| AC-CR-02 | Connection status populated from Power Platform Connections API | TBD | ✅ |
| AC-CR-03 | Graceful degradation when Connections API unavailable (SPN) — panel loads, status shows N/A | TBD | ✅ |
| AC-CR-04 | VS Code panel displays table with color-coded connection status | TBD | ✅ |
| AC-CR-05 | Solution filter persists selection to globalState with panel-specific key | TBD | 🔲 |
| AC-CR-06 | Row click shows detail pane with connection info, dependent flows, orphan status | TBD | ✅ |
| AC-CR-07 | Analyze action identifies orphaned references and flows | TBD | ✅ |
| AC-CR-08 | TUI ConnectionReferencesScreen displays same data and operations | TBD | ✅ |
| AC-CR-09 | MCP ppds_connection_references_analyze returns structured orphan analysis | TBD | ✅ |
| AC-CR-10 | Open in Maker uses `buildMakerUrl()` (not inline URL construction) | TBD | 🔲 |
| AC-CR-11 | Panel unit tests cover message handling, environment switching, data loading | TBD | 🔲 |

### Panel 3: Environment Variables

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-EV-01 | `environmentVariables/list` returns definitions joined with current values | TBD | ✅ |
| AC-EV-02 | `environmentVariables/set` updates value record (creates if none exists) | TBD | ✅ |
| AC-EV-03 | VS Code panel shows visual differentiation between default, overridden, and missing values | TBD | ✅ |
| AC-EV-04 | Solution filter persists selection to globalState with panel-specific key | TBD | 🔲 |
| AC-EV-05 | Edit action validates input by type (String/Number/Boolean/JSON) | TBD | ✅ |
| AC-EV-06 | Export deployment settings writes correct .deploymentsettings.json format | TBD | ✅ |
| AC-EV-07 | TUI EnvironmentVariablesScreen displays same data and supports edit via dialog | TBD | ✅ |
| AC-EV-08 | MCP ppds_environment_variables_set can update a variable value | TBD | ✅ |
| AC-EV-09 | Required variables with no value and no default show warning indicator | TBD | ✅ |
| AC-EV-10 | All surfaces handle zero environment variables gracefully | TBD | ✅ |
| AC-EV-11 | Open in Maker uses `buildMakerUrl()` (not inline URL construction) | TBD | 🔲 |
| AC-EV-12 | Panel unit tests cover message handling, environment switching, data loading | TBD | 🔲 |

### Panel 4: Plugin Traces

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-PT-01 | `pluginTraces/list` applies all filter combinations server-side | TBD | ✅ |
| AC-PT-02 | `pluginTraces/get` returns full detail including exception, message block, configuration | TBD | ✅ |
| AC-PT-03 | `pluginTraces/timeline` returns hierarchical execution tree | TBD | ✅ |
| AC-PT-04 | `pluginTraces/delete` supports by IDs, by filter, and by age | TBD | ✅ |
| AC-PT-05 | `pluginTraces/traceLevel` and `setTraceLevel` read/write organization setting | TBD | ✅ |
| AC-PT-06 | VS Code panel displays trace list with filter bar, color-coded status, resizable detail pane | TBD | ✅ |
| AC-PT-07 | Detail pane has 5 tabs (Details, Exception, Message Block, Configuration, Timeline) | TBD | ✅ |
| AC-PT-08 | Timeline tab renders hierarchical execution chain with timing | TBD | ✅ |
| AC-PT-09 | Quick filters apply correct filter combinations | TBD | ✅ |
| AC-PT-10 | Auto-refresh updates list at configured interval without losing selection | TBD | ✅ |
| AC-PT-11 | Delete operations require confirmation and report count | TBD | ✅ |
| AC-PT-12 | Trace level change shows warning about volume impact for "All" | TBD | ✅ |
| AC-PT-13 | TUI PluginTracesScreen provides equivalent functionality with split pane | TBD | ✅ |
| AC-PT-14 | Existing MCP tools continue working; new delete tool supports bulk cleanup | TBD | ✅ |
| AC-PT-15 | All surfaces handle "trace level is Off" — informational message, not empty table | TBD | ✅ |
| AC-PT-16 | VS Code panel has export button — CSV and JSON formats, respects current filter state | TBD | 🔲 |
| AC-PT-17 | VS Code filter bar has start date and end date inputs; quick filters update date inputs | TBD | 🔲 |
| AC-PT-18 | Panel calls `resolveEnvironmentId()` and supports Open in Maker action | TBD | 🔲 |
| AC-PT-19 | TUI Ctrl+E hotkey exports filtered traces to CSV/JSON file | TBD | 🔲 |
| AC-PT-20 | Open in Maker uses `buildMakerUrl()` (not inline URL construction) | TBD | 🔲 |

### Panel 5: Metadata Browser

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-MB-01 | `metadata/entities` returns all entity definitions with summary fields | TBD | ✅ |
| AC-MB-02 | `metadata/entity` returns full detail (attributes, relationships, keys, privileges) in one call | TBD | ✅ |
| AC-MB-03 | VS Code panel displays entity list with search/filter and tabbed detail pane | TBD | ✅ |
| AC-MB-04 | Search/filter box filters entity list as user types (client-side) | TBD | ✅ |
| AC-MB-05 | Attributes tab shows type-specific metadata | TBD | ✅ |
| AC-MB-06 | Relationships tab shows 1:N and N:N with cascade configuration | TBD | ✅ |
| AC-MB-07 | Entity list cached with configurable TTL; refresh clears cache | TBD | ✅ |
| AC-MB-08 | TUI MetadataExplorerScreen provides equivalent split pane with tab cycling | TBD | ✅ |
| AC-MB-09 | MCP ppds_metadata_entities returns entity list for AI discovery | TBD | ✅ |
| AC-MB-10 | Handles large schemas (500+ entities) without UI lag | TBD | ✅ |
| AC-MB-11 | `openMetadataBrowserForEnv` context menu command opens panel scoped to selected environment | TBD | 🔲 |
| AC-MB-12 | Open in Maker uses `buildMakerUrl()` (not inline URL construction) | TBD | 🔲 |

### Panel 6: Web Resources

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-WR-01 | `IWebResourceService` created with list, get (unpublished + published), update, and publish | TBD | ✅ |
| AC-WR-02 | `webResources/list` returns web resources with solution and type filters | TBD | ✅ |
| AC-WR-03 | `webResources/get` returns decoded content using RetrieveUnpublished | TBD | ✅ |
| AC-WR-04 | `webResources/publish` calls PublishXml for single/batch; PublishAllXml for all | TBD | ✅ |
| AC-WR-05 | FileSystemProvider registers ppds-webresource scheme with environment-scoped URIs | TBD | ✅ |
| AC-WR-06 | On open: detects unpublished changes, shows diff if different | TBD | ✅ |
| AC-WR-07 | On save: conflict detection with full resolution flow (compare, diff, resolve) | TBD | ✅ |
| AC-WR-08 | On save: non-modal notification with Publish button | TBD | ✅ |
| AC-WR-09 | Auto-refresh: panel row updates on save event without full reload | TBD | ✅ |
| AC-WR-10 | Publish coordination prevents concurrent publishes per environment | TBD | ✅ |
| AC-WR-11 | Solution filter: OData for small solutions, client-side for large | TBD | ✅ |
| AC-WR-12 | Request versioning discards stale responses during rapid solution changes | TBD | ✅ |
| AC-WR-13 | VS Code panel displays virtual table with type icons, solution filter, text-only toggle | TBD | ✅ |
| AC-WR-14 | Language mode auto-detected from web resource type on document open | TBD | ✅ |
| AC-WR-15 | TUI WebResourcesScreen displays list; Enter opens content dialog for text types | TBD | ✅ |
| AC-WR-16 | MCP tools return decoded content for AI analysis and support publish | TBD | ✅ |
| AC-WR-17 | Virtual table handles 1000+ web resources with pagination | TBD | ✅ |
| AC-WR-18 | Binary types viewable in list but not editable — clear error on edit attempt | TBD | ✅ |
| AC-WR-19 | Search input in toolbar with debounced client-side substring filtering (300ms) | TBD | 🔲 |
| AC-WR-20 | `copyToClipboard` message handler copies selected row data | TBD | 🔲 |
| AC-WR-21 | Panel uses `SolutionFilter` shared component (not raw `<select>`) | TBD | 🔲 |
| AC-WR-22 | Publish All button in VS Code panel calls `webResources/publishAll` | TBD | 🔲 |
| AC-WR-23 | TUI Publish All hotkey with confirmation dialog | TBD | 🔲 |

### Cross-Cutting: Base Class & Consistency

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-CC-01 | `WebviewPanelBase.initializePanel()` template method handles auth, env resolution, title update, initial load — all 6 panels use it instead of duplicated `initialize()` | TBD | 🔲 |
| AC-CC-02 | `WebviewPanelBase.handleEnvironmentPickerClick()` handles picker, state update, reload — all 6 panels use it instead of duplicated handler | TBD | 🔲 |
| AC-CC-03 | `WebviewPanelBase.resolveEnvironmentId()` maps environment URL to GUID — all 6 panels call it (including PluginTraces) | TBD | 🔲 |
| AC-CC-04 | `WebviewPanelBase.updatePanelTitle()` sets panel title with profile and environment — all 6 panels use it | TBD | 🔲 |
| AC-CC-05 | All panels use `config.resolvedType` fallback for environment type resolution | TBD | 🔲 |
| AC-CC-06 | All panels support `copyToClipboard` message handler | TBD | 🔲 |
| AC-CC-07 | TUI environment selector has Open in Maker and Open in Dynamics actions (#597) | TBD | 🔲 |
| AC-CC-08 | TUI keyboard shortcuts dialog scrolls to show all bindings including screen-specific (#595) | TBD | 🔲 |

---

## Design Decisions

### Why one spec for all 6 panels?

**Context:** Could have written 6 separate specs.
**Decision:** Single spec with shared architecture + per-panel sections.
**Rationale:** Panels share infrastructure (RPC handler, webview base, virtual table, solution filter, environment picker). Unified spec makes cross-panel patterns explicit and prevents drift.

### Why virtual table for every panel?

**Context:** Some panels have small datasets.
**Decision:** Virtual table everywhere.
**Rationale:** One table component, one code path. Consistency outweighs micro-optimization. Establishes the pattern in Phase 1 for Phase 2 to inherit.

### Why Connections API (BAPI) for Connection References?

**Context:** Panel could show only Dataverse data.
**Decision:** Include connection status (option a) and detail pane (option b). Defer connection picker (option c).
**Rationale:** Service already exists. Without status, panel shows raw connector IDs — low value. SPN graceful degradation keeps panel functional for all auth methods. Option c deferred because legacy never shipped it and deployment settings sync covers the automated use case.

### Why FileSystemProvider for Web Resources?

**Context:** Could simplify to read-only preview + manual publish.
**Decision:** Full FileSystemProvider with conflict detection, unpublished change detection, auto-publish notification, and publish coordination.
**Rationale:** The save, conflict detect, diff, resolve, publish flow is the core developer experience. Simplifying would be a regression. The daemon architecture changes transport (RPC) but preserves behavior.

### Why Phase 1 pattern-setter before parallel execution?

**Context:** Could parallelize all 6 immediately.
**Decision:** Import Jobs first (sequential), then 4 parallel worktrees.
**Rationale:** Simplest panel establishes RPC, webview, TUI, and MCP patterns. Without this, parallel agents would create divergent implementations.

---

## Issue Reconciliation

### Phase 3 Issues (In Scope)

| Issue | Title | ACs | Status |
|-------|-------|-----|--------|
| #619 | openMetadataBrowserForEnv context menu | AC-MB-11 | 🔲 Open |
| #620 | Persist solution filter in ConnRefs + EnvVars | AC-CR-05, AC-EV-04 | 🔲 Open |
| #621 | Unit tests for ConnRefs + EnvVars panels | AC-CR-11, AC-EV-12 | 🔲 Open |
| #622 | CSV/JSON export for Plugin Traces | AC-PT-16, AC-PT-19 | 🔲 Open |
| #623 | Date range filter for Plugin Traces | AC-PT-17 | 🔲 Open |
| #624 | Search input for Web Resources | AC-WR-19 | 🔲 Open |
| #625 | publishAll UI button (RPC exists) | AC-WR-22, AC-WR-23 | 🔲 Open |
| #595 | TUI keyboard shortcuts scroll | AC-CC-08 | 🔲 Open |
| #597 | TUI env selector Maker/Dynamics actions | AC-CC-07 | 🔲 Open |

### Stale Issues — Close After Verification

| Issue | Title | Evidence | Action |
|-------|-------|----------|--------|
| #585 | Plugin Traces panel | Shipped in PR #615 | Close |
| #586 | MCP: Import jobs tools | `ImportJobsListTool.cs`, `ImportJobsGetTool.cs` exist | Close |
| #587 | MCP: Connection references tools | `ConnectionReferencesListTool.cs`, `GetTool`, `AnalyzeTool` exist | Close |
| #588 | MCP: Environment variables tools | `EnvironmentVariablesListTool.cs`, `GetTool`, `SetTool` exist | Close |
| #589 | MCP: Web resources tools | `WebResourcesListTool.cs`, `GetTool`, `PublishTool` exist | Close |
| #590 | MCP: Metadata entities list tool | `MetadataEntitiesListTool.cs` exists | Close |
| #591 | MCP: Plugin traces delete tool | `PluginTracesDeleteTool.cs` exists | Close |
| #593 | IWebResourceService extraction | `IWebResourceService.cs` + `WebResourceService.cs` in `PPDS.Dataverse/Services/` | Close |
| #626 | PublishCoordinator | Per-env `SemaphoreSlim` in `PooledClientExtensions.cs:22` | Close |

### Consistency Fixes (No Dedicated Issues)

| Fix | ACs | Scope |
|-----|-----|-------|
| Base class extraction | AC-CC-01–04 | All 6 panels |
| `buildMakerUrl()` standardization | AC-IJ-11, AC-CR-10, AC-EV-11, AC-MB-12, AC-PT-20 | 5 panels (WebResources already uses it) |
| `resolvedType` fallback | AC-CC-05 | 5 panels |
| `copyToClipboard` handler | AC-CC-06, AC-WR-20 | WebResources (others already have it) |
| `SolutionFilter` component | AC-WR-21 | WebResources (switch from raw `<select>`) |
| `resolveEnvironmentId` + `openInMaker` | AC-PT-18 | PluginTraces |

---

## Work Breakdown

### Phasing

```
MVP PR merged to main
         |
         v
  Phase 1: Import Jobs (pattern-setter)          ✅ Merged (PR #611)
  Branch: feature/panel-import-jobs
         |
         v (merge to main)
         |
    +----+----+----------+----------+
    v         v          v          v
 Phase 2a  Phase 2b   Phase 2c   Phase 2d         ✅ All Merged
 Env+Conn  Traces     Metadata   Web Res           (PRs #617, #615, #616, #618)
    |         |          |          |
    v         v          v          v
  (all merge to main independently)
         |
         v
  Phase 3: Parity Polish                          🔲 In Progress
  Branch: feature/panel-parity-polish
  Issues: #619-625, #595, #597
  Close: #585-591, #593, #626
```

### Phase 3 Details

| Group | Items | ACs |
|-------|-------|-----|
| Base class extraction | `initializePanel()`, `handleEnvironmentPickerClick()`, `resolveEnvironmentId()`, `updatePanelTitle()` → `WebviewPanelBase` | AC-CC-01–04 |
| Plugin Traces gaps | Export CSV/JSON, date range filter, resolveEnvironmentId, openInMaker | AC-PT-16–20 |
| Web Resources gaps | Search input, copyToClipboard, SolutionFilter component, publishAll UI | AC-WR-19–23 |
| Consistency | `buildMakerUrl()` in 5 panels, `resolvedType` fallback, `copyToClipboard` | AC-IJ-11, AC-CR-10, AC-EV-11, AC-MB-12, AC-CC-05–06 |
| Persistence | Solution filter globalState in ConnRefs + EnvVars | AC-CR-05, AC-EV-04 |
| Context menus | `openMetadataBrowserForEnv` command | AC-MB-11 |
| TUI parity | Env selector Maker/Dynamics actions, keyboard shortcuts scroll | AC-CC-07–08 |
| Unit tests | ConnRefs + EnvVars panel tests | AC-CR-11, AC-EV-12 |
| Issue closure | Close #585–591, #593, #626 (9 stale issues) | N/A |

---

## Related Specs

- [architecture.md](./architecture.md) — Cross-cutting patterns
- [connection-pooling.md](./connection-pooling.md) — Dataverse connection management
- [CONSTITUTION.md](./CONSTITUTION.md) — Governing principles

---

## Roadmap

- **Connection Picker (option c):** Add connection binding dropdown to Connection References panel after parity
- **Plugin Registration:** Full plugin registration panels across all surfaces (separate spec)
- **Solution History:** Expand Import Jobs to include exports/publishes/uninstalls (matches Maker solutionsHistory)
- **Bulk operations UI:** Surface existing bulk operation infrastructure in panel actions
