# Safe Shakedown Enforcement

How PPDS enforces "do not accidentally write to a non-dev environment" during
shakedown and verify flows. Two PreToolUse hooks plus a small allowlist file
form a defense-in-depth boundary on top of the skill-level rules.

## Why this exists

The shakedown skills (`/shakedown`, `/ext-verify`, `/tui-verify`,
`/cli-verify`, `/mcp-verify`) drive live Dataverse environments. Without
explicit guardrails, an autonomous agent can:

1. Connect to the wrong environment (e.g. production instead of dev).
2. Run write/delete commands that mutate environment state.
3. Hit org-level resources accidentally.

The skills themselves contain rules ("do not run write commands during
shakedown"), but rules are advisory. The hooks below are **mechanical** and
fail closed: if the configuration is missing or the active env can't be
identified, the hook blocks rather than allows.

## Architecture

```
Bash tool invocation -> PreToolUse hooks (in order)
                        |
                        +-- dev-env-check.py        (always on)
                        |   gates ALL `ppds *` against the safe-env allowlist
                        |
                        +-- shakedown-readonly.py   (only when PPDS_SHAKEDOWN=1)
                            blocks `ppds * create/update/delete/...` patterns
```

Both hooks use the corrected JSON envelope contract from PR #816 (read
`payload["tool_input"]["command"]`, never the top-level `command`).

## Hook 1: dev-env-check.py

**Trigger:** every Bash invocation that mentions `ppds` or `ppds-mcp-server`.

**Decision tree:**

1. If the command does not mention `ppds`, the hook is a no-op (exit 0).
2. Resolve the allowlist:
   - `$PPDS_SAFE_ENVS` (comma-separated) wins if present.
   - Else `.claude/state/safe-envs.json` (`{"safe_envs": [...]}`).
   - Else **block** with a configuration-help message.
3. Resolve the active env: read `profiles.json` directly from
   `$PPDS_CONFIG_DIR` (or `%LOCALAPPDATA%\PPDS` / `~/.ppds`). The hook does
   NOT shell out to `ppds env who` -- that would be circular for a
   PreToolUse-on-Bash hook and would itself trip the hook.
4. Apply per-subcommand rules:
   - `ppds env list` / `current` / `who` / `config` / `type` / `show`:
     always allowed (read-only diagnostics; `who` issues only WhoAmI).
   - `ppds env switch <name>` / `select <name>` / `use <name>`: allowed
     when the **target** name is in the allowlist (so users can switch
     INTO a safe env even from an unsafe one).
   - All other `ppds *` invocations: allowed only when the **active** env
     is in the allowlist.
   - `ppds-mcp-server`: same rule as `ppds *` -- the MCP server inherits
     the active env at startup.

**Match semantics:** the active env's `Environment.DisplayName`,
`Environment.UniqueName`, the host of `Environment.Url`, and the leftmost
host label are all compared case-insensitively against the allowlist.

**Fail-safe:** if the profile store is missing, malformed, or has no
active profile, the hook **blocks** (the spec is "in doubt, block").

## Hook 2: shakedown-readonly.py

**Trigger:** every Bash invocation, but only when `PPDS_SHAKEDOWN=1` is set.

**Blocked verbs (any surface):** `create`, `update`, `delete`, `remove`,
`import`, `apply`, `register`, `unregister`, `publish`, `truncate`, `drop`,
`reset`, `set`.

**Special cases:**

- `ppds plugins deploy` is blocked unless `--dry-run` (or `-n`) is present.
- Anything under `ppds logs *` is read-only by definition.
- `ppds-mcp-server` is allowed -- it is long-running and can't be reasoned
  about ahead of time. The dev-env-check hook still gates which env it
  talks to.
- `ppds env switch/select` is read-only here because the dev-env-check
  hook already validates the target.

**Bypass:** `unset PPDS_SHAKEDOWN`. There is intentionally no softer bypass
marker -- if you need to write during shakedown, that is a deliberate
action.

## Configuration

### Add an env to the allowlist (per-machine)

Edit `.claude/state/safe-envs.json`:

```json
{
  "safe_envs": ["ppds-dev", "ppds-test"]
}
```

The file is committed as a template with an empty array. Local edits will
not conflict with concurrent sessions thanks to `merge=union` in
`.gitattributes`. Operators are expected to populate this once per machine.

### Override via env var (per-session)

```bash
export PPDS_SAFE_ENVS=ppds-dev,ppds-test
```

The env var takes precedence over the JSON file. Useful for CI / sandbox
runs where you want the allowlist scoped to a single shell.

### Activate read-only mode

```bash
export PPDS_SHAKEDOWN=1
```

Set automatically by Phase 0 of the `/shakedown` skill. The verify skills
(`/ext-verify`, `/tui-verify`, `/cli-verify`, `/mcp-verify`) inherit this
when they are invoked from a shakedown context.

## Common operator scenarios

### "I added a new dev env and want to use it for shakedown"

1. Add the env's friendly name to `.claude/state/safe-envs.json`.
2. `ppds env select <new-env>` -- the dev-env-check hook now allows it.
3. Re-run `/shakedown`.

### "The hook blocked a legitimate write outside shakedown"

The dev-env-check hook is **always on**. If you need to write to a
non-allowlisted env, add that env to the allowlist (per-session env var is
recommended for one-off writes, JSON file for permanent additions).

### "I need to run a real plugin deploy during a shakedown session"

Either:

- `unset PPDS_SHAKEDOWN` (preferred -- explicit), or
- Run `ppds plugins deploy --dry-run` to validate without writing, then
  unset and re-run for the real deploy.

## Related

- `docs/MERGE-POLICY.md` -- how PRs land on `main`.
- `.claude/skills/shakedown/SKILL.md` -- Phase 0 references this doc.
- `tests/hooks/test_dev_env_check.py`, `tests/hooks/test_shakedown_readonly.py`
  -- behavioral tests covering allowlist sources, switch gating, dry-run
  carve-out, fail-safe paths.
