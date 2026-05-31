# PR-Stack Beta — pipeline.py --stack Mode

**Status:** Draft
**Last Updated:** 2026-05-16
**Code:** [scripts/](../scripts/) | [tests/scripts/](../tests/scripts/)
**Surfaces:** N/A (workflow tooling)
**Verification:** `python -m pytest tests/scripts/test_pipeline_stack.py -q`
**Verification Max Iterations:** 5

---

## Overview

`pipeline.py --stack <envelope-path>` consumes a PR-stack envelope (produced by `/design` Step 4.E per `specs/feat-1070-pr-stack-alpha.md`) and drives N stack entries sequentially through the full pipeline: `implement → gates → verify → pr`. After each entry's PR is created and CI passes, the stack runner waits for the PR to be merged before auto-rebasing the next entry on `origin/main` and proceeding. Failure isolation ensures that a failed entry marks itself and its dependents as skipped without affecting any previously-merged entries.

### Goals

- **Sequential merge-gated execution**: entries run in topological dependency order; each entry's pipeline only starts after all its `depends_on` entries are merged into main
- **Auto-rebase**: each entry's worktree is rebased on `origin/main` (which now includes prior merged PRs) before its pipeline starts, producing a clean diff
- **Failure isolation**: a failed entry marks itself `failed` and its transitive dependents `skipped`; already-merged entries are untouched
- **Observability**: `.workflow/stack-result.json` is updated after each entry with per-entry status, PR URLs, and timing

### Non-Goals

- **Alpha envelope schema** (`pr_stack.py` validator, `build_envelope`, `write_envelope`): already shipped in #1070-α — this beta consumes those artifacts without modifying them
- **Supervisor pattern** (`goal_supervisor.py`, `/orchestrate`): already shipped in #1069 — the supervisor spawns N parallel workers from the same envelope; this beta is a simpler sequential alternative with merge-gating
- **Parallel entry execution**: entries run sequentially in topological order; parallelism belongs to the supervisor
- **Cross-repo stacks** or **cross-worktree state sharing**
- **Automatic stack creation**: that is `/design` Step 4.D / 4.E (alpha)

---

## Architecture

```
operator
  │ python scripts/pipeline.py --stack .plans/<date>-<name>-stack.json
  ▼
run_stack()   [pipeline.py]
  │ pr_stack.validate_envelope(envelope)
  │ topological_sort(entries)
  │
  ├── Entry pr-1  (depends_on: [])
  │    ├── create_worktree(repo_root, branch_suffix, "feat/<branch_suffix>")
  │    ├── rebase_on_main(worktree_path)       ← origin/main
  │    ├── subprocess: pipeline.py --plan <entry.plan> --worktree <wt>
  │    │    implement → gates → verify → pr
  │    ├── wait_for_merge(worktree_path, pr_number, timeout_sec)
  │    │    └── poll: gh pr view <num> --json state --jq .state
  │    └── update stack-result.json: entry-1 → merged
  │
  ├── Entry pr-2  (depends_on: [pr-1])  — gate: pr-1 merged ✓
  │    ├── create_worktree(...)
  │    ├── rebase_on_main(...)          ← origin/main now includes pr-1
  │    ├── subprocess: pipeline.py ...
  │    └── wait_for_merge(...)
  │
  └── Entry pr-3  (depends_on: [pr-2])
       └── ...

  └── write_stack_result(worktree_path, entries, status)
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `pipeline.py run_stack()` | Entry point: reads + validates envelope, runs topological sort, drives entry loop, writes stack result |
| `pipeline.py topological_sort()` | Kahn's algorithm; respects `depends_on` DAG; stable within each topological level |
| `pipeline.py wait_for_merge()` | Polls `gh pr view --json state --jq .state` until `MERGED` or timeout/`CLOSED` |
| `pipeline.py rebase_on_main()` | Runs `git fetch origin main && git rebase origin/main` in a given worktree |
| `pipeline.py write_stack_result()` | Serializes `.workflow/stack-result.json` to the stack's own worktree |
| `pr_stack.validate_envelope()` | Schema validation (from alpha — no changes needed) |
| `tests/scripts/test_pipeline_stack.py` | Pytest suite for all 20 ACs using injectable dependencies (no live subprocesses or git) |

### Dependencies

- Requires: [specs/feat-1070-pr-stack-alpha.md](./feat-1070-pr-stack-alpha.md) — envelope schema and `pr_stack.validate_envelope`; must be present in main before this spec ships
- Integrates with: [specs/feat-1069-supervisor-pattern.md](./feat-1069-supervisor-pattern.md) — consumes the same envelope schema (v1.x); `--stack` mode and supervisor are complementary, not overlapping

---

## Specification

### Core Requirements

1. **Envelope consumption.** `pipeline.py --stack <path>` reads the JSON file at `<path>`, validates it via `pr_stack.validate_envelope`, and aborts with a descriptive stderr message and exit code 1 if validation fails or the file is absent.

2. **Topological sort.** `topological_sort(entries)` implements Kahn's algorithm on the `depends_on` DAG. Within the same topological level, the original array order is preserved (stable). A valid envelope (validated by `validate_envelope`) is guaranteed cycle-free; `topological_sort` need not re-check for cycles.

3. **Per-entry pipeline.** For each entry in topological order:
   a. If any entry in `entry.depends_on` has `status != merged`, mark this entry `skipped` and continue.
   b. Create the entry's worktree at `<repo_root>/.worktrees/<entry.branch_suffix>` on branch `feat/<entry.branch_suffix>` (same convention as supervisor in #1069). If the worktree already exists, use it as-is.
   c. Run `rebase_on_main(worktree_path)`: `git fetch origin main && git rebase origin/main`. On rebase conflict, mark entry `failed` and proceed to next entry.
   d. Copy the entry's plan file (`entry.plan`, resolved relative to the repo root) into the entry worktree's `.plans/` directory if not already present.
   e. Run as a subprocess: `python scripts/pipeline.py --plan <plan-dest> --worktree <worktree_path> [--no-retro] [--dry-run] [--max-stage-seconds N] [--model ID]`. Pass-through flags are forwarded from the `--stack` invocation.
   f. On subprocess exit 0: read `pr.url` from `<worktree_path>/.workflow/state.json`; extract PR number; call `wait_for_merge`.
   g. On subprocess exit != 0 or `wait_for_merge` → False: mark entry `failed`.

4. **Merge-wait.** `wait_for_merge(worktree_path, pr_number, timeout_sec, *, poll_interval, gh_runner)` polls `gh pr view <pr_number> --json state --jq .state` every `poll_interval` seconds (default 30). Returns True on `MERGED`, False on `CLOSED` or timeout. Writes progress to stderr.

5. **Merge-wait timeout.** Default: 3600 seconds (1 hour). Configurable via `--merge-wait-sec <N>` on the `--stack` command. When exceeded, the entry is marked `failed`.

6. **Failure isolation.** When an entry is marked `failed`:
   - All entries that transitively depend on it are marked `skipped` and not run.
   - Entries with no transitive dependency on the failed entry continue to run.
   - Already-merged entries retain `status=merged`; no rollback or side-effect.

7. **Stack result file.** Written to `<stack_worktree>/.workflow/stack-result.json` (the worktree where `pipeline.py --stack` is invoked). Updated after each entry's status changes. See §Stack Result Schema.

8. **Overall status.** Written to `stack-result.json` as `status`:
   - `complete` — all entries have `status=merged`
   - `partial` — ≥1 entry merged AND ≥1 entry failed or skipped
   - `failed` — 0 entries merged AND ≥1 entry failed
   - `pending` — no entries have reached a terminal state yet (initial write, dry-run, or mid-execution before any entry completes)

9. **Stdout discipline (Constitution I1).** `run_stack` writes all progress to stderr. No structured data to stdout. Stack result is written to the file only.

10. **No new Python dependencies.** All new functions in `pipeline.py` use stdlib only (`json`, `subprocess`, `time`, `pathlib`, `os`, `sys`) plus existing project imports already at the top of `pipeline.py`.

11. **Pass-through flags.** The following `pipeline.py` flags are forwarded to every per-entry subprocess: `--no-retro`, `--dry-run`, `--max-stage-seconds`, `--model`. `--issue` is also forwarded so all entries are linked to the parent issue(s).

12. **Dry-run mode.** With `--dry-run`, `run_stack` logs planned per-entry pipeline invocations to stderr (entry ID, branch, plan path) without creating worktrees, running subprocesses, or polling for merge. Stack result is written with all entries in `pending` status.

### Stack Result Schema

```json
{
  "schema_version": "1.0",
  "stack_path": "<abs-path to envelope>",
  "started_at": "<ISO-8601>",
  "completed_at": "<ISO-8601 or null>",
  "status": "complete|partial|failed|pending",
  "entries": [
    {
      "id": "pr-1",
      "worktree_path": "<abs-path>",
      "branch": "feat/pr1",
      "plan": ".plans/2026-05-16-foo-pr1.md",
      "pr_url": "https://github.com/owner/repo/pull/123",
      "pr_number": 123,
      "status": "pending|running|pr_ready|merged|failed|skipped",
      "started_at": "<ISO-8601 or null>",
      "completed_at": "<ISO-8601 or null>",
      "merged_at": "<ISO-8601 or null>"
    }
  ]
}
```

### Primary Flows

**Successful 3-entry linear stack (pr-1 → pr-2 → pr-3):**

1. `pipeline.py --stack stack.json` reads and validates envelope; topological sort yields `[pr-1, pr-2, pr-3]`.
2. pr-1: create worktree, rebase on main, run pipeline subprocess → PR created → wait for merge → merged. Stack result updated: pr-1 `merged`.
3. pr-2: gate check ✓ (pr-1 merged), create worktree, rebase on origin/main (includes pr-1), run pipeline → PR created → wait for merge → merged. Stack result updated: pr-2 `merged`.
4. pr-3: gate check ✓ (pr-2 merged), same flow → merged. Stack result: `status=complete`.

**Entry-2 failure, entry-1 already merged:**

1. Stack runs pr-1 → merged.
2. Stack starts pr-2 → pipeline subprocess exits non-zero → entry-2 marked `failed`.
3. Stack checks pr-3: depends on pr-2 which is `failed` → pr-3 marked `skipped`, not run.
4. Stack result: pr-1 `merged`, pr-2 `failed`, pr-3 `skipped`, overall `status=partial`.
5. pr-1's merged PR is in main — no rollback, no side effect.

**Merge-wait timeout:**

1. Pipeline subprocess exits 0 (PR created).
2. `wait_for_merge` polls for 3600s without seeing MERGED.
3. Entry marked `failed`; its dependents marked `skipped`.
4. Stack exits with code 1.

**Dry run:**

1. `pipeline.py --stack stack.json --dry-run` reads envelope, topological sort.
2. For each entry: logs `[dry-run] would run entry pr-1: plan=... branch=feat/pr1` to stderr.
3. Stack result written with all entries `pending`.
4. Exits 0.

### Surface-Specific Behavior

This feature is workflow tooling only — N/A for CLI, TUI, Extension, and MCP product surfaces.

### Constraints

- `run_stack` is invoked as `pipeline.py --stack <path>` which is mutually exclusive with `--plan`, `--spec`. If `--stack` is combined with those, print an error and exit 1.
- `branch_suffix` in each entry must not contain slashes (enforced by `validate_envelope`). The runner uses it directly as `feat/<branch_suffix>`.
- The stack runner acquires no lock of its own; each per-entry subprocess acquires the per-worktree lock. Parallel invocations of `pipeline.py --stack` on the same envelope are not prevented — the operator must not run two stack invocations on the same envelope concurrently.
- `topological_sort` assumes a valid DAG; it does not re-validate cycles (that is `validate_envelope`'s job).
- `rebase_on_main` fetches `origin main` before rebasing. If the fetch fails (no network), it logs a warning and continues with the last-known `origin/main` (same behavior as the existing PR stage rebase in `run_pr_stage`).

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `topological_sort(entries)` returns entries in valid topological order for a 3-entry chain pr-1 ← pr-2 ← pr-3 | `TestTopologicalSort.test_linear_chain` | ❌ |
| AC-02 | `topological_sort(entries)` returns all entries when no `depends_on` relationships exist (parallel stack) | `TestTopologicalSort.test_parallel` | ❌ |
| AC-03 | `topological_sort(entries)` preserves stable array order within the same topological level | `TestTopologicalSort.test_stable_order` | ❌ |
| AC-04 | `wait_for_merge` returns True when the injected `gh_runner` returns `"MERGED"` within timeout | `TestWaitForMerge.test_success` | ❌ |
| AC-05 | `wait_for_merge` returns False when timeout expires before `"MERGED"` | `TestWaitForMerge.test_timeout` | ❌ |
| AC-06 | `wait_for_merge` returns False immediately when `gh_runner` returns `"CLOSED"` without waiting for timeout | `TestWaitForMerge.test_closed_returns_false_immediately` | ❌ |
| AC-07 | `run_stack` with a 3-entry linear stack invokes the pipeline subprocess exactly 3 times in topological order | `TestRunStack.test_three_entries_sequential` | ❌ |
| AC-08 | `run_stack` calls `rebase_on_main` in the entry's worktree before invoking the pipeline subprocess for that entry | `TestRunStack.test_rebase_before_pipeline` | ❌ |
| AC-09 | When entry-2's pipeline subprocess exits non-zero, `run_stack` marks entry-2 `failed` and entry-3 (depends on entry-2) `skipped` without running entry-3 | `TestRunStack.test_entry2_failure_isolation` | ❌ |
| AC-10 | Entry-1 (status=merged before entry-2 fails) retains `status=merged` in the stack result — no rollback or side effect | `TestRunStack.test_merged_entry_unaffected_by_failure` | ❌ |
| AC-11 | `pipeline.py --stack <path>` exits 1 with a stderr message when the envelope file does not exist | `TestStackCli.test_missing_file_exits_1` | ❌ |
| AC-12 | `pipeline.py --stack <path>` exits 1 with a stderr message when the envelope fails `validate_envelope` | `TestStackCli.test_invalid_envelope_exits_1` | ❌ |
| AC-13 | `pipeline.py --stack <path> --dry-run` logs planned invocations to stderr and writes stack-result.json with all entries `pending` without executing any subprocess | `TestStackCli.test_dry_run` | ❌ |
| AC-14 | `run_stack` writes `.workflow/stack-result.json` with `status=complete` and all entries `status=merged` after a successful 3-entry stack | `TestRunStack.test_stack_result_complete` | ❌ |
| AC-15 | `run_stack` writes `.workflow/stack-result.json` with `status=partial` when entry-1 merges and entry-2 fails | `TestRunStack.test_stack_result_partial` | ❌ |
| AC-16 | `run_stack` writes progress to stderr and produces no stdout output during execution (Constitution I1) | `TestRunStack.test_stdout_discipline` | ❌ |
| AC-17 | `run_stack` skips all entries whose `depends_on` list contains a `failed` entry (transitive: also skips entries that depend on the skipped entries) | `TestRunStack.test_skips_transitive_dependents` | ❌ |
| AC-18 | `run_stack` with `--merge-wait-sec 0` times out immediately on a non-merged PR and marks the entry `failed` | `TestRunStack.test_merge_wait_timeout_marks_failed` | ❌ |
| AC-19 | Smoke: 3-entry stack with all entries mocked to succeed → `stack-result.json` has `status=complete`, 3 entries each `status=merged` | `TestSmoke.test_three_entry_stack_success` | ❌ |
| AC-20 | Smoke: 3-entry stack where entry-2 pipeline fails → `stack-result.json` has entry-1 `merged`, entry-2 `failed`, entry-3 `skipped`, overall `status=partial` | `TestSmoke.test_three_entry_stack_partial_failure` | ❌ |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Single-entry stack (justification present) | Runs the single entry; result is `complete` or `failed` |
| Parallel stack (all `depends_on: []`) | All entries run in array order sequentially (beta: no parallelism) |
| `rebase_on_main` conflict | Entry marked `failed`; dependents marked `skipped`; stack continues with independent entries |
| PR already merged before `wait_for_merge` polls | First poll returns MERGED → True; no delay |
| `gh pr view` exits non-zero (network failure) | Retry on next poll interval; does not immediately fail |
| Worktree already exists for an entry | Reused as-is; `rebase_on_main` still runs |
| `--stack` combined with `--plan` | Error + exit 1: flags are mutually exclusive |
| Envelope with 1 entry + justification | Runs the single entry; result schema identical |

### Test Examples

```python
# AC-01: linear chain topological order
def test_linear_chain():
    entries = [
        {"id": "pr-1", "depends_on": []},
        {"id": "pr-2", "depends_on": ["pr-1"]},
        {"id": "pr-3", "depends_on": ["pr-2"]},
    ]
    result = topological_sort(entries)
    assert [e["id"] for e in result] == ["pr-1", "pr-2", "pr-3"]

# AC-04 / AC-05: wait_for_merge with injected gh_runner
def test_wait_for_merge_success(tmp_path):
    def gh_runner(pr_number):
        return "MERGED"
    assert wait_for_merge(str(tmp_path), "123", timeout_sec=60,
                          gh_runner=gh_runner, poll_interval=0.001)

def test_wait_for_merge_timeout(tmp_path):
    def gh_runner(pr_number):
        return "OPEN"
    assert not wait_for_merge(str(tmp_path), "123", timeout_sec=0.05,
                              gh_runner=gh_runner, poll_interval=0.001)

# AC-09 / AC-10: entry-2 failure leaves entry-1 merged
def test_entry2_failure_isolation(tmp_path):
    spawn_calls = []
    def pipeline_runner(entry, worktree_path):
        spawn_calls.append(entry["id"])
        return 0 if entry["id"] == "pr-1" else 1  # pr-2 fails

    def gh_runner(pr_number):
        return "MERGED"  # pr-1 merges successfully

    envelope = _valid_envelope(n=3)  # pr-1 → pr-2 → pr-3
    result = run_stack(str(tmp_path / "stack.json"), envelope, tmp_path,
                       pipeline_runner=pipeline_runner, gh_runner=gh_runner)
    
    assert result["entries"][0]["status"] == "merged"   # pr-1: merged
    assert result["entries"][1]["status"] == "failed"   # pr-2: failed
    assert result["entries"][2]["status"] == "skipped"  # pr-3: skipped
    assert result["status"] == "partial"
    assert "pr-3" not in spawn_calls  # pr-3 never ran
```

---

## Core Types

### Public API (new functions in pipeline.py)

```python
def topological_sort(entries: list[dict]) -> list[dict]:
    """Return entries in Kahn topological order; stable within each level.
    Assumes valid DAG (validate_envelope guarantees no cycles)."""

def wait_for_merge(
    worktree_path: str,
    pr_number: str | int,
    timeout_sec: float = 3600,
    *,
    poll_interval: float = 30,
    gh_runner: Callable[[str], str] | None = None,
) -> bool:
    """Poll gh pr view until MERGED (True) or CLOSED/timeout (False).
    gh_runner injectable for tests; defaults to subprocess gh call."""

def rebase_on_main(worktree_path: str, logger: IO) -> bool:
    """git fetch origin main && git rebase origin/main.
    Returns True on success, False on conflict (logs to logger)."""

def run_stack(
    stack_path: str,
    *,
    repo_root: str,
    worktree_path: str,
    dry_run: bool = False,
    no_retro: bool = False,
    merge_wait_sec: int = 3600,
    max_stage_seconds: int | None = None,
    model: str | None = None,
    issues: list[int] | None = None,
    pipeline_runner: Callable | None = None,
    gh_runner: Callable | None = None,
) -> int:
    """Execute a PR-stack envelope sequentially with merge-gating.
    Returns 0 on complete success (all merged), 1 on any failure."""
```

Injectable `pipeline_runner` and `gh_runner` parameters allow unit tests to avoid spawning real subprocesses or calling the GitHub API.

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `FileNotFoundError` | Envelope path does not exist | Exit 1 with stderr message |
| `json.JSONDecodeError` | Envelope is not valid JSON | Exit 1 with stderr message |
| `ValueError` (from `validate_envelope`) | Envelope fails schema validation | Exit 1 with stderr message |
| Subprocess exit != 0 | Per-entry pipeline failed | Mark entry `failed`, skip dependents, continue |
| `wait_for_merge` timeout | PR not merged within `merge_wait_sec` | Mark entry `failed`, skip dependents, continue |
| `rebase_on_main` conflict | `git rebase` exits non-zero | Mark entry `failed`, skip dependents, continue |
| `gh pr view` failure | Network or auth issue during merge-wait | Retry on next poll; do not fail immediately |

### Recovery Strategies

- **Partial stack resume**: not implemented in beta. The operator re-runs `pipeline.py --stack <path>` after fixing the failed entry. The stack runner re-reads the envelope from scratch. Already-merged PRs are idempotent (the pipeline's PR stage skips creation if a PR already exists); the stack result file is overwritten.
- **Lock contention on entry worktree**: each sub-pipeline acquires its own per-worktree lock. If a previous run left a stale lock, the sub-pipeline will handle it (same as standalone pipeline).
- **`gh pr view` failure during merge-wait**: retry on next poll interval; do not fail immediately. Persistent failure across the full `merge_wait_sec` window results in timeout failure.

---

## Design Decisions

### Why sequential execution rather than parallel?

**Context:** The supervisor pattern (#1069) already handles parallel worker spawning from the same envelope. Adding parallelism to `--stack` would duplicate the supervisor's core value.

**Decision:** Sequential only in beta. `--stack` is positioned as a simpler single-operator alternative for strictly ordered stacks where the operator wants to watch and intervene. Parallelism is available via `/orchestrate`.

**Alternatives considered:**
- Parallel `--stack` with multiprocessing: would duplicate supervisor logic and create complex lock interactions
- Sequential first, parallel as flag: adds scope; defer to a future spec if needed

**Consequences:**
- Positive: simple, easy to reason about, no subprocess pool management
- Negative: slower than the supervisor for long independent stacks

### Why merge-wait rather than PR-ready-wait?

**Context:** The per-entry pipeline subprocess creates a PR and runs `pr_monitor.py`, which waits for CI to pass and marks the PR ready. But "ready" means "out of draft, CI green" — not "merged". The next entry must be built on the merged commit, not on the unmerged branch head.

**Decision:** After the subprocess exits 0 (PR ready), the stack runner additionally waits for `MERGED` state. This is a separate wait loop in `wait_for_merge`, not a `pr_monitor` concern.

**Alternatives considered:**
- Auto-merge enabled PRs: could skip the merge-wait, but auto-merge is not always enabled; the runner must handle both cases
- Merging the PR automatically: out of scope; a human reviewer must approve each PR

**Consequences:**
- Positive: correct — next entry gets the actual merged commit via `git fetch + rebase`
- Negative: adds human latency between entries; `merge_wait_sec` default (1 hour) may need tuning for teams with slower review cycles

### Why injectable `pipeline_runner` and `gh_runner`?

**Context:** `run_stack` calls subprocesses (pipeline.py) and `gh pr view`. Unit tests cannot spawn real subprocesses or call the GitHub API reliably.

**Decision:** Both are keyword-only parameters with `None` defaults. When `None`, the real subprocess/gh call is used. Tests inject deterministic callables. This is the same injectable-dependency pattern used in `goal_supervisor.py` (`subprocess_runner`, `gh_runner`, `haiku_runner`).

**Alternatives considered:**
- Monkeypatching `subprocess.run` in tests: fragile, hard to scope
- Integration tests only: too slow; no isolation of topological sort logic

**Consequences:**
- Positive: fast, deterministic unit tests for all 20 ACs
- Negative: injection pattern is slightly less idiomatic for Python scripts than for OO code; documented as the PPDS convention

### Why `branch feat/<branch_suffix>` not `feat/<parent>-<branch_suffix>`?

**Context:** The supervisor (#1069) already creates branches as `feat/<branch_suffix>`. If the stack runner used a different naming scheme, the same envelope would produce different branches depending on which tool consumed it.

**Decision:** Use `feat/<branch_suffix>` for consistency with the supervisor.

**Consequences:**
- Positive: consistent branch names regardless of which tool (--stack or supervisor) is used
- Negative: if the parent branch is `feat/1070-beta` and `branch_suffix` is `pr1`, the entry branch is `feat/pr1` — not obviously connected to the parent. Acceptable for now; operators can set descriptive `branch_suffix` values in the envelope (e.g., `1070-pr1`).

### Why `.workflow/stack-result.json` in the invoking worktree?

**Context:** Each entry runs in its own worktree with its own `.workflow/` directory. The aggregate result needs to live somewhere accessible to the operator.

**Decision:** Stack result lives in the worktree where `pipeline.py --stack` is invoked (the stack's "supervisor worktree"). This mirrors the supervisor's pattern of writing `goal-envelope.json` to its own worktree.

**Consequences:**
- Positive: single well-known location for the aggregate result
- Negative: must not be confused with per-entry `.workflow/pipeline-result.json` files

---

## Related Specs

- [feat-1070-pr-stack-alpha.md](./feat-1070-pr-stack-alpha.md) — canonical PR-stack envelope schema; `validate_envelope`; alpha ships before this beta
- [feat-1069-supervisor-pattern.md](./feat-1069-supervisor-pattern.md) — parallel alternative using the same envelope; ships before this beta

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-16 | Initial spec — beta scope only |

---

## Roadmap

- **Phase 4**: stack resume (`--stack --resume`) that reads an existing `stack-result.json` and skips already-merged entries
- **Phase 5**: parallel entry execution within a single `--stack` invocation (independent entries in the same topological level run concurrently)
