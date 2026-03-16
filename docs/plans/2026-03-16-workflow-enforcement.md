# Workflow Enforcement Implementation Plan

**Goal:** Implement the mechanical enforcement system from `specs/workflow-enforcement.md` — hooks, workflow state tracking, skill updates/renames/creations, CLAUDE.md rewrite, and superpowers disabling.

**Spec:** `specs/workflow-enforcement.md` — AC-01 through AC-33

**Approach:** This is entirely `.claude/` infrastructure (skills, hooks, settings) and docs. No `src/` code changes. All work is markdown, Python, and JSON.

---

## Dependencies and Sequencing

```
Phase 1 (Foundation) ─────────────────────────────────────────
  ├─ gitignore entry
  ├─ /write-skill (establishes conventions for all other skills)
  └─ /status (simple, tests state file reading)

Phase 2 (Hooks) ──────────────────────────────────────────────
  ├─ post-commit-state.py (state invalidation)
  ├─ pre-commit-validate.py enhancement (workflow warning)
  ├─ pr-gate.py (hard gate)
  ├─ SessionStart hook (context injection)
  ├─ Stop hook (completion summary)
  └─ settings.json updates

Phase 3 (Existing Skill Updates) ─── can parallel with Phase 2
  ├─ /gates (write state)
  ├─ /verify (write state)
  ├─ /qa (write state)
  ├─ /review (write state + remove superpowers)
  ├─ /converge (write state + cycle handling)
  └─ /implement (remove superpowers + mandatory tail)

Phase 4 (Renames + Rewrites) ─── can parallel with Phase 2/3
  ├─ /retrospective → /retro
  ├─ /webview-cdp → /ext-verify
  ├─ /webview-panels + /panel-design → /ext-panels
  └─ /debug (absorb systematic-debugging)

Phase 5 (New Skills) ─── depends on Phase 1 (/write-skill)
  ├─ /design
  ├─ /pr
  ├─ /shakedown
  ├─ /mcp-verify
  └─ /cli-verify

Phase 6 (Integration) ─── depends on Phases 2-5
  ├─ CLAUDE.md workflow section rewrite
  ├─ specs/README.md update (add workflow-enforcement spec)
  └─ Superpowers disabling in settings.json
```

**Parallelism:** Phases 2, 3, and 4 have no dependencies on each other and can run in parallel. Phase 5 depends only on Phase 1. Phase 6 must be last.

---

## Phase 1: Foundation

Establishes the workflow state infrastructure and skill authoring conventions that everything else depends on.

### Task 1.1: Add gitignore entry

- [ ] Add `.claude/workflow-state.json` to `.gitignore`
- [ ] **AC-27**

### Task 1.2: Create `/write-skill`

Create `.claude/skills/write-skill/SKILL.md` with:

- [ ] Naming convention: `{action}` or `{action}-{qualifier}`, kebab-case
- [ ] Directory structure: `.claude/skills/<name>/SKILL.md` + optional supporting files
- [ ] Frontmatter patterns: `name`, `description` (for AI discoverability), `user-invocable` (true/false)
- [ ] Description writing guidance: describe what it does and when to use it, not the technology
- [ ] Workflow state integration: when and how a skill should write to `workflow-state.json`
- [ ] Examples of good vs. bad skill names (with rationale)
- [ ] **AC-23**

### Task 1.3: Create `/status`

Create `.claude/commands/status.md` (simple command, no supporting files needed):

- [ ] Read `.claude/workflow-state.json`
- [ ] Display same format as SessionStart hook output (see spec lines 146-153)
- [ ] Annotate stale entries (gates.commit_ref vs HEAD mismatch)
- [ ] Handle missing file gracefully ("No workflow state. Run /gates, /verify, /qa, /review to begin tracking.")
- [ ] **AC-31**

**Gate:** Commit Phase 1. Verify `/write-skill` content reads correctly, `/status` handles missing state file.

---

## Phase 2: Hooks

Python hook scripts + settings.json wiring. All hooks read/write `.claude/workflow-state.json`.

### Task 2.1: Post-Commit Hook (`post-commit-state.py`)

Create `.claude/hooks/post-commit-state.py`:

- [ ] Read `.claude/workflow-state.json` (graceful skip if missing or invalid JSON)
- [ ] Clear `gates.passed` (set to `null`)
- [ ] Update `last_commit` to current HEAD (`git rev-parse HEAD`)
- [ ] Write updated state file
- [ ] Handle errors gracefully (file permission, corrupted JSON, detached HEAD) — never block
- [ ] **AC-05, AC-24**

### Task 2.2: Enhance Pre-Commit Hook (`pre-commit-validate.py`)

Modify existing `.claude/hooks/pre-commit-validate.py`:

- [ ] After existing build/test/lint validation, add workflow state check
- [ ] If files under `src/` are staged AND `gates.commit_ref` doesn't match staging state, emit warning to stderr
- [ ] Warning is informational only — does NOT change exit code
- [ ] **AC-13**

### Task 2.3: PR Gate Hook (`pr-gate.py`)

Create `.claude/hooks/pr-gate.py`:

- [ ] Read `.claude/workflow-state.json` (if missing or corrupt: exit 2 with "No workflow state found")
- [ ] Check `gates.commit_ref` matches current HEAD
- [ ] Check `verify` has at least one surface with timestamp
- [ ] Check `qa` has at least one surface with timestamp
- [ ] Check `review.passed` has a timestamp
- [ ] On any failure: exit 2 with specific message listing missing steps
- [ ] On all pass: exit 0 (allow PR creation)
- [ ] Skip enforcement on `main` branch
- [ ] **AC-06, AC-07, AC-08, AC-09**

### Task 2.4: SessionStart Hook

Create `.claude/hooks/session-start-workflow.py` (or `.cmd` wrapper):

- [ ] Read `.claude/workflow-state.json` if it exists
- [ ] Read current branch name
- [ ] Compare `gates.commit_ref` to HEAD for staleness
- [ ] Output formatted workflow state summary (spec lines 146-153)
- [ ] If no state file and not on `main`: output full required workflow sequence
- [ ] If on `main`: skip workflow injection
- [ ] **AC-10, AC-11**

### Task 2.5: Stop Hook

Create `.claude/hooks/session-stop-workflow.py` (or `.cmd` wrapper):

- [ ] Read `.claude/workflow-state.json` if it exists
- [ ] Check for uncommitted changes (`git status --porcelain`)
- [ ] Output formatted completion summary (spec lines 222-230)
- [ ] If no state file: no output (normal for non-feature work)
- [ ] Never exit non-zero — cannot block session end
- [ ] **AC-12**

### Task 2.6: Update `settings.json`

Update `.claude/settings.json`:

- [ ] Add `PostToolUse` section with `Bash(git commit:*)` → `post-commit-state.py`
- [ ] Add `Bash(gh pr create:*)` → `pr-gate.py` to existing `PreToolUse` array
- [ ] Add `SessionStart` hook entry for `session-start-workflow.py`
- [ ] Add `Stop` hook entry for `session-stop-workflow.py`

**Gate:** Commit Phase 2. Test hooks manually: create a dummy state file, verify post-commit clears gates, verify PR gate blocks without state, verify SessionStart outputs summary.

---

## Phase 3: Existing Skill Updates (Workflow State Integration)

Add workflow state writes to existing skills. Remove superpowers dependencies.

### Task 3.1: Update `/gates`

Edit `.claude/commands/gates.md`:

- [ ] After all gates pass, add instruction: write `gates.passed` (ISO timestamp) and `gates.commit_ref` (current HEAD) to `.claude/workflow-state.json`
- [ ] If any gate fails, do NOT write state (failed gates are not "passed")
- [ ] **AC-01**

### Task 3.2: Update `/verify`

Edit `.claude/commands/verify.md`:

- [ ] After verification passes for a surface, add instruction: write `verify.{surface}` (ISO timestamp) to `.claude/workflow-state.json`
- [ ] Surface key matches mode argument: `ext`, `tui`, `mcp`, `cli`
- [ ] **AC-02**

### Task 3.3: Update `/qa`

Edit `.claude/commands/qa.md`:

- [ ] After QA passes for a surface, add instruction: write `qa.{surface}` (ISO timestamp) to `.claude/workflow-state.json`
- [ ] **AC-03**

### Task 3.4: Update `/review`

Edit `.claude/commands/review.md`:

- [ ] After review completes, add instruction: write `review.passed` (ISO timestamp) and `review.findings` (count) to `.claude/workflow-state.json`
- [ ] Remove `subagent_type: 'superpowers:code-reviewer'` — dispatch as general-purpose subagent with same isolation constraints (diff + constitution + ACs only, no implementation context)
- [ ] **AC-04, AC-29**

### Task 3.5: Update `/converge`

Edit `.claude/commands/converge.md`:

- [ ] At start of fix cycle: clear `gates.passed` in `.claude/workflow-state.json`
- [ ] After final cycle passes: run `/gates`, which writes fresh state
- [ ] Retain all existing convergence tracking, cycle evaluation, stall detection
- [ ] **AC-25, AC-26**

### Task 3.6: Update `/implement`

Edit `.claude/commands/implement.md`:

- [ ] Remove ALL superpowers references: `superpowers:using-superpowers`, `superpowers:dispatching-parallel-agents`, `superpowers:verification-before-completion`, `superpowers:requesting-code-review`, `superpowers:systematic-debugging`
- [ ] Replace with PPDS-native equivalents (use Agent tool directly for parallel dispatch, reference `/review` for code review, reference `/debug` for debugging)
- [ ] Add workflow state writes on start: `branch`, `spec`, `plan`, `started`
- [ ] Add mandatory tail after final phase: `/gates` → `/verify` (affected surfaces) → `/qa` → `/review` → `/converge` (if needed)
- [ ] **AC-14, AC-28**

**Gate:** Commit Phase 3. Verify no superpowers references remain in `/implement` and `/review`. Spot-check one skill's state write instruction.

---

## Phase 4: Renames and Rewrites

Rename existing skills and rewrite `/debug`.

### Task 4.1: Rename `/retrospective` → `/retro`

- [ ] Rename `.claude/skills/retrospective/` directory to `.claude/skills/retro/`
- [ ] Update SKILL.md `name:` frontmatter to `retro`
- [ ] Update any internal references to the old name

### Task 4.2: Rename `/webview-cdp` → `/ext-verify`

- [ ] Rename `.claude/skills/webview-cdp/` directory to `.claude/skills/ext-verify/`
- [ ] Update SKILL.md `name:` frontmatter to `ext-verify`
- [ ] Update `description:` for discoverability: "How to interact with VS Code extension webview panels for verification — Playwright Electron, screenshots, clicks, keyboard. Use when testing extension UI changes."
- [ ] Update references in `/verify` and `/qa` that mention `webview-cdp`
- [ ] **AC-21**

### Task 4.3: Merge `/webview-panels` + `/panel-design` → `/ext-panels`

- [ ] Rename `.claude/skills/webview-panels/` directory to `.claude/skills/ext-panels/`
- [ ] Read `.claude/commands/panel-design.md` content
- [ ] Merge relevant panel-design content into `/ext-panels` SKILL.md
- [ ] Delete `.claude/commands/panel-design.md`
- [ ] Update SKILL.md `name:` and `description:` frontmatter
- [ ] **AC-21**

### Task 4.4: Rewrite `/debug`

Major rewrite of `.claude/commands/debug.md`:

- [ ] Keep existing PPDS-specific content: surface detection, build commands, test commands, iterative fix loop
- [ ] Add systematic debugging discipline from superpowers:
  - 4-phase process: Root Cause Investigation → Pattern Analysis → Hypothesis Testing → Implementation
  - Iron Law: "NO FIXES WITHOUT ROOT CAUSE INVESTIGATION FIRST"
  - 3-fix escalation rule: if 3+ fixes fail, question the architecture, discuss with user
  - Red flags / rationalization table
  - Root-cause tracing technique (trace backward through call chain)
  - Defense-in-depth validation (validate at every layer)
  - Condition-based waiting (replace arbitrary timeouts with condition polling)
  - User frustration signals recognition
- [ ] Remove any superpowers references
- [ ] Convert from command to skill (`.claude/skills/debug/SKILL.md` + supporting files for root-cause-tracing.md, defense-in-depth.md, condition-based-waiting.md)
- [ ] **AC-20**

**Gate:** Commit Phase 4. Verify renamed skills are in correct directories. Verify `/debug` has all systematic debugging sections. Verify `/panel-design` is deleted.

---

## Phase 5: New Skills

All new skills follow `/write-skill` conventions from Phase 1.

### Task 5.1: Create `/design`

Create `.claude/skills/design/SKILL.md`:

- [ ] Purpose: brainstorm → spec, replacing `superpowers:brainstorming`
- [ ] Process: enter plan mode → ask clarifying questions (one at a time, multiple choice preferred) → propose 2-3 approaches → present design in sections → write spec
- [ ] Auto-load constitution (`specs/CONSTITUTION.md`) and spec template (`specs/SPEC-TEMPLATE.md`) into context at start
- [ ] Output location: `specs/` (not `docs/superpowers/specs/`)
- [ ] Creates worktree when ready to commit spec (follows `.worktrees/<name>` convention)
- [ ] Commits spec file on approval
- [ ] **AC-17, AC-18**

### Task 5.2: Create `/pr`

Create `.claude/skills/pr/SKILL.md`:

- [ ] Rebase current branch on main (handle conflicts — if conflicts exist, present to user)
- [ ] Create PR with structured body (summary, test plan, generated-by footer)
- [ ] Poll CI checks: `gh pr checks <pr-number>` every 30s for 2 min, then every 2 min
- [ ] Poll Gemini reviews: `gh api repos/{owner}/{repo}/pulls/{number}/reviews`
- [ ] Max wait: 15 minutes total. On timeout: report current status, what's still pending
- [ ] When both complete: triage Gemini comments
  - Fix valid ones (mechanical issues, real bugs)
  - Dismiss invalid ones with rationale (reply explaining why)
  - Reply to EACH comment individually on the PR
- [ ] Present summary to user: PR URL, CI status, comment count, actions taken per comment
- [ ] Write `pr.url` and `pr.created` to workflow state
- [ ] **AC-15, AC-16, AC-30**

### Task 5.3: Create `/shakedown`

Create `.claude/skills/shakedown/SKILL.md`:

- [ ] Phase 1: Scope declaration — user declares which surfaces to test (ext, tui, mcp, cli)
- [ ] Phase 2: Test matrix creation — enumerate features per surface from specs/code, create explicit checklist
- [ ] Phase 3: Interactive verification per surface:
  - Extension: use `/ext-verify` for screenshots and interaction
  - TUI: use `/tui-verify` for PTY interaction
  - MCP: use `/mcp-verify` for tool invocation
  - CLI: use `/cli-verify` for command execution
- [ ] Phase 4: Parity comparison — for features in multiple surfaces, side-by-side comparison with "who does it better"
- [ ] Phase 5: Architecture audit — trace code paths, check service bypasses, find silent error swallowing
- [ ] Phase 6: Findings document — output to `docs/qa/{date}-{scope}.md`
- [ ] Gap check before wrap-up: enumerate untested features, require user sign-off on skipping
- [ ] **AC-22, AC-32, AC-33**

### Task 5.4: Create `/mcp-verify`

Create `.claude/skills/mcp-verify/SKILL.md`:

- [ ] Supporting knowledge for `/verify mcp` and `/qa mcp`
- [ ] MCP Inspector usage: `npx @modelcontextprotocol/inspector --cli --server "ppds-mcp-server"`
- [ ] Direct tool invocation patterns
- [ ] Response validation (JSON structure, expected fields)
- [ ] Session option testing (`--profile`, `--environment`, `--read-only`, `--allowed-env`)
- [ ] Common failure modes and diagnostics

### Task 5.5: Create `/cli-verify`

Create `.claude/skills/cli-verify/SKILL.md`:

- [ ] Supporting knowledge for `/verify cli` and `/qa cli`
- [ ] Build: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0`
- [ ] Run: `.\src\PPDS.Cli\bin\Debug\net10.0\ppds.exe <command> <args>`
- [ ] stdout is data (pipeable), stderr is status/progress (Constitution I1)
- [ ] Exit code validation (0 = success)
- [ ] Common commands and expected output patterns

**Gate:** Commit Phase 5. Verify all new skills are in correct directory structure. Spot-check `/design` loads constitution reference. Spot-check `/pr` has polling and comment reply logic.

---

## Phase 6: Integration

Final integration, CLAUDE.md rewrite, and superpowers disabling. Must be last — depends on all previous phases.

### Task 6.1: Rewrite CLAUDE.md Workflow Section

Edit `CLAUDE.md`:

- [ ] Replace current Workflow section (lines 56-66) with the decision tree from the spec (spec lines 276-317)
- [ ] Includes: required sequence, bug fix track, enforcement reference, STOP conditions, autonomy scope, external review handling
- [ ] Update skill references throughout: `@webview-cdp` → `/ext-verify`, `@webview-panels` → `/ext-panels`
- [ ] Verify total line count stays under 100 (governance limit)

### Task 6.2: Update `specs/README.md`

- [ ] Add `workflow-enforcement.md` to the spec index under an appropriate category

### Task 6.3: Disable Superpowers

- [ ] Add `"enabledPlugins": { "superpowers@claude-plugins-official": false }` to `.claude/settings.json`
- [ ] **AC-19**
- [ ] Verify: grep all `.claude/commands/` and `.claude/skills/` for "superpowers" — zero matches expected

### Task 6.4: Final Verification

- [ ] Start a new session on this branch
- [ ] Verify SessionStart hook fires and shows workflow state
- [ ] Run `/status` — verify output
- [ ] Run `/gates` — verify `workflow-state.json` is created with gates entry
- [ ] Commit something — verify post-commit hook clears gates
- [ ] Attempt `gh pr create` without completing workflow — verify PR gate blocks
- [ ] Complete workflow (gates + verify + qa + review) — verify PR gate allows

**Gate:** Commit Phase 6. Full workflow test. PR creation.
