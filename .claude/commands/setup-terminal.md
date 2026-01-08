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

Run the installer script from wherever you cloned the SDK, specifying your base path:

```powershell
& {path-to-ppds}\scripts\Install-PpdsTerminalProfile.ps1 -PpdsBasePath "{your-base-path}"
```

Example:
```powershell
& C:\Dev\ppds\scripts\Install-PpdsTerminalProfile.ps1 -PpdsBasePath "C:\Dev"
```

If reinstalling/updating, add `-Force`:

```powershell
& {path-to-ppds}\scripts\Install-PpdsTerminalProfile.ps1 -PpdsBasePath "{your-base-path}" -Force
```

After installation, restart PowerShell or reload the profile:

```powershell
. $PROFILE
```

## Verification

After installation, test the commands:

```powershell
# Navigate to your ppds folder
cd {your-base-path}\ppds
ppds --version
# Should show: [ppds: ppds] followed by version

# Test goto picker
goto
# Should show numbered list of worktrees

# Test workspace launcher
ppdsw sdk -Split
# Should open a split pane in sdk directory
```
