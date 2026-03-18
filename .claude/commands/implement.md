# Implement Plan

Execute a checked-in implementation plan end-to-end using parallel agents for maximum throughput. You are the orchestrator - agents do the work, you review, fix, commit, and advance.

## Prerequisites

Key tools and skills used throughout:
- `Agent` tool for parallel subagent dispatch
- `/review` for impartial code review at phase gates
- `/verify` and `/qa` for verification before declaring any phase done
- `/debug` for systematic debugging when tests fail
- `/gates` for mechanical pass/fail checks
- `/converge` for review-fix convergence loops

## Input
$ARGUMENTS = path to the plan file (e.g., `docs/plans/2026-02-08-query-engine-v3-design.md`)

## Process

### Step 1: Read & Analyze the Plan
- Read the plan file at $ARGUMENTS (resolve relative to repo root if needed)
- Identify ALL phases, their dependencies, and parallelization opportunities
- Identify which phases are SEQUENTIAL (have dependencies) vs PARALLEL (independent streams)
- Note the quality gates defined in the plan

### Step 2: Load Spec Context

Before dispatching any agents, load the specification context that will be injected into every subagent prompt.

**A. Read Foundation**
- Read `specs/CONSTITUTION.md` — full content will be injected into every subagent prompt
- Read `specs/README.md` — maps code paths to specs

**B. Identify Relevant Specs**
- From the plan, identify which source directories/files will be touched
- Map each to its spec using the README.md code column:
  - `src/PPDS.Dataverse/Pooling/` → `specs/connection-pooling.md`
  - `src/PPDS.Dataverse/Query/` → `specs/query.md`
  - `src/PPDS.Cli/Tui/` → `specs/tui.md` + `specs/tui-foundation.md`
  - `src/PPDS.Cli/Commands/` → `specs/cli.md`
  - `src/PPDS.Mcp/` → `specs/mcp.md`
  - `src/PPDS.Migration/` → `specs/migration.md`
  - `src/PPDS.Auth/` → `specs/authentication.md`
  - `src/PPDS.Extension/src/panels/` → `specs/per-panel-environment-scoping.md` (if panels) or relevant spec
  - `src/PPDS.Extension/` → check `specs/README.md` for extension-related specs
- Always include `specs/architecture.md`
- Read each relevant spec and extract the `## Acceptance Criteria` section

**C. Build Spec Context Block**
Construct a text block that will be prepended to every subagent prompt:

```
## Constitution (MUST comply — violations are defects)
{full CONSTITUTION.md content}

## Relevant Specifications — Acceptance Criteria
### {spec-name}.md
{AC table rows from that spec}

Your implementation MUST satisfy these criteria. If your task conflicts
with a spec or constitution principle, STOP and report the conflict
to the orchestrator — do not silently deviate.
```

### Step 3: Assess Current State
- Check git status and current branch
- Search for any existing work (worktrees, branches) related to this plan
- Check if a feature branch exists; if not, create one from the plan name
- Determine what has already been implemented vs what remains
- Check git log to see if prior phases were already committed

### Step 3.5: Initialize Workflow State

Update `.workflow/state.json` to record that implementation has started:
1. Read the file (create `{}` if missing)
2. Set `branch` to the current branch name
3. Set `spec` to the path of the primary spec associated with the plan
4. Set `plan` to the plan file path ($ARGUMENTS)
5. Set `started` to the current ISO 8601 timestamp
6. Write the file back

### Step 4: Create Task Tracking
- Use TaskCreate to build a task list from the plan phases
- Set up dependencies between tasks using addBlockedBy/addBlocks
- Mark any already-completed work as done

### Step 5: Execute Each Phase

For EACH phase in the plan, repeat this cycle:

**A. Dispatch Agents**
- For ALL independent tasks within the current phase, dispatch background agents using the Task tool with `run_in_background: true`
- For parallel streams in a phase group (e.g., "Phase 2-4: PARALLEL"), dispatch ALL streams simultaneously
- Each agent prompt MUST include:
  - The specific task/subtask from the plan with full requirements
  - Full file paths and codebase context (what exists, what to read first)
  - Instructions to read existing code before writing anything
  - Build verification command to run before finishing
  - Test command to run and verify
  - The spec context block from Step 2 (constitution + relevant AC tables)
  - Reminder: no shell redirections (2>&1, >, >>)
- Maximize parallelism: if 4 tasks are independent, launch 4 agents simultaneously

**B. Collect Results**
- Wait for all agents in the current phase to complete
- Review each agent's summary (do NOT read full transcripts - save context)
- Mark tasks as completed

**B2. Cross-Agent Consistency Check**
When parallel agents implement the same semantic concept across surfaces (e.g., RPC + MCP + TUI + Extension), check for consistency BEFORE proceeding:
- Same field names and types across surfaces (e.g., `HasOverride` uses the same logic in MCP and RPC)
- Nullable/non-nullable agreement between C# DTOs and TypeScript interfaces
- Error codes defined in one layer are actually used in other layers
- Default values match across surfaces (e.g., "N/A" fallback in both mapper and TS type)
This prevents the most common parallel-agent defect: each agent delivers its slice correctly but cross-slice contracts are inconsistent.

**C. Verify Phase Gate**
- Run full solution build: `dotnet build PPDS.sln -v q` (or appropriate build command)
- Run full test suite: `dotnet test PPDS.sln --filter "Category!=Integration" -v q --no-build`
- Both MUST show 0 errors / 0 failures
- If build errors exist, dispatch a fix agent with the specific errors
- If test failures exist, dispatch a fix agent with the failing test names and error messages
- If specs with ACs are relevant to this phase, check: do the AC test methods pass?
  Run: `dotnet test --filter "FullyQualifiedName~{TestMethodFromAC}" -v q --no-build`
  for each AC referenced by this phase's tasks
- If the phase touches extension code (`src/PPDS.Extension/` directory):
  Invoke `/verify extension` to check daemon status, tree views, and panel state.
  Then invoke `/qa extension` to dispatch a blind verifier agent that tests the UI without seeing source code.
- If the phase touches TUI code (`src/PPDS.Cli/Tui/`):
  Invoke `/verify tui` to check TUI rendering.
- If the phase touches MCP code (`src/PPDS.Mcp/`):
  Invoke `/verify mcp` to check tool responses.
  Then invoke `/qa mcp` to dispatch a blind verifier for tool responses.
- If the phase touches CLI commands (`src/PPDS.Cli/Commands/`, not `Serve/`):
  Invoke `/qa cli` to verify command output matches expectations.
- Re-run verification after fixes. Do NOT proceed until gate passes AND /qa passes.

**D. Review**
- Invoke `/review` to dispatch an impartial reviewer for the phase's work
- The reviewer receives ONLY the diff, constitution, and ACs — NO implementation context (no plan, no task descriptions). It reviews code against specs, not against the plan.
- If the review identifies issues, dispatch fix agents before committing
- Only proceed to commit when review passes

**E. Commit the Phase**
- Stage all files for this phase: `git add` specific files (not `git add -A`)
- Commit with conventional format and descriptive message:
  ```
  feat(scope): Phase N - concise description

  Bullet points of what was added/changed.

  Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
  ```
- Each phase gets its OWN commit - do not batch multiple phases into one commit
- Exception: parallel streams within a phase group (e.g., Phases 2-4 running simultaneously) can share a commit since they're one logical gate

**F. Advance**
- Move to the next phase only after commit succeeds
- Update task tracking
- Continue until all phases are complete

### Step 6: Mandatory Tail — Full Verification Pipeline

After ALL phases are committed, you MUST run the complete verification pipeline. This is not optional. The whole point of /implement is that it does not declare victory after just running the phases — it proves the work is done.

**A. Gates**
Invoke `/gates` — full mechanical checks.

**B. Verify**
For each affected surface (detected from changed files across all phases):
- Extension changes → `/verify extension`
- TUI changes → `/verify tui`
- CLI changes → `/verify cli`
- MCP changes → `/verify mcp`

**C. QA**
For each affected surface:
- Extension → `/qa extension`
- CLI → `/qa cli`
- MCP → `/qa mcp`
- TUI → `/qa tui`

**D. Review**
Invoke `/review` for final comprehensive impartial review across all phases.

**E. Converge (if needed)**
If `/review` finds critical or important issues, invoke `/converge` to run the fix-review loop until clean.

**F. Final State Check**
- Verify git log shows clean commit history with one commit per phase
- Verify `.workflow/state.json` shows fresh timestamps for gates, verify, qa, and review
- All timestamps must be more recent than the `started` timestamp

## Rules

1. **YOU are the orchestrator** - agents do the work, you review and coordinate
2. **Minimize context drain** - trust agent summaries, don't read output files unless there's a failure
3. **Parallel by default** - if tasks don't depend on each other, run them simultaneously
4. **Sequential when required** - respect phase gates and dependency chains
5. **One commit per phase** - each phase gate produces exactly one commit with a clear message
6. **Review before commit** - always use code-reviewer agent before committing phase work
7. **Fix before advancing** - if build fails, tests fail, or review finds issues, fix them BEFORE committing. Dispatch fix agents rather than debugging yourself.
8. **Never skip verification** - always build + test + review before declaring a phase complete
9. **Continue until done** - execute ALL phases in the plan, don't stop early and ask permission
