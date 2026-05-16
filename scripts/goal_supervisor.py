#!/usr/bin/env python3
"""Goal supervisor — spawn N PR-stack workers and poll their states.

See specs/feat-1069-supervisor-pattern.md.

The supervisor is best-effort: workers run pipeline.py end-to-end on their
own and pr_monitor delivers the per-PR notification independently. The
supervisor adds aggregate notification and silent monitoring on top of that
durable substrate — its absence does not block individual PRs from
shipping.

Public API:
    DEFAULT_POLL_INTERVAL: int        # seconds (270 — cache-warm)
    SCHEMA_VERSION: str               # "1.1"
    WORKER_PROMPT_TEMPLATE: str
    HAIKU_PREDICATE_TEMPLATE: str
    build_envelope_v11(stack_envelope, *, supervisor_worktree,
                       poll_interval=DEFAULT_POLL_INTERVAL) -> dict
    render_worker_prompt(entry, *, spec) -> str
    render_haiku_prompt(*, entry_id, title, session_state, workflow_phase,
                        pr_url, pr_state) -> str
    spawn(stack_path, *, supervisor_worktree, poll_interval=270,
          repo_root=None, subprocess_runner=None,
          inbox_sender=None) -> dict
    poll(*, supervisor_worktree, jobs_dir=None,
         workflow_state_reader=None, gh_runner=None, haiku_runner=None,
         clock=None) -> dict

CLI:
    python scripts/goal_supervisor.py spawn <stack-json-path>
        [--supervisor-worktree <abs-path>] [--poll-interval <sec>]
    python scripts/goal_supervisor.py poll
        [--worktree <abs-path>]

Constitution: I1 — stdout is JSON-only; progress, errors → stderr.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Optional

# Sibling-script imports (conftest.py and start-bg-spawn.py both prepend
# scripts/ to sys.path; do the same here so direct CLI invocation works).
_SCRIPT_DIR = str(Path(__file__).resolve().parent)
if _SCRIPT_DIR not in sys.path:
    sys.path.insert(0, _SCRIPT_DIR)

import pr_stack  # noqa: E402  (validate_envelope accepts any "1." prefix)
import supervisor_msg  # noqa: E402

# claude_dispatch is imported lazily inside _default_haiku_runner so the
# CLI smoke and most tests do not require its (heavier) module init.

SCHEMA_VERSION = "1.1"
DEFAULT_POLL_INTERVAL = 270
JOBS_DIR = Path(os.path.expanduser("~/.claude/jobs"))

ENVELOPE_RELATIVE_PATH = Path(".workflow") / "goal-envelope.json"

VALID_ENTRY_GOAL_STATES = (
    "pending",
    "spawned",
    "working",
    "blocked",
    "merged",
    "error",
)

VALID_OVERALL_GOAL_STATES = (
    "spawning",
    "polling",
    "all_merged",
    "escalated",
    "error",
)

# ---------------------------------------------------------------------------
# Templates
# ---------------------------------------------------------------------------

WORKER_PROMPT_TEMPLATE = """\
Task brief — PR stack worker
Entry: {id} — {title}
Worktree: {worktree_path}
Spec: {spec}
Plan: {plan}
Branch: feat/{branch_suffix}
Issues: {issue_numbers}

You are running in headless mode via the goal supervisor. Do not ask
clarifying questions — make reasonable decisions and proceed.

Workflow contract:
1. Read CLAUDE.md, specs/CONSTITUTION.md, .claude/interaction-patterns.md.
2. This worker has a pre-approved plan ({plan}). Skip /design.
   Run `python scripts/pipeline.py --spec {spec} --plan {plan}` directly.
   On failure: python scripts/pipeline.py --resume (or --from <stage>).
3. After `python scripts/pipeline.py` exits successfully (pipeline includes
   /pr internally): launch pr_monitor via Bash run_in_background=true:
     python scripts/pr_monitor.py --worktree {worktree_path} --pr <PR-number>
   Claude Code will re-engage you when pr_monitor exits.
4. At re-engagement: read .workflow/pr-monitor-result.json and produce a final
   summary covering actual PR state (ready / merged / escalated / error /
   blocked). Terminate.

Supervisor note (read first, before any other action):
  python scripts/supervisor_msg.py read --consume
Goal context: {title}. Plan at {plan}. Implement and ship — no design phase needed.
"""

HAIKU_PREDICATE_TEMPLATE = """\
You are a one-turn evaluator. Assess this PR worker's completion state.

Context:
  entry_id: {entry_id}
  title: {title}
  session_state: {session_state}
  workflow_phase: {workflow_phase}
  pr_url: {pr_url}
  pr_state: {pr_state}

"session_state=done" means the Claude session exited. If pr_url is set and pr_state
is MERGED, the worker is definitely done. If session_state=done but no pr_url exists,
something likely went wrong.

Reply with EXACTLY this JSON and nothing else:
{{"verdict": "done|blocked|working|error", "confidence": "high|low", "reason": "<one sentence>"}}
"""


# ---------------------------------------------------------------------------
# Rendering
# ---------------------------------------------------------------------------

def render_worker_prompt(entry: dict, *, spec: str) -> str:
    """Render the worker prompt for one stack entry.

    Missing optional fields (issue_numbers) are rendered as an empty string
    rather than KeyError, so tests with minimal entries succeed.
    """
    issue_numbers = entry.get("issue_numbers") or ""
    if isinstance(issue_numbers, list):
        issue_numbers = ", ".join(str(x) for x in issue_numbers)
    return WORKER_PROMPT_TEMPLATE.format(
        id=entry["id"],
        title=entry["title"],
        worktree_path=entry["worktree_path"],
        spec=spec,
        plan=entry["plan"],
        branch_suffix=entry["branch_suffix"],
        issue_numbers=issue_numbers,
    )


def render_haiku_prompt(
    *,
    entry_id: str,
    title: str,
    session_state: str,
    workflow_phase: str,
    pr_url: str,
    pr_state: str,
) -> str:
    return HAIKU_PREDICATE_TEMPLATE.format(
        entry_id=entry_id,
        title=title,
        session_state=session_state,
        workflow_phase=workflow_phase,
        pr_url=pr_url,
        pr_state=pr_state,
    )


# ---------------------------------------------------------------------------
# Envelope construction
# ---------------------------------------------------------------------------

def build_envelope_v11(
    stack_envelope: dict,
    *,
    supervisor_worktree: str,
    poll_interval: int = DEFAULT_POLL_INTERVAL,
    supervisor_session_id: str = "",
) -> dict:
    """Return a deep-merged v1.1 envelope ready for disk.

    The input stack_envelope is expected to be a validated #1070-α envelope.
    We bump schema_version to 1.1, add the runtime-state envelope fields,
    and initialize each stack entry with goal_state="pending".
    """
    envelope: dict = json.loads(json.dumps(stack_envelope))  # cheap deep copy
    envelope["schema_version"] = SCHEMA_VERSION
    envelope["supervisor_worktree"] = str(supervisor_worktree)
    envelope["goal_poll_interval_sec"] = int(poll_interval)
    envelope["supervisor_session_id"] = supervisor_session_id
    envelope["goal_state"] = "spawning"

    for entry in envelope["stack"]:
        entry.setdefault("goal_state", "pending")
    return envelope


# ---------------------------------------------------------------------------
# Subprocess helpers (injectable for tests)
# ---------------------------------------------------------------------------

def _default_subprocess_runner(cmd: list, *, cwd: Optional[str] = None,
                               timeout: Optional[int] = None) -> subprocess.CompletedProcess:
    return subprocess.run(
        cmd,
        cwd=cwd,
        capture_output=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
        check=True,
    )


def _default_inbox_sender(worktree_path: str, message: str) -> None:
    supervisor_msg.send(worktree_path, "note", message=message)


def _default_gh_runner(pr_number: int) -> Optional[str]:
    """Return PR state string (OPEN/MERGED/CLOSED) or None on failure."""
    try:
        proc = subprocess.run(
            ["gh", "pr", "view", "--", str(pr_number),
             "--json", "state", "--jq", ".state"],
            capture_output=True,
            encoding="utf-8",
            errors="replace",
            timeout=30,
            check=True,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError, subprocess.CalledProcessError):
        return None
    out = (proc.stdout or "").strip()
    return out or None


def _default_haiku_runner(prompt: str) -> str:
    """Return Haiku stdout (expected to be one-line JSON)."""
    import claude_dispatch  # local — avoid hard dep at import time
    # Headless mode requires a stage_log path for the transcript.
    tf = tempfile.NamedTemporaryFile(suffix=".jsonl", delete=False)
    log_path = tf.name
    tf.close()
    try:
        handle = claude_dispatch.spawn(
            mode="headless",
            prompt=prompt,
            caller="goal_supervisor",
            model="haiku",
            stage_log=log_path,
        )
        # HeadlessHandle.wait() returns exit code; output() returns stdout text.
        handle.wait(timeout=120)
        return handle.output() or ""
    except Exception:
        return ""
    finally:
        if os.path.exists(log_path):
            try:
                os.unlink(log_path)
            except OSError:
                pass


def _default_workflow_state_reader(worktree_path: str) -> dict:
    """Read <worktree>/.workflow/state.json — return {} on missing/invalid."""
    p = Path(worktree_path) / ".workflow" / "state.json"
    if not p.exists():
        return {}
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return {}


def _read_job_state(short: str, jobs_dir: Path) -> dict:
    """Read ~/.claude/jobs/<short>/state.json — return {} on missing."""
    p = jobs_dir / short / "state.json"
    if not p.exists():
        return {}
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return {}


# ---------------------------------------------------------------------------
# spawn
# ---------------------------------------------------------------------------

def _atomic_write_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.NamedTemporaryFile(
        mode="w", encoding="utf-8",
        dir=str(path.parent), suffix=".tmp", delete=False,
    )
    try:
        json.dump(data, tmp, indent=2)
        tmp.write("\n")
        tmp.flush()
        tmp.close()
        os.replace(tmp.name, path)
    except Exception:
        try:
            os.unlink(tmp.name)
        except OSError:
            pass
        raise


def _spawn_one_entry(
    entry: dict,
    *,
    spec: str,
    repo_root: str,
    subprocess_runner: Callable,
    inbox_sender: Callable,
) -> None:
    """Mutate entry in-place: create worktree, spawn worker, record fields.

    On any failure, sets entry["goal_state"]="error" and entry["error"] with
    the failure message; writes a stderr line. Always returns None.
    """
    branch_suffix = entry["branch_suffix"]
    branch = f"feat/{branch_suffix}"

    # 1. Create the worktree.
    create_cmd = [
        sys.executable, "scripts/worktree-create.py",
        "--name", branch_suffix,
        "--branch", branch,
    ]
    cp = subprocess_runner(create_cmd, cwd=repo_root, timeout=120)
    if cp.returncode != 0:
        msg = f"worktree-create failed for {entry['id']}: {(cp.stderr or '').strip()}"
        print(msg, file=sys.stderr)
        entry["goal_state"] = "error"
        entry["error"] = msg
        return

    worktree_abs = os.path.abspath(os.path.join(repo_root, ".worktrees", branch_suffix))
    entry["worktree_path"] = worktree_abs

    # 2. Render the worker prompt and write to a temp file.
    prompt_text = render_worker_prompt(entry, spec=spec)
    prompt_dir = Path(repo_root) / ".workflow"
    prompt_dir.mkdir(parents=True, exist_ok=True)
    prompt_file = None
    try:
        tf = tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8",
            dir=str(prompt_dir), prefix=f"worker-prompt-{branch_suffix}-",
            suffix=".txt", delete=False,
        )
        tf.write(prompt_text)
        tf.flush()
        tf.close()
        prompt_file = Path(tf.name)

        # 3. Spawn the worker.
        spawn_cmd = [
            sys.executable, "scripts/start-bg-spawn.py",
            "--worktree-abs", worktree_abs,
            "--branch", branch,
            "--prompt-file", str(prompt_file),
            "--permission-mode", "bypassPermissions",
            "--model", "sonnet",
        ]
        cp = subprocess_runner(spawn_cmd, cwd=repo_root, timeout=120)
        if cp.returncode != 0:
            msg = f"start-bg-spawn failed for {entry['id']}: {(cp.stderr or '').strip()}"
            print(msg, file=sys.stderr)
            entry["goal_state"] = "error"
            entry["error"] = msg
            return

        try:
            spawn_result = json.loads((cp.stdout or "").strip().splitlines()[-1])
        except (json.JSONDecodeError, IndexError) as exc:
            msg = f"start-bg-spawn output not JSON for {entry['id']}: {exc}"
            print(msg, file=sys.stderr)
            entry["goal_state"] = "error"
            entry["error"] = msg
            return

        entry["session_short"] = spawn_result.get("short", "")
        entry["session_id"] = spawn_result.get("sessionId", "")
        entry["spawned_at"] = datetime.now(timezone.utc).isoformat()
        entry["goal_state"] = "spawned"

        # 4. Deliver the inbox note.
        try:
            inbox_sender(
                worktree_abs,
                f"Goal context: {entry['title']}. Plan at {entry['plan']}. "
                f"Implement and ship — no design phase needed.",
            )
        except Exception as exc:  # noqa: BLE001 — surface but don't undo spawn
            print(f"inbox note failed for {entry['id']}: {exc}", file=sys.stderr)
    finally:
        if prompt_file is not None and prompt_file.exists():
            try:
                prompt_file.unlink()
            except OSError:
                pass


def spawn(
    stack_path: str,
    *,
    supervisor_worktree: str,
    poll_interval: int = DEFAULT_POLL_INTERVAL,
    repo_root: Optional[str] = None,
    subprocess_runner: Optional[Callable] = None,
    inbox_sender: Optional[Callable] = None,
) -> dict:
    """Spawn N workers from stack.json, persist envelope, return summary.

    Returns: {"spawned": N, "envelope": "<abs-path>", "entries": [...]}.
    The summary is returned (not just printed) so callers and tests can
    introspect without parsing stdout.
    """
    runner = subprocess_runner or _default_subprocess_runner
    sender = inbox_sender or _default_inbox_sender

    try:
        stack_envelope = json.loads(Path(stack_path).read_text(encoding="utf-8"))
    except OSError as exc:
        raise FileNotFoundError(f"stack.json not readable: {exc}") from exc
    except json.JSONDecodeError as exc:
        raise ValueError(f"stack.json invalid JSON: {exc}") from exc

    # Reject obvious wrong-major envelopes before validate_envelope (which
    # already enforces the "1." prefix) so we can produce a tailored AC-03
    # error message.
    sv = stack_envelope.get("schema_version")
    if isinstance(sv, str) and not sv.startswith("1."):
        raise ValueError(f"unsupported schema_version major: {sv!r}")

    pr_stack.validate_envelope(stack_envelope)

    if repo_root is None:
        repo_root = str(Path(supervisor_worktree).resolve())

    envelope = build_envelope_v11(
        stack_envelope,
        supervisor_worktree=supervisor_worktree,
        poll_interval=poll_interval,
    )

    spec = envelope["spec"]
    for entry in envelope["stack"]:
        _spawn_one_entry(
            entry,
            spec=spec,
            repo_root=repo_root,
            subprocess_runner=runner,
            inbox_sender=sender,
        )

    out_path = Path(supervisor_worktree) / ENVELOPE_RELATIVE_PATH
    _atomic_write_json(out_path, envelope)

    spawned_count = sum(1 for e in envelope["stack"] if e.get("goal_state") == "spawned")
    return {
        "spawned": spawned_count,
        "envelope": str(out_path),
        "entries": envelope["stack"],
    }


# ---------------------------------------------------------------------------
# poll
# ---------------------------------------------------------------------------

def _evaluate_entry(
    entry: dict,
    *,
    jobs_dir: Path,
    workflow_state_reader: Callable,
    gh_runner: Callable,
    haiku_runner: Callable,
    now_iso: str,
) -> None:
    """Mutate entry in-place from the latest state snapshots."""
    if entry.get("goal_state") == "merged":
        return

    short = entry.get("session_short", "")
    job_state_data = _read_job_state(short, jobs_dir) if short else {}
    session_state = (job_state_data.get("state") or "").strip()
    needs = (job_state_data.get("needs") or "").strip()

    workflow_data = workflow_state_reader(entry.get("worktree_path", "")) or {}
    pr_url = workflow_data.get("pr", {}).get("url") if isinstance(workflow_data.get("pr"), dict) else workflow_data.get("pr_url")
    pr_url = pr_url or ""
    pr_number = entry.get("pr_number")
    if pr_number is None:
        pr_number = workflow_data.get("pr", {}).get("number") if isinstance(workflow_data.get("pr"), dict) else workflow_data.get("pr_number")
    if pr_number is not None:
        entry["pr_number"] = pr_number
    if pr_url and not entry.get("pr_url"):
        entry["pr_url"] = pr_url

    entry["last_polled_at"] = now_iso

    if session_state == "blocked" and needs:
        entry["goal_state"] = "blocked"
        entry["blocked_needs"] = needs
        return

    if session_state == "error":
        entry["goal_state"] = "error"
        return

    if (session_state == "done") or entry.get("pr_url"):
        pr_state = None
        if entry.get("pr_number") is not None:
            pr_state = gh_runner(int(entry["pr_number"]))
        if pr_state:
            entry["pr_state"] = pr_state
            if pr_state == "MERGED":
                entry["goal_state"] = "merged"
                entry["merged_at"] = now_iso
                return

        if session_state == "done" and not entry.get("pr_url"):
            # Ambiguous — ask Haiku.
            prompt = render_haiku_prompt(
                entry_id=entry.get("id", ""),
                title=entry.get("title", ""),
                session_state=session_state,
                workflow_phase=str(workflow_data.get("phase") or ""),
                pr_url=str(entry.get("pr_url") or ""),
                pr_state=str(entry.get("pr_state") or ""),
            )
            try:
                raw = haiku_runner(prompt) or ""
                verdict_doc = json.loads(raw.strip())
                verdict = str(verdict_doc.get("verdict") or "").lower()
            except (json.JSONDecodeError, AttributeError, TypeError):
                entry["goal_state"] = "error"
                entry["error"] = "haiku predicate parse failure"
                return
            if verdict in ("blocked", "working", "error"):
                entry["goal_state"] = verdict
            elif verdict == "done":
                # Haiku said done but no PR — treat as error.
                entry["goal_state"] = "error"
                entry["error"] = "haiku verdict=done but no PR url"
            else:
                entry["goal_state"] = "error"
                entry["error"] = f"haiku verdict unrecognized: {verdict!r}"
            return

        # session not done; pr_url present but not merged — keep working
        entry["goal_state"] = "working"
        return

    if session_state in ("working", "done"):
        entry["goal_state"] = "working"
        return

    # No usable session state — keep prior (or "spawned")
    entry.setdefault("goal_state", "spawned")


def _compute_overall_state(entries: list) -> str:
    if all(e.get("goal_state") == "merged" for e in entries):
        return "all_merged"
    if any(e.get("goal_state") in ("blocked", "error") for e in entries):
        return "escalated"
    return "polling"


def poll(
    *,
    supervisor_worktree: str,
    jobs_dir: Optional[Path] = None,
    workflow_state_reader: Optional[Callable] = None,
    gh_runner: Optional[Callable] = None,
    haiku_runner: Optional[Callable] = None,
    clock: Optional[Callable[[], str]] = None,
) -> dict:
    """Re-read envelope, update entries, write back, return verdict dict."""
    jobs_dir = jobs_dir or JOBS_DIR
    workflow_state_reader = workflow_state_reader or _default_workflow_state_reader
    gh_runner = gh_runner or _default_gh_runner
    haiku_runner = haiku_runner or _default_haiku_runner
    clock = clock or (lambda: datetime.now(timezone.utc).isoformat())

    env_path = Path(supervisor_worktree) / ENVELOPE_RELATIVE_PATH
    envelope = json.loads(env_path.read_text(encoding="utf-8"))

    now_iso = clock()
    for entry in envelope["stack"]:
        _evaluate_entry(
            entry,
            jobs_dir=jobs_dir,
            workflow_state_reader=workflow_state_reader,
            gh_runner=gh_runner,
            haiku_runner=haiku_runner,
            now_iso=now_iso,
        )

    envelope["goal_state"] = _compute_overall_state(envelope["stack"])
    _atomic_write_json(env_path, envelope)

    verdict = {
        "goal_state": envelope["goal_state"],
        "entries": envelope["stack"],
    }
    return verdict


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _resolve_supervisor_worktree(arg: Optional[str]) -> str:
    if arg:
        return str(Path(arg).resolve())
    try:
        proc = subprocess.run(
            ["git", "-c", "core.quotePath=off", "rev-parse", "--show-toplevel"],
            capture_output=True,
            encoding="utf-8",
            errors="replace",
            timeout=5,
            check=True,
        )
        return proc.stdout.strip()
    except (subprocess.TimeoutExpired, FileNotFoundError, subprocess.CalledProcessError):
        pass
    return str(Path.cwd().resolve())


def _cmd_spawn(args: argparse.Namespace) -> int:
    supervisor_worktree = _resolve_supervisor_worktree(args.supervisor_worktree)
    print(
        f"goal_supervisor: spawn stack={args.stack_path} "
        f"supervisor_worktree={supervisor_worktree} "
        f"poll_interval={args.poll_interval}",
        file=sys.stderr,
    )
    try:
        summary = spawn(
            args.stack_path,
            supervisor_worktree=supervisor_worktree,
            poll_interval=args.poll_interval,
        )
    except (FileNotFoundError, ValueError) as exc:
        print(f"goal_supervisor: {exc}", file=sys.stderr)
        return 1

    json.dump(summary, sys.stdout)
    sys.stdout.write("\n")
    if summary["spawned"] != len(summary["entries"]):
        return 1
    return 0


def _cmd_poll(args: argparse.Namespace) -> int:
    supervisor_worktree = _resolve_supervisor_worktree(args.worktree)
    print(
        f"goal_supervisor: poll supervisor_worktree={supervisor_worktree}",
        file=sys.stderr,
    )
    try:
        verdict = poll(supervisor_worktree=supervisor_worktree)
    except (FileNotFoundError, OSError, json.JSONDecodeError) as exc:
        print(f"goal_supervisor: {exc}", file=sys.stderr)
        return 0  # spec §poll #6 — always exit 0; callers parse JSON
    json.dump(verdict, sys.stdout)
    sys.stdout.write("\n")
    return 0


def main(argv: Optional[list] = None) -> int:
    parser = argparse.ArgumentParser(
        prog="goal_supervisor",
        description="Goal supervisor for PR-stack workers (#1069).",
    )
    sub = parser.add_subparsers(dest="cmd", required=True)

    sp = sub.add_parser("spawn", help="Spawn N workers from stack.json")
    sp.add_argument("stack_path")
    sp.add_argument("--supervisor-worktree", default=None)
    sp.add_argument("--poll-interval", type=int, default=DEFAULT_POLL_INTERVAL)
    sp.set_defaults(func=_cmd_spawn)

    pl = sub.add_parser("poll", help="Poll worker states; print verdict JSON")
    pl.add_argument("--worktree", default=None)
    pl.set_defaults(func=_cmd_poll)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
