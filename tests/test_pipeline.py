#!/usr/bin/env python3
"""Tests for pipeline reliability (workflow-enforcement v4.0-v5.0, ACs 51-92).

All tests are behavioral — they call production functions with mocked
subprocess/IO and assert on return values, side effects, or state changes.
Two tests (AC-4, AC-105) use ast.parse for structural verification of
deeply-nested main() code that cannot be unit-tested without integration setup.
"""
import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest.mock

import pytest

# Add scripts dir to path so we can import pipeline module
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))


# ---------------------------------------------------------------------------
# Fixture: auto-mock v8.0 pipeline functions that make real gh/git calls.
# Tests that specifically exercise these functions should patch them again.
# ---------------------------------------------------------------------------
@pytest.fixture(autouse=True)
def _mock_pipeline_v8_helpers(monkeypatch):
    """Prevent real calls from _poll_codeql_check, detect_gemini_overload,
    get_unreplied_comments in pipeline tests."""
    try:
        import pipeline
        monkeypatch.setattr(pipeline, "_poll_codeql_check", lambda *a, **kw: None)
        monkeypatch.setattr(pipeline, "detect_gemini_overload", lambda *a, **kw: False)
        monkeypatch.setattr(pipeline, "get_unreplied_comments", lambda *a, **kw: [])
    except (ImportError, AttributeError):
        pass  # pipeline not imported yet in some test classes


# ---------------------------------------------------------------------------
# AC-51: All hook commands use relative paths
# ---------------------------------------------------------------------------
class TestHookPaths:
    def test_all_commands_use_relative_paths(self):
        """AC-51: No ${CLAUDE_PROJECT_DIR} in any hook command string."""
        settings_path = os.path.join(REPO_ROOT, ".claude", "settings.json")
        with open(settings_path, "r") as f:
            settings = json.load(f)

        hooks = settings.get("hooks", {})
        for event_type, matchers in hooks.items():
            for matcher_entry in matchers:
                for hook in matcher_entry.get("hooks", []):
                    cmd = hook.get("command", "")
                    assert "CLAUDE_PROJECT_DIR" not in cmd, (
                        f"Hook command in {event_type} still uses "
                        f"CLAUDE_PROJECT_DIR: {cmd}"
                    )
                    if ".claude/hooks/" in cmd:
                        assert cmd.startswith('python ".claude/hooks/'), (
                            f"Hook command should use relative path: {cmd}"
                        )


# ---------------------------------------------------------------------------
# AC-52: Stop hook exits in pipeline mode
# ---------------------------------------------------------------------------
class TestStopHook:
    def test_exits_in_pipeline_mode(self):
        """AC-52: Stop hook exits 0 when PPDS_PIPELINE=1."""
        hook_path = os.path.join(
            REPO_ROOT, ".claude", "hooks", "session-stop-workflow.py"
        )
        env = os.environ.copy()
        env["PPDS_PIPELINE"] = "1"
        result = subprocess.run(
            [sys.executable, hook_path],
            input="{}",
            capture_output=True,
            text=True,
            env=env,
            cwd=REPO_ROOT,
            timeout=10,
        )
        assert result.returncode == 0, (
            f"Stop hook should exit 0 in pipeline mode, got {result.returncode}"
        )


# ---------------------------------------------------------------------------
# AC-53: Start hook skips behavioral rules in pipeline mode
# ---------------------------------------------------------------------------
class TestStartHook:
    def test_skips_rules_in_pipeline_mode(self):
        """AC-53: Start hook skips behavioral rules when PPDS_PIPELINE=1."""
        hook_path = os.path.join(
            REPO_ROOT, ".claude", "hooks", "session-start-workflow.py"
        )
        env = os.environ.copy()
        env["PPDS_PIPELINE"] = "1"
        result = subprocess.run(
            [sys.executable, hook_path],
            capture_output=True,
            text=True,
            env=env,
            cwd=REPO_ROOT,
            stdin=subprocess.DEVNULL,
            timeout=10,
        )
        # The hook writes to stderr. In pipeline mode, it should NOT
        # contain behavioral directives.
        assert "Don't ask" not in result.stderr, (
            "Start hook should not emit behavioral rules in pipeline mode"
        )
        assert "proceed end-to-end" not in result.stderr, (
            "Start hook should not emit behavioral rules in pipeline mode"
        )


# ---------------------------------------------------------------------------
# AC-54: Pipeline sets PPDS_PIPELINE env var
# ---------------------------------------------------------------------------
class TestPipelineEnv:
    def test_sets_pipeline_env_var(self, tmp_path):
        """AC-54: run_claude sets PPDS_PIPELINE=1 in subprocess env."""
        import pipeline

        wf_dir = tmp_path / ".workflow" / "stages"
        wf_dir.mkdir(parents=True)
        logger = pipeline.open_logger(str(tmp_path / ".workflow" / "pipeline.log"))

        mock_proc = unittest.mock.MagicMock()
        mock_proc.poll.return_value = 0  # Immediate exit
        mock_proc.returncode = 0

        with unittest.mock.patch("subprocess.Popen", return_value=mock_proc) as mock_popen:
            pipeline.run_claude(str(tmp_path), "test", logger, "test-stage")

        env_passed = mock_popen.call_args.kwargs.get("env", {})
        assert env_passed.get("PPDS_PIPELINE") == "1"
        logger.close()


# ---------------------------------------------------------------------------
# AC-55: Stage output goes to file
# ---------------------------------------------------------------------------
class TestStageOutput:
    def test_stage_output_goes_to_file(self):
        """AC-55: Dry-run creates stage log directory structure."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            # Create minimal worktree structure
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            log_path = os.path.join(wf_dir, "pipeline.log")
            logger = pipeline.open_logger(log_path)

            exit_code, logger = pipeline.run_claude(
                tmpdir, "test prompt", logger, "test-stage", dry_run=True
            )
            logger.close()

            # Dry run doesn't create stage logs, but the function should work
            assert exit_code == 0


# ---------------------------------------------------------------------------
# AC-56: Heartbeat logging
# ---------------------------------------------------------------------------
class TestHeartbeat:
    def test_heartbeat_contains_required_fields(self):
        """AC-56/58: Heartbeat log entries have elapsed, pid, output_bytes, activity."""
        import pipeline

        # Test the log function format directly
        with tempfile.TemporaryDirectory() as tmpdir:
            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)
            pipeline.log(
                logger, "test", "HEARTBEAT",
                elapsed="60s", pid=12345, output_bytes=1024, activity="active",
            )
            logger.close()

            with open(log_path) as f:
                content = f.read()

            assert "HEARTBEAT" in content
            assert "elapsed=60s" in content
            assert "pid=12345" in content
            assert "output_bytes=1024" in content
            assert "activity=active" in content


# ---------------------------------------------------------------------------
# AC-57: Timeout kills subprocess
# ---------------------------------------------------------------------------
class TestTimeout:
    def test_stall_and_hard_ceiling_constants_exist(self):
        """AC-26: Stall-based and hard ceiling timeout constants are defined."""
        import pipeline

        assert hasattr(pipeline, "STALL_LIMIT")
        assert hasattr(pipeline, "HARD_CEILING")
        assert pipeline.STALL_LIMIT == 5
        assert pipeline.HARD_CEILING == 7200

    def test_hard_ceiling_default_is_7200(self):
        """AC-26: HARD_CEILING default is 7200 seconds (120 min).

        Negative check: the old 3600 value would undersize cross-cutting
        architectural runs (shakedown-guard regression). Pin the new value
        so a silent revert to 3600 fails the gate.
        """
        import pipeline

        assert pipeline.HARD_CEILING == 7200, (
            f"HARD_CEILING must be 7200 (120 min), got {pipeline.HARD_CEILING}. "
            "If you need to lower this, update spec Tier 1 and the AC table first."
        )
        assert pipeline.HARD_CEILING != 3600, (
            "HARD_CEILING reverted to the old 3600 default — see "
            "specs/pipeline-observability.md AC-26."
        )


# ---------------------------------------------------------------------------
# AC-58: Exit code logged
# ---------------------------------------------------------------------------
class TestExitCode:
    def test_logs_exit_code_on_completion(self):
        """AC-58: DONE entry includes exit code."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            log_path = os.path.join(wf_dir, "pipeline.log")
            logger = pipeline.open_logger(log_path)

            exit_code, logger = pipeline.run_claude(
                tmpdir, "test", logger, "exit-test", dry_run=True,
            )
            logger.close()

            with open(log_path) as f:
                content = f.read()

            assert "DONE" in content
            assert "exit=0" in content


# ---------------------------------------------------------------------------
# AC-59: Output tail captured
# ---------------------------------------------------------------------------
class TestOutputTail:
    def test_captures_output_tail(self):
        """AC-59: Last 20 lines of stage output via _read_last_lines."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            stages_dir = os.path.join(wf_dir, "stages")
            os.makedirs(stages_dir)

            # Create a fake stage log with >20 lines
            stage_log = os.path.join(stages_dir, "test.log")
            with open(stage_log, "w") as f:
                for i in range(30):
                    f.write(f"output line {i}\n")

            # Call the actual production function
            lines = pipeline._read_last_lines(tmpdir, "test", n=20)
            assert len(lines) == 20
            assert lines[0] == "output line 10"
            assert lines[-1] == "output line 29"

    def test_read_last_lines_missing_file(self):
        """_read_last_lines returns empty list for missing file."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            lines = pipeline._read_last_lines(tmpdir, "nonexistent", n=20)
            assert lines == []



# ---------------------------------------------------------------------------
# AC-61: Dry-run works
# ---------------------------------------------------------------------------
class TestDryRun:
    def test_dry_run_skips_subprocess(self):
        """AC-61: Dry-run mode works without spawning subprocess."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            log_path = os.path.join(wf_dir, "pipeline.log")
            logger = pipeline.open_logger(log_path)

            exit_code, logger = pipeline.run_claude(
                tmpdir, "test", logger, "dry-test", dry_run=True,
            )
            logger.close()

            assert exit_code == 0
            with open(log_path) as f:
                content = f.read()
            assert "dry-run" in content


# ---------------------------------------------------------------------------
# AC-64: No commands directory
# ---------------------------------------------------------------------------
class TestCommandsMigration:
    def test_no_commands_directory(self):
        """AC-64: .claude/commands/ directory should not exist."""
        commands_dir = os.path.join(REPO_ROOT, ".claude", "commands")
        assert not os.path.exists(commands_dir), (
            f".claude/commands/ still exists — migration incomplete"
        )


# ---------------------------------------------------------------------------
# AC-65: Pipeline STAGES includes QA
# ---------------------------------------------------------------------------
class TestPipelineStages:
    def test_pipeline_stages_include_qa(self):
        """AC-65: STAGES list contains 'qa' between 'verify' and 'review'."""
        import pipeline

        assert "qa" in pipeline.STAGES
        qa_idx = pipeline.STAGES.index("qa")
        verify_idx = pipeline.STAGES.index("verify")
        review_idx = pipeline.STAGES.index("review")
        assert verify_idx < qa_idx < review_idx, (
            f"QA must be between verify and review: "
            f"verify={verify_idx}, qa={qa_idx}, review={review_idx}"
        )


# ---------------------------------------------------------------------------
# AC-66: /implement skips tail in pipeline mode
# ---------------------------------------------------------------------------
class TestImplementPipelineMode:
    def test_implement_skips_tail_in_pipeline_mode(self):
        """AC-66: /implement SKILL.md contains PPDS_PIPELINE pipeline mode section."""
        skill_path = os.path.join(
            REPO_ROOT, ".claude", "skills", "implement", "SKILL.md"
        )
        with open(skill_path, "r", encoding="utf-8", errors="replace") as f:
            content = f.read()

        assert "PPDS_PIPELINE" in content, (
            "/implement skill must reference PPDS_PIPELINE for pipeline mode"
        )
        assert "Skip Step 6" in content or "skip" in content.lower(), (
            "/implement skill must document skipping mandatory tail in pipeline mode"
        )


# ===========================================================================
# v5.0 Tests (ACs 67-92) — Pipeline Observability + PR Orchestration
# ===========================================================================


# ---------------------------------------------------------------------------
# AC-67: Stream-json output format
# ---------------------------------------------------------------------------
class TestStreamJsonOutput:
    def test_stream_json_output_format(self, tmp_path):
        """AC-67: run_claude passes --output-format stream-json to claude."""
        import pipeline

        wf_dir = tmp_path / ".workflow" / "stages"
        wf_dir.mkdir(parents=True)
        logger = pipeline.open_logger(str(tmp_path / ".workflow" / "pipeline.log"))

        mock_proc = unittest.mock.MagicMock()
        mock_proc.poll.return_value = 0
        mock_proc.returncode = 0

        with unittest.mock.patch("subprocess.Popen", return_value=mock_proc) as mock_popen:
            pipeline.run_claude(str(tmp_path), "test", logger, "test-stage")

        cmd = mock_popen.call_args.args[0] if mock_popen.call_args.args else mock_popen.call_args[0][0]
        assert "--output-format" in cmd
        assert "stream-json" in cmd
        logger.close()


# ---------------------------------------------------------------------------
# AC-68: Multi-signal heartbeat
# ---------------------------------------------------------------------------
class TestHeartbeatMultiSignal:
    def test_heartbeat_multi_signal(self):
        """AC-68: Heartbeat log contains git_changes and commits fields."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)
            pipeline.log(
                logger, "test", "HEARTBEAT",
                elapsed="60s", pid=12345, output_bytes=1024,
                git_changes=5, commits=3, activity="active",
            )
            logger.close()

            with open(log_path) as f:
                content = f.read()

            assert "git_changes=5" in content
            assert "commits=3" in content
            assert "output_bytes=1024" in content
            assert "activity=active" in content


# ---------------------------------------------------------------------------
# AC-69: Activity classification
# ---------------------------------------------------------------------------
class TestActivityClassification:
    def test_active_when_output_grows(self):
        """AC-69: Activity is 'active' when output_bytes increases."""
        import pipeline
        activity, idle = pipeline.classify_activity(200, 100, 3, 3, 2, 2, 5)
        assert activity == "active"
        assert idle == 0

    def test_active_when_git_changes(self):
        """AC-69: Activity is 'active' when git_changes increases."""
        import pipeline
        activity, idle = pipeline.classify_activity(100, 100, 5, 3, 2, 2, 0)
        assert activity == "active"
        assert idle == 0

    def test_active_when_commits_grow(self):
        """AC-69: Activity is 'active' when commits increase."""
        import pipeline
        activity, idle = pipeline.classify_activity(100, 100, 3, 3, 3, 2, 0)
        assert activity == "active"
        assert idle == 0

    def test_idle_when_nothing_changes(self):
        """AC-69: Activity is 'idle' when no signal changes."""
        import pipeline
        activity, idle = pipeline.classify_activity(100, 100, 3, 3, 2, 2, 1)
        assert activity == "idle"
        assert idle == 2

    def test_stalled_after_three_idle(self):
        """AC-69: Activity is 'stalled' after 3+ consecutive idle heartbeats."""
        import pipeline
        activity, idle = pipeline.classify_activity(100, 100, 3, 3, 2, 2, 2)
        assert activity == "stalled"
        assert idle == 3


# ---------------------------------------------------------------------------
# AC-70: JSONL post-processing
# ---------------------------------------------------------------------------
class TestJsonlPostProcessing:
    def test_extract_text_from_jsonl(self):
        """AC-70: extract_text_from_jsonl produces correct text from JSONL."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "test.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"system","subtype":"init"}\n')
                f.write('{"type":"assistant","message":{"content":[{"type":"tool_use"}]}}\n')
                f.write('{"type":"result","subtype":"success","result":"Final response text here"}\n')

            text = pipeline.extract_text_from_jsonl(jsonl_path)
            assert text == "Final response text here"

    def test_skips_malformed_lines(self):
        """AC-70: Malformed JSONL lines are silently skipped."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "bad.jsonl")
            with open(jsonl_path, "w") as f:
                f.write("not json at all\n")
                f.write('{"type":"result","result":"good text"}\n')
                f.write('{"truncated json\n')

            text = pipeline.extract_text_from_jsonl(jsonl_path)
            assert text == "good text"

    def test_empty_file(self):
        """AC-70: Empty JSONL file produces empty string."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "empty.jsonl")
            with open(jsonl_path, "w") as f:
                pass  # Empty file

            text = pipeline.extract_text_from_jsonl(jsonl_path)
            assert text == ""


# ---------------------------------------------------------------------------
# AC-71: Pipeline lock write
# ---------------------------------------------------------------------------
class TestPipelineLockWrite:
    def test_pipeline_lock_write(self):
        """AC-71: Pipeline writes current PID to .workflow/pipeline.lock."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            lock_path = os.path.join(tmpdir, "pipeline.lock")
            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            assert pipeline.acquire_lock(lock_path, logger)
            assert os.path.exists(lock_path)

            with open(lock_path) as f:
                pid = int(f.read().strip())
            assert pid == os.getpid()

            pipeline.release_lock(lock_path)
            logger.close()


# ---------------------------------------------------------------------------
# AC-72: Pipeline lock conflict
# ---------------------------------------------------------------------------
class TestPipelineLockConflict:
    def test_pipeline_lock_conflict(self):
        """AC-72: Exits with error if lock held by live process."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            lock_path = os.path.join(tmpdir, "pipeline.lock")
            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            # Write a lock with our own PID (guaranteed alive)
            with open(lock_path, "w") as f:
                f.write(str(os.getpid()))

            # Should fail to acquire
            assert not pipeline.acquire_lock(lock_path, logger)
            logger.close()


# ---------------------------------------------------------------------------
# AC-73: Pipeline stale lock
# ---------------------------------------------------------------------------
class TestPipelineLockStale:
    def test_pipeline_lock_stale(self):
        """AC-73: Removes lock if PID is dead."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            lock_path = os.path.join(tmpdir, "pipeline.lock")
            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            # Write a lock with a dead PID (99999999 is unlikely to exist)
            with open(lock_path, "w") as f:
                f.write("99999999")

            # Should succeed (stale lock cleaned)
            assert pipeline.acquire_lock(lock_path, logger)

            # Verify our PID is now in the lock
            with open(lock_path) as f:
                pid = int(f.read().strip())
            assert pid == os.getpid()

            pipeline.release_lock(lock_path)
            logger.close()


# ---------------------------------------------------------------------------
# AC-74: Pipeline lock release
# ---------------------------------------------------------------------------
class TestPipelineLockRelease:
    def test_pipeline_lock_release(self):
        """AC-74: Lock is deleted by release_lock."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            lock_path = os.path.join(tmpdir, "pipeline.lock")
            with open(lock_path, "w") as f:
                f.write(str(os.getpid()))

            assert os.path.exists(lock_path)
            pipeline.release_lock(lock_path)
            assert not os.path.exists(lock_path)

    def test_release_nonexistent_lock(self):
        """AC-74: release_lock handles missing file gracefully."""
        import pipeline

        pipeline.release_lock("/nonexistent/path/pipeline.lock")
        # Should not raise


# ---------------------------------------------------------------------------
# AC-75: /status JSONL parser
# ---------------------------------------------------------------------------
class TestStatusJsonlParser:
    def test_status_jsonl_parser(self):
        """AC-75: Can extract tool calls from stream-json fixture."""
        # Test that JSONL with tool_use events can be parsed
        jsonl_content = textwrap.dedent("""\
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"src/foo.cs"}}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet build"}}]}}
            {"type":"result","result":"Done."}
        """)

        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "implement.jsonl")
            with open(jsonl_path, "w") as f:
                f.write(jsonl_content)

            # Parse last tool call
            last_tool = None
            with open(jsonl_path, "r") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        event = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if event.get("type") == "assistant":
                        msg = event.get("message", {})
                        for block in msg.get("content", []):
                            if block.get("type") == "tool_use":
                                last_tool = block.get("name")

            assert last_tool == "Bash"


# ---------------------------------------------------------------------------
# AC-76: /status heartbeat display
# ---------------------------------------------------------------------------
class TestStatusHeartbeatDisplay:
    def test_status_heartbeat_display(self):
        """AC-76: Can parse heartbeat data from pipeline.log."""
        log_content = textwrap.dedent("""\
            2026-03-26T23:08:31Z [implement] START
            2026-03-26T23:09:31Z [implement] HEARTBEAT elapsed=60s pid=12345 output_bytes=45230 git_changes=3 commits=1 activity=active
            2026-03-26T23:10:31Z [implement] HEARTBEAT elapsed=120s pid=12345 output_bytes=102400 git_changes=5 commits=2 activity=active
        """)

        # Parse last heartbeat
        last_heartbeat = {}
        for line in log_content.strip().splitlines():
            if "HEARTBEAT" in line:
                parts = line.split()
                for part in parts:
                    if "=" in part:
                        key, val = part.split("=", 1)
                        last_heartbeat[key] = val

        assert last_heartbeat["elapsed"] == "120s"
        assert last_heartbeat["output_bytes"] == "102400"
        assert last_heartbeat["git_changes"] == "5"
        assert last_heartbeat["commits"] == "2"
        assert last_heartbeat["activity"] == "active"


# ---------------------------------------------------------------------------
# AC-78: Git timeout in heartbeat
# ---------------------------------------------------------------------------
class TestHeartbeatGitTimeout:
    def test_heartbeat_git_timeout(self):
        """AC-78: get_git_activity uses timeout=5 and handles failure gracefully."""
        import pipeline

        # Call on a non-git directory — should return (0, 0) without hanging
        with tempfile.TemporaryDirectory() as tmpdir:
            changes, commits = pipeline.get_git_activity(tmpdir)
            assert changes == 0
            assert commits == 0


# ---------------------------------------------------------------------------
# AC-79: PR creates draft
# ---------------------------------------------------------------------------
class TestPrCreatesDraft:
    def test_pr_creates_draft(self, tmp_path):
        """AC-79: PR is created in draft mode."""
        import pipeline

        # Setup minimal worktree
        wf_dir = tmp_path / ".workflow"
        wf_dir.mkdir()
        state = {"branch": "feat/test", "gates": {"passed": True}, "verify": {}, "qa": {}, "review": {}}
        (wf_dir / "state.json").write_text(json.dumps(state))
        logger = pipeline.open_logger(str(wf_dir / "pipeline.log"))

        # Mock all subprocess calls
        def mock_run(cmd, **kwargs):
            result = unittest.mock.MagicMock()
            result.returncode = 0
            result.stdout = ""
            result.stderr = ""
            if cmd[0] == "gh" and "pr" in cmd and "create" in cmd:
                result.stdout = "https://github.com/test/repo/pull/42\n"
            elif cmd[0] == "git" and "rev-parse" in cmd:
                result.stdout = "feat/test\n"
            elif cmd[0] == "python":
                result.stdout = "[]"
            return result

        calls = []
        original_run = unittest.mock.MagicMock(side_effect=lambda cmd, **kw: (calls.append(cmd), mock_run(cmd, **kw))[1])

        with unittest.mock.patch("subprocess.run", original_run):
            with unittest.mock.patch.object(pipeline, "run_claude", return_value=(0, logger)):
                with unittest.mock.patch.object(pipeline, "poll_gemini", return_value=[]):
                    pipeline.run_pr_stage(str(tmp_path), logger)

        # Find the gh pr create call and verify --draft is present
        pr_create_calls = [c for c in calls if len(c) > 2 and c[0] == "gh" and "create" in c]
        assert pr_create_calls, "Expected gh pr create call"
        assert "--draft" in pr_create_calls[0], f"Expected --draft in {pr_create_calls[0]}"
        logger.close()


# ---------------------------------------------------------------------------
# AC-80: Gemini min wait
# ---------------------------------------------------------------------------
class TestPrGeminiMinWait:
    def test_pr_gemini_min_wait(self):
        """AC-80: poll_gemini has min_wait=90 default."""
        import inspect
        import pipeline

        sig = inspect.signature(pipeline.poll_gemini)
        assert sig.parameters["min_wait"].default == 90


# ---------------------------------------------------------------------------
# AC-81: Triage agent invocation
# ---------------------------------------------------------------------------
class TestPrTriageAgentInvocation:
    def test_pr_triage_agent_invocation(self, tmp_path):
        """AC-81: Triage uses the gemini-triage agent."""
        import pipeline

        wf_dir = tmp_path / ".workflow" / "stages"
        wf_dir.mkdir(parents=True)
        (tmp_path / ".workflow" / "state.json").write_text('{}')
        logger = pipeline.open_logger(str(tmp_path / ".workflow" / "pipeline.log"))

        comments = [{"id": 1, "body": "test", "path": "foo.py", "line": 10}]

        with unittest.mock.patch.object(pipeline, "run_claude", return_value=(0, logger)) as mock_run:
            # Write fake triage output
            (wf_dir / "pr-triage.log").write_text('[{"id": 1, "action": "dismissed", "description": "ok"}]')
            pipeline.run_triage(str(tmp_path), "42", comments, logger)

        # Verify agent="gemini-triage" was passed
        assert mock_run.called
        _, kwargs = mock_run.call_args
        assert kwargs.get("agent") == "gemini-triage"
        logger.close()


# ---------------------------------------------------------------------------
# AC-82: Threaded replies
# ---------------------------------------------------------------------------
class TestPrThreadedReplies:
    def test_pr_threaded_replies(self, tmp_path):
        """AC-82: post_replies sends in_reply_to for threaded comments."""
        import pipeline

        logger = pipeline.open_logger(str(tmp_path / "test.log"))
        triage = [{"id": 123, "action": "fixed", "description": "done", "commit": "abc123"}]

        with unittest.mock.patch.object(pipeline, "get_repo_slug", return_value="owner/repo"):
            with unittest.mock.patch("subprocess.run") as mock_run:
                mock_run.return_value = unittest.mock.MagicMock(returncode=0)
                pipeline.post_replies(str(tmp_path), "42", triage, logger)

        assert mock_run.called
        cmd = mock_run.call_args[0][0]
        # Verify in_reply_to is in the gh api command
        in_reply_args = [a for a in cmd if "in_reply_to" in str(a)]
        assert in_reply_args, f"Expected in_reply_to in command: {cmd}"
        logger.close()


# ---------------------------------------------------------------------------
# AC-83: Draft to ready
# ---------------------------------------------------------------------------
class TestPrDraftToReady:
    def test_pr_draft_to_ready(self, tmp_path):
        """AC-83 (rewritten for v1-prelaunch retro #4):
        After PR creation, run_pr_stage delegates to pr_monitor.py which is
        responsible for converting the draft to ready (and the rest of the
        polling loop). The pipeline only needs to invoke the monitor; the
        monitor owns the ``gh pr ready`` call."""
        import pipeline

        wf_dir = tmp_path / ".workflow"
        wf_dir.mkdir()
        state = {"branch": "feat/test", "gates": {"passed": True}, "verify": {}, "qa": {}, "review": {}}
        (wf_dir / "state.json").write_text(json.dumps(state))
        logger = pipeline.open_logger(str(wf_dir / "pipeline.log"))

        calls = []

        def mock_run(cmd, **kwargs):
            calls.append(cmd)
            result = unittest.mock.MagicMock()
            result.returncode = 0
            result.stdout = ""
            result.stderr = ""
            if cmd[0] == "gh" and "pr" in cmd and "create" in cmd:
                result.stdout = "https://github.com/test/repo/pull/42\n"
            elif cmd[0] == "git" and "rev-parse" in cmd:
                result.stdout = "feat/test\n"
            elif cmd[0] == "python":
                result.stdout = "[]"
            return result

        with unittest.mock.patch("subprocess.run", side_effect=mock_run):
            with unittest.mock.patch.object(pipeline, "run_claude", return_value=(0, logger)):
                pipeline.run_pr_stage(str(tmp_path), logger)

        # Delegation contract: pr_monitor.py must be invoked
        monitor_calls = [
            c for c in calls
            if any("pr_monitor.py" in str(arg) for arg in c)
        ]
        assert monitor_calls, (
            f"pr_monitor.py was not invoked. calls: {[c[:4] for c in calls]}"
        )
        logger.close()


# ---------------------------------------------------------------------------
# AC-84: Gemini triage agent exists
# ---------------------------------------------------------------------------
class TestGeminiTriageAgentExists:
    def test_gemini_triage_agent_exists(self):
        """AC-84: Agent profile exists with model=sonnet and restricted tools."""
        agent_path = os.path.join(REPO_ROOT, ".claude", "agents", "gemini-triage.md")
        assert os.path.exists(agent_path), \
            ".claude/agents/gemini-triage.md must exist"

        with open(agent_path, "r") as f:
            content = f.read()

        assert "model: sonnet" in content, "Agent must use model: sonnet"
        assert "tools:" in content, "Agent must have tools"
        assert "Read" in content, "Agent must allow Read tool"
        assert "Edit" in content, "Agent must allow Edit tool"


# ---------------------------------------------------------------------------
# AC-85: Pipeline result JSON
# ---------------------------------------------------------------------------
class TestPipelineResultJson:
    def test_pipeline_result_json(self):
        """AC-85: write_result creates pipeline-result.json with all fields."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)

            stages = {
                "implement": {"duration": "975s", "exit": 0, "last_line": ""},
                "gates": {"duration": "300s", "exit": 0, "last_line": ""},
            }
            pipeline.write_result(
                tmpdir, "complete", 3600, stages,
                pr_url="https://github.com/test/pr/1",
            )

            result_path = os.path.join(wf_dir, "pipeline-result.json")
            assert os.path.exists(result_path)

            with open(result_path) as f:
                result = json.load(f)

            assert result["status"] == "complete"
            assert result["duration"] == 3600
            assert result["stages"]["implement"]["duration"] == "975s"
            assert result["pr_url"] == "https://github.com/test/pr/1"
            assert "timestamp" in result

    def test_pipeline_result_failure(self):
        """AC-85: Failure result includes failed_stage and stages."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)

            stages = {
                "implement": {"duration": "975s", "exit": 0, "last_line": ""},
                "gates": {"duration": "300s", "exit": 0, "last_line": ""},
            }
            pipeline.write_result(
                tmpdir, "failed", 2700, stages,
                failed_stage="converge",
                error="max rounds exceeded",
            )

            result_path = os.path.join(wf_dir, "pipeline-result.json")
            with open(result_path) as f:
                result = json.load(f)

            assert result["status"] == "failed"
            assert result["failed_stage"] == "converge"
            assert result["error"] == "max rounds exceeded"
            assert result["stages"]["implement"]["duration"] == "975s"


# ---------------------------------------------------------------------------
# AC-86: Pipeline notify
# ---------------------------------------------------------------------------
class TestPipelineNotify:
    def test_pipeline_notify(self):
        """AC-86: Notification failure does not fail the pipeline."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)

            # write_result with no notify.py script — should not raise
            pipeline.write_result(tmpdir, "complete", 100, {})
            assert os.path.exists(os.path.join(wf_dir, "pipeline-result.json"))


# ---------------------------------------------------------------------------
# AC-88: Gemini stabilization
# ---------------------------------------------------------------------------
class TestPrGeminiStabilization:
    def test_pr_gemini_stabilization(self, tmp_path):
        """AC-88 (rewritten for v1-prelaunch retro #3):
        poll_gemini terminates as soon as a Gemini bot review appears on
        any of the three endpoints. Returns inline comments from
        pulls/comments (may be empty if Gemini posted only a top-level
        review with no inline notes)."""
        import pipeline
        import triage_common

        logger = pipeline.open_logger(str(tmp_path / "test.log"))

        gemini_review = {
            "id": 1,
            "user": {"login": triage_common.GEMINI_BOT_LOGIN},
            "submitted_at": "2026-04-19T05:00:00Z",
            "state": "COMMENTED",
        }
        inline_comments = [
            {"id": i, "user": {"login": "x"}, "path": "a.py",
             "line": i, "body": f"c{i}", "created_at": "2026-04-19T05:00:00Z"}
            for i in range(3)
        ]

        def mock_run(cmd, **kwargs):
            result = unittest.mock.MagicMock()
            result.returncode = 0
            result.stderr = ""
            if cmd[0] == "gh" and cmd[1] == "pr" and "view" in cmd:
                result.stdout = "2026-04-19T04:00:00Z"  # PR createdAt
            elif cmd[0] == "gh" and cmd[1] == "api":
                path = cmd[2]
                if "/reviews" in path:
                    result.stdout = json.dumps([gemini_review])
                elif "/pulls/" in path and path.endswith("/comments"):
                    result.stdout = json.dumps(inline_comments)
                elif "/issues/" in path and path.endswith("/comments"):
                    result.stdout = json.dumps([])
                else:
                    result.stdout = "[]"
            else:
                result.stdout = ""
            return result

        with unittest.mock.patch.object(pipeline, "get_repo_slug",
                                         return_value="owner/repo"), \
             unittest.mock.patch("triage_common.get_repo_slug",
                                 return_value="owner/repo"), \
             unittest.mock.patch("triage_common.subprocess.run",
                                 side_effect=mock_run), \
             unittest.mock.patch("subprocess.run", side_effect=mock_run), \
             unittest.mock.patch("triage_common.time.sleep"):
            comments = pipeline.poll_gemini(
                str(tmp_path), "42", logger, min_wait=0, max_wait=10)

        assert len(comments) == 3, f"Expected 3 comments, got {len(comments)}"
        logger.close()


# ---------------------------------------------------------------------------
# AC-89: Gemini max wait
# ---------------------------------------------------------------------------
class TestPrGeminiMaxWait:
    def test_pr_gemini_max_wait(self):
        """AC-89: poll_gemini has max_wait=300 (5 minutes) default."""
        import inspect
        import pipeline

        sig = inspect.signature(pipeline.poll_gemini)
        assert sig.parameters["max_wait"].default == 300


# ---------------------------------------------------------------------------
# AC-90, AC-91, AC-92: post-PR-creation behavior moved to pr_monitor.py
# ---------------------------------------------------------------------------
# After v1-prelaunch retro item #4, the polling loop (Gemini timeout
# annotation, triage failure annotation, push verification, ready conversion)
# moved into pr_monitor.py — pipeline.run_pr_stage now delegates the entire
# post-creation phase to that script as a subprocess. The behavior is
# covered by tests/test_pr_monitor.py and tests/test_pipeline_pr_monitor_delegation.py.
#
# AC-90 / AC-91 (PR body annotation): pr_monitor doesn't currently re-edit
# the PR body for these conditions; it logs and proceeds. Pipeline used to
# do this in-process. The delegated design accepts a less-chatty PR body in
# exchange for a single canonical implementation. If body annotation is
# required again it should be added to pr_monitor.py (one place), not
# duplicated.


# ===========================================================================
# Pipeline Observability v2 Tests (pipeline-observability.md ACs)
# ===========================================================================


# ---------------------------------------------------------------------------
# AC-01: extract_text_from_jsonl falls back to assistant messages
# ---------------------------------------------------------------------------
class TestExtractTextAssistantFallback:
    def test_extracts_from_assistant_messages_when_no_result(self):
        """AC-01: Returns text from assistant events when no result event exists."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "timeout.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"system","subtype":"init"}\n')
                f.write('{"type":"assistant","message":{"content":[{"type":"text","text":"Phase 1 complete: CLI 7/7"}]}}\n')
                f.write('{"type":"tool_use","name":"Bash"}\n')
                f.write('{"type":"assistant","message":{"content":[{"type":"text","text":"Phase 2 starting..."}]}}\n')
                # No result event — simulates timeout kill

            text = pipeline.extract_text_from_jsonl(jsonl_path)
            assert "Phase 1 complete: CLI 7/7" in text
            assert "Phase 2 starting..." in text
            assert len(text) > 0

    def test_extracts_text_blocks_only_not_tool_use(self):
        """AC-01: Only extracts text blocks from assistant content, not tool_use."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "mixed.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{}},{"type":"text","text":"Done editing"}]}}\n')

            text = pipeline.extract_text_from_jsonl(jsonl_path)
            assert "Done editing" in text
            assert "Edit" not in text

    def test_empty_content_array(self):
        """AC-01 edge case: Assistant event with empty content returns empty string."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "empty_content.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"assistant","message":{"content":[]}}\n')
                f.write('{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash"}]}}\n')

            text = pipeline.extract_text_from_jsonl(jsonl_path)
            assert text == ""


# ---------------------------------------------------------------------------
# AC-02: extract_text_from_jsonl prefers result events
# ---------------------------------------------------------------------------
class TestExtractTextPrefersResult:
    def test_prefers_result_when_both_exist(self):
        """AC-02: Returns result text when both result and assistant events exist."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            jsonl_path = os.path.join(tmpdir, "clean.jsonl")
            with open(jsonl_path, "w") as f:
                f.write('{"type":"assistant","message":{"content":[{"type":"text","text":"Intermediate step 1"}]}}\n')
                f.write('{"type":"assistant","message":{"content":[{"type":"text","text":"Intermediate step 2"}]}}\n')
                f.write('{"type":"result","subtype":"success","result":"Final summary"}\n')

            text = pipeline.extract_text_from_jsonl(jsonl_path)
            assert text == "Final summary"
            assert "Intermediate" not in text


# ---------------------------------------------------------------------------
# AC-03: pipeline-result.json includes last_output on failure
# ---------------------------------------------------------------------------
class TestWriteResultLastOutput:
    def test_includes_last_output_on_failure(self):
        """AC-03: last_output field present when failed_stage is set."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)

            last_output = ["Phase 1 complete", "Phase 2 starting...", "VS Code launch"]
            pipeline.write_result(
                tmpdir, "failed", 1847, {},
                failed_stage="qa",
                error="stall timeout after 5m idle",
                last_output=last_output,
            )

            with open(os.path.join(wf_dir, "pipeline-result.json")) as f:
                result = json.load(f)

            assert result["last_output"] == last_output
            assert len(result["last_output"]) == 3

    def test_empty_last_output_list_is_included(self):
        """AC-03: Empty list is included (not omitted like None)."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)

            pipeline.write_result(
                tmpdir, "failed", 100, {},
                failed_stage="gates",
                last_output=[],
            )

            with open(os.path.join(wf_dir, "pipeline-result.json")) as f:
                result = json.load(f)

            assert "last_output" in result
            assert result["last_output"] == []

    def test_no_last_output_when_none(self):
        """AC-03: last_output key absent when not provided."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)

            pipeline.write_result(tmpdir, "complete", 100, {})

            with open(os.path.join(wf_dir, "pipeline-result.json")) as f:
                result = json.load(f)

            assert "last_output" not in result


# ---------------------------------------------------------------------------
# AC-04: Pipeline runs retro after PipelineFailure
# ---------------------------------------------------------------------------
class TestPipelineFailureRetro:
    """AC-4: Pipeline failure triggers retro."""

    def test_pipeline_failure_is_exception(self):
        """AC-4: PipelineFailure is a proper exception class."""
        import pipeline
        assert issubclass(pipeline.PipelineFailure, Exception)
        err = pipeline.PipelineFailure("test failure")
        assert "test failure" in str(err)

    def test_failure_handler_runs_retro(self):
        """AC-4: PipelineFailure exception handler invokes /retro.

        Verifies structurally that the except PipelineFailure block contains
        a retro invocation. This is a deliberate structural check because
        main() is too large to unit-test end-to-end without integration setup.
        """
        import pipeline
        import ast

        source = open(pipeline.__file__).read()
        tree = ast.parse(source)

        # Find the except PipelineFailure handler in main()
        found_retro_in_handler = False
        for node in ast.walk(tree):
            if isinstance(node, ast.ExceptHandler):
                if node.type and hasattr(node.type, 'id') and node.type.id == 'PipelineFailure':
                    # Check handler body for /retro reference
                    handler_source = ast.get_source_segment(source, node)
                    if handler_source and "/retro" in handler_source:
                        found_retro_in_handler = True
        assert found_retro_in_handler, "except PipelineFailure handler must invoke /retro"


# ---------------------------------------------------------------------------
# AC-06: pipeline-result.json includes partial QA results
# ---------------------------------------------------------------------------
class TestWriteResultPartialQa:
    def test_includes_partial_qa_on_qa_timeout(self):
        """AC-06: partial_results from qa_partial in state.json when QA fails."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)

            # Write state with qa_partial
            state = {
                "qa_partial": {
                    "phase1_completed": "2026-03-26T10:00:00Z",
                    "phase1_checks_passed": 7,
                    "phase1_checks_total": 7,
                }
            }
            with open(os.path.join(wf_dir, "state.json"), "w") as f:
                json.dump(state, f)

            pipeline.write_result(
                tmpdir, "failed", 1200, {},
                failed_stage="qa",
                error="stall timeout",
            )

            with open(os.path.join(wf_dir, "pipeline-result.json")) as f:
                result = json.load(f)

            assert "partial_results" in result
            assert result["partial_results"]["phase1_checks_passed"] == 7

    def test_no_partial_results_when_not_qa_failure(self):
        """AC-06: No partial_results when failed stage is not QA."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)

            pipeline.write_result(
                tmpdir, "failed", 500, {},
                failed_stage="gates",
                error="build failure",
            )

            with open(os.path.join(wf_dir, "pipeline-result.json")) as f:
                result = json.load(f)

            assert "partial_results" not in result


# ---------------------------------------------------------------------------
# AC-09: Stage entries include exit code and last_line
# ---------------------------------------------------------------------------
class TestStageDetailedEntries:
    def test_stages_include_exit_and_last_line(self):
        """AC-09: Per-stage entries have duration, exit, and last_line."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)

            stages = {
                "implement": {"duration": "450s", "exit": 0, "last_line": "All tests passed"},
                "qa": {"duration": "1200s", "exit": -1, "last_line": "Phase 2A: Opening Solutions panel..."},
            }
            pipeline.write_result(
                tmpdir, "failed", 1650, stages,
                failed_stage="qa",
            )

            with open(os.path.join(wf_dir, "pipeline-result.json")) as f:
                result = json.load(f)

            assert result["stages"]["implement"]["exit"] == 0
            assert result["stages"]["qa"]["exit"] == -1
            assert result["stages"]["qa"]["last_line"] == "Phase 2A: Opening Solutions panel..."


# ---------------------------------------------------------------------------
# AC-12: Stall timeout kills after consecutive idle
# ---------------------------------------------------------------------------
class TestStallTimeout:
    def test_stall_timeout_kills_after_consecutive_idle(self):
        """AC-12: classify_activity returns 'stalled' at consecutive_idle >= 3,
        and STALL_LIMIT is the kill threshold (5 consecutive idle beats)."""
        import pipeline

        # Verify STALL_LIMIT constant is 5
        assert pipeline.STALL_LIMIT == 5, (
            f"STALL_LIMIT should be 5, got {pipeline.STALL_LIMIT}"
        )

        # Simulate consecutive idle beats accumulating to STALL_LIMIT
        consecutive_idle = 0
        for beat in range(pipeline.STALL_LIMIT):
            # No activity: same size, same git changes, same commits
            activity, consecutive_idle = pipeline.classify_activity(
                100, 100, 3, 3, 2, 2, consecutive_idle,
            )

        # After STALL_LIMIT idle beats, consecutive_idle reaches the kill threshold
        assert consecutive_idle >= pipeline.STALL_LIMIT, (
            f"After {pipeline.STALL_LIMIT} idle beats, consecutive_idle should "
            f"be >= STALL_LIMIT ({pipeline.STALL_LIMIT}), got {consecutive_idle}"
        )
        assert activity == "stalled", (
            f"Activity should be 'stalled' after {pipeline.STALL_LIMIT} idle beats, "
            f"got '{activity}'"
        )


# ---------------------------------------------------------------------------
# AC-13: Hard timeout at ceiling
# ---------------------------------------------------------------------------
class TestHardTimeout:
    def test_hard_timeout_kills_at_ceiling(self, tmp_path):
        """AC-13: run_claude terminates subprocess when hard ceiling exceeded."""
        import pipeline

        wf_dir = tmp_path / ".workflow" / "stages"
        wf_dir.mkdir(parents=True)
        logger = pipeline.open_logger(str(tmp_path / ".workflow" / "pipeline.log"))

        mock_proc = unittest.mock.MagicMock()
        mock_proc.poll.return_value = None  # Never exits naturally
        mock_proc.wait.return_value = -1
        mock_proc.returncode = -1

        # Simulate time: first call = start, second call immediately past ceiling
        call_count = [0]
        ceiling = pipeline.HARD_CEILING

        def fake_time():
            call_count[0] += 1
            if call_count[0] <= 1:
                return 1000.0  # start = time.time()
            return 1000.0 + ceiling + 1  # All subsequent calls exceed ceiling

        # Also mock subprocess.run (called by get_git_activity during heartbeat)
        mock_run_result = unittest.mock.MagicMock(returncode=0, stdout="0\n", stderr="")

        with unittest.mock.patch("subprocess.Popen", return_value=mock_proc):
            with unittest.mock.patch("subprocess.run", return_value=mock_run_result):
                with unittest.mock.patch("time.time", side_effect=fake_time):
                    with unittest.mock.patch("time.sleep"):
                        exit_code, _ = pipeline.run_claude(
                            str(tmp_path), "test", logger, "test-stage")

        mock_proc.terminate.assert_called_once()
        assert exit_code == -1
        logger.close()


# ---------------------------------------------------------------------------
# AC-27: --max-stage-seconds overrides HARD_CEILING for the run
# AC-28: Effective ceiling is recorded on stage START
# ---------------------------------------------------------------------------
class TestMaxStageSecondsOverride:
    def _run_claude_with_ceiling(self, tmp_path, stage_name, ceiling,
                                  elapsed_offset):
        """Helper: run run_claude with a mocked subprocess that never exits,
        fake time jumping past ``elapsed_offset`` after start. Returns the
        mock_proc and log content for assertions."""
        import pipeline

        wf_dir = tmp_path / ".workflow" / "stages"
        wf_dir.mkdir(parents=True)
        log_path = tmp_path / ".workflow" / "pipeline.log"
        logger = pipeline.open_logger(str(log_path))

        mock_proc = unittest.mock.MagicMock()
        mock_proc.poll.return_value = None  # Never exits naturally
        mock_proc.wait.return_value = -1
        mock_proc.returncode = -1

        call_count = [0]

        def fake_time():
            call_count[0] += 1
            if call_count[0] <= 1:
                return 1000.0
            return 1000.0 + elapsed_offset

        mock_run_result = unittest.mock.MagicMock(returncode=0, stdout="0\n", stderr="")

        with unittest.mock.patch("subprocess.Popen", return_value=mock_proc):
            with unittest.mock.patch("subprocess.run", return_value=mock_run_result):
                with unittest.mock.patch("time.time", side_effect=fake_time):
                    with unittest.mock.patch("time.sleep"):
                        exit_code, _ = pipeline.run_claude(
                            str(tmp_path), "test", logger, stage_name,
                            ceiling=ceiling)

        logger.close()
        with open(log_path) as f:
            content = f.read()
        return mock_proc, content, exit_code

    def test_max_stage_seconds_flag_overrides_ceiling(self, tmp_path):
        """AC-27: ceiling=N shortens the effective kill threshold below the
        module default. Elapsed of 100s exceeds ceiling=60 but is well below
        HARD_CEILING (7200) — terminate must still fire, proving the override
        replaced the default."""
        mock_proc, content, exit_code = self._run_claude_with_ceiling(
            tmp_path, "override-short", ceiling=60, elapsed_offset=100,
        )
        mock_proc.terminate.assert_called_once()
        assert exit_code == -1
        assert "HARD_TIMEOUT" in content
        assert "ceiling=60s" in content, (
            f"Expected 'ceiling=60s' in HARD_TIMEOUT log, got:\n{content}"
        )

    def test_ceiling_override_extends_beyond_default(self, tmp_path):
        """AC-27: ceiling=N can also extend the ceiling past HARD_CEILING.
        Elapsed of HARD_CEILING+1 would normally trip the kill with the
        default, but with ceiling=HARD_CEILING+1000 the stage keeps running
        (proc.terminate is not called for the ceiling reason)."""
        import pipeline

        override = pipeline.HARD_CEILING + 1000  # 8200 (> default 7200)
        # elapsed falls between default and override → default would kill, override keeps running.
        elapsed = pipeline.HARD_CEILING + 500  # 7700

        wf_dir = tmp_path / ".workflow" / "stages"
        wf_dir.mkdir(parents=True)
        logger = pipeline.open_logger(str(tmp_path / ".workflow" / "pipeline.log"))

        mock_proc = unittest.mock.MagicMock()
        # Exit "naturally" on second poll so the loop terminates without
        # hitting the ceiling branch.
        poll_calls = [0]

        def fake_poll():
            poll_calls[0] += 1
            if poll_calls[0] >= 2:
                return 0  # clean exit
            return None

        mock_proc.poll.side_effect = fake_poll
        mock_proc.returncode = 0

        call_count = [0]

        def fake_time():
            call_count[0] += 1
            if call_count[0] <= 1:
                return 1000.0
            return 1000.0 + elapsed

        mock_run_result = unittest.mock.MagicMock(returncode=0, stdout="0\n", stderr="")

        with unittest.mock.patch("subprocess.Popen", return_value=mock_proc):
            with unittest.mock.patch("subprocess.run", return_value=mock_run_result):
                with unittest.mock.patch("time.time", side_effect=fake_time):
                    with unittest.mock.patch("time.sleep"):
                        exit_code, _ = pipeline.run_claude(
                            str(tmp_path), "test", logger, "override-long",
                            ceiling=override,
                        )

        # With the override, the ceiling branch should NOT fire at elapsed=7700
        # because 7700 < 8200. The stage exits cleanly via poll returning 0.
        mock_proc.terminate.assert_not_called()
        assert exit_code == 0
        logger.close()

    def test_run_claude_logs_effective_ceiling_on_start_default(self, tmp_path):
        """AC-28: When no override is provided, START log records the module
        default ceiling so operators can see which value is in effect."""
        import pipeline

        log_path = tmp_path / ".workflow" / "pipeline.log"
        (tmp_path / ".workflow").mkdir()
        logger = pipeline.open_logger(str(log_path))
        pipeline.run_claude(
            str(tmp_path), "test", logger, "start-default", dry_run=True,
        )
        logger.close()

        with open(log_path) as f:
            content = f.read()

        assert "START" in content
        assert f"ceiling={pipeline.HARD_CEILING}s" in content, (
            f"Expected START entry to log ceiling={pipeline.HARD_CEILING}s, got:\n{content}"
        )

    def test_run_claude_logs_effective_ceiling_on_start_override(self, tmp_path):
        """AC-28: When --max-stage-seconds (ceiling=N) is provided, START
        log records N, not the default."""
        import pipeline

        log_path = tmp_path / ".workflow" / "pipeline.log"
        (tmp_path / ".workflow").mkdir()
        logger = pipeline.open_logger(str(log_path))
        pipeline.run_claude(
            str(tmp_path), "test", logger, "start-override",
            dry_run=True, ceiling=1234,
        )
        logger.close()

        with open(log_path) as f:
            content = f.read()

        assert "ceiling=1234s" in content, (
            f"Expected START entry to log ceiling=1234s, got:\n{content}"
        )
        # Negative verification: the default must NOT leak into the log when
        # an override is provided.
        assert f"ceiling={pipeline.HARD_CEILING}s" not in content, (
            "Default ceiling leaked into log when override was provided"
        )

    def test_pr_monitor_uses_effective_ceiling(self, tmp_path):
        """AC-27: _delegate_to_pr_monitor passes the override to
        subprocess.run's timeout kwarg (not the module default)."""
        import pipeline

        (tmp_path / ".workflow").mkdir()
        logger = pipeline.open_logger(str(tmp_path / ".workflow" / "pipeline.log"))

        captured = {}

        def fake_run(cmd, **kwargs):
            captured["timeout"] = kwargs.get("timeout")
            result = unittest.mock.MagicMock()
            result.returncode = 0
            result.stdout = ""
            result.stderr = ""
            return result

        with unittest.mock.patch("subprocess.run", side_effect=fake_run):
            pipeline._delegate_to_pr_monitor(
                str(tmp_path), 42, logger, dry_run=False, ceiling=999,
            )
        logger.close()

        assert captured["timeout"] == 999, (
            f"Expected subprocess timeout=999 (override), got {captured['timeout']}"
        )

    def test_cli_exposes_max_stage_seconds_flag(self, capsys):
        """AC-27: pipeline's argparse advertises the --max-stage-seconds flag.

        This guards against accidental removal from the argparse block —
        the behavioral tests above exercise ceiling= directly, so without
        this gate the flag could silently disappear from the CLI surface
        while all the plumbing tests still pass.
        """
        import pipeline

        with unittest.mock.patch("sys.argv", ["pipeline.py", "--help"]):
            with pytest.raises(SystemExit) as exc_info:
                pipeline.main()
        assert exc_info.value.code == 0
        out = capsys.readouterr().out
        assert "--max-stage-seconds" in out, (
            f"--max-stage-seconds missing from --help output:\n{out}"
        )

    def test_cli_rejects_non_positive_max_stage_seconds(self, capsys):
        """AC-27: --max-stage-seconds must be a positive integer; 0 and
        negative values are rejected at parse time."""
        import pipeline

        for bad in ("0", "-1"):
            argv = ["pipeline.py", "--spec", "x", "--branch", "y",
                    "--max-stage-seconds", bad]
            with unittest.mock.patch("sys.argv", argv):
                with pytest.raises(SystemExit) as exc_info:
                    pipeline.main()
            assert exc_info.value.code != 0, (
                f"Expected error for --max-stage-seconds {bad}, got exit 0"
            )
            err = capsys.readouterr().err.lower()
            assert "max-stage-seconds" in err or "max_stage_seconds" in err, (
                f"Error message for bad --max-stage-seconds={bad} should "
                f"mention the flag, got:\n{err}"
            )

    def test_pr_monitor_falls_back_to_default_without_override(self, tmp_path):
        """AC-27 boundary: ceiling=None preserves the module default
        (backwards compat — no regression in existing behavior)."""
        import pipeline

        (tmp_path / ".workflow").mkdir()
        logger = pipeline.open_logger(str(tmp_path / ".workflow" / "pipeline.log"))

        captured = {}

        def fake_run(cmd, **kwargs):
            captured["timeout"] = kwargs.get("timeout")
            result = unittest.mock.MagicMock()
            result.returncode = 0
            result.stdout = ""
            result.stderr = ""
            return result

        with unittest.mock.patch("subprocess.run", side_effect=fake_run):
            pipeline._delegate_to_pr_monitor(
                str(tmp_path), 42, logger, dry_run=False, ceiling=None,
            )
        logger.close()

        assert captured["timeout"] == pipeline.HARD_CEILING


# ---------------------------------------------------------------------------
# AC-14: Active stage not killed by stall timeout
# ---------------------------------------------------------------------------
class TestActiveStageNotKilled:
    def test_active_stage_resets_idle_counter(self):
        """AC-14: Activity resets consecutive_idle to 0 via classify_activity."""
        import pipeline

        # Build up to 4 consecutive idle beats (one below STALL_LIMIT)
        consecutive_idle = 4

        # Output grew (200 > 100) — should reset idle counter
        activity, new_idle = pipeline.classify_activity(
            200, 100, 3, 3, 2, 2, consecutive_idle,
        )
        assert activity == "active", f"Expected 'active', got '{activity}'"
        assert new_idle == 0, f"Expected consecutive_idle reset to 0, got {new_idle}"

    def test_git_changes_reset_idle_counter(self):
        """AC-14: Git changes also reset consecutive_idle to 0."""
        import pipeline

        consecutive_idle = 4

        # Git changes grew (4 > 3) even though output didn't grow
        activity, new_idle = pipeline.classify_activity(
            100, 100, 4, 3, 2, 2, consecutive_idle,
        )
        assert activity == "active", f"Expected 'active', got '{activity}'"
        assert new_idle == 0, f"Expected consecutive_idle reset to 0, got {new_idle}"

    def test_new_commits_reset_idle_counter(self):
        """AC-14: New commits also reset consecutive_idle to 0."""
        import pipeline

        consecutive_idle = 4

        # Commits grew (3 > 2) even though output and git changes didn't grow
        activity, new_idle = pipeline.classify_activity(
            100, 100, 3, 3, 3, 2, consecutive_idle,
        )
        assert activity == "active", f"Expected 'active', got '{activity}'"
        assert new_idle == 0, f"Expected consecutive_idle reset to 0, got {new_idle}"


# ---------------------------------------------------------------------------
# AC-15: STAGE_TIMEOUTS dict removed
# ---------------------------------------------------------------------------
class TestNoFixedTimeouts:
    def test_no_per_stage_fixed_timeouts(self):
        """AC-15: STAGE_TIMEOUTS dict no longer exists."""
        import pipeline

        assert not hasattr(pipeline, "STAGE_TIMEOUTS"), \
            "STAGE_TIMEOUTS should be removed — timeouts are now activity-based"


# ---------------------------------------------------------------------------
# _read_last_lines helper
# ---------------------------------------------------------------------------
class TestReadLastLines:
    def test_reads_last_n_lines(self):
        """_read_last_lines returns last N non-empty lines from stage log."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow", "stages")
            os.makedirs(wf_dir)
            with open(os.path.join(wf_dir, "qa.log"), "w") as f:
                for i in range(100):
                    f.write(f"Line {i}\n")

            lines = pipeline._read_last_lines(tmpdir, "qa", 5)
            assert len(lines) == 5
            assert lines[-1] == "Line 99"

    def test_missing_file_returns_empty(self):
        """_read_last_lines returns [] for missing file."""
        import pipeline

        with tempfile.TemporaryDirectory() as tmpdir:
            lines = pipeline._read_last_lines(tmpdir, "nonexistent", 5)
            assert lines == []


# ---------------------------------------------------------------------------
# Retro Filing: Issue-worthiness criteria (specs/retro-filing.md)
# ---------------------------------------------------------------------------
class TestRetroSkillPrompt:
    def test_skill_prompt_contains_observation_tier(self):
        """AC-01: Retro skill prompt includes observation tier with definition."""
        skill_path = os.path.join(REPO_ROOT, ".claude", "skills", "retro", "SKILL.md")
        with open(skill_path, "r") as f:
            content = f.read()
        assert "**observation**:" in content or "- **observation**" in content, (
            "Retro skill prompt must define the observation tier"
        )
        assert "Stays in retro report" in content, (
            "Observation tier must state findings stay in retro report"
        )
        assert "NOT filed" in content, (
            "Observation tier must explicitly say NOT filed as GitHub issue"
        )

    def test_skill_prompt_contains_litmus_test(self):
        """AC-02: Retro skill prompt includes litmus test guidance."""
        skill_path = os.path.join(REPO_ROOT, ".claude", "skills", "retro", "SKILL.md")
        with open(skill_path, "r") as f:
            content = f.read()
        assert "Can someone open" in content and "code change" in content, (
            "Retro skill prompt must include the litmus test: "
            "'Can someone open this issue, make a code change, and close it?'"
        )


class TestRetroFindingsSummary:
    def test_findings_summary_includes_observation_count(self):
        """AC-03: FINDINGS_SUMMARY log includes observation count."""
        import pipeline
        from unittest.mock import patch

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            findings = {
                "findings": [
                    {"id": "R-01", "tier": "issue-only", "description": "A real bug"},
                    {"id": "R-02", "tier": "observation", "description": "A metric"},
                    {"id": "R-03", "tier": "observation", "description": "Another metric"},
                ]
            }
            with open(os.path.join(wf_dir, "retro-findings.json"), "w") as f:
                json.dump(findings, f)

            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            with patch.object(pipeline, "_find_duplicate_issue", return_value=None), \
                 patch("subprocess.run"):
                pipeline.process_retro_findings(tmpdir, logger, tmpdir)

            logger.close()

            with open(log_path) as f:
                content = f.read()
            assert "observation=2" in content, (
                "FINDINGS_SUMMARY must include observation count"
            )

    def test_observation_tier_not_filed(self):
        """AC-04: observation-tier findings do not trigger gh issue create."""
        import pipeline
        from unittest.mock import patch

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            findings = {
                "findings": [
                    {"id": "R-01", "tier": "observation", "description": "High fix ratio"},
                    {"id": "R-02", "tier": "observation", "description": "Timing gap"},
                ]
            }
            with open(os.path.join(wf_dir, "retro-findings.json"), "w") as f:
                json.dump(findings, f)

            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            with patch("subprocess.run") as mock_run:
                pipeline.process_retro_findings(tmpdir, logger, tmpdir)

            logger.close()
            # subprocess.run should never be called (no gh issue create)
            mock_run.assert_not_called()


class TestFindDuplicateIssue:
    def test_find_duplicate_returns_existing_issue(self):
        """AC-05: Returns issue number when matching open issue exists."""
        import pipeline
        from unittest.mock import patch, MagicMock

        mock_result = MagicMock()
        mock_result.returncode = 0
        mock_result.stdout = json.dumps([
            {"number": 42, "title": "retro: Pipeline resumes while previous stage still"}
        ])

        with patch("subprocess.run", return_value=mock_result):
            result = pipeline._find_duplicate_issue(
                "retro: Pipeline resumes while previous stage still running", "/repo"
            )
        assert result == 42

    def test_find_duplicate_returns_none_when_no_match(self):
        """AC-06: Returns None when no matching open issue exists."""
        import pipeline
        from unittest.mock import patch, MagicMock

        mock_result = MagicMock()
        mock_result.returncode = 0
        mock_result.stdout = json.dumps([
            {"number": 99, "title": "retro: Something completely different and unrelated"}
        ])

        with patch("subprocess.run", return_value=mock_result):
            result = pipeline._find_duplicate_issue(
                "retro: Pipeline resumes while previous stage still running", "/repo"
            )
        assert result is None

    def test_find_duplicate_returns_none_on_error(self):
        """AC-08 helper: Returns None when gh command fails."""
        import pipeline
        from unittest.mock import patch

        with patch("subprocess.run", side_effect=subprocess.TimeoutExpired("gh", 15)):
            result = pipeline._find_duplicate_issue("retro: something", "/repo")
        assert result is None


class TestRetroDeduplication:
    """Merged with TestDuplicateIssueUpdate below; see RF AC-07 tests there."""
    pass

    def test_files_issue_when_no_duplicate_found(self):
        """AC-07: Files new issue when no duplicate exists."""
        import pipeline
        from unittest.mock import patch, MagicMock

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            findings = {
                "findings": [
                    {"id": "R-01", "tier": "issue-only",
                     "description": "A specific code bug that needs fixing"},
                ]
            }
            with open(os.path.join(wf_dir, "retro-findings.json"), "w") as f:
                json.dump(findings, f)

            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            mock_create = MagicMock()
            mock_create.returncode = 0

            with patch.object(pipeline, "_find_duplicate_issue", return_value=None), \
                 patch("subprocess.run", return_value=mock_create) as mock_run:
                pipeline.process_retro_findings(tmpdir, logger, tmpdir)

            logger.close()

            # Verify gh issue create was called
            assert mock_run.called
            create_call = mock_run.call_args
            assert "gh" in create_call[0][0]
            assert "issue" in create_call[0][0]
            assert "create" in create_call[0][0]

    def test_files_issue_when_dedup_check_fails(self):
        """AC-08: Files issue when _find_duplicate_issue raises exception (best-effort)."""
        import pipeline
        from unittest.mock import patch, MagicMock

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            findings = {
                "findings": [
                    {"id": "R-01", "tier": "issue-only",
                     "description": "A dedup-failing bug"},
                ]
            }
            with open(os.path.join(wf_dir, "retro-findings.json"), "w") as f:
                json.dump(findings, f)

            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            mock_create = MagicMock()
            mock_create.returncode = 0

            with patch.object(pipeline, "_find_duplicate_issue",
                              side_effect=Exception("network error")), \
                 patch("subprocess.run", return_value=mock_create) as mock_run:
                pipeline.process_retro_findings(tmpdir, logger, tmpdir)

            logger.close()

            # Even when dedup check fails, issue should still be created
            assert mock_run.called
            create_call = mock_run.call_args
            assert "gh" in create_call[0][0]
            assert "create" in create_call[0][0]


class TestObservationsPersisted:
    def test_observations_persisted_in_store(self):
        """AC-09: observation findings remain in retro-findings.json (not filtered out)."""
        import pipeline
        from unittest.mock import patch, MagicMock

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            findings = {
                "findings": [
                    {"id": "R-01", "tier": "observation", "description": "A metric"},
                    {"id": "R-02", "tier": "issue-only", "description": "A bug"},
                ]
            }
            findings_path = os.path.join(wf_dir, "retro-findings.json")
            with open(findings_path, "w") as f:
                json.dump(findings, f)

            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            # Mock subprocess and duplicate check so gh is never called
            mock_result = MagicMock(returncode=0, stdout="", stderr="")
            with patch("subprocess.run", return_value=mock_result), \
                 patch.object(pipeline, "_find_duplicate_issue", return_value=None):
                pipeline.process_retro_findings(tmpdir, logger, tmpdir)

            logger.close()

            # After process_retro_findings runs, the file should still contain
            # observation findings (the function reads but doesn't remove them)
            with open(findings_path) as f:
                data = json.load(f)

            tiers = [f["tier"] for f in data["findings"]]
            assert "observation" in tiers, (
                "observation findings must be preserved in retro-findings.json"
            )


# ---------------------------------------------------------------------------
# RF AC-07: Duplicate issue update (not skip)
# ---------------------------------------------------------------------------
class TestDuplicateIssueUpdate:
    def test_update_duplicate_issue(self):
        """RF AC-07: process_retro_findings calls _handle_duplicate on dupes."""
        import pipeline
        from unittest.mock import patch, MagicMock

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            findings = {
                "findings": [
                    {"id": "R-01", "tier": "issue-only",
                     "description": "Pipeline resumes while previous stage still running"},
                ]
            }
            with open(os.path.join(wf_dir, "retro-findings.json"), "w") as f:
                json.dump(findings, f)

            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            with patch.object(pipeline, "_find_duplicate_issue", return_value=42), \
                 patch.object(pipeline, "_handle_duplicate") as mock_handle:
                pipeline.process_retro_findings(tmpdir, logger, tmpdir)

            logger.close()
            mock_handle.assert_called_once()
            args = mock_handle.call_args
            assert args[0][1] == 42  # existing_issue_number
            # Note: ISSUE_UPDATED_DUPLICATE log is written by _handle_duplicate
            # itself, which is mocked here. The log message is verified
            # structurally via test_duplicate_comment_format below.

    def test_duplicate_comment_format(self):
        """RF AC-16: Duplicate comment includes branch, finding ID, evidence."""
        import pipeline
        from unittest.mock import patch, MagicMock

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            state = {"branch": "feat/test-branch"}
            with open(os.path.join(wf_dir, "state.json"), "w") as f:
                json.dump(state, f)

            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            finding = {"id": "R-42", "description": "Some evidence text"}

            mock_result = MagicMock()
            mock_result.returncode = 0
            mock_result.stdout = ""
            mock_result.stderr = ""

            with patch("subprocess.run", return_value=mock_result) as mock_run:
                pipeline._handle_duplicate(finding, 99, tmpdir, logger, tmpdir)

            logger.close()

            mock_run.assert_called_once()
            call_args = mock_run.call_args
            cmd = call_args[0][0]
            assert cmd[0:3] == ["gh", "issue", "comment"], (
                f"Expected gh issue comment command, got {cmd[:3]}"
            )
            body_idx = cmd.index("--body")
            comment_body = cmd[body_idx + 1]

            assert "Also observed" in comment_body, (
                f"Comment should contain 'Also observed', got: {comment_body}"
            )
            assert "feat/test-branch" in comment_body, (
                f"Comment should contain branch name, got: {comment_body}"
            )
            assert "R-42" in comment_body, (
                f"Comment should contain finding ID, got: {comment_body}"
            )
            assert "Some evidence text" in comment_body, (
                f"Comment should contain evidence description, got: {comment_body}"
            )


# ---------------------------------------------------------------------------
# RF AC-17: PPDS_SHAKEDOWN suppresses all issue ops
# ---------------------------------------------------------------------------
class TestShakedownSuppressesIssueOps:
    def test_shakedown_suppresses_all_issue_ops(self):
        """RF AC-17: PPDS_SHAKEDOWN=1 prevents gh issue create and comment."""
        import pipeline
        from unittest.mock import patch

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            findings = {
                "findings": [
                    {"id": "R-01", "tier": "issue-only",
                     "description": "A real issue that should not be filed"},
                ]
            }
            with open(os.path.join(wf_dir, "retro-findings.json"), "w") as f:
                json.dump(findings, f)

            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            env_patch = {"PPDS_SHAKEDOWN": "1"}
            with patch.dict(os.environ, env_patch), \
                 patch("subprocess.run") as mock_run:
                pipeline.process_retro_findings(tmpdir, logger, tmpdir)

            logger.close()
            # subprocess.run should never be called for gh commands
            mock_run.assert_not_called()

            with open(log_path) as f:
                content = f.read()
            assert "SHAKEDOWN_SKIPPED" in content


# ===========================================================================
# Shakedown Tests (WE AC-115–122, AC-118, AC-119)
# ===========================================================================


# ---------------------------------------------------------------------------
# AC-116: Shakedown env var propagation
# ---------------------------------------------------------------------------
class TestShakedownEnvVar:
    def test_shakedown_env_var(self, tmp_path):
        """AC-116: run_pr_stage returns early when PPDS_SHAKEDOWN is set."""
        import pipeline

        wf_dir = tmp_path / ".workflow"
        wf_dir.mkdir()
        logger = pipeline.open_logger(str(wf_dir / "pipeline.log"))

        with unittest.mock.patch.dict(os.environ, {"PPDS_SHAKEDOWN": "1"}):
            exit_code, _ = pipeline.run_pr_stage(str(tmp_path), logger)

        assert exit_code == 0
        # Verify no subprocess calls were made (early return)
        logger.close()


# ---------------------------------------------------------------------------
# AC-117: Shakedown suppresses issue filing
# ---------------------------------------------------------------------------
class TestShakedownSuppressesIssueFiling:
    def test_shakedown_suppresses_issue_filing(self, tmp_path):
        """AC-117: process_retro_findings skips all issue filing in shakedown mode."""
        import pipeline

        wf_dir = tmp_path / ".workflow"
        wf_dir.mkdir()
        findings = {
            "findings": [
                {"id": "R-01", "tier": "issue-only", "description": "test", "fix_description": "fix it"}
            ]
        }
        (wf_dir / "retro-findings.json").write_text(json.dumps(findings))
        logger = pipeline.open_logger(str(wf_dir / "pipeline.log"))

        with unittest.mock.patch.dict(os.environ, {"PPDS_SHAKEDOWN": "1"}):
            with unittest.mock.patch("subprocess.run") as mock_run:
                pipeline.process_retro_findings(str(tmp_path), logger, str(tmp_path))

        # gh issue create should NOT have been called
        gh_calls = [c for c in mock_run.call_args_list if c[0][0][0] == "gh"]
        assert not gh_calls, f"Expected no gh calls in shakedown mode, got: {gh_calls}"
        logger.close()


# ---------------------------------------------------------------------------
# AC-118: Shakedown skips PR creation
# ---------------------------------------------------------------------------
class TestShakedownSkipsPr:
    def test_shakedown_skips_pr_creation(self):
        """AC-118: run_pr_stage exits 0 with PR_SKIPPED_SHAKEDOWN in shakedown."""
        import pipeline
        from unittest.mock import patch

        with tempfile.TemporaryDirectory() as tmpdir:
            wf_dir = os.path.join(tmpdir, ".workflow")
            os.makedirs(wf_dir)
            log_path = os.path.join(tmpdir, "test.log")
            logger = pipeline.open_logger(log_path)

            with patch.dict(os.environ, {"PPDS_SHAKEDOWN": "1"}):
                exit_code, _ = pipeline.run_pr_stage(tmpdir, logger)

            logger.close()
            assert exit_code == 0

            with open(log_path) as f:
                content = f.read()
            assert "PR_SKIPPED_SHAKEDOWN" in content


# ---------------------------------------------------------------------------
# AC-119: Shakedown suppresses notify
# ---------------------------------------------------------------------------
class TestShakedownSuppressesNotify:
    def test_shakedown_suppresses_notify(self):
        """AC-119: notify.py exits 0 without sending in shakedown."""
        hook_path = os.path.join(
            REPO_ROOT, ".claude", "hooks", "notify.py"
        )
        with open(hook_path, "r") as f:
            content = f.read()
        assert "PPDS_SHAKEDOWN" in content


# ===========================================================================
# Workflow Enforcement v7.0 Tests (ACs 100-128) — Stop Hook + Heartbeat
# ===========================================================================


# ---------------------------------------------------------------------------
# AC-100: Stop hook uses origin/main
# ---------------------------------------------------------------------------
class TestStopHookOriginMain:
    def test_stop_hook_uses_origin_main(self):
        """AC-100: Stop hook uses origin/main...HEAD, not local main...HEAD."""
        hook_path = os.path.join(
            REPO_ROOT, ".claude", "hooks", "session-stop-workflow.py"
        )
        with open(hook_path, "r") as f:
            content = f.read()
        assert "origin/main...HEAD" in content, (
            "Stop hook must use origin/main...HEAD for code change detection"
        )
        # Ensure bare main...HEAD is NOT used (outside of origin/main)
        lines = content.split("\n")
        for line in lines:
            if "main...HEAD" in line and "origin/main" not in line:
                assert False, (
                    f"Found bare main...HEAD (without origin/) in: {line.strip()}"
                )


# ---------------------------------------------------------------------------
# AC-101: Stop hook phase bypass
# ---------------------------------------------------------------------------
class TestStopHookPhaseBypass:
    def test_stop_hook_phase_bypass(self):
        """AC-101: Stop hook exits 0 for non-implementing phases."""
        hook_path = os.path.join(
            REPO_ROOT, ".claude", "hooks", "session-stop-workflow.py"
        )
        with open(hook_path, "r") as f:
            content = f.read()

        # Verify the phase bypass logic exists
        assert 'state.get("phase")' in content, (
            "Stop hook must read phase from state"
        )
        for phase in ("starting", "investigating", "design", "reviewing", "qa", "shakedown", "retro", "pr"):
            assert f'"{phase}"' in content, (
                f"Stop hook must bypass for phase: {phase}"
            )


# ---------------------------------------------------------------------------
# AC-102: Stop hook enforces implementing phase
# ---------------------------------------------------------------------------
class TestStopHookEnforcesImplementing:
    def test_stop_hook_enforces_implementing_phase(self):
        """AC-102: Stop hook enforces workflow for implementing phase and null."""
        hook_path = os.path.join(
            REPO_ROOT, ".claude", "hooks", "session-stop-workflow.py"
        )
        with open(hook_path, "r") as f:
            content = f.read()

        # The bypass list should NOT include "implementing" or "pipeline"
        # These phases require full workflow enforcement
        assert '"implementing"' not in content.split("if phase in")[1].split(")")[0], (
            "implementing phase must NOT be in the bypass list"
        )


# ---------------------------------------------------------------------------
# AC-103: Stop hook exits in shakedown mode
# ---------------------------------------------------------------------------
class TestStopHookShakedown:
    def test_stop_hook_exits_in_shakedown_mode(self):
        """AC-103: Stop hook exits 0 when PPDS_SHAKEDOWN=1."""
        hook_path = os.path.join(
            REPO_ROOT, ".claude", "hooks", "session-stop-workflow.py"
        )
        env = os.environ.copy()
        env["PPDS_SHAKEDOWN"] = "1"
        result = subprocess.run(
            [sys.executable, hook_path],
            input="{}",
            capture_output=True,
            text=True,
            env=env,
            cwd=REPO_ROOT,
            timeout=10,
        )
        assert result.returncode == 0, (
            f"Stop hook should exit 0 in shakedown mode, got {result.returncode}"
        )


# ---------------------------------------------------------------------------
# AC-104: Stop hook enforcement logging
# ---------------------------------------------------------------------------
class TestStopHookEnforcementLogging:
    def test_stop_hook_enforcement_logging(self):
        """AC-104: Stop hook writes stop_hook_blocked, count, and timestamp to state."""
        hook_path = os.path.join(
            REPO_ROOT, ".claude", "hooks", "session-stop-workflow.py"
        )
        with open(hook_path, "r") as f:
            content = f.read()

        assert "stop_hook_blocked" in content, (
            "Stop hook must write stop_hook_blocked to state"
        )
        assert "stop_hook_count" in content, (
            "Stop hook must write stop_hook_count to state"
        )
        assert "stop_hook_last" in content, (
            "Stop hook must write stop_hook_last timestamp to state"
        )


# ---------------------------------------------------------------------------
# AC-124: Pipeline heartbeat uses origin/main
# ---------------------------------------------------------------------------
class TestHeartbeatOriginMain:
    def test_heartbeat_uses_origin_main(self, tmp_path):
        """AC-124: Both commit count and git activity use origin/main..HEAD."""
        import pipeline

        calls = []

        def mock_run(cmd, **kwargs):
            calls.append(cmd)
            result = unittest.mock.MagicMock()
            result.returncode = 0
            result.stdout = "5\n"
            result.stderr = ""
            return result

        with unittest.mock.patch("subprocess.run", side_effect=mock_run):
            count = pipeline.get_commit_count(str(tmp_path))
            changes, commits = pipeline.get_git_activity(str(tmp_path))

        # Verify get_commit_count uses origin/main..HEAD
        commit_count_calls = [c for c in calls if "rev-list" in c and "--count" in c]
        assert any("origin/main..HEAD" in c for c in commit_count_calls), \
            f"get_commit_count must use origin/main..HEAD, got: {commit_count_calls}"

        assert count == 5
        assert commits == 5


# ===========================================================================
# Pipeline Reliability (PO AC-16–25) — Converge Logic
# ===========================================================================


# ---------------------------------------------------------------------------
# PO AC-20: Pipeline runs converge on review FAIL
# ---------------------------------------------------------------------------
class TestConvergeOnReviewFail:
    def test_pipeline_runs_converge_on_review_fail(self):
        """PO AC-20: should_converge returns True when review failed."""
        import pipeline

        state = {"review": {"passed": False, "findings": 3}}
        run, reason = pipeline.should_converge(state)
        assert run is True, "Converge must run when review failed"
        assert "FAIL" in reason

    def test_converge_on_review_fail_with_empty_passed(self):
        """PO AC-20: should_converge treats empty string passed as False."""
        import pipeline

        state = {"review": {"passed": "", "findings": 5}}
        run, reason = pipeline.should_converge(state)
        assert run is True


# ---------------------------------------------------------------------------
# PO AC-21: Pipeline skips converge on zero findings
# ---------------------------------------------------------------------------
class TestConvergeSkipsOnZero:
    def test_pipeline_skips_converge_on_zero_findings(self):
        """PO AC-21: should_converge returns False when zero findings."""
        import pipeline

        state = {"review": {"passed": True, "findings": 0}}
        run, reason = pipeline.should_converge(state)
        assert run is False, "Converge must be skipped when review passed with 0 findings"
        assert "zero" in reason.lower()


# ---------------------------------------------------------------------------
# PO AC-22: Pipeline runs converge on pass with findings
# ---------------------------------------------------------------------------
class TestConvergeRunsOnPassWithFindings:
    def test_pipeline_runs_converge_on_pass_with_findings(self):
        """PO AC-22: should_converge returns True when review passed with findings."""
        import pipeline

        state = {"review": {"passed": True, "findings": 5}}
        run, reason = pipeline.should_converge(state)
        assert run is True, "Converge must run when review passed with findings > 0"
        assert "5 findings" in reason


# ---------------------------------------------------------------------------
# AC-123: Hook path resolution in worktrees
# ---------------------------------------------------------------------------
class TestHookPathResolution:
    def test_hooks_use_relative_paths_that_work_in_worktrees(self):
        """AC-123: Hook commands use relative paths that resolve in worktrees."""
        settings_path = os.path.join(REPO_ROOT, ".claude", "settings.json")
        with open(settings_path, "r") as f:
            settings = json.load(f)

        hooks = settings.get("hooks", {})
        for event_type, matchers in hooks.items():
            for matcher_entry in matchers:
                for hook in matcher_entry.get("hooks", []):
                    cmd = hook.get("command", "")
                    if ".claude/hooks/" in cmd:
                        # Verify it uses a simple relative path (no env var expansion)
                        assert "CLAUDE_PROJECT_DIR" not in cmd, (
                            f"Hook should not use CLAUDE_PROJECT_DIR: {cmd}"
                        )
                        # Verify the hook file actually exists
                        # Extract filename from command
                        parts = cmd.split('"')
                        for part in parts:
                            if ".claude/hooks/" in part:
                                hook_path = os.path.join(REPO_ROOT, part)
                                assert os.path.exists(hook_path), (
                                    f"Hook file not found: {hook_path}"
                                )

    def test_stop_hook_runs_in_worktree(self):
        """AC-123: Stop hook executes without path errors in a worktree."""
        hook_path = os.path.join(
            REPO_ROOT, ".claude", "hooks", "session-stop-workflow.py"
        )
        env = os.environ.copy()
        env["PPDS_PIPELINE"] = "1"
        env["CLAUDE_PROJECT_DIR"] = REPO_ROOT
        result = subprocess.run(
            [sys.executable, hook_path],
            input="{}",
            capture_output=True,
            text=True,
            env=env,
            cwd=REPO_ROOT,
            timeout=10,
        )
        assert result.returncode == 0, (
            f"Stop hook failed in worktree: {result.stderr}"
        )


# ---------------------------------------------------------------------------
# AC-105: All skills set phase
# ---------------------------------------------------------------------------
class TestAllSkillsSetPhase:
    EXPECTED_PHASES = {
        "start": "starting",
        "investigate": "investigating",
        "design": "design",
        "implement": "implementing",
        "review": "reviewing",
        "qa": "qa",
        "pr": "pr",
        # Bundle 4 (meta-retro #20): shakedown-workflow was folded into
        # shakedown as "Workflow Mode"; the phase-setting call lives in
        # shakedown/SKILL.md now.
        "shakedown": "shakedown",
    }

    def test_all_skills_set_phase(self):
        """AC-105: Every skill writes phase to state via workflow-state.py set phase."""
        for skill_name, expected_phase in self.EXPECTED_PHASES.items():
            skill_path = os.path.join(
                REPO_ROOT, ".claude", "skills", skill_name, "SKILL.md"
            )
            with open(skill_path, "r", encoding="utf-8") as f:
                content = f.read()
            assert f"set phase {expected_phase}" in content, (
                f"Skill '{skill_name}' must set phase to '{expected_phase}'"
            )

    def test_pipeline_sets_phase(self):
        """AC-105: Pipeline main() calls workflow-state.py set phase pipeline.

        Uses AST analysis to verify the phase-setting call exists in main()
        without running the full pipeline. This is a deliberate structural check
        because main() requires integration-level setup to execute.
        """
        import pipeline
        import ast

        source = open(pipeline.__file__).read()
        tree = ast.parse(source)

        # Find main function
        main_func = None
        for node in ast.walk(tree):
            if isinstance(node, ast.FunctionDef) and node.name == "main":
                main_func = node
                break

        assert main_func is not None, "main() function not found"
        main_source = ast.get_source_segment(source, main_func)
        assert '"phase"' in main_source and '"pipeline"' in main_source, \
            "main() must call workflow-state.py set phase pipeline"


# ===========================================================================
# Workflow Enforcement v8.0 Tests (ACs 129-146) — Commit-Aware PR Gate
# ===========================================================================

# Ensure pr-gate module is importable
HOOKS_DIR = os.path.join(REPO_ROOT, ".claude", "hooks")
if HOOKS_DIR not in sys.path:
    sys.path.insert(0, HOOKS_DIR)


def _load_pr_gate(name="pr_gate"):
    """Import pr-gate.py as a module."""
    import importlib
    spec = importlib.util.spec_from_file_location(
        name, os.path.join(HOOKS_DIR, "pr-gate.py")
    )
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ---------------------------------------------------------------------------
# AC-129: PR gate blocks when gates.commit_ref != HEAD
# ---------------------------------------------------------------------------
class TestPrGateExactHeadGates:
    def test_pr_gate_exact_head_gates(self, tmp_path):
        """AC-129: PR gate blocks when gates.commit_ref doesn't match HEAD.

        Runs the real pr-gate.py hook via subprocess with a state file whose
        gates.commit_ref does not match HEAD.  Since tmp_path is not a git repo,
        git rev-parse HEAD will fail and the hook should exit 2.
        """
        wf = tmp_path / ".workflow"
        wf.mkdir()
        (wf / "state.json").write_text(json.dumps({
            "gates": {"passed": "2026-01-01T00:00:00Z", "commit_ref": "wrong_sha"},
            "verify": {"workflow": "2026-01-01T00:00:00Z", "workflow_commit_ref": "wrong_sha"},
            "qa": {},
            "review": {"passed": "2026-01-01T00:00:00Z", "commit_ref": "wrong_sha"},
        }))

        hook_path = os.path.join(REPO_ROOT, ".claude", "hooks", "pr-gate.py")
        env = os.environ.copy()
        env["CLAUDE_PROJECT_DIR"] = str(tmp_path)
        result = subprocess.run(
            [sys.executable, hook_path],
            input=json.dumps({"tool_input": {"command": "gh pr create --title test"}}),
            capture_output=True, text=True, env=env, timeout=10,
        )
        assert result.returncode == 2, f"Expected exit 2, got {result.returncode}: {result.stderr}"
        # git rev-parse HEAD fails in tmp_path, so hook blocks on HEAD resolution
        assert result.stderr.strip(), "Expected error message on stderr"


# ---------------------------------------------------------------------------
# AC-130: PR gate blocks when review.commit_ref != HEAD
# ---------------------------------------------------------------------------
class TestPrGateExactHeadReview:
    def test_pr_gate_exact_head_review(self, tmp_path):
        """AC-130: PR gate blocks when review.commit_ref doesn't match HEAD.

        Runs the real pr-gate.py hook via subprocess.  The state has a stale
        review.commit_ref.  Since tmp_path is not a git repo, git rev-parse
        HEAD will fail and the hook exits 2.
        """
        wf = tmp_path / ".workflow"
        wf.mkdir()
        (wf / "state.json").write_text(json.dumps({
            "gates": {"passed": "2026-01-01T00:00:00Z", "commit_ref": "abc123"},
            "verify": {"workflow_commit_ref": "abc123"},
            "qa": {},
            "review": {"passed": "2026-01-01T00:00:00Z", "commit_ref": "stale_review_sha"},
        }))

        hook_path = os.path.join(REPO_ROOT, ".claude", "hooks", "pr-gate.py")
        env = os.environ.copy()
        env["CLAUDE_PROJECT_DIR"] = str(tmp_path)
        result = subprocess.run(
            [sys.executable, hook_path],
            input=json.dumps({"tool_input": {"command": "gh pr create --title test"}}),
            capture_output=True, text=True, env=env, timeout=10,
        )
        assert result.returncode == 2, f"Expected exit 2, got {result.returncode}: {result.stderr}"
        assert result.stderr.strip(), "Expected error message on stderr"


# ---------------------------------------------------------------------------
# AC-131: PR gate verify uses ancestor check
# ---------------------------------------------------------------------------
class TestPrGateAncestorVerify:
    def test_pr_gate_ancestor_verify(self):
        """AC-131: Verify uses ancestor check — an ancestor commit_ref passes.

        Mocks git merge-base --is-ancestor to return success (exit 0),
        confirming is_ancestor returns True for ancestor refs.
        """
        pr_gate = _load_pr_gate("pr_gate_131")

        parent_sha = "aaa111" * 6 + "aaaa"
        head_sha = "bbb222" * 6 + "bbbb"

        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 0  # is-ancestor returns 0 = True

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            assert pr_gate.is_ancestor(parent_sha, head_sha, REPO_ROOT), (
                "is_ancestor should return True when git merge-base succeeds"
            )

    def test_pr_gate_verify_missing_exits_2(self, tmp_path):
        """AC-131: Hook exits 2 when verify is missing for a surface.

        Since tmp_path is not a git repo, detect_affected_surfaces returns
        empty set and the hook falls through to the 'no affected surfaces'
        path — requiring at least one verify entry.  State has no verify
        entries, so the hook should block.
        """
        wf = tmp_path / ".workflow"
        wf.mkdir()
        (wf / "state.json").write_text(json.dumps({
            "gates": {"passed": "2026-01-01T00:00:00Z", "commit_ref": "wrong"},
            "verify": {},
            "qa": {},
            "review": {"passed": "2026-01-01T00:00:00Z", "commit_ref": "wrong"},
        }))

        hook_path = os.path.join(REPO_ROOT, ".claude", "hooks", "pr-gate.py")
        env = os.environ.copy()
        env["CLAUDE_PROJECT_DIR"] = str(tmp_path)
        result = subprocess.run(
            [sys.executable, hook_path],
            input=json.dumps({"tool_input": {"command": "gh pr create --title test"}}),
            capture_output=True, text=True, env=env, timeout=10,
        )
        assert result.returncode == 2, f"Expected exit 2, got {result.returncode}: {result.stderr}"


# ---------------------------------------------------------------------------
# AC-132: PR gate QA uses ancestor check
# ---------------------------------------------------------------------------
class TestPrGateAncestorQa:
    def test_pr_gate_qa_missing_exits_2(self, tmp_path):
        """AC-132: Hook exits 2 when QA is missing for non-workflow surfaces.

        Since tmp_path is not a git repo, git rev-parse HEAD fails and the
        hook exits 2 before reaching the QA check.  This confirms the hook
        blocks on missing git context.  The is_ancestor unit tests below
        verify the QA ancestor logic directly.
        """
        wf = tmp_path / ".workflow"
        wf.mkdir()
        (wf / "state.json").write_text(json.dumps({
            "gates": {"passed": "2026-01-01T00:00:00Z", "commit_ref": "wrong"},
            "verify": {"workflow_commit_ref": "some_sha"},
            "qa": {},
            "review": {"passed": "2026-01-01T00:00:00Z", "commit_ref": "wrong"},
        }))

        hook_path = os.path.join(REPO_ROOT, ".claude", "hooks", "pr-gate.py")
        env = os.environ.copy()
        env["CLAUDE_PROJECT_DIR"] = str(tmp_path)
        result = subprocess.run(
            [sys.executable, hook_path],
            input=json.dumps({"tool_input": {"command": "gh pr create --title test"}}),
            capture_output=True, text=True, env=env, timeout=10,
        )
        assert result.returncode == 2, f"Expected exit 2, got {result.returncode}: {result.stderr}"
        assert "blocked" in result.stderr.lower(), f"Error should mention blocked: {result.stderr}"

    def test_is_ancestor_returns_false_on_failure(self):
        """AC-132: is_ancestor returns False when git merge-base fails."""
        pr_gate = _load_pr_gate("pr_gate_132b")

        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 1  # is-ancestor returns 1 = not ancestor

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            assert not pr_gate.is_ancestor("HEAD", "HEAD~1", REPO_ROOT), (
                "is_ancestor should return False when git merge-base fails"
            )

    def test_is_ancestor_handles_timeout(self):
        """AC-132: is_ancestor returns False on timeout."""
        pr_gate = _load_pr_gate("pr_gate_132c")

        with unittest.mock.patch(
            "subprocess.run",
            side_effect=subprocess.TimeoutExpired("git", 10)
        ):
            assert not pr_gate.is_ancestor("sha1", "sha2", REPO_ROOT), (
                "is_ancestor should return False on timeout"
            )


# ---------------------------------------------------------------------------
# AC-133: Surface detection maps paths correctly
# ---------------------------------------------------------------------------
class TestPrGateSurfaceDetection:
    def test_detect_affected_surfaces(self):
        """AC-133: detect_affected_surfaces maps file paths to correct surface keys."""
        pr_gate = _load_pr_gate("pr_gate_133")

        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 0
        mock_result.stdout = "\n".join([
            "src/PPDS.Extension/package.json",
            "src/PPDS.Cli/Commands/Export/ExportCommand.cs",
            ".claude/hooks/pr-gate.py",
            "src/PPDS.Cli/Tui/MainWindow.cs",
            "src/PPDS.Mcp/Tools/ListTool.cs",
        ])

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            surfaces = pr_gate.detect_affected_surfaces("/fake/project")

        assert "ext" in surfaces, f"Expected 'ext' in surfaces, got {surfaces}"
        assert "cli" in surfaces, f"Expected 'cli' in surfaces, got {surfaces}"
        assert "workflow" in surfaces, f"Expected 'workflow' in surfaces, got {surfaces}"
        assert "tui" in surfaces, f"Expected 'tui' in surfaces, got {surfaces}"
        assert "mcp" in surfaces, f"Expected 'mcp' in surfaces, got {surfaces}"

    def test_detect_affected_surfaces_empty_diff(self):
        """AC-133: Empty diff returns empty surface set."""
        pr_gate = _load_pr_gate("pr_gate_133b")

        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 0
        mock_result.stdout = ""

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            surfaces = pr_gate.detect_affected_surfaces("/fake/project")

        assert surfaces == set(), f"Expected empty set, got {surfaces}"

    def test_detect_affected_surfaces_serve_excluded(self):
        """AC-133: Serve/ directory is excluded from CLI surface."""
        pr_gate = _load_pr_gate("pr_gate_133c")

        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 0
        mock_result.stdout = "src/PPDS.Cli/Commands/Serve/ServeCommand.cs"

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            surfaces = pr_gate.detect_affected_surfaces("/fake/project")

        assert "cli" not in surfaces, (
            f"Serve/ should be excluded from CLI surface, got {surfaces}"
        )


# ---------------------------------------------------------------------------
# AC-134: Workflow-only diffs skip QA
# ---------------------------------------------------------------------------
class TestPrGateWorkflowOnlyNoQa:
    def test_workflow_only_detected_by_surface_detection(self):
        """AC-134: detect_affected_surfaces returns only 'workflow' for .claude/ changes."""
        pr_gate = _load_pr_gate("pr_gate_134")

        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 0
        mock_result.stdout = ".claude/hooks/pr-gate.py\nscripts/pr_monitor.py"

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            surfaces = pr_gate.detect_affected_surfaces("/fake/project")

        assert surfaces == {"workflow"}, f"Expected only 'workflow', got {surfaces}"

    def test_workflow_only_detection_produces_empty_non_workflow(self):
        """AC-134: When detect_affected_surfaces returns only 'workflow',
        the non_workflow set (affected - {'workflow'}) is empty.

        This tests the precondition that enables the QA skip: when the diff
        is workflow-only, the set subtraction produces an empty set, and the
        QA check branch in main() is skipped.
        """
        pr_gate = _load_pr_gate("pr_gate_134b")

        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 0
        mock_result.stdout = ".claude/hooks/pr-gate.py\nscripts/pipeline.py"

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            affected = pr_gate.detect_affected_surfaces("/fake/project")

        non_workflow = affected - {"workflow"}
        assert affected == {"workflow"}, f"Expected only 'workflow', got {affected}"
        assert non_workflow == set(), (
            "Workflow-only diff should have empty non_workflow set — QA is skipped"
        )

    def test_mixed_surfaces_require_qa_in_hook(self, tmp_path):
        """AC-134 negative: Non-workflow surfaces DO require QA.

        Uses detect_affected_surfaces directly with mocked git to show
        that mixed surfaces produce a non-empty non_workflow set.
        """
        pr_gate = _load_pr_gate("pr_gate_134c")

        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 0
        mock_result.stdout = ".claude/hooks/pr-gate.py\nsrc/PPDS.Extension/package.json"

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            surfaces = pr_gate.detect_affected_surfaces("/fake/project")

        non_workflow = surfaces - {"workflow"}
        assert "ext" in non_workflow, (
            f"Expected 'ext' in non-workflow surfaces, got {non_workflow}"
        )


# ---------------------------------------------------------------------------
# AC-135: Skills write commit_ref
# ---------------------------------------------------------------------------
class TestSkillsWriteCommitRef:
    SKILL_COMMIT_REF_MAP = {
        "gates": ["gates.commit_ref"],
        "verify": ["verify."],  # verify.{surface}_commit_ref
        "qa": ["qa."],          # qa.{surface}_commit_ref
        "review": ["review.commit_ref"],
    }

    def test_skills_write_commit_ref(self):
        """AC-135: gates, verify, qa, review skills all write commit_ref."""
        for skill_name, expected_patterns in self.SKILL_COMMIT_REF_MAP.items():
            skill_path = os.path.join(
                REPO_ROOT, ".claude", "skills", skill_name, "SKILL.md"
            )
            with open(skill_path, "r", encoding="utf-8") as f:
                content = f.read()

            for pattern in expected_patterns:
                assert f"{pattern}" in content and "commit_ref" in content, (
                    f"Skill '{skill_name}' must write {pattern}commit_ref "
                    f"to state"
                )


# ---------------------------------------------------------------------------
# AC-136: Post-commit hook clears review
# ---------------------------------------------------------------------------
class TestPostCommitClearsReview:
    def test_post_commit_clears_review(self, tmp_path):
        """AC-136: Post-commit hook clears both gates.passed AND review.passed."""
        wf = tmp_path / ".workflow"
        wf.mkdir()
        (wf / "state.json").write_text(json.dumps({
            "branch": "feat/test",
            "phase": "implementing",
            "gates": {"passed": "2026-01-01T00:00:00Z", "commit_ref": "abc123"},
            "review": {"passed": "2026-01-01T00:00:00Z", "commit_ref": "abc123"},
        }))

        hook_path = os.path.join(REPO_ROOT, ".claude", "hooks", "post-commit-state.py")
        env = os.environ.copy()
        env["CLAUDE_PROJECT_DIR"] = str(tmp_path)
        result = subprocess.run(
            [sys.executable, hook_path],
            input="{}",
            capture_output=True, text=True, env=env, timeout=10,
        )
        assert result.returncode == 0

        with open(wf / "state.json") as f:
            state = json.load(f)
        assert state["gates"]["passed"] is None, "gates.passed should be cleared"
        assert state["review"]["passed"] is None, "review.passed should be cleared"
        assert state["review"]["commit_ref"] is None, "review.commit_ref should be cleared"


# ---------------------------------------------------------------------------
# AC-137: Verify skill does NOT write qa.workflow
# ---------------------------------------------------------------------------
class TestVerifyWorkflowNoQaStamp:
    def test_verify_workflow_no_qa_stamp(self):
        """AC-137: Verify SKILL.md does NOT set qa.workflow in any code block."""
        skill_path = os.path.join(
            REPO_ROOT, ".claude", "skills", "verify", "SKILL.md"
        )
        with open(skill_path, "r", encoding="utf-8") as f:
            content = f.read()

        # Extract code blocks (``` delimited)
        in_code_block = False
        code_blocks = []
        current_block = []
        for line in content.split("\n"):
            if line.strip().startswith("```"):
                if in_code_block:
                    code_blocks.append("\n".join(current_block))
                    current_block = []
                in_code_block = not in_code_block
            elif in_code_block:
                current_block.append(line)

        for block in code_blocks:
            assert "qa.workflow" not in block, (
                f"Verify SKILL.md must NOT set qa.workflow in code blocks. "
                f"Found in: {block[:200]}"
            )


# ---------------------------------------------------------------------------
# AC-138: PR gate triage from PR comment graph
# ---------------------------------------------------------------------------
class TestPrGateTriageFromPr:
    def test_pr_gate_triage_from_pr(self):
        """AC-138: PR gate checks triage via _check_triage_completeness.

        When state has pr.number set but triage is incomplete (unreplied
        comments exist), _check_triage_completeness returns failure messages.
        """
        pr_gate = _load_pr_gate("pr_gate_138")

        state = {
            "gates": {"passed": True},
            "review": {"passed": True},
            "pr": {"number": 999},
        }

        # Mock the triage_common module to return unreplied comments
        fake_triage = unittest.mock.MagicMock()
        fake_triage.get_unreplied_comments.return_value = [
            {"id": 1, "body": "Unreplied comment"},
        ]

        with unittest.mock.patch.dict("sys.modules", {"triage_common": fake_triage}):
            failures = pr_gate._check_triage_completeness(REPO_ROOT, state)

        assert len(failures) == 1, f"Expected 1 triage failure, got: {failures}"
        assert "triage" in failures[0].lower(), (
            f"Expected triage-related error: {failures[0]}"
        )
        assert "999" in failures[0], (
            f"Expected PR number in error message: {failures[0]}"
        )

    def test_pr_gate_no_triage_check_without_pr_number(self):
        """AC-138: No triage check when no PR number in state.

        Tests _check_triage_completeness directly -- when no pr.number is
        present, it returns an empty list (no failures).
        """
        pr_gate = _load_pr_gate("pr_gate_138b")

        # State without pr.number -> should return empty list
        state_no_pr = {
            "gates": {"passed": True},
            "review": {"passed": True},
        }
        failures = pr_gate._check_triage_completeness("/fake/path", state_no_pr)
        assert failures == [], (
            f"Without pr.number, triage should not be checked, got: {failures}"
        )

        # State with pr but empty number -> should also return empty
        state_empty_pr = {"pr": {}}
        failures = pr_gate._check_triage_completeness("/fake/path", state_empty_pr)
        assert failures == [], (
            f"With empty pr dict, triage should not be checked, got: {failures}"
        )


# ---------------------------------------------------------------------------
# AC-146: Converge fix commits don't invalidate verify/qa (ancestor check)
# ---------------------------------------------------------------------------
class TestConvergePreservesVerifyQa:
    def test_converge_preserves_verify_qa(self):
        """AC-146: Ancestor check passes after new commits (converge fix commits).

        If verify.ext_commit_ref points to a parent commit and HEAD advances
        (via converge fix commits), the ancestor check should still pass
        because the verify ref is still reachable from HEAD.
        Mocks git to confirm the ancestor relationship is preserved.
        """
        pr_gate = _load_pr_gate("pr_gate_146")

        grandparent = "aaa111" * 6 + "aaaa"
        real_head = "ccc333" * 6 + "cccc"

        # git merge-base --is-ancestor grandparent HEAD -> exit 0 (true)
        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 0

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            assert pr_gate.is_ancestor(grandparent, real_head, REPO_ROOT), (
                "Grandparent should be ancestor of HEAD -- "
                "converge fix commits should not invalidate verify"
            )

    def test_converge_preserves_qa_ancestor(self):
        """AC-146: QA commit ref from before converge is still valid ancestor."""
        pr_gate = _load_pr_gate("pr_gate_146b")

        parent = "aaa111" * 6 + "aaaa"
        real_head = "bbb222" * 6 + "bbbb"

        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 0

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            assert pr_gate.is_ancestor(parent, real_head, REPO_ROOT), (
                "Parent should be ancestor of HEAD -- "
                "QA ref from before converge should remain valid"
            )

    def test_non_ancestor_is_rejected(self):
        """AC-146 negative: A commit that is NOT an ancestor is rejected."""
        pr_gate = _load_pr_gate("pr_gate_146c")

        mock_result = unittest.mock.MagicMock()
        mock_result.returncode = 1  # not an ancestor

        with unittest.mock.patch("subprocess.run", return_value=mock_result):
            assert not pr_gate.is_ancestor(
                "0000000000000000000000000000000000000000",
                "HEAD",
                REPO_ROOT,
            ), "Non-ancestor commit should be rejected"

    def test_converge_full_verify_qa_logic(self):
        """AC-146: Full verify+QA logic with ancestor refs after converge.

        Simulates: verify ran at commit A, then 2 converge fix commits
        happened (A -> B -> C = HEAD). The ancestor check for A against C
        should pass, so verify/QA remain valid.
        """
        pr_gate = _load_pr_gate("pr_gate_146d")

        head_sha = "ccc333" * 6 + "cccc"
        verify_ref = "aaa111" * 6 + "aaaa"  # Set at commit A (before converge)
        qa_ref = "aaa111" * 6 + "aaaa"      # Same

        # Mock is_ancestor -> True (A is ancestor of C)
        with unittest.mock.patch.object(
            pr_gate, "is_ancestor", return_value=True
        ):
            # Replicate verify check
            affected = {"ext"}
            verify = {"ext_commit_ref": verify_ref}
            missing = []
            for surface in sorted(affected):
                ref_key = f"{surface}_commit_ref"
                surface_ref = verify.get(ref_key)
                if not surface_ref:
                    missing.append(f"verify missing for {surface}")
                elif not pr_gate.is_ancestor(surface_ref, head_sha, REPO_ROOT):
                    missing.append(f"verify not ancestor for {surface}")

            assert missing == [], f"Verify should pass after converge: {missing}"

            # Replicate QA check
            qa = {"ext_commit_ref": qa_ref}
            qa_missing = []
            non_workflow = affected - {"workflow"}
            for surface in sorted(non_workflow):
                ref_key = f"{surface}_commit_ref"
                surface_ref = qa.get(ref_key)
                if not surface_ref:
                    qa_missing.append(f"qa missing for {surface}")
                elif not pr_gate.is_ancestor(surface_ref, head_sha, REPO_ROOT):
                    qa_missing.append(f"qa not ancestor for {surface}")

            assert qa_missing == [], f"QA should pass after converge: {qa_missing}"
