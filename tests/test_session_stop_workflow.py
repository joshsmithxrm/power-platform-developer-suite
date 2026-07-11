"""Tests for session-stop-workflow hook — .claude/ enforcement and workflow surface."""
import importlib.util
import json
import os
import subprocess
import sys
from unittest.mock import patch

import pytest


def _load_hook():
    """Import session-stop-workflow.py as a module."""
    hook_path = os.path.join(
        os.path.dirname(__file__),
        os.pardir,
        ".claude",
        "hooks",
        "session-stop-workflow.py",
    )
    hook_path = os.path.normpath(hook_path)
    spec = importlib.util.spec_from_file_location("session_stop_workflow", hook_path)
    mod = importlib.util.module_from_spec(spec)
    hooks_dir = os.path.dirname(hook_path)
    if hooks_dir not in sys.path:
        sys.path.insert(0, hooks_dir)
    spec.loader.exec_module(mod)
    return mod


hook = _load_hook()


class TestClaudeDirEnforcement:
    def test_claude_dir_requires_enforcement(self):
        """AC-29: .claude/ files are NOT in non_code_prefixes, so they require enforcement."""
        # Read the source file and check that .claude/ is not in non_code_prefixes
        hook_path = os.path.join(
            os.path.dirname(__file__),
            os.pardir,
            ".claude",
            "hooks",
            "session-stop-workflow.py",
        )
        hook_path = os.path.normpath(hook_path)
        with open(hook_path, "r") as f:
            source = f.read()

        # Find the non_code_prefixes tuple in the source
        # It should NOT contain ".claude/"
        import ast

        tree = ast.parse(source)
        for node in ast.walk(tree):
            if isinstance(node, ast.Assign):
                for target in node.targets:
                    if isinstance(target, ast.Name) and target.id == "non_code_prefixes":
                        # Extract tuple elements
                        if isinstance(node.value, ast.Tuple):
                            elements = [
                                elt.value
                                for elt in node.value.elts
                                if isinstance(elt, ast.Constant)
                            ]
                            assert ".claude/" not in elements, (
                                ".claude/ should NOT be in non_code_prefixes — "
                                "process code requires workflow enforcement"
                            )


class TestWorkflowSurface:
    def test_workflow_surface_accepted(self):
        """AC-25/AC-31: 'workflow' is a valid surface in session-stop-workflow."""
        hook_path = os.path.join(
            os.path.dirname(__file__),
            os.pardir,
            ".claude",
            "hooks",
            "session-stop-workflow.py",
        )
        hook_path = os.path.normpath(hook_path)
        with open(hook_path, "r") as f:
            source = f.read()

        import ast

        tree = ast.parse(source)
        for node in ast.walk(tree):
            if isinstance(node, ast.Assign):
                for target in node.targets:
                    if isinstance(target, ast.Name) and target.id == "valid_surfaces":
                        if isinstance(node.value, ast.Tuple):
                            elements = [
                                elt.value
                                for elt in node.value.elts
                                if isinstance(elt, ast.Constant)
                            ]
                            assert "workflow" in elements, (
                                "'workflow' must be in valid_surfaces"
                            )

    def test_workflow_surface_in_pr_gate(self):
        """AC-25: 'workflow' is a valid surface in pr-gate."""
        hook_path = os.path.join(
            os.path.dirname(__file__),
            os.pardir,
            ".claude",
            "hooks",
            "pr-gate.py",
        )
        hook_path = os.path.normpath(hook_path)
        with open(hook_path, "r") as f:
            source = f.read()

        import ast

        tree = ast.parse(source)
        for node in ast.walk(tree):
            if isinstance(node, ast.Assign):
                for target in node.targets:
                    if isinstance(target, ast.Name) and target.id == "valid_surfaces":
                        if isinstance(node.value, ast.Tuple):
                            elements = [
                                elt.value
                                for elt in node.value.elts
                                if isinstance(elt, ast.Constant)
                            ]
                            assert "workflow" in elements, (
                                "'workflow' must be in pr-gate valid_surfaces"
                            )


# ---------------------------------------------------------------------------
# #1177 — reviewer-optional mode: the triage gate respects pr.reviewer
# ---------------------------------------------------------------------------


def _base_state():
    """Workflow state that passes every gate EXCEPT (optionally) PR triage."""
    return {
        "gates": {"passed": True, "commit_ref": "abc123"},
        "verify": {"workflow": "x"},
        "qa": {"workflow": "x"},
        "review": {"passed": True},
        "pr": {
            "url": "https://github.com/o/r/pull/1",
            "invoked_via_skill": True,
        },
    }


def _run_hook(tmp_path, state, monkeypatch, capsys, branch="feat/x"):
    """Drive session_stop_workflow.main() end-to-end against a temp worktree.

    Mocks git so the hook sees a feature branch with one code commit ahead of
    main and a clean tree (HEAD == gates.commit_ref), so the block/allow
    decision is driven purely by the provided state.json. Returns
    (exit_code, combined_stdout+stderr).
    """
    import io as _io

    monkeypatch.setenv("CLAUDE_PROJECT_DIR", str(tmp_path))
    monkeypatch.delenv("PPDS_PIPELINE", raising=False)
    monkeypatch.delenv("PPDS_SHAKEDOWN", raising=False)

    wf = tmp_path / ".workflow"
    wf.mkdir(parents=True, exist_ok=True)
    (wf / "state.json").write_text(json.dumps(state), encoding="utf-8")

    def _fake_git(*args, **kwargs):
        argv = args[0] if args else kwargs.get("args", [])
        if argv[:2] == ["git", "rev-parse"] and "--abbrev-ref" in argv:
            out = branch
        elif argv[:2] == ["git", "rev-parse"]:
            out = "abc123"
        elif argv[:3] == ["git", "rev-list", "--count"]:
            out = "1"
        elif argv[:3] == ["git", "diff", "--name-only"]:
            out = "scripts/x.py\n"
        elif argv[:3] == ["git", "status", "--porcelain"]:
            out = ""
        else:
            out = ""
        return subprocess.CompletedProcess(argv, 0, out, "")

    with patch.object(hook.subprocess, "run", side_effect=_fake_git), \
         patch("sys.stdin", _io.StringIO("{}")):
        with pytest.raises(SystemExit) as exc:
            hook.main()

    captured = capsys.readouterr()
    code = exc.value.code if exc.value.code is not None else 0
    return code, captured.out + captured.err


class TestReviewerOptionalTriageGate:
    """#1177: the session-stop triage gate reads pr.reviewer.

    reviewer == "none"  → no external reviewer → triage gate skipped.
    reviewer == "gemini" or ABSENT (legacy) → gate enforced.
    """

    def test_reviewer_none_allows_stop_without_triage(
            self, tmp_path, monkeypatch, capsys):
        state = _base_state()
        state["pr"]["reviewer"] = "none"
        code, out = _run_hook(tmp_path, state, monkeypatch, capsys)
        assert code == 0
        assert "Reviewer: none" in out

    def test_reviewer_gemini_blocks_without_triage(
            self, tmp_path, monkeypatch, capsys):
        state = _base_state()
        state["pr"]["reviewer"] = "gemini"
        code, out = _run_hook(tmp_path, state, monkeypatch, capsys)
        assert code == 2
        assert "triage" in out.lower()

    def test_reviewer_absent_defaults_to_gemini_gate(
            self, tmp_path, monkeypatch, capsys):
        # No reviewer key at all — legacy state written before reviewer modes.
        state = _base_state()
        code, out = _run_hook(tmp_path, state, monkeypatch, capsys)
        assert code == 2
        assert "triage" in out.lower()

    def test_reviewer_gemini_triaged_allows_stop(
            self, tmp_path, monkeypatch, capsys):
        state = _base_state()
        state["pr"]["reviewer"] = "gemini"
        state["pr"]["gemini_triaged"] = True
        code, out = _run_hook(tmp_path, state, monkeypatch, capsys)
        assert code == 0
        assert "Gemini review triaged" in out
