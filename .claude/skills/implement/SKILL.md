---
name: implement
description: Implement Plan
---

# Implement Plan

## Pipeline Mode Detection

If the environment variable `PPDS_PIPELINE=1` is set, this skill is being invoked by the
pipeline orchestrator. In pipeline mode:
- Execute Steps 1-5 only (plan analysis through phase execution)
- Skip Step 6 (Mandatory Tail) — the pipeline orchestrator runs gates/verify/qa/review
  as separate `claude -p` sessions with their own timeouts and monitoring
- Exit cleanly after all phases are committed

In interactive mode (`PPDS_PIPELINE` not set), execute the full process including Step 6.

---

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
$ARGUMENTS = path to the plan file (e.g., `.plans/2026-02-08-query-engine-v3-design.md`)

If no $ARGUMENTS is provided, look for the spec referenced in `.workflow/state.json` (the `spec` field). Read the spec and generate an implementation plan as Step 0, saving it to `.plans/` in the current worktree. Then proceed with Step 1 using the generated plan.

## Process

### Step 0: Generate Plan from Spec (only if no plan argument)
- Read the spec file from `specs/` (identified via workflow state or by scanning `specs/` for the most recently modified spec)
- Generate a phased implementation plan following the spec's acceptance criteria
- Save to `.plans/{date}-{spec-name}.md`
- Continue to Step 1 with the generated plan
**Fallback — no spec found:** If no relevant spec is found (workflow state has no `spec` field AND no spec in `specs/` matches the current work domain):
- Prompt user: "No spec found for this work. (1) Run `/design` to create one, or (2) continue without a spec?"
- If user chooses `/design`: stop and instruct user to run `/design`
- If user chooses continue: check for `.plans/context.md` and generate a plan from issue context if available, otherwise ask the user to describe the work for plan generation

### Step 1: Read & Analyze the Plan
- Read the plan file at $ARGUMENTS (resolve relative to repo root if needed)
- Identify ALL phases, their dependencies, and parallelization opportunities
- Identify which phases are SEQUENTIAL (have dependencies) vs PARALLEL (independent streams)
- Note the quality gates defined in the plan
- **Findings reconciliation:** If the plan references a findings document (check for `**Findings:**` or links to a findings file), read it and extract all finding IDs (e.g., CC-01, V-15, CR-06). Cross-reference every finding ID against the plan text. Report any finding IDs that do not appear in the plan — these may have been accidentally dropped during plan authoring. Present the gap to the user before proceeding.
- **Shared-infrastructure scan:** Identify files that appear in multiple phases (e.g., `message-types.ts`, `shared.css`, `dom-utils.ts`). Verify these files are modified in an earlier sequential phase, not in parallel phases. If a shared file appears in parallel phases, flag it: either serialize those modifications or designate one phase as the owner of the shared file.

### Step 2: Load Spec Context

Before dispatching any agents, load the specification context that will be injected into every subagent prompt.

**A. Read Foundation**
- Read `specs/CONSTITUTION.md` — full content will be injected into every subagent prompt

**B. Identify Relevant Specs**
- From the plan, identify which source directories/files will be touched
- Grep all `specs/*.md` files for `**Code:**` frontmatter lines. Match each touched source directory against code path prefixes to find governing specs. This replaces hardcoded mappings and the README index.
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

**Note:** When creating new code paths for a spec (new directories, new files in new locations), update the spec's `**Code:**` frontmatter to include the new paths. This ensures frontmatter grep continues to discover the correct specs.

### Step 3: Assess Current State
- Check git status and current branch
- Search for any existing work (worktrees, branches) related to this plan
- Check if a feature branch exists; if not, create one from the plan name
- Determine what has already been implemented vs what remains
- Check git log to see if prior phases were already committed

### Step 3.5: Initialize Workflow State

Record that implementation has started:

```bash
python scripts/workflow-state.py set branch "$(git rev-parse --abbrev-ref HEAD)"
python scripts/workflow-state.py set spec "{spec-path}"
python scripts/workflow-state.py set plan "$ARGUMENTS"
python scripts/workflow-state.py set started now
python scripts/workflow-state.py set phase implementing
```

### Step 4: Create Task Tracking
- Use TodoWrite to build a task list from the plan phases
- Mark any already-completed work as done

### Step 4.5: Assess Model Selection

For each phase, assess complexity to choose the appropriate model for subagents:
- **Primary model (Opus):** Phases with >3 sub-steps, complex UI work (timelines, query builders, virtual scrolling), cross-cutting refactors touching >10 files, or Constitution-sensitive Dataverse service changes
- **Lighter model (Sonnet):** Mechanical phases — one-liner fixes, find-and-replace, boilerplate application, CSS-only changes, documentation updates

Do not hardcode model IDs in the plan. Assess at dispatch time based on the phase's actual complexity. When in doubt, use the primary model — the cost of a subagent re-dispatch exceeds the cost difference between models.

### Step 5: Execute Each Phase

For EACH phase in the plan, repeat this cycle:

**A. Dispatch Agents**
- For ALL independent tasks within the current phase, dispatch background agents using the Agent tool with `run_in_background: true`
- For parallel streams in a phase group (e.g., "Phase 2-4: PARALLEL"), dispatch ALL streams simultaneously
- Each agent prompt MUST include:
  - The specific task/subtask from the plan with full requirements
  - Full file paths and codebase context (what exists, what to read first)
  - Instructions to read existing code before writing anything
  - Build verification command to run before finishing
  - Test command to run and verify
  - The spec context block from Step 2 (constitution + relevant AC tables)
  - Reminder: no shell redirections (2>&1, >, >>)
  - Self-check gate: before reporting completion, run the relevant gate checks for your changed files. For TypeScript/extension changes: `npm run typecheck:all --prefix src/PPDS.Extension` and `npx eslint --quiet {changed-files}`. For C# changes: `dotnet build {project}.csproj -v q`. Report gate results in your summary — do not silently suppress failures.
- Maximize parallelism: if 4 tasks are independent, launch 4 agents simultaneously
- **Shared-file guard:** If multiple parallel agents will modify the same file (identified in Step 1's shared-infrastructure scan), either: (a) serialize those agents, (b) have the first agent create the shared additions and later agents import them, or (c) designate one agent as the file owner and have others list their additions as requirements for the owner.

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
- **AC coverage gate (Constitution I6):** For every AC in the relevant spec(s) that this phase claims to implement, verify:
  1. The spec AC table `Test` column is filled in (not empty, not "❌ no test yet")
  2. The referenced test method exists in the codebase
  3. The test passes: `dotnet test --filter "FullyQualifiedName~{TestMethodFromAC}" -v q --no-build`
  If any AC is missing a test, the phase gate FAILS. Do not proceed — dispatch an agent to write the missing tests. This is not optional — Constitution I6 makes untested ACs a defect.
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

  Co-Authored-By: {use the format from the system prompt}
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

**G. Continue Pipeline**
After final state check passes, proceed IMMEDIATELY to the tail verification pipeline. Do NOT stop to present a summary. The pipeline is: gates → verify → qa → review → pr. Execute end-to-end unless a step fails.

## Rules

1. **YOU are the orchestrator** - agents do the work, you review and coordinate
2. **Minimize context drain** - trust agent summaries, don't read output files unless there's a failure
3. **Parallel by default** - if tasks don't depend on each other, run them simultaneously
4. **Sequential when required** - respect phase gates and dependency chains
5. **One commit per phase** - each phase gate produces exactly one commit with a clear message
6. **Review before commit** - invoke `/review` before committing phase work
7. **Fix before advancing** - if build fails, tests fail, or review finds issues, fix them BEFORE committing. Dispatch fix agents rather than debugging yourself.
8. **Never skip verification** - always build + test + review before declaring a phase complete
9. **Continue until done** - execute ALL phases in the plan, don't stop early and ask permission
