"""Regression tests for /start skill fixes — #799 (stale base + stranded
index), Bug 3 (terminal launch regression), Bug 4 (context.md handoff).

These tests exercise the Python helpers the skill calls so the
skill's behavior is validated at the code level, not just the prose
level. The v1 dispatch AI deviated from the prose before; the helpers
make deviation impossible.
"""
from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
from pathlib import Path

import pytest


REPO_ROOT = Path(__file__).resolve().parent.parent


def _run(*args, **kwargs):
    """subprocess.run wrapper that always closes stdin.

    Python 3.14 on Windows raises `OSError: [WinError 6]` from
    `_make_inheritable` when pytest's captured stdin is inherited into
    a subprocess. Passing `stdin=DEVNULL` avoids the handle dance.
    """
    kwargs.setdefault("stdin", subprocess.DEVNULL)
    return subprocess.run(*args, **kwargs)


def _load_module(name: str, relpath: str):
    path = REPO_ROOT / relpath
    spec = importlib.util.spec_from_file_location(name, str(path))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


worktree_create = _load_module(
    "worktree_create", "scripts/worktree-create.py"
)
launch_session = _load_module(
    "launch_claude_session", "scripts/launch-claude-session.py"
)


# ---------------------------------------------------------------------------
# worktree-create.py — #799 stale base + stranded index
# ---------------------------------------------------------------------------


class TestParseRegisteredWorktrees:
    def test_empty_porcelain_yields_empty(self):
        assert worktree_create.parse_registered_worktrees("") == []

    def test_single_worktree_parsed(self):
        porcelain = (
            "worktree C:/repo\nHEAD abc\nbranch refs/heads/main\n"
        )
        paths = worktree_create.parse_registered_worktrees(porcelain)
        assert paths == ["C:/repo"]

    def test_multiple_worktrees_parsed(self):
        porcelain = (
            "worktree C:/repo\nHEAD abc\nbranch refs/heads/main\n\n"
            "worktree C:/repo/.worktrees/feat-a\nHEAD def\nbranch refs/heads/feat/a\n\n"
            "worktree C:/repo/.worktrees/feat-b\nHEAD ghi\nbranch refs/heads/feat/b\n"
        )
        paths = worktree_create.parse_registered_worktrees(porcelain)
        assert paths == [
            "C:/repo",
            "C:/repo/.worktrees/feat-a",
            "C:/repo/.worktrees/feat-b",
        ]

    def test_backslash_normalized_to_forward_slash(self):
        porcelain = "worktree C:\\repo\\.worktrees\\x\n"
        paths = worktree_create.parse_registered_worktrees(porcelain)
        assert paths == ["C:/repo/.worktrees/x"]


class TestDetectStranded:
    def test_nonexistent_path_is_not_stranded(self, tmp_path):
        target = tmp_path / "missing"
        assert not worktree_create.detect_stranded(str(target), [])

    def test_registered_path_is_not_stranded(self, tmp_path):
        target = tmp_path / ".worktrees" / "foo"
        target.mkdir(parents=True)
        registered = [str(target).replace("\\", "/")]
        assert not worktree_create.detect_stranded(str(target), registered)

    def test_unregistered_existing_path_is_stranded(self, tmp_path):
        """The #799 failure mode: directory on disk but not a worktree."""
        target = tmp_path / ".worktrees" / "stranded"
        target.mkdir(parents=True)
        # Leave a file to simulate the stranded-index residue
        (target / "bogus.txt").write_text("stranded")
        assert worktree_create.detect_stranded(str(target), [])

    def test_trailing_slash_normalized(self, tmp_path):
        target = tmp_path / ".worktrees" / "foo"
        target.mkdir(parents=True)
        registered = [str(target).replace("\\", "/") + "/"]
        # With or without trailing slash, registered means NOT stranded
        assert not worktree_create.detect_stranded(str(target), registered)


@pytest.mark.skipif(
    sys.platform == "win32" and sys.version_info >= (3, 14),
    reason=(
        "Python 3.14 on Windows raises OSError(WinError 50) from "
        "subprocess.run(..., capture_output=True) under pytest stdout "
        "capture — a CPython/pytest interaction, not a code bug. "
        "The TestParseRegisteredWorktrees + TestDetectStranded unit "
        "tests cover the same logic without subprocess."
    ),
)
class TestCreateEndToEnd:
    """Create a real throwaway repo and exercise the full path.

    This proves #799 fix: worktree is based on origin/main after a
    fetch, and a stranded directory is refused with exit code 1.
    """

    @staticmethod
    def _init_bare_origin_with_main(origin_dir: Path) -> str:
        _run(
            ["git", "init", "--bare", "-b", "main", str(origin_dir)],
            check=True,
            capture_output=True,
            stdin=subprocess.DEVNULL,
        )
        return str(origin_dir)

    @staticmethod
    def _init_clone_with_commit(clone_dir: Path, origin_url: str) -> str:
        _run(
            ["git", "clone", origin_url, str(clone_dir)],
            check=True,
            capture_output=True,
        )
        _run(
            ["git", "config", "user.email", "t@t"], cwd=clone_dir, check=True
        )
        _run(
            ["git", "config", "user.name", "t"], cwd=clone_dir, check=True
        )
        (clone_dir / "README.md").write_text("# test\n")
        _run(["git", "add", "README.md"], cwd=clone_dir, check=True)
        _run(
            ["git", "commit", "-m", "initial"],
            cwd=clone_dir,
            check=True,
            capture_output=True,
        )
        _run(
            ["git", "push", "origin", "main"],
            cwd=clone_dir,
            check=True,
            capture_output=True,
        )
        return _run(
            ["git", "rev-parse", "HEAD"],
            cwd=clone_dir,
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()

    def test_creates_worktree_based_on_origin_main(self, tmp_path):
        origin = tmp_path / "origin.git"
        clone = tmp_path / "clone"
        origin_url = self._init_bare_origin_with_main(origin)
        origin_sha = self._init_clone_with_commit(clone, origin_url)

        code, msg = worktree_create.create(str(clone), "my-feat")
        assert code == 0, msg

        wt_sha = _run(
            ["git", "rev-parse", "HEAD"],
            cwd=clone / ".worktrees" / "my-feat",
            capture_output=True,
            text=True,
            check=True,
        ).stdout.strip()
        assert wt_sha == origin_sha

        status = _run(
            ["git", "status", "--porcelain"],
            cwd=clone / ".worktrees" / "my-feat",
            capture_output=True,
            text=True,
            check=True,
        ).stdout.strip()
        assert status == "", "new worktree must have clean index (#799)"

    def test_refuses_stranded_directory(self, tmp_path):
        origin = tmp_path / "origin.git"
        clone = tmp_path / "clone"
        origin_url = self._init_bare_origin_with_main(origin)
        self._init_clone_with_commit(clone, origin_url)

        # Create a stranded directory where the new worktree wants to go
        stranded = clone / ".worktrees" / "my-feat"
        stranded.mkdir(parents=True)
        (stranded / "leftover.txt").write_text("stranded residue")

        code, msg = worktree_create.create(str(clone), "my-feat")
        assert code == 1
        assert "STRANDED" in msg
        # Directory must not be touched — user's work could be in there
        assert (stranded / "leftover.txt").exists()

    def test_worktree_based_on_origin_not_local_main(self, tmp_path):
        """When local `main` is behind origin/main, the new worktree
        must still be based on origin/main (the #799 pathology)."""
        origin = tmp_path / "origin.git"
        clone_a = tmp_path / "clone-a"  # will push a new commit
        clone_b = tmp_path / "clone-b"  # has stale local main

        origin_url = self._init_bare_origin_with_main(origin)
        self._init_clone_with_commit(clone_a, origin_url)

        # clone_b is the one we'll create the worktree in. Clone it BEFORE
        # clone_a advances origin/main so that clone_b's local main is stale.
        _run(
            ["git", "clone", origin_url, str(clone_b)],
            check=True,
            capture_output=True,
        )
        _run(
            ["git", "config", "user.email", "t@t"], cwd=clone_b, check=True
        )
        _run(["git", "config", "user.name", "t"], cwd=clone_b, check=True)

        # Advance origin/main from clone_a
        (clone_a / "advance.txt").write_text("new commit\n")
        _run(["git", "add", "advance.txt"], cwd=clone_a, check=True)
        _run(
            ["git", "commit", "-m", "advance"],
            cwd=clone_a,
            check=True,
            capture_output=True,
        )
        _run(
            ["git", "push", "origin", "main"],
            cwd=clone_a,
            check=True,
            capture_output=True,
        )
        new_origin_sha = _run(
            ["git", "rev-parse", "HEAD"],
            cwd=clone_a,
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()

        # clone_b's local `main` still points at the old commit. The
        # helper must fetch and base the new worktree on origin/main,
        # NOT on local main.
        code, msg = worktree_create.create(str(clone_b), "stale-test")
        assert code == 0, msg

        wt_sha = _run(
            ["git", "rev-parse", "HEAD"],
            cwd=clone_b / ".worktrees" / "stale-test",
            capture_output=True,
            text=True,
            check=True,
        ).stdout.strip()
        assert wt_sha == new_origin_sha, (
            f"new worktree must be based on origin/main ({new_origin_sha[:8]}), "
            f"not stale local main — got {wt_sha[:8]} (#799 pathology)"
        )


# ---------------------------------------------------------------------------
# launch-claude-session.py — bug 3 (TTY-interactive spawn) and bug 4
# (no context.md)
# ---------------------------------------------------------------------------


class TestBuildLaunchScript:
    def test_script_contains_set_location(self):
        script = launch_session.build_launch_script(
            "C:\\worktree", "C:\\claude.exe", "hello"
        )
        assert 'Set-Location -Path "C:\\worktree"' in script

    def test_script_uses_here_string(self):
        """@' ... '@ is the only reliable way to embed a multi-line
        prompt without shell-escaping madness."""
        script = launch_session.build_launch_script(
            "C:\\w", "C:\\claude.exe", "line1\nline2"
        )
        assert "$prompt = @'" in script
        assert "\n'@\n" in script

    def test_script_invokes_claude_as_positional_arg(self):
        """Bug 3: `claude -p $prompt` is non-interactive. The bare
        positional keeps the session interactive."""
        script = launch_session.build_launch_script(
            "C:\\w", "C:\\claude.exe", "hi"
        )
        assert "& \"C:\\claude.exe\" $prompt" in script
        assert "-p $prompt" not in script
        assert "--prompt" not in script

    def test_prompt_with_apostrophes_is_accepted(self):
        """Apostrophes mid-line are fine in PowerShell here-strings."""
        prompt = "it's fine. don't panic."
        script = launch_session.build_launch_script(
            "C:\\w", "C:\\claude.exe", prompt
        )
        assert prompt in script

    def test_prompt_line_starting_with_at_quote_is_rejected(self):
        """A line starting with `'@` terminates the here-string."""
        bad = "valid line\n'@ this kills the here-string"
        with pytest.raises(ValueError, match="would terminate"):
            launch_session.build_launch_script(
                "C:\\w", "C:\\claude.exe", bad
            )


class TestBuildSpawnCommand:
    def test_uses_start_process_with_file(self):
        """Bug 3: `-Command` robs claude of a TTY. Must be `-File`."""
        cmd = launch_session.build_spawn_command("C:\\script.ps1")
        assert cmd[0] == "pwsh"
        assert cmd[1] == "-Command"
        inner = cmd[2]
        assert "Start-Process pwsh" in inner
        assert "-NoExit" in inner
        assert "-File" in inner
        assert "'C:\\script.ps1'" in inner

    def test_does_not_use_inner_command_flag(self):
        """Explicitly guard against the v1 regression: the INNER
        pwsh must use -File, not -Command."""
        cmd = launch_session.build_spawn_command("C:\\s.ps1")
        # The outer pwsh uses -Command (that's fine, it just invokes
        # Start-Process). But the ArgumentList for the INNER pwsh must
        # contain -File, not -Command.
        inner = cmd[2]
        # Find the ArgumentList
        assert "'-NoExit','-File'," in inner
        # Defensively assert the broken v1 pattern is absent for the
        # INNER pwsh. (The OUTER pwsh does use -Command to invoke
        # Start-Process, which is fine — that's cmd[1], not inner.)
        assert "'-Command'" not in inner


class TestPowerShellSingleQuoteEscaping:
    """Gemini PR #830 review (medium): paths with embedded apostrophes
    (e.g. `C:\\Users\\O'Brien\\repo` or worktree names like
    `it's-a-fix`) must be escaped per PowerShell single-quoted literal
    rules — one `'` becomes `''`. Otherwise the literal terminates
    early and the spawn command (or copy-pasted fallback snippet)
    silently corrupts. Fixed in commit f1bef48.
    """

    def test_powershell_path_with_apostrophe_escaped(self):
        """build_spawn_command must double single quotes in the
        script path before interpolating into the inner PowerShell
        single-quoted literal."""
        script_path = "C:\\Users\\O'Brien\\launch.ps1"
        cmd = launch_session.build_spawn_command(script_path)
        inner = cmd[2]
        # Doubled apostrophe must be present (escaped form).
        assert "O''Brien" in inner
        # The escaped path must be properly wrapped in PS single quotes.
        assert "'C:\\Users\\O''Brien\\launch.ps1'" in inner

    def test_powershell_path_without_apostrophe_unchanged(self):
        """Regression: the escape must not corrupt normal paths."""
        cmd = launch_session.build_spawn_command("C:\\plain\\launch.ps1")
        inner = cmd[2]
        assert "'-NoExit','-File','C:\\plain\\launch.ps1'" in inner

    def test_manual_fallback_path_with_apostrophe_escaped(self, capsys):
        """_print_manual_fallback writes copy-pasteable PowerShell to
        stderr; both target and claude paths are interpolated into
        single-quoted literals and must be escaped."""
        launch_session._print_manual_fallback(
            target_win="C:\\Users\\O'Brien\\worktree",
            prompt="hello",
            claude_win="C:\\Users\\O'Brien\\bin\\claude.exe",
        )
        err = capsys.readouterr().err
        # Both apostrophes must be doubled in the rendered snippet.
        assert "cd 'C:\\Users\\O''Brien\\worktree'" in err
        assert "& 'C:\\Users\\O''Brien\\bin\\claude.exe' $prompt" in err

    def test_manual_fallback_path_without_apostrophe_unchanged(self, capsys):
        """Regression: the escape must not corrupt normal paths in
        the fallback snippet."""
        launch_session._print_manual_fallback(
            target_win="C:\\plain\\worktree",
            prompt="hello",
            claude_win="C:\\plain\\claude.exe",
        )
        err = capsys.readouterr().err
        assert "cd 'C:\\plain\\worktree'" in err
        assert "& 'C:\\plain\\claude.exe' $prompt" in err


class TestLaunchDryRun:
    """End-to-end dry-run: writes the script, prints the spawn
    command, does NOT execute. Validates the file contents.
    """

    def test_dry_run_writes_script_and_does_not_execute(self, tmp_path, capsys):
        prompt = "Task: do the thing\n\nFirst action: say hello"
        code = launch_session.launch(
            target="C:\\some\\worktree",
            name="testfeat",
            prompt=prompt,
            claude_path="C:\\claude.exe",
            launch_dir=str(tmp_path),
            dry_run=True,
        )
        assert code == 0
        script_path = tmp_path / "launch-testfeat.ps1"
        assert script_path.exists()
        content = script_path.read_text(encoding="utf-8")
        assert "Task: do the thing" in content
        assert "$prompt = @'" in content
        assert "& \"C:\\claude.exe\" $prompt" in content

        out = capsys.readouterr().out
        assert "spawn:" in out
        assert "-File" in out

    def test_empty_prompt_refused(self, tmp_path):
        code = launch_session.launch(
            target="C:\\w",
            name="x",
            prompt="",
            claude_path="C:\\claude.exe",
            launch_dir=str(tmp_path),
            dry_run=True,
        )
        assert code == 1


class TestContextMdNotWritten:
    """Bug 4: the helper must NEVER write `.plans/context.md` or any
    other handoff file. Inline-prompt is the only mechanism."""

    def test_launch_does_not_create_plans_context_md(self, tmp_path):
        # Treat tmp_path as a fake worktree root
        (tmp_path / ".plans").mkdir()
        launch_session.launch(
            target=str(tmp_path),
            name="nofile",
            prompt="hi",
            claude_path="C:\\claude.exe",
            launch_dir=str(tmp_path / "launch"),
            dry_run=True,
        )
        # Bug 4: must NOT have created .plans/context.md
        assert not (tmp_path / ".plans" / "context.md").exists()


# ---------------------------------------------------------------------------
# /design skill — #800 premature phase flip
# ---------------------------------------------------------------------------


class TestDesignSkillDoesNotFlipPhase:
    """#800: /design Step 6 must NOT run `workflow-state.py set phase
    implementing`. That transition belongs to /implement.
    """

    def test_design_skill_has_no_phase_implementing_flip(self):
        skill_path = REPO_ROOT / ".claude" / "skills" / "design" / "SKILL.md"
        text = skill_path.read_text(encoding="utf-8")
        # The forbidden instruction is the literal command the old
        # Step 6 ran. Grep for it.
        forbidden = "workflow-state.py set phase implementing"
        # Allow the string to appear inside a historical-note or
        # "do not do this" callout, but NOT as an active instruction
        # in a ```bash ... ``` code block. Simplest enforceable check:
        # ensure the exact command is not inside a bash fence.
        import re

        code_blocks = re.findall(r"```bash\n(.*?)\n```", text, re.DOTALL)
        offenders = [b for b in code_blocks if forbidden in b]
        assert not offenders, (
            f"/design SKILL.md contains an active bash fence that runs "
            f"`{forbidden}` — this is the #800 premature-flip bug. Found in:\n"
            + "\n---\n".join(offenders)
        )

    def test_design_skill_documents_the_deferred_flip(self):
        """Regression guard: the skill should explicitly say which
        downstream skill owns the phase transition."""
        skill_path = REPO_ROOT / ".claude" / "skills" / "design" / "SKILL.md"
        text = skill_path.read_text(encoding="utf-8")
        assert "/implement" in text and "phase" in text, (
            "/design must document that /implement owns phase=implementing"
        )
