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
