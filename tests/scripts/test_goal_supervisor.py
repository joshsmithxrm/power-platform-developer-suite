"""Unit tests for scripts/goal_supervisor.py — ACs 01-19 from specs/feat-1069-supervisor-pattern.md."""
from __future__ import annotations

import ast
import json
import subprocess
import sys
from io import StringIO
from pathlib import Path
from unittest.mock import patch

import pytest

# tests/conftest.py prepends scripts/ to sys.path; reach the module by name.
import goal_supervisor as gs
import pr_stack


# ---------- envelope helpers ----------

def _valid_entry(id="pr-1", branch_suffix="pr1", depends_on=None):
    return {
        "id": id,
        "title": f"feat: {id}",
        "branch_suffix": branch_suffix,
        "plan": f".plans/foo-{id}.md",
        "files": ["src/a.py"],
        "size_estimate": "~100 LOC",
        "depends_on": depends_on or [],
        "ac_refs": [],
    }


def _valid_stack(n=2, schema_version="1.0"):
    entries = [
        _valid_entry(
            f"pr-{i}",
            f"pr{i}",
            depends_on=[f"pr-{i-1}"] if i > 1 else [],
        )
        for i in range(1, n + 1)
    ]
    return {
        "schema_version": schema_version,
        "spec": "specs/foo.md",
        "created_at": "2026-05-15T00:00:00+00:00",
        "stack": entries,
    }


def _write_envelope(path: Path, envelope: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(envelope, indent=2) + "\n", encoding="utf-8")


def _make_envelope_v11(tmp_path: Path, entries_state: list, *, n=None) -> Path:
    """Build a v1.1 envelope on disk with the given per-entry overrides.

    entries_state: list of dicts (each merged onto the base entry).
    Returns the supervisor worktree path; envelope lives at
    <supervisor_worktree>/.workflow/goal-envelope.json.
    """
    if n is None:
        n = len(entries_state)
    stack = _valid_stack(n)
    envelope = gs.build_envelope_v11(
        stack, supervisor_worktree=str(tmp_path), poll_interval=270,
    )
    for entry, overrides in zip(envelope["stack"], entries_state):
        entry.update(overrides)
    sw = tmp_path / "supervisor"
    sw.mkdir(parents=True, exist_ok=True)
    envelope["supervisor_worktree"] = str(sw)
    _write_envelope(sw / ".workflow" / "goal-envelope.json", envelope)
    return sw


# ---------- spawn-runner mocks ----------

class _CompletedProcessStub:
    def __init__(self, returncode=0, stdout="", stderr=""):
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr


def _make_spawn_runner(*, fail_at: int | None = None):
    """Return a fake subprocess_runner that succeeds N times then optionally fails."""
    calls = []
    counter = {"n": 0}

    def runner(cmd, cwd=None, timeout=None):
        calls.append({"cmd": list(cmd), "cwd": cwd, "timeout": timeout})
        counter["n"] += 1
        if fail_at is not None and counter["n"] == fail_at:
            return _CompletedProcessStub(returncode=1, stderr="boom")
        # Distinguish worktree-create vs. start-bg-spawn by script path.
        script = cmd[1] if len(cmd) > 1 else ""
        if script.endswith("start-bg-spawn.py"):
            short = f"short{counter['n']:04x}"
            payload = {"short": short, "sessionId": f"sid-{short}", "cwd": "/x"}
            return _CompletedProcessStub(returncode=0, stdout=json.dumps(payload))
        # worktree-create returns no useful stdout
        return _CompletedProcessStub(returncode=0)

    runner.calls = calls  # type: ignore[attr-defined]
    return runner


# ---------- AC-01 — spawn creates envelope ----------

def test_spawn_creates_envelope(tmp_path):  # AC-01
    stack_path = tmp_path / "stack.json"
    stack_path.write_text(json.dumps(_valid_stack(2)), encoding="utf-8")

    sw = tmp_path / "sw"; sw.mkdir()
    inbox_calls = []
    summary = gs.spawn(
        str(stack_path),
        supervisor_worktree=str(sw),
        repo_root=str(tmp_path),
        subprocess_runner=_make_spawn_runner(),
        inbox_sender=lambda wt, msg: inbox_calls.append((wt, msg)),
    )

    env_path = sw / ".workflow" / "goal-envelope.json"
    assert env_path.exists()
    envelope = json.loads(env_path.read_text(encoding="utf-8"))
    assert envelope["schema_version"] == "1.1"
    assert envelope["goal_poll_interval_sec"] == 270
    assert all(e["goal_state"] == "spawned" for e in envelope["stack"])
    assert summary["spawned"] == 2
    assert len(inbox_calls) == 2


# ---------- AC-02 — invalid stack rejected ----------

def test_spawn_rejects_invalid_stack(tmp_path):  # AC-02
    bad = {"schema_version": "1.0", "spec": "", "created_at": "x", "stack": []}
    stack_path = tmp_path / "bad.json"
    stack_path.write_text(json.dumps(bad), encoding="utf-8")
    with pytest.raises(ValueError):
        gs.spawn(
            str(stack_path),
            supervisor_worktree=str(tmp_path),
            subprocess_runner=_make_spawn_runner(),
            inbox_sender=lambda *a, **kw: None,
        )


def test_cli_spawn_invalid_stack_exits_1(tmp_path, capsys):  # AC-02 (CLI)
    bad = {"schema_version": "1.0", "spec": "", "created_at": "x", "stack": []}
    stack_path = tmp_path / "bad.json"
    stack_path.write_text(json.dumps(bad), encoding="utf-8")
    rc = gs.main(["spawn", str(stack_path), "--supervisor-worktree", str(tmp_path)])
    captured = capsys.readouterr()
    assert rc == 1
    assert "goal_supervisor" in captured.err


# ---------- AC-03 — wrong major version rejected ----------

def test_spawn_rejects_wrong_major(tmp_path):  # AC-03
    bad = _valid_stack(2)
    bad["schema_version"] = "2.0"
    stack_path = tmp_path / "v2.json"
    stack_path.write_text(json.dumps(bad), encoding="utf-8")
    with pytest.raises(ValueError, match="schema_version"):
        gs.spawn(
            str(stack_path),
            supervisor_worktree=str(tmp_path),
            subprocess_runner=_make_spawn_runner(),
            inbox_sender=lambda *a, **kw: None,
        )


# ---------- AC-04 — last_polled_at updated ----------

def test_poll_updates_last_polled_at(tmp_path):  # AC-04
    sw = _make_envelope_v11(tmp_path, [
        {"goal_state": "working", "session_short": "abc1"},
        {"goal_state": "spawned", "session_short": "abc2"},
    ])
    verdict = gs.poll(
        supervisor_worktree=str(sw),
        jobs_dir=tmp_path / "jobs",  # non-existent → empty job states
        workflow_state_reader=lambda wt: {},
        gh_runner=lambda n: None,
        haiku_runner=lambda p: '{"verdict": "working", "confidence": "high", "reason": "x"}',
        clock=lambda: "2026-05-16T10:00:00+00:00",
    )
    for entry in verdict["entries"]:
        assert entry["last_polled_at"] == "2026-05-16T10:00:00+00:00"
    # Verify it was persisted.
    saved = json.loads((sw / ".workflow" / "goal-envelope.json").read_text(encoding="utf-8"))
    for entry in saved["stack"]:
        assert entry["last_polled_at"] == "2026-05-16T10:00:00+00:00"


# ---------- AC-05 — all_merged verdict ----------

def test_poll_all_merged(tmp_path):  # AC-05
    sw = _make_envelope_v11(tmp_path, [
        {"goal_state": "merged"},
        {"goal_state": "merged"},
    ])
    verdict = gs.poll(
        supervisor_worktree=str(sw),
        jobs_dir=tmp_path / "jobs",
        workflow_state_reader=lambda wt: {},
        gh_runner=lambda n: None,
        haiku_runner=lambda p: "",
    )
    assert verdict["goal_state"] == "all_merged"


# ---------- AC-06 — escalation on blocked + needs ----------

def _write_job_state(jobs_dir: Path, short: str, payload: dict) -> None:
    d = jobs_dir / short
    d.mkdir(parents=True, exist_ok=True)
    (d / "state.json").write_text(json.dumps(payload), encoding="utf-8")


def test_poll_escalation_blocked(tmp_path):  # AC-06
    sw = _make_envelope_v11(tmp_path, [
        {"goal_state": "working", "session_short": "abcd"},
    ])
    jobs = tmp_path / "jobs"
    _write_job_state(jobs, "abcd", {"state": "blocked", "needs": "fix the layout"})
    verdict = gs.poll(
        supervisor_worktree=str(sw),
        jobs_dir=jobs,
        workflow_state_reader=lambda wt: {},
        gh_runner=lambda n: None,
        haiku_runner=lambda p: "",
    )
    entry = verdict["entries"][0]
    assert entry["goal_state"] == "blocked"
    assert entry["blocked_needs"] == "fix the layout"
    assert verdict["goal_state"] == "escalated"


# ---------- AC-07 — merged via gh pr view ----------

def test_poll_merged_via_gh(tmp_path):  # AC-07
    sw = _make_envelope_v11(tmp_path, [
        {
            "goal_state": "working",
            "session_short": "abcd",
            "pr_number": 1234,
            "pr_url": "https://github.com/x/y/pull/1234",
        },
    ])
    jobs = tmp_path / "jobs"
    _write_job_state(jobs, "abcd", {"state": "done", "needs": ""})
    verdict = gs.poll(
        supervisor_worktree=str(sw),
        jobs_dir=jobs,
        workflow_state_reader=lambda wt: {},
        gh_runner=lambda n: "MERGED" if n == 1234 else None,
        haiku_runner=lambda p: "",
        clock=lambda: "2026-05-16T11:00:00+00:00",
    )
    entry = verdict["entries"][0]
    assert entry["goal_state"] == "merged"
    assert entry["merged_at"] == "2026-05-16T11:00:00+00:00"
    assert entry["pr_state"] == "MERGED"


# ---------- AC-08 — worker error state ----------

def test_poll_worker_error(tmp_path):  # AC-08
    sw = _make_envelope_v11(tmp_path, [
        {"goal_state": "working", "session_short": "errx"},
    ])
    jobs = tmp_path / "jobs"
    _write_job_state(jobs, "errx", {"state": "error", "needs": ""})
    verdict = gs.poll(
        supervisor_worktree=str(sw),
        jobs_dir=jobs,
        workflow_state_reader=lambda wt: {},
        gh_runner=lambda n: None,
        haiku_runner=lambda p: "",
    )
    assert verdict["entries"][0]["goal_state"] == "error"
    assert verdict["goal_state"] == "escalated"


# ---------- AC-09 — empty needs does NOT escalate ----------

def test_poll_no_escalation_empty_needs(tmp_path):  # AC-09
    sw = _make_envelope_v11(tmp_path, [
        {"goal_state": "working", "session_short": "okay"},
    ])
    jobs = tmp_path / "jobs"
    _write_job_state(jobs, "okay", {"state": "blocked", "needs": "   "})
    verdict = gs.poll(
        supervisor_worktree=str(sw),
        jobs_dir=jobs,
        workflow_state_reader=lambda wt: {},
        gh_runner=lambda n: None,
        haiku_runner=lambda p: "",
    )
    entry = verdict["entries"][0]
    assert entry["goal_state"] != "blocked"
    assert verdict["goal_state"] != "escalated"


# ---------- AC-10 — Haiku predicate template renders + parses ----------

def test_haiku_predicate_parses(tmp_path):  # AC-10
    prompt = gs.render_haiku_prompt(
        entry_id="pr-1",
        title="feat: foo",
        session_state="done",
        workflow_phase="implementing",
        pr_url="",
        pr_state="",
    )
    assert "entry_id: pr-1" in prompt
    assert "title: feat: foo" in prompt
    assert '"verdict"' in prompt

    # Drive an actual poll that triggers Haiku (session=done, no pr_url).
    sw = _make_envelope_v11(tmp_path, [
        {"goal_state": "working", "session_short": "ambi"},
    ])
    jobs = tmp_path / "jobs"
    _write_job_state(jobs, "ambi", {"state": "done", "needs": ""})

    def fake_haiku(p):
        # Return a well-formed Haiku response.
        return '{"verdict": "error", "confidence": "high", "reason": "no pr"}'

    verdict = gs.poll(
        supervisor_worktree=str(sw),
        jobs_dir=jobs,
        workflow_state_reader=lambda wt: {},
        gh_runner=lambda n: None,
        haiku_runner=fake_haiku,
    )
    assert verdict["entries"][0]["goal_state"] == "error"


def test_haiku_parse_failure_marks_error(tmp_path):
    sw = _make_envelope_v11(tmp_path, [
        {"goal_state": "working", "session_short": "ambi"},
    ])
    jobs = tmp_path / "jobs"
    _write_job_state(jobs, "ambi", {"state": "done", "needs": ""})
    verdict = gs.poll(
        supervisor_worktree=str(sw),
        jobs_dir=jobs,
        workflow_state_reader=lambda wt: {},
        gh_runner=lambda n: None,
        haiku_runner=lambda p: "this is not json",
    )
    assert verdict["entries"][0]["goal_state"] == "error"


# ---------- AC-11 — stdout discipline (CLI) ----------

def test_stdout_discipline(tmp_path, capsys):  # AC-11
    sw = _make_envelope_v11(tmp_path, [{"goal_state": "merged"}])
    rc = gs.main(["poll", "--worktree", str(sw)])
    captured = capsys.readouterr()
    assert rc == 0
    # stdout must parse as JSON.
    payload = json.loads(captured.out)
    assert payload["goal_state"] == "all_merged"
    # stderr must contain the progress line.
    assert "goal_supervisor: poll" in captured.err


# ---------- AC-12 — no extra imports ----------

ALLOWED_TOPLEVEL_IMPORTS = {
    # Standard library.
    "argparse", "json", "os", "subprocess", "sys", "tempfile",
    "datetime", "pathlib", "typing", "__future__",
    # Existing project scripts.
    "pr_stack", "supervisor_msg",
    # claude_dispatch is imported lazily — see _default_haiku_runner.
}


def test_no_extra_imports():  # AC-12
    src = Path(gs.__file__).read_text(encoding="utf-8")
    tree = ast.parse(src)
    bad = []
    for node in ast.iter_child_nodes(tree):
        if isinstance(node, ast.Import):
            for n in node.names:
                top = n.name.split(".")[0]
                if top not in ALLOWED_TOPLEVEL_IMPORTS:
                    bad.append(top)
        elif isinstance(node, ast.ImportFrom):
            mod = (node.module or "").split(".")[0]
            if mod and mod not in ALLOWED_TOPLEVEL_IMPORTS:
                bad.append(mod)
    assert bad == [], f"unexpected imports: {bad}"


# ---------- AC-13 — pr_stack accepts v1.1 envelope ----------

class TestGoalSupervisorCompat:
    def test_validate_v1_1_accepted(self):  # AC-13
        stack = _valid_stack(2)
        envelope = gs.build_envelope_v11(
            stack, supervisor_worktree="/abs/path", poll_interval=270,
        )
        # Should not raise.
        pr_stack.validate_envelope(envelope)


# ---------- AC-14, AC-15, AC-16 — /orchestrate SKILL.md content ----------

_SKILL_PATH = Path(gs.__file__).resolve().parent.parent / ".claude" / "skills" / "orchestrate" / "SKILL.md"


def _skill_text() -> str:
    return _SKILL_PATH.read_text(encoding="utf-8")


class TestOrchestrateSkill:
    def test_skill_documents_spawn(self):  # AC-14
        text = _skill_text()
        assert "python scripts/goal_supervisor.py spawn" in text
        assert "270" in text
        # PushNotification + gh comment + all_merged termination
        assert "PushNotification" in text
        assert "gh issue comment" in text
        assert "all_merged" in text

    def test_skill_documents_escalation_rule(self):  # AC-15
        text = _skill_text()
        # Single-signal rule must mention both conditions.
        assert "state=blocked" in text
        assert 'needs != ""' in text or "needs is non-empty" in text

    def test_skill_documents_crash_tolerance(self):  # AC-16
        text = _skill_text()
        assert "pr_monitor" in text
        assert ("independent" in text.lower() or
                "best-effort" in text.lower() or
                "crash" in text.lower())


# ---------- AC-17 — smoke two-entry all_merged ----------

def test_smoke_two_entry_all_merged(tmp_path):  # AC-17
    sw = _make_envelope_v11(tmp_path, [
        {"goal_state": "merged"},
        {"goal_state": "merged"},
    ])
    verdict = gs.poll(
        supervisor_worktree=str(sw),
        jobs_dir=tmp_path / "jobs",
        workflow_state_reader=lambda wt: {},
        gh_runner=lambda n: None,
        haiku_runner=lambda p: "",
    )
    assert verdict["goal_state"] == "all_merged"


# ---------- AC-18 — smoke one blocked ----------

def test_smoke_one_blocked(tmp_path):  # AC-18
    sw = _make_envelope_v11(tmp_path, [
        {"goal_state": "blocked", "blocked_needs": "fix the layout"},
    ])
    # Avoid re-evaluation overwriting goal_state (no job state file → not done).
    verdict = gs.poll(
        supervisor_worktree=str(sw),
        jobs_dir=tmp_path / "jobs",
        workflow_state_reader=lambda wt: {},
        gh_runner=lambda n: None,
        haiku_runner=lambda p: "",
    )
    assert verdict["goal_state"] == "escalated"


# ---------- AC-19 — worker prompt template contents ----------

def test_worker_prompt_contains_workflow_contract():  # AC-19
    text = gs.WORKER_PROMPT_TEMPLATE
    # (a) skip-design note referencing the pre-approved plan
    assert "pre-approved plan" in text
    assert "Skip /design" in text
    # (b) pipeline.py --spec --plan invocation
    assert "scripts/pipeline.py --spec" in text and "--plan" in text
    # (c) --resume fallback
    assert "--resume" in text
    # (d) pr_monitor.py via Bash run_in_background=true
    assert "pr_monitor.py" in text
    assert "run_in_background=true" in text
    # (e) re-engagement reads pr-monitor-result.json and terminates
    assert "pr-monitor-result.json" in text
    assert "Terminate" in text


def test_render_worker_prompt_fills_placeholders():
    entry = {
        "id": "pr-1", "title": "feat: x",
        "worktree_path": "/abs/path",
        "plan": ".plans/x.md", "branch_suffix": "pr1",
    }
    out = gs.render_worker_prompt(entry, spec="specs/foo.md")
    assert "/abs/path" in out
    assert "feat: x" in out
    assert "feat/pr1" in out
    assert "specs/foo.md" in out
