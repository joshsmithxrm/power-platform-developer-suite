# Differential tests for the environment hook

`.claude/hooks/shakedown-safety.py` reads the text of a shell command and refuses it when it would start the PPDS CLI or MCP server against an environment that is not on the safe list, or would write during a read-only shakedown.
To do that it imitates two real things: how `bash` and PowerShell turn a text into programs and arguments, and how the CLI's own parsers bind those arguments.
These tests check the hook against those real things, on generated inputs, instead of against hand-picked examples.

## The two oracles

| | Oracle 1: argument level | Oracle 2: statement level |
|---|---|---|
| Input | Generated argument vectors for every command path of the real CLI tree and for the MCP server | Generated command texts in Bash style, PowerShell style or both |
| Ground truth | The CLI's real root command (`Program.CreateRootCommand`), `McpSessionOptions.Parse` and the product's `QueryParser`, through the parse-only helper in `ParseOracle/` | Real `bash` and real `pwsh` running the text with stand-in programs, then oracle 1 for what each stand-in received |
| Hook run | Each vector rendered as text for each tool name (`Bash`, `PowerShell`) | The same text for both tool names |

Every hook run is a subprocess with the payload on stdin, in three modes, each with a made-up allowlist (`zz-safe-a`, `zz-safe-b`) and a throwaway profile store:

| Mode | Active environment | Shakedown |
|---|---|---|
| `gate-unlisted` | not on the list (`zz-prod-x`) | off |
| `gate-listed` | on the list (`zz-safe-a`) | off |
| `shakedown` | on the list | on (`PPDS_SHAKEDOWN=1`) |

### The property

If the hook passes a command (any exit code but 2) in a mode, ground truth must say it is harmless in that mode:

- `gate-unlisted`: it does not run at all (parse error, help), or it switches into a listed environment.
- `gate-listed`: it does not switch the active environment to an unlisted or unknown target.
- `shakedown`: both of the above, and it cannot write: its command path is read-only (`read_paths.json`), or it is `plugins deploy` with `--dry-run` bound to true, or `query sql` whose text the product's SQL parser reads as reads only, or the MCP server with `McpSessionOptions.ReadOnly` true.

For oracle 2 the property is about both tool names: if a stand-in started under either shell with harmful arguments, the hook must refuse the text for both.

A refusal where ground truth says harmless is a false block.
False blocks are counted and reported, never failed on: the maintainer accepted them on #1780.

### Severities

- `requirement`: R4 of the review requirement says the hook must refuse it. A pass fails the test.
- `policy`: harmful by the property but outside R4's letter, or documented as allowed: per-command `--environment` and `--profile` overrides with a listed active environment, `auth select`, the MCP server's `--environment`, `--profile` and `--allowed-env`, and the `env` read-only diagnostics on an unlisted active environment. They are counted and reported; a pass fails only with `PPDS_DIFF_STRICT=1`.
- `-approx`: a .NET host form (`dotnet run`, `dnx`, `dotnet tool exec`) whose mapping from the `dotnet` stand-in's arguments to the CLI's arguments in `_shells.map_start` is an approximation of the .NET SDK's own forwarding. These are reported in their own group.

## How to run

The tests need Python 3 with pytest, and for oracle 1 the .NET 10 SDK (the helper builds `src/PPDS.Cli` and `src/PPDS.Mcp` on first use).
Oracle 2 also needs `bash` and `pwsh` (PowerShell 7).

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
python tests/hooks/differential/differential.py args --long
```

```bash
python tests/hooks/differential/repro.py out/args/<name>.case.json
```

| Setting | Effect |
|---|---|
| `--size N`, `--seed S` | Corpus size and seed. Generation is deterministic for a seed and size. Defaults: oracle 1 seed 1814 and 4,500 vectors, oracle 2 seed 1780 and 700 texts; `--long` uses 40,000 and 8,000. |
| `--cases DIR` | Writes each failing case as `<name>.payload.json` (the hook's stdin) and `<name>.case.json` (tool, mode, command, ground truth). At most a few per group. |
| `PPDS_DIFF_SIZE_ARGS`, `PPDS_DIFF_SIZE_SHELL` | Corpus sizes under pytest (defaults 1,500 and 700). |
| `PPDS_DIFF_STRICT=1` | Policy-severity passes also fail. |
| `PPDS_DIFF_REQUIRE=1` | A missing tool fails the test instead of skipping it. |
| `PPDS_DIFF_XFAIL=1` | A test whose failures all carry a label in `expected_failures.json` is reported as xfail. |
| `PPDS_DIFF_HOOK` | Path of the hook under test (default: the one in this checkout). |

## Expected-failure state

At commit 3164e825 (the second review round of #1814) the requirement-severity tests fail: they rediscover the round-2 must-fix findings and find more.
`findings.py` labels each failure after the fact (the generators never use these labels), and `expected_failures.json` lists the labels known at that commit with the finding each belongs to.
When a fix lands, the failures with its label disappear; remove the label from `expected_failures.json`.
A failure labelled `unlabelled` is a finding nobody has named yet.

## Safety of the runs

- The helper in `ParseOracle/` only calls `Parse`. No command action is invoked, so it cannot sign in or reach an environment. It runs with `HOME` and `PPDS_CONFIG_DIR` pointing at empty temporary folders.
- Shell runs use stand-ins named `ppds`, `ppds-mcp-server`, `dotnet` and `dnx` (and `.exe` and upper-case spellings) that only record their arguments to a file and exit.
- Each shell run has a temporary working directory, `HOME` and `PPDS_CONFIG_DIR` inside it, `PATH` set to the stand-in folder plus `/usr/bin:/bin`, and a timeout. Before the first run the lab asserts that every guarded name resolves to its stand-in and that no real `ppds` or `ppds-mcp-server` exists on the system folders.
- The text vocabulary has no deletion, no network access and no redirection outside the temporary folder.
- The hook runs with a made-up allowlist and profile store and a scrubbed environment (no `PPDS_*`, `CLAUDE_*` or credential variables).

## Where it runs in CI

The Python tests job (`.github/workflows/workflow-tests.yml`, `ubuntu-latest`) runs `python -m pytest tests/`, so it collects these tests.

- Oracle 2 needs `bash` and `pwsh`; the hosted Ubuntu image has both.
- Oracle 1 needs the .NET 10 SDK that `global.json` pins. That job does not run `actions/setup-dotnet`, so whether the image's preinstalled SDK satisfies `global.json` decides whether oracle 1 builds there; when it does not, oracle 1 skips with the reason, and oracle 2 then judges only the `gate-unlisted` mode.
- To run oracle 1 in CI, add `actions/setup-dotnet` with `global-json-file: global.json` to that job. This change does not touch the workflow.

## What is not covered

- Timing: no generated input is large. The hook's run time is recorded per run (`slower_than_budget`) but the late-answer finding of round 2 needs long inputs this generator does not make.
- Windows shells: `cmd`, Windows PowerShell 5.1 and Git Bash are not run. Windows' case-insensitive program lookup is only modelled for a few spellings (`PPDS`, `Ppds`, `DotNet`, `DOTNET` and the `.exe` names).
- What a command does beyond its parse: write-ness of a path comes from `read_paths.json`, a frozen copy of the read-only paths of the hook's table at 3164e825, which review B checked handler by handler, plus the conditional rules above. A handler that writes despite being listed there is not caught.
- The .NET SDK's own argument forwarding for `dotnet run`, `dnx` and `dotnet tool exec` is approximated (see `-approx`).
- SQL that reaches another environment through a `[LABEL].entity` name, and profile-to-environment resolution for `--profile`, are not modelled.
- Script files (`./x.sh`, `pwsh -File`), aliases defined in the text, and programs found through a changed `PATH` are not generated.
