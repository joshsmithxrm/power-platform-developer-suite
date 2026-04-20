# Workflow Env-Var Contract

This file documents environment variables that affect skill and hook behavior. Skills document their env dependencies here; hooks reference it for opt-out logic.

Treat this as the canonical surface: if a skill reads an env var, or a hook short-circuits on one, it is listed here. When adding a new env var, update this file in the same PR.

## Summary Table

| Env Var | Set By | Consumed By (hooks) | Consumed By (skills) | Effect |
|---------|--------|---------------------|----------------------|--------|
| `PPDS_PIPELINE` | `scripts/pipeline.py` (non-interactive orchestrator), `/implement` when invoked by pipeline | `session-start-workflow.py`, `session-stop-workflow.py`, `notify.py`, `protect-main-branch.py` | `implement` | Signals non-interactive pipeline mode. Hooks exit 0 without running their normal checks/output; `/implement` skips its interactive Step 6 (mandatory tail) because the orchestrator runs gates/verify/qa/review as separate sessions. |
| `PPDS_SHAKEDOWN` | `shakedown-workflow` skill when exercising pipelines against throwaway worktrees | `session-stop-workflow.py`, `notify.py`, `protect-main-branch.py` | `shakedown-workflow` | Marks the session as a shakedown run against disposable state. Hooks exit 0; downstream skills must not file real issues, open real PRs, or otherwise touch durable systems. |
| `CLAUDE_PROJECT_DIR` | Claude Code harness at session start | `_pathfix.py` (re-exported as `get_project_dir()`), `protect-main-branch.py`, all hooks that shell out to git | (indirectly all skills via hook path resolution) | Absolute path to the project root. Hooks normalize it for Git Bash / MSYS and fall back to `os.getcwd()` when unset. In worktrees it resolves to the worktree root, not the main repo. |

## PPDS_PIPELINE

### Set by
- `scripts/pipeline.py` — exported into the subprocess environment of every `claude -p` invocation.
- Any wrapper that is intentionally running Claude non-interactively and wants to suppress interactive hook output.

### Consumed by (hooks)
- `.claude/hooks/session-start-workflow.py` — when set, exits 0 immediately without reading stdin, running git, or printing workflow enforcement messages. Also used downstream to suppress the behavioral-rules preamble.
- `.claude/hooks/session-stop-workflow.py` — when set, exits 0 without the Stop-blocking logic; the orchestrator is responsible for stage sequencing.
- `.claude/hooks/notify.py` — when set, exits 0 without emitting desktop toasts. Pipeline sessions are headless.
- `.claude/hooks/protect-main-branch.py` — when set, exits 0 (pipeline runs under a worktree branch; main-branch protection is handled by the orchestrator's branch management).

### Consumed by (skills)
- `.claude/skills/implement/SKILL.md` — when set, the skill runs Steps 1–5 only and exits after the final phase is committed. In interactive mode (env unset) it runs the full Step 6 mandatory tail (gates/verify/qa/review).

### Effect
Pipeline mode disables interactive enforcement because the orchestrator drives stage transitions itself. Skills must not assume a human is watching; do not ask interactive questions, do not block on confirmation.

## PPDS_SHAKEDOWN

### Set by
- `.claude/skills/shakedown-workflow/SKILL.md` — exports `PPDS_SHAKEDOWN=1` when launching pipeline or hook-execution tests against throwaway worktrees.

### Consumed by (hooks)
- `.claude/hooks/session-stop-workflow.py` — when set, exits 0 (stop-blocking logic is noise during shakedown).
- `.claude/hooks/notify.py` — when set, exits 0 (no toast spam during batch shakedown runs).
- `.claude/hooks/protect-main-branch.py` — when set, exits 0 (shakedown runs in disposable worktrees; main-branch protection is moot).

### Consumed by (skills)
- `.claude/skills/shakedown-workflow/SKILL.md` — sets the variable and asserts in downstream tooling that issue-creation, PR-creation, and any other durable side effects are suppressed.
- Any skill that writes to GitHub or Dataverse must check `PPDS_SHAKEDOWN` and short-circuit before performing the side effect.

### Effect
Shakedown mode guarantees no real artifacts leak from exercise runs. Violations are bugs: if a skill files an issue or opens a PR during a shakedown, file a rule-drift finding in the next retro.

## CLAUDE_PROJECT_DIR

### Set by
- Claude Code harness — exported into every hook and skill subprocess automatically.

### Consumed by (hooks)
- `.claude/hooks/_pathfix.py` — `get_project_dir()` normalizes the value for Git Bash / MSYS and falls back to `os.getcwd()` when unset. Every other hook imports this helper rather than reading the env var directly.
- `.claude/hooks/protect-main-branch.py` — also logs the raw value for debugging.

### Consumed by (skills)
- Indirectly — skills that shell into hooks inherit the normalized value. Skills authoring new hook commands should reference `$CLAUDE_PROJECT_DIR` via forward slashes on Windows (see `workflow-verify/SKILL.md`).

### Effect
Portable path resolution across Windows cmd, Git Bash, and POSIX shells. In worktrees, resolves to the worktree root — hooks operating on a worktree see the worktree as the project root, not the main checkout.

## Adding a New Env Var

1. Add a row to the Summary Table above.
2. Add a section documenting set-by, consumed-by (hooks + skills), and effect.
3. In the skill that sets the variable, cross-link to this file in a Related Docs section.
4. In the hook or skill that consumes it, guard with `os.environ.get(...)` (not `os.environ[...]` — unset is always a valid state).
5. If removal of a variable is contemplated, file a retro observation first; env vars are an implicit contract between skills and hooks.

## Related Docs

- `CLAUDE.md` — repo-level rules
- `.claude/interaction-patterns.md` — agent topology and decision UX
- `.claude/skills/workflow-verify/SKILL.md` — testing patterns for hooks and skills
- `.claude/skills/shakedown-workflow/SKILL.md` — reference implementation of `PPDS_SHAKEDOWN`
- `.claude/skills/implement/SKILL.md` — reference implementation of `PPDS_PIPELINE` detection
