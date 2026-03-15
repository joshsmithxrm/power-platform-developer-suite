# Setup

Set up a complete PPDS development environment on a new machine, or refresh an existing one after pulling new code.

## Usage

`/setup` - Interactive wizard (fresh machine or new contributor)
`/setup --update` - Non-interactive refresh (restore working build after code changes)

## What It Does

**Wizard mode (`/setup`):** Prerequisites check → clone repos → install dependencies → build → install tools → check AI tooling → configure DX options → verify → summary.

**Update mode (`/setup --update`):** Prerequisites check → git pull → install dependencies → build → install tools → verify → summary. Zero questions asked.

> **Note:** For terminal customization (Oh My Posh, eza, bat, etc.), see the separate [dotfiles repo](https://github.com/joshsmithxrm/dotfiles).

## Process

### Step 0: Platform Detection

Detect the platform to gate Windows-only steps:

```bash
uname -s
```

- Contains `MINGW` or `MSYS` → Windows (`IS_WINDOWS=true`)
- Otherwise → Linux (`IS_WINDOWS=false`)

Windows-only steps: VS Code check, extension VSIX install, sound notification.

### Step 1: Prerequisites Check

Verify required tools are on PATH. If any are missing, print what's needed and **stop** — don't continue with a broken environment.

| Tool | Check | Min Version | Error |
|------|-------|-------------|-------|
| git | `git --version` | Any | "git not found. Install from https://git-scm.com/" |
| node | `node --version` | ≥18 | "Node.js ≥18 required. Install from https://nodejs.org/" |
| dotnet | `dotnet --version` | ≥8.0 | ".NET SDK ≥8.0 required. Install from https://dot.net/" |
| pwsh | `pwsh --version` | ≥7.0 | "PowerShell 7+ required. Install from https://aka.ms/powershell" |
| code | `code --version` | Any | "VS Code not found. VSIX installation will be skipped." |

**Platform rules:**
- `code` check: Windows only. Skip entirely on Linux (not expected in dev containers).
- `code` is a soft prerequisite: missing `code` prints a warning but does not stop setup.

**Version parsing:** `node --version` returns `v18.19.0`, `dotnet --version` returns `8.0.404`. Parse the major version number for comparison.

**Both modes** run this step. In `--update` mode, this catches scenarios where a tool was removed or downgraded between sessions.

---

**The following steps differ by mode. See "Update Mode" section below for `--update` flow.**

---

### Step 2: Choose Base Path (wizard only)

Ask where to put repos (use AskUserQuestion):
- Suggest common paths: `C:\VS`, `C:\Dev`, `D:\Projects`, `~/dev`, etc.
- User can specify any path via "Other" option
- No hardcoded default — always ask

### Step 3: Select Repositories (wizard only)

Multi-select:

| Option | Description |
|--------|-------------|
| `ppds` | SDK + CLI + TUI + VS Code Extension + MCP (core) |
| `ppds-docs` | Documentation site (Docusaurus) |
| `ppds-alm` | CI/CD templates (GitHub Actions) |
| `ppds-tools` | PowerShell module (CLI wrapper) |
| `ppds-demo` | Reference Dataverse implementation |
| `vault` | Personal knowledge base (Obsidian) — private, ask maintainer for access |

### Step 4: Clone/Update Repositories (wizard only)

For each selected repo:
1. Check if folder exists
2. If exists and is git repo: `git pull` to update
3. If exists but not git repo: Warn and skip
4. If not exists: Clone from GitHub

```bash
git clone https://github.com/joshsmithxrm/power-platform-developer-suite.git {base}/ppds
git clone https://github.com/joshsmithxrm/ppds-docs.git {base}/ppds-docs
git clone https://github.com/joshsmithxrm/ppds-alm.git {base}/ppds-alm
git clone https://github.com/joshsmithxrm/ppds-tools.git {base}/ppds-tools
git clone https://github.com/joshsmithxrm/ppds-demo.git {base}/ppds-demo
```

`vault` is private — if selected, ask the user for the clone URL.

### Step 5: Install Dependencies (if ppds selected/present)

```bash
dotnet restore PPDS.sln
npm install --prefix src/PPDS.Extension
npm install --prefix tests/tui-e2e
```

### Step 6: Build Verification (if ppds selected/present)

```bash
dotnet build PPDS.sln -v q
npm run ext:compile
```

If either fails, **stop** and report the error. Dependencies are wrong or something is misconfigured — no point continuing.

### Step 7: Install Tools (if ppds selected/present)

**CLI global tool:**
```bash
pwsh scripts/Install-LocalCli.ps1
```

If installation fails, report "TUI or CLI may be running in another terminal (file lock)" and continue.

Verify: `ppds --version`

**Extension VSIX (Windows only):**
```bash
npm run ext:local --prefix src/PPDS.Extension
```

Skip on Linux — not meaningful in dev containers.

### Step 8: AI Tooling Check (wizard only)

Check if superpowers plugin is installed:

```bash
ls ~/.claude/plugins/cache/claude-plugins-official/superpowers/
```

If the directory does not exist, print:

```
Superpowers plugin not installed. The PPDS workflow depends on it.
Install with:
  /plugin marketplace add obra/superpowers-marketplace
  /plugin install superpowers@superpowers-marketplace
```

Do NOT auto-install — the marketplace has known recognition issues. Let the user run the commands manually.

If the directory exists, skip silently.

### Step 9: Developer Experience Options (wizard only)

Multi-select:

| Option | Description | Platform |
|--------|-------------|----------|
| VS Code workspace | Create `ppds.code-workspace` file | Both |
| Sound notification | Play Windows sound when Claude finishes | Windows only |
| Status line | Show directory and git branch in Claude UI | Both |

#### Create VS Code Workspace (if selected)

Generate `{base}/ppds.code-workspace`:
```json
{
    "folders": [
        { "path": "ppds" },
        { "path": "ppds-docs" },
        { "path": "ppds-alm" },
        { "path": "ppds-tools" },
        { "path": "ppds-demo" }
    ],
    "settings": {}
}
```
Only include folders that were actually cloned.

#### Setup Sound Notification (if selected, Windows only)

Add Stop hook to `~/.claude/settings.json`:
```json
{
  "hooks": {
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "pwsh -NoProfile -Command \"[System.Media.SystemSounds]::Asterisk.Play()\"",
            "timeout": 3
          }
        ]
      }
    ]
  }
}
```

Read existing settings.json first, merge the hooks section, then write back. Use `pwsh` not `powershell`.

#### Setup Status Line (if selected)

**Windows version** — create `~/.claude/statusline.ps1`:
```powershell
# PPDS Claude status line - shows directory and git branch with colors
$json = [Console]::In.ReadToEnd()
$data = $json | ConvertFrom-Json
$dir = Split-Path $data.workspace.current_dir -Leaf
$branch = ""
try {
    Push-Location $data.workspace.current_dir
    $b = git branch --show-current 2>$null
    if ($LASTEXITCODE -eq 0 -and $b) { $branch = $b }
    Pop-Location
} catch {}

$cyan = "$([char]27)[96m"
$magenta = "$([char]27)[95m"
$reset = "$([char]27)[0m"

if ($branch) {
    Write-Output "${cyan}${dir}${reset} ${magenta}(${branch})${reset}"
} else {
    Write-Output "${cyan}${dir}${reset}"
}
```

**Linux version** — create `~/.claude/statusline.sh`:
```bash
#!/bin/bash
read -r json
dir=$(echo "$json" | node -e "const d=JSON.parse(require('fs').readFileSync('/dev/stdin','utf8'));console.log(require('path').basename(d.workspace.current_dir))")
branch=$(cd "$(echo "$json" | node -e "const d=JSON.parse(require('fs').readFileSync('/dev/stdin','utf8'));console.log(d.workspace.current_dir)")" && git branch --show-current 2>/dev/null || true)
if [ -n "$branch" ]; then
    echo -e "\033[96m${dir}\033[0m \033[95m(${branch})\033[0m"
else
    echo -e "\033[96m${dir}\033[0m"
fi
```

Add statusLine config to `~/.claude/settings.json`:

Windows:
```json
{ "statusLine": { "type": "command", "command": "pwsh -NoProfile -File ~/.claude/statusline.ps1" } }
```

Linux:
```json
{ "statusLine": { "type": "command", "command": "bash ~/.claude/statusline.sh" } }
```

### Step 10: Verification

```bash
ppds --version
npm run ext:test
dotnet test PPDS.sln --filter "Category!=Integration" -v q
```

### Step 11: Summary

```
Setup complete!

Platform: {Windows/Linux}
Prerequisites: git {ver}, node {ver}, dotnet {ver}, pwsh {ver}

Repositories cloned:
  - ppds
  - ppds-docs

Dependencies installed:
  - .NET packages restored
  - Extension npm packages installed
  - TUI test packages installed

Tools installed:
  - CLI: ppds {version}
  - Extension VSIX: installed (Windows) / skipped (Linux)

AI Tooling:
  - Superpowers: installed / NOT INSTALLED (see instructions above)

Developer tools configured:
  - VS Code workspace: {base}/ppds.code-workspace
  - Sound notification: Plays when Claude finishes
  - Status line: Shows directory and git branch

Dev container: config found at .devcontainer/ — use for isolated feature work

Next steps:
  - Open workspace: code "{base}/ppds.code-workspace"
  - Restart Claude Code for hooks/status line
  - For terminal customization: https://github.com/joshsmithxrm/dotfiles
```

If `.devcontainer/` exists in the ppds repo, include the dev container note. Otherwise omit.

---

## Update Mode (`/setup --update`)

Non-interactive. Operates on the repo at the current working directory. No questions asked.

1. **Platform detection** (Step 0)
2. **Prerequisites check** (Step 1) — same checks, same stop behavior
3. **git pull** the repo at the current working directory
4. **Install dependencies** (Step 5)
5. **Build** (Step 6)
6. **Install tools** (Step 7)
7. **Verification** (Step 10)
8. **Summary** — print versions, report any failures

**Error handling:** If any step fails after prerequisites, log the failure and continue to the next step. Report all failures in the summary at the end. Prerequisites still stop early.

---

## Idempotent Behavior

| Scenario | Action |
|----------|--------|
| Folder doesn't exist | Clone repo |
| Folder exists, is git repo | `git pull` to update |
| Folder exists, not git repo | Warn and skip |
| Workspace file exists | Ask to overwrite or skip |
| settings.json exists | Merge new config (don't overwrite) |
| npm node_modules exists | `npm install` updates if needed |
| .NET packages restored | `dotnet restore` is a no-op |
| CLI already installed | Script uninstalls and reinstalls |
| Superpowers installed | Skip check, no message |
| Dev container exists | Note in summary, don't modify |

## Repository URLs

| Folder | GitHub URL |
|--------|------------|
| `ppds` | `https://github.com/joshsmithxrm/power-platform-developer-suite.git` |
| `ppds-docs` | `https://github.com/joshsmithxrm/ppds-docs.git` |
| `ppds-alm` | `https://github.com/joshsmithxrm/ppds-alm.git` |
| `ppds-tools` | `https://github.com/joshsmithxrm/ppds-tools.git` |
| `ppds-demo` | `https://github.com/joshsmithxrm/ppds-demo.git` |
| `vault` | Private — ask maintainer for access |

## When to Use

- Setting up a new development machine
- Adding a new developer to the project
- Refreshing after pulling new code (`--update`)
- AI agent in a fresh Claude Code session
