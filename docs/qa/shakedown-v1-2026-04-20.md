# PPDS v1.0.0 Pre-Release Shakedown — 2026-04-20

**Scope:** Broad multi-surface validation of Extension, TUI, MCP, and CLI against
live Dataverse, with parity comparison and architecture audit.
**Environment:** `PPDS Demo - Dev` (only declared blast radius; in
`.claude/settings.json` `safety.shakedown_safe_envs` allowlist).
**Duration:** single continuous session, branch `verify/v1-shakedown`.
**Conducted by:** Claude Opus 4.7 coordinating 5 Sonnet subagents (CLI, TUI, MCP,
Extension, architecture audit) + 1 targeted diagnosis subagent (auth).
**Tiered depth:** D1 (deep) on core query path / metadata / plugin traces /
parity / safety-block; D2 (standard) on solutions / env vars / imports / web
resources / plugin registration / connection refs; D3 (smoke) on periphery
(users, roles, version, docs).

Three findings were fixed in-session and committed to this branch. Two are
fully end-to-end re-verified; one has test suite coverage but was not
re-verified against the running CLI binary. Three release-blocker findings
are deferred to a dedicated architecture session (`feat/shakedown-guard`
worktree).

## Real mutations that occurred during this shakedown

All against `PPDS Demo - Dev` (declared blast radius, documented here for
transparency):

1. `ppds_EnableFeatureX` environment variable flipped from unset (default
   `false`) to `true`. Occurred before the sentinel-based hook fix landed;
   mutation was via CLI `ppds environmentvariables set`.
2. 100 plugin trace log records deleted via the TUI's "Delete loaded traces"
   dialog. Environment regenerates traces actively; data loss not material.
   Exposed Finding #3 (TUI bypass).
3. 1 plugin trace (`6a784469-6cf1-4125-a9fa-bf3af3a32796`) deleted via the
   VS Code Extension's Plugin Traces panel. Exposed Finding #4 (daemon bypass).
4. 1 web resource (`63bfbf17-aab8-e911-a98a-000d3af475a9`,
   `accessChecker_closeDialog.js`) published via the Extension's Web Resources
   panel. Exposed Finding #4.

The shakedown's intent was write-blocked; the fact that mutations occurred is
itself the headline finding.

## Summary

- **39 distinct findings identified** across CLI, TUI, MCP, Extension, auth, and
  architecture layers.
- **3 fixed and committed during the session** (hook activation, docs URLs,
  auth caching bundle).
- **3 release-blocker findings** form a single "safety architecture" family
  being addressed in a dedicated worktree (`feat/shakedown-guard`).
- **Parity across surfaces: clean.** All four surfaces returned identical
  query results and metadata counts. No silent divergence.
- **Architecture audit: 9 issues flagged**, ranging from A1 command-layer
  query leaks to sovereign-cloud URL hardcoding to a single dead dialog class.

## Table of Contents

1. [Surfaces Tested](#surfaces-tested)
2. [Fixes Landed in This Session](#fixes-landed-in-this-session)
3. [Release Blockers (Deferred to `feat/shakedown-guard`)](#release-blockers-deferred-to-featshakedown-guard)
4. [High-Severity Findings Not Yet Fixed](#high-severity-findings-not-yet-fixed)
5. [Medium-Severity Findings](#medium-severity-findings)
6. [Low-Severity Findings and Polish](#low-severity-findings-and-polish)
7. [Test Matrix Results](#test-matrix-results)
8. [Parity Comparison](#parity-comparison)
9. [Architecture Audit](#architecture-audit)
10. [Untested Areas](#untested-areas)
11. [Recommended Fix Sequencing](#recommended-fix-sequencing)

---

## Surfaces Tested

| Surface | Depth | How |
|---|---|---|
| CLI | All 95+ verbs smoke-tested; 18+ deep on core (query/metadata/plugin traces); 7 safety-block attempts | `cli-verify` via Bash tool |
| TUI | All 10 screens + keybindings navigated; 4-tab framework tested; 3 safety-block attempts | `tui-verify` via PTY |
| MCP | Server launched with `--read-only`; 41 registered tools exercised per matrix; read-only-guard verified on all 4 mutation tools; Bash hook verified to block unprotected server launch | `mcp-verify` via stdio JSON-RPC |
| Extension | VS Code launched with extension; all 9 webview panels + 2 tree views driven; 3 safety-block attempts; Data Explorer + Notebooks taken to full depth | `ext-verify` via Playwright Electron + CDP |

**Parallel subagents per surface:** 4 Sonnet subagents ran concurrently under
a single Opus coordinator. Each had access to the full depth-tiered test list
and known-findings exclusion list to avoid duplicate reporting.

---

## Fixes Landed in This Session

Three findings were fixed during the shakedown itself and committed to
`verify/v1-shakedown`.

### F1 — Shakedown-safety hook write-block never activated (CLOSED)

**Commit:** `68b346b3a` — "fix(hook): shakedown-safety write-block reads file
sentinel, not just env var"

**Root cause:** `shakedown-safety.py` read `PPDS_SHAKEDOWN` from its own Python
process env. Claude Code's Bash tool spawns a fresh shell per invocation, so
inline `PPDS_SHAKEDOWN=1 ppds …` prefixes and `export` from prior calls never
propagated. `shakedown_active` was always `False` → write-block silently
skipped. Two real mutations went through under supposed protection during
Phase 0 of this shakedown.

**Fix:** Dual activation. Hook now blocks when EITHER `PPDS_SHAKEDOWN=1` OR a
fresh `.claude/state/shakedown-active.json` sentinel (containing `started_at`)
is present. Stale sentinels (>24h) self-heal at hook read and at
`session-start-workflow.py`. Skill Phase 0 step 3 rewritten to write the
sentinel as its first act (instead of telling the user to `export`, which
doesn't work in Claude Code). Phase 7 step 4 deletes it.

**Tests:** 7 new `TestSentinelActivation` cases; 96/96 pass.

**Re-verification:** End-to-end confirmed in-session. Under the sentinel,
`environmentvariables set`, `plugins deploy` (no --dry-run), `plugintraces
delete`, and `metadata table create` (via shakedown-guard session — after
fix) were all refused with clear messages.

### F2 — Three "where do docs live?" hardcoded URLs (CLOSED)

**Commit:** `da669218f` — "fix(docs): unify docs URLs across Extension, CLI,
and specs (shakedown #8)"

**Root cause:** Extension hardcoded `https://ppds.dev` (a domain that serves
nothing useful) in `src/PPDS.Extension/src/commands/browserCommands.ts:122`
and `src/PPDS.Extension/src/panels/PluginsPanel.ts:119`. CLI opened a GitHub
blob URL (`DocsCommand.cs:14`). Specs (`specs/custom-apis.md:876`,
`specs/plugins.md:1028`) referenced a non-existent `$schema` at
`https://ppds.dev/schemas/registrations.json`. Three surfaces, three wrong
answers.

**Fix:** New `src/PPDS.Extension/src/constants/docsUrls.ts` centralizes
`DOCS_URL` + `PLUGIN_REGISTRATION_DOCS_URL`. CLI `DocsCommand.DocsUrl`
updated to the canonical URL (`https://joshsmithxrm.github.io/ppds-docs/`).
Program.cs banner (unexpected second hardcoded site) also refactored to use
the CLI constant. Spec `$schema` lines removed entirely (schema file didn't
exist anywhere, better to omit than 404).

**Tests:** TypeScript typecheck + `dotnet build` green.

**Re-verification:** Not run end-to-end in-session; deferred to Phase 7 of a
future shakedown run against the rebuilt binaries.

### F3 — Auth token caching: 4 distinct bugs causing login-prompt spam (CLOSED)

**Commit:** `5935006e1` — "fix(auth): [bundle title]"

**Root cause:** Four independent bugs compounding into unusable CLI auth UX:

1. **Authority mismatch (cross-invocation re-auth):**
   `GlobalDiscoveryService` created its MSAL client with `tenantId: null`
   (authority `/organizations/`); `InteractiveBrowserCredentialProvider`
   created with the profile's tenant GUID (authority
   `/<tenant-guid>/`). MSAL keys cache by authority, so tokens written by
   one were invisible to the other — every invocation fell through to
   interactive auth.
2. **`_cachedResult` not volatile (within-invocation double-auth):**
   `InteractiveBrowserCredentialProvider.cs:33` + same pattern in
   `DeviceCodeCredentialProvider`. SDK invoked the token provider on a
   background thread during `ServiceClient` construction; no memory barrier
   → background thread saw `null` → second interactive auth within a single
   command. Observable as "Opening browser for authentication..." twice in
   the output of a single `ppds env who`.
3. **Auth messages on stdout (CLAUDE.md NEVER-rule violation):**
   `AuthenticationOutput.cs:15` defaulted `_writer` to `Console.WriteLine`.
   Stdout is data; status messages corrupt pipelines.
4. **`VerifyPersistence()` ran on every invocation:**
   `MsalClientBuilder.cs:93` performed a write/read/clear cache self-test
   on every `CreateClient` — redundant I/O and parallel-contention risk.

**Fix:** Unified both MSAL clients on `organizations` authority with
per-request `.WithTenantId()` (Microsoft's recommended multi-tenant public-
client pattern). `_cachedResult` + `_cachedResultUrl` marked `volatile` on
both providers. `AuthenticationOutput` default writer is now a lambda that
defers to `Console.Error.WriteLine` (lambda chosen over method-group so
`Console.SetError` redirection still works in tests). `VerifyPersistence`
gated by a `private static bool _persistenceVerified;` + double-checked
locking.

**Tests:** 458 → 468 (10 new). Pass across net8.0 / net9.0 / net10.0. Threading
race test for bug 2 not included (non-deterministic in CI; `volatile` is the
contract). Call-count spy for bug 4 not included (static helper not easily
injectable without refactor; field + source-text verification used instead).
Gaps documented in the commit.

**Re-verification:** Not run end-to-end in-session. Verifying would require
rebuilding the locally installed `ppds` binary and risking further login
prompts during the shakedown — deferred to Phase 7 of a future shakedown run.

**Related observation (not fixed, flagged for future):** `Prompt.SelectAccount`
in `InteractiveBrowserCredentialProvider.cs:288` forces the account picker on
every interactive auth. Could be relaxed to `Prompt.NoPrompt` + account hint
once `homeAccountId` is populated. Not a bug — optimization. Logged as Finding
L12.

---

## Release Blockers (Deferred to `feat/shakedown-guard`)

Three findings form a single architectural family: **the shakedown safety
primitive runs at the wrong layer.** Each is independently a v1 blocker. All
three are being addressed together in a dedicated worktree and PR
(`feat/shakedown-guard`) that should land before v1 ships.

### B1 — Hook pattern gap: `metadata <noun> <verb>` mutations escape the hook

**Severity:** Critical / Release Blocker
**Surface:** CLI (via Bash hook)
**Evidence:** `ppds metadata table create --solution PPDSDemo --name
shakedown_test --display-name Test --plural-name Tests --ownership
OrganizationOwned` reached Dataverse under an armed sentinel during the CLI
subagent run. Only a Dataverse server-side schema-prefix validation rule
stopped actual table creation.

**Root cause:** `.claude/hooks/shakedown-safety.py` `is_mutation()` reads the
verb at `argv[2]`. For `plugins register assembly` (verb-first at argv[2] =
"register"), this works. For `metadata table create` (noun-first: argv[2] =
"table", actual verb at argv[3] = "create"), `verb in _MUTATION_VERBS`
returns False → passthrough. Affects at least 18 commands:

- `metadata table create | update | delete`
- `metadata column create | update | delete`
- `metadata choice create | update | delete | add-option | update-option |
  remove-option | reorder`
- `metadata relationship create | update | delete`
- `metadata key create | delete | reactivate`

**Proper fix:** Handled by the architectural change in `feat/shakedown-guard`
— service-layer guard catches the mutation regardless of CLI verb shape.
Bash-hook pattern remains as secondary defense and should be extended to
recognize 3-level noun-verb forms as well (belt and suspenders).

### B2 — In-TUI mutations bypass the shakedown hook entirely

**Severity:** Critical / Release Blocker
**Surface:** TUI
**Evidence:** TUI subagent deleted 100 plugin trace log records from `PPDS
Demo - Dev` via the "Delete loaded traces" dialog under an armed sentinel.
Source grep confirms zero references to "shakedown" anywhere in
`src/PPDS.Cli/Tui/`.

**Root cause:** `shakedown-safety.py` is a Claude Code Bash `PreToolUse`
hook. Once `ppds interactive` (TUI) is running as a PTY process, all in-TUI
mutation actions invoke C# Application Services directly via in-process
method calls — no new Bash shell, no hook invocation. The sentinel file
exists but nothing in the TUI process reads it.

**Proper fix:** Service-layer `IShakedownGuard` (in `feat/shakedown-guard`)
consulted by every mutation service method, regardless of which surface
called it.

### B3 — Extension daemon mutations bypass the shakedown hook entirely

**Severity:** Critical / Release Blocker
**Surface:** Extension (via `ppds serve` daemon)
**Evidence:** Extension subagent deleted plugin trace
`6a784469-6cf1-4125-a9fa-bf3af3a32796` and published web resource
`63bfbf17-aab8-e911-a98a-000d3af475a9` under an armed sentinel. Daemon logs
show `[INF] [PPDS.Dataverse.PluginTrace] Deleted 1 of 1 plugin traces` and
`Published 1 web resource(s)` with no shakedown-related checks.

**Root cause:** `ppds serve` is a long-lived JSON-RPC daemon. Hook sees only
the daemon launch, not individual tool RPC calls. `RpcMethodHandler.cs` has
zero shakedown awareness — source grep confirms no sentinel check, no env-var
check, no guard interface.

**Proper fix:** Same `IShakedownGuard` as B2. The daemon shares the
Application Services layer with the TUI, so one service-layer guard fixes
both surfaces simultaneously. MCP already has its own parallel defense
(per-tool `Context.IsReadOnly` check when server launched with `--read-only`)
— the design should decide whether to unify or leave MCP's guard alongside
the new one.

**Combined fix plan:** The three blockers share one architectural change. The
`feat/shakedown-guard` session kicks off with `/design` and will route
through `/spec` → `/plan` → `/implement` → re-validation. Re-validation
exit criterion is a re-run of the mutation-block subset of this shakedown,
confirming (a) CLI `metadata table create` blocks, (b) in-TUI plugin trace
delete blocks, (c) Extension plugin trace delete and web resource publish
both block.

---

## High-Severity Findings Not Yet Fixed

### H1 — `SolutionService.ListAsync(filter:…)` uses Dataverse `contains` operator on non-fulltext columns

**Status: FIXED** — commit `a650bd6f3`

**Severity:** High
**Surfaces:** CLI `solutions list --filter`, MCP `ppds_solutions_list` with
`filter` arg
**Evidence:** CLI: `ppds solutions list --filter "foo"` returns
`Error: Condition Operator Contains is not valid for attribute
c7f1c81a-18f5-40af-a1c1-afebf1005382 on entity 3ffa7e2c-9d9c-4923-bf59-
645d04c10063 with no fulltext index`. MCP: calling `ppds_solutions_list`
with `filter` parameter hits the same 0x80048415 error.

**Root cause:** The service passes the filter as a `contains` operator on
unique-name / friendly-name columns. Those columns do not have fulltext
indexes configured. Likely applies to other list services that accept
`filter` params (`users list --filter`, `roles list --filter`,
`deployment-settings validate --filter` all potentially affected — not
empirically tested per surface but same code path suspected).

**Fix direction:** Replace `contains` with `like` + `%value%` or
`beginswith`, or filter client-side on the returned list. One change to
`SolutionService` likely cascades to other `*Service.ListAsync` methods
following the same pattern.

**Note:** Originally tracked as Finding #2 alongside "duplicate `-f` short
flag" — those are two distinct bugs that coincidentally both manifest when
passing `-f Json`. The flag collision is **L1** below; the filter-operator
bug is this entry.

### H2 — MCP `ppds_plugins_list` is completely broken

**Status: FIXED** — commit `bc13a310a`

**Severity:** High (P2 per subagent)
**Surface:** MCP
**Evidence:** Every invocation returns
`Dataverse error 0x80041103: 'primaryobjecttypecode' is not a valid attribute
on 'sdkmessageprocessingstep'`. Tool does not return results under any
combination of args.

**Root cause:** `PluginsListTool.BuildJoinedQuery` queries
`step.primaryobjecttypecode` in its FetchXML. The attribute name is wrong
for that entity. CLI `plugins list` works correctly — suggests the CLI
service uses a different query path than the MCP tool.

**Fix direction:** Replace the tool's inlined FetchXML with a call to the
same `IPluginService.ListAsync` method the CLI uses (A2 alignment — single
code path). Delete the duplicated query.

### H3 — MCP `ppds_data_providers_list` is broken

**Status: FIXED** — commit `bc13a310a`

**Severity:** High (P3 per subagent, upgraded to High here because the tool
is a complete failure)
**Surface:** MCP
**Evidence:** Returns `Dataverse error 0x80041103: 'entitydataprovider.createdon'
does not exist`.

**Root cause:** `DataProvidersListTool` FetchXML queries a non-existent
attribute on the `entitydataprovider` entity.

**Fix direction:** Same as H2 — consolidate on the service-layer query path
rather than inline FetchXML in the tool.

### H4 — Extension Connection References panel is broken for all users

**Status: FIXED** — commit `3836bc9ca`

**Severity:** High
**Surface:** Extension
**Evidence:** Opening the Connection References panel returns "No connection
references found" with error: `Unable to retrieve connection status (CPM may
not have access to Connections API). No service for type
'PPDS.Cli.Services.IConnectionService' has been registered.` Fixture env has
2 connection references and CLI / MCP both see them fine.

**Root cause:** `IConnectionService` is not registered in the daemon's DI
container for the Extension path. Panel fails closed.

**Fix direction:** Register `IConnectionService` in
`src/PPDS.Cli/Program.cs` (or wherever the daemon composes its DI
container) alongside the other services used by RPC handlers.

### H5 — D4 violation pattern: Application Services throw raw exceptions, relying on RPC handler to wrap

**Status: FIXED** — commit `17a88dc90`

**Severity:** High (architectural)
**Surfaces:** TUI, MCP, any consumer calling services directly without going
through the RPC handler
**Evidence:**
`src/PPDS.Dataverse/Services/WebResourceService.cs:23` comment explicitly says
"The RPC handler layer wraps these in `PpdsException`." The convention is
repeated across `PluginTraceService`, `SolutionService`,
`ComponentNameResolver`, etc. Constitution D4: "Wrap all exceptions from
Application Services in `PpdsException` with `ErrorCode`."

**User-visible consequence:** Raw Dataverse faults with GUID-encoded
attribute/entity IDs leak in CLI output and TUI error dialogs:

- `Condition Operator Contains is not valid for attribute
  c7f1c81a-18f5-40af-a1c1-afebf1005382 on entity 3ffa7e2c-9d9c-4923-
  bf59-645d04c10063 with no fulltext index` (from H1)
- `Entity 'plugintracelog' With Id = <guid> Does Not Exist` (from
  `plugintraces timeline` — leaks internal entity name)
- `The entity with a name = 'nonexistent_table' with namemapping =
  'Logical' was not found in the MetadataCache.LazyDynamicMetadataCache
  with version 7433989` (leaks internal class name)

**Fix direction:** Wrap at the service layer where the Dataverse SDK call
occurs. The "RPC wraps" pattern is a documented shortcut that violates D4
and leaves TUI/MCP consumers with raw faults. One PR per service cluster,
each replacing raw `throw` with `throw new PpdsException(ErrorCode.X, "...",
inner)`. Error messages become the user-friendly contract, not a leaky
abstraction.

### H6 — MCP `ppds_web_resources_list` returns 5.7 MB payload with no pagination

**Status: FIXED** — commit `7a35a3eb2`

**Severity:** High (P2)
**Surface:** MCP
**Evidence:** A single call to the tool in the fixture env returns
approximately 5.7 MB of JSON. The fixture has 16,361 web resources; all are
returned without pagination.

**Impact:** Guaranteed LLM context overflow for MCP clients. Claude, other
LLM clients, and any MCP consumer with a fixed-size context window cannot use
this tool against a realistically populated environment.

**Fix direction:** Add a default `maxRows` (suggest 100) and a pagination
cursor (`nextPageToken` or similar). Require a `solutionId` filter for envs
over a configurable threshold, OR default to filtering out managed Microsoft
resources. Same pattern applies to any MCP `*_list` tool with
unbounded return size — audit the full set.

### H7 — MCP tool errors surface as opaque `"An error occurred invoking '<tool>'."`

**Status: FIXED** — commit `6c07ca353`

**Severity:** High (P2; degrades agent reliability)
**Surface:** MCP
**Evidence:** Every tool error — read-only refusals, query errors,
validation failures, server bugs — is returned identically in the MCP
response as `{"content": [{"type": "text", "text": "An error occurred
invoking '<tool>'."}], "isError": true}`. The actual exception message
(`"Cannot modify metadata: this MCP session is read-only."`, Dataverse
fault details, etc.) is only visible in the MCP server stderr at `fail:`
log level.

**Impact:** MCP clients (LLMs) cannot distinguish legitimate refusals from
query errors from server bugs. All failure modes look identical. Agent
orchestration can't make informed decisions about whether to retry, adjust,
or escalate.

**Root cause:** The ModelContextProtocol SDK catches exceptions from tools
and returns a generic envelope. Tools need to catch exceptions themselves
and return structured error content via the MCP content helper.

**Fix direction:** Per-tool try/catch that builds a structured error content
block (code + human-readable message + suggested client action).
Cross-references H5 — once services wrap in `PpdsException`, tools have a
structured error code to surface.

---

## Medium-Severity Findings

### M1 — `ppds flows get <id>` returns "not found" for all IDs `ppds flows list` returns

**Status: FIXED** — commit `190cf2a23`

**Surface:** CLI (likely Extension / MCP if they share the code path)
**Evidence:** 3 flows returned by `ppds flows list --output-format Json`.
All 3 IDs produce exit code 6 "Cloud flow not found" when passed to
`ppds flows get`. Same for `ppds flows url`.
**Root cause:** Probably a Dataverse `workflow`-entity ID vs Power Automate
flow-management-API ID mismatch. `list` and `get` use different backends and
different ID schemas.
**Fix direction:** Either unify the backend, or have `list` return both IDs so
downstream `get`/`url` can find the record.

### M2 — `ppds logs tail` and `ppds logs dump` don't exist

**Surface:** CLI
**Evidence:** Both commands are listed in the test matrix but produce
`"logs" was not matched` when invoked. No `logs` command group is registered
in the CLI.
**Interpretation:** Either the commands were spec'd but not implemented, or
the spec/matrix drifted from the implementation. Unknown which is intended
for v1.
**Fix direction:** Ship the commands, or strike them from the spec.

### M3 — CLI `metadata optionset <name>` fails with raw SDK error on entity-scoped option sets

**Status: FIXED** — commit `ce2470338`

**Surface:** CLI
**Evidence:** `ppds metadata optionset statuscode` returns `Could not find
an optionset with name statuscode` (exit 2). `statuscode` is entity-scoped
(per-entity status), not global, so the command is semantically correct in
rejecting — but the error message doesn't explain the global-only
restriction. A related invocation produces `An OptionSet with
IsGlobal='False' and OptionSetType='Status' cannot be retrieved through this
SDK method.` — raw SDK message, exit code 0 (wrong).
**Fix direction:** Add a flag `--entity <name>` for entity-scoped lookups;
document the global-only default; wrap the SDK error per H5.

### M4 — CLI `plugintraces timeline <id>` has confusing arg semantics

**Status: FIXED** — commit `ce2470338`

**Surface:** CLI
**Evidence:** The argument name and description suggest it takes a
correlation ID, but passing a correlation ID returns `Entity 'plugintracelog'
With Id = <correlation-id> Does Not Exist`. Passing a trace ID works. Either
the arg name is wrong or the underlying lookup is wrong. Also leaks raw
entity name (cross-references H5).
**Fix direction:** Clarify the arg — either fix the lookup to accept
correlation IDs (and group traces by correlation for a hierarchical
timeline view), or rename the arg to "trace id".

### M5 — `ppds serve --help` is silent; `ppds serve` with no args exits silently

**Status: FIXED** — commit `ce2470338`

**Surface:** CLI
**Evidence:** `ppds serve --help` produces zero output (exit 0). `ppds serve`
with no args exits immediately after printing only the version header to
stderr — no "listening" message, no indication the daemon is ready or
failed.
**Fix direction:** Implement proper help text; emit a "Daemon ready on
stdio" message to stderr when the server starts; exit non-zero on startup
failure.

### M6 — A1 architectural violation: `DeleteCommand`, `TruncateCommand`, `UpdateCommand` perform Dataverse queries in the CLI command layer

**Status: FIXED** — commit `35a26b8b3`

**Surface:** Architecture
**Evidence:** `src/PPDS.Cli/Commands/Data/DeleteCommand.cs:498,667`;
`TruncateCommand.cs:312,426`; `UpdateCommand.cs:642,826` all call
`client.RetrieveMultipleAsync(query, …)` directly in the command handler
(alternate-key lookup, record fetch for update). Business query logic living
in the UI layer violates Constitution A1 ("Logic in Services").
**Fix direction:** Extract the query paths into `IDataService` (or the
existing service interfaces for each entity type). Command handlers should
dispatch to services without touching the pool directly.

### M7 — Sovereign-cloud URL gap in `DataverseUrlBuilder`

**Surface:** Architecture (CLI/Extension)
**Evidence:** `src/PPDS.Cli/Infrastructure/DataverseUrlBuilder.cs:42,52,62,72,90`
hardcodes `make.powerapps.com` and `make.powerautomate.com` for Maker portal
URLs. `src/PPDS.Auth/Cloud/CloudEndpoints.cs` has per-cloud tables for auth
/ API endpoints but Maker URLs are not parameterized.
**Impact:** GovHigh / GovDoD / China cloud users get wrong Maker portal
links from every `<surface> url` command.
**Fix direction:** Extend `CloudEndpoints` with per-cloud Maker URLs and use
those in `DataverseUrlBuilder`.

### M8 — TUI FetchXML autocomplete masks successful execution

**Surface:** TUI
**Evidence:** Typing `<fetch …>` into the SQL Query editor triggers the SQL
autocomplete on the `<` character. The autocomplete suggestion can corrupt
the input such that the editor displays a "SQL parse error" — but Query
History shows the FetchXML actually executed successfully. Users see an
error and don't realize the query ran.
**Fix direction:** Detect FetchXML mode from the first non-whitespace
character and suppress SQL autocomplete, OR provide an explicit FetchXML
mode toggle (cross-references L7).

### M9 — Extension `PPDS: Restart Daemon` command crashes with TypeError

**Status: FIXED** — commit `cd677aff9`

**Surface:** Extension
**Evidence:** Invoking the command produces `TypeError: Cannot read
properties of null (reading 'removeListener')` in `_DaemonClient.start` /
`_DaemonClient.restart`. The daemon process does restart (new PID in logs),
but the extension's command handler throws and shows an error toast.
**Root cause:** Extension nulls out `_connection` before `start()` tries to
remove event listeners from it.
**Fix direction:** Guard the `removeListener` call with a null check, OR
move the null-out after the listener cleanup.

### M10 — CLI `env config` errors on no-argument invocation

**Status: FIXED** — commit `ce2470338`

**Surface:** CLI
**Evidence:** `ppds env config` (no args) exits 2 with `Error: Environment
URL is required. Use --list to see all configs.` The matrix expected this
to be a read-only no-op (show current config or help).
**Fix direction:** Either show the current config when no args supplied, or
document the required args in help text.

---

## Low-Severity Findings and Polish

### L1 — Duplicate `-f` short-flag in 4 CLI commands

**Status: FIXED** — commit `7ead6d3dd`

**Surface:** CLI
**Evidence:** `solutions list`, `users list`, `roles list`, and
`deployment-settings validate` each declare `-f` for both `--filter` (or
`--file`) AND `--output-format`. The System.CommandLine parser silently
binds to one based on heuristics; `-f Json` on `solutions list` is
interpreted as `--filter "Json"` (triggering H1 as a side effect). In
`deployment-settings validate`, `-f` conflicts with a REQUIRED `--file`
flag — users see `"required --file not provided"` when actually the file
path was misparsed as an output format.
**Fix direction:** Remove the `-f` short alias from `--filter` /
`--file`; reserve `-f` exclusively for `--output-format` (consistent with
the rest of the CLI). Or vice versa, pick one and apply globally.

### L2 — `plugintraces delete` arg parsing dumps unhandled exception + absolute build path

**Status: FIXED** — commit `3836bc9ca`

**Surface:** CLI
**Evidence:** `ppds plugintraces delete --all --yes` produces `Unhandled
exception: System.InvalidOperationException: Cannot parse argument
'--yes' for command 'delete' as expected type 'System.Guid'.` with a full
C# stack trace to stdout including an absolute build path:
`at …DeleteCommand.cs:line 65 at D:\a\power-platform-developer-suite\power
-platform-developer-suite\src\PPDS.Cli\…`. Exit code 0 (wrong).
**Fix direction:** Add a top-level argument parser catch in
`Program.cs` that renders a clean error + exits non-zero. Remove path
leakage. Also: the command's actual arg surface is positional `<Guid>`,
but help / UX suggests `--all`/`--yes` should be supported — add these or
document otherwise.

### L3 — CLI `plugins get <name>` exits 0 on bad args, dumps help to stdout

**Surface:** CLI
**Evidence:** `ppds plugins get PPDSDemo.Plugins` (missing required
subnoun) prints usage text to stdout (should be stderr) and exits 0
(should be 2). Valid invocation is `ppds plugins get assembly
PPDSDemo.Plugins`.
**Fix direction:** Unknown-noun error path should write to stderr and
exit non-zero.

### L4 — Dead code: `ResultComparisonView` TUI dialog never instantiated

**Status: FIXED** — commit `d16f051a0`

**Surface:** Architecture / TUI
**Evidence:** `src/PPDS.Cli/Tui/Components/ResultComparisonView.cs`
implements a `Dialog` class with full layout and logic. No `new
ResultComparisonView(…)` site exists anywhere in the codebase; not reached
from any menu or keybinding.
**Fix direction:** Delete the class, OR wire it into the TUI menu (the
name suggests it belongs on the SQL Query screen for diffing two
result sets — product decision required).

### L5 — Architecture S2: `UseShellExecute = true` in 2 places without spec-level justification

**Surface:** Architecture
**Evidence:** `src/PPDS.Cli/Infrastructure/DefaultBrowserLauncher.cs:21`
(browser launcher — known-safe URL input) and
`src/PPDS.Cli/Tui/Dialogs/ErrorDetailsDialog.cs:243` (opens log file in
default text editor — known-safe local path). Constitution S2 requires
justification in the spec, not a code comment.
**Fix direction:** Add a short note in the relevant spec section
documenting the shell-execute use and its safety rationale, OR
`using System.Diagnostics` to start the process without shell execute
(works for URLs via `Process.Start(psi)` with explicit verb).

### L6 — Architecture I1: "Connected as {identity}" auth status message on stdout

**Status: FIXED** — commit `f0b37cb50`

**Surface:** Architecture / CLI
**Evidence:** `src/PPDS.Cli/Commands/Auth/AuthCommandGroup.cs:1368` uses
`Console.WriteLine` for a status header. Constitution I1: "CLI stdout is
data only — status, progress, diagnostics go to stderr."
**Fix direction:** Change to `Console.Error.WriteLine`. This is the same
class of bug as F3 item 3, but in a different source file.

### L7 — TUI lacks an explicit FetchXML mode toggle

**Surface:** TUI
**Evidence:** The SQL Query screen executes FetchXML when the editor
contains XML (confirmed via Query History showing rows returned), but
there is no mode toggle, no syntax highlighting switch, and SQL
autocomplete interferes (cross-references M8).
**Fix direction:** Add a mode toggle key (e.g. Ctrl+M) that switches
between SQL and FetchXML syntax, adjusting autocomplete and highlighting.

### L8 — TUI Metadata Explorer keyboard nav between Attributes/Relationships/Keys/Privileges/Choices tabs not discoverable

**Status: FIXED** — commit `3836bc9ca`

**Surface:** TUI
**Evidence:** Tab key moves focus to the [New]/[Edit]/[Delete] buttons
within the Attributes tab rather than cycling between sibling tabs. No
hotkey is documented in the KeyboardShortcutsDialog for tab switching.
The Relationships/Keys/Privileges tab contents were unreachable via
keyboard during the shakedown.
**Fix direction:** Add an explicit hotkey (e.g. Ctrl+Tab or Alt+number),
document it in the shortcuts dialog.

### L9 — TUI Tools menu items 14/15 ("Environment Details" vs "Configure Environment") easy to confuse

**Status: FIXED** — commit `bfec838d0`

**Surface:** TUI
**Evidence:** Adjacent menu items, similar names, distinct purposes (read
organization info vs edit label/type/color). Off-by-one menu clicks
during testing.
**Fix direction:** Rename to "View Organization Info" and "Edit Environment
Label/Color" or similar to disambiguate, or insert a separator / section
header between them.

### L10 — TUI Plugin Traces list default columns hide the useful ones

**Status: FIXED** — commit `bfec838d0`

**Surface:** TUI
**Evidence:** Default list shows Time and Duration columns; plugin type
name, message name, and primary entity are not visible without
horizontal scrolling.
**Fix direction:** Default column set should include plugin type + message
+ entity (the primary facets users filter on). Time and duration stay
but deprioritized.

### L11 — TUI discoverability: Create Table dialog, TraceTimelineDialog, Web Resources solution filter not findable via keyboard

**Status: FIXED** — commit `3836bc9ca`

**Surface:** TUI
**Evidence:** During TUI shakedown, the subagent could not locate:
- A "Create Table" dialog via Ctrl+N (that hotkey opened "Create Column"
  instead — used as a proxy for the mutation test)
- `TraceTimelineDialog` — the trace detail view has tabs for
  Details/Exception/Block/Configuration but no Timeline
- A solution filter input in the Web Resources screen
**Fix direction:** Keyboard shortcuts dialog should enumerate every
dialog/action; each dialog should be reachable via at least one
documented hotkey. Cross-references L8.

### L12 — Auth `Prompt.SelectAccount` forces picker on every interactive auth

**Surface:** Auth (post-v1 polish)
**Evidence:** `InteractiveBrowserCredentialProvider.cs:288` always uses
`Prompt.SelectAccount`. Once `homeAccountId` is populated on the profile
(after first auth), subsequent interactive auths could use
`Prompt.NoPrompt` + account hint.
**Fix direction:** Conditional prompt: `SelectAccount` when `homeAccountId`
is unset, `NoPrompt` with account hint when set. Deserves its own small
design pass to verify all entry points guarantee the invariant. Not
blocking v1.

### L13 — Architecture R3: `Loaded +=` event subscriptions in 5 TUI dialogs/screens not unsubscribed

**Status: FIXED** — commit `ddc7fc6db` (regression fix: `597b13a4a`)

**Surface:** Architecture / TUI
**Evidence:** `EnvironmentConfigDialog.cs:148`,
`EnvironmentSelectorDialog.cs:243`, `ProfileSelectorDialog.cs:168`,
`ExportDialog.cs:199`, `SqlQueryScreen.cs:470` all `+=` to `Loaded`
without a matching `-=` in `Dispose`. Terminal.Gui tends to clean up on
view-tree disposal, so this is low-severity, but Constitution R3 is
categorical.
**Fix direction:** Add the matching `-=` in each Dispose override.

### L14 — MCP tool count mismatch: 41 registered, matrix / docs say 39

**Status: FIXED** — commit `d286488b7`

**Surface:** MCP (docs drift)
**Evidence:** MCP server initializes 41 tools. Docs / matrix list 39.
Undocumented: `ppds_metadata_add_option_value`,
`ppds_metadata_create_key`.
**Fix direction:** Update docs to match reality.

### L15 — MCP `ppds_env_select` can't confirm current env when `--allowed-env` is empty

**Status: FIXED** — commit `d286488b7`

**Surface:** MCP (design gap)
**Evidence:** With no `--allowed-env` flag passed at server startup,
`ValidateEnvironmentSwitch` refuses all switches — including a no-op
confirmation of the currently-active env. Matrix expected this to
succeed.
**Fix direction:** Same-env confirmation should be allowed (no-op) even
with empty allowlist, OR the allowlist should default to include the
active env at startup.

### L16 — CLI `data schema|analyze|users` flag-name discrepancies

**Surface:** CLI (docs / UX)
**Evidence:** Help text uses `--entities`, `--schema`, `--source-env` /
`--target-env` — but the test matrix assumed `--entity`, `--source`.
Either docs drift or help drift.
**Fix direction:** Align flag names across commands or document the
command-specific variants prominently.

---

## Test Matrix Results

Feature matrix (31 feature families × 4 surfaces) plus 7 safety-block
rows. Status legend: `✅` pass, `✗` fail (finding filed), `🔒` expected
mutation block, `—` N/A for surface, `?` untested (reason in Untested
Areas).

### Read-path features

| # | Feature family | CLI | TUI | MCP | Ext |
|---|---|---|---|---|---|
| 1 | Version / docs / help | ✅ | ✅ | — | ✅ (F2 fixed) |
| 2 | Auth profile list / who | ✅ | ✅ | ✅ | ✅ |
| 3 | Env list / who / select read | ✅ | ✅ | ✅ (L15) | ✅ |
| 4 | Env details & config read | ✗ (M10) | ✅ | — | ✅ |
| 5 | SQL query happy / invalid / large | ✅ | ✅ (M8) | ✅ | ✅ (hero OK) |
| 6 | FetchXML query | ✅ | ✅ (M8/L7) | ✅ | ✅ |
| 7 | Query explain plan | ✅ | ✅ | — | ✅ |
| 8 | FetchXML preview from SQL | — | ✅ | — | ✅ |
| 9 | Query history | ✅ | ✅ | — | ✅ |
| 10 | Metadata entities list | ✅ | ✅ | ✅ | ✅ |
| 11 | Metadata entity detail (attrs/rels/keys) | ✅ | ✅ (L8) | ✅ | ✅ |
| 12 | Global option sets | ✗ (M3) | ? | — | ? |
| 13 | Solutions list / get / components | ✅ | ✅ | ✅ (H1) | ✅ |
| 14 | Plugin traces list/get/timeline/related | ✅ (M4) | ✅ (L11) | ✅ | ✅ |
| 15 | Plugin registration list/get/diff | ✗ (L3) | ✅ | ✗ (H2) | ✅ |
| 16 | Custom APIs list / get | ✅ | ✅ | ✅ | ✅ |
| 17 | Service endpoints list / get | ✅ | ✅ | ✅ | ✅ |
| 18 | Data providers / sources list | ✅ | ✅ | ✗ (H3) | ✅ |
| 19 | Connection refs / connections / flows | ✅ (M1) | ✅ | ✅ | ✗ (H4) |
| 20 | Environment variables read | ✅ | ✅ | ✅ | ✅ |
| 21 | Import jobs | ✅ | ✅ | ✅ | ✅ |
| 22 | Web resources read | ✅ | ✅ (L11) | ✗ (H6) | ✅ |
| 23 | Users / roles | ✅ | — | — | — |
| 24 | Data schema / analyze | ✅ (L16) | ? | ✅ | — |
| 25 | Deployment settings | ? | — | — | — |
| 26 | Logs | ✗ (M2) | ✅ | — | ✅ |
| 27 | Notebooks | — | — | — | ✅ (deep) |
| 28 | Serve / daemon | ✗ (M5) | — | — | ✗ (M9) |

### Mutation-block rows (safety verification)

| # | Attempt | CLI | TUI | MCP | Ext |
|---|---|---|---|---|---|
| 29 | plugins deploy without --dry-run | ✅ blocked | — | — | — |
| 30 | plugins deploy --dry-run (allowed) | ✅ allowed | — | — | — |
| 31 | metadata create table | ✗ (B1 bypass) | ✗ (B2 bypass) | ✅ blocked | ✗ (B3 bypass — business rule caught it instead) |
| 32 | data truncate | ✅ blocked | — | — | — |
| 33 | plugin traces delete | ✅ blocked | ✗ (B2 — 100 deleted) | ✅ blocked | ✗ (B3 — 1 deleted) |
| 34 | env variable set | ✅ blocked (after F1) | — | ✅ blocked | — |
| 35 | web resource publish | ✅ blocked | ✗ (dialog opens, would succeed) | ✅ blocked | ✗ (B3 — 1 published) |

Safety-block summary:
- **CLI** — hook works for verb-first commands, misses noun-verb-noun (B1). 6/7 expected blocks succeeded.
- **TUI** — hook never fires. 0/3 expected blocks succeeded. Real data deleted.
- **MCP** — per-tool `--read-only` guard works. Hook also blocks unprotected server launch. 4/4 expected blocks succeeded (belt-and-suspenders).
- **Extension** — daemon has no guard. 0/3 expected blocks succeeded. Real data deleted and published.

---

## Parity Comparison

All four surfaces executed the same canary query and metadata drilldown.
Results were **bit-identical** for the query and exactly-matched for
metadata counts. No silent cross-surface divergence detected.

### Canary query: `SELECT TOP 10 name, accountid FROM account ORDER BY name`

| Surface | Row count | Execution time | First record | Last record | Notes |
|---|---|---|---|---|---|
| CLI (`-f Json`) | 10 | 112 ms | "An even newer account" / `9ecaf2fb-…` | "PPDS_LiveTest_06c210c3…" / `6af12ded-…` | `executedFetchXml` in response |
| TUI | 10 | 332 ms | "An even newer account" | "PPDS_LiveTest_06c210c3…" | 7 of 10 visible in 30-row terminal; scrolls correctly |
| MCP | 10 | 248 ms | "An even newer account" | "PPDS_LiveTest_06c210c3…" | Same records, `executedFetchXml` included |
| Extension (Data Explorer) | 10 | 389 ms | "An even newer account" | "PPDS_LiveTest_06c210c3…" | Sortable grid, sort indicator on `name` column |

### Metadata entity `account`

| Surface | Attribute count | Relationship count | Notes |
|---|---|---|---|
| CLI | 215 | (not reported in same shape) | Response includes 1:N / N:1 / N:N separately |
| TUI | 215 | (via Attributes tab) | L8 blocked direct verification of relationship count tab |
| MCP | 215 | 67 (46 1:N + 20 N:1 + 1 N:N) | Requires `includeRelationships: true` |
| Extension | 215 | 67 | Per log output |

**Verdict:** Parity clean. The "who does it better?" judgments are purely UX, not data:

- **SQL query UX best surface:** Extension Data Explorer — Monaco IntelliSense, sortable grid, entity-aware context menu (7 items including "Open <entity> Record URL"), rectangular selection, smart copy. TUI is functional but uses fixed-column grid and autocomplete hurts FetchXML (M8). CLI is data-only (correctly).
- **Metadata browser best surface:** Extension — detail panel with tabbed UI, keyboard + mouse. TUI is 2nd but L8 makes it keyboard-hostile. CLI requires three separate commands for what the UI shows on one screen.
- **Plugin traces best surface:** Extension — 6-tab detail view (Overview, Details, Exception, Message Block, Configuration, Timeline). TUI has trace detail but no Timeline dialog found (L11). CLI's `plugintraces timeline` arg semantics are confusing (M4).
- **Profile selector best surface:** Extension tree is most discoverable; TUI dialog shows adequate detail; CLI's `auth list` is fine for scripts.
- **Env switcher best surface:** Extension context menu; TUI dialog second; CLI `env select` functional.

---

## Architecture Audit

Conducted as a parallel Sonnet subagent run over Phase 5, read-only static
analysis against Constitution laws. Nine checks. Findings cross-referenced
to entries above where applicable.

| Check | Status | Finding refs |
|---|---|---|
| 1. `ServiceClient` bypass (D1/D2) | **Clean** | — |
| 2. Pooled client across parallel queries (CLAUDE.md NEVER) | **Clean** | — (ParallelExporter checks out per-request) |
| 3. Silent catches (D4) | Degraded-result pattern | H5 (systemic) |
| 4. Dead code | 1 found | L4 (ResultComparisonView) |
| 5. TUI handler wiring | All wired (except L4) | — |
| 6. Constitution spot-check | A1/D4/I1 violations found | M6 (A1), H5 (D4), L6 (I1) |
| 7. Secrets in logs (S3) | **Clean** | — |
| 8. `innerHTML` usage (S1) | 52 sites, all escaped | — (properly guarded via `escapeHtml` / `escapeAttr`) |
| 9. `shell:true` without justification (S2) | 2 sites | L5 |

Plus bonus:
- `TODO`/`FIXME`/`HACK` markers: 2 deferred-work notes, neither a runtime bug.
- `NotImplementedException`: zero occurrences in production code.
- Hardcoded URLs: M7 (sovereign cloud Maker URLs) + F2 (docs URLs, fixed).

---

## Untested Areas

Called out explicitly so the gap is visible.

### Features partially covered

- **CLI row 24 (`data schema|analyze|users`)**: Flag-name confusion (L16) led
  to partial coverage. Actual commands run successfully when correct flags
  were used.
- **CLI row 25 (`deployment-settings validate`)**: Not fully exercised —
  requires a deployment-settings file which fixture env did not have.
- **CLI row 28 (`ppds serve`)**: Launched briefly for `--help` smoke (M5);
  daemon was not driven end-to-end from CLI side (Extension drives it).
- **TUI row 12 (Global option sets)**: The MetadataExplorer has a "Choices"
  tab visible but option set content was not verified specifically.
- **TUI row 31 (CreateTableDialog)**: Could not be located via the usual
  keyboard paths (L11). Mutation test used CreateColumnDialog as a proxy —
  B2 is still validly demonstrated because the bypass is layer-level, not
  dialog-specific.
- **TUI row 14 (TraceTimelineDialog)**: Not found (L11). Trace detail view
  has other tabs but no Timeline tab.
- **TUI row 22 (Web Resources solution filter)**: Filter UI not located.
- **Extension row 27(e)(f) (Notebook CSV/JSON export)**: Export commands
  fire correctly; file-write path confirmed in source. The OS-native
  `showSaveDialog` could not be completed via Playwright automation.
  Non-issue, flagged for transparency.
- **Extension row 27(i) (Notebook save + reload)**: Same OS-dialog
  limitation. Notebook save mechanism is correct per source inspection.

### Surfaces / features explicitly out of scope

- **Performance**: no load tests, no memory profiling, no concurrency stress.
  Not part of shakedown charter.
- **Multi-env / multi-profile workflows**: single profile (PPDS) + single env
  (PPDS Demo - Dev) used throughout. Profile-switching UX was exercised but
  cross-env data operations were not.
- **Sovereign clouds (GovHigh / GovDoD / China)**: Public cloud only.
  Related finding (M7) surfaced via audit.
- **NuGet library consumers of `PPDS.Dataverse`**: Library was tested
  indirectly via the CLI / TUI / MCP / Extension that consume it. Direct
  library consumer scenarios not exercised. Important because `feat/shakedown-
  guard` must explicitly preserve NuGet-consumer behavior.
- **Plugin deploy / registration push**: All plugin tests were read-only.
  No plugin was deployed against `PPDS Demo - Dev`. This is intentional
  given the blast radius — plugin deploys are mutation-heavy and the
  safety architecture is itself under repair.
- **Data import / export / migration end-to-end**: `data export` smoke only;
  full export→import→validate round trip not run.
- **Plugin trace level changes (`plugintraces settings set`)**: Not
  exercised; setting was already "All" in fixture, and changing is a
  mutation.

---

## Recommended Fix Sequencing

Three PR tracks.

### Track 1 — This PR (`verify/v1-shakedown`)

**All Track 1 items are now done.**

Originally included F1, F2, F3. All of the following were fixed and committed
to this branch:

- H1 (SolutionService filter operator) — commit `a650bd6f3`
- H2 / H3 (MCP plugins_list / data_providers_list) — commit `bc13a310a`
- H4 (Extension Connection References DI gap) — commit `3836bc9ca`
- H6 (web_resources_list pagination) — commit `7a35a3eb2`
- M1 (flows get/url GUID vs uniqueName) — commit `190cf2a23`
- M3, M4, M5, M10 (CLI UX) — commit `ce2470338`
- L1 (duplicate `-f` short-flag) — commit `7ead6d3dd`
- L2, L8, L11 (DI + UX) — commit `3836bc9ca`
- L4 (dead ResultComparisonView) — commit `d16f051a0`
- L6 (stdout "Connected as …") — commit `f0b37cb50`
- L9 / L10 (TUI menu rename + plugin traces columns) — commit `bfec838d0`
- L13 (event cleanup + regression fix) — commits `ddc7fc6db`, `597b13a4a`
- L14 / L15 (MCP tool count + env_select) — commit `d286488b7`

Three items originally slated for Track 3 were also pulled into this PR:

- **H5** (raw exceptions — D4 violation) — commit `17a88dc90` *(originally Track 3)*
- **H7** (MCP opaque errors) — commit `6c07ca353` *(originally Track 3)*
- **M6** (DataQueryService extraction — A1 violation) — commit `35a26b8b3` *(originally Track 3)*

Remaining from the original Track 1 list: **M2** (`logs` command missing —
decision: implement or strike) is not yet addressed.

### Track 2 — `feat/shakedown-guard` (already launched)

B1 / B2 / B3 bundled into a single architectural PR. Lands independently,
ideally after Track 1 merges (so it can rebase onto the new hook sentinel
logic on main).

Exit criterion: re-run mutation-block subset of this shakedown against
TUI + Extension and confirm (a) CLI `metadata table create` blocks, (b)
in-TUI plugin trace delete blocks, (c) Extension plugin trace delete AND
web resource publish both block.

### Track 3 — Post-v1 polish

Items originally in Track 3 that were pulled into this PR unexpectedly:
- **H5** (raw exceptions / D4 violation) — FIXED in this PR, commit `17a88dc90`
- **H7** (MCP opaque errors) — FIXED in this PR, commit `6c07ca353`
- **M6** (A1 violation / DataQueryService extraction) — FIXED in this PR, commit `35a26b8b3`

Remaining Track 3 items (not yet addressed):
- L3 / L5 (arg UX / architecture hygiene)
- L7 (FetchXML mode toggle — TUI UX)
- L12 (auth prompt optimization — needs its own design pass)
- L16 (docs drift — flag-name discrepancies)
- M2 (logs command missing — decision: implement or strike)
- M7 (sovereign cloud Maker URLs — larger refactor)

None of the remaining Track 3 items is release-blocking, but several materially
improve the product and should be prioritized into the first post-v1 milestone.

---

## Environment State at Shakedown End

- **Sentinel**: still armed at `.claude/state/shakedown-active.json`.
  Phase 7 cleanup deletes it before this session closes.
- **`ppds_EnableFeatureX`** in `PPDS Demo - Dev`: **still `true`**. User
  elected to leave it (not material; feature flag not observed to have
  readers).
- **Plugin traces**: the 100 deleted have been replaced by new ones
  (environment actively generating). Net effect on test fixtures: none.
- **Web resource `63bfbf17-…`**: was already managed/published; the
  publish action was idempotent in practice.

---

*End of report.*
