# Connection References

**Status:** Implemented
**Last Updated:** 2026-03-18
**Code:** [src/PPDS.Dataverse/Services/IConnectionReferenceService.cs](../src/PPDS.Dataverse/Services/IConnectionReferenceService.cs) | [src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts](../src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts) | [src/PPDS.Cli/Tui/Screens/ConnectionReferencesScreen.cs](../src/PPDS.Cli/Tui/Screens/ConnectionReferencesScreen.cs)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

View connection references in an environment, see which Power Platform connections they're bound to (with health status via Connections API), understand flow dependencies, and detect orphaned references. Key tool for connection health monitoring and deployment troubleshooting.

### Goals

- **Connection health visibility:** Surface connection status from Power Platform Connections API alongside Dataverse connection reference data
- **Dependency analysis:** Show which cloud flows depend on each connection reference
- **Orphan detection:** Identify connection references with no bound connection or flows with missing references
- **Multi-surface consistency:** Same data and operations via VS Code, TUI, MCP, and CLI (Constitution A1, A2)

### Non-Goals

- Connection Picker / rebinding (deferred — tracked as separate issue)
- Connection creation or management (Power Platform admin operations)
- Connector registration or custom connector management

---

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                   UI Surfaces (thin)                  │
│  ┌───────────┐  ┌─────────┐  ┌──────┐  ┌─────────┐ │
│  │  VS Code  │  │   TUI   │  │ MCP  │  │   CLI   │ │
│  │  Webview  │  │ Screen  │  │ Tool │  │ Command │ │
│  └─────┬─────┘  └────┬────┘  └──┬───┘  └────┬────┘ │
│   JSON-RPC        Direct     Direct       Direct     │
│  ┌─────▼──────────────▼──────────▼────────────▼────┐ │
│  │        IConnectionReferenceService               │ │
│  │    ListAsync, GetAsync, GetFlowsUsingAsync,      │ │
│  │    AnalyzeAsync                                  │ │
│  ├──────────────────────────────────────────────────┤ │
│  │  IConnectionService (Power Platform API)         │ │
│  │  IFlowService                                    │ │
│  └─────────────────────┬───────────────────────────┘ │
│                        │                              │
│  ┌─────────────────────▼───────────────────────────┐ │
│  │  IDataverseConnectionPool + IPowerPlatformToken  │ │
│  └──────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `IConnectionReferenceService` | Domain service — ListAsync, GetAsync, GetFlowsUsingAsync, AnalyzeAsync |
| `IConnectionService` | Power Platform API integration — connection status via `service.powerapps.com` |
| `IFlowService` | Flow dependency resolution — ListAsync |
| `ConnectionReferencesPanel.ts` | VS Code webview panel — table, detail pane, solution filter, orphan analysis |
| `ConnectionReferencesScreen.cs` | TUI screen — data table, detail/analyze dialogs, hotkeys |
| `ConnectionReferencesListTool.cs` | MCP tool — structured listing with connection status |
| `ConnectionReferencesGetTool.cs` | MCP tool — full detail with flows and connection info |
| `ConnectionReferencesAnalyzeTool.cs` | MCP tool — orphan detection |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) for pooled Dataverse clients
- Depends on: [architecture.md](./architecture.md) for Application Service boundary
- Uses patterns from: [CONSTITUTION.md](./CONSTITUTION.md) — A1, A2, D1, D4

---

## Specification

### Service Layer

**RPC Endpoints:**

| Method | Request | Response |
|--------|---------|----------|
| `connectionReferences/list` | `{ solutionId?, environmentUrl? }` | `{ references: ConnectionReferenceInfo[] }` |
| `connectionReferences/get` | `{ logicalName, environmentUrl? }` | `{ reference: ConnectionReferenceDetail, flows: FlowInfo[], connection?: ConnectionInfo }` |
| `connectionReferences/analyze` | `{ environmentUrl? }` | `{ orphanedReferences: [], orphanedFlows: [] }` |
| `connections/list` | `{ connectorId?, environmentUrl? }` | `{ connections: ConnectionInfo[] }` |

**ConnectionReferenceInfo fields:** logicalName, displayName, connectorId, connectionId, isManaged, modifiedOn, connectionStatus (Connected/Error/Unknown), connectorDisplayName

**Note:** `connections/list` uses Power Platform API (`service.powerapps.com`), not Dataverse SDK. Requires `IPowerPlatformTokenProvider`. SPN auth has limited access — graceful degradation: panel loads without connection status, shows "N/A" instead.

### Extension Surface

- **viewType:** `ppds.connectionReferences`
- **Layout:** Three-zone with virtual table + detail pane on row click
- **Table columns:** Display Name, Logical Name, Connector, Connection Status (color-coded), Managed, Modified On
- **Default sort:** logicalName ascending
- **Solution filter:** Dropdown in toolbar (persisted across sessions)
- **Detail view (on row click):** Connection details (status, owner, shared/personal), dependent flows (clickable), orphan indicator
- **Actions:** Refresh (Ctrl+R), solution filter, analyze (orphan detection), Open in Maker, environment picker with theming
- **Graceful degradation:** SPN auth — connection status column shows "N/A", panel otherwise fully functional

### TUI Surface

- **Class:** `ConnectionReferencesScreen` extending `TuiScreenBase`
- **Hotkeys:** Ctrl+R (refresh), Enter (detail dialog), Ctrl+A (analyze), Ctrl+F (filter by solution), Ctrl+O (open in Maker)
- **Dialogs:** `ConnectionReferenceDetailDialog`, `OrphanAnalysisDialog`

### MCP Surface

| Tool | Input | Output |
|------|-------|--------|
| `ppds_connection_references_list` | `{ solutionId?, environmentUrl? }` | List with connection status |
| `ppds_connection_references_get` | `{ logicalName }` | Full detail with flows and connection info |
| `ppds_connection_references_analyze` | `{ environmentUrl? }` | Orphaned references and flows |

---

## Acceptance Criteria

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

---

## Design Decisions

### Why Connections API (BAPI) for connection status?

**Context:** Panel could show only Dataverse data (connection reference records without connection health).

**Decision:** Include connection status from Power Platform API and detail pane with dependent flows. Defer connection picker/rebinding.

**Rationale:** `IConnectionService` already exists. Without status, panel shows raw connector IDs — low value. SPN graceful degradation keeps panel functional for all auth methods. Connection picker deferred because the legacy extension never shipped it and deployment settings sync covers the automated use case.

### Why orphan detection as a dedicated analyze action?

**Context:** Could surface orphan indicators inline on every load, or provide a separate analysis action.

**Decision:** Separate `analyze` action that identifies orphaned references (no connection) and orphaned flows (missing connection reference).

**Rationale:** Orphan analysis requires cross-referencing flows, connections, and connection references — heavier than a standard list. Making it explicit avoids slow default loads while providing high-value diagnostic capability.

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-18 | Extracted from panel-parity.md per SL1 |

---

## Related Specs

- [architecture.md](./architecture.md) — Application Service boundary
- [connection-pooling.md](./connection-pooling.md) — Dataverse connection management
- [CONSTITUTION.md](./CONSTITUTION.md) — Governing principles
