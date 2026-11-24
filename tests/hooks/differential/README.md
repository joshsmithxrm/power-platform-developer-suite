# Differential tests for the environment hook

`.claude/hooks/shakedown-safety.py` reads the text of a shell command and refuses it when it would start the PPDS CLI or MCP server against an environment that is not on the safe list, or would write during a read-only shakedown.
To do that it imitates two real things: how `bash` and PowerShell turn a text into programs and arguments, and how the CLI's own parsers bind those arguments.
These tests check the hook against those real things, on generated inputs, instead of against hand-picked examples.

## What "the suite is green" proves, and what it does not

**Green means:** `python -m pytest tests/hooks/differential` ends with no failure and no skip (in CI the job sets `PPDS_DIFF_REQUIRE=1`, so a missing SDK, `bash` or `pwsh` is a failure, not a skip), with `PPDS_DIFF_XFAIL` unset. Then, at the seeds and sizes that ran:

- For every generated argument vector (every runnable command path of the real CLI tree, crossed with the shapes listed below) and every generated command text, in all eight modes and for both tool names, the hook refused everything the product's own parsers and real `bash` / `pwsh` say would run harmfully in that mode (requirement severity, including the `-approx` host and wrapper forms).
- Every write path of the CLI was judged as a write at least once in the shakedown.
- The shells provably recorded: every text has a recorded outcome under `bash` and under `pwsh`, known-good canary texts recorded their start at the beginning, middle and end of every batch, and timeouts, unreproduced PowerShell-only failures, parse-oracle errors and unverified or dropped renderings stayed within the limits under "Integrity" (most of them zero).
- Ordinary searches and process queries that only mention a program name were not refused with a listed environment and no shakedown (the cost rule).
- Long texts (to 110,000 characters by default) and raw text far above the caps (200,000 characters by default) were answered inside the documented budget plus start-up allowance, and inside the registered timeout.

For the exit rule of #1814 run the long size on fresh seeds as well; each must also be green:

```bash
python tests/hooks/differential/differential.py all --long --seeds 7,20261120,314159
```

(`PPDS_DIFF_SEED=<n> python -m pytest tests/hooks/differential` runs the default size at another seed.)

**Green does not prove:**

- Anything about inputs the generators do not make: see "What is not covered" (Windows shells, script files, aliases, `[LABEL].entity`, PATH changes and others). The generators are broad but finite; a generated corpus samples the input space.
- That a command path in `read_paths.json` really only reads: that list is frozen and was checked by review, not by these tests.
- What a handler does beyond its parse (for example whether `--dry-run` keeps an implicit `EXEC` from executing): ground truth stops at the parsers and the stand-ins.
- Enforcement by Claude Code itself (that a refusal stops the command, that the hook is called for both tools): only the hook's own decision is tested.
- The policy-severity results (`env list` and the read forms of `env config` / `env type` with an unlisted active environment, and environment-gate harms in a listed-environment text that names no target): they are reported, not failed, unless `PPDS_DIFF_STRICT=1`.
- Timing on another machine: the measured times depend on the machine's load.

## The two oracles

| | Oracle 1: argument level | Oracle 2: statement level |
|---|---|---|
| Input | Generated argument vectors for every command path of the real CLI tree and for the MCP server | Generated command texts in Bash style, PowerShell style or both |
| Ground truth | The CLI's real root command (`Program.CreateRootCommand`), `McpSessionOptions.Parse` and the product's `QueryParser`, through the parse-only helper in `ParseOracle/` | Real `bash` and real `pwsh` running the text with stand-in programs, then oracle 1 for what each stand-in received |
| Hook run | Each vector rendered as text for each tool name (`Bash`, `PowerShell`) | The same text for both tool names |

A third, smaller check, the **timing oracle** (`gen_timing.py`), feeds long texts (50,000 to 200,000 characters, valid in both shells by `bash -n` and the PowerShell parser) in several shapes: a write followed by many words naming the programs and many short words, quoted names, mixed program names, many statements, `dotnet run` words.
A fourth, the **raw timing check**, feeds raw text far above the hook's caps (one repeated character or short unit, 200,000 characters at CI size and up to 30 million with `--long`, with and without a mention in front); its property is an answer inside the registered timeout.
A fifth, the **ordinary-mention check** (`gen_ordinary.py`), is the other direction: searches and process queries that only mention a program name next to option-looking words (`grep ppds -rE docs`, `Select-String -Pattern 'ppds' -Path ...`, `Get-Process ppds -ErrorAction SilentlyContinue`, `Stop-Process -Name ppds -Force -WhatIf`). With a listed environment and no shakedown, a text under which no stand-in starts in either shell and that names no switch or target must pass; a refusal is a requirement failure of the cost rule (cause `cost-rule`).

The timing oracle's property: the hook refuses each text inside its documented budget (`_BUDGET_SECONDS`, read from the hook, plus 0.5 s for interpreter start-up) and never after the timeout registered for it in `.claude/settings.json` (read from the file).

Every hook run is a subprocess with the payload on stdin, started the way it is registered: through `shakedown-safety-launcher.py` next to the hook (the hook itself when no launcher sits next to it). Every run is tallied in the report's `hook_exits`: `abnormal` counts the runs where the launcher reports that the hook crashed, was ended by a signal, exited with any code but its pass code or 2 (a bare exit 0 included: an empty or cut-short hook file), hung, was missing or could not start, and runs where the entry itself ended with a code other than 0 or 2 or passed with output; `let_through` counts those of them whose final exit was not 2, so the command would have run. `let_through` above 0 is an integrity failure (`test_no_abnormal_hook_exit_lets_a_command_run`); `abnormal` is reported, with up to five stderr samples (the hook runs with `PYTHONFAULTHANDLER=1`).

The runs are in eight modes (`_harness.MODE_SPEC`). Each has a throwaway profile store under its own `HOME` and its own project folder, and, except where noted, a made-up allowlist (`zz-safe-a`, `zz-safe-b`) in `PPDS_SAFE_ENVS`:

| Mode | Active environment | Allowlist | Shakedown | Judged as |
|---|---|---|---|---|
| `gate-unlisted` | `zz-prod-x` (not listed) | `PPDS_SAFE_ENVS` | off | unlisted gate |
| `gate-unknown` | none (no profile store) | `PPDS_SAFE_ENVS` | off | unlisted gate |
| `gate-profile-named` | `zz-prod-y`, in a profile named `zz-safe-b` | `PPDS_SAFE_ENVS` | off | unlisted gate |
| `gate-listed` | `zz-safe-a` | `PPDS_SAFE_ENVS` | off | listed gate |
| `gate-settings` | `zz-safe-a` | `.claude/settings.json` `safety.shakedown_safe_envs` | off | listed gate |
| `shakedown` | `zz-safe-a` | `PPDS_SAFE_ENVS` | `PPDS_SHAKEDOWN=1` | shakedown |
| `shakedown-sentinel` | `zz-safe-a` | `PPDS_SAFE_ENVS` | fresh sentinel file | shakedown |
| `shakedown-noal` | `zz-safe-a` | none | `PPDS_SHAKEDOWN=1` | write-block only |

### The property

If the hook passes a command (any exit code but 2) in a mode, ground truth must say it is harmless in that mode:

- `gate-unlisted`: it does not run at all (parse error, help, a blank switch target, which `EnvironmentResolver` resolves to nothing), or it switches into a listed environment.
- `shakedown-noal`: it cannot write (as below), and it does not switch the active environment at all (`env select`, `auth select`): with no allowlist nothing checks the target.
- `gate-listed`: it does not switch the active environment to an unlisted or unknown target (`env select`, or `auth select` to a profile whose environment is not listed), and it does not point the invocation at another environment: a per-command `--environment` naming an unlisted environment, a `--profile` or `PPDS_PROFILE` that does not resolve to a listed environment in the made-up store (resolved like `ProfileCollection.GetByNameOrIndex`), a changed `PPDS_CONFIG_DIR`, or the MCP server's `--environment`, `--allowed-env` or `--profile` doing the same.
- `shakedown`: both of the above, and it cannot write: its command path is read-only (`read_paths.json`), or it is `plugins deploy` with `--dry-run` bound to true, or `query sql` whose text the product's SQL parser reads as reads only, or the MCP server with `McpSessionOptions.ReadOnly` true.

For oracle 2 the property is about both tool names: if a stand-in started under either shell with harmful arguments, the hook must refuse the text for both.

A refusal where ground truth says harmless is a false block.
False blocks are counted and reported, never failed on: the maintainer accepted them on #1780.

### Severities

- `requirement`: the hook must refuse it (R4, and the maintainer's decisions in the third review round). A pass fails the test.
- `policy`: counted and reported; a pass fails only with `PPDS_DIFF_STRICT=1`. Two kinds:
  - the pure read diagnostics on an unlisted active environment: `env list` (it asks Global Discovery, not the active environment) and `env config` / `env type` in their `--list` / `--show` forms (their writing forms are `requirement`). `env who` is **not** excused: it sends WhoAmI to the active environment (`EnvCommandGroup.ExecuteWhoAsync`), so it is a requirement harm there;
  - an environment-gate harm (a switch or a target override) in a text that shows no switch words (after joining line continuations, turning `\n`, `\t` and `\xNN` escapes into the characters they stand for, removing quoting characters, expanding braces and decoding `-EncodedCommand`) (`env`, `org` or `auth` with `select`, `switch` or `use`) and no target option (`--environment`, `-env`, `-e`, `--profile`, `-p`, `--allowed-env`, `PPDS_CONFIG_DIR`, `PPDS_PROFILE`), after quoting characters are removed and `-EncodedCommand` payloads are decoded. Example: `ppds @cmd.rsp`, where the switch is inside the response file. For such texts a pass is acceptable in the listed-environment gate only, never during a shakedown; the cause is suffixed `-not-in-text`.
- `-approx`: a .NET host form (`dotnet run`, `dnx`, `dotnet tool exec`) whose mapping from the `dotnet` stand-in's arguments to the CLI's arguments in `_shells.map_start` is an approximation of the .NET SDK's own forwarding. These are reported in their own group.

## How to run

The tests need Python 3 with pytest, and for oracle 1 the .NET 10 SDK (the helper builds `src/PPDS.Cli` and `src/PPDS.Mcp` on first use).
Oracle 2 and the timing oracle also need `bash` and `pwsh` (PowerShell 7).

```bash
python -m pytest tests/hooks/differential -q
```

```bash
python tests/hooks/differential/differential.py args --cases out/args --json out/args.json
```

```bash
python tests/hooks/differential/differential.py shell --cases out/shell --json out/shell.json
```

```bash
python tests/hooks/differential/differential.py timing --cases out/timing --json out/timing.json
```

```bash
python tests/hooks/differential/differential.py args --long
```

```bash
python tests/hooks/differential/repro.py out/args/<name>.case.json
```

| Setting | Effect |
|---|---|
| `--size N`, `--seed S` | Corpus size and seed. Generation is deterministic for a seed and size. Defaults: oracle 1 seed 1814 and 4,500 vectors; oracle 2 seed 1780, 700 random texts plus a seeded sample of 500 of the 1,840 shell-feed texts; timing 50,000 and 110,000 characters in the shakedown mode. `--long` uses 40,000 vectors, 8,000 random texts plus every shell-feed text, and 20,000 to 200,000 characters in two modes. |
| `--cases DIR` | Writes each failing case as `<name>.payload.json` (the hook's stdin) and `<name>.case.json` (tool, mode, command, ground truth). At most a few per group. |
| `PPDS_DIFF_SIZE_ARGS`, `PPDS_DIFF_SIZE_SHELL`, `PPDS_DIFF_TIMING_LENGTHS`, `PPDS_DIFF_SIZE_ORDINARY`, `PPDS_DIFF_RAW_LENGTHS` | Corpus sizes under pytest (defaults 1,500 vectors, 700 texts, `50000`, 120 ordinary texts, `200000`; lengths are comma-separated). |
| `PPDS_DIFF_STRICT=1` | Policy-severity passes also fail. |
| `PPDS_DIFF_REQUIRE=1` | A missing tool fails the test instead of skipping it. |
| `PPDS_DIFF_XFAIL=1` | A test whose failures all carry a label in `expected_failures.json` is reported as xfail. |
| `PPDS_DIFF_HOOK` | Path of the hook under test (default: the one in this checkout). |

## Expected-failure state

At commit 3164e825 (the second review round of #1814) the requirement-severity tests fail: they rediscover the round-2 must-fix findings and find more.
`findings.py` labels each failure after the fact (the generators never use these labels), and `expected_failures.json` lists the labels known at that commit with the finding each belongs to.
When a fix lands, the failures with its label disappear; remove the label from `expected_failures.json`.
Mode tags (`r5-mode-*`) are not listed and excuse nothing: a failure is excused only when it carries a class label and every class label it carries is listed. After the maintainer's ruling on `env who` (2026-11-24: refuse it, as the hook now does) the list is empty.
Round-4 labels (`new-xargs-options`, `new-hidden-words`, `new-unknown-wrapper`, `new-home-override`, `new-sql-exponent-glue`, `cost-rule-false-block`) were added against the #1814 head a39e7e68; some earlier labels are listed again because they reappear combined with a round-4 one.
A failure labelled `unlabelled` is a finding nobody has named yet.

## Safety of the runs

- The helper in `ParseOracle/` only calls `Parse`. No command action is invoked, so it cannot sign in or reach an environment. It runs with `HOME` and `PPDS_CONFIG_DIR` pointing at empty temporary folders.
- Shell runs use stand-ins named `ppds`, `ppds-mcp-server`, `dotnet` and `dnx` (and `.exe` and upper-case spellings) that only record their arguments to a file and exit.
- Each shell run has a temporary working directory, `HOME` and `PPDS_CONFIG_DIR` inside it, `PATH` set to the stand-in folder plus `/usr/bin:/bin`, and a timeout. Before the first run the lab asserts that every guarded name resolves to its stand-in and that no real `ppds` or `ppds-mcp-server` exists on the system folders.
- `Stop-Process` in the ordinary-mention texts always carries `-WhatIf`.
- The text vocabulary has no deletion, no network access and no redirection outside the temporary folder.
- Oracle 2's generator has a systematic block for the class "code handed to a shell": every wrapper and assignment prefix (`env`, `env FOO=1`, `FOO=1`, `timeout 5`, `nohup`, `command`, `exec`, a changed `PPDS_CONFIG_DIR`) in front of every way of feeding code to `bash`, `sh` and `pwsh` (here-string, quoted and unquoted here-document, `-c` / `-Command`, stdin pipe), with the program name literal, quoted, escaped, from a variable and from a substitution.
- Stand-ins also record `PPDS_CONFIG_DIR`, `PPDS_PROFILE` and `HOME` as they saw them, so environment-variable overrides in the text are judged. Neither the hook nor the shells get `PPDS_CONFIG_DIR`: the hook finds each mode's store under `$HOME/.ppds`, as the product does on Linux (`ProfilePaths.cs`), so a `HOME=<dir>` in front of the CLI is a change of store (cause `home-override`).
- Round-4 blocks of oracle 2 (recipes `xargs:`, `hidden:`, `wrapper:`, `home:`): `xargs` with its own options (`-n`, `-L`, `-P`, `-I{}`, `-r`, `-t`, `-s`, `-x`) and GNU `parallel` (`-j`, `-n`, `:::`) with the words of a switch, a target or a write arriving on stdin; switch, target and write words that are in the text but not as whole words (inside `printf` / `echo -e` escapes, in an assignment, split by a line continuation, in brace expansion, splatted); wrappers in front of a shell given code (`setsid`, `flock <file>`, `chroot /`, `busybox sh`, `xvfb-run -a`, `winpty`, `stdbuf`, `nice`, `ionice`, `script -c`, `ssh host`); `HOME`, `USERPROFILE` and `XDG_CONFIG_HOME` changes.
- `ssh`, `parallel`, `busybox`, `winpty` and `xvfb-run` are approximated by stand-ins (`_shells.APPROX_WRAPPERS`) that apply the wrapper's own option rules and run the rest locally: `ssh` must not reach a network and `xvfb-run` would start an X server; the others are not installed here. Their texts are reported with `-approx` severity. `chroot /` needs root here, so nothing starts under it.
- Each shell run has its own process group; a run past its timeout is killed with its children. Texts that take a file lock (`flock`) run one at a time.
- The hook runs with a made-up allowlist and profile store and a scrubbed environment (no `PPDS_*`, `CLAUDE_*` or credential variables).

## Integrity: when a run does not count

Each oracle run carries an `integrity` record, and a run whose record is not `ok` fails: the `differential.py` exit code and the `*_harness_integrity` tests. The limits are in `differential.py`:

| Problem | Limit |
|---|---|
| A text without a recorded outcome (finished or timed out) under bash or under pwsh | 0 |
| Texts that hit their per-text timeout twice: a first run of 60 s, then one retry of 180 s, 4 at a time | 1% of the texts, per shell |
| PowerShell-only failures a fresh `pwsh -Command` did not reproduce | 2 |
| Parse-oracle errors | 0 |
| Oracle-1 failures whose shell rendering did not hold, or a skipped rendering check | 0 |
| A write path with no vector judged a write in the shakedown | 0 |
| An abnormal end of the hook whose final exit is not 2 (`hook_exits.let_through`) | 0 |
| A text that timed out under a shell (after the retry) and that the hook passed in any mode (`timed_out_texts_passed`): its recorded starts are only what ran before it was killed, so the pass cannot be judged | 0 |

`requirement-approx` failures fail the `differential.py` exit code as well as pytest.

Timeouts are harness faults, not hook decisions, and on a busy machine most of them are slow starts: a cold `pwsh` start in a fresh `HOME` takes 1 to 6 s at load average 17 and 19 to 29 s at load 40 to 160, and a text that sends work to the background (`&`) under PowerShell starts a second `pwsh` for the job. So the lab runs at most 16 texts at a time (`ShellLab.workers`, the CPU count when lower), retries every run that timed out once with a longer limit and only 4 at a time, and counts as a timeout only a text that timed out on the retry as well. The record carries both counts (`*_retried`, `*_timeouts`). The limit still catches a PowerShell that records nothing: one that exits at once fails the self-test and the canaries, and one that hangs times out on every retry (`test_shell_lab_integrity_fails_when_pwsh_does_not_record`).
The parse helper is rebuilt when any file under `src/` (or the helper, or `global.json` and the shared props) is newer than its DLL, and always with `--long` or `PPDS_DIFF_REBUILD=1`.

## Switches that make the suite pass without checking

None of these is set in CI. Each one, set locally, can turn a check off:

| Switch | Effect |
|---|---|
| `PPDS_DIFF_HOOK` | The suite tests that file instead of `.claude/hooks/shakedown-safety.py`. |
| `PPDS_DIFF_SIZE_ARGS=0`, `PPDS_DIFF_SIZE_SHELL=0`, `PPDS_DIFF_SIZE_ORDINARY=0` | Keep only the systematic shapes, or no random texts; `PPDS_DIFF_SIZE_ORDINARY=0` runs no ordinary text at all. |
| `PPDS_DIFF_TIMING_LENGTHS=1`, `PPDS_DIFF_RAW_LENGTHS=1` | Timing texts of one character: the timing checks become trivial. |
| No .NET 10 SDK, or no `pwsh`, without `PPDS_DIFF_REQUIRE=1` | The oracle that needs it skips. The end of the pytest run lists every skipped differential test with its reason in red (`conftest.py`). |
| `PPDS_DIFF_XFAIL=1` | Failures whose labels are all in `expected_failures.json` are reported as xfail. |

## Mutation check of this suite

`mutate.py` (not run by pytest) re-introduces each defect in `mutants.py` into a scratch copy of `.claude/hooks` and reports which oracle catches it at the CI size; `--unit` also names the hook unit test that catches a defect no oracle catches. The list is the 39 defects of the independent review of these tests. Ground truth is computed once; the harmful jobs are sampled (at most 25 per mode, cause and shape) and only jobs the unmutated hook refuses are kept.

```bash
python tests/hooks/differential/mutate.py --unit --json out/mutants.json
```

The results at the head this README was last updated against are in the pull request description and in the table below.

<!-- mutation-table -->
| Defect | Oracle 1 | Oracle 2 | Timing | Caught |
|---|---|---|---|---|
| `m01_colon_delim` | 80 | 0 |  | yes |
| `m02a_bare_start_shakedown` | 42 | 120 |  | yes |
| `m02b_bare_start_envgate` | 42 | 74 |  | yes |
| `m03_sql_leading_dash_dropped` | 6 | 0 |  | yes |
| `m04_sql_word_boundary` | 2 | 0 |  | yes |
| `m05_dry_run_anywhere` | 6 | 0 |  | yes |
| `m06_read_only_anywhere` | 1 | 0 |  | yes |
| `m07_ignore_environment_option` | 271 | 154 |  | yes |
| `m08_profile_name_as_env` | 358 | 470 |  | yes |
| `m09_no_shell_feed_after_prefix` | 0 | 28 |  | yes |
| `m10_unplaced_switch_passes_when_listed` | 0 | 146 |  | yes |
| `m11_auth_select_passes` | 126 | 80 |  | yes |
| `m12_response_file_passes_both_gates` | 43 | 0 |  | yes |
| `m12a_response_file_passes_envgate` | 2 | 0 |  | yes |
| `m12b_response_file_passes_writeblock` | 4 | 0 |  | yes |
| `m13_cap_removed_runs_past_budget` | 0 | 0 | 0 | no: no effect at this head: other caps and the deadline bound the run (mutant and unmutated hook both refuse 2 M characters of `dotnet` words in 0.3 to 0.6 s); no test catches it |
| `m13b_caps_raised_deadline_kept` | 0 | 0 | 0 | no: refuses inside the timeout, just over the budget; caught by the unit test `test_shakedown_decisions.py::test_past_the_program_word_cap_is_unreadable` |
| `m14_ignore_source_target_env` | 68 | 0 |  | yes |
| `m15_ignore_target_variables` | 0 | 110 |  | yes |
| `m15b_environment_url_var_safe` | 0 | 16 |  | yes |
| `m16_ignore_profile_option` | 255 | 83 |  | yes |
| `m17_data_delete_classified_read` | 6 | 92 |  | yes |
| `m18_help_flag_anywhere` | 32 | 0 |  | yes |
| `m19_version_update_passes` | 12 | 14 |  | yes |
| `m20_env_config_write_passes` | 51 | 0 |  | yes |
| `m21_encoded_command_not_decoded` | 0 | 108 |  | yes |
| `m22_mention_accounting_off` | 0 | 38 |  | yes |
| `m23_unknown_active_passes` | 315 | 468 |  | yes |
| `m24_env_select_passes_without_allowlist` | 51 | 91 |  | yes |
| `m25_sentinel_ignored` | 326 | 436 |  | yes |
| `m26_dotnet_projects_unknown` | 1100 | 212 |  | yes |
| `m27_runtime_program_not_guessed` | 0 | 149 |  | yes |
| `m28_switch_to_unlisted_passes` | 0 | 0 |  | no: masked: the per-command `--environment` check still refuses every real `env select` target; only a positional target (which the real CLI rejects) tells them apart; caught by the unit test `test_shakedown_ordinary_work.py::test_unplaced_invocation_naming_a_target_is_refused_with_a_listed_env[timeout-quoted-c]` |
| `m29_mcp_targets_ignored` | 236 | 116 |  | yes |
| `m30_watchdog_and_deadline_off` (since the launcher: its limit raised to 600 s) | 0 | 0 | 4 | yes |
| `m31_only_first_invocation_checked` | 14 | 428 |  | yes |
| `m33_only_the_tools_own_dialect_read` | 0 | 97 |  | yes |
| `m34_five_write_paths_classified_read` | 17 | 0 |  | yes |
| `m35_three_write_paths_classified_read` | 9 | 0 |  | yes |

Caught: 36 of 39 at CI size (the review counted 22 of 39 at a39e7e68). Counts are failing hook runs among the sampled harmful jobs. m12, m12a and m12b were caught after the `response-file-after-read` shapes were added (targeted re-run, oracle 1 only).


## Where it runs in CI

The Python tests job (`.github/workflows/workflow-tests.yml`, `ubuntu-latest`) runs `python -m pytest tests/`, so it collects these tests.

- Oracle 2 needs `bash` and `pwsh`; the hosted Ubuntu image has both.
- **Always run there:** oracle 2 in the unlisted-environment modes (`gate-unlisted`, `gate-unknown`, `gate-profile-named`), the timing oracle, the raw timing check and the ordinary-mention check. They need only Python, `bash` and `pwsh`, which the hosted Ubuntu image has.
- **Run only if the image's preinstalled .NET SDK satisfies `global.json` (10.0.115, `latestPatch`):** oracle 1 in all eight modes, the write-path coverage test, and oracle 2 in the five other modes. That job does not run `actions/setup-dotnet`, so when the image's SDK does not match, these skip and the job stays green; the end of the log lists each skip in red.
- **To make oracle 1 mandatory** (not done here; workflow files are not changed by this pull request), add to the `workflow-tests` job, before the pytest step:

  ```yaml
  - uses: actions/setup-dotnet@v6
    with:
      global-json-file: global.json
  ```

  and run the step with `env: PPDS_DIFF_REQUIRE: "1"`, so a missing tool fails instead of skipping.
- Shell ground truth starts one fresh `bash` and one fresh `pwsh` per text. On a heavily loaded machine a `pwsh` start can take tens of seconds; the CI sample of oracle 2 (700 random, 500 shell-feed and 400 round-4 texts) is sized for an unloaded runner. `differential.py shell --round4` runs only the round-4 blocks, all of them.
- The timing oracle runs two hook runs at a time so the measured time is the hook's; on a loaded runner the budget check can fail for load alone. Each run is stopped 5 s after the registered timeout.

## What is not covered

- Windows, not verified here. The registered command (`python "$CLAUDE_PROJECT_DIR/.claude/hooks/shakedown-safety-launcher.py" || exit 2`) assumes a POSIX hook shell. In a hook shell where `||` does not parse (Windows PowerShell 5.1), the line is a parse error that exits 1, and Claude Code runs every command: the gate is silently off. Under `cmd` or PowerShell 7 `$CLAUDE_PROJECT_DIR` does not expand and every command is refused. A Windows check must show, in the shell Claude Code actually uses for hooks, in a Bash-tool session and in a PowerShell-tool session (also on a machine without Git Bash): a refusable payload gives 2 and a harmless one 0; no `python`, or the Microsoft Store `python` stub, gives 2; a hook stand-in that ends with an access violation gives 2; a hook that hangs gives 2 within about 6 s with the child process gone; a 1 MB payload reaches the hook whole.
- Windows shells: `cmd`, Windows PowerShell 5.1 and Git Bash are not run (`cmd /c` is approximated by a stand-in). Windows' case-insensitive program lookup is modelled only for the spellings in `_shells.EXTRA_SPELLINGS`. `LOCALAPPDATA` / `USERPROFILE` as the store's location on Windows are not modelled.
- What a command does beyond its parse: write-ness of a path comes from `read_paths.json`, a frozen copy of the read-only paths of the hook's table at 3164e825, which review B checked handler by handler, plus the conditional rules above. A handler that writes despite being listed there is not caught. **When the hook's command table changes, `read_paths.json` must be re-frozen by review** (a reviewer checks each added or reclassified read-only path against its handler); it is deliberately not derived from the hook, so a wrong classification in the hook cannot make the oracle agree with it.
- The .NET SDK's own argument forwarding for `dotnet run`, `dnx` and `dotnet tool exec` is approximated (see `-approx`).
- SQL that reaches another environment through a `[LABEL].entity` name is not modelled. Implicit `EXEC` (a bare message name, or a word a flag leaves as the SQL) is generated and judged a write; whether `--dry-run`, `--explain` or `--show-fetchxml` keep such a text from executing is not modelled (it counts as a write).
- Profiles resolve only against each mode's two-profile made-up store.
- Script files (`./x.sh`, `pwsh -File`), aliases defined in the text, programs found through a changed `PATH`, and Windows-only launchers are not generated.
- `sudo`, `cmd`, `ssh`, `parallel`, `busybox`, `winpty` and `xvfb-run` are approximated (`-approx`); `chroot /` needs root here, so nothing starts under it.

## Registered through the launcher, or directly

The checked-in registration runs the hook through `.claude/hooks/shakedown-safety-launcher.py`, and these tests run it the same way. Through the launcher, a pass is the hook's private pass code (the launcher hands it over as `--pass-exit-code=N` and maps it to 0); exit 2 stays a refusal; everything else is refused: a signal or crash, exit 1 or any other code, a bare exit 0 (an empty hook file, or one cut short, which decided nothing), a missing hook file, and no answer within 5 s (the hook is killed). The shell's `|| exit 2` refuses when the launcher cannot run.

A direct registration (`python ".../shakedown-safety.py"`, as in an older settings file) gets the same decisions, a pass as exit 0 and a refusal as exit 2, and the refusal of any internal error. It does not get: a refusal when the hook crashes, is killed or exits 1 (Claude Code runs the command), a time limit beyond the budget checks between steps (a hang runs into Claude Code's timeout, and the command runs), or a refusal of an empty or cut-short hook file.

## Known limits of the hook (documented, not failed by a fix)

By class; these are limits of a text-reading guard, written here so that nobody takes a green suite as their absence:

1. A program name supplied at run time from outside the text (a variable exported earlier), behind a wrapper the hook does not know: passes in every mode.
2. A program name built at run time inside code handed to a runner the reader does not know, or after a PowerShell assignment: passes with a listed active environment. The long statement-oracle runs find this class (requirement failures in `gate-listed` and `gate-settings`).
3. A target hidden in a script file, in a response file given to a guessed program, or in a variable set outside the text: passes with a listed active environment.
4. Above 200,000 characters only byte checks apply; run-time text passes with a listed active environment.
5. Launcher: Claude Code's own 10 s timeout, a hook shell that cannot start, a module planted in the hooks folder or on `PYTHONPATH`, and a fake interpreter on `PATH` let the command run.
6. A hook input with no parseable command and no mention of a guarded program is passed.
7. Oracle: up to two PowerShell-only failures that one fresh re-run does not reproduce are tolerated (`MAX_UNCONFIRMED`); the suite runs the launcher as `python launcher`, not through the registered shell line (the unit tests in `tests/hooks/test_shakedown_safety_launcher.py` do that).

