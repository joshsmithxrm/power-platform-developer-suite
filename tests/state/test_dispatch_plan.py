#!/usr/bin/env python3
"""Tests for the ``/backlog dispatch`` plan helpers.

Covers:
  * Plan markdown round-trip (write -> parse -> write)
  * ``build_plan_from_dicts`` validation
  * ``run_conflict_check`` integration with a mocked ``inflight-check.py``
  * ``annotate_with_conflicts`` updates entry status correctly
  * ``mark_launched`` flips status + stamps metadata
  * End-to-end 3-worktree dispatch simulation: plan written, conflict
    check called per entry, launched markers applied
"""
from __future__ import annotations

import concurrent.futures
import json
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts"))

import dispatch_plan as dp  # noqa: E402  (path tweak required first)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture
def tmp_plan(tmp_path, monkeypatch):
    """Redirect plan_path() to a temp file."""
    target = tmp_path / "dispatch-plan.md"
    monkeypatch.setattr(dp, "plan_path", lambda: target)
    return target


@pytest.fixture
def sample_items():
    return [
        {
            "worktree": "feat/issue-660",
            "issues": [660],
            "areas": ["src/PPDS.Auth/"],
            "intent": "env var auth",
        },
        {
            "worktree": "feat/audit-capture",
            "issues": [101, 102],
            "areas": ["src/PPDS.Audit/", "src/PPDS.Telemetry/"],
            "intent": "audit capture pipeline",
        },
        {
            "worktree": "feat/cli-help",
            "issues": [],
            "areas": ["src/PPDS.Cli/Help/"],
            "intent": "cli help text overhaul",
        },
    ]


# ---------------------------------------------------------------------------
# Plan generation / serialization
# ---------------------------------------------------------------------------


class TestBuildPlan:
    def test_build_from_dicts_preserves_order(self, sample_items):
        plan = dp.build_plan_from_dicts(sample_items, generator="session-test")
        assert [e.worktree for e in plan.entries] == [
            "feat/issue-660",
            "feat/audit-capture",
            "feat/cli-help",
        ]
        assert plan.generator == "session-test"
        assert plan.version == dp.PLAN_SCHEMA_VERSION
        assert plan.generated  # timestamp filled in

    def test_build_with_no_items_is_valid(self):
        plan = dp.build_plan_from_dicts([], generator="g")
        assert plan.entries == []
        assert "no worktrees planned" in plan.as_markdown()

    def test_default_status_is_planned(self, sample_items):
        plan = dp.build_plan_from_dicts(sample_items)
        assert all(e.status == dp.STATUS_PLANNED for e in plan.entries)

    def test_explicit_status_passes_through(self):
        plan = dp.build_plan_from_dicts([
            {"worktree": "feat/x", "status": dp.STATUS_DONE},
        ])
        assert plan.entries[0].status == dp.STATUS_DONE


class TestPlanFile:
    def test_write_then_load_round_trip(self, tmp_plan, sample_items):
        plan = dp.build_plan_from_dicts(sample_items, generator="session-abc")
        dp.write_plan(plan)

        loaded = dp.load_plan()
        assert loaded.generator == "session-abc"
        assert [e.worktree for e in loaded.entries] == [
            e["worktree"] for e in sample_items
        ]
        # Issue / area parsing survives round trip.
        assert loaded.entries[0].issues == [660]
        assert loaded.entries[1].issues == [101, 102]
        assert loaded.entries[1].areas == [
            "src/PPDS.Audit/",
            "src/PPDS.Telemetry/",
        ]

    def test_load_when_file_missing_returns_empty_plan(self, tmp_plan):
        assert not tmp_plan.exists()
        plan = dp.load_plan()
        assert plan.entries == []
        assert plan.version == dp.PLAN_SCHEMA_VERSION

    def test_write_uses_atomic_rename(self, tmp_plan, sample_items):
        plan = dp.build_plan_from_dicts(sample_items)
        dp.write_plan(plan)
        # The .tmp staging file must not be left behind.
        leftover = tmp_plan.with_suffix(tmp_plan.suffix + ".tmp")
        assert not leftover.exists()
        assert tmp_plan.exists()

    def test_unknown_status_coerced_to_planned(self, tmp_plan):
        # Hand-write a plan with an invalid status — the parser should not
        # silently treat the typo as terminal.
        tmp_plan.write_text(
            "# Dispatch Plan\n\n"
            "Schema: 1\n"
            "Generated: 2026-04-19T00:00:00Z\n"
            "Generator: t\n\n"
            "## Planned\n\n"
            "### Worktree: feat/typo\n"
            "- Issues: #1\n"
            "- Areas: src/X/\n"
            "- Intent: test\n"
            "- Status: dunne\n",
            encoding="utf-8",
        )
        loaded = dp.load_plan()
        assert loaded.entries[0].status == dp.STATUS_PLANNED

    def test_human_inline_notes_dont_break_parser(self, tmp_plan):
        tmp_plan.write_text(
            "# Dispatch Plan\n\n"
            "Schema: 1\n"
            "Generated: 2026-04-19T00:00:00Z\n"
            "Generator: t\n\n"
            "## Planned\n\n"
            "### Worktree: feat/x\n"
            "- Issues: #1\n"
            "- Areas: src/X/\n"
            "- Intent: test\n"
            "- Status: planned\n"
            "Note from josh: revisit after PR #999 merges\n",
            encoding="utf-8",
        )
        loaded = dp.load_plan()
        assert len(loaded.entries) == 1
        assert loaded.entries[0].worktree == "feat/x"

    def test_summary_counts(self, tmp_plan):
        plan = dp.build_plan_from_dicts([
            {"worktree": "a", "status": dp.STATUS_PLANNED},
            {"worktree": "b", "status": dp.STATUS_PLANNED},
            {"worktree": "c", "status": dp.STATUS_CONFLICT},
            {"worktree": "d", "status": dp.STATUS_IN_FLIGHT},
        ])
        counts = plan.summary_counts()
        assert counts[dp.STATUS_PLANNED] == 2
        assert counts[dp.STATUS_CONFLICT] == 1
        assert counts[dp.STATUS_IN_FLIGHT] == 1
        assert counts[dp.STATUS_DONE] == 0


# ---------------------------------------------------------------------------
# Conflict-check integration
# ---------------------------------------------------------------------------


def _runner_factory(plan_per_call: list[tuple[int, dict]]):
    """Build a runner that returns the next (rc, payload) on each call.

    ``plan_per_call`` is consumed in order. Each entry is the simulated
    inflight-check.py response for one CLI invocation. ``payload`` is the
    JSON that the script would print on stdout; the runner serializes it
    automatically.
    """
    calls: list[list[str]] = []
    iterator = iter(plan_per_call)

    def runner(cmd: list[str]) -> tuple[int, str, str]:
        calls.append(cmd)
        try:
            rc, payload = next(iterator)
        except StopIteration as exc:
            raise AssertionError(
                f"runner called more times than scripted; extra cmd={cmd!r}"
            ) from exc
        return rc, json.dumps(payload), ""

    return runner, calls


class TestRunConflictCheck:
    def test_no_conflict_when_all_clear(self):
        entry = dp.PlanEntry(
            worktree="feat/x",
            issues=[1, 2],
            areas=["src/A/"],
        )
        # 2 issues + 1 area = 3 calls, all rc=0.
        runner, calls = _runner_factory([
            (dp.INFLIGHT_OK, {"conflicts": []}),
            (dp.INFLIGHT_OK, {"conflicts": []}),
            (dp.INFLIGHT_OK, {"conflicts": []}),
        ])
        ok, conflicts = dp.run_conflict_check(entry, runner=runner)
        assert ok is True
        assert conflicts == []
        assert len(calls) == 3
        # Issues are checked before areas (cleaner signal first).
        assert calls[0][-2:] == ["--issue", "1"]
        assert calls[1][-2:] == ["--issue", "2"]
        assert calls[2][-2:] == ["--area", "src/A/"]

    def test_conflict_collected_across_queries(self):
        entry = dp.PlanEntry(
            worktree="feat/x",
            issues=[5],
            areas=["src/A/", "src/B/"],
        )
        runner, _ = _runner_factory([
            (dp.INFLIGHT_OK, {"conflicts": []}),
            (dp.INFLIGHT_CONFLICT, {"conflicts": [
                {"session_id": "abc", "branch": "feat/sib1", "intent": "shipped"},
            ]}),
            (dp.INFLIGHT_CONFLICT, {"conflicts": [
                {"session_id": "def", "branch": "feat/sib2", "intent": "wip"},
            ]}),
        ])
        ok, conflicts = dp.run_conflict_check(entry, runner=runner)
        assert ok is False
        assert {c["session_id"] for c in conflicts} == {"abc", "def"}

    def test_no_issues_no_areas_treated_as_ok(self):
        entry = dp.PlanEntry(worktree="feat/empty")
        runner, calls = _runner_factory([])  # no calls expected
        ok, conflicts = dp.run_conflict_check(entry, runner=runner)
        assert ok is True
        assert conflicts == []
        assert calls == []

    def test_inflight_badargs_raises(self):
        entry = dp.PlanEntry(worktree="feat/x", issues=[1])
        runner, _ = _runner_factory([(dp.INFLIGHT_BADARGS, {})])
        with pytest.raises(RuntimeError, match="rc=2"):
            dp.run_conflict_check(entry, runner=runner)

    def test_inflight_failure_includes_stderr(self):
        """Gemini #3106679471: stderr from inflight-check.py must surface in
        the RuntimeError so the operator can diagnose without re-running.
        """
        entry = dp.PlanEntry(worktree="feat/x", issues=[1])

        def runner(cmd):
            return (
                dp.INFLIGHT_BADARGS,
                "",
                "argparse: --issue must be a positive integer\n",
            )

        with pytest.raises(RuntimeError, match="argparse: --issue must be a positive integer"):
            dp.run_conflict_check(entry, runner=runner)

    def test_inflight_failure_without_stderr_still_raises(self):
        """Empty stderr should still produce a sensible error (no trailing
        ``stderr:`` suffix when nothing to add).
        """
        entry = dp.PlanEntry(worktree="feat/x", issues=[1])

        def runner(cmd):
            return (99, "", "")

        with pytest.raises(RuntimeError) as excinfo:
            dp.run_conflict_check(entry, runner=runner)
        assert "rc=99" in str(excinfo.value)
        assert "stderr:" not in str(excinfo.value)


class TestAnnotateWithConflicts:
    def test_planned_entry_with_conflict_flips_status(self, tmp_plan):
        plan = dp.build_plan_from_dicts([
            {"worktree": "feat/x", "issues": [1], "areas": ["src/A/"]},
        ])
        runner, _ = _runner_factory([
            # 1 issue + 1 area
            (dp.INFLIGHT_CONFLICT, {"conflicts": [
                {"session_id": "rival", "branch": "feat/rival",
                 "intent": "doing the same thing"},
            ]}),
            (dp.INFLIGHT_OK, {"conflicts": []}),
        ])
        dp.annotate_with_conflicts(plan, runner=runner)

        entry = plan.find("feat/x")
        assert entry.status == dp.STATUS_CONFLICT
        assert "rival" in entry.conflict_detail
        assert "feat/rival" in entry.conflict_detail
        assert "doing the same thing" in entry.conflict_detail

    def test_planned_entry_without_conflict_unchanged(self):
        plan = dp.build_plan_from_dicts([
            {"worktree": "feat/x", "issues": [1]},
        ])
        runner, _ = _runner_factory([(dp.INFLIGHT_OK, {"conflicts": []})])
        dp.annotate_with_conflicts(plan, runner=runner)
        assert plan.find("feat/x").status == dp.STATUS_PLANNED

    def test_terminal_status_left_alone(self):
        # in-flight entries should not be re-checked — they are already
        # launched, and a re-check would burn an unnecessary subprocess.
        plan = dp.build_plan_from_dicts([
            {"worktree": "feat/done", "issues": [1], "status": dp.STATUS_DONE},
            {"worktree": "feat/wip", "issues": [2], "status": dp.STATUS_IN_FLIGHT},
            {"worktree": "feat/skip", "issues": [3], "status": dp.STATUS_SKIPPED},
        ])
        runner, calls = _runner_factory([])  # zero calls expected
        dp.annotate_with_conflicts(plan, runner=runner)
        assert calls == []


# ---------------------------------------------------------------------------
# mark_launched
# ---------------------------------------------------------------------------


class TestMarkLaunched:
    def test_flips_status_and_stamps_metadata(self, sample_items):
        plan = dp.build_plan_from_dicts(sample_items)
        dp.mark_launched(plan, "feat/issue-660", session_id="abc12345")

        entry = plan.find("feat/issue-660")
        assert entry.status == dp.STATUS_IN_FLIGHT
        assert entry.launched_by_session == "abc12345"
        assert entry.launched_at  # auto-stamped

    def test_unknown_worktree_raises(self, sample_items):
        plan = dp.build_plan_from_dicts(sample_items)
        with pytest.raises(KeyError):
            dp.mark_launched(plan, "feat/typo")

    def test_explicit_when_used(self, sample_items):
        plan = dp.build_plan_from_dicts(sample_items)
        dp.mark_launched(
            plan, "feat/issue-660", session_id="x", when="2026-04-19T12:00:00Z",
        )
        assert plan.find("feat/issue-660").launched_at == "2026-04-19T12:00:00Z"


# ---------------------------------------------------------------------------
# End-to-end: 3-worktree dispatch simulation
# ---------------------------------------------------------------------------


class TestDispatchSimulation:
    """The integration scenario the spec calls out:

    Simulate a 3-worktree dispatch plan. Verify:
      * plan file written
      * conflict-check is called per planned entry
      * launched markers applied to the right entries
      * a mid-flow entry can be skipped without breaking the plan
    """

    def test_three_worktree_dispatch_flow(self, tmp_plan, sample_items):
        # Phase A: build + persist the plan.
        plan = dp.build_plan_from_dicts(
            sample_items, generator="session-dispatcher",
        )
        dp.write_plan(plan)
        assert tmp_plan.exists()

        # Phase B: conflict check fires once per (issue, area) pair, in
        # plan-file order. Entry 1 has 1 issue + 1 area = 2 calls. Entry 2
        # has 2 issues + 2 areas = 4 calls. Entry 3 has 0 issues + 1 area
        # = 1 call. Make entry 2 conflict on its first area; the rest pass.
        runner, calls = _runner_factory([
            # entry 1: clean
            (dp.INFLIGHT_OK, {"conflicts": []}),
            (dp.INFLIGHT_OK, {"conflicts": []}),
            # entry 2: issue-101 ok, issue-102 ok, src/PPDS.Audit/ conflict, src/PPDS.Telemetry/ ok
            (dp.INFLIGHT_OK, {"conflicts": []}),
            (dp.INFLIGHT_OK, {"conflicts": []}),
            (dp.INFLIGHT_CONFLICT, {"conflicts": [
                {"session_id": "blocker", "branch": "feat/audit-rival",
                 "intent": "already shipping audit pipeline"},
            ]}),
            (dp.INFLIGHT_OK, {"conflicts": []}),
            # entry 3: clean
            (dp.INFLIGHT_OK, {"conflicts": []}),
        ])

        # Reload from disk to prove the on-disk plan is the source of truth.
        loaded = dp.load_plan()
        dp.annotate_with_conflicts(loaded, runner=runner)
        dp.write_plan(loaded)

        # Total expected calls = 2 + 4 + 1 = 7
        assert len(calls) == 7

        # Verify status flips landed correctly.
        post_b = dp.load_plan()
        assert post_b.find("feat/issue-660").status == dp.STATUS_PLANNED
        assert post_b.find("feat/audit-capture").status == dp.STATUS_CONFLICT
        assert (
            "blocker"
            in post_b.find("feat/audit-capture").conflict_detail
        )
        assert post_b.find("feat/cli-help").status == dp.STATUS_PLANNED

        # Phase D: dispatch only the unblocked entries. Mark each launched.
        for entry in post_b.entries:
            if entry.status == dp.STATUS_PLANNED:
                dp.mark_launched(
                    post_b, entry.worktree,
                    session_id=f"sid-{entry.worktree.split('/')[-1]}",
                )
        dp.write_plan(post_b)

        # Phase E: confirm summary reflects the wave.
        final = dp.load_plan()
        counts = final.summary_counts()
        assert counts[dp.STATUS_IN_FLIGHT] == 2  # issue-660 + cli-help
        assert counts[dp.STATUS_CONFLICT] == 1   # audit-capture
        assert counts[dp.STATUS_PLANNED] == 0
        # Launched entries got a session ID.
        assert (
            final.find("feat/issue-660").launched_by_session == "sid-issue-660"
        )
        assert (
            final.find("feat/cli-help").launched_by_session == "sid-cli-help"
        )
        # Conflict entry was NOT launched.
        assert final.find("feat/audit-capture").launched_by_session == ""


# ---------------------------------------------------------------------------
# Null-tolerance in build_plan_from_dicts (Gemini #3106679474)
# ---------------------------------------------------------------------------


class TestBuildPlanNullTolerance:
    """AI-generated JSON often emits ``null`` for optional fields rather than
    omitting them. ``build_plan_from_dicts`` must absorb explicit nulls
    without TypeError.
    """

    def test_explicit_null_issues_treated_as_empty(self):
        plan = dp.build_plan_from_dicts([
            {"worktree": "feat/x", "issues": None, "areas": ["src/A/"]},
        ])
        assert plan.entries[0].issues == []
        assert plan.entries[0].areas == ["src/A/"]

    def test_explicit_null_areas_treated_as_empty(self):
        plan = dp.build_plan_from_dicts([
            {"worktree": "feat/x", "issues": [1], "areas": None},
        ])
        assert plan.entries[0].issues == [1]
        assert plan.entries[0].areas == []

    def test_explicit_null_intent_treated_as_empty_string(self):
        plan = dp.build_plan_from_dicts([
            {"worktree": "feat/x", "intent": None},
        ])
        assert plan.entries[0].intent == ""

    def test_explicit_null_status_falls_back_to_planned(self):
        plan = dp.build_plan_from_dicts([
            {"worktree": "feat/x", "status": None},
        ])
        assert plan.entries[0].status == dp.STATUS_PLANNED

    def test_all_optionals_null_does_not_raise(self):
        plan = dp.build_plan_from_dicts([
            {
                "worktree": "feat/x",
                "issues": None,
                "areas": None,
                "intent": None,
                "status": None,
            },
        ])
        e = plan.entries[0]
        assert e.worktree == "feat/x"
        assert e.issues == []
        assert e.areas == []
        assert e.intent == ""
        assert e.status == dp.STATUS_PLANNED


# ---------------------------------------------------------------------------
# File locking concurrency (Gemini #3106679468)
# ---------------------------------------------------------------------------


class TestPlanLocking:
    """Mirror the inflight ThreadPoolExecutor concurrency check: spawn N
    simultaneous read-modify-writes through ``locked_plan`` and verify
    every update lands without corrupting the markdown file.
    """

    def test_concurrent_writes_no_corruption(self, tmp_plan):
        # Seed an empty plan so locked_plan has something to start from.
        dp.write_plan(dp.DispatchPlan(generator="seed"))

        def add_entry(i: int) -> str:
            with dp.locked_plan() as plan:
                plan.entries.append(
                    dp.PlanEntry(
                        worktree=f"feat/parallel-{i}",
                        issues=[1000 + i],
                        areas=[f"src/Module{i}/"],
                        intent=f"parallel work {i}",
                    )
                )
            return f"feat/parallel-{i}"

        with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
            results = list(pool.map(add_entry, range(5)))

        assert sorted(results) == [f"feat/parallel-{i}" for i in range(5)]

        # Reload from disk: every concurrent append must be present and
        # the file must still parse cleanly (no half-written tmp files,
        # no truncated markdown).
        loaded = dp.load_plan()
        worktrees = sorted(e.worktree for e in loaded.entries)
        assert worktrees == [f"feat/parallel-{i}" for i in range(5)]

        # No orphaned .tmp file left behind (atomic rename completed).
        leftover = tmp_plan.with_suffix(tmp_plan.suffix + ".tmp")
        assert not leftover.exists()

    def test_locked_plan_persists_mutations(self, tmp_plan):
        with dp.locked_plan() as plan:
            plan.entries.append(dp.PlanEntry(worktree="feat/from-cm"))

        loaded = dp.load_plan()
        assert [e.worktree for e in loaded.entries] == ["feat/from-cm"]

    def test_locked_plan_starts_empty_when_file_missing(self, tmp_plan):
        assert not tmp_plan.exists()
        with dp.locked_plan() as plan:
            assert plan.entries == []
            plan.entries.append(dp.PlanEntry(worktree="feat/first"))

        assert tmp_plan.exists()
        loaded = dp.load_plan()
        assert [e.worktree for e in loaded.entries] == ["feat/first"]
