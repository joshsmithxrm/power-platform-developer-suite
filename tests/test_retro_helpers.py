#!/usr/bin/env python3
"""Tests for retro_helpers.py (RF AC-10–13)."""
import json
import os
import sys
import tempfile

import pytest

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))


class TestExtractTranscriptSignals:
    def test_extract_transcript_signals(self):
        """RF AC-10: Returns structured signals from JSONL."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "session.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"human","message":{"content":"no, that is wrong"}}\n')
                f.write('{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet build"}}]}}\n')

            signals = retro_helpers.extract_transcript_signals(jsonl_path)
            assert "user_corrections" in signals
            assert "tool_failures" in signals
            assert "repeated_commands" in signals
            # Verify content is actually extracted, not just empty containers
            assert len(signals["user_corrections"]) >= 1, "Should detect 'no, that is wrong' as correction"


class TestUserCorrectionDetection:
    def test_user_correction_detection(self):
        """RF AC-11: Detects user correction patterns."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "session.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"human","message":{"content":"no, try again"}}\n')
                f.write('{"type":"human","message":{"content":"that is wrong"}}\n')
                f.write('{"type":"human","message":{"content":"great work!"}}\n')

            signals = retro_helpers.extract_transcript_signals(jsonl_path)
            assert len(signals["user_corrections"]) >= 2


class TestToolFailureDetection:
    def test_tool_failure_detection(self):
        """RF AC-12: Detects tool failures (non-zero exit, file not found, old_string not found)."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "session.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"tool_result","content":[{"type":"tool_result","content":"Exit code: 1\\nError: build failed"}]}\n')
                f.write('{"type":"tool_result","content":[{"type":"tool_result","content":"file not found: foo.txt"}]}\n')

            signals = retro_helpers.extract_transcript_signals(jsonl_path)
            assert len(signals["tool_failures"]) >= 2


class TestEnforcementSignalExtraction:
    def test_enforcement_signal_extraction(self):
        """RF AC-13: Reads stop_hook_count from state."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            state_path = os.path.join(tmpdir, "state.json")
            with open(state_path, "w") as f:
                json.dump({
                    "stop_hook_blocked": True,
                    "stop_hook_count": 5,
                    "stop_hook_last": "2026-03-27T10:00:00Z",
                }, f)

            signals = retro_helpers.extract_enforcement_signals(state_path)
            assert signals["stop_hook_count"] == 5
            assert signals["stop_hook_blocked"] is True
            assert signals["stop_hook_last"] == "2026-03-27T10:00:00Z"

    def test_missing_state_returns_defaults(self):
        """RF AC-13: Missing state file returns safe defaults."""
        import retro_helpers
        signals = retro_helpers.extract_enforcement_signals("/nonexistent/state.json")
        assert signals["stop_hook_count"] == 0
        assert signals["stop_hook_blocked"] is False


class TestDiscoverTranscripts:
    def test_discover_transcripts(self):
        """discover_transcripts finds JSONL files in .workflow/stages."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            stages_dir = os.path.join(tmpdir, ".workflow", "stages")
            os.makedirs(stages_dir)
            with open(os.path.join(stages_dir, "implement.jsonl"), "w") as f:
                f.write('{"type":"test"}\n')

            transcripts = retro_helpers.discover_transcripts(tmpdir)
            assert any("implement.jsonl" in t for t in transcripts)

    def test_discover_transcripts_filters_by_worktree(self, monkeypatch):
        """discover_transcripts only returns transcripts for the given worktree."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as fake_home:
            claude_projects = os.path.join(fake_home, ".claude", "projects")
            # Simulate two project dirs: one matching, one not
            worktree = os.path.join(fake_home, "my", "project")
            os.makedirs(worktree)
            encoded = retro_helpers._encode_project_dir(worktree)
            matching_dir = os.path.join(claude_projects, encoded)
            other_dir = os.path.join(claude_projects, "other-project")
            os.makedirs(matching_dir)
            os.makedirs(other_dir)
            with open(os.path.join(matching_dir, "mine.jsonl"), "w") as f:
                f.write("{}\n")
            with open(os.path.join(other_dir, "theirs.jsonl"), "w") as f:
                f.write("{}\n")

            monkeypatch.setattr(
                os.path, "expanduser",
                lambda p: p.replace("~", fake_home),
            )
            transcripts = retro_helpers.discover_transcripts(worktree)
            assert any("mine.jsonl" in t for t in transcripts)
            assert not any("theirs.jsonl" in t for t in transcripts)

    def test_encode_project_dir_uses_dispatch_slug_rule(self):
        """_encode_project_dir matches claude_dispatch.derive_slug — every
        non-[A-Za-z0-9-] character (including underscores and dots) becomes -."""
        import retro_helpers
        from claude_dispatch import derive_slug

        # Real Windows-shaped path with an underscore in the username and a
        # dot in `.worktrees` — both are dropped by the old regex but caught
        # by the dispatcher's rule.
        path = r"C:\Users\josh_\source\repos\ppdsw\ppds\.worktrees\workflow-gates"
        encoded = retro_helpers._encode_project_dir(path)
        # Underscore replaced
        assert "_" not in encoded
        # Matches the dispatcher's derivation (derive_slug normalizes internally)
        assert encoded == derive_slug(path)

    def test_discover_transcripts_since_filter(self, monkeypatch):
        """discover_transcripts skips transcripts older than ``since`` mtime."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as fake_home:
            claude_projects = os.path.join(fake_home, ".claude", "projects")
            worktree = os.path.join(fake_home, "my", "project")
            os.makedirs(worktree)
            encoded = retro_helpers._encode_project_dir(worktree)
            project_dir = os.path.join(claude_projects, encoded)
            os.makedirs(project_dir)

            old = os.path.join(project_dir, "old.jsonl")
            new = os.path.join(project_dir, "new.jsonl")
            with open(old, "w") as f:
                f.write("{}\n")
            with open(new, "w") as f:
                f.write("{}\n")
            os.utime(old, (1_000_000, 1_000_000))
            os.utime(new, (2_000_000, 2_000_000))

            monkeypatch.setattr(
                os.path, "expanduser",
                lambda p: p.replace("~", fake_home),
            )
            transcripts = retro_helpers.discover_transcripts(worktree, since=1_500_000)
            assert any("new.jsonl" in t for t in transcripts)
            assert not any("old.jsonl" in t for t in transcripts)


class TestDiscoverTranscriptsDesktop:
    def test_discover_transcripts_desktop_manifest(self, monkeypatch, tmp_path):
        """Desktop local_*.json manifest -> CLI transcript lookup."""
        import retro_helpers

        fake_home = str(tmp_path)
        worktree = os.path.join(fake_home, "my", "project")
        os.makedirs(worktree)

        cli_session_id = "aaaabbbb-cccc-dddd-eeee-ffffffffffff"
        encoded = retro_helpers._encode_project_dir(worktree)
        cli_dir = os.path.join(fake_home, ".claude", "projects", encoded)
        os.makedirs(cli_dir)
        cli_transcript = os.path.join(cli_dir, cli_session_id + ".jsonl")
        with open(cli_transcript, "w") as f:
            f.write('{"type": "user"}\n')

        appdata = os.path.join(fake_home, "AppData", "Roaming")
        ccd_dir = os.path.join(appdata, "Claude", "claude-code-sessions", "uid1", "mid1")
        os.makedirs(ccd_dir)
        manifest = {
            "sessionId": "local_abc",
            "cliSessionId": cli_session_id,
            "cwd": worktree,
            "worktreePath": worktree,
        }
        with open(os.path.join(ccd_dir, "local_abc.json"), "w") as f:
            json.dump(manifest, f)

        monkeypatch.setenv("APPDATA", appdata)
        monkeypatch.setattr(os.path, "expanduser", lambda p: p.replace("~", fake_home))

        transcripts = retro_helpers.discover_transcripts(worktree)
        assert any(cli_session_id in t for t in transcripts), (
            f"Desktop manifest transcript not found; got {transcripts!r}"
        )

    def test_discover_transcripts_desktop_mtime_filter(self, monkeypatch, tmp_path):
        """mtime filter excludes Desktop-manifest transcripts older than since."""
        import retro_helpers

        fake_home = str(tmp_path)
        worktree = os.path.join(fake_home, "my", "project")
        os.makedirs(worktree)

        cli_session_id = "bbbbcccc-dddd-eeee-ffff-aaaaaaaaaaaa"
        encoded = retro_helpers._encode_project_dir(worktree)
        cli_dir = os.path.join(fake_home, ".claude", "projects", encoded)
        os.makedirs(cli_dir)
        cli_transcript = os.path.join(cli_dir, cli_session_id + ".jsonl")
        with open(cli_transcript, "w") as f:
            f.write('{"type": "user"}\n')
        os.utime(cli_transcript, (1_000_000, 1_000_000))

        appdata = os.path.join(fake_home, "AppData", "Roaming")
        ccd_dir = os.path.join(appdata, "Claude", "claude-code-sessions", "uid1", "mid1")
        os.makedirs(ccd_dir)
        manifest = {
            "sessionId": "local_old",
            "cliSessionId": cli_session_id,
            "cwd": worktree,
            "worktreePath": worktree,
        }
        with open(os.path.join(ccd_dir, "local_old.json"), "w") as f:
            json.dump(manifest, f)

        monkeypatch.setenv("APPDATA", appdata)
        monkeypatch.setattr(os.path, "expanduser", lambda p: p.replace("~", fake_home))

        transcripts = retro_helpers.discover_transcripts(worktree, since=1_500_000)
        assert not any(cli_session_id in t for t in transcripts), (
            "Old Desktop transcript should be excluded by mtime filter"
        )

    def test_discover_transcripts_desktop_no_duplicate(self, monkeypatch, tmp_path):
        """Desktop manifest transcript already found by CLI scan is not duplicated."""
        import retro_helpers

        fake_home = str(tmp_path)
        worktree = os.path.join(fake_home, "my", "project")
        os.makedirs(worktree)

        cli_session_id = "ccccdddd-eeee-ffff-aaaa-bbbbbbbbbbbb"
        encoded = retro_helpers._encode_project_dir(worktree)
        cli_dir = os.path.join(fake_home, ".claude", "projects", encoded)
        os.makedirs(cli_dir)
        with open(os.path.join(cli_dir, cli_session_id + ".jsonl"), "w") as f:
            f.write('{"type": "user"}\n')

        appdata = os.path.join(fake_home, "AppData", "Roaming")
        ccd_dir = os.path.join(appdata, "Claude", "claude-code-sessions", "uid1", "mid1")
        os.makedirs(ccd_dir)
        manifest = {
            "sessionId": "local_dup",
            "cliSessionId": cli_session_id,
            "cwd": worktree,
            "worktreePath": worktree,
        }
        with open(os.path.join(ccd_dir, "local_dup.json"), "w") as f:
            json.dump(manifest, f)

        monkeypatch.setenv("APPDATA", appdata)
        monkeypatch.setattr(os.path, "expanduser", lambda p: p.replace("~", fake_home))

        transcripts = retro_helpers.discover_transcripts(worktree)
        matching = [t for t in transcripts if cli_session_id in t]
        assert len(matching) == 1, f"Expected exactly 1 match, got {matching!r}"


class TestAllowlistDriftDetector:
    def _init_repo(self, tmpdir):
        import subprocess
        env = {
            **os.environ,
            "GIT_AUTHOR_NAME": "Test",
            "GIT_AUTHOR_EMAIL": "test@example.com",
            "GIT_COMMITTER_NAME": "Test",
            "GIT_COMMITTER_EMAIL": "test@example.com",
        }
        for cmd in (
            ["git", "init", "-q", "-b", "main"],
            ["git", "commit", "-q", "--allow-empty", "-m", "init"],
        ):
            subprocess.run(cmd, cwd=tmpdir, check=True, env=env)
        return env

    def _commit(self, tmpdir, env, subject, files):
        import subprocess
        for rel, content in files.items():
            full = os.path.join(tmpdir, rel)
            os.makedirs(os.path.dirname(full), exist_ok=True)
            with open(full, "w", encoding="utf-8") as f:
                f.write(content)
            subprocess.run(["git", "add", rel], cwd=tmpdir, check=True, env=env)
        subprocess.run(["git", "commit", "-q", "-m", subject], cwd=tmpdir, check=True, env=env)

    def test_flags_post_verify_fix_on_subprocess_file(self):
        """A fix-after-/verify commit touching a subprocess-spawning file
        outside the allowlist produces a concrete proposal."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            env = self._init_repo(tmpdir)
            # Pre-verify work (ignored)
            self._commit(tmpdir, env, "feat: introduce foo",
                         {"scripts/foo.py": "import subprocess\n"})
            # The /verify marker
            self._commit(tmpdir, env, "verify: workflow ok",
                         {"docs/notes.md": "x\n"})
            # Two post-verify fixes touching scripts/foo.py — must be flagged
            self._commit(tmpdir, env, "fix: foo crash on Windows",
                         {"scripts/foo.py": "import subprocess  # patched\n"})
            self._commit(tmpdir, env, "fix(foo): error path",
                         {"scripts/foo.py": "import subprocess  # patched2\n"})
            # Post-verify fix touching an allowlisted file — must NOT be flagged
            self._commit(tmpdir, env, "fix(dispatch): edge case",
                         {"scripts/claude_dispatch.py": "import subprocess\n"})
            # Post-verify fix touching a non-subprocess file — must NOT be flagged
            self._commit(tmpdir, env, "fix: doc typo",
                         {"docs/notes.md": "y\n"})

            proposals = retro_helpers.detect_allowlist_drift(cwd=tmpdir)
            flagged = [p["file"] for p in proposals]
            assert "scripts/foo.py" in flagged
            assert "scripts/claude_dispatch.py" not in flagged
            assert "docs/notes.md" not in flagged
            foo = next(p for p in proposals if p["file"] == "scripts/foo.py")
            assert foo["fix_count"] == 2
            assert "shakedown allowlist" in foo["proposal"]

    def test_feat_referencing_verify_is_not_a_marker(self):
        """Subjects that *reference* /verify but aren't a verify run (e.g.
        ``feat(verify): ...``) must not be picked as the marker — the
        detector should anchor on subject-prefix patterns only."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            env = self._init_repo(tmpdir)
            # The real /verify marker
            self._commit(tmpdir, env, "verify: workflow ok",
                         {"docs/notes.md": "x\n"})
            # A later commit whose subject merely contains the word
            # "verify" — must NOT be treated as a new marker.
            self._commit(tmpdir, env, "feat(verify): tighten marker",
                         {"docs/notes.md": "y\n"})
            # A post-verify fix on a subprocess-spawning file outside the
            # allowlist — must still be flagged because the real marker is
            # the first commit, not the feat() above.
            self._commit(tmpdir, env, "fix: foo crash",
                         {"scripts/foo.py": "import subprocess\n"})

            proposals = retro_helpers.detect_allowlist_drift(cwd=tmpdir)
            flagged = [p["file"] for p in proposals]
            assert "scripts/foo.py" in flagged

    def test_no_verify_marker_returns_empty(self):
        """Detector is a no-op when no /verify commit appears in history."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            env = self._init_repo(tmpdir)
            self._commit(tmpdir, env, "fix: thing",
                         {"scripts/foo.py": "import subprocess\n"})
            assert retro_helpers.detect_allowlist_drift(cwd=tmpdir) == []

    def test_write_drift_proposals_merges_existing_findings(self):
        """write_drift_proposals preserves prior keys in retro-findings.json."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            findings = os.path.join(tmpdir, ".workflow", "retro-findings.json")
            os.makedirs(os.path.dirname(findings))
            with open(findings, "w", encoding="utf-8") as f:
                json.dump({"prior": [1, 2, 3]}, f)
            proposals = [
                {"file": "scripts/bar.py", "fix_count": 1, "commits": ["abc"],
                 "proposal": "Add scripts/bar.py to shakedown allowlist (1 post-/verify fix this PR)"},
            ]
            retro_helpers.write_drift_proposals(findings, proposals)
            with open(findings, "r", encoding="utf-8") as f:
                data = json.load(f)
            assert data["prior"] == [1, 2, 3]
            assert data["allowlist_drift"] == proposals

    def test_write_drift_proposals_skips_empty(self):
        """No file write when proposals list is empty."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            findings = os.path.join(tmpdir, ".workflow", "retro-findings.json")
            retro_helpers.write_drift_proposals(findings, [])
            assert not os.path.exists(findings)

    def test_unicode_path_in_post_verify_fix_is_flagged(self):
        """Paths with unicode characters arrive raw (no octal escapes) so
        the detector matches them against the allowlist correctly. Regression
        for Gemini PR #1073: ``.strip('"')`` alone could not handle git's
        C-style escaping when ``core.quotePath`` was on; the fix is to pass
        ``-c core.quotePath=off`` to git."""
        import retro_helpers
        with tempfile.TemporaryDirectory() as tmpdir:
            env = self._init_repo(tmpdir)
            self._commit(tmpdir, env, "verify: workflow ok",
                         {"docs/notes.md": "x\n"})
            # Path with a non-ASCII character — git would normally emit
            # this as ``"scripts/\303\251.py"`` under default core.quotePath.
            self._commit(tmpdir, env, "fix: unicode path",
                         {"scripts/é.py": "import subprocess\n"})
            proposals = retro_helpers.detect_allowlist_drift(cwd=tmpdir)
            flagged = [p["file"] for p in proposals]
            assert "scripts/é.py" in flagged, (
                f"unicode path missed; got: {flagged!r}"
            )
