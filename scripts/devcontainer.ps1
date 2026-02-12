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
    .\scripts\devcontainer.ps1 rebase                     # rebase onto origin/main (prompts for worktree)
    .\scripts\devcontainer.ps1 rebase query-engine-v3    # rebase worktree onto origin/main
    .\scripts\devcontainer.ps1 send                      # send host files to container (prompts for worktree)
    .\scripts\devcontainer.ps1 send query-engine-v3      # send host worktree to container worktree
    .\scripts\devcontainer.ps1 sync                      # sync origin git state into container
    .\scripts\devcontainer.ps1 reset                    # nuke everything, full clean rebuild
#>

param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'shell', 'claude', 'ppds', 'down', 'status', 'send', 'push', 'rebase', 'sync', 'reset', 'help')]
    [string]$Command = 'help',

    [Parameter(Position = 1)]
    [string]$Target,

    [switch]$NoPlanMode
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

function Sync-ContainerFromOrigin {
    # Sync origin's git state into the container via bundle (container has no credentials)
    $containerId = (docker ps -q --filter "label=devcontainer.local_folder=$WorkspaceFolder").Trim()
    if (-not $containerId) {
        Write-Err 'Container is not running.'
        return
    }

    # Host fetches latest from origin
    Write-Step 'Fetching from origin...'
    git -C $WorkspaceFolder fetch origin --prune
    if ($LASTEXITCODE -ne 0) {
        Write-Err 'Failed to fetch from origin.'
        return
    }

    # Collect all origin refs (exclude HEAD symref — causes duplicate update errors)
    $originRefs = @(git -C $WorkspaceFolder for-each-ref --format='%(refname)' refs/remotes/origin/) | Where-Object { $_ -ne 'refs/remotes/origin/HEAD' }
    if ($originRefs.Count -eq 0) {
        Write-Err 'No remote refs found.'
        return
    }

    # Bundle all origin refs
    Write-Step "Bundling $($originRefs.Count) refs from origin..."
    $bundlePath = Join-Path $env:TEMP 'ppds-sync.bundle'
    git -C $WorkspaceFolder bundle create $bundlePath @originRefs
    if ($LASTEXITCODE -ne 0) {
        Write-Err 'Failed to create bundle.'
        Remove-Item $bundlePath -ErrorAction SilentlyContinue
        return
    }

    # Send bundle to container
    docker cp $bundlePath "${containerId}:/tmp/sync.bundle" | Out-Null
    Remove-Item $bundlePath -ErrorAction SilentlyContinue

    # Container: clear stale origin refs and fetch fresh ones from bundle
    # Clearing first ensures pruning — refs deleted on origin get removed
    # Must delete HEAD symref explicitly — update-ref --stdin doesn't handle symrefs,
    # and an existing HEAD→main symref causes "multiple updates" error on fetch
    devcontainer exec --workspace-folder $WorkspaceFolder bash -c "git symbolic-ref --delete refs/remotes/origin/HEAD 2>/dev/null; git for-each-ref --format='delete %(refname)' refs/remotes/origin/ | git update-ref --stdin" 2>$null
    devcontainer exec --workspace-folder $WorkspaceFolder bash -c "git fetch /tmp/sync.bundle 'refs/remotes/origin/*:refs/remotes/origin/*'" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Err 'Failed to fetch from bundle in container.'
        docker exec -u 0 $containerId rm -f /tmp/sync.bundle
        return
    }
    $refCount = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c "git for-each-ref --format='%(refname)' refs/remotes/origin/ | wc -l" 2>$null).Trim()
    Write-Ok "Updated remote tracking refs ($refCount refs synced)."

    # Fast-forward main if the main checkout is clean and on main
    $currentBranch = (devcontainer exec --workspace-folder $WorkspaceFolder git branch --show-current 2>$null).Trim()
    if ($currentBranch -ne 'main') {
        Write-Step 'Main checkout is not on main — skipping fast-forward.'
    }
    else {
        devcontainer exec --workspace-folder $WorkspaceFolder bash -c "git diff --quiet && git diff --cached --quiet" 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Step 'Main checkout has uncommitted changes — skipping fast-forward.'
        }
        else {
            $oldSha = (devcontainer exec --workspace-folder $WorkspaceFolder git rev-parse --short HEAD 2>$null).Trim()
            devcontainer exec --workspace-folder $WorkspaceFolder git merge --ff-only origin/main 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Err 'main has diverged from origin (local commits?) — resolve manually.'
            }
            else {
                $newSha = (devcontainer exec --workspace-folder $WorkspaceFolder git rev-parse --short HEAD 2>$null).Trim()
                if ($oldSha -eq $newSha) {
                    Write-Ok 'main is up-to-date.'
                }
                else {
                    Write-Ok "Fast-forwarded main ($oldSha -> $newSha)."
                }
            }
        }
    }

    # Report worktree status
    $worktrees = @(Get-Worktrees)
    if ($worktrees.Count -gt 0) {
        Write-Host ''
        Write-Host '  Worktree status:' -ForegroundColor Yellow
        foreach ($wt in $worktrees) {
            $branch = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd .worktrees/$wt && git branch --show-current" 2>$null).Trim()
            if (-not $branch) {
                $padWt = $wt.PadRight(20)
                Write-Host "    $padWt (detached HEAD)" -ForegroundColor DarkYellow
                continue
            }
            devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd .worktrees/$wt && git rev-parse origin/$branch" 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) {
                $padWt = $wt.PadRight(20)
                $padBranch = $branch.PadRight(25)
                Write-Host "    $padWt $padBranch " -ForegroundColor White -NoNewline
                Write-Host "branch deleted on origin" -ForegroundColor Red
                continue
            }
            $ahead = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd .worktrees/$wt && git rev-list --count origin/${branch}..HEAD" 2>$null).Trim()
            $behind = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c "cd .worktrees/$wt && git rev-list --count HEAD..origin/${branch}" 2>$null).Trim()
            $padWt = $wt.PadRight(20)
            $padBranch = $branch.PadRight(25)
            Write-Host "    $padWt $padBranch ${ahead} ahead, ${behind} behind" -ForegroundColor White
        }
        Write-Host ''
    }

    # Clean up (bundle was docker cp'd as root, so remove as root via -u 0)
    docker exec -u 0 $containerId rm -f /tmp/sync.bundle
}

switch ($Command) {
    'up' {
        $freshClone = -not (docker volume ls -q --filter "name=^${WorkspaceVolume}$")
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
            # Sync origin state into container (skip on fresh clone — already current)
            if (-not $freshClone) {
                Sync-ContainerFromOrigin
            }

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

            # Repair container worktrees — fix .git files that have Windows host paths
            $containerWsFolder = (devcontainer exec --workspace-folder $WorkspaceFolder sh -c 'pwd' 2>$null).Trim()
            $containerWorktrees = devcontainer exec --workspace-folder $WorkspaceFolder sh -c 'ls -d .worktrees/*/ 2>/dev/null' 2>$null
            if ($containerWorktrees) {
                $containerWorktrees -split "`n" | ForEach-Object { ($_.Trim() -replace '/$','') } | Where-Object { $_ } | ForEach-Object {
                    $wtPath = $_
                    $wtName = $wtPath -replace '^\.worktrees/',''
                    $gitdir = devcontainer exec --workspace-folder $WorkspaceFolder sh -c "cat $wtPath/.git 2>/dev/null" 2>$null
                    if ($gitdir -and $gitdir -match '[A-Z]:[\\/]') {
                        Write-Step "Repairing container worktree '$wtName' .git file (was Windows host path)..."
                        devcontainer exec --workspace-folder $WorkspaceFolder sh -c "echo 'gitdir: $containerWsFolder/.git/worktrees/$wtName' > $wtPath/.git"
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
        devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && dotnet build src/PPDS.Cli -f net10.0' -- $workdir
        if ($LASTEXITCODE -ne 0) {
            Write-Err 'Build failed.'
            exit 1
        }

        Write-Step 'Launching TUI...'
        devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && PPDS_FORCE_TUI=1 dotnet src/PPDS.Cli/bin/Debug/net10.0/ppds.dll' -- $workdir
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
        $branch = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git branch --show-current' -- $workdir).Trim()
        if (-not $branch) {
            Write-Err "Could not determine branch (detached HEAD?)."
            exit 1
        }
        $localSha = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git rev-parse HEAD' -- $workdir).Trim()

        # Compare against actual remote state (not local tracking refs which may be stale)
        $remoteSha = (git -C $WorkspaceFolder ls-remote origin "refs/heads/${branch}" 2>$null)
        if ($remoteSha) { $remoteSha = ($remoteSha -split '\s')[0] }

        if ($localSha -eq $remoteSha) {
            Write-Ok "Branch '$branch' is already up-to-date on origin."
            return
        }

        if (-not $remoteSha) {
            $ahead = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git rev-list --count HEAD' -- $workdir).Trim()
            Write-Step "New branch '$branch' ($ahead commit(s))."
        }
        else {
            $ahead = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git rev-list --count "$2..HEAD" 2>/dev/null' -- $workdir $remoteSha).Trim()
            if (-not $ahead -or $ahead -eq '0') {
                Write-Ok "Branch '$branch' is already up-to-date on origin."
                return
            }
            Write-Step "Branch '$branch' is $ahead commit(s) ahead of origin."
        }

        # Check if force push is needed (e.g., after rebasing onto main)
        $forceNeeded = $false
        if ($remoteSha) {
            $hasRemote = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git cat-file -t "$2" 2>/dev/null && echo yes || echo no' -- $workdir $remoteSha).Trim()
            if ($hasRemote -eq 'yes') {
                $isFF = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git merge-base --is-ancestor "$2" HEAD 2>/dev/null && echo yes || echo no' -- $workdir $remoteSha).Trim()
            }
            else {
                $isFF = 'no'
            }
            if ($isFF -eq 'no') {
                Write-Step "Branch has been rebased — will force push with lease."
                $forceNeeded = $true
            }
        }

        # Create git bundle in container
        Write-Step 'Bundling commits...'
        devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git bundle create /tmp/push.bundle "$2"' -- $workdir $branch | Out-Null
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
        if ($forceNeeded) {
            Write-Step 'Force pushing to origin (with lease)...'
            git -C $WorkspaceFolder push --force-with-lease="refs/heads/${branch}:${remoteSha}" origin "FETCH_HEAD:refs/heads/${branch}"
        }
        else {
            Write-Step 'Pushing to origin...'
            git -C $WorkspaceFolder push origin "FETCH_HEAD:refs/heads/${branch}"
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Err 'Push failed. Run rebase to sync with origin first.'
            Remove-Item $tempBundle -ErrorAction SilentlyContinue
            exit 1
        }

        # Clean up
        Remove-Item $tempBundle -ErrorAction SilentlyContinue
        devcontainer exec --workspace-folder $WorkspaceFolder rm -f /tmp/push.bundle

        # Update container's remote tracking ref so git status shows up-to-date
        Write-Step 'Updating container remote refs...'
        devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git fetch origin "$2" 2>/dev/null || git update-ref "refs/remotes/origin/$2" HEAD' -- $workdir $branch

        $verb = if ($forceNeeded) { 'Force pushed' } else { 'Pushed' }
        Write-Ok "$verb '$branch' to origin ($ahead commit(s))."
    }

    'rebase' {
        # Rebase feature branch onto origin/main (syncs origin refs first)
        Ensure-ContainerRunning
        $subdir = Select-WorkingDirectory -Target $Target
        $workdir = if ($subdir) { $subdir } else { '.' }

        # Get current branch
        $branch = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git branch --show-current' -- $workdir).Trim()
        if (-not $branch) {
            Write-Err "Could not determine branch (detached HEAD?)."
            exit 1
        }
        if ($branch -eq 'main') {
            Write-Err "Already on main — use 'sync' to fast-forward main instead."
            exit 1
        }

        # Sync origin refs so origin/main is current
        Sync-ContainerFromOrigin

        # Rebase onto origin/main
        Write-Step "Rebasing '$branch' onto origin/main..."
        devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git rebase origin/main' -- $workdir
        if ($LASTEXITCODE -ne 0) {
            Write-Err "Rebase has conflicts."
            Write-Step 'Launching Claude Code to help resolve conflicts...'
            $planInstruction = if ($NoPlanMode) {
                'Resolve all conflicts directly.'
            } else {
                'Start by using plan mode to analyze the conflicts and present a resolution strategy before making changes.'
            }
            $safeBranch = $branch -replace '[^a-zA-Z0-9_\-/.]', ''
            $conflictPrompt = "A git rebase of branch '${safeBranch}' onto origin/main has resulted in merge conflicts. Run git status to see conflicted files. Analyze each conflict, resolve them, git add the resolved files, and run git rebase --continue. If there are multiple conflicting commits, continue resolving until the rebase is complete. ${planInstruction}"
            devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && claude --dangerously-skip-permissions -p "$2"' -- $workdir $conflictPrompt
            Write-Step "Claude session ended. Verify rebase is complete, then 'push' when ready."
            exit 1
        }

        $newBase = (devcontainer exec --workspace-folder $WorkspaceFolder bash -c 'cd "$1" && git log --oneline -1 origin/main' -- $workdir).Trim()
        Write-Ok "Rebased '$branch' onto origin/main ($newBase)."
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

    'sync' {
        Ensure-ContainerRunning
        Sync-ContainerFromOrigin
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
        Write-Host '    rebase [worktree]     Rebase feature branch onto origin/main (syncs first)'
        Write-Host '    send [worktree]       Send host files to container (preserves .git state)'
        Write-Host '    sync                  Sync origin git state into container (auto-runs on up)'
        Write-Host '    reset                 Nuke container + all volumes + rebuild from scratch'
        Write-Host ''
        Write-Host '  Options:' -ForegroundColor White
        Write-Host '    -NoPlanMode           Skip plan mode when Claude resolves rebase conflicts'
        Write-Host ''
        Write-Host '  Examples:' -ForegroundColor White
        Write-Host '    .\scripts\devcontainer.ps1 claude                   # prompts for location'
        Write-Host '    .\scripts\devcontainer.ps1 claude query-engine-v3   # straight to worktree'
        Write-Host '    .\scripts\devcontainer.ps1 ppds                      # build + launch TUI'
        Write-Host '    .\scripts\devcontainer.ps1 ppds query-engine-v3      # TUI from worktree'
        Write-Host '    .\scripts\devcontainer.ps1 shell main               # shell at repo root'
        Write-Host '    .\scripts\devcontainer.ps1 push query-engine-v3      # push worktree branch via host'
        Write-Host '    .\scripts\devcontainer.ps1 rebase query-engine-v3    # rebase worktree onto origin/main'
        Write-Host '    .\scripts\devcontainer.ps1 send query-engine-v3      # send host worktree to container'
        Write-Host '    .\scripts\devcontainer.ps1 sync                      # sync origin refs into container'
        Write-Host ''
    }
}
