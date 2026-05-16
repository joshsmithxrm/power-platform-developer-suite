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

    def test_read_multiple_messages_sorted(self):
        sm.send(str(self.worktree), "note", message="first")
        sm.send(str(self.worktree), "revise", message="second")
        messages = sm.read_inbox(str(self.worktree))
        self.assertEqual(len(messages), 2)
        # Sorted by filename (which starts with timestamp) so first < second
        self.assertEqual(messages[0]["message"], "first")
        self.assertEqual(messages[1]["message"], "second")

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


if __name__ == "__main__":
    unittest.main()
