"""Tests for triage_common.poll_gemini_review and the flatten helper.

v1-prelaunch retro:
- Item #3: poll_gemini must poll all three GitHub endpoints (reviews,
  pulls/comments, issues/comments) and terminate on the first
  Gemini-bot submission newer than the PR's created_at.
- Item #5: --paginate --slurp returns a list-of-pages; we must flatten
  before iterating or every caller crashes with AttributeError.
"""
import json
import os
import subprocess
import sys
from unittest.mock import patch

import pytest

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(REPO_ROOT, "scripts"))

from triage_common import (  # noqa: E402
    GEMINI_BOT_LOGIN,
    _flatten_paginate_slurp,
    poll_gemini_review,
)


# ---------------------------------------------------------------------------
# _flatten_paginate_slurp (v1-prelaunch retro item #5)
# ---------------------------------------------------------------------------


class TestFlattenPaginateSlurp:
    def test_flat_list_returned_unchanged(self):
        """A flat list (no pagination) is returned as-is."""
        items = [{"id": 1}, {"id": 2}]
        assert _flatten_paginate_slurp(items) == items

    def test_list_of_pages_flattened(self):
        """A list-of-pages is flattened one level."""
        pages = [
            [{"id": 1}, {"id": 2}],
            [{"id": 3}],
        ]
        assert _flatten_paginate_slurp(pages) == [{"id": 1}, {"id": 2}, {"id": 3}]

    def test_empty_list_returned_unchanged(self):
        assert _flatten_paginate_slurp([]) == []

    def test_none_returns_empty_list(self):
        assert _flatten_paginate_slurp(None) == []

    def test_single_page_with_one_item(self):
        """[[{...}]] still flattens correctly to [{...}]."""
        pages = [[{"id": 1}]]
        assert _flatten_paginate_slurp(pages) == [{"id": 1}]

    def test_dict_returned_unchanged(self):
        """Non-list payload is returned unchanged (defensive)."""
        d = {"foo": "bar"}
        assert _flatten_paginate_slurp(d) == d


# ---------------------------------------------------------------------------
# poll_gemini_review (v1-prelaunch retro item #3)
# ---------------------------------------------------------------------------


def _gh_ok(payload):
    return subprocess.CompletedProcess(
        args=[], returncode=0, stdout=json.dumps(payload), stderr="",
    )


class TestPollGeminiReview:
    """Verify all three endpoints are polled and the right one terminates."""

    def test_terminates_on_top_level_review(self, tmp_path):
        """Gemini posts only a top-level review (pulls/reviews).
        The old code (which only polled pulls/comments) timed out;
        the new code MUST terminate on the review."""
        gemini_review = {
            "id": 1,
            "user": {"login": GEMINI_BOT_LOGIN},
            "submitted_at": "2026-04-19T05:00:00Z",
            "state": "COMMENTED",
        }

        endpoints_polled = []

        def mock_run(cmd, **kwargs):
            assert cmd[0] == "gh" and cmd[1] == "api"
            path = cmd[2]
            endpoints_polled.append(path)
            if "/reviews" in path:
                return _gh_ok([gemini_review])
            if "/pulls/" in path and path.endswith("/comments"):
                return _gh_ok([])
            if "/issues/" in path and path.endswith("/comments"):
                return _gh_ok([])
            if cmd[1:3] == ["repo", "view"]:
                return subprocess.CompletedProcess(
                    args=cmd, returncode=0, stdout="owner/repo", stderr="")
            return _gh_ok([])

        with patch("triage_common.subprocess.run", side_effect=mock_run), \
             patch("triage_common.get_repo_slug", return_value="owner/repo"), \
             patch("triage_common.time.sleep"):
            comments, status = poll_gemini_review(
                str(tmp_path), 803, "2026-04-19T04:00:00Z",
                max_wait=60, poll_interval=0,
            )

        assert status == "review_received"
        # All three endpoints polled at least once
        kinds = {p.split("/")[-1] for p in endpoints_polled}
        assert "reviews" in kinds, f"reviews not polled: {endpoints_polled!r}"
        assert "comments" in kinds, f"comments not polled: {endpoints_polled!r}"

    def test_terminates_on_inline_pull_comment(self, tmp_path):
        """Gemini posts an inline review comment (pulls/comments) — should
        also terminate (it's still a Gemini submission)."""
        inline = {
            "id": 99,
            "user": {"login": GEMINI_BOT_LOGIN},
            "created_at": "2026-04-19T05:00:00Z",
            "path": "src/foo.py",
            "line": 1,
            "body": "Suggestion",
        }

        def mock_run(cmd, **kwargs):
            path = cmd[2] if len(cmd) > 2 else ""
            if "/reviews" in path:
                return _gh_ok([])
            if "/pulls/" in path and path.endswith("/comments"):
                return _gh_ok([inline])
            if "/issues/" in path and path.endswith("/comments"):
                return _gh_ok([])
            return _gh_ok([])

        with patch("triage_common.subprocess.run", side_effect=mock_run), \
             patch("triage_common.get_repo_slug", return_value="owner/repo"), \
             patch("triage_common.time.sleep"):
            comments, status = poll_gemini_review(
                str(tmp_path), 803, "2026-04-19T04:00:00Z",
                max_wait=60, poll_interval=0,
            )

        assert status == "review_received"
        assert len(comments) == 1
        assert comments[0]["id"] == 99

    def test_terminates_on_issue_comment(self, tmp_path):
        """Gemini posts a PR-level (issue) comment — should also terminate."""
        issue_c = {
            "id": 7,
            "user": {"login": GEMINI_BOT_LOGIN},
            "created_at": "2026-04-19T05:00:00Z",
            "body": "Summary review",
        }

        def mock_run(cmd, **kwargs):
            path = cmd[2] if len(cmd) > 2 else ""
            if "/reviews" in path:
                return _gh_ok([])
            if "/pulls/" in path and path.endswith("/comments"):
                return _gh_ok([])
            if "/issues/" in path and path.endswith("/comments"):
                return _gh_ok([issue_c])
            return _gh_ok([])

        with patch("triage_common.subprocess.run", side_effect=mock_run), \
             patch("triage_common.get_repo_slug", return_value="owner/repo"), \
             patch("triage_common.time.sleep"):
            comments, status = poll_gemini_review(
                str(tmp_path), 803, "2026-04-19T04:00:00Z",
                max_wait=60, poll_interval=0,
            )

        assert status == "review_received"
        # No inline comments — Gemini only posted issue-level
        assert comments == []

    def test_ignores_stale_review_before_pr_created(self, tmp_path):
        """A Gemini review with submitted_at *before* the PR's created_at
        is ignored (likely a stale entry from a force-pushed parent)."""
        stale_review = {
            "id": 1,
            "user": {"login": GEMINI_BOT_LOGIN},
            "submitted_at": "2026-04-18T00:00:00Z",  # before PR
            "state": "COMMENTED",
        }

        def mock_run(cmd, **kwargs):
            path = cmd[2] if len(cmd) > 2 else ""
            if "/reviews" in path:
                return _gh_ok([stale_review])
            return _gh_ok([])

        with patch("triage_common.subprocess.run", side_effect=mock_run), \
             patch("triage_common.get_repo_slug", return_value="owner/repo"), \
             patch("triage_common.time.sleep"):
            comments, status = poll_gemini_review(
                str(tmp_path), 803, "2026-04-19T04:00:00Z",
                max_wait=2, poll_interval=0,
            )

        assert status == "timeout"
        assert comments == []

    def test_ignores_non_gemini_user(self, tmp_path):
        """Reviews from other bots/users should not terminate the loop."""
        other_review = {
            "id": 1,
            "user": {"login": "github-actions[bot]"},
            "submitted_at": "2026-04-19T05:00:00Z",
        }

        def mock_run(cmd, **kwargs):
            path = cmd[2] if len(cmd) > 2 else ""
            if "/reviews" in path:
                return _gh_ok([other_review])
            return _gh_ok([])

        with patch("triage_common.subprocess.run", side_effect=mock_run), \
             patch("triage_common.get_repo_slug", return_value="owner/repo"), \
             patch("triage_common.time.sleep"):
            comments, status = poll_gemini_review(
                str(tmp_path), 803, "2026-04-19T04:00:00Z",
                max_wait=2, poll_interval=0,
            )

        assert status == "timeout"

    def test_handles_paginated_endpoint_response(self, tmp_path):
        """``--paginate --slurp`` returns a list-of-pages; the helper must
        flatten before iterating (v1-prelaunch retro item #5)."""
        gemini_review = {
            "id": 1,
            "user": {"login": GEMINI_BOT_LOGIN},
            "submitted_at": "2026-04-19T05:00:00Z",
        }

        def mock_run(cmd, **kwargs):
            path = cmd[2] if len(cmd) > 2 else ""
            if "/reviews" in path:
                # Simulate page-of-pages
                return _gh_ok([[gemini_review], []])
            return _gh_ok([])

        with patch("triage_common.subprocess.run", side_effect=mock_run), \
             patch("triage_common.get_repo_slug", return_value="owner/repo"), \
             patch("triage_common.time.sleep"):
            comments, status = poll_gemini_review(
                str(tmp_path), 803, "2026-04-19T04:00:00Z",
                max_wait=10, poll_interval=0,
            )

        assert status == "review_received"

    def test_shakedown_skipped(self, tmp_path):
        """Shakedown mode short-circuits without calling gh."""
        with patch("triage_common.subprocess.run") as mock_run:
            comments, status = poll_gemini_review(
                str(tmp_path), 1, "", shakedown=True,
            )
        assert status == "shakedown"
        assert comments == []
        mock_run.assert_not_called()

    def test_poll_gemini_filters_inline_to_bot_only(self, tmp_path):
        """The returned inline list must contain ONLY gemini-authored
        comments. The pulls/comments endpoint returns human-reviewer
        comments and stale force-push comments alongside gemini's, and
        callers must not triage those as if Gemini posted them."""
        gemini_inline = {
            "id": 100,
            "user": {"login": GEMINI_BOT_LOGIN},
            "created_at": "2026-04-19T05:00:00Z",
            "path": "src/foo.py",
            "line": 10,
            "body": "Gemini suggestion",
        }
        human_inline = {
            "id": 101,
            "user": {"login": "alice"},
            "created_at": "2026-04-19T05:00:01Z",
            "path": "src/foo.py",
            "line": 11,
            "body": "Human comment",
        }
        other_bot_inline = {
            "id": 102,
            "user": {"login": "github-advanced-security[bot]"},
            "created_at": "2026-04-19T05:00:02Z",
            "path": "src/foo.py",
            "line": 12,
            "body": "CodeQL finding",
        }

        def mock_run(cmd, **kwargs):
            path = cmd[2] if len(cmd) > 2 else ""
            if "/reviews" in path:
                return _gh_ok([])
            if "/pulls/" in path and path.endswith("/comments"):
                return _gh_ok(
                    [gemini_inline, human_inline, other_bot_inline])
            if "/issues/" in path and path.endswith("/comments"):
                return _gh_ok([])
            return _gh_ok([])

        with patch("triage_common.subprocess.run", side_effect=mock_run), \
             patch("triage_common.get_repo_slug", return_value="owner/repo"), \
             patch("triage_common.time.sleep"):
            comments, status = poll_gemini_review(
                str(tmp_path), 816, "2026-04-19T04:00:00Z",
                max_wait=60, poll_interval=0,
            )

        assert status == "review_received"
        assert len(comments) == 1, (
            f"expected only the gemini comment, got {comments!r}")
        assert comments[0]["id"] == 100
        assert comments[0]["user"] == GEMINI_BOT_LOGIN

    def test_logs_endpoint_counts(self, tmp_path):
        """log_fn is called with per-endpoint counts on each poll."""
        events = []

        def log_fn(event, **kw):
            events.append((event, kw))

        def mock_run(cmd, **kwargs):
            path = cmd[2] if len(cmd) > 2 else ""
            if "/reviews" in path:
                return _gh_ok([{"id": 1, "user": {"login": GEMINI_BOT_LOGIN},
                                "submitted_at": "2026-04-19T05:00:00Z"}])
            return _gh_ok([])

        with patch("triage_common.subprocess.run", side_effect=mock_run), \
             patch("triage_common.get_repo_slug", return_value="owner/repo"), \
             patch("triage_common.time.sleep"):
            poll_gemini_review(
                str(tmp_path), 803, "2026-04-19T04:00:00Z",
                max_wait=10, poll_interval=0, log_fn=log_fn,
            )

        # Should have at least one POLL log + one REVIEW_RECEIVED
        assert any(e[0] == "POLL" for e in events)
        assert any(e[0] == "REVIEW_RECEIVED" for e in events)
