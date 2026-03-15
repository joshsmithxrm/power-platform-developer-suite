# Plan: Rename `src/extension/` → `src/PPDS.Extension/`

**Date:** 2026-03-15
**Status:** Complete
**Branch:** feature/vscode-extension-mvp (or dedicated branch)

## Motivation

`src/PPDS.Extension/` is the only project under `src/` that doesn't follow the `PPDS.*` naming convention. This inconsistency:
- Caused the daemon debug path bug (relative path from `src/PPDS.Extension/` to `src/PPDS.Cli/` was miscalculated)
- Creates cognitive friction when navigating the codebase
- Makes path calculations non-obvious (is it `../PPDS.Cli` or `../src/PPDS.Cli`?)

## Prerequisites

- No parallel sessions actively modifying extension paths
- Clean working tree on the target branch

## Execution

### Step 1: Rename the folder

```bash
git mv src/PPDS.Extension src/PPDS.Extension
```

### Step 2: Root configuration (4 files)

| File | What to change |
|------|---------------|
| `package.json` | All `ext:*` npm scripts use `--prefix src/PPDS.Extension` → `--prefix src/PPDS.Extension` |
| `ppds.code-workspace` | Folder path and task config references |
| `.gitignore` | `extension/test-results/`, `extension/playwright-report/` |
| `README.md` | Documentation reference |

### Step 3: CI/CD workflows (3 files)

| File | What to change |
|------|---------------|
| `.github/workflows/build.yml` | Path filters, working directories |
| `.github/workflows/extension-publish.yml` | Cache paths, job working directories |
| `.github/dependabot.yml` | npm directory config |

### Step 4: VS Code configuration (2 files)

| File | What to change |
|------|---------------|
| `.vscode/launch.json` | Extension dev paths, outFiles, test paths |
| `.vscode/tasks.json` | npm task paths |

### Step 5: Claude tooling (9 files)

| File | Refs |
|------|------|
| `.claude/commands/debug.md` | 3 |
| `.claude/commands/gates.md` | 7 |
| `.claude/commands/implement.md` | 3 |
| `.claude/commands/setup.md` | 2 |
| `.claude/commands/spec-audit.md` | 1 |
| `.claude/commands/verify.md` | 16 |
| `.claude/skills/webview-panels/SKILL.md` | 59 |
| `.claude/skills/webview-cdp/SKILL.md` | 1 |
| `.claude/hooks/pre-commit-validate.py` | 1 |

### Step 6: Specs and plans (21 files)

Bulk replace in `specs/` and `docs/plans/`. Highest-impact files:
- `specs/2026-02-08-vscode-extension-mvp.md` (~122 refs)
- `docs/plans/2026-03-15-extension-mvp-review-fixes.md` (~92 refs)
- `docs/plans/2026-03-15-retrospective-tooling-fixes.md` (~78 refs)

### Step 7: Source code and docs (4 files)

| File | What to change |
|------|---------------|
| `CLAUDE.md` | Testing commands, key files |
| `CONTRIBUTING.md` | Directory tree |
| `src/PPDS.Cli/Plugins/Registration/PluginRegistrationService.cs` | Comment path |
| `src/PPDS.Extension/scripts/bundle-cli.js` | Comment (already partially fixed) |

### Step 8: Extension internal configs

Check these for any absolute path references that need updating:
- `src/PPDS.Extension/esbuild.js`
- `src/PPDS.Extension/knip.json`
- `src/PPDS.Extension/dev/vite.config.ts`
- `src/PPDS.Extension/.vscode/README.md`

## Verification

```bash
# TypeScript compiles
npm run ext:compile

# Unit tests pass
npm run ext:test

# Full gate check
# /gates
```

## Commit strategy

Single atomic commit:
```
refactor: rename src/PPDS.Extension/ to src/PPDS.Extension/ for naming consistency
```
