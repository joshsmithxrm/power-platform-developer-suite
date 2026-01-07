# Setup PPDS Ecosystem

Set up PPDS repositories and developer tools on a new machine.

## Usage

`/setup-ecosystem`

## What It Does

Interactive wizard that:
1. Clones PPDS repositories
2. Creates VS Code workspace
3. Installs terminal helpers (`ppds`, `goto`, `ppdsw`)
4. Configures Claude notifications and status line

## Flow

### Step 1: Choose Base Path

Ask where to put repos (use AskUserQuestion):
- Default: `C:\VS\ppds` (Windows) or `~/dev/ppds` (macOS/Linux)
- User can specify any path via "Other" option

### Step 2: Select Repositories

Multi-select (AskUserQuestion with multiSelect: true):

| Option | Description |
|--------|-------------|
| `sdk` | NuGet packages & CLI (core) |
| `extension` | VS Code extension |
| `tools` | PowerShell module |
| `alm` | CI/CD templates |
| `demo` | Reference implementation |

### Step 3: Developer Experience Options

Multi-select (AskUserQuestion with multiSelect: true):

| Option | Description |
|--------|-------------|
| VS Code workspace | Create `ppds.code-workspace` file |
| Terminal profile | Install `ppds`, `goto`, `ppdsw` commands |
| Sound notification | Play Windows sound when Claude finishes |
| Status line | Show directory and git branch in Claude's status bar |

### Step 4: Execute Setup

#### Clone/Update Repositories

For each selected repo:
1. Check if folder exists
2. If exists and is git repo: `git pull` to update
3. If exists but not git repo: Warn and skip
4. If not exists: Clone from GitHub

```bash
git clone https://github.com/joshsmithxrm/ppds-sdk.git {base}/sdk
git clone https://github.com/joshsmithxrm/power-platform-developer-suite.git {base}/extension
git clone https://github.com/joshsmithxrm/ppds-tools.git {base}/tools
git clone https://github.com/joshsmithxrm/ppds-alm.git {base}/alm
git clone https://github.com/joshsmithxrm/ppds-demo.git {base}/demo
```

#### Create VS Code Workspace (if selected)

Generate `{base}/ppds.code-workspace`:
```json
{
    "folders": [
        { "path": "sdk" },
        { "path": "extension" },
        { "path": "tools" },
        { "path": "alm" },
        { "path": "demo" }
    ],
    "settings": {}
}
```
Only include folders that were actually cloned.

#### Install Terminal Profile (if selected)

Run the installer script:
```powershell
& "{base}/sdk/scripts/Install-PpdsTerminalProfile.ps1" -PpdsBasePath "{base}"
```

This installs:
- `ppds` - Runs CLI from current worktree
- `goto` - Quick navigation to worktrees
- `ppdsw` - Open new terminal tabs/panes
- Custom prompt showing `[worktree:branch]`

#### Setup Sound Notification (if selected)

Add Stop hook to `~/.claude/settings.json`:
```json
{
  "hooks": {
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "powershell -NoProfile -Command \"[System.Media.SystemSounds]::Asterisk.Play()\"",
            "timeout": 3
          }
        ]
      }
    ]
  }
}
```

Read existing settings.json first, merge the hooks section, then write back.

#### Setup Status Line (if selected)

**Important:** The statusLine command requires an absolute path - `~` does not expand correctly on Windows.

1. First, detect the user's home directory:
```powershell
# Windows
$homePath = [Environment]::GetFolderPath('UserProfile')
# macOS/Linux
$homePath = $env:HOME
```

2. Create status line script at `{homePath}/.claude/statusline.ps1`:

**Windows version** (PowerShell):
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

# ANSI colors: cyan for dir, magenta for branch
$cyan = "$([char]27)[96m"
$magenta = "$([char]27)[95m"
$reset = "$([char]27)[0m"

if ($branch) {
    Write-Output "${cyan}${dir}${reset} ${magenta}(${branch})${reset}"
} else {
    Write-Output "${cyan}${dir}${reset}"
}
```

**macOS/Linux version** (bash):
```bash
#!/bin/bash
# PPDS Claude status line - shows directory and git branch with colors
read -r json
dir=$(echo "$json" | jq -r '.workspace.current_dir' | xargs basename)
branch=$(git -C "$(echo "$json" | jq -r '.workspace.current_dir')" branch --show-current 2>/dev/null)

# ANSI colors: cyan for dir, magenta for branch
cyan='\033[96m'
magenta='\033[95m'
reset='\033[0m'

if [ -n "$branch" ]; then
    echo -e "${cyan}${dir}${reset} ${magenta}(${branch})${reset}"
else
    echo -e "${cyan}${dir}${reset}"
fi
```

3. Add statusLine config to `~/.claude/settings.json` with **absolute path**:

**Windows:**
```json
{
  "statusLine": {
    "type": "command",
    "command": "pwsh -NoProfile -File C:/Users/USERNAME/.claude/statusline.ps1"
  }
}
```

**macOS/Linux:**
```json
{
  "statusLine": {
    "type": "command",
    "command": "bash /Users/USERNAME/.claude/statusline.sh"
  }
}
```

Replace `USERNAME` with the actual username from the detected home path.

This shows `sdk (feature/my-branch)` at the bottom of Claude's UI with cyan directory and magenta branch - great for split panes!

### Step 5: Summary

Display what was set up:
```
Setup complete!

Repositories cloned:
  - sdk
  - extension

Developer tools configured:
  - VS Code workspace: {base}/ppds.code-workspace
  - Terminal profile: ppds, goto, ppdsw commands installed
  - Sound notification: Plays when Claude finishes
  - Status line: Shows directory and git branch in Claude UI

Next steps:
  - Open workspace: code "{base}/ppds.code-workspace"
  - Restart terminal to load profile
  - Restart Claude Code for hooks/status line to take effect
```

## Idempotent Behavior

| Scenario | Action |
|----------|--------|
| Folder doesn't exist | Clone repo |
| Folder exists, is git repo | `git pull` to update |
| Folder exists, not git repo | Warn and skip |
| Workspace file exists | Ask to overwrite or skip |
| Terminal profile exists | Reinstall with `-Force` |
| settings.json exists | Merge new config (don't overwrite) |

## Repository URLs

| Shorthand | GitHub URL |
|-----------|------------|
| `sdk` | `https://github.com/joshsmithxrm/ppds-sdk.git` |
| `extension` | `https://github.com/joshsmithxrm/power-platform-developer-suite.git` |
| `tools` | `https://github.com/joshsmithxrm/ppds-tools.git` |
| `alm` | `https://github.com/joshsmithxrm/ppds-alm.git` |
| `demo` | `https://github.com/joshsmithxrm/ppds-demo.git` |

## Example Session

```
User: /setup-ecosystem

Claude: Where should PPDS repos live?
[AskUserQuestion with default C:\VS\ppds]

User: C:\VS\ppds (default)

Claude: Which repos do you need?
[AskUserQuestion multiSelect]

User: [selects sdk, extension]

Claude: Which developer tools do you want?
[AskUserQuestion multiSelect]

User: [selects all: workspace, terminal, sound, status line]

Claude: Setting up C:\VS\ppds...

Cloning sdk... done
Cloning extension... done
Creating ppds.code-workspace... done
Installing terminal profile... done
Configuring sound notification... done
Setting up status line... done

Setup complete!

To open in VS Code: code "C:\VS\ppds\ppds.code-workspace"
Restart your terminal to load the profile.
Restart Claude Code for hooks and status line to take effect.
```
