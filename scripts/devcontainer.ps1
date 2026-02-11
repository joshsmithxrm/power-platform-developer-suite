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
    .\scripts\devcontainer.ps1 ppds                     # launch TUI (prompts for worktree, builds if needed)
    .\scripts\devcontainer.ps1 ppds query-engine-v3     # launch TUI in worktree
    .\scripts\devcontainer.ps1 down                     # stop container
    .\scripts\devcontainer.ps1 push                      # push container commits via host (prompts for worktree)
    .\scripts\devcontainer.ps1 push query-engine-v3      # push worktree branch via host
    .\scripts\devcontainer.ps1 send                      # send host files to container (prompts for worktree)
    .\scripts\devcontainer.ps1 send query-engine-v3      # send host worktree to container worktree
    .\scripts\devcontainer.ps1 reset                    # nuke everything, full clean rebuild
#>

param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'shell', 'claude', 'ppds', 'down', 'status', 'send', 'push', 'reset', 'help')]
    [string]$Command = 'help',

    [Parameter(Position = 1)]
    [string]$Target
)

$ErrorActionPreference = 'Stop'
$WorkspaceFolder = Split-Path -Parent $PSScriptRoot
$WorkspaceVolume = 'ppds-workspace'
$NugetVolume = 'ppds-nuget-cache'
$AuthVolume = 'ppds-auth-cache'
$ClaudeSessionsVolume = 'ppds-claude-sessions'
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

        # Ensure cache volumes exist with correct ownership (Docker creates as root)
        foreach ($vol in @($NugetVolume, $AuthVolume, $ClaudeSessionsVolume)) {
            $exists = docker volume ls -q --filter "name=^${vol}$"
            if (-not $exists) {
                docker volume create $vol | Out-Null
            }
        }
        docker run --rm -v "${NugetVolume}:/nuget" -v "${AuthVolume}:/auth" -v "${ClaudeSessionsVolume}:/sessions" alpine sh -c "chown -R 1000:1000 /nuget /auth /sessions"

        Write-Step 'Building and starting devcontainer...'
        devcontainer up --workspace-folder $WorkspaceFolder
        if ($LASTEXITCODE -eq 0) {
            # Repair host worktrees — fix .git files that have container Linux paths
            $hostWtDir = Join-Path $WorkspaceFolder '.worktrees'
            if (Test-Path $hostWtDir) {
                $hostWorktrees = Get-ChildItem -Directory $hostWtDir | Select-Object -ExpandProperty Name
                foreach ($wt in $hostWorktrees) {
                    $wtGitFile = Join-Path $hostWtDir $wt '.git'
                    if (Test-Path $wtGitFile) {
                        $gitdir = (Get-Content $wtGitFile -Raw).Trim()
                        if ($gitdir -match '^gitdir:\s*/workspaces/') {
                            $correctPath = Join-Path $WorkspaceFolder ".git/worktrees/$wt"
                            Write-Step "Repairing worktree '$wt' .git file (was container Linux path)..."
                            Set-Content -Path $wtGitFile -Value "gitdir: $($correctPath -replace '\\','/')" -NoNewline
                        }
                    }
                }
            }

            # Recreate worktrees from host into the volume
            if (Test-Path $hostWtDir) {
                $hostWorktrees = Get-ChildItem -Directory $hostWtDir | Select-Object -ExpandProperty Name
                foreach ($wt in $hostWorktrees) {
                    $branch = git -C (Join-Path $hostWtDir $wt) branch --show-current 2>$null
                    if (-not $branch) { $branch = $wt }
                    # Check if worktree already exists in container
                    $exists = devcontainer exec --workspace-folder $WorkspaceFolder sh -c "test -d .worktrees/$wt && echo yes" 2>$null
                    if ($exists -ne 'yes') {
                        Write-Step "Creating worktree: $wt ($branch)..."
                        devcontainer exec --workspace-folder $WorkspaceFolder git worktree add ".worktrees/$wt" $branch 2>$null
                        if ($LASTEXITCODE -eq 0) {
                            Write-Ok "Worktree ready: $wt ($branch)"
                        }
                        else {
                            Write-Err "Failed to create worktree $wt — branch '$branch' may not exist on remote"
                        }
                    }
                }
            }

            Write-Ok 'Container is running.'
            Write-Host ''
            Write-Host '  Next steps:' -ForegroundColor Yellow
            Write-Host '    .\scripts\devcontainer.ps1 shell     # bash shell'
            Write-Host '    .\scripts\devcontainer.ps1 claude    # claude code'
            Write-Host '    .\scripts\devcontainer.ps1 ppds      # launch TUI'
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

    'ppds' {
        Ensure-ContainerRunning
        $subdir = Select-WorkingDirectory -Target $Target
        $workdir = if ($subdir) { $subdir } else { '.' }
        Write-Step 'Building PPDS CLI...'
        devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd $workdir && dotnet build src/PPDS.Cli -f net10.0"
        if ($LASTEXITCODE -ne 0) {
            Write-Err 'Build failed.'
            exit 1
        }

        Write-Step 'Launching TUI...'
        devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd $workdir && PPDS_FORCE_TUI=1 dotnet src/PPDS.Cli/bin/Debug/net10.0/ppds.dll"
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

    'push' {
        # Push container commits to origin via the host (container has no git credentials)
        Ensure-ContainerRunning
        $subdir = Select-WorkingDirectory -Target $Target
        $workdir = if ($subdir) { $subdir } else { '.' }

        # Get branch name and local HEAD SHA from container
        $branch = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd $workdir && git branch --show-current").Trim()
        if (-not $branch) {
            Write-Err "Could not determine branch (detached HEAD?)."
            exit 1
        }
        $localSha = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd $workdir && git rev-parse HEAD").Trim()

        # Compare against actual remote state (not local tracking refs which may be stale)
        $remoteSha = (git -C $WorkspaceFolder ls-remote origin "refs/heads/${branch}" 2>$null)
        if ($remoteSha) { $remoteSha = ($remoteSha -split '\s')[0] }

        if ($localSha -eq $remoteSha) {
            Write-Ok "Branch '$branch' is already up-to-date on origin."
            return
        }

        if (-not $remoteSha) {
            $ahead = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd $workdir && git rev-list --count HEAD").Trim()
            Write-Step "New branch '$branch' ($ahead commit(s))."
        }
        else {
            $ahead = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd $workdir && git rev-list --count $remoteSha..HEAD 2>/dev/null").Trim()
            if (-not $ahead -or $ahead -eq '0') {
                Write-Ok "Branch '$branch' is already up-to-date on origin."
                return
            }
            Write-Step "Branch '$branch' is $ahead commit(s) ahead of origin."
        }

        # Create git bundle in container
        Write-Step 'Bundling commits...'
        devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd $workdir && git bundle create /tmp/push.bundle $branch" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Err 'Failed to create git bundle.'
            exit 1
        }

        # Copy bundle from container to host
        $containerId = (docker ps -q --filter "label=devcontainer.local_folder=$WorkspaceFolder").Trim()
        $tempBundle = Join-Path $env:TEMP 'ppds-push.bundle'
        docker cp "${containerId}:/tmp/push.bundle" $tempBundle | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Err 'Failed to copy bundle from container.'
            exit 1
        }

        # Fetch objects from bundle into host repo (sets FETCH_HEAD, doesn't touch working tree)
        Write-Step 'Fetching into host repo...'
        git -C $WorkspaceFolder fetch $tempBundle "refs/heads/${branch}"
        if ($LASTEXITCODE -ne 0) {
            Write-Err 'Failed to fetch from bundle.'
            Remove-Item $tempBundle -ErrorAction SilentlyContinue
            exit 1
        }

        # Push from host using FETCH_HEAD (host has git credentials)
        Write-Step 'Pushing to origin...'
        git -C $WorkspaceFolder push origin "FETCH_HEAD:refs/heads/${branch}"
        if ($LASTEXITCODE -ne 0) {
            Write-Err 'Push failed. You may need to pull/rebase first.'
            Remove-Item $tempBundle -ErrorAction SilentlyContinue
            exit 1
        }

        # Clean up
        Remove-Item $tempBundle -ErrorAction SilentlyContinue
        devcontainer exec --workspace-folder $WorkspaceFolder rm -f /tmp/push.bundle

        # Update container's remote tracking ref so git status shows up-to-date
        Write-Step 'Updating container remote refs...'
        devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd $workdir && git fetch origin $branch 2>/dev/null || git update-ref refs/remotes/origin/$branch HEAD"

        Write-Ok "Pushed '$branch' to origin ($ahead commit(s))."
    }

    'send' {
        # Sync host working tree into the container volume (preserves .git and .worktrees)
        Ensure-ContainerRunning
        $subdir = Select-WorkingDirectory -Target $Target

        if ($subdir) {
            $wtName = $subdir -replace '^\.worktrees/', ''
            $hostPath = Join-Path $WorkspaceFolder ".worktrees\$wtName"
            $containerTarget = ".worktrees/$wtName"
            $label = "worktree '$wtName'"
            $excludes = "--exclude='.git'"
        }
        else {
            $hostPath = $WorkspaceFolder
            $containerTarget = '.'
            $label = 'main repo'
            $excludes = "--exclude='.git/' --exclude='.worktrees/'"
        }

        if (-not (Test-Path $hostPath)) {
            Write-Err "Host path not found: $hostPath"
            exit 1
        }

        $branch = git -C $hostPath branch --show-current 2>$null
        if (-not $branch) { $branch = 'unknown' }
        Write-Step "Syncing $label to container ($branch)..."

        docker run --rm `
            -v "${WorkspaceVolume}:/workspace" `
            -v "${hostPath}:/source:ro" `
            alpine sh -c "apk add --no-cache rsync >/dev/null 2>&1 && rsync -a --delete $excludes /source/ /workspace/${containerTarget}/ && chown -R 1000:1000 /workspace/${containerTarget}/"
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "Synced $label to container ($branch)."
        }
        else { Write-Err 'Sync failed.'; exit 1 }
    }

    'reset' {
        Write-Step 'Nuking everything for a clean rebuild...'

        # Stop and remove container
        Stop-Container

        # Remove all named volumes
        foreach ($vol in @($WorkspaceVolume, $NugetVolume, $AuthVolume, $ClaudeSessionsVolume, $PluginVolume)) {
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
        Write-Host '    ppds [worktree]       Launch PPDS TUI (builds if needed, prompts for worktree)'
        Write-Host '    down                  Stop the container'
        Write-Host '    status                Check if container is running'
        Write-Host '    push [worktree]       Push container commits to origin via host credentials'
        Write-Host '    send [worktree]       Send host files to container (preserves .git state)'
        Write-Host '    reset                 Nuke container + all volumes + rebuild from scratch'
        Write-Host ''
        Write-Host '  Examples:' -ForegroundColor White
        Write-Host '    .\scripts\devcontainer.ps1 claude                   # prompts for location'
        Write-Host '    .\scripts\devcontainer.ps1 claude query-engine-v3   # straight to worktree'
        Write-Host '    .\scripts\devcontainer.ps1 ppds                      # build + launch TUI'
        Write-Host '    .\scripts\devcontainer.ps1 ppds query-engine-v3      # TUI from worktree'
        Write-Host '    .\scripts\devcontainer.ps1 shell main               # shell at repo root'
        Write-Host '    .\scripts\devcontainer.ps1 push query-engine-v3      # push worktree branch via host'
        Write-Host '    .\scripts\devcontainer.ps1 send query-engine-v3      # send host worktree to container'
        Write-Host ''
    }
}
