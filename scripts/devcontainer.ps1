<#
.SYNOPSIS
    PPDS devcontainer management script.
.DESCRIPTION
    Uses a Docker volume for the workspace (not a bind mount) for native Linux I/O performance.
    The local Windows repo is only used to locate the devcontainer config.
    Code lives in the ppds-workspace Docker volume.
.EXAMPLE
    .\scripts\devcontainer.ps1 up                       # build + start + ready to go
    .\scripts\devcontainer.ps1 shell                    # bash shell (prompts for worktree)
    .\scripts\devcontainer.ps1 shell query-engine-v3    # bash shell in worktree
    .\scripts\devcontainer.ps1 claude                   # claude code (prompts for worktree)
    .\scripts\devcontainer.ps1 claude query-engine-v3   # claude code in worktree
    .\scripts\devcontainer.ps1 down                     # stop container
    .\scripts\devcontainer.ps1 sync                     # push local changes into the workspace volume
    .\scripts\devcontainer.ps1 reset                    # nuke everything, full clean rebuild
#>

param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'shell', 'claude', 'down', 'status', 'sync', 'reset', 'help')]
    [string]$Command = 'help',

    [Parameter(Position = 1)]
    [string]$Target
)

$ErrorActionPreference = 'Stop'
$WorkspaceFolder = Split-Path -Parent $PSScriptRoot
$WorkspaceVolume = 'ppds-workspace'
$NugetVolume = 'ppds-nuget-cache'
$PluginVolume = 'ppds-claude-plugins'
$RepoUrl = 'https://github.com/joshsmithxrm/power-platform-developer-suite.git'

function Write-Step($msg) { Write-Host "  $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  $msg" -ForegroundColor Green }
function Write-Err($msg)  { Write-Host "  $msg" -ForegroundColor Red }

function Get-ContainerId {
    docker ps -aq --filter "label=devcontainer.local_folder=$WorkspaceFolder" 2>$null
}

function Stop-Container {
    $ids = Get-ContainerId
    if ($ids) {
        Write-Step 'Stopping container...'
        $ids | ForEach-Object { docker rm -f $_ } | Out-Null
    }
}

function Ensure-ContainerRunning {
    $id = docker ps -q --filter "label=devcontainer.local_folder=$WorkspaceFolder"
    if (-not $id) {
        Write-Step 'Container not running, starting it...'
        devcontainer up --workspace-folder $WorkspaceFolder | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Err 'Failed to start container.'
            exit 1
        }
    }
}

function Ensure-WorkspaceVolume {
    # Create volume if it doesn't exist
    $volExists = docker volume ls -q --filter "name=^${WorkspaceVolume}$"
    if (-not $volExists) {
        Write-Step "Creating workspace volume ($WorkspaceVolume)..."
        docker volume create $WorkspaceVolume | Out-Null

        # Clone repo into the volume (always clone default branch, switch later inside container)
        Write-Step "Cloning repo into volume..."
        docker run --rm -v "${WorkspaceVolume}:/workspace" alpine/git clone $RepoUrl /workspace
        # Fix ownership — alpine/git runs as root, devcontainer runs as vscode (uid 1000)
        docker run --rm -v "${WorkspaceVolume}:/workspace" alpine chown -R 1000:1000 /workspace
        if ($LASTEXITCODE -ne 0) {
            Write-Err 'Failed to clone repo into volume.'
            docker volume rm $WorkspaceVolume | Out-Null
            exit 1
        }
        Write-Ok 'Workspace volume ready.'
    }
    else {
        Write-Ok 'Workspace volume exists.'
    }
}

function Get-Worktrees {
    # Query the container for worktrees (they live in the volume, not on Windows)
    $raw = devcontainer exec --workspace-folder $WorkspaceFolder sh -c 'ls -d .worktrees/*/ 2>/dev/null' 2>$null
    if ($raw) {
        $raw -split "`n" | ForEach-Object { ($_.Trim() -replace '/$','') -replace '^\.worktrees/','' } | Where-Object { $_ }
    }
}

function Select-WorkingDirectory {
    param([string]$Target)

    $worktrees = @(Get-Worktrees)

    # No worktrees — use repo root
    if ($worktrees.Count -eq 0) { return $null }

    # Target specified directly — validate and use it
    if ($Target) {
        if ($Target -eq 'main' -or $Target -eq 'root') { return $null }
        if ($worktrees -contains $Target) { return ".worktrees/$Target" }
        Write-Err "Worktree '$Target' not found. Available: $($worktrees -join ', ')"
        exit 1
    }

    # Get main branch name from container
    $mainBranch = devcontainer exec --workspace-folder $WorkspaceFolder git branch --show-current 2>$null
    if (-not $mainBranch) { $mainBranch = 'main' }

    # Prompt user to pick
    Write-Host ''
    Write-Host '  Where do you want to work?' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "  [0] main repo ($($mainBranch.Trim()))" -ForegroundColor White
    for ($i = 0; $i -lt $worktrees.Count; $i++) {
        $branch = devcontainer exec --workspace-folder $WorkspaceFolder git -C ".worktrees/$($worktrees[$i])" branch --show-current 2>$null
        if (-not $branch) { $branch = $worktrees[$i] }
        Write-Host "  [$($i + 1)] $($worktrees[$i]) ($($branch.Trim()))" -ForegroundColor White
    }
    Write-Host ''
    $choice = Read-Host '  Select'

    if ($choice -eq '0' -or $choice -eq '') { return $null }

    $idx = [int]$choice - 1
    if ($idx -ge 0 -and $idx -lt $worktrees.Count) {
        return ".worktrees/$($worktrees[$idx])"
    }

    Write-Err "Invalid selection."
    exit 1
}

switch ($Command) {
    'up' {
        Ensure-WorkspaceVolume

        # Ensure NuGet volume exists with correct ownership (Docker creates as root)
        $nugetExists = docker volume ls -q --filter "name=^${NugetVolume}$"
        if (-not $nugetExists) {
            docker volume create $NugetVolume | Out-Null
        }
        docker run --rm -v "${NugetVolume}:/nuget" alpine chown -R 1000:1000 /nuget

        Write-Step 'Building and starting devcontainer...'
        devcontainer up --workspace-folder $WorkspaceFolder
        if ($LASTEXITCODE -eq 0) {
            Write-Ok 'Container is running.'
            Write-Host ''
            Write-Host '  Next steps:' -ForegroundColor Yellow
            Write-Host '    .\scripts\devcontainer.ps1 shell     # bash shell'
            Write-Host '    .\scripts\devcontainer.ps1 claude    # claude code'
            Write-Host ''
        }
        else { Write-Err 'Failed to start container.'; exit 1 }
    }

    'shell' {
        Ensure-ContainerRunning
        $subdir = Select-WorkingDirectory -Target $Target
        if ($subdir) {
            devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd $subdir && exec bash"
        }
        else {
            devcontainer exec --workspace-folder $WorkspaceFolder bash
        }
    }

    'claude' {
        Ensure-ContainerRunning
        $subdir = Select-WorkingDirectory -Target $Target
        if ($subdir) {
            devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd $subdir && claude --dangerously-skip-permissions"
        }
        else {
            devcontainer exec --workspace-folder $WorkspaceFolder claude --dangerously-skip-permissions
        }
    }

    'down' {
        Stop-Container
        Write-Ok 'Container stopped.'
    }

    'status' {
        $id = docker ps -q --filter "label=devcontainer.local_folder=$WorkspaceFolder"
        if ($id) {
            Write-Ok 'Container is running.'
            docker ps --filter "label=devcontainer.local_folder=$WorkspaceFolder" --format "table {{.ID}}\t{{.Status}}\t{{.Ports}}"
        }
        else {
            Write-Err 'Container is not running.'
        }
    }

    'sync' {
        # Push current local branch state into the workspace volume
        $branch = git -C $WorkspaceFolder branch --show-current
        Write-Step "Syncing local repo into volume (branch: $branch)..."
        $volExists = docker volume ls -q --filter "name=^${WorkspaceVolume}$"
        if (-not $volExists) {
            Write-Err "Workspace volume does not exist. Run 'up' first."
            exit 1
        }
        docker run --rm `
            -v "${WorkspaceVolume}:/workspace" `
            -v "${WorkspaceFolder}:/source:ro" `
            alpine sh -c "cd /workspace && rm -rf /workspace/* /workspace/.* 2>/dev/null; cp -a /source/. /workspace/"
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "Volume synced to local state ($branch)."
        }
        else { Write-Err 'Sync failed.'; exit 1 }
    }

    'reset' {
        Write-Step 'Nuking everything for a clean rebuild...'

        # Stop and remove container
        Stop-Container

        # Remove all named volumes
        foreach ($vol in @($WorkspaceVolume, $NugetVolume, $PluginVolume)) {
            $exists = docker volume ls -q --filter "name=^${vol}$"
            if ($exists) {
                Write-Step "Removing volume $vol..."
                docker volume rm $vol | Out-Null
            }
        }

        # Remove devcontainer images (both manual and CLI-generated)
        $images = docker images --format "{{.Repository}}:{{.Tag}}" | Where-Object {
            $_ -match 'ppds-devcontainer' -or $_ -match 'vsc-ppds-'
        }
        foreach ($img in $images) {
            Write-Step "Removing image $img..."
            docker rmi $img 2>$null | Out-Null
        }

        # Fresh start — volume + container
        Write-Step 'Starting fresh...'
        & $PSCommandPath up
    }

    'help' {
        Write-Host ''
        Write-Host '  PPDS Devcontainer' -ForegroundColor Yellow
        Write-Host ''
        Write-Host '  Usage: .\scripts\devcontainer.ps1 <command> [target]' -ForegroundColor White
        Write-Host ''
        Write-Host '  Commands:' -ForegroundColor White
        Write-Host '    up                    Build + clone into volume + start'
        Write-Host '    shell [worktree]      Open bash shell (prompts for worktree if any exist)'
        Write-Host '    claude [worktree]     Start Claude Code (prompts for worktree if any exist)'
        Write-Host '    down                  Stop the container'
        Write-Host '    status                Check if container is running'
        Write-Host '    sync                  Push local repo state into the workspace volume'
        Write-Host '    reset                 Nuke container + all volumes + rebuild from scratch'
        Write-Host ''
        Write-Host '  Examples:' -ForegroundColor White
        Write-Host '    .\scripts\devcontainer.ps1 claude                   # prompts for location'
        Write-Host '    .\scripts\devcontainer.ps1 claude query-engine-v3   # straight to worktree'
        Write-Host '    .\scripts\devcontainer.ps1 shell main               # shell at repo root'
        Write-Host '    .\scripts\devcontainer.ps1 sync                     # push local changes to volume'
        Write-Host ''
    }
}
