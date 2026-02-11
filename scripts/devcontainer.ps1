<#
.SYNOPSIS
    PPDS devcontainer management script.
.EXAMPLE
    .\scripts\devcontainer.ps1 up        # build + start + ready to go
    .\scripts\devcontainer.ps1 shell     # bash shell in container
    .\scripts\devcontainer.ps1 claude    # claude code in container
    .\scripts\devcontainer.ps1 down      # stop container
    .\scripts\devcontainer.ps1 reset     # nuke everything, full clean rebuild
#>

param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'shell', 'claude', 'down', 'status', 'reset', 'help')]
    [string]$Command = 'help'
)

$ErrorActionPreference = 'Stop'
$WorkspaceFolder = Split-Path -Parent $PSScriptRoot
$ImageName = 'ppds-devcontainer'
$VolumeName = 'ppds-claude-plugins'

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

switch ($Command) {
    'up' {
        # Build + start in one command. devcontainer CLI handles caching.
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
        devcontainer exec --workspace-folder $WorkspaceFolder bash
    }

    'claude' {
        Ensure-ContainerRunning
        devcontainer exec --workspace-folder $WorkspaceFolder claude --dangerously-skip-permissions
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

    'reset' {
        Write-Step 'Nuking everything for a clean rebuild...'

        # Stop and remove container
        Stop-Container

        # Remove named volume (plugin cache)
        $vol = docker volume ls -q --filter "name=$VolumeName" 2>$null
        if ($vol) {
            Write-Step "Removing volume $VolumeName..."
            docker volume rm $VolumeName | Out-Null
        }

        # Remove the named image so it rebuilds from scratch
        $img = docker images -q $ImageName 2>$null
        if ($img) {
            Write-Step "Removing image $ImageName..."
            docker rmi $ImageName
            if ($LASTEXITCODE -ne 0) {
                Write-Err "Failed to remove image '$ImageName'. It may be in use. Aborting reset."
                exit 1
            }
        }

        # Full rebuild no cache
        Write-Step 'Rebuilding image (no cache)...'
        docker build --no-cache -t $ImageName "$WorkspaceFolder/.devcontainer/"
        if ($LASTEXITCODE -ne 0) { Write-Err 'Build failed.'; exit 1 }

        # Start fresh
        Write-Step 'Starting fresh container...'
        devcontainer up --workspace-folder $WorkspaceFolder
        if ($LASTEXITCODE -eq 0) {
            Write-Ok 'Clean rebuild complete. Container is running.'
            Write-Host ''
            Write-Host '  Next steps:' -ForegroundColor Yellow
            Write-Host '    .\scripts\devcontainer.ps1 shell     # bash shell'
            Write-Host '    .\scripts\devcontainer.ps1 claude    # claude code'
            Write-Host ''
        }
        else { Write-Err 'Failed to start container.'; exit 1 }
    }

    'help' {
        Write-Host ''
        Write-Host '  PPDS Devcontainer' -ForegroundColor Yellow
        Write-Host ''
        Write-Host '  Usage: .\scripts\devcontainer.ps1 <command>' -ForegroundColor White
        Write-Host ''
        Write-Host '  Commands:' -ForegroundColor White
        Write-Host '    up        Build + start (one command, handles everything)'
        Write-Host '    shell     Open bash shell (auto-starts if needed)'
        Write-Host '    claude    Start Claude Code (auto-starts if needed)'
        Write-Host '    down      Stop the container'
        Write-Host '    status    Check if container is running'
        Write-Host '    reset     Nuke container + volumes + rebuild from scratch'
        Write-Host ''
    }
}
