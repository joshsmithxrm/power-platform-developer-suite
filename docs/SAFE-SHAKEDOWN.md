# Safe Shakedown Enforcement

How PPDS enforces "do not accidentally write to a non-dev environment" during
shakedown and verify flows. A single PreToolUse hook plus a small allowlist
block in `.claude/settings.json` form a defense-in-depth boundary on top of
the skill-level rules.

## Why this exists

The shakedown skills (`/shakedown`, `/verify`) drive live Dataverse
environments. Without
explicit guardrails, an autonomous agent can:

1. Connect to the wrong environment (e.g. production instead of dev).
2. Run write/delete commands that mutate environment state.
3. Hit org-level resources accidentally.

The skills themselves contain rules ("do not run write commands during
shakedown"), but rules are advisory. The hook below is **mechanical**.

Environment gating is **opt-in**: it turns on once you configure an
allowlist of safe environments, and from then on it fails closed (if the
active env can't be identified, the hook blocks rather than allows). With
no allowlist configured the hook does not gate `ppds` commands, so a fresh
clone works without setup; an allowlist that is present but unusable blocks
them (see Concern 1). Anyone who runs an agent against live Dataverse
should configure the allowlist first; `/shakedown` Phase 0 refuses to start
without one.

## Architecture

```
Bash tool invocation -> PreToolUse hooks
                        |
                        +-- shakedown-safety.py
                            1. Env allowlist gate (ALL ppds *, once an
                               allowlist is configured)
                            2. Write-block (only during a shakedown)
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

**Trigger:** every Bash invocation that mentions `ppds` or `ppds-mcp-server`,
or runs either through `dotnet` (below).

### What the hook recognises

- The programs `ppds` and `ppds-mcp-server`, with an optional path and
  `.exe`, anywhere they appear as a word in the command.
- The same programs started through the .NET host, treated exactly like the
  direct program:
  - `dotnet run` (or `dotnet watch run`) whose `--project`, `--project=` or
    `-p` names the `PPDS.Cli` / `PPDS.Mcp` project folder or `.csproj`, or
    after a `cd` / `pushd` into that folder earlier in the same command.
    Arguments are what follows `--` plus anything `dotnet run` does not
    recognise; when an unknown option makes that ambiguous, both readings
    are checked and either can block.
  - `dotnet [exec] <path>/ppds.dll` and `<path>/ppds-mcp-server.dll`.
  - `dotnet ppds ...` and `dotnet tool run ppds ...`.

  Other `dotnet` commands (`build`, `test`, `pack`, `run` for any other
  project) are not gated.

Matching is deliberately broad and best-effort. It cannot see through a
script, an alias, another shell (`pwsh -c`, `cmd /c`) or a working
directory set in an earlier Bash call. A command that merely mentions
`ppds` (or one of these `dotnet` forms), for example in a commit message or
a `grep` pattern, can be blocked when a gate applies; that is existing
behaviour. The C# guard (`IShakedownGuard`, `specs/shakedown-guard.md`)
blocks writes during a shakedown however the CLI was started.

### Concern 1: env allowlist gating (opt-in)

1. If the command does not mention `ppds`, the hook is a no-op (exit 0).
2. Resolve the allowlist. The first source that names an environment wins:
   - `$PPDS_SAFE_ENVS` (comma-separated), when set and non-empty.
   - Else `safety.shakedown_safe_envs` in `.claude/settings.json`, when it
     is a non-empty list.
   - Else `safe_envs` in legacy `.claude/state/safe-envs.json`
     (transitional fallback — will be removed once mid-migration trees
     have re-landed), when it is a non-empty list. An empty settings list
     (`[]`, as checked in) falls through to this file.
   - The gate is **off**, and the steps below do not run, only when nothing
     is configured: `$PPDS_SAFE_ENVS` unset or empty,
     `safety.shakedown_safe_envs` absent or `[]`, and the legacy file absent
     or with `safe_envs` absent or `[]`.
   - A **misconfigured** allowlist blocks every `ppds` command (exit 2) with
     a message naming the problem: `$PPDS_SAFE_ENVS` set but naming no
     environment (e.g. `,`), `.claude/settings.json` that cannot be parsed
     as JSON, a `safety` or `shakedown_safe_envs` value of the wrong type
     (e.g. a string instead of a list), a legacy file that cannot be parsed
     or has a non-list `safe_envs`, or a non-empty list with no environment
     names in it. Fix the entry, or remove it to leave the gate off.
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

**Fail-safe:** with an allowlist configured, if the profile store is
missing, malformed, or has no active profile, the hook **blocks** (the spec
is "in doubt, block").

### Concern 2: write-block during shakedown

Active when the configured read-only env var is set to `1`, or when the
sentinel file `.claude/state/shakedown-active.json` is present and less
than 24 hours old (the form a Claude Code session uses, because the Bash
tool starts a fresh shell per call). Default env var name is
`PPDS_SHAKEDOWN`; override via `safety.readonly_env_var` in
`.claude/settings.json`. The write-block does not depend on the allowlist.

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

**Bypass:** remove whichever source armed the block — `unset PPDS_SHAKEDOWN`
(or whatever name is configured) when the env var armed it, or delete
`.claude/state/shakedown-active.json` when the sentinel file armed it (the
form a `/shakedown` session in Claude Code uses). The block message names
the active source. There is intentionally no softer bypass marker — if you
need to write during shakedown, that is a deliberate action.

## Configuration

### Add an env to the allowlist

**Your own envs: use the `PPDS_SAFE_ENVS` environment variable.**

```bash
export PPDS_SAFE_ENVS=ppds-dev,ppds-test
```

The variable takes precedence over `settings.json`. The hook reads it from
its own process environment, which it inherits from Claude Code, so set it
in the shell or profile you start Claude Code from (or in CI). An `export`
run inside the Claude Code Bash tool does not reach the hook, because that
tool starts a fresh shell for each command.

**A list the whole team shares: the tracked `.claude/settings.json`.**

```json
{
  "safety": {
    "shakedown_safe_envs": ["ppds-dev", "ppds-test"],
    "readonly_env_var": "PPDS_SHAKEDOWN"
  }
}
```

The file is checked in with `shakedown_safe_envs: []`, so a name added here
applies to everyone who pulls it; do not commit personal env names.
`readonly_env_var` is optional (default `PPDS_SHAKEDOWN`); include it only
when you need a project-specific override.

**`.claude/settings.local.json` is not read by the hook.** A `safety` block
there has no effect, even though that file is where personal Claude Code
permissions go.

### Activate read-only mode

```bash
export PPDS_SHAKEDOWN=1
```

Inside a Claude Code session, Phase 0 of the `/shakedown` skill arms the
same write-block by writing the sentinel file, and `/verify` inherits it
when invoked from a shakedown context.

## Common operator scenarios

### "I added a new dev env and want to use it for shakedown"

1. Add the env's friendly name to `PPDS_SAFE_ENVS` in the environment you
   start Claude Code from, then restart Claude Code. (Edit
   `safety.shakedown_safe_envs` in `.claude/settings.json` instead only if
   the whole team should share that env.)
2. `ppds env select <new-env>` — the hook now allows it.
3. Re-run `/shakedown`.

### "The hook blocked a legitimate write outside shakedown"

Once an allowlist is configured, the env-allowlist concern applies to every
`ppds` command, in and out of shakedown. If you need to write to a
non-allowlisted env, add that env to `PPDS_SAFE_ENVS`; change the tracked
`.claude/settings.json` only for an env the whole team should share.

### "I need to run a real plugin deploy during a shakedown session"

Either:

- Remove the source that armed the write-block (preferred — explicit):
  `unset PPDS_SHAKEDOWN` if the env var armed it, or delete
  `.claude/state/shakedown-active.json` if the sentinel file did (a
  `/shakedown` session in Claude Code), or
- Run `ppds plugins deploy --dry-run` to validate without writing, then
  remove that source and re-run for the real deploy.

## Related

- `docs/MERGE-POLICY.md` — how PRs land on `main`.
- `.claude/skills/shakedown/SKILL.md` — Phase 0 references this doc.
- `tests/hooks/test_shakedown_safety.py` — behavioral tests covering
  allowlist sources, switch gating, dry-run carve-out, fail-safe paths.
