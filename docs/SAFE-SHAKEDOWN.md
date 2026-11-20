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
Bash or PowerShell tool invocation -> PreToolUse hooks
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

## The principle: what passes

The hook grants a pass only on a shape it positively recognises, at the
statement level (which programs a command text starts) and at the argument
level (what one invocation does). Everything else is refused (exit 2).

- **Cannot tell means refuse, also inside an invocation.** When the hook
  cannot place a switch target, an environment, a profile, a statement or a
  flag, that is a refusal, never a pass. An invocation whose arguments the
  hook could not read in full (a cap, the time budget, a mention it could
  not place, arguments from elsewhere such as `xargs`), a response file
  (`@file`), or an argument only known when the command runs (`$X`,
  `$(...)`) is refused by both gates, even when the active env is listed.
- **A token that grants a pass must be positively placed.** `--dry-run`,
  `--read-only`, `--help`, `--list` and `--show` count only as the bare
  token in the place where the CLI's parser binds it as that flag: not as
  the value of the option before it, not with a value attached (`=x`, `:x`)
  and not followed by a word the flag might take as its value.
- **Both value delimiters.** Wherever the hook reads an option's value, it
  reads `--name value`, `--name=value` and `--name:value`, and the same for
  the aliases (`-env:x`).
- **Everything that can change which environment is reached is a target.**
  With an allowlist configured, an invocation passes only when every
  environment it names is listed, every profile it names resolves in the
  profile store to a listed environment, and it names nothing else that can
  move the target (below).
- **Bounded work.** Every loop over the input runs under the time budget,
  and the work per input is capped. Past a cap the command is refused.

What passes, gate by gate, is listed under Concern 1 and Concern 2 below;
anything not listed there is refused while that gate applies.

## Hook behavior

**Trigger:** every Bash tool or PowerShell tool invocation that mentions
`ppds`, `ppds-mcp-server`, `dotnet` or `dnx`, or runs a program the hook
cannot identify (below). The hook is registered for both shell tools and
reads every command twice, whatever the tool: with PowerShell's rules
(backtick escapes and line continuations, `''` and `""` inside quotes, the
call operator `&`, script blocks, also glued to the word before them,
sub-expressions, `$r=<command>` assignments, `--%`, every Unicode space as
a separator, backslash paths) and with POSIX rules. A hit from either
reading counts.

**Every mention is accounted for.** Each mention of `ppds`, `dotnet` or
`dnx` in the raw command text (any case, also with quoting, escape, brace
or glob characters between the letters) must be placed by each reading in
a word of a statement it read to the end: a word that names the program
(which the hook then checks), or a word naming something else that only
contains it (`src/PPDS.Cli`, `PPDS_CONFIG_DIR=x`,
`--environment=ppds-dev`). A mention that is not placed (for example
`@{a=ppds ...}`, `cmd /c p^pds ...`, `['ppds','data','delete']`) is an
invocation of both programs whose verb is unknown.

### What the hook recognises

- The programs `ppds` and `ppds-mcp-server`, with an optional path and
  `.exe`, anywhere they appear as a word in the command: in each statement
  (`;`, `&&`, `||`, `|`, a new line), inside substitutions (`$(...)`,
  backticks), PowerShell script blocks and sub-expressions, and inside
  every quoted string. The code handed to `bash -c`, `pwsh -Command` (all
  the words after it), `cmd /c`, `eval`, `Invoke-Expression` and
  `pwsh -EncodedCommand` (every spelling, `/enc` included) is also read as
  one joined text in that shell's rules. A program started by
  `Start-Process`, `xargs`, `find -exec` and the like gets its arguments
  from elsewhere, so its verb is unknown.
- The same programs started through the .NET host, treated exactly like the
  direct program:
  - `dotnet run`, `dotnet watch run` or `dotnet watch` whose `--project`,
    `--project=` or `-p` names the `PPDS.Cli` / `PPDS.Mcp` project folder
    or `.csproj`, also when the path is relative to a `cd`, `pushd` or
    `Set-Location` earlier in the same command (`--project .`) or contains
    a substitution (`$(git rev-parse --show-toplevel)/src/PPDS.Cli`).
    Without `--project`, the shell's working directory from the hook input
    decides, after a plain `cd` / `Set-Location <path>` earlier in the
    command; any other way of changing directory (`cd /d`, `-Path:`,
    `env -C`, `Start-Process -WorkingDirectory`, `popd`) makes the working
    directory unknown, and the run counts as both programs.
    Arguments are what follows `--` plus anything `dotnet run` does not
    recognise; when an unknown option makes that ambiguous, each reading is
    checked and any can block.
  - `dotnet [exec] <path>/ppds.dll` and `<path>/ppds-mcp-server.dll`.
  - `dotnet ppds ...`, `dotnet tool run ppds ...`, and the tool packages
    run without installing them: `dotnet tool exec PPDS.Cli ...`,
    `dotnet tool execute PPDS.Cli ...`, `dnx PPDS.Cli ...` (and
    `PPDS.Mcp`, with or without `@version`).
  - An option before the `dotnet` verb other than the host's own
    (`--info`, `-d`, `--roll-forward ...`) makes the command unknown.

  Other `dotnet` commands (`build`, `test`, `pack`, `watch test`, `run` for
  any other project) are not gated.

**Put `--` before application arguments with `dotnet run`.** Write
`dotnet run --project src/PPDS.Cli -- data list`, not
`dotnet run --project src/PPDS.Cli data list`. Without `--` the hook cannot
tell which tokens `dotnet run` consumes, so it checks several readings and
may block a command that is in fact read-only.

**When the hook cannot tell, it counts as an invocation** of both programs,
gated by the allowlist and, during a shakedown, refused unless its verb is
known to be read-only; when its arguments could not be read in full it is
refused by both gates, whatever the active env: a program only known when the command runs
(`$x data list`, `& $exe ...`, `timeout 5 $x ...`, a brace or glob in the
program name, `[scriptblock]::Create(...)`), code for a wrapper that is not
in the text (`pwsh -Command $c`, `$c | iex`, `echo $c | sh`,
`sh <<< "$c"`, a script file such as `bash x.sh` or `pwsh -File x.ps1`), a
`dotnet run` project or working directory only known when it runs, an
alias that names `ppds` or a run-time value, a mention the readings do not
place (above), text the hook could not read to the end (an unterminated
quote, an encoded command that does not decode), or a command past a cap
below. During a shakedown a command is also refused when anything before
its verb is an option (`ppds --verbose data delete`) or several words in
one argument.

**Caps.** Past any of these the command counts as an invocation of both
programs that the hook could not read, whether or not it mentions `ppds`,
and both gates refuse it:

| Cap | Value |
|-----|-------|
| Command length | 200,000 characters |
| Nesting (substitutions, blocks, quoted strings read again) | 8 levels |
| Reading work | 4,000,000 characters read, counting every nested reading |
| Time | 3.5 seconds per command; a watchdog refuses at 4.5 seconds (the hook is registered with 10) |
| Words naming `ppds`, `ppds-mcp-server`, `dotnet` or `dnx`, per reading | 500 |
| Arguments kept per invocation | 400 |
| Argument words produced by all readings together | 200,000 |
| Invocations found in one command | 2,000 |
| Mentions accounted for | 1,000 |
| Original matcher | 500 `ppds` words and 500 `dotnet` words, and 400 tokens or 4,096 characters after each |

Real commands are far below all of them. Claude Code runs a command whose
hook exceeds its registered timeout or exits 1, in both shell tools. So
every loop over the input runs under the 3.5-second budget, the work per
input is capped, and a watchdog ends the hook with exit 2 one second after
the budget if it has not answered. On Linux a 110,000-character command of
about 8,800 words naming `ppds` is answered in about 1.4 seconds (timing on
Windows is not measured). Any internal error refuses the command, whatever
it is (exit 2). With no allowlist and no shakedown, the hook exits 0 for
every command before reading it.

**Statements led by a variable.** The POSIX reading of a PowerShell
statement such as `$x = 1` or `$env:FOO = 'bar'` is a program only known at
run time. It therefore blocks during a shakedown and when the active env is
not on the allowlist, in both shell tools.

**Mentions are blocked too.** A command that merely mentions `ppds` (or one
of these `dotnet` forms), for example in a commit message, a pull request
body or a search pattern, is treated as an invocation and can be blocked
when a gate applies, in both shells. The maintainer decided on #1780 to
accept these false positives rather than parse them away. To avoid one,
pass long text through a file (`git commit -F`, `gh pr create --body-file`)
or run the search when no gate applies.

**False blocks to expect while a gate applies** (accepted on #1780; each is
a refusal where a pass would have been safe):

- With a listed active env as well as an unlisted one: a command whose
  arguments are only known when it runs (`ppds data export --output "$OUT"`);
  one that names a `PPDS_` variable other than `PPDS_SHAKEDOWN`,
  `PPDS_SAFE_ENVS`, `PPDS_FORCE_TUI`, `PPDS_INTERNAL`, `PPDS_MCP_LOG_LEVEL`
  or `PPDS_HOOK_BUDGET_SECONDS`, also in a commit message; a single-dash
  option holding an `e` or a `p` that is not one of the environment or
  profile options (`-cdp`, `-cp`, `-pkg`), because it may be a bundle of
  short options; an environment given as a URL whose host is not itself an
  allowlist entry; a mention the readings cannot place (`it's ppds's`);
  any command past a cap.
- `query sql` (with labels configured): SQL that contains the text of an
  unlisted environment's label anywhere, also inside a longer word.
- During a shakedown: SQL with a write word in a string, a comment or a
  bracketed name (`SELECT '[delete]'`), or a write word right after a
  number or a non-ASCII letter (`SELECT 1set`), because the hook reads the
  SQL text the way the T-SQL lexer splits words but does not skip strings
  and comments; `--help` anywhere but directly after the command path
  (`ppds data delete --entity x --help`); `plugins deploy --dry-run` after
  an option (`--config c.json` before it is fine, `--verbose --dry-run` is
  not) or with a value; the `--dry-run` preview of other write commands.
- `ppds query sql "<select>" -f json` and `<write command> --help` (help
  directly after the command path) now pass: on `query sql`, `-f` is
  `--output-format` (`--file` has no short form), and a help flag there
  only prints help.

The hook still cannot see into a script file (`scripts/ppds-dev.ps1`, and
the CLI or TUI that `tui-verify.mjs` starts from a script), an alias or
function defined outside the command, a working directory changed by an
earlier tool call that the hook input does not report, or what a program it
let start does afterwards: the TUI and `ppds serve` can change environment
from inside, and the MCP server switches environment only to an
`--allowed-env` value (the hook checks those values at start-up). The C# guard (`IShakedownGuard`,
`specs/shakedown-guard.md`) blocks writes during a shakedown however the CLI
was started.

### Concern 1: env allowlist gating (opt-in)

1. If the command neither mentions `ppds` nor runs a program the hook
   cannot identify (above), the hook is a no-op (exit 0), except for a
   `PPDS_` variable set in a way that outlives the command (step 5).
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
4. Refuse what the hook cannot place, whatever the active env: arguments
   it could not read in full, a response file (`@file`), an argument only
   known when the command runs.
5. Check every target the invocation names (any case, value after a space,
   `=` or `:`):
   - Environment options (`--environment`, `-env`, `-e`, `--env`,
     `--source-env` / `-se`, `--target-env` / `-te`): the value must be an
     allowlist entry, or a URL whose host is one. An option with no value
     is refused.
   - Profile options (`--profile`, `-p`, `--source-profile` / `-sp`,
     `--target-profile` / `-tp`): the profile (by name or index) must exist
     in the profile store and its environment must be listed. The profile's
     own name does not count here.
   - A single-dash token holding an `e` or a `p` that is none of these may
     be a bundle of short options (`-vp x`, `-eprod`) and is refused.
   - `ppds-mcp-server`: every `--environment`, `--allowed-env` and
     `--profile` value, checked the same way.
   - `query sql` / `query explain`: the SQL text must not contain the label
     (`env config --label`, kept in `environments.json` beside the profile
     store) of an environment that is not listed, because `[LABEL].entity`
     reaches that environment. With such a label configured, SQL from
     `--file` or `--stdin` and `query history execute` are refused; an
     `environments.json` the hook cannot read refuses all three.
   - A `PPDS_` variable named anywhere in a command that runs the CLI
     (`PPDS_CONFIG_DIR=...`, `$env:PPDS_PROFILE = ...`,
     `PPDS_ENVIRONMENT_URL`, ...), other than the ones that leave the
     target alone, is refused; so is setting one in a way that outlives
     the command (`$env:PPDS_X = ...`, `SetEnvironmentVariable`, `setx`),
     even with no CLI in the command. `PPDS_PROFILE` and
     `PPDS_ENVIRONMENT_URL` in the hook's own environment (inherited by
     the CLI too) are part of the active env: the profile it names, and
     the URL, must be listed.
6. Then, per command:
   - `ppds env select` (also spelled `switch` or `use`, which the CLI does
     not have): allowed when every target (the `--environment` / `-env`
     value and any plain word) is listed, whatever the active env, so you
     can switch INTO a safe env from an unsafe one. No target the hook can
     place is refused.
   - `ppds auth select`: allowed when every profile it names
     (`--name` / `-n`, `--index` / `-i`, a plain word) resolves to a listed
     environment, whatever the active env; no profile is refused.
   - With an unlisted (or unknown) active env, only these read forms pass:
     `ppds env` alone or with a help flag, `env list`, `env who` (issues
     only WhoAmI), `env config` alone, with `--list`, `--show` / `-s`, a
     URL, or a URL then `--show` / `-s`, and `env type` alone or with
     `--list`.
   - All other `ppds *` invocations, `ppds` with no arguments (it starts
     the TUI against the active env) included: allowed only when the
     **active** env is in the allowlist.
   - `ppds-mcp-server`: same rule — the MCP server inherits the active env
     at startup.

**Match semantics:** the active env's `Environment.DisplayName`,
`Environment.UniqueName`, the host of `Environment.Url`, the leftmost
host label, and the active profile's name are all compared
case-insensitively against the allowlist. A target typed in a command
is compared as written: equal to an allowlist entry, or a URL whose host
is one. The CLI itself may resolve a name that is not exactly one
environment's name by a partial match; list environments by a name that
matches exactly one of them.

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

**Only commands known to be read-only pass.** The hook holds a table of
every `ppds` command path (`CLI_COMMANDS` in the hook, aliases such as `org`
included) and passes a command only when its path is classified as
read-only there, for example `data export`, `metadata entities`,
`solutions list` or `logs tail`. Every other path is blocked: a command that
writes, one the hook is not sure only reads (`auth select`, `env config`
with settings, `query history execute`, `api request`, `serve`,
`interactive`), a group followed by a word that is not one of its commands,
and a command the hook has never heard of. A group alone (`ppds data`)
passes, because it only prints help; the root alone (`ppds` with no
arguments, also with `PPDS_FORCE_TUI=1`) starts the TUI and is refused. A
help flag (`--help`, `-h`, `-?`, `/?`, `/h`, exact case) placed directly
after the command path passes, because then nothing runs; anywhere else it
counts for nothing. A test derives the command tree from
`src/PPDS.Cli` and fails when the CLI has a command the table does not
classify, so a new command is blocked until someone classifies it. Because
the CLI's parser also accepts a child command after its parent's options or
arguments and expands response files, the hook refuses any `@file` argument
and any child command name placed anywhere after its parent (so reading a
table or option set whose name equals one of the subcommand names is
refused too).

**Special cases:**

- `ppds plugins deploy` passes only with `--dry-run` positively placed:
  the exact bare token, before any `--`, not directly after an
  option-looking token (`--correlation-id --dry-run` gives that option the
  value `--dry-run`), and not followed by a word the flag may take as its
  value (`--dry-run false`, `--dry-run " false"`). `--dry-run=<x>`,
  `--dry-run:<x>` and a second `--dry-run` are refused. There is no `-n`.
- `ppds query sql` passes only with the SQL inline and no word that writes
  (`INSERT`, `UPDATE`, `DELETE`, `MERGE`, `UPSERT`, `TRUNCATE`, `DROP`,
  `CREATE`, `ALTER`, `EXEC`, `EXECUTE`, `INTO`, `GRANT`, `REVOKE`, `DENY`,
  `BULK`, `OPENROWSET`, `DECLARE`, `SET`) in any argument, an option's
  value or SQL that starts with `-` (a `--` comment) included. Words are
  split the way the T-SQL lexer splits them: a number ends where its digits
  end, so `SELECT 1DELETE ...` and `0x12UPDATE` hold a write word, while
  `isdeleted` or `x1delete` do not. Strings, comments and bracketed names
  are read like code, so a write word there blocks too. `--file`,
  `--stdin` (in any form) and SQL only known when the command runs are
  blocked. A bare procedure call with none of these words (`query sql
  "PublishAllXml"`) is not recognised as a write; the CLI does not run one
  today.
- `ppds env config` passes only alone, with `--list`, `--show` / `-s`, a
  URL, or a URL then `--show` / `-s`; `ppds env type` only alone or with
  `--list`. Every other form writes local settings (labels, the
  environment type that sets the SQL DML protection level, colours).
- `ppds version` passes only alone or with `--check`; `--update` replaces
  the CLI under test.
- `ppds-mcp-server` during shakedown MUST be launched with `--read-only` —
  the server can otherwise issue arbitrary writes via its tools, which
  would defeat the write-block boundary. The flag counts as the server's
  own parser reads it: the exact token, not the value of `--profile`,
  `--environment` or `--allowed-env` (`--allowed-env --read-only` starts
  the server writable). Through `dotnet run`, a `--read-only` before `--`
  counts only when no option `dotnet run` does not recognise comes before
  it, and the tokens before and after `--` are checked in both orders;
  `dotnet run --project src/PPDS.Mcp -- --read-only` counts.
  Only the bare flag counts: `--read-only=<value>` (`=true`, `=false`, `=`)
  is refused with a message that the server only accepts the bare flag,
  because the server ignores the valued form and starts writable (#1718).
- `ppds env select` passes only when an allowlist is configured, because
  then Concern 1 checks its target; with no allowlist nothing checks it,
  and it is refused.

**Bypass:** remove whichever source armed the block — `unset PPDS_SHAKEDOWN`
(or whatever name is configured) when the env var armed it, or delete
`.claude/state/shakedown-active.json` when the sentinel file armed it (the
form a `/shakedown` session in Claude Code uses). The block message names
the active source. There is intentionally no softer bypass marker — if you
need to write during shakedown, that is a deliberate action.

## Enforcement in the PowerShell tool

`tests/test_settings_shell_rules.py` proves what the checked-in text says:
every deny and ask rule exists for the Bash tool and the PowerShell tool
with the same command, and the environment hook is registered for both
tool names as a `command` hook. The tests in `tests/hooks/` prove that the
hook process gives the same answer for `tool_name` Bash and PowerShell. No
automated test can observe whether Claude Code enforces a rule or runs the
hook for the PowerShell tool; that rests on an observation in a real
session:

<!-- OBSERVATION: the maintainer records here the session observation of
the PowerShell tool (a denied rule, an ask rule and a hook block), with the
Claude Code version and the date. Not recorded yet. -->

*Not recorded yet.*

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
2. `ppds env select --environment <new-env>` — the hook now allows it.
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
- `tests/hooks/test_shakedown_safety_shells.py` — each recognised form in
  Bash and PowerShell syntax, sent in the hook input shape each tool uses.
- `tests/hooks/test_shakedown_safety_forms.py` — forms that must count as
  an invocation, each in the syntax of its shell, and what each reading and
  the mention accounting contribute.
- `tests/hooks/test_shakedown_safety_process.py` — the hook as a process:
  input shapes, caps, the time budget against the registered timeout, and
  internal errors.
- `tests/hooks/test_shakedown_cli_commands.py` — the write-block's command
  table against the command tree derived from `src/PPDS.Cli`
  (`tests/hooks/cli_command_tree.py`).
- `tests/hooks/test_shakedown_decisions.py` — a copy of the table, every
  class, the SQL write words, where pass-granting flags count, the caps,
  and the environment gate at the argument level.
- `.claude/hooks/_shellparse.py` — the command reader the hook uses.
