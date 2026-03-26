# Setup Command

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-15
**Code:** [.claude/skills/setup/SKILL.md](../.claude/skills/setup/SKILL.md)

---

## Overview

The `/setup` command configures a complete PPDS development environment from scratch or refreshes an existing one. It serves three audiences: developers on new machines, new contributors joining the project, and AI agents in fresh Claude Code sessions. Supports Windows and Linux (dev containers).

### Goals

- **Zero-to-productive in one command**: A new contributor should be able to clone, build, test, and develop after running `/setup`
- **Non-interactive refresh**: `--update` mode restores a working environment after pulling new code without any prompts
- **Platform-aware**: Detects Windows vs Linux and skips platform-inappropriate steps

### Non-Goals

- Installing system-level tools (Node.js, .NET SDK, git, pwsh) — check and report, don't install
- Managing multiple .NET SDK versions or Node.js version managers
- Setting up CI/CD pipelines (that's ppds-alm)
- Configuring Dataverse auth profiles (that's `ppds auth` at runtime)

---

## Architecture

```
/setup                          /setup --update
  │                               │
  ▼                               ▼
Prerequisites ──────────────── Prerequisites (light)
  │                               │
  ▼                               ▼
Repo Selection ─────────────── git pull (cwd repo)
  │                               │
  ▼                               ▼
Clone/Update ───────────────── (skip)
  │                               │
  ▼                               ▼
Install Dependencies ───────── Install Dependencies
  │                               │
  ▼                               ▼
Build Verification ─────────── Build Verification
  │                               │
  ▼                               ▼
Install Tools ──────────────── Install Tools
  │                               │
  ▼                               ▼
AI Tooling Check ───────────── (skip)
  │                               │
  ▼                               ▼
DX Options ─────────────────── (skip)
  │                               │
  ▼                               ▼
Verification ───────────────── Verification
  │                               │
  ▼                               ▼
Summary ────────────────────── Summary
```

### Components

| Component | Responsibility |
|-----------|----------------|
| Prerequisites Check | Verify system tools exist on PATH with minimum versions |
| Repository Manager | Clone new repos or pull existing ones |
| Dependency Installer | dotnet restore, npm install for extension and TUI tests |
| Build Verifier | dotnet build, npm run ext:compile — prove the build works |
| Tool Installer | CLI global tool, extension VSIX (Windows only) |
| AI Tooling Check | Verify superpowers plugin is installed |
| DX Configurator | Workspace file, sound notification, status line, dev container |
| Platform Detector | Windows vs Linux, gates platform-specific steps |

---

## Specification

### Core Requirements

1. `/setup` runs an interactive wizard that takes a fresh machine to a working dev environment
2. `/setup --update` runs a non-interactive refresh that restores a working build after code changes
3. Both modes detect platform (Windows vs Linux) and skip platform-inappropriate steps
4. Prerequisites check stops early with actionable instructions if system tools are missing
5. All steps are idempotent — safe to run repeatedly
6. `--update` reports all failures at the end rather than stopping at the first one

### Primary Flows

**Flow 1: Fresh Machine (`/setup`)**

1. **Platform detection**: `uname -s` — sets `IS_WINDOWS` flag
2. **Prerequisites check**: Verify git, node (≥18), dotnet (≥8.0), pwsh (≥7.0) on PATH. On Windows also check `code`. If any missing, print what's needed with install links, stop.
3. **Choose base path**: Ask user via AskUserQuestion. Suggest common paths.
4. **Select repositories**: Multi-select from ppds, ppds-docs, ppds-alm, ppds-tools, ppds-demo, vault. Show descriptions.
5. **Clone/update repos**: For each selected repo — clone if missing, pull if exists and is git repo, warn and skip if exists but not git repo.
6. **Install dependencies** (if ppds selected): `dotnet restore PPDS.sln`, `npm install --prefix src/PPDS.Extension`, `npm install --prefix tests/PPDS.Tui.E2eTests`
7. **Build verification** (if ppds selected): `dotnet build PPDS.sln -v q`, `npm run ext:compile`. Stop if either fails.
8. **Install tools** (if ppds selected): Run `pwsh scripts/Install-LocalCli.ps1` for CLI. On Windows, run `npm run ext:local --prefix src/PPDS.Extension` for extension VSIX.
9. **AI tooling check**: Check if superpowers is installed by looking for `~/.claude/plugins/cache/claude-plugins-official/superpowers/` directory. If not found, print install commands (do not auto-install):
    ```
    Superpowers plugin not installed. The PPDS workflow depends on it.
    Install with:
      /plugin marketplace add obra/superpowers-marketplace
      /plugin install superpowers@superpowers-marketplace
    ```
10. **DX options**: Multi-select from: VS Code workspace, sound notification (Windows only), status line. Note: if `.devcontainer/` exists in the repo, mention it in the summary ("Dev container config found at .devcontainer/ — use for isolated feature work").
11. **Verification**: Run `ppds --version`, `npm run ext:test`, `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
12. **Summary**: Print what was set up, versions, any warnings, next steps.

**Flow 2: Refresh (`/setup --update`)**

Operates on the repo at the current working directory. Non-interactive — no questions asked.

1. **Platform detection**: Same as above
2. **Prerequisites (light)**: Verify git, node, dotnet, pwsh are on PATH (same checks as wizard, same stop-and-report behavior). This catches scenarios where a tool was removed or downgraded between sessions.
3. **git pull**: Pull the repo at the current working directory
4. **Install dependencies**: `dotnet restore PPDS.sln`, `npm install --prefix src/PPDS.Extension`, `npm install --prefix tests/PPDS.Tui.E2eTests`
5. **Build**: `dotnet build PPDS.sln -v q`, `npm run ext:compile`
6. **Install tools**: CLI global tool. Extension VSIX on Windows.
7. **Verification**: `ppds --version`, `npm run ext:test`, `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
8. **Summary**: Print versions, report any failures

If any step fails in `--update` mode (after prerequisites), log the failure and continue to the next step. Report all failures in the summary. Prerequisites still stop early — no point continuing if Node.js is missing.

### Constraints

- Never install system-level tools (Node.js, .NET SDK, git, pwsh, VS Code)
- Never auto-install superpowers plugin (marketplace has known issues)
- Use `pwsh` not `powershell.exe` per workspace CLAUDE.md
- Never use shell redirections (`2>&1`, `>`, `>>`) per workspace CLAUDE.md
- Extension VSIX install only on Windows (not meaningful in dev containers)
- Sound notification only on Windows (uses `System.Media.SystemSounds`) — hook command must use `pwsh` not `powershell`

### Validation Rules

| Check | Rule | Error |
|-------|------|-------|
| git | `git --version` exits 0 | "git not found. Install from https://git-scm.com/" |
| node | `node --version` ≥ 18.x | "Node.js ≥18 required. Install from https://nodejs.org/" |
| dotnet | `dotnet --version` ≥ 8.x | ".NET SDK ≥8.0 required. Install from https://dot.net/" |
| pwsh | `pwsh --version` ≥ 7.x | "PowerShell 7+ required. Install from https://aka.ms/powershell" |
| code | `code --version` exits 0 | "VS Code not found. VSIX installation will be skipped." (Windows only — skip check entirely on Linux) |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `/setup` on a fresh machine with all prerequisites clones repos, installs deps, builds, and passes verification | Manual test | 🔲 |
| AC-02 | `/setup` stops with actionable message when Node.js is missing | Manual test | 🔲 |
| AC-03 | `/setup --update` restores a working build after `git pull` with dependency changes | Manual test | 🔲 |
| AC-04 | `/setup --update` reports all failures at end, not just the first | Manual test | 🔲 |
| AC-05 | On Linux (dev container), Windows-only steps (VSIX install, sound, code check) are skipped | Manual test | 🔲 |
| AC-06 | `--update` mode asks zero questions (fully non-interactive) | Manual test | 🔲 |
| AC-07 | Running `/setup` twice produces same result (idempotent) | Manual test | 🔲 |
| AC-08 | Superpowers check detects missing plugin and prints install commands | Manual test | 🔲 |
| AC-09 | vault repo appears in selection list and clones correctly | Manual test | 🔲 |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Repo folder exists but is not a git repo | Warn and skip that repo |
| npm install fails (network error) | Report failure, continue to next step in --update; stop in wizard mode |
| CLI install fails (file lock) | Report "TUI or CLI may be running in another terminal" |
| `code` not found on Linux | Skip silently (expected in dev containers) |
| settings.json doesn't exist | Create it with the new config |
| settings.json exists with other hooks | Merge, don't overwrite |
| Superpowers already installed | Skip check, no message |
| dotnet restore succeeds but build fails | Report build errors with file:line |

---

## Design Decisions

### Why check-and-stop for system tools?

**Context:** System tools (Node, .NET, git) have version managers, admin requirements, and platform-specific installers. Auto-installing risks conflicts.

**Decision:** Check prerequisites exist with minimum versions. If missing, print what's needed with install links and stop. Don't attempt installation.

**Alternatives considered:**
- Auto-install via winget/apt: Risky — could conflict with nvm, sdkman, or existing installations
- Ignore and let steps fail: Poor UX — cryptic errors instead of clear guidance

### Why two modes instead of one smart idempotent command?

**Context:** A new machine needs an interactive wizard (which repos? which DX options?). A daily refresh needs zero prompts.

**Decision:** `/setup` is interactive wizard, `/setup --update` is non-interactive refresh. Different entry points, shared steps for dependencies/build/tools.

**Alternatives considered:**
- Single mode that detects state: Complex to implement, unclear UX when repos are partially set up
- Separate commands (`/setup` and `/refresh`): Harder to discover, duplicates documentation

### Why not auto-install superpowers?

**Context:** The superpowers plugin is a hard dependency for the PPDS workflow. The Claude Code marketplace has had recognition issues as of March 2026.

**Decision:** Check if installed, print manual commands if not. Don't auto-install.

**Alternatives considered:**
- Auto-install: Could fail silently due to marketplace bugs, leaving the user in a broken state without knowing why

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| Base path | string | Yes (wizard) | None — always ask | Where to clone repositories |
| Selected repos | string[] | Yes (wizard) | All | Which repos to clone |
| DX options | string[] | No | None | Workspace, sound, status line |

---

## Extension Points

### Adding a New Repository

1. Add row to Step 2 repository selection table
2. Add clone URL to Repository URLs table
3. Add folder to workspace file generation

### Adding a New Prerequisite

1. Add row to Prerequisites check with command, version, and error message
2. Add platform gate if platform-specific

### Adding a New DX Option

1. Add row to DX options multi-select
2. Add execution logic with platform gate if needed
3. Add idempotent behavior row

---

## Repository URLs

| Folder | GitHub URL |
|--------|------------|
| `ppds` | `https://github.com/joshsmithxrm/power-platform-developer-suite.git` |
| `ppds-docs` | `https://github.com/joshsmithxrm/ppds-docs.git` |
| `ppds-alm` | `https://github.com/joshsmithxrm/ppds-alm.git` |
| `ppds-tools` | `https://github.com/joshsmithxrm/ppds-tools.git` |
| `ppds-demo` | `https://github.com/joshsmithxrm/ppds-demo.git` |
| `vault` | Private — ask maintainer for access |

---

## Related Specs

- None — `/setup` is self-contained tooling, not application code

---

## Roadmap

- macOS support (if contributors use macOS)
- `--check` flag that only runs prerequisites + verification without modifying anything
- Integration with `ppds auth create-profile` for first-time Dataverse auth setup
