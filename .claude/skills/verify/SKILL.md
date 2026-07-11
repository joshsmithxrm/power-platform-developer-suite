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

**Check 1 - Python tests** (the full automation suite — hook behavior, skill
content, bash portability):
```bash
python -m pytest tests/ scripts/ --import-mode=importlib -q
```

**Check 2 - Hook scripts:** for each surviving `.py` in `.claude/hooks/`
(except `_pathfix.py`): `echo '{}' | python .claude/hooks/{hook}.py`. Exit
0 or 2, never 1.

**Check 3 - settings.json:** parses; every hook `command` exists on disk.

**Check 4 - Skill frontmatter:** every `.claude/skills/*/SKILL.md` parses;
`name` and `description` non-empty.

**Check 5 - Agent frontmatter:** every `.claude/agents/*.md` parses;
`tools` present; names in known set (`Read`, `Edit`, `Write`, `Glob`,
`Grep`, `Bash`, `Agent`, `WebSearch`, `WebFetch`, `NotebookEdit`, and
`Bash(pattern:*)`).

**Check 6 - Skill file refs:** every referenced path exists.

### 8. Report

See `REFERENCE.md` "Report template". Include actual values.

Verify is one step in the usual shipping sequence (`/gates` -> `/verify`
-> `/pr`), not the last one. On PASS, proceed to `/pr`. On FAIL, fix
first and re-run `/verify` before continuing.

## Rules

1. Unit tests first.
2. Screenshots for visual changes - look at them, don't just take them.
3. Report actual values, not just pass/fail.
4. Prerequisites are hard gates.
5. Don't fix during verify - report problems for `/debug`.
