# Workflow Env-Var Contract

This file documents environment variables that affect skill and hook behavior. Skills document their env dependencies here; hooks reference it for opt-out logic.

Treat this as the canonical surface: if a skill reads an env var, or a hook short-circuits on one, it is listed here. When adding a new env var, update this file in the same PR.

## Summary Table

| Env Var | Set By | Consumed By (hooks) | Consumed By (skills) | Effect |
|---------|--------|---------------------|----------------------|--------|
| `PPDS_SHAKEDOWN` | `/shakedown` skill when validating surfaces against disposable state | `shakedown-safety.py`, `shakedown-readonly-guard.py`, `protect-main-branch.py` | `shakedown` | Marks the session as a shakedown run. Env allowlist + write-blocks apply; downstream steps must not file real issues, open real PRs, or otherwise touch durable systems. |
| `CLAUDE_PROJECT_DIR` | Claude Code harness at session start | `_pathfix.py` (re-exported as `get_project_dir()`), `protect-main-branch.py`, all hooks that shell out to git | (indirectly all skills via hook path resolution) | Absolute path to the project root. Hooks normalize it for Git Bash / MSYS and fall back to `os.getcwd()` when unset. In worktrees it resolves to the worktree root, not the main repo. |

## PPDS_SHAKEDOWN

### Set by
- `.claude/skills/shakedown/SKILL.md` — exports `PPDS_SHAKEDOWN=1` when exercising product surfaces against throwaway state.

### Consumed by (hooks)
- `.claude/hooks/shakedown-safety.py` — enforces the safe-environment allowlist (`safety.shakedown_safe_envs` in `.claude/settings.json`) and blocks writes during shakedown.
- `.claude/hooks/shakedown-readonly-guard.py` — enforces the read-only claim of the shakedown phase.
- `.claude/hooks/protect-main-branch.py` — when set, exits 0 (shakedown runs in disposable worktrees; main-branch protection is moot).

### Consumed by (skills)
- `.claude/skills/shakedown/SKILL.md` — sets the variable and asserts in downstream tooling that issue-creation, PR-creation, and any other durable side effects are suppressed.
- Any skill that writes to GitHub or Dataverse must check `PPDS_SHAKEDOWN` and short-circuit before performing the side effect.

### Effect
Shakedown mode guarantees no real artifacts leak from exercise runs. Violations are bugs — fix the offending skill in the same PR that discovers the leak.

## CLAUDE_PROJECT_DIR

### Set by
- Claude Code harness — exported into every hook and skill subprocess automatically.

### Consumed by (hooks)
- `.claude/hooks/_pathfix.py` — `get_project_dir()` normalizes the value for Git Bash / MSYS and falls back to `os.getcwd()` when unset. Every other hook imports this helper rather than reading the env var directly.
- `.claude/hooks/protect-main-branch.py` — also logs the raw value for debugging.

### Consumed by (skills)
- Indirectly — skills that shell into hooks inherit the normalized value. Skills authoring new hook commands should reference `$CLAUDE_PROJECT_DIR` via forward slashes on Windows.

### Effect
Portable path resolution across Windows cmd, Git Bash, and POSIX shells. In worktrees, resolves to the worktree root — hooks operating on a worktree see the worktree as the project root, not the main checkout.

## Adding a New Env Var

1. Add a row to the Summary Table above.
2. Add a section documenting set-by, consumed-by (hooks + skills), and effect.
3. In the skill that sets the variable, cross-link to this file in a Related Docs section.
4. In the hook or skill that consumes it, guard with `os.environ.get(...)` (not `os.environ[...]` — unset is always a valid state).
5. Env vars are an implicit contract between skills and hooks — when removing one, sweep both sides in the same PR and note the rationale in the PR description.

## Bash Tool Portability

The Bash tool runs commands through Git Bash / MSYS, not PowerShell. Two
runtime anti-patterns recur (originally from #1130, #1131):

### Anti-pattern 1: PowerShell cmdlets via Bash tool

PowerShell cmdlets (`Test-Path`, `Get-Item`, `Remove-Item`, etc.) fail with
exit 127 — the Bash tool's shell does not resolve them. Use POSIX
equivalents or shell out to Python:

| Instead of | Use |
|------------|-----|
| `Test-Path specs/foo.md` | `[ -e specs/foo.md ] && echo exists \|\| echo missing` |
| `Test-Path specs/foo.md` | `python -c "import os; print(os.path.exists('specs/foo.md'))"` |
| `Get-ChildItem docs` | `ls docs` |
| `Remove-Item -Recurse tmp` | `rm -rf tmp` |

PowerShell-native scripts (`scripts/*.ps1`) are exempt — they run under
`pwsh.exe`, not the Bash tool's shell.

### Anti-pattern 2: Windows backslash paths in inline Python literals

Inline `python -c "..."` and heredoc snippets passed via the Bash tool
treat the payload as Python source code. Backslashes in regular string
literals become escape sequences — `'specs\today.md'` becomes
`specs<TAB>oday.md` (a literal tab + truncated path), and
`'C:\Users\foo'` is a `SyntaxError: (unicode error) ...`.

Fix the path literal at write time, not the file system at read time:

| Instead of | Use |
|------------|-----|
| `python -c "open('specs\today.md')"` | `python -c "open('specs/today.md')"` (forward slashes) |
| `python -c "open('C:\Users\foo')"` | `python -c "open(r'C:\Users\foo')"` (raw string) |
| Inline heredoc + Windows path | Save to a `.py` file and run it — escapes go away |

POSIX paths work on Windows Python — forward slashes are the correct
default for cross-platform inline Python.

Regression test: `tests/test_skill_bash_portability.py` scans every
`.claude/skills/**/*.md` for these patterns. Edits that reintroduce them
fail in CI and `/gates`.

## Related Docs

- `CLAUDE.md` — repo-level rules
- `.claude/skills/shakedown/SKILL.md` — reference implementation of `PPDS_SHAKEDOWN`
