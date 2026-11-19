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

**Trigger:** every Bash command that runs `ppds` or `ppds-mcp-server`, in
one of the forms below.

### What the hook recognises

The design keeps the broad matching the hook has always had, uses a
precise parse to add forms it missed, and clears matches only for a
command string that is data as a whole. When in doubt it blocks.

1. **Broad match (unchanged).** Any `ppds` / `ppds-mcp-server` word, with
   an optional path and `.exe`, at the start of the command or after
   whitespace, `|`, `;`, `&`, `(` or a backtick. This catches launchers the
   hook knows nothing about (`find -exec ppds ...`, `parallel`, `ssh`,
   `npx`, ...).
2. **Precise parse (adds forms).** The hook also parses the command like a
   shell (quotes including `$'...'`, escapes, `&&` / `||` / `;` / `|` /
   newlines, redirections, heredocs, `$(...)` and backticks) and adds:
   a quoted program word (`"C:\Program Files\PPDS\ppds.exe" ...`); the
   program after assignments, reserved words and wrappers (`env`, `sudo`,
   `time`, `timeout`, `xargs`, ...); command strings given to `bash -c` /
   `sh -c` (with other options before `-c`), `su -c`, `script -c`, `eval`,
   `pwsh` / `powershell -Command` and `cmd /c`; here-strings and heredocs
   fed to a shell; `echo` / `printf` output piped into a shell; arguments
   that `git` / `gh` / `rg` would run (`git -c core.sshCommand=...`,
   `git rebase --exec`, `git config alias.x '!...'`, `gh alias set`,
   `rg --pre`); and PPDS run through the .NET host (table below).
3. **Clearing (whole string only).** Every broad match in a command string
   is cleared when, and only when, step 2 found nothing and the whole
   string is data:
   - it parsed cleanly: no `for` / `while` / `if` / `case` or other
     keyword, no `{ }` group, `( )` subshell or function, no here-string,
     process substitution, arithmetic or `${...}`, no heredoc delimiter
     other than `WORD`, `'WORD'` or `"WORD"`, nothing unterminated;
   - it is simple commands joined only by `;`, newline, `&&`, `||`, `|`;
   - every command word, including inside `$(...)` and backticks, is a bare
     unquoted name from `git`, `gh`, `grep`, `rg`, `echo`, `printf`, `cat`,
     `ls`, `cd`, `head`, `tail`, `wc`, `mkdir` (no path, variable or
     assignment prefix);
   - no output redirection except to `/dev/null` or a file descriptor;
   - an unquoted heredoc body contains no `$(` or backtick;
   - `printf` has no `-v`; `rg` uses only allowlisted exact options; `git`
     starts with `commit`, `log`, `show`, `diff`, `status` or `add` (no
     option before it) and `gh` with an allowlisted pair such as
     `pr create` / `pr view` / `issue comment`, each with only allowlisted
     exact options (no abbreviation, attached value or bare `--`).

   Otherwise nothing is cleared and the result is exactly steps 1 + 2. The
   accepted trade: a mixed string such as
   `dotnet build && git commit -m "... ppds ..."` is gated as before.

So `grep -rn ppds docs/`, `git commit -m "fix: ppds query crash"`,
`gh pr create --title "..." --body "..."` and a commit message passed as
`git commit -m "$(cat <<'EOF' ... EOF)"` are not gated, while the same
commit command followed by `&& ppds data delete ...` is.

.NET host forms, each treated exactly like the direct program:

| Form | Treated as |
|------|------------|
| `ppds ...` / `ppds-mcp-server ...`, with an optional path and `.exe` | itself |
| `dotnet run --project <PPDS.Cli dir or .csproj> [run options] [--] <args>` | `ppds <args>` |
| `dotnet run --project <PPDS.Mcp dir or .csproj> [run options] [--] <args>` | `ppds-mcp-server <args>` |
| `dotnet watch [--project <dir>] run [--project <dir>] [--] <args>` | as `dotnet run` |
| `cd <PPDS.Cli or PPDS.Mcp dir> && dotnet run [--] <args>` (same command string) | `ppds <args>` / `ppds-mcp-server <args>` |
| `dotnet [exec] <path>/ppds.dll <args>` / `.../ppds-mcp-server.dll <args>` | `ppds <args>` / `ppds-mcp-server <args>` |
| `dotnet ppds <args>` / `dotnet tool run [options] ppds <args>` (local tool) | `ppds <args>` |
| `dotnet tool exec PPDS.Cli <args>` / `dnx PPDS.Cli <args>` | `ppds <args>` |

For `dotnet run`, the project path may use either slash style and be
absolute or relative; `--project=<path>` and the legacy `-p <path>` (a `-p`
value without `=` is a project, with `=` an MSBuild property) are accepted.
The known `dotnet run` options (`-c Release`, `--no-build`, `-f net10.0`,
...) are dropped. The hook then checks two readings and blocks if either
blocks: the CLI arguments are only what follows `--`; and they are every
token `dotnet run` does not recognise plus what follows `--`, which is what
`dotnet run` itself passes to the app. An option the hook does not know
(`--tl off`, `--ucr`, `-bl`, ...) therefore cannot hide a write. The cost
is the occasional extra block, for example
`dotnet run --project src/PPDS.Mcp --read-only` without `--` during a
shakedown. `dotnet run --project src/PPDS.Cli` with no arguments is a bare
`ppds`, which both gates allow. Any other `dotnet` command (`dotnet build`,
`dotnet test`, `dotnet run` for another project) is not a PPDS invocation.

**Limits.** Matching is best-effort. The hook does not see:

- a PPDS command inside a script file, an alias or a shell function
  defined in an earlier command, or `scripts/ppds-dev.ps1`;
- a program word built from a variable, glob or brace expansion
  (`$P data delete`, `pp{d,}s`, `src/PPDS.C*`);
- commands run by an interpreter (`python -c`, `node -e`);
- a working directory set anywhere other than an earlier `cd` / `pushd` in
  the same command string (the Bash tool's own working directory is not
  used), so `dotnet run -- ...` run from inside `src/PPDS.Cli` is not
  recognised;
- `cd` inside a subshell is not scoped, so `(cd src/PPDS.Cli); dotnet run`
  is treated as if the `cd` still applied (an extra block, never a miss).

It is a fast early reject, not the boundary: the C# guard
(`IShakedownGuard`, see `specs/shakedown-guard.md`) blocks writes during a
shakedown regardless of how the CLI was started.

### Concern 1: env allowlist gating (opt-in)

1. If the command does not run `ppds` (as above), the hook is a no-op (exit 0).
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
  allowlist sources, switch gating, dry-run carve-out, fail-safe paths,
  and the recognised command forms.
