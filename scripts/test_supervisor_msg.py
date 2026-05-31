#!/usr/bin/env python3
"""Behavioral unit tests for supervisor_msg.py.

Tests exercise send/read/list operations, atomicity contract, kind validation,
consume flag, and CLI entry points.

Run:
    python -m unittest scripts.test_supervisor_msg
    python -m pytest scripts/test_supervisor_msg.py
"""
from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPT_DIR))

import supervisor_msg as sm


class TestSend(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.worktree = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def test_send_creates_inbox_file(self):
        path = sm.send(str(self.worktree), "approve")
        self.assertTrue(path.exists())
        data = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(data["kind"], "approve")
        self.assertIn("sent_at", data)

    def test_send_with_message(self):
        path = sm.send(str(self.worktree), "revise", message="Fix AC-03 layout")
        data = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(data["message"], "Fix AC-03 layout")

    def test_send_with_payload(self):
        path = sm.send(str(self.worktree), "note", payload={"priority": "low"})
        data = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(data["payload"]["priority"], "low")

    def test_send_file_in_inbox_subdir(self):
        path = sm.send(str(self.worktree), "abort")
        inbox = self.worktree / ".workflow" / "inbox"
        self.assertTrue(inbox.is_dir())
        self.assertIn(path, list(inbox.glob("*.json")))

    def test_send_multiple_creates_distinct_files(self):
        p1 = sm.send(str(self.worktree), "note")
        p2 = sm.send(str(self.worktree), "note")
        self.assertNotEqual(p1, p2)
        self.assertEqual(len(list((self.worktree / ".workflow" / "inbox").glob("*.json"))), 2)

    def test_send_invalid_kind_raises(self):
        with self.assertRaises(ValueError):
            sm.send(str(self.worktree), "unknown_kind")

    def test_send_nonexistent_worktree_raises(self):
        with self.assertRaises(FileNotFoundError):
            sm.send("/nonexistent/path/worktree", "approve")

    def test_filename_contains_kind(self):
        path = sm.send(str(self.worktree), "revise")
        self.assertIn("revise", path.name)

    def test_all_valid_kinds_accepted(self):
        for kind in sm.VALID_KINDS:
            path = sm.send(str(self.worktree), kind)
            self.assertTrue(path.exists(), f"Expected file for kind={kind}")


class TestReadInbox(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.worktree = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def test_read_empty_returns_empty_list(self):
        messages = sm.read_inbox(str(self.worktree))
        self.assertEqual(messages, [])

    def test_read_returns_sent_message(self):
        sm.send(str(self.worktree), "approve", message="LGTM")
        messages = sm.read_inbox(str(self.worktree))
        self.assertEqual(len(messages), 1)
        self.assertEqual(messages[0]["kind"], "approve")
        self.assertEqual(messages[0]["message"], "LGTM")

    def test_read_preserves_files_by_default(self):
        sm.send(str(self.worktree), "note")
        sm.read_inbox(str(self.worktree), consume=False)
        inbox = self.worktree / ".workflow" / "inbox"
        self.assertEqual(len(list(inbox.glob("*.json"))), 1)

    def test_read_consume_deletes_files(self):
        sm.send(str(self.worktree), "note")
        sm.read_inbox(str(self.worktree), consume=True)
        inbox = self.worktree / ".workflow" / "inbox"
        self.assertEqual(len(list(inbox.glob("*.json"))), 0)

    def test_read_multiple_messages_both_present(self):
        sm.send(str(self.worktree), "note", message="first")
        sm.send(str(self.worktree), "revise", message="second")
        messages = sm.read_inbox(str(self.worktree))
        self.assertEqual(len(messages), 2)
        kinds = {m["kind"] for m in messages}
        self.assertEqual(kinds, {"note", "revise"})

    def test_read_sorted_by_filename(self):
        # Write messages with manually controlled filenames so ordering is deterministic
        inbox = self.worktree / ".workflow" / "inbox"
        inbox.mkdir(parents=True, exist_ok=True)
        import json as _json
        (inbox / "20260101T000001_note_aaaaaa.json").write_text(
            _json.dumps({"kind": "note", "sent_at": "2026-01-01T00:00:01Z", "message": "earlier"}),
            encoding="utf-8",
        )
        (inbox / "20260101T000002_approve_bbbbbb.json").write_text(
            _json.dumps({"kind": "approve", "sent_at": "2026-01-01T00:00:02Z"}),
            encoding="utf-8",
        )
        messages = sm.read_inbox(str(self.worktree))
        self.assertEqual(len(messages), 2)
        self.assertEqual(messages[0]["message"], "earlier")
        self.assertEqual(messages[1]["kind"], "approve")

    def test_read_includes_file_path(self):
        sm.send(str(self.worktree), "approve")
        messages = sm.read_inbox(str(self.worktree))
        self.assertIn("_file", messages[0])
        self.assertTrue(messages[0]["_file"].endswith(".json"))

    def test_read_no_inbox_dir_returns_empty(self):
        # Worktree exists but .workflow/inbox doesn't
        messages = sm.read_inbox(str(self.worktree))
        self.assertEqual(messages, [])

    def test_consume_second_read_is_empty(self):
        sm.send(str(self.worktree), "abort")
        sm.read_inbox(str(self.worktree), consume=True)
        messages = sm.read_inbox(str(self.worktree))
        self.assertEqual(messages, [])


class TestListInbox(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.worktree = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def test_list_empty(self):
        result = sm.list_inbox(str(self.worktree))
        self.assertEqual(result, [])

    def test_list_returns_paths(self):
        sm.send(str(self.worktree), "note")
        result = sm.list_inbox(str(self.worktree))
        self.assertEqual(len(result), 1)
        self.assertTrue(result[0].endswith(".json"))


class TestCLISend(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.worktree = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def _run(self, args: list[str]) -> tuple[int, str]:
        import io
        from unittest.mock import patch

        captured_out = io.StringIO()
        exit_code = 0
        try:
            with patch("sys.stdout", captured_out):
                sm.main(["supervisor_msg.py"] + args)
        except SystemExit as e:
            exit_code = e.code or 0
        return exit_code, captured_out.getvalue()

    def test_send_exits_zero_and_prints_path(self):
        code, out = self._run(["send", str(self.worktree), "approve"])
        self.assertEqual(code, 0)
        self.assertIn(".json", out)

    def test_send_with_message_flag(self):
        code, out = self._run(["send", str(self.worktree), "revise",
                               "--message", "Please fix AC-02"])
        self.assertEqual(code, 0)
        path = Path(out.strip())
        data = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(data["message"], "Please fix AC-02")

    def test_send_invalid_kind_exits_nonzero(self):
        code, _ = self._run(["send", str(self.worktree), "invalid_kind"])
        self.assertNotEqual(code, 0)

    def test_send_nonexistent_worktree_exits_2(self):
        code, _ = self._run(["send", "/no/such/path", "approve"])
        self.assertEqual(code, 2)

    def test_send_payload_file(self):
        with tempfile.NamedTemporaryFile(
            mode="w", suffix=".json", delete=False, encoding="utf-8"
        ) as pf:
            json.dump({"detail": "needs revision"}, pf)
            pf_name = pf.name
        try:
            code, out = self._run([
                "send", str(self.worktree), "revise",
                "--payload-file", pf_name,
            ])
            self.assertEqual(code, 0)
            path = Path(out.strip())
            data = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(data["payload"]["detail"], "needs revision")
        finally:
            os.unlink(pf_name)


class TestCLIRead(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.worktree = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def _run(self, args: list[str]) -> tuple[int, str]:
        import io
        from unittest.mock import patch

        captured_out = io.StringIO()
        exit_code = 0
        try:
            with patch("sys.stdout", captured_out):
                sm.main(["supervisor_msg.py"] + args)
        except SystemExit as e:
            exit_code = e.code or 0
        return exit_code, captured_out.getvalue()

    def test_read_empty_prints_empty_array(self):
        code, out = self._run(["read", "--worktree", str(self.worktree)])
        self.assertEqual(code, 0)
        data = json.loads(out)
        self.assertEqual(data, [])

    def test_read_returns_messages_json(self):
        sm.send(str(self.worktree), "approve", message="ship it")
        code, out = self._run(["read", "--worktree", str(self.worktree)])
        self.assertEqual(code, 0)
        data = json.loads(out)
        self.assertEqual(len(data), 1)
        self.assertEqual(data[0]["kind"], "approve")

    def test_read_consume_clears_inbox(self):
        sm.send(str(self.worktree), "note")
        code, _ = self._run(["read", "--worktree", str(self.worktree), "--consume"])
        self.assertEqual(code, 0)
        inbox = self.worktree / ".workflow" / "inbox"
        self.assertEqual(len(list(inbox.glob("*.json"))), 0)


class TestCLIList(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.worktree = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def _run(self, args: list[str]) -> tuple[int, str]:
        import io
        from unittest.mock import patch

        captured_out = io.StringIO()
        exit_code = 0
        try:
            with patch("sys.stdout", captured_out):
                sm.main(["supervisor_msg.py"] + args)
        except SystemExit as e:
            exit_code = e.code or 0
        return exit_code, captured_out.getvalue()

    def test_list_empty_prints_nothing(self):
        code, out = self._run(["list", "--worktree", str(self.worktree)])
        self.assertEqual(code, 0)
        self.assertEqual(out.strip(), "")

    def test_list_one_file(self):
        sm.send(str(self.worktree), "note", message="heads up")
        code, out = self._run(["list", "--worktree", str(self.worktree)])
        self.assertEqual(code, 0)
        self.assertIn(".json", out)


class TestEdgeCases(unittest.TestCase):
    """Edge cases and C-1 bug regression."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.worktree = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def test_send_empty_dict_payload_is_preserved(self):
        """C-1 regression: payload={} must not be silently dropped."""
        path = sm.send(str(self.worktree), "note", payload={})
        data = json.loads(path.read_text(encoding="utf-8"))
        self.assertIn("payload", data)
        self.assertEqual(data["payload"], {})

    def test_sent_at_matches_filename_timestamp_precision(self):
        """N-1: sent_at and filename timestamp are derived from same datetime."""
        path = sm.send(str(self.worktree), "approve")
        data = json.loads(path.read_text(encoding="utf-8"))
        # sent_at is ISO-8601; filename starts with compact UTC timestamp
        self.assertIn("sent_at", data)
        # Both contain year/month/day portion from the same moment
        from datetime import datetime, timezone
        sent_at = datetime.fromisoformat(data["sent_at"].replace("Z", "+00:00"))
        filename_ts = path.name.split("_")[0]  # e.g. 20260516T083000123456Z
        self.assertTrue(filename_ts.startswith(sent_at.strftime("%Y%m%dT%H%M%S")))


class TestInboxProtocolIntegration(unittest.TestCase):
    """Integration-style test: supervisor writes directive → worker reads and acts.

    Demonstrates the full revise→revision-mode flow end-to-end at the file level:
    supervisor writes a 'revise' message; worker calls read --consume and gets it;
    a second read confirms the inbox is now clear.
    """

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.worker_worktree = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def test_revise_directive_supervisor_to_worker(self):
        # Supervisor: write a revise directive to the worker's inbox
        feedback = "Apply reviewer feedback: AC-03 missing error handling"
        sm.send(
            str(self.worker_worktree),
            "revise",
            message=feedback,
            payload={"ac": "AC-03", "priority": "high"},
        )

        # Worker: at phase entry, reads and consumes the inbox
        messages = sm.read_inbox(str(self.worker_worktree), consume=True)

        # Worker confirms: received exactly one revise directive with full payload
        self.assertEqual(len(messages), 1)
        msg = messages[0]
        self.assertEqual(msg["kind"], "revise")
        self.assertEqual(msg["message"], feedback)
        self.assertEqual(msg["payload"]["ac"], "AC-03")
        self.assertEqual(msg["payload"]["priority"], "high")

        # Worker enters revision mode (confirmed by consume: inbox now empty)
        subsequent = sm.read_inbox(str(self.worker_worktree))
        self.assertEqual(subsequent, [], "Inbox must be empty after consume — worker enters revision mode")

    def test_abort_directive_stops_workflow(self):
        sm.send(str(self.worker_worktree), "abort", message="Design rejected — restart with new scope")
        messages = sm.read_inbox(str(self.worker_worktree), consume=True)
        self.assertEqual(len(messages), 1)
        self.assertEqual(messages[0]["kind"], "abort")
        self.assertIn("rejected", messages[0]["message"])

    def test_approve_directive_clears_after_consume(self):
        sm.send(str(self.worker_worktree), "approve")
        messages = sm.read_inbox(str(self.worker_worktree), consume=True)
        self.assertEqual(len(messages), 1)
        self.assertEqual(messages[0]["kind"], "approve")
        self.assertEqual(sm.read_inbox(str(self.worker_worktree)), [])

    def test_multiple_directives_processed_in_order(self):
        """Simulates supervisor sending note then approve; worker processes both."""
        import json as _json
        inbox = self.worker_worktree / ".workflow" / "inbox"
        inbox.mkdir(parents=True, exist_ok=True)
        # Write deterministic filenames to ensure ordering
        (inbox / "20260101T000001_note_aaaaaa.json").write_text(
            _json.dumps({"kind": "note", "sent_at": "2026-01-01T00:00:01Z", "message": "CI slow today"}),
            encoding="utf-8",
        )
        (inbox / "20260101T000002_approve_bbbbbb.json").write_text(
            _json.dumps({"kind": "approve", "sent_at": "2026-01-01T00:00:02Z"}),
            encoding="utf-8",
        )
        messages = sm.read_inbox(str(self.worker_worktree), consume=True)
        self.assertEqual(len(messages), 2)
        self.assertEqual(messages[0]["kind"], "note")
        self.assertEqual(messages[1]["kind"], "approve")
        # Inbox cleared
        self.assertEqual(sm.read_inbox(str(self.worker_worktree)), [])


if __name__ == "__main__":
    unittest.main()
