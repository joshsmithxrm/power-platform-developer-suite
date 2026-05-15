# Gates - Reference

Rationale, recovery patterns, and post-gate guidance for the `gates` skill.
The procedural steps live in `SKILL.md`. This file is loaded on demand.

## §1 - Why mechanical gates

Gates are pass/fail compiler/linter/test checks - no judgment, no opinions. They
catch regressions that a reviewer cannot reliably spot (signature drift, lint
violations, snapshot diffs). Code review is for logic and design; gates are for
mechanical correctness. Run gates first; never review code that does not compile.

## §2 - File-locking recovery (.NET)

If `dotnet build PPDS.sln` fails with "used by another process" errors (common
when the daemon is running from debug output, or when ext-verify has VS Code
open), retry by building only the changed projects individually:

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q
dotnet build src/PPDS.Query/PPDS.Query.csproj -v q
# etc. - only the projects with changed files
```

The daemon runs from shadow-copied binaries, but the solution build may still
contend with other processes that hold open handles to `bin/` outputs.

## §3 - Compile vs typecheck (TypeScript)

`npm run compile` (Gate 3) only runs esbuild, which does NOT type-check.
`npm run typecheck:all` (Gate 3.5) runs `tsc --noEmit` against both
`tsconfig.json` (host) and `tsconfig.webview.json` (browser) to catch type
errors that esbuild silently passes. Both gates are required - skipping
3.5 is how type errors leak to runtime.

## §4 - TUI snapshot baselines

Snapshots in `tests/PPDS.Tui.E2eTests/tests/__snapshots__/` are committed
baselines for visual regression testing. If your TUI changes produce new or
updated snapshots, commit them with the code that changed them. New screens
generate new snapshot files; layout changes update existing ones.
Use `npm run tui:test -- --update-snapshots` to regenerate after intentional
visual changes.

## §5 - Post-gate reminder (necessary but not sufficient)

Gates are necessary but NOT sufficient. After gates pass:

- **Webview TS/CSS/HTML changed** (`src/PPDS.Extension/src/panels/`): you MUST
  also run `/verify extension` and/or `/qa extension` for visual verification.
  Gates prove code compiles and tests pass - they do NOT prove it renders
  correctly or works as the user would experience.
- **CLI commands changed** (`src/PPDS.Cli/Commands/`, not `Serve/`): run the
  command and verify the output.
- **MCP tools changed** (`src/PPDS.Mcp/`): call the tool and verify the
  response.
- **TUI changed** (`src/PPDS.Cli/Tui/`): run `/verify tui` for interactive
  verification (Gate 5.5 covers snapshot regression, but interactive testing
  catches what snapshots can't).

**Do not commit UI/CLI/MCP changes with only gates passing.** That is how
bugs ship undetected.

## §6 - Workflow continuation rationale

Gates are ONE step in the shipping pipeline - not the last one. The `/gates`
SKILL.md ends by checking `phase` in workflow-state and either returning to
the orchestrator (when `/implement` is driving) or executing
`/verify` -> `/pr` itself.

The #1 workflow failure mode (retro R-01, 2026-04-24) was reporting
"all gates pass" and stopping. 3 of 9 PRs in v1.1 were never submitted because
the AI stopped here. The chain is `/gates` -> `/verify` -> `/pr`.
The `session-stop-workflow.py` hook (T1) blocks Stop events when gates have
passed but verify has not yet run.

## §7 - Rules (rationale)

1. **Mechanical only** - subjective judgment belongs in `/review`, not here.
   Mixing the two makes both worse: reviewers cannot trust gate output, and
   gate failures get rationalized away.
2. **All gates run** - "they probably pass" is the failure mode that ships
   bugs. The cost of running an extra gate is seconds; the cost of skipping
   is a regression in main.
3. **Exact output** - report exact error messages, not summaries. The
   implementer needs the literal compiler text to diagnose.
4. **Binary verdict** - PASS (all green) or FAIL (any red). "PASS with
   warnings" is how warnings become errors over time.
5. **No fixes** - report problems, don't fix them. Mixing detection with
   repair makes the gate non-deterministic and obscures what was actually
   broken.

## §8 - Preflight + pipefail

### Why preflight is FAIL not SKIP

If `npm` is missing the toolchain is unavailable in this shell — the gate
cannot tell whether the code is correct. Reporting SKIP creates a false
"didn't run, probably fine" signal that callers (and humans) treat as
PASS. Reporting FAIL forces the operator to either fix the environment
or rerun from a shell with the toolchain active. The same logic applies
to `dotnet` for .NET gates.

This failure mode is real, not hypothetical: a session in May 2026 ran
six TS gates with `npm: command not found`, piped each through
`| tail -10`, and reported all six PASS because `tail` returned 0. See
`.investigation/node-gates-recommendation.md` for the full root cause
(fnm's per-shell PATH activation not firing in the shell that launched
Claude Code).

### Why pipefail matters

POSIX shells (Git Bash included) return the exit status of the *last*
command in a pipeline by default. So:

```bash
$ false | tail -10; echo "exit=$?"
exit=0                              # tail succeeded reading stdin
```

`tail` is happy reading the error output and returns 0, masking the
upstream failure. The three safe patterns:

```bash
# A: capture to file, tail only on failure (preferred — preserves full log)
npm run compile --prefix src/PPDS.Extension >.gates.log 2>&1 \
    || { tail -40 .gates.log; exit 1; }

# B: enable pipefail for the pipeline
set -o pipefail
npm run compile --prefix src/PPDS.Extension 2>&1 | tail -40

# C: inspect ${PIPESTATUS[0]} explicitly
npm run compile --prefix src/PPDS.Extension 2>&1 | tail -40
[ "${PIPESTATUS[0]}" -eq 0 ] || exit 1
```

Pattern A is preferred for gate commands because the full log is on disk
for later inspection — `tail` is only invoked on failure to surface a
preview. Patterns B and C are fine for ad-hoc shell work but lose the
upstream lines on long failures.
