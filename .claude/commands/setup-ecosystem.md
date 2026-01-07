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
| Status line | Show worktree name in Claude's status bar |

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

1. Create status line script at `~/.claude/statusline.ps1`:
```powershell
# PPDS Claude status line - shows worktree name
$data = $input | ConvertFrom-Json
$dir = Split-Path $data.workspace.current_dir -Leaf
$model = $data.model.display_name
Write-Output "[$model] $dir"
```

2. Add statusLine config to `~/.claude/settings.json`:
```json
{
  "statusLine": {
    "type": "command",
    "command": "powershell -NoProfile -File ~/.claude/statusline.ps1"
  }
}
```

This shows `[Claude Sonnet 4] sdk-tui-enhancements` at the bottom of Claude's UI - great for split panes!

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
  - Status line: Shows worktree name in Claude UI

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
