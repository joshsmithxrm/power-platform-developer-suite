"""Exercise the real rollover guard and gh parser using only loopback HTTP.

The absolute GH shim accepts one read-only endpoint and rewrites only its URL.
Pagination, response parsing, jq filtering and exit codes are handled by real gh.
No GitHub credentials, user configuration, proxies or writer commands are used.
"""
from __future__ import annotations

from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import threading

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
GUARD = REPO_ROOT / "scripts/docs-gen/check-open-rollover.sh"
ENDPOINT = "repos/example/ppds/pulls?state=open&per_page=100"
FIXTURE_TOKEN = "offline-loopback-fixture-token"
CLEAN_MESSAGE = "no open baseline rollover PRs"


@dataclass(frozen=True)
class Response:
    body: bytes
    status: int = 200


def _json_response(value, status: int = 200) -> Response:
    return Response(json.dumps(value).encode("utf-8"), status)


def _pr(number: int, title: str = "fix: ordinary change") -> dict:
    return {
        "number": number,
        "title": title,
        "html_url": f"https://github.com/example/ppds/pull/{number}",
    }


@pytest.fixture(scope="module")
def cli_tools() -> tuple[Path, Path]:
    # Windows' System32 bash is a WSL launcher, not the Git Bash used by CI
    # shell-script tests. Prefer a real Git Bash installation there.
    bash = None
    if os.name == "nt":
        for root in (os.environ.get("ProgramFiles"), os.environ.get("ProgramW6432")):
            if root and (candidate := Path(root) / "Git/bin/bash.exe").is_file():
                bash = candidate
                break
    else:
        found = shutil.which("bash")
        bash = Path(found) if found else None
    gh = shutil.which("gh")
    if bash is None or gh is None:
        pytest.fail("Rollover guard tests require Bash (Git Bash on Windows) and the real gh CLI")
    return bash.resolve(), Path(gh).resolve()


@pytest.fixture
def run_guard(tmp_path: Path, cli_tools: tuple[Path, Path]):
    bash, gh = cli_tools
    config = tmp_path / "isolated-gh-config"
    config.mkdir()
    # Allowlist operating-system essentials instead of trying to enumerate all
    # possible inherited secrets, GH/Git settings and upper/lowercase proxies.
    env = {
        key: value for key, value in os.environ.items()
        if key.upper() in {"PATH", "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT"}
    }
    env.update({
        "TMP": str(tmp_path),
        "TEMP": str(tmp_path),
        "TMPDIR": str(tmp_path),
        "GH_CONFIG_DIR": str(config),
        "GH_TOKEN": FIXTURE_TOKEN,
        "GH_ENTERPRISE_TOKEN": FIXTURE_TOKEN,
        "GH_PROMPT_DISABLED": "1",
        "GH_NO_UPDATE_NOTIFIER": "1",
        "GH_NO_EXTENSION_UPDATE_NOTIFIER": "1",
        "GIT_CONFIG_NOSYSTEM": "1",
        "GIT_CONFIG_GLOBAL": os.devnull,
        "GIT_TERMINAL_PROMPT": "0",
        "NO_PROXY": "127.0.0.1",
    })

    def run(responses: list[Response], args: tuple[str, ...] = ("--repo", "example/ppds")):
        requests = []
        paths = [f"/{ENDPOINT}" + (f"&page={i + 1}" if i else "") for i in range(len(responses))]

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_args):
                pass

            def do_GET(self):
                requests.append((self.path, self.headers.get("Authorization")))
                if self.path not in paths:
                    self.send_error(400, "Unexpected fixture endpoint")
                    return
                index = paths.index(self.path)
                response = responses[index]
                self.send_response(response.status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(response.body)))
                if index + 1 < len(paths):
                    # Every pagination URL is constructed here; fixture data
                    # cannot redirect gh outside the loopback server.
                    self.send_header("Link", f'<{origin}{paths[index + 1]}>; rel="next"')
                self.end_headers()
                self.wfile.write(response.body)

        with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            origin = f"http://127.0.0.1:{server.server_port}"
            shim = tmp_path / "fixture-gh.sh"
            shim.write_text(
                "#!/bin/bash\nset -euo pipefail\n"
                f"[[ $# -ge 2 && $1 == api && $2 == {shlex.quote(ENDPOINT)} ]] || exit 90\n"
                "shift 2\nargs=(\"$@\")\nfilter_seen=0\npaginate_seen=0\ninclude_seen=0\n"
                "while [[ $# -gt 0 ]]; do\n"
                "  case \"$1\" in\n"
                "    --jq) [[ $# -ge 2 && $filter_seen == 0 ]] || exit 91; filter_seen=1; shift 2 ;;\n"
                "    --paginate) [[ $paginate_seen == 0 ]] || exit 92; paginate_seen=1; shift ;;\n"
                "    --include) [[ $include_seen == 0 ]] || exit 93; include_seen=1; shift ;;\n"
                "    *) exit 94 ;;\n"
                "  esac\ndone\n[[ $filter_seen == 1 ]] || exit 95\n"
                # Forward the original flags. In particular, never inject
                # --paginate or --include and accidentally repair the guard.
                f"exec {shlex.quote(gh.as_posix())} api {shlex.quote(origin + '/' + ENDPOINT)} \"${{args[@]}}\"\n",
                encoding="utf-8", newline="\n",
            )
            shim.chmod(0o700)
            env["GH"] = shim.as_posix()
            thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True)
            thread.start()
            try:
                result = subprocess.run(
                    [str(bash), GUARD.as_posix(), *args], cwd=tmp_path,
                    env=env, stdin=subprocess.DEVNULL, text=True,
                    capture_output=True, timeout=20, check=False,
                )
            finally:
                server.shutdown()
                thread.join(timeout=5)

        assert all(auth in {None, f"token {FIXTURE_TOKEN}", f"Bearer {FIXTURE_TOKEN}"} for _, auth in requests)
        assert result.stdout == "", "Guard diagnostics must not leak into stdout"
        return result, [path for path, _ in requests]

    return run


def _assert_query_failed(result: subprocess.CompletedProcess) -> None:
    assert result.returncode == 2, result.stderr
    assert "check-open-rollover:" in result.stderr
    assert CLEAN_MESSAGE not in result.stderr


def test_blocks_rollover_beyond_first_hundred(run_guard) -> None:
    result, requests = run_guard([
        _json_response([_pr(i) for i in range(1, 101)]),
        _json_response([_pr(101, "chore(release): v1.1.0 baseline rollover")]),
    ])
    assert len(requests) == 2
    assert result.returncode == 1, result.stderr
    assert "#101 https://github.com/example/ppds/pull/101" in result.stderr
    assert CLEAN_MESSAGE not in result.stderr


def test_allows_only_after_all_pages_are_checked(run_guard) -> None:
    result, requests = run_guard([
        _json_response([_pr(1, "fix: baseline rollover"), _pr(2, "chore(release): ordinary change")]),
        _json_response([_pr(3)]), _json_response([_pr(4)]),
    ])
    assert len(requests) == 3
    assert result.returncode == 0, result.stderr
    assert CLEAN_MESSAGE in result.stderr


def test_empty_success_is_clean(run_guard) -> None:
    result, requests = run_guard([_json_response([])])
    assert len(requests) == 1
    assert result.returncode == 0, result.stderr
    assert CLEAN_MESSAGE in result.stderr


def test_matching_pr_preserves_url_fallback(run_guard) -> None:
    pr = _pr(7, "chore(release): v1.0.0 baseline rollover")
    pr["url"] = pr.pop("html_url")
    result, requests = run_guard([_json_response([pr])])
    assert len(requests) == 1
    assert result.returncode == 1, result.stderr
    assert f'#7 {pr["url"]}' in result.stderr


@pytest.mark.parametrize("status", [401, 500])
def test_api_failures_fail_closed(run_guard, status: int) -> None:
    result, requests = run_guard([_json_response({"message": "fixture API failure"}, status)])
    assert len(requests) == 1
    _assert_query_failed(result)


@pytest.mark.parametrize("first_page_matches", [False, True])
def test_late_page_failure_fails_closed(run_guard, first_page_matches: bool) -> None:
    title = "chore(release): v1.0.0 baseline rollover" if first_page_matches else "fix: ordinary change"
    result, requests = run_guard([
        _json_response([_pr(1, title)]),
        _json_response({"message": "fixture late-page failure"}, 500),
    ])
    assert len(requests) == 2
    _assert_query_failed(result)


@pytest.mark.parametrize("body", [
    pytest.param(b"{invalid JSON", id="invalid-json"),
    pytest.param(b"", id="empty-body"),
    pytest.param(b"  \r\n\t", id="whitespace-only-body"),
    pytest.param(b"[] []", id="concatenated-json"),
    pytest.param(b"[] trailing garbage", id="trailing-garbage"),
    pytest.param(b"{}", id="object"),
    pytest.param(b"null", id="null"),
    pytest.param(b'"unexpected"', id="string"),
    pytest.param(b"123", id="number"),
    pytest.param(b"[null]", id="null-pr"),
])
def test_invalid_api_responses_fail_closed(run_guard, body: bytes) -> None:
    result, requests = run_guard([Response(body)])
    assert len(requests) == 1
    _assert_query_failed(result)


@pytest.mark.parametrize(("field", "value"), [
    ("title", None), ("title", 42),
    ("number", None), ("number", "1"), ("number", 0), ("number", 1.5),
    ("html_url", None), ("html_url", 42), ("html_url", ""),
])
def test_invalid_pr_fields_fail_closed(run_guard, field: str, value) -> None:
    # An invalid non-matching PR must not be silently filtered out either.
    pr = {**_pr(1), field: value}
    result, requests = run_guard([_json_response([pr])])
    assert len(requests) == 1
    _assert_query_failed(result)


@pytest.mark.parametrize("field", ["title", "number", "html_url"])
def test_missing_pr_fields_fail_closed(run_guard, field: str) -> None:
    pr = _pr(1)
    del pr[field]
    result, requests = run_guard([_json_response([pr])])
    assert len(requests) == 1
    _assert_query_failed(result)


def test_malformed_later_page_fails_closed(run_guard) -> None:
    result, requests = run_guard([
        _json_response([_pr(1)]), Response(b"{invalid second page"),
    ])
    assert len(requests) == 2
    _assert_query_failed(result)


@pytest.mark.parametrize("status", [201, 204])
@pytest.mark.parametrize("late_page", [False, True])
def test_non_200_success_status_fails_closed(run_guard, status: int, late_page: bool) -> None:
    responses = [_json_response([_pr(1)])] if late_page else []
    responses.append(Response(b"" if status == 204 else b"[]", status))
    result, requests = run_guard(responses)
    assert len(requests) == len(responses)
    _assert_query_failed(result)


def test_missing_repo_value_fails_without_api_call(run_guard) -> None:
    result, requests = run_guard([], args=("--repo",))
    assert requests == []
    _assert_query_failed(result)
