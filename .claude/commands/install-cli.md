# Install CLI

Pack and install the latest local build of PPDS CLI as a global tool.

## Usage

`/install-cli`

## What It Does

Run the existing installation script:

```powershell
.\scripts\Install-LocalCli.ps1
```

This script:
1. Packs PPDS.Cli to `nupkgs/`
2. Finds the latest package by timestamp
3. Uninstalls any existing version
4. Installs the new version globally

## If Installation Fails

**Stop and prompt the user.** Installation failures often occur because:
- TUI is running in another terminal session (file lock)
- Another CLI process is active
- Antivirus is blocking the operation

The user may not realize another process has the CLI locked.

## After Installation

Verify with:
```bash
ppds --version
```

## When to Use

- After making changes to PPDS.Cli or its dependencies
- When testing CLI behavior locally

**Alternative:** The terminal profile's `ppds` function runs CLI directly from the worktree without global installation. Run `/setup-terminal` to install the profile.
