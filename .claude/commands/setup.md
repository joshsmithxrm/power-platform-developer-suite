# Setup

Set up PPDS development environment on a new machine.

## Usage

`/setup` - Interactive wizard
`/setup --terminal` - Just install terminal profile
`/setup --update` - Update existing installation

## What It Does

Interactive wizard that:
1. Clones PPDS repositories
2. Creates VS Code workspace
3. Installs terminal helpers (`ppds`, `goto`, `ppdsw`)
4. Configures Claude notifications and status line

## Process

### Step 1: Choose Base Path

Ask where to put repos (use AskUserQuestion):
- Suggest common paths: `C:\Dev`, `D:\Projects`, `~/dev`, etc.
- User can specify any path via "Other" option
- No hardcoded default - always ask

### Step 2: Select Repositories

Multi-select:

| Option | Description |
|--------|-------------|
| `ppds` | SDK: NuGet packages & CLI (core) |
| `ppds-docs` | Documentation site |
| `ppds-alm` | CI/CD templates |
| `ppds-tools` | PowerShell module |
| `ppds-demo` | Reference implementation |

### Step 3: Developer Experience Options

Multi-select:

| Option | Description |
|--------|-------------|
| VS Code workspace | Create `ppds.code-workspace` file |
| Terminal profile | Install `ppds`, `goto`, `ppdsw` commands |
| Sound notification | Play Windows sound when Claude finishes |
| Status line | Show directory and git branch in Claude UI |

### Step 4: Execute Setup

#### Clone/Update Repositories

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

#### Install Terminal Profile (if selected)

Run the installer script:
```powershell
& "{base}/ppds/scripts/Install-PpdsTerminalProfile.ps1" -PpdsBasePath "{base}"
```

This installs:
- `ppds` - Runs CLI from current worktree
- `goto` - Quick navigation to worktrees with tab completion
- `ppdsw` - Open new terminal tabs/panes per worktree
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

**Important:** The statusLine command requires an absolute path.

1. Detect user's home directory:
```powershell
$homePath = [Environment]::GetFolderPath('UserProfile')
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

$cyan = "$([char]27)[96m"
$magenta = "$([char]27)[95m"
$reset = "$([char]27)[0m"

if ($branch) {
    Write-Output "${cyan}${dir}${reset} ${magenta}(${branch})${reset}"
} else {
    Write-Output "${cyan}${dir}${reset}"
}
```

3. Add statusLine config to `~/.claude/settings.json` using the user's home directory:
```json
{
  "statusLine": {
    "type": "command",
    "command": "pwsh -NoProfile -File ~/.claude/statusline.ps1"
  }
}
```

> **Note:** Use `~` for cross-platform compatibility. On Windows, Claude Code expands `~` to `$env:USERPROFILE`.

### Step 5: Summary

```
Setup complete!

Repositories cloned:
  - ppds
  - ppds-docs

Developer tools configured:
  - VS Code workspace: {base}/ppds.code-workspace
  - Terminal profile: ppds, goto, ppdsw commands installed
  - Sound notification: Plays when Claude finishes
  - Status line: Shows directory and git branch

Next steps:
  - Open workspace: code "{base}/ppds.code-workspace"
  - Restart terminal to load profile
  - Restart Claude Code for hooks/status line
```

## Terminal Profile Only

If `/setup --terminal` is used, skip repository cloning and just run:

```powershell
& {path-to-ppds}\scripts\Install-PpdsTerminalProfile.ps1 -PpdsBasePath "{base}" -Force
```

Then reload:
```powershell
. $PROFILE
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

| Folder | GitHub URL |
|--------|------------|
| `ppds` | `https://github.com/joshsmithxrm/power-platform-developer-suite.git` |
| `ppds-docs` | `https://github.com/joshsmithxrm/ppds-docs.git` |
| `ppds-alm` | `https://github.com/joshsmithxrm/ppds-alm.git` |
| `ppds-tools` | `https://github.com/joshsmithxrm/ppds-tools.git` |
| `ppds-demo` | `https://github.com/joshsmithxrm/ppds-demo.git` |

## When to Use

- Setting up a new development machine
- Adding a new developer to the project
- Updating tools after changes (`--update`)
- Reinstalling terminal profile (`--terminal`)

## Verification

After setup, test:
```powershell
cd {base}\ppds
ppds --version
goto  # Should show worktree list
```
