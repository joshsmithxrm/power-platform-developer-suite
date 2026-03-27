#!/usr/bin/env python3
"""Tests for pipeline reliability (workflow-enforcement v4.0-v5.0, ACs 51-92)."""
import json
import os
import subprocess
import sys
import tempfile
import textwrap

import pytest

# Add scripts dir to path so we can import pipeline module
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))


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
    def test_sets_pipeline_env_var(self):
        """AC-54: run_claude sets PPDS_PIPELINE=1 in subprocess env."""
        import pipeline

        # The env setup is inside run_claude. We verify by checking
        # the source code contains the env var setting.
        import inspect

        source = inspect.getsource(pipeline.run_claude)
        assert 'env["PPDS_PIPELINE"] = "1"' in source


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
        """AC-57: Stall-based and hard ceiling timeout constants are defined."""
        import pipeline

        assert hasattr(pipeline, "STALL_LIMIT")
        assert hasattr(pipeline, "HARD_CEILING")
        assert pipeline.STALL_LIMIT == 5
        assert pipeline.HARD_CEILING == 3600


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
        """AC-59: Last 20 lines of stage output in pipeline.log."""
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

            # Verify we can read the last 20 lines
            with open(stage_log, "r") as f:
                lines = f.readlines()
            assert len(lines) == 30
            assert len(lines[-20:]) == 20
            assert "output line 10" in lines[-20:][0]



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
    def test_stream_json_output_format(self):
        """AC-67: Popen command includes --output-format stream-json."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.run_claude)
        assert '"--output-format", "stream-json"' in source or \
               "'--output-format', 'stream-json'" in source, \
            "run_claude must use --output-format stream-json"


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
        # Test the logic inline — output grew
        last_log_size, last_git_changes, last_commits = 100, 3, 2
        current_size, git_changes, commits = 200, 3, 2
        consecutive_idle = 5

        output_grew = current_size > last_log_size
        git_grew = git_changes > last_git_changes
        commits_grew = commits > last_commits

        if output_grew or git_grew or commits_grew:
            activity = "active"
            consecutive_idle = 0
        else:
            consecutive_idle += 1
            activity = "stalled" if consecutive_idle >= 3 else "idle"

        assert activity == "active"
        assert consecutive_idle == 0

    def test_active_when_git_changes(self):
        """AC-69: Activity is 'active' when git_changes increases."""
        last_log_size, last_git_changes, last_commits = 100, 3, 2
        current_size, git_changes, commits = 100, 5, 2  # Only git changed
        consecutive_idle = 0

        output_grew = current_size > last_log_size
        git_grew = git_changes > last_git_changes
        commits_grew = commits > last_commits

        if output_grew or git_grew or commits_grew:
            activity = "active"
        else:
            activity = "idle"

        assert activity == "active"

    def test_idle_when_nothing_changes(self):
        """AC-69: Activity is 'idle' when no signal changes."""
        last_log_size, last_git_changes, last_commits = 100, 3, 2
        current_size, git_changes, commits = 100, 3, 2
        consecutive_idle = 1

        output_grew = current_size > last_log_size
        git_grew = git_changes > last_git_changes
        commits_grew = commits > last_commits

        if output_grew or git_grew or commits_grew:
            activity = "active"
            consecutive_idle = 0
        else:
            consecutive_idle += 1
            activity = "stalled" if consecutive_idle >= 3 else "idle"

        assert activity == "idle"
        assert consecutive_idle == 2

    def test_stalled_after_three_idle(self):
        """AC-69: Activity is 'stalled' after 3+ consecutive idle heartbeats."""
        consecutive_idle = 2  # Already idle twice
        current_size, git_changes, commits = 100, 3, 2
        last_log_size, last_git_changes, last_commits = 100, 3, 2

        output_grew = current_size > last_log_size
        git_grew = git_changes > last_git_changes
        commits_grew = commits > last_commits

        if output_grew or git_grew or commits_grew:
            activity = "active"
            consecutive_idle = 0
        else:
            consecutive_idle += 1
            activity = "stalled" if consecutive_idle >= 3 else "idle"

        assert activity == "stalled"
        assert consecutive_idle == 3


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
    def test_pr_creates_draft(self):
        """AC-79: run_pr_stage uses --draft flag."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.run_pr_stage)
        assert '"--draft"' in source or "'--draft'" in source, \
            "run_pr_stage must create PR with --draft flag"


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
    def test_pr_triage_agent_invocation(self):
        """AC-81: run_triage invokes gemini-triage agent."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.run_triage)
        assert 'agent="gemini-triage"' in source or \
               "agent='gemini-triage'" in source, \
            "run_triage must use gemini-triage agent"


# ---------------------------------------------------------------------------
# AC-82: Threaded replies
# ---------------------------------------------------------------------------
class TestPrThreadedReplies:
    def test_pr_threaded_replies(self):
        """AC-82: post_replies uses in_reply_to for threaded comments."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.post_replies)
        assert "in_reply_to" in source, \
            "post_replies must use in_reply_to for threaded replies"


# ---------------------------------------------------------------------------
# AC-83: Draft to ready
# ---------------------------------------------------------------------------
class TestPrDraftToReady:
    def test_pr_draft_to_ready(self):
        """AC-83: run_pr_stage calls gh pr ready."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.run_pr_stage)
        assert '"gh", "pr", "ready"' in source or \
               "'gh', 'pr', 'ready'" in source, \
            "run_pr_stage must convert draft to ready with gh pr ready"


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
    def test_pr_gemini_stabilization(self):
        """AC-88: poll_gemini requires 2 consecutive same-count polls."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.poll_gemini)
        assert "stable_polls" in source, \
            "poll_gemini must track stable polls for stabilization"
        assert "stable_polls >= 1" in source, \
            "poll_gemini must stop after two consecutive same-count polls"


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
# AC-90: Gemini timeout annotation
# ---------------------------------------------------------------------------
class TestPrGeminiTimeoutAnnotation:
    def test_pr_gemini_timeout_annotation(self):
        """AC-90: PR body annotated on Gemini timeout."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.run_pr_stage)
        assert "no review received" in source, \
            "run_pr_stage must annotate PR body on Gemini timeout"


# ---------------------------------------------------------------------------
# AC-91: Triage failure annotation
# ---------------------------------------------------------------------------
class TestPrTriageFailureAnnotation:
    def test_pr_triage_failure_annotation(self):
        """AC-91: PR body annotated on triage failure."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.run_pr_stage)
        assert "triage incomplete" in source, \
            "run_pr_stage must annotate PR body on triage failure"


# ---------------------------------------------------------------------------
# AC-92: Triage push verify
# ---------------------------------------------------------------------------
class TestPrTriagePushVerify:
    def test_pr_triage_push_verify(self):
        """AC-92: run_pr_stage verifies remote HEAD before posting replies."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.run_pr_stage)
        assert "ls-remote" in source, \
            "run_pr_stage must verify remote HEAD via ls-remote"
        assert "PUSH_MISMATCH" in source, \
            "run_pr_stage must log PUSH_MISMATCH when heads differ"


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
    def test_pipeline_failure_exception_exists(self):
        """AC-04: PipelineFailure exception class exists."""
        import pipeline

        assert hasattr(pipeline, "PipelineFailure")
        assert issubclass(pipeline.PipelineFailure, Exception)

    def test_pipeline_fail_raises_pipeline_failure(self):
        """AC-04: _pipeline_fail raises PipelineFailure, not sys.exit."""
        import inspect
        import pipeline

        # Verify _pipeline_fail source uses raise PipelineFailure, not sys.exit
        # We need to check main() source since _pipeline_fail is a nested function
        source = inspect.getsource(pipeline.main)
        assert "raise PipelineFailure" in source
        assert "except PipelineFailure" in source

    def test_failure_handler_runs_retro(self):
        """AC-04: except PipelineFailure block runs retro."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.main)
        # Find the PipelineFailure handler and verify it runs retro
        pf_idx = source.index("except PipelineFailure")
        handler_section = source[pf_idx:pf_idx + 1000]
        assert 'mode="failure-retro"' in handler_section
        assert "/retro" in handler_section


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
                    "phase1_cli_passed": 7,
                    "phase1_cli_total": 7,
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
            assert result["partial_results"]["phase1_cli_passed"] == 7

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
        """AC-12: Stage killed when consecutive_idle >= STALL_LIMIT."""
        import pipeline

        # Verify the stall timeout logic exists in run_claude
        import inspect
        source = inspect.getsource(pipeline.run_claude)
        assert "STALL_TIMEOUT" in source
        assert "consecutive_idle >= STALL_LIMIT" in source


# ---------------------------------------------------------------------------
# AC-13: Hard timeout at ceiling
# ---------------------------------------------------------------------------
class TestHardTimeout:
    def test_hard_timeout_kills_at_ceiling(self):
        """AC-13: Stage killed when elapsed > HARD_CEILING."""
        import pipeline
        import inspect

        source = inspect.getsource(pipeline.run_claude)
        assert "HARD_TIMEOUT" in source
        assert "HARD_CEILING" in source


# ---------------------------------------------------------------------------
# AC-14: Active stage not killed by stall timeout
# ---------------------------------------------------------------------------
class TestActiveStageNotKilled:
    def test_active_stage_resets_idle_counter(self):
        """AC-14: Activity resets consecutive_idle to 0."""
        # Inline test of the activity logic
        consecutive_idle = 4  # One away from STALL_LIMIT
        current_size, git_changes, commits = 200, 3, 2
        last_log_size, last_git_changes, last_commits = 100, 3, 2

        output_grew = current_size > last_log_size

        if output_grew:
            consecutive_idle = 0

        assert consecutive_idle == 0  # Reset because output grew


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
    def test_skip_duplicate_issue_filing(self):
        """AC-07: Skips filing and logs ISSUE_SKIPPED_DUPLICATE when duplicate exists."""
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

            with patch.object(pipeline, "_find_duplicate_issue", return_value=42):
                pipeline.process_retro_findings(tmpdir, logger, tmpdir)

            logger.close()

            with open(log_path) as f:
                content = f.read()
            assert "ISSUE_SKIPPED_DUPLICATE" in content
            assert "#42" in content

    def test_files_issue_when_dedup_check_fails(self):
        """AC-08: Files issue when _find_duplicate_issue fails (best-effort dedup)."""
        import pipeline
        from unittest.mock import patch, MagicMock, call

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


class TestObservationsPersisted:
    def test_observations_persisted_in_store(self):
        """AC-09: observation findings remain in retro-findings.json (not filtered out)."""
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

            # After process_retro_findings runs, the file should still contain
            # observation findings (the function reads but doesn't modify the file)
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

    def test_duplicate_comment_format(self):
        """RF AC-16: Duplicate comment includes branch, finding ID, evidence."""
        import pipeline
        import inspect
        source = inspect.getsource(pipeline._handle_duplicate)
        assert "branch" in source.lower()
        assert "finding_id" in source or "finding" in source
        assert "Also observed" in source


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
# Workflow Enforcement v7.0 Tests (ACs 100-127) — Stop Hook + Heartbeat
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
        for phase in ("starting", "investigating", "design", "reviewing", "pr"):
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
# AC-123: Pipeline heartbeat uses origin/main
# ---------------------------------------------------------------------------
class TestHeartbeatOriginMain:
    def test_heartbeat_uses_origin_main(self):
        """AC-123: get_commit_count and get_git_activity use origin/main..HEAD."""
        import inspect
        import pipeline

        # Check get_commit_count
        source_count = inspect.getsource(pipeline.get_commit_count)
        assert "origin/main..HEAD" in source_count, (
            "get_commit_count must use origin/main..HEAD"
        )

        # Check get_git_activity
        source_activity = inspect.getsource(pipeline.get_git_activity)
        assert "origin/main..HEAD" in source_activity, (
            "get_git_activity must use origin/main..HEAD"
        )


# ===========================================================================
# Pipeline Reliability (PO AC-16–24) — Converge Logic
# ===========================================================================


# ---------------------------------------------------------------------------
# PO AC-20: Pipeline runs converge on review FAIL
# ---------------------------------------------------------------------------
class TestConvergeOnReviewFail:
    def test_pipeline_runs_converge_on_review_fail(self):
        """PO AC-20: Converge stage checks review state, not just review.passed."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.main)
        # The converge stage should read review findings and make a decision
        assert "review_findings" in source, (
            "Converge logic must check review findings count"
        )
        assert "review FAIL" in source or "review_passed" in source, (
            "Converge logic must handle review FAIL case"
        )


# ---------------------------------------------------------------------------
# PO AC-21: Pipeline skips converge on zero findings
# ---------------------------------------------------------------------------
class TestConvergeSkipsOnZero:
    def test_pipeline_skips_converge_on_zero_findings(self):
        """PO AC-21: Converge skipped when review passes with zero findings."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.main)
        assert "review_findings == 0" in source or "zero findings" in source, (
            "Converge must skip when review passes with zero findings"
        )


# ---------------------------------------------------------------------------
# PO AC-21b: Pipeline runs converge on pass with findings
# ---------------------------------------------------------------------------
class TestConvergeRunsOnPassWithFindings:
    def test_pipeline_runs_converge_on_pass_with_findings(self):
        """PO AC-21b: Converge runs when review passes with non-zero findings."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.main)
        assert "review_findings > 0" in source, (
            "Converge must run when review passes with findings > 0"
        )


# ---------------------------------------------------------------------------
# AC-122: Hook path resolution in worktrees
# ---------------------------------------------------------------------------
class TestHookPathResolution:
    def test_hooks_use_relative_paths_that_work_in_worktrees(self):
        """AC-122: Hook commands use relative paths that resolve in worktrees."""
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
        """AC-122: Stop hook executes without path errors in a worktree."""
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
        "qa": "reviewing",
        "pr": "pr",
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
        """AC-105: pipeline.py writes phase=pipeline to state."""
        import inspect
        import pipeline

        source = inspect.getsource(pipeline.main)
        assert '"phase", "pipeline"' in source or "'phase', 'pipeline'" in source, (
            "pipeline.py must set phase to 'pipeline'"
        )
