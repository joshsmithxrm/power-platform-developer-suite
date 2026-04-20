# Shakedown Guard

**Status:** Draft
**Last Updated:** 2026-04-20
**Code:** [src/PPDS.Cli/Infrastructure/Safety/](../src/PPDS.Cli/Infrastructure/Safety/) | [src/PPDS.Cli/Services/](../src/PPDS.Cli/Services/) | [src/PPDS.Cli/Infrastructure/Errors/](../src/PPDS.Cli/Infrastructure/Errors/)
**Surfaces:** CLI | TUI | Extension | MCP

---

## Overview

A service-layer safety primitive that refuses Dataverse mutations while a `/shakedown` session is active. Closes the v1.0.0 shakedown's safety-architecture triad (findings #6, #23, #37): the existing Bash PreToolUse hook runs at the process-boundary layer and is bypassed by the TUI (long-lived PTY), the Extension daemon (long-lived JSON-RPC), and by CLI verb patterns it fails to pattern-match. The guard moves the check into the layer every surface traverses — Application Services — so all four surfaces are covered by a single code path per constitution A2.

### Goals

- **Uniform coverage**: Every mutation path (CLI, TUI, Extension daemon, MCP) hits the same guard check before Dataverse is touched.
- **Dual activation sources**: `PPDS_SHAKEDOWN=1` env var OR `.claude/state/shakedown-active.json` sentinel with a fresh `started_at`.
- **Defense in depth**: The Bash hook stays in place and still catches CLI mutation attempts at the process boundary. Guard catches everything the hook misses.
- **Clean error surface**: Blocks throw `PpdsException` with `ErrorCode = Safety.ShakedownActive`, so every UI renders the refusal the same way it renders any other service failure.
- **Pay down the A1 debt**: Six domain services currently hosted in `PPDS.Dataverse` move to `PPDS.Cli.Services`. The guard belongs in the domain layer; putting it in the infrastructure layer would leak PPDS policy into the reusable library.

### Non-Goals

- **Does not replace the Bash hook.** Hook and guard are complementary. Removing the hook costs defense in depth and the early-block UX for obvious CLI misuse.
- **Does not expose a bypass other than `unset PPDS_SHAKEDOWN` + sentinel expiry.** Matching the hook: there is no softer bypass. If a write is genuinely needed, that takes deliberate action.
- **Does not write or delete the sentinel file.** Writing is owned by the `/shakedown` skill's Phase 0. Cleanup is owned by `.claude/hooks/session-start-workflow.py`. Guard is read-only.
- **Does not analyze or block non-mutating reads.** The guard's scope is strictly writes; reads, metadata lookups, and pooled connection checkouts are unaffected.
- **Does not unify with MCP's existing `--read-only` mode.** Different activation intent (per-process launch flag vs. session-wide sentinel). Both checks fire independently; guard wins at the service layer.

---

## Architecture

```
                         ┌──────────────────────────────────────────┐
                         │  Sentinel sources (read-only)            │
                         │  - env var PPDS_SHAKEDOWN=1              │
                         │  - .claude/state/shakedown-active.json   │
                         │    (started_at within 24h)               │
                         └──────────────────────────────────────────┘
                                            │ resolved by
                                            ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│  IShakedownGuard  (src/PPDS.Cli/Infrastructure/Safety/)                      │
│  - EnsureCanMutate(op): void    (throws PpdsException on block, cached ≤5s)  │
└──────────────────────────────────────────────────────────────────────────────┘
                                            │ injected into
                                            ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│  Application Services  (src/PPDS.Cli/Services/)                              │
│  Every mutation method calls _guard.EnsureCanMutate(...) before Dataverse.   │
│                                                                              │
│  CLI-hosted (no relocation):     │  Relocated from PPDS.Dataverse this PR:   │
│  - PluginRegistrationService     │  - PluginTraceService                     │
│  - ServiceEndpointService        │  - WebResourceService                     │
│  - CustomApiService              │  - EnvironmentVariableService             │
│  - DataProviderService           │  - SolutionService                        │
│  - SqlQueryService (DML branch)  │  - ImportJobService (no mutations)        │
│                                  │  - MetadataAuthoringService               │
│                                  │  - UserService (no mutations)             │
│                                  │  - RoleService                            │
│                                  │  - FlowService (no mutations)             │
│                                  │  - ConnectionReferenceService (no muts)   │
│                                  │  - DeploymentSettingsService (no muts)    │
│                                  │  - ComponentNameResolver (no mutations)   │
└──────────────────────────────────────────────────────────────────────────────┘
                  ▲                ▲                ▲                ▲
                  │                │                │                │
           ┌──────┴──────┐  ┌──────┴──────┐  ┌──────┴──────┐  ┌──────┴──────┐
           │  CLI cmds   │  │  TUI screens│  │  MCP tools  │  │  Extension  │
           │             │  │             │  │             │  │  daemon RPC │
           └─────────────┘  └─────────────┘  └─────────────┘  └─────────────┘
                  ▲
                  │ PreToolUse
┌─────────────────┴────────────────────┐
│  .claude/hooks/shakedown-safety.py   │   ← defense in depth, unchanged
│  (still catches Bash `ppds` invokes) │
└──────────────────────────────────────┘
```

The guard is the single choke point at the domain layer. The hook remains a fast early-reject for obvious CLI misuse. MCP's per-tool `--read-only` check stays in place above the guard — different semantics, both fire.

### Components

| Component | Responsibility |
|-----------|----------------|
| `IShakedownGuard` / `ShakedownGuard` | Resolves activation state from env + sentinel file; throws structured exception on block. Thread-safe with short TTL cache. |
| `ShakedownSentinelReader` | Internal helper. Locates `.claude/state/shakedown-active.json` via `CLAUDE_PROJECT_DIR` > CWD, parses `started_at`, applies 24h staleness check. |
| `ErrorCodes.Safety` | New error-code category. `Safety.ShakedownActive` is the sole code in v1. |
| Relocated domain services (full set, 12 total) | All services under `PPDS.Dataverse/Services/` and `PPDS.Dataverse/Metadata/IMetadataAuthoringService` move to `PPDS.Cli/Services/`: `PluginTraceService`, `WebResourceService`, `EnvironmentVariableService`, `SolutionService`, `ImportJobService`, `MetadataAuthoringService`, `UserService`, `RoleService`, `FlowService`, `ConnectionReferenceService`, `DeploymentSettingsService`, `ComponentNameResolver`. Services with mutation methods (`PluginTrace`, `WebResource`, `EnvironmentVariable`, `Solution`, `MetadataAuthoring`, `Role`) take `IShakedownGuard` as a constructor parameter; services that are pure reads/transforms (`ImportJob`, `User`, `Flow`, `ConnectionReference`, `DeploymentSettings`, `ComponentNameResolver`) relocate for A1 compliance only. |
| CLI-hosted domain services (pre-existing) | `PluginRegistrationService`, `ServiceEndpointService`, `CustomApiService`, `DataProviderService`, `SqlQueryService` — gain `IShakedownGuard` dependency and a guard call at the top of every mutation method. |

### Dependencies

- Reads from: sentinel format defined by `.claude/skills/shakedown/SKILL.md` Phase 0 (written by the shakedown skill).
- Read-cleanup owned by: `.claude/hooks/session-start-workflow.py` (stale-sentinel sweep).
- Complements: `.claude/hooks/shakedown-safety.py` (PreToolUse Bash gate, unchanged).
- Related to: [architecture.md](./architecture.md) (A1/A2 layering), [query.md](./query.md) (DML safety; guard fires in addition to `DmlSafetyGuard`).

---

## Mutation Method Inventory

Concrete enumeration of every service method that must call `_guard.EnsureCanMutate(...)`. Frozen at spec-write time by reading the interface files listed below. Any method added to these interfaces in the future must also call the guard; enforcement is a reviewer responsibility until the deferred analyzer (see Roadmap) lands.

### CLI-hosted services (already in `src/PPDS.Cli/`)

| Service | Interface | Mutation methods | Count |
|---------|-----------|------------------|-------|
| `PluginRegistrationService` | [IPluginRegistrationService.cs](../src/PPDS.Cli/Plugins/Registration/IPluginRegistrationService.cs) | `UpsertAssemblyAsync`, `UpsertPackageAsync`, `UpsertPluginTypeAsync`, `UpsertStepAsync`, `UpsertImageAsync`, `DeleteImageAsync`, `DeleteStepAsync`, `DeletePluginTypeAsync`, `UnregisterImageAsync`, `UnregisterStepAsync`, `UnregisterPluginTypeAsync`, `UnregisterAssemblyAsync`, `UnregisterPackageAsync`, `UpdateStepAsync`, `UpdateImageAsync`, `EnableStepAsync`, `DisableStepAsync`, `AddToSolutionAsync` | 18 |
| `ServiceEndpointService` | [IServiceEndpointService.cs](../src/PPDS.Cli/Services/IServiceEndpointService.cs) | `RegisterWebhookAsync`, `RegisterServiceBusAsync`, `UpdateAsync`, `UnregisterAsync` | 4 |
| `CustomApiService` | [ICustomApiService.cs](../src/PPDS.Cli/Services/ICustomApiService.cs) | `RegisterAsync`, `UpdateAsync`, `UnregisterAsync`, `AddParameterAsync`, `UpdateParameterAsync`, `RemoveParameterAsync`, `SetPluginTypeAsync` | 7 |
| `DataProviderService` | [IDataProviderService.cs](../src/PPDS.Cli/Services/IDataProviderService.cs) | `RegisterDataSourceAsync`, `UnregisterDataSourceAsync`, `RegisterDataProviderAsync`, `UpdateDataProviderAsync`, `UnregisterDataProviderAsync` | 5 |
| `SqlQueryService` (DML path only) | [ISqlQueryService.cs](../src/PPDS.Cli/Services/Query/ISqlQueryService.cs) | DML branch inside `ExecuteAsync` (post-parse, post-`DmlSafetyGuard.Check()`, pre-executor dispatch) | 1 |

### Services relocated from PPDS.Dataverse (constitution A1 fix)

| Service | New interface location | Mutation methods | Count |
|---------|------------------------|------------------|-------|
| `PluginTraceService` | `src/PPDS.Cli/Services/PluginTraces/IPluginTraceService.cs` | `DeleteAsync`, `DeleteByIdsAsync`, `DeleteByFilterAsync`, `DeleteAllAsync`, `DeleteOlderThanAsync`, `SetSettingsAsync` | 6 |
| `WebResourceService` | `src/PPDS.Cli/Services/WebResources/IWebResourceService.cs` | `UpdateContentAsync`, `PublishAsync`, `PublishAllAsync` | 3 |
| `EnvironmentVariableService` | `src/PPDS.Cli/Services/EnvironmentVariables/IEnvironmentVariableService.cs` | `SetValueAsync` | 1 |
| `SolutionService` | `src/PPDS.Cli/Services/Solutions/ISolutionService.cs` | `ImportAsync`, `PublishAllAsync` | 2 |
| `ImportJobService` | `src/PPDS.Cli/Services/ImportJobs/IImportJobService.cs` | _(none — service is pure monitor/query; relocated only to pay down A1, no guard wiring required)_ | 0 |
| `MetadataAuthoringService` | `src/PPDS.Cli/Services/Metadata/Authoring/IMetadataAuthoringService.cs` | `CreateTableAsync`, `UpdateTableAsync`, `DeleteTableAsync`, `CreateColumnAsync`, `UpdateColumnAsync`, `DeleteColumnAsync`, `CreateOneToManyAsync`, `CreateManyToManyAsync`, `UpdateRelationshipAsync`, `DeleteRelationshipAsync`, `CreateGlobalChoiceAsync`, `UpdateGlobalChoiceAsync`, `DeleteGlobalChoiceAsync`, `AddOptionValueAsync`, `UpdateOptionValueAsync`, `DeleteOptionValueAsync`, `ReorderOptionsAsync`, `UpdateStateValueAsync`, `CreateKeyAsync`, `DeleteKeyAsync`, `ReactivateKeyAsync` | 21 |
| `UserService` | `src/PPDS.Cli/Services/Users/IUserService.cs` | _(none — pure reads: List, GetById, GetByDomainName, GetUserRoles; relocated for A1)_ | 0 |
| `RoleService` | `src/PPDS.Cli/Services/Roles/IRoleService.cs` | `AssignRoleAsync`, `RemoveRoleAsync` | 2 |
| `FlowService` | `src/PPDS.Cli/Services/Flows/IFlowService.cs` | _(none — pure reads: List, Get, GetById; relocated for A1)_ | 0 |
| `ConnectionReferenceService` | `src/PPDS.Cli/Services/ConnectionReferences/IConnectionReferenceService.cs` | _(none — pure reads + analysis: List, Get, GetById, GetFlowsUsing, Analyze; relocated for A1)_ | 0 |
| `DeploymentSettingsService` | `src/PPDS.Cli/Services/DeploymentSettings/IDeploymentSettingsService.cs` | _(none — pure transforms producing files: Generate, Sync, Validate. Consumes `IEnvironmentVariableService` and `IConnectionReferenceService` internally; relocating together resolves the dependency direction. Relocated for A1)_ | 0 |
| `ComponentNameResolver` | `src/PPDS.Cli/Services/SolutionComponents/IComponentNameResolver.cs` | _(none — pure read/resolve: Resolve; relocated for A1)_ | 0 |

**Totals:** 17 services, 70 mutation methods, 1 DML branch. Finding #6's "18 metadata noun-verb-noun forms" refers to the original report's approximation; the actual method count on `IMetadataAuthoringService` is 21, all enumerated above. Six of the 12 relocated services are pure reads/transforms — they relocate strictly for A1 compliance and carry no guard wiring.

### Operation descriptor convention

`operationDescription` is dot-delimited lowercase, matching the Bash hook's subcommand/verb pairs where possible. Examples:

| Service method | Operation descriptor |
|----------------|---------------------|
| `PluginRegistrationService.UpsertAssemblyAsync` | `plugins.assembly.upsert` |
| `PluginRegistrationService.UnregisterStepAsync` | `plugins.step.unregister` |
| `ServiceEndpointService.RegisterWebhookAsync` | `serviceendpoints.webhook.register` |
| `PluginTraceService.DeleteOlderThanAsync` | `plugintraces.deleteOlderThan` |
| `WebResourceService.PublishAsync` | `webresources.publish` |
| `MetadataAuthoringService.CreateTableAsync` | `metadata.table.create` |
| `RoleService.AssignRoleAsync` | `roles.assignRole` |
| `RoleService.RemoveRoleAsync` | `roles.removeRole` |
| `SqlQueryService.ExecuteAsync` (DML branch) | `query.dml` |

Descriptors are informational — they populate `PpdsException.UserMessage` and `Context["operation"]`. They are not used for routing, allowlisting, or any conditional behavior inside the guard.

---

## Specification

### Core Requirements

1. **Guard interface.** `IShakedownGuard` exposes exactly one public method: `void EnsureCanMutate(string operationDescription)`. It throws when a shakedown is active; returns normally otherwise. Synchronous — reads env vars and stats a file; no I/O that warrants async. No `IsActive()` accessor is exposed — surface layers never pre-check activation state because doing so creates a second code path (constitution A2) and invites TOCTOU bugs. If a future consumer genuinely needs a non-throwing check, that consumer calls `EnsureCanMutate` inside a `try/catch` scoped to `PpdsException.ErrorCode == Safety.ShakedownActive`.
2. **Activation detection.** Guard reports active when **either** of:
   - Environment variable `PPDS_SHAKEDOWN=1` (any other value, including unset, treated as inactive).
   - Sentinel file `.claude/state/shakedown-active.json` exists, parses as JSON, contains an ISO-8601 `started_at` string, AND the parsed timestamp is within 24h of `DateTimeOffset.UtcNow`.
3. **Project root resolution.** Guard looks for the sentinel at `{project_root}/.claude/state/shakedown-active.json`. Project root is resolved in this order: (a) `CLAUDE_PROJECT_DIR` environment variable, **if set to a non-empty string AND the directory exists**; otherwise (b) `Directory.GetCurrentDirectory()`. The existence check on (a) is deliberate: a stale `CLAUDE_PROJECT_DIR` pointing at a deleted worktree must not suppress sentinel detection in the current CWD. This mirrors the Bash hook's `_project_dir()` intent (the hook uses `normalize_msys_path` which silently returns the input for nonexistent paths — the C# port makes the fallback explicit).
4. **Stale-sentinel self-heal.** A sentinel with `started_at` older than 24h is treated as absent. Guard does NOT delete the file (that is the session-start hook's job); it just ignores it. This prevents a forgotten sentinel from permanently locking mutations.
5. **Corrupt-sentinel handling.** A sentinel file that fails to parse as JSON, is missing `started_at`, or has an unparseable timestamp is treated as absent — fail-open. Rationale: a corrupt file is more likely an unrelated project artifact than an active shakedown, and the env-var path still catches a genuinely-active shakedown. The guard logs a `Warning` level message when this occurs so operators can investigate.
6. **Thread-safety and caching.** Guard is registered as a singleton. Activation state is cached for up to 5 seconds. Rationale: real call-site patterns today invoke the guard roughly once per user action (one `DeleteAllAsync` call drives a bulk delete underneath) so the stat cost is already small — the cache exists primarily to keep the guard cheap for future call sites that may call it more frequently (e.g., per-record DML validation) without forcing every consumer to reason about amortization. The 5s TTL stays far shorter than any human-mediated state change: an operator opting in or out of a shakedown is a deliberate multi-second action. Cache granularity is coarse — any env var change or sentinel mtime change within the TTL window is ignored. Thread safety is enforced via a single `object`-monitor around cache resolution; no torn reads.

7. **Injected dependencies.** `ShakedownGuard`'s constructor takes four abstractions (for test substitutability): `IEnvironment` (reads env vars), `IFileSystem` (stats/opens the sentinel file), `IClock` (`DateTimeOffset.UtcNow` source for freshness check), and `ILogger<ShakedownGuard>` (warnings for corrupt sentinel, stale-`CLAUDE_PROJECT_DIR`, truthy-non-one env var). The three non-logger abstractions are new to this spec; their default implementations wrap `System.Environment`, `System.IO.File`/`Directory`, and `DateTimeOffset.UtcNow` respectively. They live alongside the guard in `src/PPDS.Cli/Infrastructure/Safety/` and are registered in `AddCliApplicationServices`.
8. **Block exception.** `EnsureCanMutate` throws `PpdsException` with:
   - `ErrorCode = ErrorCodes.Safety.ShakedownActive`
   - `UserMessage` naming the blocked operation, the activation source (`env:PPDS_SHAKEDOWN` or `sentinel:<path-relative-to-project-root>`), and the bypass instructions (`unset PPDS_SHAKEDOWN` and/or remove sentinel).
   - `Severity = PpdsSeverity.Error`
   - `Context` dictionary (`PpdsException` has `IDictionary<string, object>? Context { get; init; }`, verified in [PpdsException.cs](../src/PPDS.Cli/Infrastructure/Errors/PpdsException.cs#L40)) populated with keys `operation` (string), `activationSource` (string), `sentinelPath` (string, project-root-relative, when sentinel-driven), `sentinelAgeSeconds` (double — total seconds, chosen over `TimeSpan` so JSON-RPC to Extension and MCP tool-error marshalling produce identical wire format).
9. **Guard placement in services.** Every mutation method in every Application Service calls `_guard.EnsureCanMutate(operationDescription)` as its first statement after argument validation. "Mutation method" is defined by verb prefix: `Create*`, `Update*`, `Delete*`, `Remove*`, `Import*`, `Apply*`, `Register*`, `Unregister*`, `Upsert*`, `Publish*`, `Truncate*`, `Drop*`, `Reset*`, `Set*`, `Enable*`, `Disable*`, `Reactivate*`, `Reorder*`, `Add*`. The Bash hook's `_MUTATION_VERBS` list (shell-argv-level verbs) is related but not identical: the service-layer list adds `Upsert`/`Reactivate`/`Reorder`/`Add` which appear as method names but not as CLI verbs. For SQL-path DML (which is not method-name-prefixed), `SqlQueryService.ExecuteAsync` calls the guard explicitly on the DML branch in addition to its existing `DmlSafetyGuard.Check()` call. The complete per-service inventory is enumerated in [§ Mutation Method Inventory](#mutation-method-inventory) below.
10. **Domain service relocation.** All 12 domain services currently in `PPDS.Dataverse/Services/` and `PPDS.Dataverse/Metadata/IMetadataAuthoringService` move into `PPDS.Cli/Services/`: the 6 with mutations (`PluginTrace`, `WebResource`, `EnvironmentVariable`, `Solution`, `MetadataAuthoring`, `Role`) take `IShakedownGuard` as a new constructor parameter; the 6 without mutations (`ImportJob`, `User`, `Flow`, `ConnectionReference`, `DeploymentSettings`, `ComponentNameResolver`) relocate for A1 compliance without adding any dependency. All other method signatures are preserved verbatim — including every `CancellationToken` parameter, every `IProgress<T>` / `IProgressReporter` parameter, and every DTO shape. Constitution R2 (CancellationToken plumbed through the entire async call chain) is explicitly unchanged by the relocation: the token parameters on existing methods remain in the same positions and are passed through to the same downstream calls. Inter-service dependencies (e.g., `DeploymentSettingsService` consumes `IEnvironmentVariableService` + `IConnectionReferenceService`) stay intact — all three relocate together, preserving the dependency direction within the new CLI-hosted domain layer.
11. **PPDS.Dataverse purity.** After the relocation, `PPDS.Dataverse/Services/` contains no types ending in `Service` (i.e., no `FooService` or `IFooService`). `PPDS.Dataverse/Metadata/Authoring/` no longer contains `IMetadataAuthoringService` / `MetadataAuthoringService` / `DataverseMetadataAuthoringService` (DTOs like `CreateTableRequest` and helpers like `SchemaValidator` may remain — they are value types and internal validation primitives, not services). PPDS.Dataverse hosts only infrastructure primitives and DTOs: pool, bulk executor, query executors, metadata query providers, generated entities, model DTOs. PPDS.Dataverse has zero references to `IShakedownGuard`, `ErrorCodes.Safety.*`, or any `PPDS.Cli.*` type (enforced by assembly-reference inspection: `Assembly.GetReferencedAssemblies()` must not include `PPDS.Cli`).
12. **Bash hook preservation.** `.claude/hooks/shakedown-safety.py` is unchanged. The hook still enforces env allowlist gating AND the write-block for Bash `ppds` invocations. Finding #6 (CLI pattern gap) is NOT fixed in the hook by this spec — it is fixed by the guard, which catches the same mutations at a deeper layer.
13. **MCP `--read-only` preservation.** The 14 MCP tool files in `src/PPDS.Mcp/Tools/` that perform a mutation and check `Context.IsReadOnly` today (enumerated at spec-write time: `WebResourcesPublishTool.cs`, `QuerySqlTool.cs`, `MetadataUpdateChoiceTool.cs`, `MetadataUpdateColumnTool.cs`, `MetadataUpdateRelationshipTool.cs`, `MetadataUpdateTableTool.cs`, `PluginTracesDeleteTool.cs`, `MetadataCreateChoiceTool.cs`, `MetadataCreateKeyTool.cs`, `MetadataCreateRelationshipTool.cs`, `MetadataCreateTableTool.cs`, `EnvironmentVariablesSetTool.cs`, `MetadataAddColumnTool.cs`, `MetadataAddOptionValueTool.cs`) each retain their existing `if (Context.IsReadOnly) { ... }` guard (or equivalent throw/return shape) before any Dataverse mutation call. Service-layer guard fires regardless of whether MCP was launched with `--read-only` — these are two independent gates.

### Primary Flows

**Mutation attempted under active shakedown:**

1. User triggers action (CLI command, TUI screen button, MCP tool call, Extension panel action).
2. Surface layer invokes the Application Service method.
3. Service method's first post-validation line: `_guard.EnsureCanMutate("plugins.assembly.register")`.
4. Guard checks cache (≤5s TTL). If cache miss:
   a. Read `PPDS_SHAKEDOWN` env var. If `"1"`, active.
   b. Else resolve project root (`CLAUDE_PROJECT_DIR` > CWD).
   c. Read `{root}/.claude/state/shakedown-active.json`. If present + parseable + `started_at` within 24h, active.
   d. Else inactive.
5. If active: construct `PpdsException` with `Safety.ShakedownActive`, UserMessage citing operation + activation source + bypass hint. Throw.
6. Surface layer catches the exception and renders it via the surface's normal error pipeline (CLI `stderr`, TUI error toast, MCP error response, Extension webview notification).

**Mutation attempted under no shakedown:**

1–3. Same as above.
4. Cache miss: env var absent, sentinel absent. Inactive.
5. `EnsureCanMutate` returns normally.
6. Service delegates to PPDS.Dataverse infrastructure (pool, bulk executor, etc.). Mutation proceeds.

**Stale sentinel self-heal on session start:**

1. Claude Code session starts.
2. `.claude/hooks/session-start-workflow.py` runs. Reads the sentinel; if `started_at` > 24h old, deletes the file.
3. Guard, in its next call, reads no sentinel. Inactive.

(Guard itself never deletes. This flow is owned by the session-start hook — noted here for completeness.)

### Surface-Specific Behavior

#### CLI Surface

- No new CLI flags. The guard is transparent — a user running `ppds metadata table create ...` under `PPDS_SHAKEDOWN=1` sees the usual CLI error pipeline render the `Safety.ShakedownActive` error with the user-facing message. Exit code 1.
- The Bash hook still fires first for `ppds` invocations — a user who hits the hook sees the hook's message, never reaches the C# process. A user who hits the guard sees the guard's message (different wording, same outcome).

#### TUI Surface

- Every TUI screen that invokes a mutation Application Service method already has error-rendering scaffolding for `PpdsException`. Guard errors render the same way.
- No new TUI UI state. Screens do not pre-check guard activation to disable buttons — that would be a second code path, and the failure-on-submit UX is consistent with how every other service error is surfaced. Constitution A2. The interface deliberately exposes no `IsActive()` accessor to prevent this drift.

#### Extension Surface

- Extension's `ppds serve` daemon hosts the same Application Services. RPC handlers in `src/PPDS.Cli/Commands/Serve/Handlers/` already marshal `PpdsException` into JSON-RPC error responses; guard errors ride that same path.
- No new webview UI. Panels handle `Safety.ShakedownActive` identically to any other `PpdsException` from an RPC call.

#### MCP Surface

- MCP tools continue to call the Application Services they already call. The guard fires inside those services, and MCP's existing exception-to-tool-error marshalling surfaces the error to the tool caller.
- MCP's per-process `--read-only` gate remains in place. When both are active, the first check to fire wins in the call path — typically the tool-layer `--read-only` since it runs before the service method is entered. When MCP is NOT launched with `--read-only` but the shakedown sentinel is active, the service-layer guard fires.

### Constraints

- Guard is synchronous. No async I/O. File stat and env var read are fast; async adds complexity for zero benefit.
- Guard logic lives in exactly ONE place. No duplicate checks in surface layers. No helper on command handlers that pre-screens mutations. Constitution A2.
- Guard does NOT enforce the env-allowlist concern. That is still the Bash hook's job (pre-process). Guard only enforces the write-block concern.
- Guard does NOT read `.claude/settings.json`. Activation is fixed: env var name `PPDS_SHAKEDOWN` and sentinel path `.claude/state/shakedown-active.json`. The Bash hook's `safety.readonly_env_var` configurability is not ported; the defaults have shipped in every release and there is no known user who has customized them.
- Guard throws, never returns an error code, and exposes no non-throwing accessor. Every consumer uses `EnsureCanMutate` at the method boundary. If a consumer ever needs a non-throwing check (none is expected), they wrap `EnsureCanMutate` in `try/catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Safety.ShakedownActive)` — discouraged, but available as an escape hatch without widening the interface.

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `operationDescription` (parameter) | Non-null, non-empty string. Convention: dot-delimited lowercase (`plugins.assembly.register`). | `ArgumentException` at guard entry — a bug in the calling service, not a user error. |
| `started_at` in sentinel JSON | ISO-8601 UTC timestamp. | Parse failure → sentinel treated as absent (fail-open), warning logged. |

---

## Acceptance Criteria

Constitution I6 requires every AC to have a corresponding passing test before implementation is complete. Every row below names a specific test method in `tests/PPDS.Cli.Tests/`. `Status: 🔲` indicates "not yet implemented" per the template legend; rows flip to `✅` when the named test exists and passes.

A separate **Re-validation Plan** section below the AC table lists live end-to-end checks that are not unit-testable and are therefore not ACs — they are the exit criteria for declaring the finding-closure successful, executed by a `/shakedown` re-run post-merge.

### Guard behavior

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `IShakedownGuard` interface declares exactly one public method: `void EnsureCanMutate(string operationDescription)`. No `IsActive` or other accessors. Interface type resides in `PPDS.Cli.Infrastructure.Safety`. `ShakedownGuard` is the default implementation, registered as singleton inside `AddCliApplicationServices`. | `ShakedownGuardInterfaceTests.Interface_Has_OnlyEnsureCanMutate` | 🔲 |
| AC-02 | `EnsureCanMutate` throws `PpdsException` with `ErrorCode == ErrorCodes.Safety.ShakedownActive` when `PPDS_SHAKEDOWN=1`. | `ShakedownGuardTests.Block_When_EnvVarEqualsOne` | 🔲 |
| AC-03 | `EnsureCanMutate` throws when `.claude/state/shakedown-active.json` contains `started_at` within the last 24 hours (UTC). | `ShakedownGuardTests.Block_When_FreshSentinelPresent` | 🔲 |
| AC-04 | Sentinel with `started_at` older than 24 hours: guard does NOT throw, does NOT delete the file, and logs no warning. | `ShakedownGuardTests.Allow_When_StaleSentinel_NoSideEffects` | 🔲 |
| AC-05 | Guard is inactive (no throw) when env var is unset (or any value other than literal `"1"`) AND sentinel file does not exist. | `ShakedownGuardTests.Allow_When_NoSignalsPresent` | 🔲 |
| AC-06 | Corrupt sentinel JSON (unparseable, empty, missing `started_at`, unparseable timestamp) → treated as absent (fail-open) + `Warning`-level log entry emitted. | `ShakedownGuardTests.Allow_And_Warn_When_SentinelCorrupt` | 🔲 |
| AC-07 | Sentinel path resolution: tries `CLAUDE_PROJECT_DIR` env var first; falls back to `Directory.GetCurrentDirectory()`. When `CLAUDE_PROJECT_DIR` is set to a non-empty value but the directory does not exist, guard falls back to CWD AND emits a `Warning`-level log entry naming the stale path. | `ShakedownGuardTests.ProjectRoot_ResolvesInExpectedOrder` (parameterized over three environments: env-var-unset, env-var-set-existing, env-var-set-stale; third variant asserts both the CWD fallback AND the warning emission) | 🔲 |
| AC-08 | `ErrorCodes.Safety.ShakedownActive` constant equals `"Safety.ShakedownActive"`. Defined under a new nested class `Safety` inside `PPDS.Cli.Infrastructure.Errors.ErrorCodes`. | `ErrorCodesTests.Safety_ShakedownActive_HasExpectedValue` | 🔲 |
| AC-09 | Thrown exception: `UserMessage` contains the operation descriptor, the activation source string (`env:PPDS_SHAKEDOWN` OR `sentinel:<project-root-relative-path>`), and bypass instructions. `Context` dictionary has keys: `operation` (string), `activationSource` (string), and — when sentinel-driven — `sentinelPath` (project-root-relative string) and `sentinelAgeSeconds` (double total seconds). | `ShakedownGuardTests.ExceptionShape_MatchesSpec` (two test variants: env-driven, sentinel-driven) | 🔲 |
| AC-10 | 1000 concurrent calls to `EnsureCanMutate` across 50 threads produce consistent results (all throw OR all return) with no `IOException` or locking-related failures. | `ShakedownGuardTests.Concurrent_Calls_AreConsistent` | 🔲 |
| AC-11 | Cache TTL: after the first resolution, subsequent calls within 5 seconds do not restat the sentinel file. Measured via a spy `IFileSystem`. | `ShakedownGuardTests.Cache_TTL_SuppressesRepeatedFileStats` | 🔲 |
| AC-12 | Sentinel file locked for exclusive write by another process at the moment of a guard read: guard catches `IOException`, treats as absent (fail-open), logs warning. | `ShakedownGuardTests.Allow_And_Warn_When_SentinelLockedForWrite` | 🔲 |
| AC-13 | For each of the 8 truthy-string values enumerated in the Edge Cases table (`"true"`, `"True"`, `"TRUE"`, `"yes"`, `"Yes"`, `"YES"`, `"on"`, `"ON"`), guard treats the env var as inactive AND emits a `Warning`-level log entry naming the value. For any other non-`"1"` value (empty string, `"0"`, `"2"`, `"garbage"`), guard is inactive AND emits no warning. | `ShakedownGuardTests.Warn_When_EnvVar_IsTruthyNonOne` — parameterized `[Theory]` with two row sets: 8 rows asserting warning emission (one per allowlist value), and at least 4 rows asserting no warning for other non-`"1"` values | 🔲 |

### Service relocation (constitution A1 fix)

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-14 | `IPluginTraceService` / `PluginTraceService` reside in assembly `PPDS.Cli`, namespace `PPDS.Cli.Services.PluginTraces`. | `ArchitectureTests.PluginTraceService_LivesInCliAssembly` (uses `typeof(IPluginTraceService).Assembly.GetName().Name`) | 🔲 |
| AC-15 | `IWebResourceService` / `WebResourceService` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.WebResources`. | `ArchitectureTests.WebResourceService_LivesInCliAssembly` | 🔲 |
| AC-16 | `IEnvironmentVariableService` / `EnvironmentVariableService` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.EnvironmentVariables`. | `ArchitectureTests.EnvironmentVariableService_LivesInCliAssembly` | 🔲 |
| AC-17 | `ISolutionService` / `SolutionService` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.Solutions`. | `ArchitectureTests.SolutionService_LivesInCliAssembly` | 🔲 |
| AC-18 | `IImportJobService` / `ImportJobService` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.ImportJobs`. | `ArchitectureTests.ImportJobService_LivesInCliAssembly` | 🔲 |
| AC-19 | `IMetadataAuthoringService` / `MetadataAuthoringService` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.Metadata.Authoring`. | `ArchitectureTests.MetadataAuthoringService_LivesInCliAssembly` | 🔲 |
| AC-20 | `IUserService` / `UserService` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.Users`. | `ArchitectureTests.UserService_LivesInCliAssembly` | 🔲 |
| AC-21 | `IRoleService` / `RoleService` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.Roles`. | `ArchitectureTests.RoleService_LivesInCliAssembly` | 🔲 |
| AC-22 | `IFlowService` / `FlowService` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.Flows`. | `ArchitectureTests.FlowService_LivesInCliAssembly` | 🔲 |
| AC-23 | `IConnectionReferenceService` / `ConnectionReferenceService` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.ConnectionReferences`. | `ArchitectureTests.ConnectionReferenceService_LivesInCliAssembly` | 🔲 |
| AC-24 | `IDeploymentSettingsService` / `DeploymentSettingsService` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.DeploymentSettings`. | `ArchitectureTests.DeploymentSettingsService_LivesInCliAssembly` | 🔲 |
| AC-25 | `IComponentNameResolver` / `ComponentNameResolver` reside in `PPDS.Cli`, namespace `PPDS.Cli.Services.SolutionComponents`. | `ArchitectureTests.ComponentNameResolver_LivesInCliAssembly` | 🔲 |
| AC-26 | `PPDS.Dataverse` assembly contains zero types named or ending in `Service` or `IService` shape inside `PPDS.Dataverse.Services` namespace. `PPDS.Dataverse.Metadata.Authoring` no longer contains `IMetadataAuthoringService` / `MetadataAuthoringService` / `DataverseMetadataAuthoringService` (DTOs and `SchemaValidator` may remain). Assembly-reference inspection: `typeof(IDataverseConnectionPool).Assembly.GetReferencedAssemblies()` does NOT include `PPDS.Cli`. | `ArchitectureTests.Dataverse_NoDomainServicesOrCliReferences` — enumerates 12 forbidden type names (`IPluginTraceService`, `PluginTraceService`, ...) and 12 forbidden impls and asserts `typeof(...).Assembly` on each throws or resolves to `PPDS.Cli`; asserts `typeof(IDataverseConnectionPool).Assembly` has no `PPDS.Cli` reference | 🔲 |
| AC-27 | `src/PPDS.Dataverse/CHANGELOG.md` has a new entry under the `## Unreleased` heading (created if absent — this project's CHANGELOG convention is Keep-a-Changelog with `## Unreleased` at the top) describing the relocation as a **breaking change** and listing all 12 moved interface type names (`IPluginTraceService`, `IWebResourceService`, `IEnvironmentVariableService`, `ISolutionService`, `IImportJobService`, `IMetadataAuthoringService`, `IUserService`, `IRoleService`, `IFlowService`, `IConnectionReferenceService`, `IDeploymentSettingsService`, `IComponentNameResolver`). | `ChangelogTests.Dataverse_Changelog_DocumentsRelocation` — parses the CHANGELOG, asserts `## Unreleased` heading presence, asserts all 12 interface names are present in that section, asserts the word "breaking" (case-insensitive) appears in that section | 🔲 |

### Mutation method coverage

Each service below: a parameterized test enumerates every mutation method listed in [§ Mutation Method Inventory](#mutation-method-inventory) for that service and asserts `PpdsException(Safety.ShakedownActive)` is thrown when the guard is active (`FakeShakedownGuard` configured to throw). The test's `[Theory]` row count MUST equal the count in the inventory table (regression guard: adding a method to the service without updating the inventory or test fails the test).

| ID | Service | Method count | Test | Status |
|----|---------|--------------|------|--------|
| AC-28 | `PluginRegistrationService` | 18 | `PluginRegistrationServiceGuardTests.EveryMutationMethod_Blocks (18 rows)` | 🔲 |
| AC-29 | `ServiceEndpointService` | 4 | `ServiceEndpointServiceGuardTests.EveryMutationMethod_Blocks (4 rows)` | 🔲 |
| AC-30 | `CustomApiService` | 7 | `CustomApiServiceGuardTests.EveryMutationMethod_Blocks (7 rows)` | 🔲 |
| AC-31 | `DataProviderService` | 5 | `DataProviderServiceGuardTests.EveryMutationMethod_Blocks (5 rows)` | 🔲 |
| AC-32 | `PluginTraceService` | 6 | `PluginTraceServiceGuardTests.EveryMutationMethod_Blocks (6 rows)` | 🔲 |
| AC-33 | `WebResourceService` | 3 | `WebResourceServiceGuardTests.EveryMutationMethod_Blocks (3 rows)` | 🔲 |
| AC-34 | `EnvironmentVariableService` | 1 | `EnvironmentVariableServiceGuardTests.SetValueAsync_Blocks` | 🔲 |
| AC-35 | `SolutionService` | 2 | `SolutionServiceGuardTests.EveryMutationMethod_Blocks (2 rows)` | 🔲 |
| AC-36 | `MetadataAuthoringService` | 21 | `MetadataAuthoringServiceGuardTests.EveryMutationMethod_Blocks (21 rows)` | 🔲 |
| AC-37 | `RoleService` | 2 | `RoleServiceGuardTests.EveryMutationMethod_Blocks (2 rows: AssignRoleAsync, RemoveRoleAsync)` | 🔲 |
| AC-38 | `SqlQueryService` — DML branch fires guard in addition to `DmlSafetyGuard`. Guard call is placed inside `ExecuteAsync` (public entry point), after `PrepareExecutionAsync` returns, gated on `safetyResult != null && !safetyResult.IsDryRun` — i.e., it fires only when DML will actually execute (not on SELECT, not on dry-run). Non-DML path does NOT fire guard. | `SqlQueryServiceGuardTests.DmlBranch_Blocks_AndSelectBranch_DoesNot` (two test rows: one DML, one SELECT, one DML-dry-run — dry-run does NOT fire) | 🔲 |

### Defense in depth (preservation)

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-39 | `.claude/hooks/shakedown-safety.py`'s mutation-verb list and block logic are unchanged. Asserted by a C# test in `tests/PPDS.Cli.Tests/Preservation/` that reads the hook file as text and verifies: (a) the `_MUTATION_VERBS` set literal contents match the expected baseline (`create, update, delete, remove, import, apply, register, unregister, publish, truncate, drop, reset, set`), (b) the `_NAMED_MUTATIONS` dict contains the key `("plugins", "deploy")`, and (c) the file contains the BLOCK message string `"BLOCKED [shakedown-safety/readonly]"`. No Python test harness needed — this is a text-level regression guard readable from xUnit. | `ShakedownSafetyHookPreservationTests.MutationVerbsAndBlockLogic_Unchanged` | 🔲 |
| AC-40 | Each of the 14 MCP tool files enumerated in Core Requirement #13 contains at least one source-level occurrence matching the regex `\bContext\.IsReadOnly\b` followed by either a `throw` or `return` within the next 5 lines. The test reads each enumerated file as text and asserts the pattern. Adding a new mutation tool that forgets the check fails this test only after that tool is added to the enumeration (intentional — the enumeration is the spec's authoritative list of pre-spec `--read-only`-gated tools; a follow-up spec would be needed to add new tools to the preservation set). | `McpReadOnlyPreservationTests.EnumeratedTools_StillCheckIsReadOnly (14 rows, [Theory])` | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

## Re-validation Plan

These are live, multi-surface end-to-end checks that exit-criterion the spec's finding-closure goal. Not ACs — they cannot be asserted by a unit test because they require a live Dataverse environment, a running TUI, a loaded Extension, and a running MCP server. Executed once post-merge as part of a `/shakedown` re-run on the allowlisted dev environment.

| RV-ID | Scenario | Expected outcome |
|-------|----------|------------------|
| RV-01 | With sentinel file fresh, run `ppds metadata table create --schemaName foo_test ...` | Blocks with `Safety.ShakedownActive`. Finding #6 closed. |
| RV-02 | With sentinel file fresh, open the TUI plugin-traces screen, select a trace, trigger delete | Blocks with `Safety.ShakedownActive` rendered in the TUI error pipeline. Finding #23 closed. |
| RV-03 | With sentinel file fresh, open the Extension Plugin Traces panel, select a trace, trigger delete | Blocks with `Safety.ShakedownActive` in the Extension error toast. Finding #37 closed. |
| RV-04 | With sentinel file fresh, open the Extension Web Resources panel, select a resource, trigger publish | Blocks with `Safety.ShakedownActive`. Second Finding #37 surface closed. |
| RV-05 | With sentinel file fresh, run `ppds plugins register assembly ...` | Blocks at the Bash hook layer (hook's pre-spec coverage not regressed). |
| RV-06 | With sentinel fresh + `PPDS_SHAKEDOWN=1` unset + sentinel removed, run any previously-blocked mutation | Succeeds (or fails for unrelated reasons). Confirms the guard does not block when both activation sources are absent. |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Sentinel file exists but is empty (0 bytes) | `shakedown-active.json` is empty | Treated as corrupt → absent. Warning logged. Guard returns inactive. |
| Sentinel file exists but is a directory | Stat returns directory, not file | Treated as absent. No exception. |
| Sentinel `started_at` is in the future | Timestamp 3h ahead of now | Treated as fresh (within 24h window, age is -3h). Guard blocks. The 24h window is defined as `|now - started_at| ≤ 24h`, clock skew tolerated in both directions. |
| `CLAUDE_PROJECT_DIR` points to a nonexistent directory | Env var set but dir missing | Fall back to `Directory.GetCurrentDirectory()`. If that also lacks `.claude/state/`, guard reads no sentinel → inactive (env var path still evaluated). |
| `PPDS_SHAKEDOWN=0` | Env var set to literal `"0"` | Treated as inactive. No warning (explicit opt-out — `"0"` is a conventional off-value). |
| `PPDS_SHAKEDOWN` set to one of `"true"`, `"True"`, `"TRUE"`, `"yes"`, `"Yes"`, `"YES"`, `"on"`, `"ON"` (case-sensitive match against this fixed allowlist) | Truthy-looking non-`"1"` value | Treated as inactive. Warning logged: `"PPDS_SHAKEDOWN={value}" was set but only "1" activates the shakedown guard; did you mean to set it to 1?`. The allowlist is fixed; any other non-`"1"` value (empty string, garbage, `"2"`, etc.) is silently inactive. |
| Sentinel file held open for exclusive write by another process | `File.Open` throws `IOException` / `UnauthorizedAccessException` | Caught. Treated as absent (fail-open). Warning logged. Matches corrupt-sentinel behavior — env-var path still catches a genuinely-active shakedown. |
| Service throws `OperationCanceledException` from an operation that already called `EnsureCanMutate` | Guard was called, passed, then mutation cancelled mid-flight | Unrelated. Guard only throws at entry; cancellation is a separate concern. |
| Guard is called from a test that sets `PPDS_SHAKEDOWN=1` in its setup | Unit test intentionally activating guard | Guard honors env var. Tests MUST use the test-injectable `IShakedownGuard` fake for negative-path tests, not real env manipulation. |
| Sentinel file is actively being written by the shakedown skill when guard reads it | Partial JSON | Parse fails → treated as absent (fail-open). Next call after write completes will block. Acceptable race — shakedown skill writes the sentinel ~0.1s after user opts in, and the operator is not mutating in that window. |

### Test Examples

```csharp
[Fact]
public void EnsureCanMutate_Throws_WhenPpdsShakedownEnvVarSet()
{
    // Arrange
    var clock = new FakeClock(DateTimeOffset.UtcNow);
    var env = new FakeEnvironment { ["PPDS_SHAKEDOWN"] = "1" };
    var fs = new FakeFileSystem();  // no sentinel file
    var guard = new ShakedownGuard(env, fs, clock, NullLogger<ShakedownGuard>.Instance);

    // Act + Assert
    var ex = Assert.Throws<PpdsException>(() => guard.EnsureCanMutate("plugins.assembly.register"));
    Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
    Assert.Contains("plugins.assembly.register", ex.UserMessage);
    Assert.Contains("env:PPDS_SHAKEDOWN", ex.UserMessage);
    Assert.Equal("env:PPDS_SHAKEDOWN", ex.Context!["activationSource"]);
}

[Fact]
public async Task PluginTraceService_Delete_Throws_WhenGuardActive()
{
    var fakeGuard = new FakeShakedownGuard { Active = true };
    var service = new PluginTraceService(pool, fakeGuard, logger);

    var ex = await Assert.ThrowsAsync<PpdsException>(
        () => service.DeleteAsync(Guid.NewGuid(), CancellationToken.None));

    Assert.Equal(ErrorCodes.Safety.ShakedownActive, ex.ErrorCode);
    Assert.Equal("plugintraces.delete", ex.Context!["operation"]);
}
```

---

## Core Types

### IShakedownGuard

The domain-layer safety primitive. Every mutation service takes this as a constructor dependency and calls `EnsureCanMutate` at the top of every mutation method.

```csharp
namespace PPDS.Cli.Infrastructure.Safety;

public interface IShakedownGuard
{
    /// <summary>
    /// Throws <see cref="PpdsException"/> with code
    /// <see cref="ErrorCodes.Safety.ShakedownActive"/> when a shakedown is
    /// active. Returns normally when inactive. Internally cached ≤5s.
    /// </summary>
    /// <param name="operationDescription">Dot-delimited lowercase identifier
    /// for the blocked operation (e.g., "plugins.assembly.upsert"). Used
    /// in the exception's UserMessage and Context["operation"].</param>
    void EnsureCanMutate(string operationDescription);
}
```

### ShakedownGuard (implementation)

Reads env var + sentinel. Thread-safe singleton. Short TTL cache.

```csharp
public sealed class ShakedownGuard : IShakedownGuard
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(24);
    private const string EnvVarName = "PPDS_SHAKEDOWN";
    private const string SentinelRelPath = ".claude/state/shakedown-active.json";

    private readonly IEnvironment _env;
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly ILogger<ShakedownGuard> _log;

    // Activation state is cached; re-resolved on TTL expiry.
    private (DateTimeOffset resolvedAt, ActivationState state) _cache;
    private readonly object _gate = new();

    public ShakedownGuard(IEnvironment env, IFileSystem fs, IClock clock, ILogger<ShakedownGuard> log) { ... }

    public void EnsureCanMutate(string operationDescription)
    {
        if (string.IsNullOrWhiteSpace(operationDescription))
            throw new ArgumentException("operationDescription required.", nameof(operationDescription));

        // GetState() acquires _gate internally — all cache reads/writes
        // are serialized. No torn reads on concurrent callers.
        var state = GetState();
        if (!state.IsActive) return;

        // sentinelPath is stored in project-root-relative form (not absolute)
        // so diagnostics do not leak filesystem layout.
        var ctx = new Dictionary<string, object>
        {
            ["operation"] = operationDescription,
            ["activationSource"] = state.Source,  // "env:PPDS_SHAKEDOWN" or "sentinel:<rel-path>"
        };
        if (state.SentinelRelativePath is not null)
            ctx["sentinelPath"] = state.SentinelRelativePath;
        if (state.SentinelAge.HasValue)
            ctx["sentinelAgeSeconds"] = state.SentinelAge.Value.TotalSeconds;  // double, not TimeSpan

        throw new PpdsException(
            ErrorCodes.Safety.ShakedownActive,
            BuildUserMessage(operationDescription, state),
            ctx);
    }

    // ... private GetState (uses _gate), ResolveFromEnvAndSentinel,
    //     ReadSentinel, BuildUserMessage omitted in spec ...
}
```

### ErrorCodes.Safety

New nested class in `ErrorCodes`. Single constant today; the category exists so future safety primitives can land without code-structure churn.

```csharp
public static class Safety
{
    /// <summary>
    /// A mutation was refused because a shakedown session is active.
    /// Bypass: unset PPDS_SHAKEDOWN and/or remove the sentinel file.
    /// </summary>
    public const string ShakedownActive = "Safety.ShakedownActive";
}
```

### Sentinel Schema

Read-only consumer — the guard never writes this. Schema documented here for cross-reference with the shakedown skill's writer.

```json
{
  "started_at": "2026-04-20T14:15:00Z",
  "scope": "v1.0.0-shakedown",
  "session_id": "optional-correlation-id"
}
```

Required field: `started_at` (ISO-8601 UTC). All other fields ignored by the guard. Parse failure or missing `started_at` → treated as absent.

### Usage Pattern

```csharp
public sealed class PluginTraceService : IPluginTraceService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly IShakedownGuard _guard;
    private readonly ILogger<PluginTraceService> _log;

    public PluginTraceService(IDataverseConnectionPool pool, IShakedownGuard guard, ILogger<PluginTraceService> log)
    {
        _pool = pool;
        _guard = guard;
        _log = log;
    }

    public async Task<bool> DeleteAsync(Guid traceId, CancellationToken ct)
    {
        _guard.EnsureCanMutate("plugintraces.delete");
        // ... Dataverse delete via pool ...
    }

    public async Task<int> DeleteOlderThanAsync(TimeSpan age, IProgress<int>? progress, CancellationToken ct)
    {
        _guard.EnsureCanMutate("plugintraces.deleteOlderThan");
        // ... Dataverse bulk delete via pool ...
    }
}
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `PpdsException(Safety.ShakedownActive)` | Guard is active; mutation attempted. | Bypass: `unset PPDS_SHAKEDOWN` and/or remove `.claude/state/shakedown-active.json`. No programmatic recovery inside the service call — the operator must change environmental state. |
| `ArgumentException` from `EnsureCanMutate` | `operationDescription` was null or empty. Bug in the calling service. | Fix the calling service. Not a user-facing condition. |

### Recovery Strategies

- **Shakedown block**: The operator deliberately chose to be in shakedown mode. The bypass requires the same deliberate intent. Do not offer silent retry, confirmation prompts, or per-call bypass flags. The block is the feature.
- **Corrupt sentinel**: Fail-open + warning log. Operators noticing the warning can investigate; the env var path still catches a genuinely-active shakedown.

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Guard called on a non-mutation method by mistake | Returns normally when inactive; throws when active. This is not strictly "wrong" — the method just never mutates — but it's surprising. Reviewer catches misplaced guard calls. |
| Bulk operation calls `EnsureCanMutate` 1000 times in a loop | Cache TTL (≤5s) absorbs all but the first call. Effective cost: ~1 file stat per 5 seconds. |
| Concurrent mutations from parallel pooled connections | Each thread sees the cached state. If the cache expires mid-batch, re-resolution is serialized by `_gate`. No torn reads. |

---

## Design Decisions

### Why a service-layer guard instead of per-surface checks?

**Context:** The existing Bash PreToolUse hook runs at the process boundary. The v1.0.0 shakedown proved this layer is wrong: TUI and daemon bypass new-shell spawning entirely, and the hook's CLI argv parsing has real pattern gaps (finding #6).

**Decision:** Put the guard in the Application Services layer — the single code path every surface traverses (constitution A2).

**Alternatives considered:**
- **Keep extending the hook.** Rejected: cannot reach in-process TUI or daemon RPC, so it structurally cannot close findings #23 and #37 regardless of how well it handles #6.
- **Per-surface guards** (one in CLI command handlers, one in TUI screens, one in daemon RPC handlers, one in MCP tool handlers). Rejected: four copies of the same check, drift risk, constitution A2 violation.
- **Guard inside PPDS.Dataverse.** Rejected: leaks PPDS policy into the reusable infrastructure library; NuGet consumers would inherit shakedown semantics they don't want.

**Consequences:**
- Positive: one check, one test harness, every surface covered. Scales to future surfaces (gRPC, REST, whatever).
- Negative: requires the domain services to actually live in the domain layer, which exposes a pre-existing A1 violation (six services in `PPDS.Dataverse` that should be in `PPDS.Cli/Services/`). This spec fixes that — which is extra work, but right work.

### Why relocate 12 services from PPDS.Dataverse to PPDS.Cli.Services?

**Context:** The guard needs to sit at the domain layer (between the infrastructure library and the presentation surfaces). The initial shakedown findings pointed at six services hosting mutations we needed to cover (`PluginTrace`, `WebResource`, `EnvironmentVariable`, `Solution`, `ImportJob`, `MetadataAuthoring`). Plan-phase analysis revealed PPDS.Dataverse's `RegisterDataverseServices` extension method actually registers 12 domain services, and that `DeploymentSettingsService` (not in the original six) depends transitively on `IEnvironmentVariableService` (which IS moving) + `IConnectionReferenceService` (which wasn't). Leaving some behind forces either a circular reference from Dataverse to Cli or an interface shim — both worse than a full relocation.

**Decision:** Move all 12 services wholesale into `PPDS.Cli/Services/`. The 6 with mutations (`PluginTrace`, `WebResource`, `EnvironmentVariable`, `Solution`, `MetadataAuthoring`, `Role`) gain `IShakedownGuard` as a constructor parameter; the 6 without (`ImportJob`, `User`, `Flow`, `ConnectionReference`, `DeploymentSettings`, `ComponentNameResolver`) relocate for A1 compliance only. PPDS.Dataverse shrinks to pure infrastructure (pool, query primitives, bulk executors, metadata query, generated entities, DTOs, internal validators like `SchemaValidator`). Call sites update their `using` directives. Interface names and method signatures are preserved.

**Alternatives considered:**
- **Wrapper facades in PPDS.Cli that delegate to PPDS.Dataverse services** (Option X from the design conversation). Rejected after analysis: more LOC written, more interface noise, and leaves PPDS.Dataverse with unused-by-PPDS services that confuse future readers.
- **Add `IShakedownGuard` as an optional dependency inside PPDS.Dataverse services with null-default for NuGet consumers** (leak-via-escape-hatch). Rejected: still leaks the concept into the library, and the `guard = guard ?? NoOp` pattern is a code smell that says "this doesn't belong here."
- **Leave services where they are and skip those surfaces.** Rejected: fails the finding-closure goal (plugin traces delete and web resources publish are exactly the surfaces the Extension bypassed).

**Consequences:**
- Positive: Constitution A1 compliance in one coherent PR. PPDS.Dataverse becomes a cleaner library contract. Future cross-cutting concerns (audit log, metrics, tracing) have a natural insertion layer.
- Negative: Breaking change to `PPDS.Dataverse` NuGet package surface. Any external consumer relying on any of the 12 relocated interfaces or their implementations will see compile errors on upgrade (the types and their namespaces move to the `PPDS.Cli` assembly, which is published as a separate CLI tool package, not a library). `RegisterDataverseServices` itself also becomes a smaller extension method — consumers calling it will still get pool + metadata query + bulk primitives, but none of the 12 domain services. Mitigated by a prominent CHANGELOG entry in `PPDS.Dataverse` under `## Unreleased` listing all 12 moved types. External-consumer impact is assumed low (`PPDS.Dataverse` is primarily a PPDS internal library; no published documentation advertises these services as a consumer API); if this assumption turns out wrong, a follow-up can re-export shims from `PPDS.Dataverse`.
- Negative: Bigger PR. Managed via phased plan — relocation is one phase, guard wiring is the next, and each is independently verifiable.

### Why CWD + CLAUDE_PROJECT_DIR (mirror of hook) instead of walk-up or env-var-pointed?

**Context:** Application Services need a sentinel path that works from CLI one-shots, long-lived TUI, long-lived daemon, and MCP server processes — all with potentially different CWDs.

**Decision:** Resolve project root as first-non-empty of `CLAUDE_PROJECT_DIR` then `Directory.GetCurrentDirectory()`. Match `.claude/hooks/shakedown-safety.py::_project_dir()` verbatim.

**Alternatives considered:**
- **Walk up from CWD looking for `.claude/`**, like git's `.git/` discovery. Rejected: adds I/O on every guard call (even with TTL cache), and `CLAUDE_PROJECT_DIR` already handles nested-CWD cases during Claude Code sessions. Manual CLI use from the repo root (the 99% case) is covered by CWD.
- **New env var `PPDS_SHAKEDOWN_SENTINEL`** pointing at the file directly. Rejected: adds a knob nobody needs, and creates a third activation axis to document and test.
- **Well-known per-user path** (`%LOCALAPPDATA%/PPDS/shakedown-active.json`). Rejected: shakedown is per-repo, not per-user. A user running two shakedowns in two clones would share state. Wrong model.

**Consequences:**
- Positive: Hook and guard are a pair. They always agree on where the sentinel lives. A bug in one is a bug in both and gets fixed once.
- Negative: An end-user of PPDS installed as a global dotnet tool, running outside any PPDS repo, has neither `CLAUDE_PROJECT_DIR` nor a `.claude/state/` in CWD → guard correctly no-ops. This is the intended behavior; end users are unaffected by the shakedown primitive.

### Why an explicit `EnsureCanMutate` call instead of attribute + interceptor?

**Context:** Every mutation method needs a guard call. Three mechanisms available: explicit call (`_guard.EnsureCanMutate("...")`), attribute (`[ShakedownGuarded]`) + DI interceptor, or Roslyn analyzer requiring the explicit call on mutation-shaped methods.

**Decision:** Explicit call at the top of each method. Same pattern as `DmlSafetyGuard.Check()` in `SqlQueryService`.

**Alternatives considered:**
- **Attribute + Castle/DispatchProxy interceptor.** Rejected: introduces runtime reflection to a codebase that does not otherwise use it; adds a dependency and a build step for one feature; creates debugging friction (stack traces go through the proxy).
- **Roslyn analyzer enforcing the explicit call.** Promising but deferred. PPDS has an analyzer project, but building a correct "is this method a mutation" analyzer handles edge cases (overloads, interface impls, generic methods, async state machines) that take real time. Filed as a follow-up hardening task.

**Consequences:**
- Positive: Zero runtime magic. Grep `EnsureCanMutate` finds every protected call site. Reviewers can see the call. Failure mode on forgetting the call is the same as attribute-based approaches pending analyzer.
- Negative: Manual discipline required. Every new mutation method must remember the guard call. Mitigated by the re-validation test matrix + reviewer checklist.

### Why a 24-hour self-heal window?

**Context:** The sentinel file can be orphaned — an operator crashes out of a `/shakedown` session, forgets to run the cleanup skill, or closes their terminal. If the file had no expiry, a forgotten sentinel from weeks ago would permanently block writes from a fresh shell. We need a staleness threshold.

**Decision:** 24 hours from `started_at`.

**Rationale:**
- Realistic shakedown durations run from ~30 minutes (quick single-surface kick) to ~4 hours (full multi-surface product validation across Extension/TUI/MCP/CLI with parity audit). 24h is 6× the upper bound — comfortable margin for a genuinely long session that spans a coffee break, a debugging detour, or a lunch — while still self-healing within one working day.
- The threshold is paired with the session-start hook's active cleanup: a fresh Claude Code session sees any >24h sentinel and deletes it. Guard's own 24h check is the safety net for processes that don't go through session-start (a long-lived daemon launched on Monday and still running Tuesday).
- Shorter thresholds (1h, 4h, 8h) would false-positive on normal long shakedowns. Longer thresholds (7d, 30d) would leave a write-block hole open across a long weekend.

**Alternatives considered:**
- **No expiry** — forgotten sentinel is a permanent lock. Rejected: the shakedown skill has occasional UX friction (kill-9 on the claude process, ctrl-c mid-phase) where cleanup doesn't fire; a permanent lock punishes the next session.
- **Configurable via `.claude/settings.json`** — parallel to the hook's `safety.readonly_env_var`. Rejected for v1: adds a knob, no known operator demand. Can be added later if a shakedown duration ever exceeds 24h.
- **mtime-based instead of `started_at`-based** — simpler (no JSON parse needed for the expiry check). Rejected: sentinel mtime updates when anything rewrites the file (even a no-op save), breaking the "fresh" semantics. `started_at` is monotonic.

**Consequences:**
- Positive: Orphaned sentinels self-heal within one working day. Operators never need to remember manual cleanup.
- Negative: A legitimately >24h shakedown (vanishingly rare) appears to end early. Mitigation: the operator can re-arm with `PPDS_SHAKEDOWN=1` or a fresh sentinel write.

### Why fail-open on corrupt sentinel?

**Context:** The sentinel file could be corrupt (partial write, unrelated tool clobbered it, operator hand-edited and fat-fingered). We must decide: fail-safe (block) or fail-open (allow).

**Decision:** Fail-open, with a warning log. Guard treats unparseable sentinel as absent.

**Rationale:**
- A corrupt sentinel is more plausibly an unrelated project artifact than a genuine but malformed shakedown marker.
- The env var path is an independent activation axis. If a shakedown is genuinely active, the operator set `PPDS_SHAKEDOWN=1` — that path is unaffected by sentinel corruption.
- Fail-safe would create false positives for users whose unrelated `.claude/` tooling happens to write a file at the same path, permanently locking all mutations until they investigate.

**Alternatives considered:**
- **Fail-safe (block).** Rejected for false-positive risk as above.
- **Crash on corrupt sentinel.** Rejected: punishes unrelated usage. The warning log + fail-open gives operators a diagnostic without breaking anything.

**Consequences:**
- Positive: Guard behavior matches operator expectations — a corrupt/irrelevant file doesn't brick the CLI.
- Negative: In a pathological race (sentinel is being rewritten at the exact moment of a mutation), the window of "appears absent" is whatever it takes for the writer to finish — ~10ms. Operators are not actively mutating in that 10ms window during a shakedown.

### Why keep the Bash hook (defense in depth)?

**Context:** The guard catches every mutation at the service layer. Arguably the hook is now redundant.

**Decision:** Keep the Bash hook unchanged.

**Rationale:**
- The hook catches misuse BEFORE the C# process starts. Faster feedback, lower resource cost. A user pasting a destructive command gets an instant reject, not a reject after authentication + pool warmup.
- The hook enforces a second concern the guard does not: env allowlist gating. That's orthogonal to the write-block and must stay somewhere.
- Defense in depth is cheap here. The hook file is ~550 lines and works; removing it to save nothing is anti-value.

**Consequences:**
- Positive: Two layers of protection. A bug in one still leaves the other. Finding #6 (CLI pattern gap) remains a hook-layer bug worth fixing eventually, but it no longer leaves a real hole because the guard covers the same mutations deeper.
- Negative: Two code paths to reason about. The guard spec notes this explicitly so readers aren't confused.

---

## Related Specs

- [architecture.md](./architecture.md) — A1/A2 layering that this spec realizes.
- [query.md](./query.md) — `DmlSafetyGuard` pattern that `IShakedownGuard` echoes. DML path calls both.
- [retro-filing.md](./retro-filing.md) — different consumer of `PPDS_SHAKEDOWN=1` (suppresses issue filing during shakedown). No interaction with the guard; documented here so readers know the env var has multiple observers.
- [metadata-authoring.md](./metadata-authoring.md) — home of the 18 noun-verb-noun forms that finding #6 identified as gaps.
- [plugin-traces.md](./plugin-traces.md), [web-resources.md](./web-resources.md), [plugins.md](./plugins.md), [solutions.md](./solutions.md), [import-jobs.md](./import-jobs.md), [environment-variables.md](./environment-variables.md) — domain specs whose services gain guard calls (and whose six are relocated).

---

## Changelog

| Date | Change |
|------|--------|
| 2026-04-20 | Initial spec — v1.0.0 shakedown finding closure (#6, #23, #37). |
| 2026-04-20 | Revised per first-pass review: added Mutation Method Inventory with concrete per-service method lists + counts; dropped `IsActive()` from the interface contract; moved live re-validation checks out of ACs into a separate Re-validation Plan; rewrote relocation ACs as assembly-reflection architecture tests; reworked preservation ACs as C#-side text-parse tests (no Python harness needed); enumerated the 14 `Context.IsReadOnly`-gated MCP tool files explicitly; added design decisions for the 24h self-heal threshold; tightened sentinel path resolution with an existence check on `CLAUDE_PROJECT_DIR`; specified the fixed truthy-string allowlist for env-var warnings; specified `sentinelAgeSeconds` (double) over `TimeSpan` for wire consistency; documented the four injected dependencies (`IEnvironment`, `IFileSystem`, `IClock`, `ILogger`) as part of the contract. |
| 2026-04-20 | Revised per plan-phase review: expanded relocation scope from 6 services to the full 12 in `PPDS.Dataverse.Services` (added `UserService`, `RoleService`, `FlowService`, `ConnectionReferenceService`, `DeploymentSettingsService`, `ComponentNameResolver`) to resolve the transitive-dependency hazard (`DeploymentSettingsService` → `IEnvironmentVariableService`) and to fully pay down the A1 debt in one pass. Added `RoleService.AssignRoleAsync`/`RemoveRoleAsync` to the Mutation Method Inventory (the only new mutation methods in the 6 added services — the other 5 are pure reads/transforms). Tightened AC-26's assembly-purity test predicate to enumerate the 12 forbidden type names concretely (rather than forbid whole namespaces, which would have caught `SchemaValidator` and DTOs that legitimately remain). Total: 17 services, 70 mutation methods, 1 DML branch. |

---

## Roadmap

- **Roslyn analyzer for mutation-method guard calls.** Deferred from this spec. Would flag any service method named `Create*/Update*/Delete*/etc.` that lacks a `_guard.EnsureCanMutate(...)` call as a compile error. Non-trivial to implement correctly; filed as a hardening follow-up.
- **Unify hook's `safety.readonly_env_var` configurability into the guard.** Today the guard hardcodes `PPDS_SHAKEDOWN`. The hook allows customization via `.claude/settings.json`. If any operator actually uses the setting (we don't know of one), extend the guard to read the same setting.
- **Audit-log hook.** With the guard as a natural insertion layer, a future spec can add per-mutation audit logging at the same chokepoint without touching service internals.
