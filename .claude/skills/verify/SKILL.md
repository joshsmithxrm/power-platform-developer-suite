---
name: verify
description: AI self-verification of implemented work across surfaces (extension, CLI, MCP, TUI, workflow). Use after implementation to verify code works in its runtime environment.
---

# Verify

AI self-verification of implemented work. Goes beyond unit tests to verify
code actually works in its runtime environment.

## Usage

- `/verify` - auto-detect from recent changes
- `/verify cli|tui|extension|mcp|workflow` - explicit mode

## Prerequisites

| Mode | Required |
|------|----------|
| cli | Bash tool only |
| tui | `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs` (`@microsoft/tui-test`, `node-pty`) |
| extension | `src/PPDS.Extension/tools/webview-cdp.mjs` (`@playwright/test`, `@vscode/test-electron`) |
| mcp | `npx @modelcontextprotocol/inspector` |

If a prerequisite is missing, tell the user what to install and stop.

## Process

### 1. Detect component

From `$ARGUMENTS` or recent changes:
`src/PPDS.Cli/Commands/`->cli, `src/PPDS.Cli/Tui/`->tui,
`src/PPDS.Extension/`->extension, `src/PPDS.Mcp/`->mcp,
`.claude/`+`scripts/`->workflow. No match: ask.

### 2. Unit tests first

- CLI/TUI: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
- Extension: `npm run test --prefix src/PPDS.Extension`
- MCP: `dotnet test --filter "FullyQualifiedName~Mcp" -v q`

Don't waste interactive cycles on broken code.

### 3. CLI mode

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0
.\src\PPDS.Cli\bin\Debug\net10.0\ppds.exe <command> <args>
```
Verify exit 0, format correct, edge cases (empty/invalid/no-auth).

### 4. TUI mode

See `REFERENCE.md` "TUI Mode - worked example" for the Phase A/B/C
commands. Phase B is mandatory when changed files touch
`src/PPDS.Cli/Tui/`. <!-- enforcement: T3 -->

### 5. Extension mode

See `REFERENCE.md` "Extension Mode - worked example" for Phase A/B/C
commands. Phase B is mandatory when query/data/panel files change.
<!-- enforcement: T3 -->

### 6. MCP mode

```bash
npx @modelcontextprotocol/inspector --cli --server "ppds-mcp-server"
```
Per tool: valid->success shape; edge case->error handling; matches schema.

### 7. Workflow mode

**Check 1 - Python tests:**
```bash
pytest tests/test_pipeline.py tests/test_workflow_state.py tests/test_protect_main_branch.py tests/test_session_stop_workflow.py -v
```

**Check 2 - Hook scripts:** for each `.py` in `.claude/hooks/` (except
`_pathfix.py`): `echo '{}' | python .claude/hooks/{hook}.py`. Exit 0 or 2,
never 1.

**Check 3 - settings.json:** parses; every hook `command` exists on disk.

**Check 4 - Skill frontmatter:** every `.claude/skills/*/SKILL.md` parses;
`name` and `description` non-empty.

**Check 5 - Agent frontmatter:** every `.claude/agents/*.md` parses;
`tools` present; names in known set (`Read`, `Edit`, `Write`, `Glob`,
`Grep`, `Bash`, `Agent`, `WebSearch`, `WebFetch`, `NotebookEdit`, and
`Bash(pattern:*)`).

**Check 6 - Skill file refs:** every referenced path exists.

**Check 7 - Retro store schema:** see `REFERENCE.md` "Retro store schema".

**Check 8 - Behavioral scenarios:** `python scripts/verify-workflow.py`;
any failed scenario fails the check.

**Check 9 - Empirical shakedown gate** <!-- enforcement: T2 hook:shakedown-gate -->
```bash
python scripts/verify_shakedown.py
```
Allowlist source of truth: `scripts/_shakedown_allowlist.py`. When the
diff touches any allowlisted file, spawn one real `claude --bg` against
a throwaway prompt and assert exit 0; otherwise log a skip and exit 0.
Subscription pool only (`claude --bg`), never `-p`. Rationale + how to
add a wrapper: `REFERENCE.md` "Empirical shakedown gate". Exit codes:
0=skipped/passed, 1=ran-and-failed, 2=setup error.

**Check 10 - State write:** on all checks passing,
`python scripts/workflow-state.py set verify.workflow now` and
`python scripts/workflow-state.py set verify.workflow_commit_ref "$(git rev-parse HEAD)"`.

### 8. Report

See `REFERENCE.md` "Report template". Include actual values.

## Workflow state

After PASS for a surface (`ext`/`tui`/`mcp`/`cli`/`workflow`):
```bash
python scripts/workflow-state.py set verify.{surface} now
python scripts/workflow-state.py set verify.{surface}_commit_ref "$(git rev-parse HEAD)"
```

## Workflow continuation - MANDATORY <!-- enforcement: T1 hook:session-stop-workflow -->

Verify is one step in the shipping pipeline, not the last one. Check
`python scripts/workflow-state.py get phase`:
- `implementing` -> return results to `/implement`.
- otherwise -> proceed to `/pr` immediately. Pipeline is `/gates` ->
  `/verify` -> `/pr`. Do not stop.

Exception: on FAIL, fix first, rerun `/verify`, then `/pr`.

## Rules

1. Unit tests first.
2. Screenshots for visual changes - look at them, don't just take them.
3. Report actual values, not just pass/fail.
4. Prerequisites are hard gates.
5. Don't fix during verify - report problems for `/debug` or `/converge`.
