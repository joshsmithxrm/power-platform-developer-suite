# Safe Shakedown Enforcement

How PPDS enforces "do not accidentally write to a non-dev environment" during
shakedown and verify flows. A single PreToolUse hook plus a small allowlist
block in `.claude/settings.json` form a defense-in-depth boundary on top of
the skill-level rules.

## Why this exists

The shakedown skills (`/shakedown`, `/ext-verify`, `/tui-verify`,
`/cli-verify`, `/mcp-verify`) drive live Dataverse environments. Without
explicit guardrails, an autonomous agent can:

1. Connect to the wrong environment (e.g. production instead of dev).
2. Run write/delete commands that mutate environment state.
3. Hit org-level resources accidentally.

The skills themselves contain rules ("do not run write commands during
shakedown"), but rules are advisory. The hook below is **mechanical** and
fails closed: if the configuration is missing or the active env can't be
identified, the hook blocks rather than allows.

## Architecture

```
Bash tool invocation -> PreToolUse hooks
                        |
                        +-- shakedown-safety.py        (always on)
                            1. Env allowlist gate (ALL ppds *)
                            2. Write-block (only when PPDS_SHAKEDOWN=1)
```

The hook uses the JSON envelope contract from PR #816 (read
`payload["tool_input"]["command"]`, never the top-level `command`).

**Consolidation history:** the two concerns previously lived in separate
hooks (`dev-env-check.py` + `shakedown-readonly.py`) with a shared allowlist
file (`.claude/state/safe-envs.json`). They were folded into a single
`shakedown-safety.py` hook reading a `safety` block in `.claude/settings.json`
during meta-retro bundle 4 (#18, #19, #20). One file, one place to review
policy; fewer ways to drift.

## Hook behavior

**Trigger:** every Bash invocation that mentions `ppds` or `ppds-mcp-server`.

### Concern 1: env allowlist gating (always on)

1. If the command does not mention `ppds`, the hook is a no-op (exit 0).
2. Resolve the allowlist:
   - `$PPDS_SAFE_ENVS` (comma-separated) wins if present.
   - Else `safety.shakedown_safe_envs` in `.claude/settings.json`.
   - Else legacy `.claude/state/safe-envs.json` (transitional fallback —
     will be removed once mid-migration trees have re-landed).
   - Else **block** with a configuration-help message.
3. Resolve the active env: read `profiles.json` directly from
   `$PPDS_CONFIG_DIR` (or `%LOCALAPPDATA%\PPDS` / `~/.ppds`). The hook does
   NOT shell out to `ppds env who` — that would be circular for a
   PreToolUse-on-Bash hook and would itself trip the hook.
4. Apply per-subcommand rules:
   - `ppds env list` / `current` / `who` / `config` / `type` / `show`:
     always allowed (read-only diagnostics; `who` issues only WhoAmI).
   - `ppds env switch <name>` / `select <name>` / `use <name>`: allowed
     when the **target** name is in the allowlist (so users can switch
     INTO a safe env even from an unsafe one).
   - All other `ppds *` invocations: allowed only when the **active** env
     is in the allowlist.
   - `ppds-mcp-server`: same rule as `ppds *` — the MCP server inherits
     the active env at startup.

**Match semantics:** the active env's `Environment.DisplayName`,
`Environment.UniqueName`, the host of `Environment.Url`, and the leftmost
host label are all compared case-insensitively against the allowlist.

**Fail-safe:** if the profile store is missing, malformed, or has no
active profile, the hook **blocks** (the spec is "in doubt, block").

### Concern 2: write-block during shakedown

Active only when the configured read-only env var is set to `1`. Default
name is `PPDS_SHAKEDOWN`; override via `safety.readonly_env_var` in
`.claude/settings.json`.

**Blocked verbs (any surface):** `create`, `update`, `delete`, `remove`,
`import`, `apply`, `register`, `unregister`, `publish`, `truncate`, `drop`,
`reset`, `set`.

**Special cases:**

- `ppds plugins deploy` is blocked unless `--dry-run` (or `-n`) is present.
- Anything under `ppds logs *` is read-only by definition.
- `ppds-mcp-server` during shakedown MUST be launched with `--read-only` —
  the server can otherwise issue arbitrary writes via its tools, which
  would defeat the write-block boundary.
- `ppds env switch/select` is read-only here because the env-allowlist
  concern above already validates the target.

**Bypass:** `unset PPDS_SHAKEDOWN` (or whatever name is configured). There
is intentionally no softer bypass marker — if you need to write during
shakedown, that is a deliberate action.

## Configuration

### Add an env to the allowlist

Edit `.claude/settings.json`:

```json
{
  "safety": {
    "shakedown_safe_envs": ["ppds-dev", "ppds-test"],
    "readonly_env_var": "PPDS_SHAKEDOWN"
  }
}
```

`readonly_env_var` is optional (default `PPDS_SHAKEDOWN`); include it only
when you need a project-specific override. `shakedown_safe_envs` is the
authoritative list — operators add their own dev/test envs locally.

### Override via env var (per-session)

```bash
export PPDS_SAFE_ENVS=ppds-dev,ppds-test
```

The env var takes precedence over `settings.json`. Useful for CI / sandbox
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

1. Add the env's friendly name to `safety.shakedown_safe_envs` in
   `.claude/settings.json`.
2. `ppds env select <new-env>` — the hook now allows it.
3. Re-run `/shakedown`.

### "The hook blocked a legitimate write outside shakedown"

The env-allowlist concern is **always on**. If you need to write to a
non-allowlisted env, add that env to the allowlist (per-session env var is
recommended for one-off writes, settings.json for permanent additions).

### "I need to run a real plugin deploy during a shakedown session"

Either:

- `unset PPDS_SHAKEDOWN` (preferred — explicit), or
- Run `ppds plugins deploy --dry-run` to validate without writing, then
  unset and re-run for the real deploy.

## Related

- `docs/MERGE-POLICY.md` — how PRs land on `main`.
- `.claude/skills/shakedown/SKILL.md` — Phase 0 references this doc.
- `tests/hooks/test_shakedown_safety.py` — behavioral tests covering
  allowlist sources, switch gating, dry-run carve-out, fail-safe paths.
