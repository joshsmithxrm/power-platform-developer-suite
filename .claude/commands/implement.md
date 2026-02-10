# Implement Plan

Execute a checked-in implementation plan end-to-end using parallel agents for maximum throughput. You are the orchestrator - agents do the work, you review, fix, commit, and advance.

## Prerequisites

Before starting, invoke `superpowers:using-superpowers` to load all available skills. Key skills used throughout:
- `superpowers:dispatching-parallel-agents` for parallel agent orchestration
- `superpowers:verification-before-completion` before declaring any phase done
- `superpowers:requesting-code-review` at phase gates via code-reviewer agent
- `superpowers:systematic-debugging` when tests fail

## Input
$ARGUMENTS = path to the plan file (e.g., `docs/plans/2026-02-08-query-engine-v3-design.md`)

## Process

### Step 1: Read & Analyze the Plan
- Read the plan file at $ARGUMENTS (resolve relative to repo root if needed)
- Identify ALL phases, their dependencies, and parallelization opportunities
- Identify which phases are SEQUENTIAL (have dependencies) vs PARALLEL (independent streams)
- Note the quality gates defined in the plan

### Step 2: Assess Current State
- Check git status and current branch
- Search for any existing work (worktrees, branches) related to this plan
- Check if a feature branch exists; if not, create one from the plan name
- Determine what has already been implemented vs what remains
- Check git log to see if prior phases were already committed

### Step 3: Create Task Tracking
- Use TaskCreate to build a task list from the plan phases
- Set up dependencies between tasks using addBlockedBy/addBlocks
- Mark any already-completed work as done

### Step 4: Execute Each Phase

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
  - Reminder: no shell redirections (2>&1, >, >>)
- Maximize parallelism: if 4 tasks are independent, launch 4 agents simultaneously

**B. Collect Results**
- Wait for all agents in the current phase to complete
- Review each agent's summary (do NOT read full transcripts - save context)
- Mark tasks as completed

**C. Verify Phase Gate**
- Run full solution build: `dotnet build PPDS.sln -v q` (or appropriate build command)
- Run full test suite: `dotnet test PPDS.sln --filter "Category!=Integration" -v q --no-build`
- Both MUST show 0 errors / 0 failures
- If build errors exist, dispatch a fix agent with the specific errors
- If test failures exist, dispatch a fix agent with the failing test names and error messages
- Re-run verification after fixes. Do NOT proceed until gate passes.

**D. Review**
- Use `superpowers:requesting-code-review` agent to review the phase's work against the plan
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

### Step 5: Final Verification
- Full solution build
- Full test suite run (all categories except Integration)
- Verify git log shows clean commit history with one commit per phase
- Use `superpowers:requesting-code-review` for final comprehensive review

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
