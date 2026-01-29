# Plan: Get PPDS Specs Loop-Ready

## Goal

Fix spec accuracy, finish backend spec gaps, so UI specs can be written on a solid foundation — then ralph/muse builds the UI autonomously.

---

## Phase 1: Fix Drift in Existing Specs — COMPLETE

8 of 12 specs had additive drift (code had more members than specs documented). All fixed on `feat/spec-system-v2`. Staged, not yet committed.

| Spec | What was fixed |
|------|---------------|
| `plugins.md` | +4 properties on `PluginStepAttribute` |
| `analyzers.md` | Status → "Partial (3 of 13 rules implemented)", diagram updated |
| `authentication.md` | `ICredentialProvider` +4 props +1 method, `ISecureCredentialStore` +2 methods, `IGlobalDiscoveryService` removed phantom property, `AuthProfile` expanded from 7 to 20+ properties |
| `connection-pooling.md` | `IDataverseConnectionPool` expanded 3→16 members, `IPooledClient` +5 props, `IConnectionSource` +1 prop, `IThrottleTracker` +5 members |
| `bulk-operations.md` | `BulkOperationOptions` +3 props |
| `cli.md` | `IOutputWriter` +2 methods |
| `query.md` | `QueryResult` +4 props, `IQueryHistoryService` +3 methods |
| `tui.md` | `IHotkeyRegistry` +6 members + `HotkeyBinding` type, `ITuiErrorService` +2 methods |

No drift: `dataverse-services.md`, `mcp.md`, `migration.md`, `architecture.md`.

### Methodology Finding

The spec-gen loop itself caused the drift — LLMs summarize by default, capturing ~70-80% of interface members. A verification phase (PROMPT-SPEC-VERIFY.md) is needed between spec generation and planning. Documented in `vault/Methodology/Specs/ADDENDUM-SPEC-VERIFICATION.md`.

---

## Phase 2: Verify/Finish Backend Spec Gaps — COMPLETE

Four areas verified. Three specs updated, one noted as future work.

### 2a. Plugins — COMPLETE
- `plugins.md` expanded from ~15% to 100% coverage of `IPluginRegistrationService` (37 methods)
- Added 9 Info/Result types, 5 missing CLI commands (register, get, download, update, clean)
- Expanded config model properties (Deployment, RunAsUser, Enabled, AllTypeNames, ExtensionData)

### 2b. Data Migration — COMPLETE
- `migration.md` updated with 4 minor gaps: IProgressReporter.Reset(), IPluginStepManager interface, PageSize naming fix, 3 ImportOptions properties

### 2c. Plugin Traces — COMPLETE
- Created new `specs/plugin-traces.md` from scratch (595 lines)
- IPluginTraceService (12 methods), 9 data types, 6 CLI commands, TimelineHierarchyBuilder
- Design decisions: depth-based hierarchy, IProgress<int>, FetchXml counts, parallel deletion

### 2d. Web Resources — NO ACTION
- No code exists (only component type 61 references in solution service)
- Deferred to future work

**Verification discipline applied:** All member counts verified against source files before each commit.

---

## Phase 3: Write UI Specs — COMPLETE

5 TUI screen specs created with `Status: Draft`, `Code: None`. Each references the backend specs it depends on.

### 3a. Plugin Traces Screen — COMPLETE
- `tui-plugin-traces.md` (540 lines): PluginTraceScreen + 3 dialogs (detail, filter, timeline)
- Depends on: `plugin-traces.md` (IPluginTraceService, TimelineHierarchyBuilder)
- Workflows: browse/filter traces, inspect detail, view timeline hierarchy, bulk delete

### 3b. Plugin Registration Screen — COMPLETE
- `tui-plugin-registration.md` (523 lines): TreeView hierarchy browser + ConfirmDestructiveActionDialog
- Depends on: `plugins.md` (IPluginRegistrationService, 37 methods)
- Workflows: browse Package/Assembly/Type/Step/Image hierarchy, toggle step state, unregister with cascade preview

### 3c. Solutions Screen — COMPLETE
- `tui-solutions.md` (366 lines): SolutionScreen with component inspection
- Depends on: `dataverse-services.md` (ISolutionService, IImportJobService)
- Workflows: browse solutions, view components, export, monitor imports

### 3d. Environment Dashboard — COMPLETE
- `tui-environment-dashboard.md` (511 lines): Tabbed dashboard (Users, Flows, EnvVars, ConnRefs)
- Depends on: `dataverse-services.md` (5 service interfaces)
- Workflows: user/role management, flow enable/disable, env var editing, connection reference analysis

### 3e. Data Migration Screen — COMPLETE
- `tui-migration.md` (588 lines): MigrationScreen + ExecutionPlanPreviewDialog
- Depends on: `migration.md`, `bulk-operations.md`
- Workflows: configure export/import, preview execution plan, real-time progress with rate/ETA

**Spot-check before writing:** All Phase 2 member counts verified (IPluginRegistrationService=37, IPluginTraceService=12, PluginTraceFilter=16, ImportOptions=14).

---

## Phase 4: Run the Loop — COMPLETE

### 4a. Verify Phase 3 Specs — COMPLETE
All 5 TUI specs verified against SPEC-TEMPLATE.md:
- Template format: All sections present ✅
- Backend dependencies: Correctly referenced ✅
- State capture records: ITuiStateCapture pattern implemented ✅

| Spec | State Records |
|------|--------------|
| tui-plugin-traces.md | 4 (screen + 3 dialogs) |
| tui-plugin-registration.md | 2 (screen + shared dialog) |
| tui-solutions.md | 1 (screen only) |
| tui-environment-dashboard.md | 3 (screen + 2 dialogs) |
| tui-migration.md | 2 (screen + preview dialog) |

### 4b. Generate Implementation Plan — COMPLETE
Created `docs/UI-IMPLEMENTATION-PLAN.md` with:
- 31 new files to create
- 12 state capture records
- 7 dialog classes (1 shared across screens)
- Implementation order: ConfirmDestructiveActionDialog → Traces → Registration → Solutions → Dashboard → Migration
- Service dependencies, hotkeys, menu integration, and tests for each screen

### Next Steps
1. Configure muse.toml for ppds (or ralph-win for quick start)
2. Run the loop — human gates at design (spec review) and review (PR merge)

---

## Key Files

| File | Purpose |
|------|---------|
| `ppds/specs/*.md` | 18 specs (13 backend + 5 UI) |
| `ppds/specs/tui-*.md` | 5 UI specs (Phase 3) |
| `ppds/specs/SPEC-TEMPLATE.md` | Template for new specs |
| `ppds/docs/SPEC-LOOP-READY-PLAN.md` | This plan |
| `ppds/docs/UI-IMPLEMENTATION-PLAN.md` | Phase 4 implementation plan for ralph/muse |

## Git State

- Branch: `feat/spec-system-v2`
- Phase 1 committed: 8 spec drift fixes (`5ec0297`)
- Phase 2 committed: migration.md update (`c77e02b`), plugin-traces.md created (`1390ff4`), plugins.md expanded (`e5e36d2`)
- Phase 3 committed: 5 UI specs (`6012086`..`54eb6ab`)
- Phase 4: UI-IMPLEMENTATION-PLAN.md created, ready to commit
