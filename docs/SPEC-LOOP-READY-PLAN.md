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

## Phase 3: Write UI Specs — FUTURE (Human Gate)

Design session in plan mode. Write new specs with `Status: Draft`, `Code: None` for TUI screens/dialogs that consume backend services. Each UI spec references the backend spec it depends on.

This is creative/architectural work — decide which screens to build, what workflows they support, how they compose services. Human reviews and approves before ralph builds.

---

## Phase 4: Run the Loop — FUTURE

1. Generate `IMPLEMENTATION_PLAN.md` from Draft specs using planning prompts
2. Configure muse.toml for ppds (or ralph-win for quick start)
3. Run the loop — human gates at design (spec review) and review (PR merge)

---

## Key Files

| File | Purpose |
|------|---------|
| `ppds/specs/*.md` | 13 specs (12 accuracy-fixed in Phase 1, 1 new in Phase 2) |
| `ppds/specs/SPEC-TEMPLATE.md` | Template for new specs |
| `ppds/docs/SPEC-LOOP-READY-PLAN.md` | This plan |
| `vault/Methodology/Specs/ADDENDUM-SPEC-VERIFICATION.md` | Methodology fix for spec-gen accuracy |
| `vault/Methodology/Specs/PROMPT-SPEC-BUILD.md` | Existing spec-gen prompt |
| `vault/Methodology/Specs/PROMPT-PLAN-*.md` | Planning prompts |
| `vault/Methodology/Specs/PROMPT-BUILD-BUILD.md` | Build loop prompt |

## Git State

- Branch: `feat/spec-system-v2`
- Phase 1 committed: 8 spec drift fixes (`5ec0297`)
- Phase 2 committed: migration.md update (`c77e02b`), plugin-traces.md created (`1390ff4`), plugins.md expanded (`e5e36d2`)
