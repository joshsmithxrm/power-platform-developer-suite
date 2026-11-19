# Workflow Env-Var Contract

Environment variables and files that connect the checked-in skills (`shakedown`, `verify`) to the checked-in hooks (`.claude/hooks/`). If a skill reads one, or a hook short-circuits on one, it is listed here. When adding or removing one, update this file in the same PR.

## Summary Table

| Name | Set by | Read by | Effect |
|------|--------|---------|--------|
| `.claude/state/shakedown-active.json` | The `shakedown` skill (Phase 0) | `shakedown-safety.py`, C# `IShakedownGuard` | Arms the shakedown write-block. Used inside Claude Code sessions. Stale after 24h. |
| `PPDS_SHAKEDOWN` | The operator's shell or CI | `shakedown-safety.py`, `IShakedownGuard` | Same write-block, for shell scripts and CI. The name can be changed with `safety.readonly_env_var` in `.claude/settings.json`. |
| `PPDS_SAFE_ENVS` | The operator (optional) | `shakedown-safety.py` | Comma-separated safe environment names; one of three allowlist sources. |
| `CLAUDE_PROJECT_DIR` | Claude Code | `shakedown-safety.py` | Project root. The hook normalizes it for Git Bash / MSYS and falls back to the working directory. In a worktree it is the worktree root. |

## Shakedown write-block

Two activation sources, either is enough: the sentinel file above, or `PPDS_SHAKEDOWN=1`. The Bash tool starts a fresh shell for every call, so an `export` in one call never reaches the hook; inside Claude Code the sentinel file is the source that works, and `PPDS_SHAKEDOWN` is normally unset. To decide whether a shakedown is running, check for a fresh sentinel (under 24h) as well as `PPDS_SHAKEDOWN`. Any step that writes to GitHub or Dataverse must stop while the write-block is armed.

## Environment allowlist

The hook gates every `ppds` command to a list of safe environments, but only once a list is configured. The first source that names an environment wins: `PPDS_SAFE_ENVS`, then `safety.shakedown_safe_envs` in `.claude/settings.json` (checked in as `[]`), then the legacy `.claude/state/safe-envs.json`. With none configured the gate is off; a malformed one blocks everything. Rules, matching and fail-safe behaviour: `docs/SAFE-SHAKEDOWN.md`.

## Bash Tool Portability

The Bash tool runs commands through Git Bash / MSYS, not PowerShell. Two
runtime anti-patterns recur (originally from #1130, #1131):

### Anti-pattern 1: PowerShell cmdlets via Bash tool

PowerShell cmdlets (`Test-Path`, `Get-Item`, `Remove-Item`, etc.) fail with
exit 127 because the Bash tool's shell does not resolve them. Use POSIX
equivalents or shell out to Python:

| Instead of | Use |
|------------|-----|
| `Test-Path specs/foo.md` | `[ -e specs/foo.md ] && echo exists \|\| echo missing` |
| `Test-Path specs/foo.md` | `python -c "import os; print(os.path.exists('specs/foo.md'))"` |
| `Get-ChildItem docs` | `ls docs` |
| `Remove-Item -Recurse tmp` | `rm -rf tmp` |

PowerShell-native scripts (`scripts/*.ps1`) are exempt: they run under
`pwsh.exe`, not the Bash tool's shell.

### Anti-pattern 2: Windows backslash paths in inline Python literals

Inline `python -c "..."` and heredoc snippets passed via the Bash tool
treat the payload as Python source code. Backslashes in regular string
literals become escape sequences: `'specs\today.md'` becomes
`specs<TAB>oday.md` (a literal tab + truncated path), and
`'C:\Users\foo'` is a `SyntaxError: (unicode error) ...`.

| Instead of | Use |
|------------|-----|
| `python -c "open('specs\today.md')"` | `python -c "open('specs/today.md')"` (forward slashes) |
| `python -c "open('C:\Users\foo')"` | `python -c "open(r'C:\Users\foo')"` (raw string) |
| Inline heredoc + Windows path | Save to a `.py` file and still use forward slashes or raw strings for the path literals (Python parses `\t`/`\U` escapes in a file too) |

POSIX paths work on Windows Python; forward slashes are the correct
default for cross-platform inline Python.

Regression test: `tests/test_skill_bash_portability.py` scans every
`.md` file under `.claude/skills/` for these patterns, and checks that this
section stays in place. Edits that reintroduce them fail in CI.
