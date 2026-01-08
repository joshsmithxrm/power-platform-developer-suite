# Claude Code Version Pinning & Auto-Update Disable (Windows)

## Install a Specific Version
```powershell
& ([scriptblock]::Create((irm https://claude.ai/install.ps1))) 2.0.72
```

Replace `2.0.72` with whatever version you need.

## Find Available Versions
```bash
npm view @anthropic-ai/claude-code versions --json
```

## Disable Auto-Updates

### Option 1: Set Environment Variable (Per Session)
```powershell
$env:DISABLE_AUTOUPDATER = "1"
claude
```

### Option 2: Set Permanently via System Settings

1. Search "Environment Variables" in Start menu
2. Edit the system environment variables → Environment Variables
3. Under User variables → New
4. Name: `DISABLE_AUTOUPDATER`, Value: `1`

### Option 3: Add to PowerShell Profile
```powershell
# Add this line to your $PROFILE
$env:DISABLE_AUTOUPDATER = "1"
```

## Verify Installation
```bash
claude --version
claude doctor
```

## Manual Update (When Ready)
```bash
claude update
```

Or re-run the installer with a specific version.

## Alternative: Install via npm
```bash
npm install -g @anthropic-ai/claude-code@2.0.72
```

---

*Note: `claude config set -g autoUpdates disabled` exists but is reportedly inconsistent. The environment variable method is more reliable.*
