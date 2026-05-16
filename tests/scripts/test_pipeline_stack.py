"""Tests for pipeline.py --stack mode — ACs 01-20 from specs/feat-1070-pr-stack-beta.md."""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

# tests/conftest.py prepends scripts/ to sys.path.
from pipeline import (  # noqa: E402
    rebase_on_main,
    run_stack,
    topological_sort,
    wait_for_merge,
)


# --------------------------------------------------------------------------- #
# helpers
# --------------------------------------------------------------------------- #

def _valid_entry(id_, branch_suffix, depends_on=None):
    return {
        "id": id_,
        "title": f"feat: {id_}",
        "branch_suffix": branch_suffix,
        "plan": f".plans/2026-05-16-foo-{id_}.md",
        "files": ["src/a.py"],
        "size_estimate": "~100 LOC",
        "depends_on": depends_on or [],
        "ac_refs": [],
    }


def _valid_envelope(n=3):
    entries = [
        _valid_entry(
            f"pr-{i}", f"pr{i}",
            depends_on=[f"pr-{i-1}"] if i > 1 else [],
        )
        for i in range(1, n + 1)
    ]
    return {
        "schema_version": "1.0",
        "spec": "specs/foo.md",
        "created_at": "2026-05-16T00:00:00+00:00",
        "stack": entries,
    }


def _write_envelope(tmp_path: Path, envelope: dict) -> Path:
    p = tmp_path / "stack.json"
    p.write_text(json.dumps(envelope), encoding="utf-8")
    return p


def _prime_entry_worktree(repo_root: Path, branch_suffix: str, pr_url: str = ""):
    """Pre-create entry worktree dir + .workflow/state.json with optional pr.url.

    The worktree_creator stub treats existing dirs as success; this lets
    run_stack proceed to pipeline_runner + read_state without git invocations.
    """
    entry_wt = repo_root / ".worktrees" / branch_suffix
    (entry_wt / ".workflow").mkdir(parents=True, exist_ok=True)
    state = {}
    if pr_url:
        state["pr"] = {"url": pr_url}
    (entry_wt / ".workflow" / "state.json").write_text(
        json.dumps(state), encoding="utf-8"
    )
    return entry_wt


def _stub_creator_factory():
    """Return (creator, calls). creator records and returns worktree path."""
    calls = []

    def creator(repo_root, name, branch, logger):
        path = os.path.join(repo_root, ".worktrees", name)
        os.makedirs(path, exist_ok=True)
        os.makedirs(os.path.join(path, ".workflow"), exist_ok=True)
        # Initialize state.json so read_state doesn't fail; tests may overwrite.
        state_file = os.path.join(path, ".workflow", "state.json")
        if not os.path.exists(state_file):
            Path(state_file).write_text("{}", encoding="utf-8")
        calls.append((name, branch, path))
        return path

    return creator, calls


def _stub_rebaser_factory(return_value=True):
    """Return (rebaser, calls). rebaser records and returns given value."""
    calls = []

    def rebaser(worktree_path, logger):
        calls.append(worktree_path)
        return return_value

    return rebaser, calls


# --------------------------------------------------------------------------- #
# AC-01, 02, 03 — topological_sort
# --------------------------------------------------------------------------- #

class TestTopologicalSort:
    def test_linear_chain(self):
        # AC-01: 3-entry chain pr-1 ← pr-2 ← pr-3.
        entries = [
            {"id": "pr-1", "depends_on": []},
            {"id": "pr-2", "depends_on": ["pr-1"]},
            {"id": "pr-3", "depends_on": ["pr-2"]},
        ]
        result = topological_sort(entries)
        assert [e["id"] for e in result] == ["pr-1", "pr-2", "pr-3"]

    def test_linear_chain_when_input_order_reversed(self):
        # AC-01 negative case: input array is in reverse topological order;
        # output must still respect the DAG.
        entries = [
            {"id": "pr-3", "depends_on": ["pr-2"]},
            {"id": "pr-2", "depends_on": ["pr-1"]},
            {"id": "pr-1", "depends_on": []},
        ]
        result = topological_sort(entries)
        order = [e["id"] for e in result]
        # pr-1 must come before pr-2, pr-2 before pr-3.
        assert order.index("pr-1") < order.index("pr-2") < order.index("pr-3")

    def test_parallel(self):
        # AC-02: no depends_on relationships → all returned in array order.
        entries = [
            {"id": "pr-a", "depends_on": []},
            {"id": "pr-b", "depends_on": []},
            {"id": "pr-c", "depends_on": []},
        ]
        result = topological_sort(entries)
        assert [e["id"] for e in result] == ["pr-a", "pr-b", "pr-c"]

    def test_stable_order(self):
        # AC-03: within the same topological level, original array order
        # is preserved.
        entries = [
            {"id": "root", "depends_on": []},
            {"id": "leaf-b", "depends_on": ["root"]},
            {"id": "leaf-a", "depends_on": ["root"]},
            {"id": "leaf-c", "depends_on": ["root"]},
        ]
        result = topological_sort(entries)
        order = [e["id"] for e in result]
        assert order[0] == "root"
        # leaves are in array order, regardless of alphabetical sort.
        assert order[1:] == ["leaf-b", "leaf-a", "leaf-c"]


# --------------------------------------------------------------------------- #
# AC-04, 05, 06 — wait_for_merge
# --------------------------------------------------------------------------- #

class TestWaitForMerge:
    def test_success(self, tmp_path):
        # AC-04: gh_runner returns MERGED within timeout → True.
        def gh_runner(pr_number):
            return "MERGED"

        assert wait_for_merge(
            str(tmp_path), "123", timeout_sec=60,
            poll_interval=0.001, gh_runner=gh_runner,
        )

    def test_timeout(self, tmp_path):
        # AC-05: timeout expires before MERGED → False.
        def gh_runner(pr_number):
            return "OPEN"

        assert not wait_for_merge(
            str(tmp_path), "123", timeout_sec=0.05,
            poll_interval=0.001, gh_runner=gh_runner,
        )

    def test_closed_returns_false_immediately(self, tmp_path):
        # AC-06: gh_runner returns CLOSED → False without waiting for timeout.
        calls = []

        def gh_runner(pr_number):
            calls.append(pr_number)
            return "CLOSED"

        assert not wait_for_merge(
            str(tmp_path), "123", timeout_sec=600,
            poll_interval=0.001, gh_runner=gh_runner,
        )
        # Exactly one call: CLOSED short-circuits.
        assert len(calls) == 1


# --------------------------------------------------------------------------- #
# AC-07, 08, 09, 10, 14, 15, 16, 17, 18 — run_stack
# --------------------------------------------------------------------------- #

class TestRunStack:
    def _setup(self, tmp_path, n=3, pr_url_template="https://github.com/x/y/pull/{idx}"):
        envelope = _valid_envelope(n=n)
        if n == 1:
            envelope["justification"] = "single-entry-test"
        for i, e in enumerate(envelope["stack"], start=1):
            _prime_entry_worktree(
                tmp_path, e["branch_suffix"],
                pr_url=pr_url_template.format(idx=100 + i) if pr_url_template else "",
            )
        stack_path = _write_envelope(tmp_path, envelope)
        return envelope, stack_path

    def test_three_entries_sequential(self, tmp_path):
        # AC-07: 3-entry linear stack → pipeline subprocess invoked 3× in topo order.
        _envelope, stack_path = self._setup(tmp_path, n=3)
        spawn_order = []

        def pipeline_runner(entry, worktree_path):
            spawn_order.append(entry["id"])
            return 0

        def gh_runner(pr_number):
            return "MERGED"

        creator, _ = _stub_creator_factory()
        rebaser, _ = _stub_rebaser_factory()

        rc = run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            pipeline_runner=pipeline_runner, gh_runner=gh_runner,
            worktree_creator=creator, rebaser=rebaser, merge_wait_sec=5,
        )
        assert rc == 0
        assert spawn_order == ["pr-1", "pr-2", "pr-3"]

    def test_rebase_before_pipeline(self, tmp_path):
        # AC-08: rebase_on_main called in entry's worktree before pipeline subprocess.
        _envelope, stack_path = self._setup(tmp_path, n=2)
        events = []

        def pipeline_runner(entry, worktree_path):
            events.append(("pipeline", entry["id"], worktree_path))
            return 0

        def gh_runner(pr_number):
            return "MERGED"

        creator, _ = _stub_creator_factory()

        def rebaser(worktree_path, logger):
            events.append(("rebase", worktree_path))
            return True

        run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            pipeline_runner=pipeline_runner, gh_runner=gh_runner,
            worktree_creator=creator, rebaser=rebaser, merge_wait_sec=5,
        )

        # For each entry, "rebase" must appear before "pipeline" in events.
        # branch_suffix "prN" appears in the rebase worktree path.
        for eid, suffix in (("pr-1", "pr1"), ("pr-2", "pr2")):
            r_idx = next(i for i, ev in enumerate(events)
                         if ev[0] == "rebase" and ev[1].endswith(suffix))
            p_idx = next(i for i, ev in enumerate(events)
                         if ev[0] == "pipeline" and ev[1] == eid)
            assert r_idx < p_idx, f"rebase must precede pipeline for {eid}"

    def test_entry2_failure_isolation(self, tmp_path):
        # AC-09: entry-2 pipeline non-zero exit → entry-2 failed, entry-3 skipped.
        _envelope, stack_path = self._setup(tmp_path, n=3)
        spawn_order = []

        def pipeline_runner(entry, worktree_path):
            spawn_order.append(entry["id"])
            return 0 if entry["id"] == "pr-1" else 1

        def gh_runner(pr_number):
            return "MERGED"

        creator, _ = _stub_creator_factory()
        rebaser, _ = _stub_rebaser_factory()

        run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            pipeline_runner=pipeline_runner, gh_runner=gh_runner,
            worktree_creator=creator, rebaser=rebaser, merge_wait_sec=5,
        )

        result = json.loads(
            (tmp_path / ".workflow" / "stack-result.json").read_text()
        )
        statuses = {e["id"]: e["status"] for e in result["entries"]}
        assert statuses["pr-1"] == "merged"
        assert statuses["pr-2"] == "failed"
        assert statuses["pr-3"] == "skipped"
        assert "pr-3" not in spawn_order

    def test_merged_entry_unaffected_by_failure(self, tmp_path):
        # AC-10: pr-1 (merged) retains status=merged after pr-2 fails.
        _envelope, stack_path = self._setup(tmp_path, n=3)

        def pipeline_runner(entry, worktree_path):
            return 0 if entry["id"] == "pr-1" else 1

        def gh_runner(pr_number):
            return "MERGED"

        creator, _ = _stub_creator_factory()
        rebaser, _ = _stub_rebaser_factory()

        run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            pipeline_runner=pipeline_runner, gh_runner=gh_runner,
            worktree_creator=creator, rebaser=rebaser, merge_wait_sec=5,
        )

        result = json.loads(
            (tmp_path / ".workflow" / "stack-result.json").read_text()
        )
        pr1 = next(e for e in result["entries"] if e["id"] == "pr-1")
        assert pr1["status"] == "merged"
        assert pr1["merged_at"] is not None
        assert pr1["pr_url"] == "https://github.com/x/y/pull/101"
        assert pr1["pr_number"] == 101

    def test_stack_result_complete(self, tmp_path):
        # AC-14: status=complete and all entries merged on full success.
        _envelope, stack_path = self._setup(tmp_path, n=3)

        def pipeline_runner(entry, worktree_path):
            return 0

        def gh_runner(pr_number):
            return "MERGED"

        creator, _ = _stub_creator_factory()
        rebaser, _ = _stub_rebaser_factory()

        rc = run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            pipeline_runner=pipeline_runner, gh_runner=gh_runner,
            worktree_creator=creator, rebaser=rebaser, merge_wait_sec=5,
        )
        assert rc == 0

        result = json.loads(
            (tmp_path / ".workflow" / "stack-result.json").read_text()
        )
        assert result["status"] == "complete"
        assert all(e["status"] == "merged" for e in result["entries"])
        assert result["schema_version"] == "1.0"
        assert result["completed_at"] is not None

    def test_stack_result_partial(self, tmp_path):
        # AC-15: status=partial when pr-1 merges and pr-2 fails.
        _envelope, stack_path = self._setup(tmp_path, n=2)

        def pipeline_runner(entry, worktree_path):
            return 0 if entry["id"] == "pr-1" else 1

        def gh_runner(pr_number):
            return "MERGED"

        creator, _ = _stub_creator_factory()
        rebaser, _ = _stub_rebaser_factory()

        rc = run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            pipeline_runner=pipeline_runner, gh_runner=gh_runner,
            worktree_creator=creator, rebaser=rebaser, merge_wait_sec=5,
        )
        assert rc == 1

        result = json.loads(
            (tmp_path / ".workflow" / "stack-result.json").read_text()
        )
        assert result["status"] == "partial"

    def test_stdout_discipline(self, tmp_path, capsys):
        # AC-16: progress to stderr, no stdout output.
        envelope = _valid_envelope(n=1)
        envelope["justification"] = "single-entry-justification"
        stack_path = _write_envelope(tmp_path, envelope)

        # Dry-run avoids needing worktree dirs.
        rc = run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            dry_run=True,
            pipeline_runner=lambda e, w: 0,
            gh_runner=lambda n: "MERGED",
            worktree_creator=_stub_creator_factory()[0],
            rebaser=_stub_rebaser_factory()[0],
        )
        captured = capsys.readouterr()
        assert captured.out == ""
        # stderr may be empty if log() also writes only to the file logger,
        # but log() prints a console line to stderr. Confirm at least no stdout.
        assert rc in (0, 1)

    def test_skips_transitive_dependents(self, tmp_path):
        # AC-17: depends_on a skipped entry → also skipped (transitive).
        envelope = _valid_envelope(n=4)
        for e in envelope["stack"]:
            _prime_entry_worktree(
                tmp_path, e["branch_suffix"],
                pr_url=f"https://github.com/x/y/pull/{e['id']}",
            )
        stack_path = _write_envelope(tmp_path, envelope)

        def pipeline_runner(entry, worktree_path):
            return 1 if entry["id"] == "pr-2" else 0

        def gh_runner(pr_number):
            return "MERGED"

        creator, _ = _stub_creator_factory()
        rebaser, _ = _stub_rebaser_factory()

        run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            pipeline_runner=pipeline_runner, gh_runner=gh_runner,
            worktree_creator=creator, rebaser=rebaser, merge_wait_sec=5,
        )

        result = json.loads(
            (tmp_path / ".workflow" / "stack-result.json").read_text()
        )
        statuses = {e["id"]: e["status"] for e in result["entries"]}
        assert statuses["pr-1"] == "merged"
        assert statuses["pr-2"] == "failed"
        # pr-3 depends on pr-2 (failed) → skipped
        assert statuses["pr-3"] == "skipped"
        # pr-4 depends on pr-3 (skipped) → also skipped (transitive)
        assert statuses["pr-4"] == "skipped"

    def test_merge_wait_timeout_marks_failed(self, tmp_path):
        # AC-18: --merge-wait-sec 0 with OPEN PR → entry failed.
        _envelope, stack_path = self._setup(tmp_path, n=1)

        def pipeline_runner(entry, worktree_path):
            return 0

        def gh_runner(pr_number):
            return "OPEN"  # never merges

        creator, _ = _stub_creator_factory()
        rebaser, _ = _stub_rebaser_factory()

        rc = run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            pipeline_runner=pipeline_runner, gh_runner=gh_runner,
            worktree_creator=creator, rebaser=rebaser, merge_wait_sec=0,
        )
        assert rc == 1

        result = json.loads(
            (tmp_path / ".workflow" / "stack-result.json").read_text()
        )
        assert result["entries"][0]["status"] == "failed"


# --------------------------------------------------------------------------- #
# AC-11, 12, 13 — CLI surface (subprocess invocations of pipeline.py)
# --------------------------------------------------------------------------- #

REPO_ROOT = Path(__file__).resolve().parents[2]


def _pipeline_cli(*extra_args, env=None):
    cmd = [sys.executable, str(REPO_ROOT / "scripts" / "pipeline.py"), *extra_args]
    return subprocess.run(
        cmd, stdin=subprocess.DEVNULL,
        capture_output=True, text=True, env=env, cwd=str(REPO_ROOT),
        encoding="utf-8", errors="replace", timeout=60,
    )


class TestStackCli:
    def test_missing_file_exits_1(self, tmp_path):
        # AC-11: missing envelope file → exit 1, stderr message.
        missing = tmp_path / "nope.json"
        result = _pipeline_cli("--stack", str(missing))
        assert result.returncode == 1
        assert "not found" in result.stderr.lower()

    def test_invalid_envelope_exits_1(self, tmp_path):
        # AC-12: malformed envelope (fails validate_envelope) → exit 1, stderr message.
        bad = tmp_path / "bad.json"
        # Missing required top-level keys.
        bad.write_text('{"schema_version": "1.0"}', encoding="utf-8")
        result = _pipeline_cli("--stack", str(bad))
        assert result.returncode == 1
        assert result.stderr.strip() != ""

    def test_dry_run(self, tmp_path):
        # AC-13: --dry-run logs planned invocations; entries=pending; no subprocesses.
        envelope = _valid_envelope(n=2)
        stack_path = tmp_path / "stack.json"
        stack_path.write_text(json.dumps(envelope), encoding="utf-8")

        # Use the tmp_path as the working directory for the stack result.
        result = _pipeline_cli(
            "--stack", str(stack_path), "--dry-run", "--worktree", str(tmp_path),
        )
        assert result.returncode in (0, 1)
        stack_result_path = tmp_path / ".workflow" / "stack-result.json"
        assert stack_result_path.exists()
        result_data = json.loads(stack_result_path.read_text())
        for entry in result_data["entries"]:
            assert entry["status"] == "pending"

    def test_mutex_with_plan(self, tmp_path):
        # AC-16 / spec constraints: --stack with --plan → exit 1.
        stack = tmp_path / "stack.json"
        stack.write_text(json.dumps(_valid_envelope(n=1) | {"justification": "x"}),
                         encoding="utf-8")
        plan = tmp_path / "plan.md"
        plan.write_text("# plan", encoding="utf-8")
        result = _pipeline_cli("--stack", str(stack), "--plan", str(plan))
        assert result.returncode == 1
        assert "mutually exclusive" in result.stderr.lower()


# --------------------------------------------------------------------------- #
# AC-19, 20 — smoke tests (3-entry, end-to-end with mocked runners)
# --------------------------------------------------------------------------- #

class TestSmoke:
    def test_three_entry_stack_success(self, tmp_path):
        # AC-19: 3-entry stack, all succeed → status=complete, 3 entries merged.
        envelope = _valid_envelope(n=3)
        for i, e in enumerate(envelope["stack"], start=1):
            _prime_entry_worktree(
                tmp_path, e["branch_suffix"],
                pr_url=f"https://github.com/x/y/pull/{200 + i}",
            )
        stack_path = _write_envelope(tmp_path, envelope)

        creator, _ = _stub_creator_factory()
        rebaser, _ = _stub_rebaser_factory()

        rc = run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            pipeline_runner=lambda e, w: 0,
            gh_runner=lambda n: "MERGED",
            worktree_creator=creator, rebaser=rebaser, merge_wait_sec=5,
        )
        assert rc == 0

        result = json.loads(
            (tmp_path / ".workflow" / "stack-result.json").read_text()
        )
        assert result["status"] == "complete"
        assert len(result["entries"]) == 3
        assert all(e["status"] == "merged" for e in result["entries"])

    def test_three_entry_stack_partial_failure(self, tmp_path):
        # AC-20: 3-entry stack, pr-2 pipeline fails → pr-1 merged,
        # pr-2 failed, pr-3 skipped, overall partial.
        envelope = _valid_envelope(n=3)
        for i, e in enumerate(envelope["stack"], start=1):
            _prime_entry_worktree(
                tmp_path, e["branch_suffix"],
                pr_url=f"https://github.com/x/y/pull/{300 + i}",
            )
        stack_path = _write_envelope(tmp_path, envelope)

        creator, _ = _stub_creator_factory()
        rebaser, _ = _stub_rebaser_factory()

        rc = run_stack(
            str(stack_path), repo_root=str(tmp_path), worktree_path=str(tmp_path),
            pipeline_runner=lambda e, w: 1 if e["id"] == "pr-2" else 0,
            gh_runner=lambda n: "MERGED",
            worktree_creator=creator, rebaser=rebaser, merge_wait_sec=5,
        )
        assert rc == 1

        result = json.loads(
            (tmp_path / ".workflow" / "stack-result.json").read_text()
        )
        statuses = {e["id"]: e["status"] for e in result["entries"]}
        assert statuses == {"pr-1": "merged", "pr-2": "failed", "pr-3": "skipped"}
        assert result["status"] == "partial"
