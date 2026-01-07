# Setup PPDS Terminal Profile

Install or update the PPDS terminal profile on this machine.

## What This Does

Installs PowerShell functions for parallel PPDS development:

| Command | Purpose |
|---------|---------|
| `ppds` | Runs CLI from current worktree (no reinstalling!) |
| `goto` | Quick navigation to worktrees with tab completion |
| `ppdsw` | Open new terminal tabs/panes per worktree |
| (prompt) | Shows `[worktree:branch]` so you know where you are |

## Instructions

Run the installer script from the SDK:

```powershell
& C:\VS\ppds\sdk\scripts\Install-PpdsTerminalProfile.ps1
```

If reinstalling/updating, add `-Force`:

```powershell
& C:\VS\ppds\sdk\scripts\Install-PpdsTerminalProfile.ps1 -Force
```

After installation, restart PowerShell or reload the profile:

```powershell
. $PROFILE
```

## Custom Base Path

If your PPDS repos are not in `C:\VS\ppds`, specify the path:

```powershell
& C:\VS\ppds\sdk\scripts\Install-PpdsTerminalProfile.ps1 -PpdsBasePath "D:\Projects\ppds"
```

## Verification

After installation, test the commands:

```powershell
# Check ppds runs from worktree
cd C:\VS\ppds\sdk
ppds --version
# Should show: [ppds: sdk] followed by version

# Test goto picker
goto
# Should show numbered list of worktrees

# Test workspace launcher
ppdsw sdk -Split
# Should open a split pane in sdk directory
```
