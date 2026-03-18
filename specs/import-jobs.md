# Import Jobs

**Status:** Implemented
**Last Updated:** 2026-03-18
**Code:** [src/PPDS.Dataverse/Services/IImportJobService.cs](../src/PPDS.Dataverse/Services/IImportJobService.cs) | [src/PPDS.Extension/src/panels/ImportJobsPanel.ts](../src/PPDS.Extension/src/panels/ImportJobsPanel.ts) | [src/PPDS.Cli/Tui/Screens/ImportJobsScreen.cs](../src/PPDS.Cli/Tui/Screens/ImportJobsScreen.cs)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

View solution import history for an environment. Shows import status, progress, duration, and allows drilling into the XML import log for troubleshooting failed imports.

### Goals

- **Import visibility:** Surface solution import history with status, progress, and duration across all PPDS surfaces
- **Failure diagnostics:** Drill into XML import logs to troubleshoot failed imports without leaving the IDE or TUI
- **Multi-surface consistency:** Same import data available via VS Code, TUI, MCP, and CLI (Constitution A1, A2)

### Non-Goals

- Solution History expansion (export/publish/uninstall tracking deferred)
- Import triggering (import operations handled by solution management, not this spec)

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
│  │              IImportJobService                   │ │
│  │          ListAsync, GetAsync, GetDataAsync       │ │
│  └─────────────────────┬───────────────────────────┘ │
│                        │                              │
│  ┌─────────────────────▼───────────────────────────┐ │
│  │         IDataverseConnectionPool                 │ │
│  └──────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `IImportJobService` | Domain service — ListAsync, GetAsync, GetDataAsync |
| `ImportJobsPanel.ts` | VS Code webview panel — table, detail, environment picker |
| `ImportJobsScreen.cs` | TUI screen — data table, detail dialog, hotkeys |
| `ImportJobsListTool.cs` | MCP tool — structured import job listing |
| `ImportJobsGetTool.cs` | MCP tool — full detail including import log |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) for pooled Dataverse clients
- Depends on: [architecture.md](./architecture.md) for Application Service boundary
- Uses patterns from: [CONSTITUTION.md](./CONSTITUTION.md) — A1, A2, D1

---

## Specification

### Service Layer

**RPC Endpoints:**

| Method | Request | Response |
|--------|---------|----------|
| `importJobs/list` | `{ environmentUrl?: string }` | `{ jobs: ImportJobInfo[] }` |
| `importJobs/get` | `{ id: string, environmentUrl?: string }` | `{ job: ImportJobDetail }` |

**ImportJobInfo fields:** id, solutionName, status, progress (%), createdBy, createdOn, startedOn, completedOn, duration (formatted)

**ImportJobDetail fields:** all of ImportJobInfo + data (XML import log)

### Extension Surface

- **viewType:** `ppds.importJobs`
- **Layout:** Three-zone (toolbar, virtual table, status bar) per webview panel pattern
- **Table columns:** Solution Name, Status (color-coded), Progress, Created By, Created On, Duration
- **Default sort:** createdOn descending
- **Actions:** Refresh (Ctrl+R), click row for import log detail, Open in Maker (opens solutionsHistory URL), environment picker with theming

### TUI Surface

- **Class:** `ImportJobsScreen` extending `TuiScreenBase`
- **Layout:** Data table with status bar
- **Hotkeys:** Ctrl+R (refresh), Enter (detail dialog), Ctrl+O (open in Maker)
- **Dialog:** `ImportJobDetailDialog` — scrollable XML import log

### MCP Surface

| Tool | Input | Output |
|------|-------|--------|
| `ppds_import_jobs_list` | `{ environmentUrl?: string }` | Import jobs with status/progress |
| `ppds_import_jobs_get` | `{ id: string }` | Full detail including import log XML |

---

## Acceptance Criteria

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

---

## Design Decisions

### Why Import Jobs as Phase 1 pattern-setter?

**Context:** Six panels needed to be implemented. Could parallelize all six immediately or sequence one first.

**Decision:** Import Jobs first (sequential), then four parallel worktrees for remaining panels.

**Rationale:** Simplest panel — read-only list + detail with no editing, no secondary API calls, no solution filtering. Establishes RPC handler, webview panel, TUI screen, and MCP tool patterns that other panels inherit. Without this, parallel agents would create divergent implementations.

### Why virtual table for import jobs?

**Context:** Import job lists are typically small (tens to low hundreds of records).

**Decision:** Virtual table component, same as all other panels.

**Rationale:** One table component, one code path. Consistency outweighs micro-optimization. The pattern established here was inherited by all five subsequent panels.

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
