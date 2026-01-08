<#
.SYNOPSIS
    Installs or updates the PPDS terminal profile for parallel worktree development.

.DESCRIPTION
    Sets up PowerShell with:
    - Dynamic `ppds` command that runs the CLI from your current worktree
    - `goto` command for quick navigation between worktrees
    - `ppdsw` (Start-PpdsWorkspace) to launch new terminal tabs/panes
    - Visual prompt showing current worktree and branch

.PARAMETER Force
    Overwrite existing profile without prompting.

.PARAMETER PpdsBasePath
    Base path where PPDS repos are located. Required parameter - no default.

.EXAMPLE
    .\Install-PpdsTerminalProfile.ps1 -PpdsBasePath "C:\Dev"

.EXAMPLE
    .\Install-PpdsTerminalProfile.ps1 -Force -PpdsBasePath "D:\Projects"
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [Parameter(Mandatory=$true)]
    [string]$PpdsBasePath
)

$ErrorActionPreference = "Stop"

# Determine profile paths for both Windows PowerShell and PowerShell Core
$windowsPowerShellProfile = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "WindowsPowerShell\profile.ps1"
$powerShellCoreProfile = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "PowerShell\Microsoft.PowerShell_profile.ps1"

# Use current shell's profile as primary
$profilePath = $PROFILE.CurrentUserAllHosts
if (-not $profilePath) {
    $profilePath = $PROFILE
}

Write-Host "PPDS Terminal Profile Installer" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Profile locations:"
Write-Host "  Windows PowerShell: $windowsPowerShellProfile"
Write-Host "  PowerShell Core:    $powerShellCoreProfile"
Write-Host "PPDS base path:       $PpdsBasePath"
Write-Host ""

# Check if profile already exists in either location
$alreadyInstalled = $false
foreach ($prof in @($windowsPowerShellProfile, $powerShellCoreProfile)) {
    if (Test-Path $prof) {
        $existingContent = Get-Content $prof -Raw
        if ($existingContent -match "# PPDS Terminal Profile") {
            $alreadyInstalled = $true
        } elseif (-not $Force) {
            # Backup existing non-PPDS profile
            $backupPath = "$prof.backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
            Copy-Item $prof $backupPath
            Write-Host "Backed up existing profile to: $backupPath" -ForegroundColor Green
        }
    }
}

if ($alreadyInstalled -and -not $Force) {
    Write-Host "PPDS profile already installed. Use -Force to reinstall." -ForegroundColor Yellow
    return
}

if ($alreadyInstalled) {
    Write-Host "Reinstalling PPDS profile..." -ForegroundColor Yellow
}

# Ensure profile directory exists
$profileDir = Split-Path $profilePath -Parent
if (-not (Test-Path $profileDir)) {
    New-Item -ItemType Directory -Path $profileDir -Force | Out-Null
    Write-Host "Created profile directory: $profileDir" -ForegroundColor Green
}

# Profile content
$profileContent = @"
# PPDS Terminal Profile
# Installed by Install-PpdsTerminalProfile.ps1
# https://github.com/joshsmithxrm/power-platform-developer-suite

`$script:PpdsBasePath = "$PpdsBasePath"

#region Dynamic ppds Command
<#
.SYNOPSIS
    Runs the ppds CLI from your current worktree automatically.
.DESCRIPTION
    When you're in a PPDS worktree (ppds/, ppds-feature-x/, etc.), this runs
    that worktree's CLI via 'dotnet run'. Outside worktrees, uses the global tool.
#>
function ppds {
    # Walk up from current directory looking for PPDS.Cli
    `$dir = `$PWD.Path
    while (`$dir) {
        `$cliProject = Join-Path `$dir "src\PPDS.Cli\PPDS.Cli.csproj"
        if (Test-Path `$cliProject) {
            # Found it - run this worktree's CLI
            `$worktreeName = Split-Path `$dir -Leaf
            Write-Host "[ppds: `$worktreeName]" -ForegroundColor DarkGray
            dotnet run --project `$cliProject --framework net8.0 --no-launch-profile -- @args
            return
        }
        `$parent = Split-Path `$dir -Parent
        if (-not `$parent -or `$parent -eq `$dir) { break }
        `$dir = `$parent
    }

    # Not in a worktree - use global tool
    `$globalPpds = Get-Command ppds -CommandType Application -ErrorAction SilentlyContinue
    if (`$globalPpds) {
        & `$globalPpds @args
    } else {
        Write-Error "Not in a PPDS worktree and no global ppds tool installed"
    }
}
#endregion

#region Quick Navigation
<#
.SYNOPSIS
    Quickly navigate to PPDS repos.
.DESCRIPTION
    Without arguments, shows an interactive picker of all ppds* folders.
    With an argument, jumps directly to that folder (supports partial match or index).
.EXAMPLE
    goto           # Interactive picker
    goto ppds-docs # Jump to ppds-docs (partial match)
    goto 0         # Jump to first item (ppds)
    goto 1         # Jump to second item (ppds-docs)
#>
function goto {
    param([string]`$Worktree)

    if (-not (Test-Path `$script:PpdsBasePath)) {
        Write-Error "PPDS base path not found: `$script:PpdsBasePath"
        return
    }

    # Get all ppds* folders with custom sort order
    `$priorityOrder = @('ppds', 'ppds-docs', 'ppds-alm', 'ppds-tools', 'ppds-demo')
    `$allFolders = Get-ChildItem `$script:PpdsBasePath -Directory | Where-Object {
        `$_.Name -like "ppds*"
    } | Select-Object -ExpandProperty Name

    `$worktrees = @()
    foreach (`$p in `$priorityOrder) {
        if (`$allFolders -contains `$p) { `$worktrees += `$p }
    }
    `$worktrees += `$allFolders | Where-Object { `$_ -notin `$priorityOrder } | Sort-Object

    # Support direct number access (goto 0, goto 1)
    if (`$Worktree -match '^\d+`$') {
        `$idx = [int]`$Worktree
        if (`$idx -lt `$worktrees.Count) {
            `$Worktree = `$worktrees[`$idx]
        } else {
            Write-Error "Index `$idx out of range (max: `$(`$worktrees.Count - 1))"
            return
        }
    }

    if (-not `$Worktree) {
        # Interactive picker with enhancements
        Write-Host "PPDS Repos:" -ForegroundColor Cyan
        for (`$i = 0; `$i -lt `$worktrees.Count; `$i++) {
            `$wt = `$worktrees[`$i]
            `$wtPath = Join-Path `$script:PpdsBasePath `$wt
            `$branch = git -C `$wtPath branch --show-current 2>`$null

            # Check for uncommitted changes (dirty indicator)
            `$dirty = ""
            `$status = git -C `$wtPath status --porcelain 2>`$null
            if (`$status) { `$dirty = "*" }

            # Color by type: Yellow for SDK (has PPDS.Cli), Cyan for others
            `$isSDK = Test-Path (Join-Path `$wtPath "src\PPDS.Cli")
            `$nameColor = if (`$isSDK) { "Yellow" } else { "Cyan" }

            Write-Host "  [`$i] " -NoNewline -ForegroundColor DarkGray
            Write-Host `$wt -NoNewline -ForegroundColor `$nameColor
            if (`$branch) {
                Write-Host " (`$branch`$dirty)" -ForegroundColor DarkGray
            } else {
                Write-Host ""
            }
        }
        Write-Host ""
        `$selection = Read-Host "Select (number or name)"

        if (`$selection -match '^\d+`$' -and [int]`$selection -lt `$worktrees.Count) {
            `$Worktree = `$worktrees[[int]`$selection]
        } else {
            `$Worktree = `$selection
        }
    }

    # Support partial matching
    `$match = `$worktrees | Where-Object { `$_ -like "`$Worktree*" } | Select-Object -First 1
    if (`$match) {
        `$Worktree = `$match
    }

    `$targetPath = Join-Path `$script:PpdsBasePath `$Worktree
    if (Test-Path `$targetPath) {
        Set-Location `$targetPath
        `$host.UI.RawUI.WindowTitle = "PPDS: `$Worktree"
    } else {
        Write-Error "Repo not found: `$Worktree"
    }
}

# Tab completion for goto
Register-ArgumentCompleter -CommandName goto -ParameterName Worktree -ScriptBlock {
    param(`$commandName, `$parameterName, `$wordToComplete, `$commandAst, `$fakeBoundParameters)

    if (Test-Path `$script:PpdsBasePath) {
        Get-ChildItem `$script:PpdsBasePath -Directory |
            Where-Object { `$_.Name -like "ppds*" -and `$_.Name -like "`$wordToComplete*" } |
            ForEach-Object {
                [System.Management.Automation.CompletionResult]::new(
                    `$_.Name,
                    `$_.Name,
                    'ParameterValue',
                    `$_.Name
                )
            }
    }
}
#endregion

#region Workspace Launcher
<#
.SYNOPSIS
    Opens a new terminal tab/pane in a PPDS worktree.
.DESCRIPTION
    Launches Windows Terminal with a new tab or split pane in the specified worktree.
    Optionally launches Claude Code alongside.
.PARAMETER Worktree
    Name of the worktree folder (e.g., sdk-tui-ux)
.PARAMETER WithClaude
    Also launch Claude Code in a split pane
.PARAMETER Split
    Add as split pane instead of new tab
.EXAMPLE
    Start-PpdsWorkspace sdk-tui-ux
    ppdsw sdk-tui-ux -WithClaude -Split
#>
function Start-PpdsWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)]
        [string]`$Worktree,

        [switch]`$WithClaude,
        [switch]`$Split
    )

    `$worktreePath = Join-Path `$script:PpdsBasePath `$Worktree

    if (-not (Test-Path `$worktreePath)) {
        Write-Error "Worktree not found: `$worktreePath"
        return
    }

    # Build Windows Terminal command arguments
    `$wtArgs = @('-w', '0')
    if (`$Split) {
        `$wtArgs += 'split-pane', '-d', `$worktreePath, '--title', `$Worktree
    } else {
        `$wtArgs += 'new-tab', '-d', `$worktreePath, '--title', `$Worktree
    }

    if (`$WithClaude) {
        `$wtArgs += ';', 'split-pane', '-d', `$worktreePath, '--title', "`$Worktree (Claude)", 'pwsh', '-NoExit', '-Command', 'claude'
    }

    # Execute wt with all arguments at once
    & wt @`$wtArgs
}

Set-Alias ppdsw Start-PpdsWorkspace

# Tab completion for Start-PpdsWorkspace
Register-ArgumentCompleter -CommandName Start-PpdsWorkspace -ParameterName Worktree -ScriptBlock {
    param(`$commandName, `$parameterName, `$wordToComplete, `$commandAst, `$fakeBoundParameters)

    if (Test-Path `$script:PpdsBasePath) {
        Get-ChildItem `$script:PpdsBasePath -Directory |
            Where-Object { `$_.Name -like "ppds*" -and `$_.Name -like "`$wordToComplete*" } |
            ForEach-Object {
                [System.Management.Automation.CompletionResult]::new(
                    `$_.Name,
                    `$_.Name,
                    'ParameterValue',
                    `$_.Name
                )
            }
    }
}
#endregion

#region Visual Prompt
function Get-PpdsPromptInfo {
    `$dir = `$PWD.Path
    if (`$dir -like "`$script:PpdsBasePath\*") {
        `$relative = `$dir.Substring(`$script:PpdsBasePath.Length + 1)
        `$worktree = (`$relative -split '\\')[0]
        `$branch = git branch --show-current 2>`$null
        if (`$branch) {
            return "[`${worktree}:`${branch}]"
        }
        return "[`$worktree]"
    }
    return `$null
}

function prompt {
    `$ppdsInfo = Get-PpdsPromptInfo
    if (`$ppdsInfo) {
        Write-Host `$ppdsInfo -NoNewline -ForegroundColor Magenta
        Write-Host " " -NoNewline
    }

    # Shortened path display
    `$shortPath = `$PWD.Path
    if (`$shortPath.Length -gt 50) {
        `$parts = `$shortPath -split '\\'
        if (`$parts.Count -gt 3) {
            `$shortPath = `$parts[0] + "\...\" + (`$parts[-2..-1] -join '\')
        }
    }

    Write-Host `$shortPath -NoNewline -ForegroundColor Blue
    return "> "
}
#endregion

Write-Host "[PPDS] Terminal profile loaded. Commands: ppds, goto, ppdsw" -ForegroundColor DarkGray
"@

# Write to both profile locations
$profilesToWrite = @($windowsPowerShellProfile, $powerShellCoreProfile) | Select-Object -Unique

foreach ($prof in $profilesToWrite) {
    $profDir = Split-Path $prof -Parent
    if (-not (Test-Path $profDir)) {
        New-Item -ItemType Directory -Path $profDir -Force | Out-Null
    }
    Set-Content -Path $prof -Value $profileContent -Encoding UTF8
    Write-Host "Wrote profile to: $prof" -ForegroundColor Green
}

Write-Host ""
Write-Host "PPDS terminal profile installed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "Available commands:" -ForegroundColor Cyan
Write-Host "  ppds     - Runs CLI from current worktree automatically"
Write-Host "  goto     - Quick navigation to worktrees (with tab completion)"
Write-Host "  ppdsw    - Open new terminal tab/pane in a worktree"
Write-Host ""
Write-Host "Restart PowerShell or run: . `$PROFILE" -ForegroundColor Yellow
