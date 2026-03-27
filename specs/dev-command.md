# Dev Command

**Status:** Draft
**Last Updated:** 2026-03-26
**Code:** System-wide
**Surfaces:** N/A (personal shell utility — see Design Decisions)

---

## Overview

A general-purpose worktree dashboard and workflow tool that provides situational awareness across all active work in a git repository. Shows what needs attention, what's in progress, and what's done — organized by priority, not alphabetically. Works in any git repo; discovers repo-specific conventions (`.workflow/state.json`, `scripts/devcontainer.ps1`) and adapts.

### Goals

- **Situational awareness**: One command shows the state of all worktrees — dirty files, workflow stage, pipeline status, PR state — so you can decide what needs attention without opening each worktree individually.
- **Fast context switching**: Jump to any worktree by name (prefix match + tab completion), with cd in-place as default and new terminal tab as an option.
- **Repo-agnostic with progressive enhancement**: Core worktree navigation works in any git repo. Repos that provide `.workflow/state.json` or `scripts/devcontainer.ps1` get richer behavior automatically.

### Non-Goals

- **Build/test/lint wrapping**: `dotnet build` and `npm run ext:lint` don't need wrappers.
- **Replacing `/status` in Claude sessions**: The `/status` skill shows workflow detail inside a Claude session. `dev status` shows it from the terminal. Different audiences (human vs AI), same data.
- **Replacing ppdsw**: Workspace-level repo navigation stays in `ppdsw`. `dev` operates within a single repo.
- **Replacing devcontainer.ps1**: Container management logic stays in `devcontainer.ps1`. `dev` delegates to it for container subcommands.

---

## Architecture

```
Profile (dotfiles)              Repo (ppds or any repo)
┌──────────────┐                ┌──────────────────────────────┐
│ dev()        │───resolves────▶│ git root                     │
│  3-line shim │  git root      │                              │
└──────────────┘                │ .worktrees/                  │
       │                        │   ├── feat-a/                │
       │ calls                  │   │   └── .workflow/         │
       ▼                        │   │       └── state.json     │
┌──────────────┐                │   └── feat-b/                │
│ dev.ps1      │───discovers───▶│       └── .workflow/         │
│ (dotfiles)   │  conventions   │           ├── state.json     │
└──────────────┘                │           ├── pipeline.lock  │
       │                        │           └── pipeline.log   │
       │ delegates              │                              │
       ▼                        │ scripts/                     │
┌──────────────┐                │   └── devcontainer.ps1       │
│ devcontainer │◀───if present──│                              │
│ .ps1         │                └──────────────────────────────┘
└──────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| Profile function `dev()` | 3-line shim in PowerShell profile. Finds git root (walks up from cwd), calls `dev.ps1` from dotfiles. |
| `dev.ps1` | Core logic. Reads git worktree state, discovers repo conventions, renders dashboard, handles subcommands. Lives in dotfiles repo, not in ppds. |
| Convention: `.workflow/state.json` | Per-worktree workflow state written by observability system. `dev` reads this for stage display. Optional — dashboard degrades gracefully without it. |
| Convention: `.workflow/pipeline.lock` | PID file indicating a running pipeline. `dev` reads this for "in progress" grouping. |
| Convention: `.workflow/pipeline.log` | Pipeline stage log. `dev` reads last HEARTBEAT for elapsed time and activity signal. |
| Convention: `scripts/devcontainer.ps1` | Container management script. `dev` delegates container subcommands to it if present. |

### Dependencies

- Depends on: [workflow-enforcement.md](./workflow-enforcement.md) (`.workflow/state.json` schema)
- Uses: `gh` CLI for PR status queries

---

## Specification

### Installation

The `dev` function is added to the user's PowerShell profile, either manually or via `ppds setup` (DX options). The profile function is a thin shim:

```powershell
function dev {
    $root = git rev-parse --show-toplevel 2>$null
    if ($root) {
        # Resolve to main repo root when inside a worktree
        $commonDir = git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null
        if ($commonDir -and (Split-Path $commonDir -Leaf) -eq '.git') {
            $root = Split-Path $commonDir
        }
    }
    # Call dev.ps1 from dotfiles (or fallback location)
    $devScript = Join-Path $env:USERPROFILE 'dotfiles\scripts\dev.ps1'
    if (-not (Test-Path $devScript)) {
        $devScript = Join-Path $root 'scripts\dev.ps1'  # repo fallback
    }
    if (Test-Path $devScript) {
        & $devScript -RepoRoot $root @args
    } else {
        Write-Host "dev.ps1 not found" -ForegroundColor Red
    }
}
```

The script resolves to the main repo root even when invoked from inside a worktree subdirectory. This means `dev` works from `C:\VS\ppdsw\ppds\.worktrees\cmt-parity\src\PPDS.Cli\` — it walks up to the worktree root, then resolves to the main repo.

### Tab Completion

Registered in the profile alongside the function:

```powershell
Register-ArgumentCompleter -CommandName dev -ScriptBlock {
    param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
    # Worktree names
    $root = git rev-parse --show-toplevel 2>$null
    if ($root) {
        $commonDir = git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null
        if ($commonDir -and (Split-Path $commonDir -Leaf) -eq '.git') {
            $root = Split-Path $commonDir
        }
    }
    $wtDir = Join-Path $root '.worktrees'
    if (Test-Path $wtDir) {
        Get-ChildItem -Directory $wtDir |
            Where-Object { $_.Name -like "$wordToComplete*" } |
            ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_.Name, $_.Name, 'ParameterValue', $_.Name)
            }
    }
    # Subcommands
    @('status', 'run', 'pr', 'clean', 'help', 'up', 'shell', 'claude', 'down', 'sync', 'reset', 'push') |
        Where-Object { $_ -like "$wordToComplete*" } |
        ForEach-Object {
            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
        }
}
```

### Command Surface

| Command | Behavior |
|---------|----------|
| `dev` | Dashboard — print all worktrees grouped by attention priority, exit. |
| `dev <name>` | cd to worktree (prefix match). `Set-Location` in current terminal. |
| `dev <name> -tab` | Open worktree in new Windows Terminal tab. |
| `dev status [name]` | Detailed status for one worktree. Defaults to current worktree if omitted. |
| `dev run <name>` | Kick off pipeline on a worktree (calls `scripts/pipeline.py`). No cd needed. |
| `dev pr <name>` | Open the worktree's PR in the default browser. |
| `dev clean` | Remove worktrees whose branches are merged into main. Prune stale remote branches. |
| `dev up` | Delegate to `scripts/devcontainer.ps1 up`. Only available if script exists. |
| `dev shell [wt]` | Delegate to `scripts/devcontainer.ps1 shell [wt]`. |
| `dev claude [wt]` | Delegate to `scripts/devcontainer.ps1 claude [wt]`. |
| `dev down` | Delegate to `scripts/devcontainer.ps1 down`. |
| `dev sync` | Delegate to `scripts/devcontainer.ps1 sync`. |
| `dev reset` | Delegate to `scripts/devcontainer.ps1 reset`. |
| `dev push [wt]` | Delegate to `scripts/devcontainer.ps1 push [wt]`. |

### Subcommand vs Worktree Disambiguation

Reserved subcommands: `status`, `run`, `pr`, `clean`, `up`, `shell`, `claude`, `down`, `sync`, `reset`, `push`, `help`.

Any argument that is not a reserved subcommand is treated as a worktree name prefix. If no worktree matches, print an error with available names. If multiple worktrees match, print the ambiguous matches and exit.

### Dashboard Output

#### Grouping Logic

Worktrees are categorized into groups based on state. A worktree appears in exactly one group, evaluated in this order:

1. **Needs attention** — any of:
   - Has dirty files AND no active pipeline
   - Pipeline failed or stalled (lock file exists, PID dead or activity=stalled)
   - Workflow steps incomplete and no pipeline running
2. **In progress** — active pipeline (lock file exists, PID alive, activity=active or idle)
3. **PRs open** — `pr.url` exists in state.json. Subgroups: comments to triage, waiting on CI, approved.
4. **Ready to clean** — branch is merged into main (checked via `git branch --merged main`)
5. **Design** — worktree has a spec but no implementation commits (state.json has `spec` but no `gates`, no `implemented`)
6. **Other** — fallback for worktrees that don't match any group (bare worktrees, no state file)

Empty groups are not displayed.

#### Output Format

```
ppds dev                                        6 worktrees
──────────────────────────────────────────────────────────

  Needs attention:
  → cmt-parity         +6  5 dirty   needs qa, review
    v1-polish          +6  2 dirty   needs review

  In progress:
    pipeline-observ…   pipeline → verify (3m, active)

  PRs open:
    plugin-registr…    #699  waiting on CI
    tui-refactoring    #703  2 comments

  Design:
    dev-script-ux      spec in progress

──────────────────────────────────────────────────────────
  dev <name>  jump · dev status <name>  detail · --help
```

- Header shows repo name and total worktree count (I4 compliance — user can verify completeness).
- The main worktree is not shown in the grouped list (it is not a feature worktree).
- `→` prefix marks the current worktree (detected from cwd).
- Worktree names are truncated to 20 characters with `…` if needed.
- `+N` = commits ahead of main.
- `N dirty` = count of modified/untracked files.
- `needs X, Y` = workflow steps remaining before PR (derived from state.json).
- Pipeline display: `pipeline → {stage} ({elapsed}, {activity})`.
- PR display: `#{number}  {status}` where status is one of: `N comments`, `waiting on CI`, `approved`, `changes requested`, `waiting on review`.

#### Colors

| Element | Color |
|---------|-------|
| Group headers | Yellow |
| Current worktree `→` | Green |
| Dirty count | Red |
| "needs ..." | DarkYellow |
| Pipeline active | Cyan |
| PR approved | Green |
| PR changes requested | Red |
| PR waiting | DarkGray |
| Footer hints | DarkGray |

### Data Collection

#### Git State (always available)

For each worktree:
1. `git worktree list --porcelain` — paths and HEADs
2. `git -C <worktree> status --porcelain` — dirty file count
3. `git -C <worktree> rev-list --count origin/main..HEAD` — commits ahead
4. `git -C <worktree> branch --show-current` — branch name
5. `git branch --merged main` — for "ready to clean" detection

#### Workflow State (when `.workflow/state.json` exists)

Read per worktree. Schema defined in [workflow-enforcement.md](./workflow-enforcement.md). Extract:
- `gates.passed` + `gates.commit_ref` — gates status and staleness
- `verify.*` — which surfaces verified
- `qa.*` — which surfaces QA'd
- `review.passed` + `review.findings` — review status
- `pr.url` + `pr.created` — PR exists
- `spec` — spec path (for design group detection)

#### Pipeline State (when `.workflow/pipeline.lock` exists)

1. Read PID from lock file.
2. Check if PID is alive (`Get-Process -Id $pid -ErrorAction SilentlyContinue`).
3. If alive, parse last HEARTBEAT from `.workflow/pipeline.log` for: stage, elapsed, activity, output_bytes.
4. If dead, classify as stalled/failed → "needs attention" group.

#### PR State (when `pr.url` exists in state.json, requires `gh` CLI)

For each worktree with a PR:
1. Extract PR number from URL.
2. `gh pr view <number> --json state,reviewDecision,comments,statusCheckRollup` — single API call per PR.
3. Derive status: comment count, review decision, check status.

**Caching:** PR state is cached to `.workflow/pr-cache.json` with a TTL of 60 seconds. When cache is stale, dashboard fetches fresh data synchronously before rendering (blocks until complete). This keeps output always accurate. With parallelized `gh` calls, the fetch adds ~1 second for 5 PRs. This prevents GitHub API rate limiting when running `dev` frequently.

### `dev status [name]` — Detail View

Shows full workflow state for a single worktree:

```
cmt-parity (feature/cmt-parity)
──────────────────────────────────────────────
Branch:    feature/cmt-parity
           +6 ahead, 0 behind main
Started:   2026-03-26 23:12 UTC
Spec:      specs/migration.md
Issues:    (none)
Dirty:     2 modified, 3 untracked

Workflow:
  ✓ gates      passed 23:19 (commit 9d09045, current)
  ✓ verify     cli 23:24 · tui 23:24
  ✗ qa         not completed
  ✗ review     not completed
  ✗ PR         not created
  Next: /qa → /review → /pr

Pipeline:  not running
PR:        not created
```

When a pipeline is active:

```
Pipeline:  ACTIVE (PID 12345)
  Stage:   verify (elapsed: 3m 12s)
  Tool:    Edit src/PPDS.Extension/panels.ts
  Git:     2 modified files, 1 commit this stage
  Output:  48KB
  Activity: active (12s ago)
```

When a PR exists:

```
PR:        #703 (open)
  URL:     https://github.com/joshsmithxrm/power-platform-developer-suite/pull/703
  Reviews: approved (Gemini)
  Checks:  3/3 passed
  Comments: 2 (0 unresolved)
```

### `dev <name>` — Jump to Worktree

1. Match `<name>` against worktree directory names using prefix match.
2. If exactly one match: `Set-Location <worktree-path>`.
3. If zero matches: print error with available names.
4. If multiple matches: print ambiguous matches, do not jump.

With `-tab` flag and Windows Terminal detected (`$env:WT_SESSION`):
```powershell
wt -w 0 nt --title "<worktree-name>" -d "<worktree-path>"
```

Fallback when not in Windows Terminal:
```powershell
Start-Process pwsh -WorkingDirectory "<worktree-path>"
```

### `dev run <name>` — Start Pipeline

1. Resolve worktree path.
2. Check for `.workflow/pipeline.lock` — if pipeline already running, error.
3. Check that `scripts/pipeline.py` exists in the repo root — if not, print "No pipeline script found in this repo" and exit.
4. Look for spec file in worktree's state.json (`spec` field) or prompt.
5. Execute via direct invocation (no shell, per Constitution S2):
   ```powershell
   & python "$repoRoot/scripts/pipeline.py" --worktree $worktreePath --spec $specPath
   ```
6. Runs in background via `Start-Process`. Print PID and "use `dev status <name>` to monitor."

### `dev pr <name>` — Open PR

1. Read `pr.url` from worktree's `.workflow/state.json`.
2. If present: `Start-Process <url>` (opens default browser).
3. If absent: print "No PR found. Run the workflow to completion first."

### `dev clean` — Clean Merged Worktrees

1. `git branch --merged main` — find merged branches.
2. Cross-reference with worktree list.
3. Print all candidates with count: "Found N worktrees merged into main:" followed by the list.
4. Ask single confirmation: "Remove all? [y/N]" (or `--dry-run` to preview without prompting).
5. For each confirmed: `git worktree remove <path>` + `git branch -d <branch>`.
6. `git remote prune origin`.
7. Print summary: "Removed N worktrees, pruned remote branches."

### Devcontainer Delegation

When the first argument is a recognized devcontainer subcommand (`up`, `shell`, `claude`, `down`, `sync`, `reset`, `push`):

1. Check if `scripts/devcontainer.ps1` exists in the repo root.
2. If yes: delegate all arguments to it.
3. If no: print "No devcontainer script found in this repo."

This preserves full backward compatibility with the existing `dev up`, `dev shell`, etc. workflow.

### Progressive Enhancement

| Convention Present | Behavior Added |
|-------------------|----------------|
| `.worktrees/` directory | Worktree listing and navigation |
| `.workflow/state.json` | Workflow stage display, "needs" calculation, grouping |
| `.workflow/pipeline.lock` + `pipeline.log` | Pipeline status, "in progress" group |
| `scripts/devcontainer.ps1` | Container subcommands available |
| `gh` CLI authenticated | PR status with comment counts |
| None of the above | Basic git branch status only |

A repo with no worktrees and no workflow files still gets a useful output: current branch, dirty status, ahead/behind. `dev` never errors due to missing conventions — it shows what it can.

### Constraints

- **Read-only**: `dev` never writes to `.workflow/state.json` or any repo state. It is purely observational (except `dev clean` which removes worktrees and `dev run` which starts pipelines).
- **No shell: true**: All process spawning uses direct invocation, not shell execution (Constitution S2).
- **Fast**: Dashboard must render in under 2 seconds with warm PR cache. Git operations are parallelized where possible.
- **No global install required**: Works from profile function + dotfiles script. No npm/dotnet/pip install.

---

## Acceptance Criteria

Tests use Pester 5 (`tests/dev-command/dev.tests.ps1`).

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `dev` with no args prints worktree dashboard grouped by attention priority and exits | `Describe "dashboard" / It "groups by attention priority"` | 🔲 |
| AC-02 | Dashboard shows commits ahead, dirty count, workflow stage, and PR status per worktree | `Describe "dashboard" / It "shows all status columns"` | 🔲 |
| AC-03 | Current worktree (detected from cwd) is marked with `→` in dashboard | `Describe "dashboard" / It "highlights current worktree"` | 🔲 |
| AC-04 | `dev <prefix>` changes directory to the matching worktree via Set-Location | `Describe "navigation" / It "cd to worktree by prefix"` | 🔲 |
| AC-05 | `dev <prefix>` with ambiguous match prints all matches and does not navigate | `Describe "navigation" / It "rejects ambiguous prefix"` | 🔲 |
| AC-06 | `dev <prefix> -tab` opens a new Windows Terminal tab when `$env:WT_SESSION` is set | `Describe "navigation" / It "opens WT tab with -tab flag"` | 🔲 |
| AC-07 | `dev status <name>` shows full workflow detail including gates, verify, qa, review, PR, and pipeline state | `Describe "status" / It "shows full workflow detail"` | 🔲 |
| AC-08 | `dev status` with no name defaults to the current worktree | `Describe "status" / It "defaults to current worktree"` | 🔲 |
| AC-09 | Dashboard displays active pipeline stage, elapsed time, and activity signal when `.workflow/pipeline.lock` exists with alive PID | `Describe "pipeline" / It "shows active pipeline"` | 🔲 |
| AC-10 | Dashboard classifies worktree as "needs attention" when pipeline lock exists but PID is dead | `Describe "pipeline" / It "detects dead pipeline"` | 🔲 |
| AC-11 | Dashboard shows PR number and comment count from GitHub API via `gh pr view` | `Describe "pr status" / It "shows PR with comment count"` | 🔲 |
| AC-12 | PR status is cached to `.workflow/pr-cache.json` with 60-second TTL; stale cache triggers synchronous refresh | `Describe "pr status" / It "caches with 60s TTL"` | 🔲 |
| AC-13 | `dev run <name>` starts pipeline.py in background via direct invocation (no shell) and prints monitoring instructions | `Describe "run" / It "starts pipeline in background"` | 🔲 |
| AC-14 | `dev pr <name>` opens PR URL in default browser when PR exists in state.json | `Describe "pr" / It "opens PR in browser"` | 🔲 |
| AC-15 | `dev clean` lists all merged worktrees with count, confirms, then removes | `Describe "clean" / It "removes merged worktrees"` | 🔲 |
| AC-16 | Devcontainer subcommands (`up`, `shell`, `claude`, `down`, `sync`, `reset`, `push`) delegate to `scripts/devcontainer.ps1` | `Describe "devcontainer" / It "delegates to script"` | 🔲 |
| AC-17 | `dev` works from any subdirectory within any worktree (resolves to main repo root) | `Describe "resolution" / It "works from subdirectory"` | 🔲 |
| AC-18 | `dev` in a repo without `.workflow/` or `.worktrees/` shows basic branch status without error | `Describe "fallback" / It "works without conventions"` | 🔲 |
| AC-19 | Tab completion provides worktree names and subcommand names | `Describe "completion" / It "completes names and subcommands"` | 🔲 |
| AC-20 | Dashboard renders in under 2 seconds with warm PR cache | `Describe "performance" / It "renders under 2 seconds"` | 🔲 |
| AC-21 | Empty groups are not displayed in dashboard output | `Describe "dashboard" / It "hides empty groups"` | 🔲 |
| AC-22 | Dashboard header shows total worktree count | `Describe "dashboard" / It "shows worktree count"` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No worktrees | Repo with no `.worktrees/` | Show current branch status, ahead/behind, dirty count |
| No workflow state | Worktree without `.workflow/state.json` | Show in "Other" group with git-only info |
| Stale PR cache | Cache older than 60s | Fetch fresh from GitHub API |
| No `gh` CLI | `gh` not installed or not authenticated | Skip PR status, show "PR #{number}" without details |
| Pipeline lock with dead PID | Lock file exists, PID not running | Show in "Needs attention" with "pipeline stalled" |
| Worktree name collision with subcommand | Worktree named "status" | Subcommand takes priority. Use `dev status status` for detail. |
| Windows Terminal not available | No `$env:WT_SESSION` | `-tab` falls back to `Start-Process pwsh` |
| No worktree match, not a subcommand | `dev xyz` where "xyz" matches nothing | Print "No worktree matching 'xyz'. Available:" followed by worktree names |
| pipeline.py not found | `dev run foo` in a repo without `scripts/pipeline.py` | Print "No pipeline script found in this repo" |
| `dev clean --dry-run` | Merged worktrees exist | List candidates without removing, print "dry run — no changes made" |

---

## Design Decisions

### Why `dev` is exempt from A1/A2 (Application Services)

**Context:** Constitution A1/A2 require all business logic in Application Services so CLI, TUI, Extension, and MCP share a single code path. `dev` is a PowerShell script that lives in personal dotfiles, not a PPDS surface.

**Decision:** `dev` is a personal shell utility, not a PPDS product surface. It does not duplicate any Application Service logic — there is no "worktree dashboard" service because no other surface (TUI, Extension, MCP) needs this capability. `dev` reads files (`.workflow/state.json`, git state) and renders text. If a worktree dashboard ever becomes a product feature (e.g., a TUI screen or Extension panel), that would be built as an Application Service and the shell script would remain a separate, personal tool.

**Why this is not a violation:** A1/A2 prevent logic duplication across surfaces. `dev` is the only consumer of this logic. There is nothing to deduplicate.

### Why dotfiles, not repo scripts?

**Context:** `dev` needs to work in any git repo, not just ppds.

**Decision:** The script lives in the user's dotfiles repo. Repos provide data (`.workflow/state.json`) not tooling.

**Alternatives considered:**
- `scripts/dev.ps1` in ppds repo: Rejected — locks `dev` to one repo, doesn't help on client projects.
- npm/dotnet global tool: Rejected — violates zero-install constraint, heavy for a shell utility.
- Profile function only (no external script): Rejected — 150+ lines in profile makes it unreadable.

**Consequences:**
- Positive: `dev` works everywhere, repo-agnostic, easy to evolve.
- Negative: Requires dotfiles repo to be cloned. Mitigated by fallback to `scripts/dev.ps1` in repo.

### Why attention-priority grouping?

**Context:** A flat alphabetical worktree list requires the user to scan every entry and mentally classify what needs attention.

**Decision:** Group worktrees by what action they need: attention → in progress → PRs → clean up → design → other.

**Alternatives considered:**
- Alphabetical list with status columns: Rejected — puts cognitive load on the user to prioritize.
- Interactive TUI with filters: Rejected — over-engineered for a "glance and decide" tool.

**Consequences:**
- Positive: Most important items are always at the top. Zero cognitive overhead.
- Negative: Grouping logic adds complexity. Mitigated by clear, testable categorization rules.

### Why cd in-place as default?

**Context:** The user's profile uses `Set-Location` consistently (ppdsw, zoxide). New terminal tabs are available via `-tab` flag.

**Decision:** `dev <name>` does `Set-Location` (cd in-place). `-tab` opens a new Windows Terminal tab.

**Alternatives considered:**
- New tab as default: Rejected — inconsistent with established muscle memory, causes tab proliferation.
- Print cd command for user to copy: Rejected — adds friction, defeats the purpose.

**Consequences:**
- Positive: Consistent with ppdsw and zoxide patterns.
- Negative: Leaves previous worktree context. Mitigated by `-tab` for concurrent work.

### Why cache PR status?

**Context:** GitHub API calls take 500ms–2s per PR. With 5 open PRs, uncached dashboard takes 5–10 seconds.

**Decision:** Cache to `.workflow/pr-cache.json` with 60-second TTL. When cache is stale, dashboard fetches fresh data synchronously before rendering. This keeps output always accurate at the cost of ~1 second for stale cache refresh.

**Alternatives considered:**
- No caching (always fresh): Rejected — too slow for a "type and glance" tool.
- Skip PR status entirely: Rejected — PR comment counts are critical for "needs attention" classification.
- Longer TTL (5 min): Rejected — PR state changes quickly during active reviews.

**Consequences:**
- Positive: Dashboard stays under 2-second target.
- Negative: PR status may be up to 60 seconds stale. Acceptable for a dashboard.

---

## Related Specs

- [workflow-enforcement.md](./workflow-enforcement.md) — Defines `.workflow/state.json` schema and pipeline observability
- [cli.md](./cli.md) — PPDS CLI architecture (ppds function wraps this)
- [setup-command.md](./setup-command.md) — DX options where `dev` profile function could be auto-installed

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-26 | Initial spec |
