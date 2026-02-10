# Implement Plan

Execute a checked-in implementation plan end-to-end using parallel agents for maximum throughput.

## Input
$ARGUMENTS = path to the plan file (e.g., `docs/plans/2026-02-08-query-engine-v3-design.md`)

## Process

### Step 1: Read & Analyze the Plan
- Read the plan file at $ARGUMENTS (resolve relative to repo root if needed)
- Identify ALL phases, their dependencies, and parallelization opportunities
- Identify which phases are SEQUENTIAL (have dependencies) vs PARALLEL (independent streams)

### Step 2: Assess Current State
- Check git status and current branch
- Search for any existing work (worktrees, branches) related to this plan
- Check if a feature branch exists; if not, create one from the plan name
- Determine what has already been implemented vs what remains

### Step 3: Create Task Tracking
- Use TaskCreate to build a task list from the plan phases
- Set up dependencies between tasks using addBlockedBy/addBlocks
- Mark any already-completed work as done

### Step 4: Execute Phase by Phase

For each phase gate in the plan:

**A. Dispatch Parallel Agents**
- For ALL independent tasks within the current phase, dispatch background agents using the Task tool with `run_in_background: true`
- Each agent prompt MUST include:
  - The specific task/subtask from the plan
  - Full file paths and context about the codebase
  - Instructions to read existing code before writing
  - Build verification command to run before finishing
  - Reminder: no shell redirections (2>&1, >, >>)
- Maximize parallelism: if 4 tasks are independent, launch 4 agents simultaneously

**B. Monitor & Collect Results**
- Wait for all agents in the current phase to complete
- Review each agent's summary (do NOT read full transcripts - save context)
- Mark tasks as completed

**C. Verify Phase Gate**
- Run `dotnet build` (or appropriate build command) to verify 0 errors
- Run tests: `dotnet test --filter Category!=Integration -v q --no-build`
- If failures exist, dispatch a fix agent with the specific error details
- Do NOT proceed to next phase until gate passes

**D. Commit at Phase Boundaries**
- After each phase gate passes, commit the work with a descriptive message
- Use conventional commit format: `feat(query): Phase N - description`

### Step 5: Repeat for Subsequent Phases
- Move to the next phase only after the current phase gate passes
- For phases with parallel streams (e.g., "Phase 2-4: PARALLEL STREAMS"), dispatch ALL streams simultaneously as separate agents
- Continue until all phases are complete

### Step 6: Final Verification
- Full solution build
- Full test suite run
- Use the `superpowers:requesting-code-review` skill for final review
- Commit any remaining changes

## Rules

1. **YOU are the orchestrator** - agents do the work, you review and coordinate
2. **Minimize context drain** - don't read agent output files unless there's a failure. Trust agent summaries.
3. **Parallel by default** - if tasks don't depend on each other, run them simultaneously
4. **Sequential when required** - respect phase gates and dependency chains
5. **Commit early, commit often** - commit after each successful phase gate
6. **Fix forward** - when tests fail, dispatch a fix agent rather than debugging yourself
7. **Never skip verification** - always build + test before declaring a phase complete
