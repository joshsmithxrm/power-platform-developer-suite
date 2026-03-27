<#
.SYNOPSIS
    Worktree dashboard and workflow tool.
.DESCRIPTION
    Shows the state of all worktrees in a git repo, organized by attention priority.
    Works in any git repo. Discovers repo-specific conventions (.workflow/state.json,
    scripts/devcontainer.ps1) and adapts.
.EXAMPLE
    dev                     # Dashboard
    dev cmt                 # cd to worktree matching prefix "cmt"
    dev cmt -tab            # Open in new terminal tab
    dev status cmt-parity   # Detailed status
    dev run cmt-parity      # Start pipeline
    dev pr cmt-parity       # Open PR in browser
    dev clean               # Remove merged worktrees
    dev up                  # Devcontainer: start
#>
param(
    [Parameter()]
    [string]$RepoRoot,

    [Parameter(Position = 0, ValueFromRemainingArguments)]
    [string[]]$Args_,

    [switch]$Tab,
    [switch]$DryRun,
    [switch]$Completions
)

$ErrorActionPreference = 'Stop'

# --- Constants ---

$ReservedSubcommands = @('status', 'run', 'pr', 'clean', 'help', 'up', 'shell', 'claude', 'down', 'sync', 'reset', 'push')
$DevcontainerCommands = @('up', 'shell', 'claude', 'down', 'sync', 'reset', 'push')

# --- Repo Root Resolution ---

function Resolve-RepoRoot {
    param([string]$Hint)

    if ($Hint -and (Test-Path $Hint)) { return $Hint }

    $root = git rev-parse --show-toplevel 2>$null
    if (-not $root) { return $null }

    # Resolve to main repo root when inside a worktree
    $commonDir = git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($commonDir -and (Split-Path $commonDir -Leaf) -eq '.git') {
        return Split-Path $commonDir
    }
    return $root
}

# --- Worktree Discovery ---

function Get-MainBranch {
    param([string]$Root)
    # Check for origin/main first, fall back to origin/master
    $ref = git -C $Root rev-parse --verify origin/main 2>$null
    if ($ref) { return 'origin/main' }
    $ref = git -C $Root rev-parse --verify origin/master 2>$null
    if ($ref) { return 'origin/master' }
    return 'origin/main'  # default
}

function Get-Worktrees {
    param([string]$Root)

    $wtDir = Join-Path $Root '.worktrees'
    if (-not (Test-Path $wtDir)) { return @() }

    $mainRef = Get-MainBranch -Root $Root
    $worktrees = @()
    foreach ($dir in (Get-ChildItem -Directory $wtDir)) {
        $path = $dir.FullName
        $name = $dir.Name

        # Git state
        $branch = git -C $path branch --show-current 2>$null
        if (-not $branch) { $branch = '(detached)' }

        $ahead = 0
        $aheadStr = git -C $path rev-list --count "$mainRef..HEAD" 2>$null
        if ($aheadStr) { $ahead = [int]$aheadStr }

        $dirtyCount = 0
        $dirtyLines = git -C $path status --porcelain 2>$null
        if ($dirtyLines) {
            $dirtyCount = @($dirtyLines).Count
        }

        # Workflow state
        $workflow = $null
        $statePath = Join-Path $path '.workflow' 'state.json'
        if (Test-Path $statePath) {
            try {
                $workflow = Get-Content $statePath -Raw | ConvertFrom-Json
            } catch {
                $workflow = $null
            }
        }

        # Pipeline state
        $pipeline = Get-PipelineState -WorktreePath $path

        $worktrees += [PSCustomObject]@{
            Name      = $name
            Path      = $path
            Branch    = $branch
            Ahead     = $ahead
            Dirty     = $dirtyCount
            Workflow  = $workflow
            Pipeline  = $pipeline
        }
    }

    return $worktrees
}

# --- Pipeline State ---

function Get-PipelineState {
    param([string]$WorktreePath)

    $lockPath = Join-Path $WorktreePath '.workflow' 'pipeline.lock'
    if (-not (Test-Path $lockPath)) {
        return @{ Running = $false }
    }

    $pidStr = (Get-Content $lockPath -Raw).Trim()
    $pid_ = 0
    if (-not [int]::TryParse($pidStr, [ref]$pid_)) {
        return @{ Running = $false; StaleLock = $true }
    }

    $proc = Get-Process -Id $pid_ -ErrorAction SilentlyContinue
    if (-not $proc) {
        return @{ Running = $false; StaleLock = $true; DeadPid = $pid_ }
    }

    # Parse last heartbeat from pipeline.log
    $logPath = Join-Path $WorktreePath '.workflow' 'pipeline.log'
    $stage = 'unknown'
    $elapsed = ''
    $activity = 'active'

    if (Test-Path $logPath) {
        $lines = Get-Content $logPath -Tail 50
        # Find last HEARTBEAT or START line
        for ($i = $lines.Count - 1; $i -ge 0; $i--) {
            if ($lines[$i] -match '\[(\S+)\] HEARTBEAT.*elapsed=(\S+).*activity=(\S+)') {
                $stage = $Matches[1]
                $elapsed = $Matches[2]
                $activity = $Matches[3]
                break
            }
            if ($lines[$i] -match '\[(\S+)\] START') {
                $stage = $Matches[1]
                break
            }
        }
    }

    return @{
        Running  = $true
        Pid      = $pid_
        Stage    = $stage
        Elapsed  = $elapsed
        Activity = $activity
    }
}

# --- Workflow Stage Derivation ---

function Get-WorkflowStage {
    param($Workflow)

    if (-not $Workflow) { return $null }

    if ($Workflow.pr -and $Workflow.pr.url) { return 'pr' }
    if ($Workflow.review -and $Workflow.review.passed) { return 'review' }
    if ($Workflow.qa -and ($Workflow.qa.PSObject.Properties.Count -gt 0)) { return 'qa' }
    if ($Workflow.verify -and ($Workflow.verify.PSObject.Properties.Count -gt 0)) { return 'verify' }
    if ($Workflow.gates -and $Workflow.gates.passed) { return 'gates' }
    if ($Workflow.implemented) { return 'implement' }
    if ($Workflow.spec) { return 'design' }

    return 'started'
}

function Get-MissingSteps {
    param($Workflow)

    if (-not $Workflow) { return @() }

    $missing = @()
    if (-not $Workflow.gates -or -not $Workflow.gates.passed) { $missing += 'gates' }
    if (-not $Workflow.verify -or $Workflow.verify.PSObject.Properties.Count -eq 0) { $missing += 'verify' }
    if (-not $Workflow.qa -or $Workflow.qa.PSObject.Properties.Count -eq 0) { $missing += 'qa' }
    if (-not $Workflow.review -or -not $Workflow.review.passed) { $missing += 'review' }
    return $missing
}

# --- Grouping ---

function Get-WorktreeGroup {
    param($Wt, [string[]]$MergedBranches)

    # In priority order per spec

    # 1. Needs attention: dirty + no pipeline, dead pipeline, incomplete + no pipeline
    if ($Wt.Pipeline.Running) {
        return 'in-progress'
    }

    if ($Wt.Pipeline.StaleLock -or $Wt.Pipeline.DeadPid) {
        return 'attention'
    }

    if ($Wt.Dirty -gt 0) {
        return 'attention'
    }

    # 3. PR open
    if ($Wt.Workflow -and $Wt.Workflow.pr -and $Wt.Workflow.pr.url) {
        return 'pr'
    }

    # 4. Ready to clean — branch merged into main
    if ($MergedBranches -contains $Wt.Branch) {
        return 'clean'
    }

    # 5. Design — spec but no gates/implemented
    if ($Wt.Workflow -and $Wt.Workflow.spec) {
        if ((-not $Wt.Workflow.gates -or -not $Wt.Workflow.gates.passed) -and -not $Wt.Workflow.implemented) {
            return 'design'
        }
    }

    # Incomplete workflow, no pipeline
    if ($Wt.Workflow) {
        $missing = Get-MissingSteps -Workflow $Wt.Workflow
        $stage = Get-WorkflowStage -Workflow $Wt.Workflow
        if ($missing.Count -gt 0 -and $stage -ne 'design') {
            return 'attention'
        }
    }

    return 'other'
}

# --- PR Status ---

function Get-PrStatus {
    param([string]$Root, $Worktrees)

    $prWorktrees = $Worktrees | Where-Object { $_.Workflow -and $_.Workflow.pr -and $_.Workflow.pr.url }
    if (-not $prWorktrees) { return @{} }

    # Check gh availability
    $ghAvailable = [bool](Get-Command gh -ErrorAction SilentlyContinue)

    # Read cache
    $cachePath = Join-Path $Root '.workflow' 'pr-cache.json'
    $cache = @{}
    $cacheAge = [int]::MaxValue

    if (Test-Path $cachePath) {
        try {
            $cacheData = Get-Content $cachePath -Raw | ConvertFrom-Json
            $cacheTime = [datetime]::ParseExact($cacheData._timestamp, 'o', [cultureinfo]::InvariantCulture)
            $cacheAge = ([datetime]::UtcNow - $cacheTime).TotalSeconds
            foreach ($prop in $cacheData.PSObject.Properties) {
                if ($prop.Name -ne '_timestamp') {
                    $cache[$prop.Name] = $prop.Value
                }
            }
        } catch {
            $cache = @{}
            $cacheAge = [int]::MaxValue
        }
    }

    # If cache is fresh (< 60s), return it
    if ($cacheAge -lt 60) { return $cache }

    # Fetch fresh if gh available
    if (-not $ghAvailable) {
        # Return basic info
        $result = @{}
        foreach ($wt in $prWorktrees) {
            $url = $wt.Workflow.pr.url
            if ($url -match '/pull/(\d+)$') {
                $result[$wt.Name] = @{ Number = [int]$Matches[1] }
            }
        }
        return $result
    }

    $result = @{}
    foreach ($wt in $prWorktrees) {
        $url = $wt.Workflow.pr.url
        if ($url -match '/pull/(\d+)$') {
            $prNum = [int]$Matches[1]
            try {
                $json = gh pr view $prNum --json number,state,reviewDecision,comments,statusCheckRollup 2>$null
                if ($json) {
                    $pr = $json | ConvertFrom-Json
                    $commentCount = 0
                    if ($pr.comments) { $commentCount = @($pr.comments).Count }

                    $checks = ''
                    if ($pr.statusCheckRollup) {
                        $total = @($pr.statusCheckRollup).Count
                        $passed = @($pr.statusCheckRollup | Where-Object { $_.conclusion -eq 'SUCCESS' }).Count
                        $checks = "$passed/$total passed"
                    }

                    $result[$wt.Name] = @{
                        Number   = $prNum
                        State    = $pr.state
                        Review   = $pr.reviewDecision
                        Comments = $commentCount
                        Checks   = $checks
                    }
                }
            } catch {
                $result[$wt.Name] = @{ Number = $prNum }
            }
        }
    }

    # Write cache
    $cacheDir = Join-Path $Root '.workflow'
    if (-not (Test-Path $cacheDir)) { New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null }
    $cacheObj = @{ _timestamp = [datetime]::UtcNow.ToString('o') }
    foreach ($key in $result.Keys) { $cacheObj[$key] = $result[$key] }
    $cacheObj | ConvertTo-Json -Depth 5 | Set-Content $cachePath -Encoding UTF8

    return $result
}

# --- Dashboard Rendering ---

function Format-WorktreeLine {
    param($Wt, [bool]$IsCurrent, $PrStatuses)

    $marker = if ($IsCurrent) { '→' } else { ' ' }

    $name = $Wt.Name
    if ($name.Length -gt 20) { $name = $name.Substring(0, 19) + '…' }
    $name = $name.PadRight(21)

    $ahead = if ($Wt.Ahead -gt 0) { "+$($Wt.Ahead)" } else { ' 0' }
    $ahead = $ahead.PadRight(5)

    $dirty = ''
    if ($Wt.Dirty -gt 0) {
        $dirty = "$($Wt.Dirty) dirty"
    }
    $dirty = $dirty.PadRight(10)

    $detail = ''

    # Pipeline in progress
    if ($Wt.Pipeline.Running) {
        $detail = "pipeline -> $($Wt.Pipeline.Stage)"
        if ($Wt.Pipeline.Elapsed) { $detail += " ($($Wt.Pipeline.Elapsed), $($Wt.Pipeline.Activity))" }
    }
    # Dead pipeline
    elseif ($Wt.Pipeline.StaleLock) {
        $detail = 'pipeline stalled'
    }
    # PR status
    elseif ($Wt.Workflow -and $Wt.Workflow.pr -and $Wt.Workflow.pr.url) {
        $url = $Wt.Workflow.pr.url
        $prNum = ''
        if ($url -match '/pull/(\d+)$') { $prNum = "#$($Matches[1])" }

        $prDetail = ''
        if ($PrStatuses -and $PrStatuses[$Wt.Name]) {
            $ps = $PrStatuses[$Wt.Name]
            if ($ps.Review -eq 'APPROVED') { $prDetail = 'approved' }
            elseif ($ps.Review -eq 'CHANGES_REQUESTED') { $prDetail = 'changes requested' }
            elseif ($ps.Comments -gt 0) { $prDetail = "$($ps.Comments) comments" }
            elseif ($ps.Checks) { $prDetail = "waiting on CI" }
            else { $prDetail = 'open' }
        }

        $detail = "$prNum  $prDetail"
    }
    # Workflow stage
    elseif ($Wt.Workflow) {
        $missing = Get-MissingSteps -Workflow $Wt.Workflow
        $stage = Get-WorkflowStage -Workflow $Wt.Workflow
        if ($missing.Count -gt 0 -and $stage -ne 'pr') {
            $detail = "needs $($missing -join ', ')"
        } elseif ($stage) {
            $detail = $stage
        }
    }

    return @{
        Marker = $marker
        Name   = $name
        Ahead  = $ahead
        Dirty  = $dirty
        Detail = $detail
        IsCurrent = $IsCurrent
        Group  = $null  # Computed in Show-Dashboard with real merged branches
        Raw    = $Wt
    }
}

function Show-Dashboard {
    param([string]$Root)

    $worktrees = Get-Worktrees -Root $Root
    $repoName = Split-Path $Root -Leaf

    if ($worktrees.Count -eq 0) {
        # Fallback: no worktrees, show basic branch info
        $branch = git -C $Root branch --show-current 2>$null
        if (-not $branch) { $branch = '(detached)' }
        $mainRef = Get-MainBranch -Root $Root
        $ahead = git -C $Root rev-list --count "$mainRef..HEAD" 2>$null
        $dirty = @(git -C $Root status --porcelain 2>$null).Count

        Write-Host ""
        Write-Host "  $repoName ($branch)" -ForegroundColor White
        if ($ahead -and [int]$ahead -gt 0) { Write-Host "  +$ahead ahead of main" -ForegroundColor DarkGray }
        if ($dirty -gt 0) { Write-Host "  $dirty dirty files" -ForegroundColor Red }
        Write-Host ""
        return
    }

    # Get merged branches for "ready to clean" detection
    $mergedRaw = git -C $Root branch --merged main 2>$null
    $mergedBranches = @()
    if ($mergedRaw) {
        $mergedBranches = @($mergedRaw | ForEach-Object { $_.Trim().TrimStart('* ') } | Where-Object { $_ -ne 'main' -and $_ -ne 'master' })
    }

    # PR statuses
    $prStatuses = Get-PrStatus -Root $Root -Worktrees $worktrees

    # Detect current worktree
    $currentPath = $PWD.Path -replace '\\', '/'
    $currentWt = $null
    foreach ($wt in $worktrees) {
        $wtPath = $wt.Path -replace '\\', '/'
        if ($currentPath -eq $wtPath -or $currentPath.StartsWith("$wtPath/")) {
            $currentWt = $wt.Name
        }
    }

    # Build lines with groups
    $lines = @()
    foreach ($wt in $worktrees) {
        $isCurrent = ($wt.Name -eq $currentWt)
        $line = Format-WorktreeLine -Wt $wt -IsCurrent $isCurrent -PrStatuses $prStatuses
        $line.Group = Get-WorktreeGroup -Wt $wt -MergedBranches $mergedBranches
        $lines += $line
    }

    # Group display order
    $groupOrder = @(
        @{ Key = 'attention';   Label = 'Needs attention' }
        @{ Key = 'in-progress'; Label = 'In progress' }
        @{ Key = 'pr';          Label = 'PRs open' }
        @{ Key = 'clean';       Label = 'Ready to clean' }
        @{ Key = 'design';      Label = 'Design' }
        @{ Key = 'other';       Label = 'Other' }
    )

    # Render
    Write-Host ""
    $header = "  $repoName dev"
    $countStr = "$($worktrees.Count) worktrees"
    $pad = 60 - $header.Length - $countStr.Length
    if ($pad -lt 1) { $pad = 1 }
    Write-Host "$header$(' ' * $pad)$countStr" -ForegroundColor White
    Write-Host "  $('─' * 58)" -ForegroundColor DarkGray

    $anyGroupShown = $false
    foreach ($group in $groupOrder) {
        $groupLines = @($lines | Where-Object { $_.Group -eq $group.Key })
        if ($groupLines.Count -eq 0) { continue }

        $anyGroupShown = $true
        Write-Host ""
        Write-Host "  $($group.Label):" -ForegroundColor Yellow

        foreach ($line in $groupLines) {
            $prefix = "  $($line.Marker) "

            if ($line.IsCurrent) {
                Write-Host $prefix -ForegroundColor Green -NoNewline
            } else {
                Write-Host $prefix -NoNewline
            }

            Write-Host $line.Name -ForegroundColor White -NoNewline
            Write-Host $line.Ahead -ForegroundColor DarkGray -NoNewline

            if ($line.Raw.Dirty -gt 0) {
                Write-Host $line.Dirty -ForegroundColor Red -NoNewline
            } else {
                Write-Host $line.Dirty -NoNewline
            }

            # Detail color depends on group
            $detailColor = switch ($line.Group) {
                'attention'   { 'DarkYellow' }
                'in-progress' { 'Cyan' }
                'pr'          {
                    if ($line.Detail -match 'approved') { 'Green' }
                    elseif ($line.Detail -match 'changes requested') { 'Red' }
                    else { 'DarkGray' }
                }
                default { 'DarkGray' }
            }

            Write-Host "  $($line.Detail)" -ForegroundColor $detailColor
        }
    }

    Write-Host ""
    Write-Host "  $('─' * 58)" -ForegroundColor DarkGray
    Write-Host "  dev <name>  jump  ·  dev status <name>  detail  ·  --help" -ForegroundColor DarkGray
    Write-Host ""
}

# --- Status Detail ---

function Show-WorktreeStatus {
    param([string]$Root, [string]$Name)

    # Resolve worktree
    if (-not $Name) {
        $currentPath = $PWD.Path -replace '\\', '/'
        $wtDir = (Join-Path $Root '.worktrees') -replace '\\', '/'
        if ($currentPath.StartsWith($wtDir)) {
            $rel = $currentPath.Substring($wtDir.Length + 1)
            $Name = $rel.Split('/')[0]
        }
        if (-not $Name) {
            Write-Host "  Not in a worktree. Specify a name: dev status <name>" -ForegroundColor Red
            return
        }
    }

    $wtPath = Resolve-WorktreePath -Root $Root -Prefix $Name
    if (-not $wtPath) { return }

    $name = Split-Path $wtPath -Leaf
    $branch = git -C $wtPath branch --show-current 2>$null
    $mainRef = Get-MainBranch -Root $Root
    $ahead = git -C $wtPath rev-list --count "$mainRef..HEAD" 2>$null
    $behind = git -C $wtPath rev-list --count "HEAD..$mainRef" 2>$null
    $dirtyLines = git -C $wtPath status --porcelain 2>$null
    $dirtyCount = if ($dirtyLines) { @($dirtyLines).Count } else { 0 }

    $workflow = $null
    $statePath = Join-Path $wtPath '.workflow' 'state.json'
    if (Test-Path $statePath) {
        try { $workflow = Get-Content $statePath -Raw | ConvertFrom-Json }
        catch { Write-Host "  Warning: failed to read $statePath`: $_" -ForegroundColor DarkYellow }
    }

    $pipeline = Get-PipelineState -WorktreePath $wtPath

    Write-Host ""
    Write-Host "  $name ($branch)" -ForegroundColor White
    Write-Host "  $('─' * 45)" -ForegroundColor DarkGray

    Write-Host "  Branch:    " -NoNewline -ForegroundColor DarkGray
    Write-Host "$branch" -ForegroundColor White
    $behindStr = if ($behind) { $behind } else { '0' }
    Write-Host "             +$ahead ahead, -$behindStr behind main" -ForegroundColor DarkGray

    if ($workflow -and $workflow.started) {
        Write-Host "  Started:   $($workflow.started)" -ForegroundColor DarkGray
    }
    if ($workflow -and $workflow.spec) {
        Write-Host "  Spec:      $($workflow.spec)" -ForegroundColor DarkGray
    }
    if ($workflow -and $workflow.issues) {
        Write-Host "  Issues:    $($workflow.issues -join ', ')" -ForegroundColor DarkGray
    }

    Write-Host "  Dirty:     " -NoNewline -ForegroundColor DarkGray
    if ($dirtyCount -gt 0) {
        Write-Host "$dirtyCount files" -ForegroundColor Red
    } else {
        Write-Host "clean" -ForegroundColor Green
    }

    # Workflow checklist
    if ($workflow) {
        Write-Host ""
        Write-Host "  Workflow:" -ForegroundColor Yellow

        # Gates
        if ($workflow.gates -and $workflow.gates.passed) {
            $ref = if ($workflow.gates.commit_ref) { $workflow.gates.commit_ref.Substring(0, 7) } else { '?' }
            $head = git -C $wtPath rev-parse --short HEAD 2>$null
            if (-not $head) { $head = '?' }
            $stale = if ($ref -ne $head) { ', STALE' } else { ', current' }
            Write-Host "    ✓ gates      passed (commit $ref$stale)" -ForegroundColor Green
        } else {
            Write-Host "    ✗ gates      not run" -ForegroundColor Red
        }

        # Verify
        if ($workflow.verify -and $workflow.verify.PSObject.Properties.Count -gt 0) {
            $surfaces = ($workflow.verify.PSObject.Properties | ForEach-Object { $_.Name }) -join ' · '
            Write-Host "    ✓ verify     $surfaces" -ForegroundColor Green
        } else {
            Write-Host "    ✗ verify     not completed" -ForegroundColor Red
        }

        # QA
        if ($workflow.qa -and $workflow.qa.PSObject.Properties.Count -gt 0) {
            $surfaces = ($workflow.qa.PSObject.Properties | ForEach-Object { $_.Name }) -join ' · '
            Write-Host "    ✓ qa         $surfaces" -ForegroundColor Green
        } else {
            Write-Host "    ✗ qa         not completed" -ForegroundColor Red
        }

        # Review
        if ($workflow.review -and $workflow.review.passed) {
            $findings = if ($workflow.review.findings) { "$($workflow.review.findings) findings" } else { '0 findings' }
            Write-Host "    ✓ review     passed ($findings)" -ForegroundColor Green
        } else {
            Write-Host "    ✗ review     not completed" -ForegroundColor Red
        }

        # PR
        if ($workflow.pr -and $workflow.pr.url) {
            $prNum = ''
            if ($workflow.pr.url -match '/pull/(\d+)$') { $prNum = "#$($Matches[1])" }
            Write-Host "    ✓ PR         $prNum" -ForegroundColor Green
        } else {
            Write-Host "    ✗ PR         not created" -ForegroundColor Red
        }

        # Next steps
        $missing = Get-MissingSteps -Workflow $workflow
        if ($missing.Count -gt 0 -and -not ($workflow.pr -and $workflow.pr.url)) {
            $steps = ($missing | ForEach-Object { "/$_" }) -join ' → '
            Write-Host "    Next: $steps → /pr" -ForegroundColor DarkYellow
        }
    } else {
        Write-Host ""
        Write-Host "  No workflow state tracked." -ForegroundColor DarkGray
    }

    # Pipeline
    Write-Host ""
    Write-Host "  Pipeline:  " -NoNewline -ForegroundColor DarkGray
    if ($pipeline.Running) {
        Write-Host "ACTIVE (PID $($pipeline.Pid))" -ForegroundColor Cyan
        Write-Host "    Stage:     $($pipeline.Stage) (elapsed: $($pipeline.Elapsed))" -ForegroundColor Cyan
        Write-Host "    Activity:  $($pipeline.Activity)" -ForegroundColor Cyan
    } elseif ($pipeline.StaleLock) {
        Write-Host "STALLED (PID $($pipeline.DeadPid) dead)" -ForegroundColor Red
    } else {
        Write-Host "not running" -ForegroundColor DarkGray
    }

    # PR detail
    if ($workflow -and $workflow.pr -and $workflow.pr.url) {
        $url = $workflow.pr.url
        $prNum = ''
        if ($url -match '/pull/(\d+)$') { $prNum = "#$($Matches[1])" }

        Write-Host "  PR:        $prNum" -ForegroundColor DarkGray
        Write-Host "    URL:     $url" -ForegroundColor DarkGray

        $ghAvailable = [bool](Get-Command gh -ErrorAction SilentlyContinue)
        if ($ghAvailable -and $prNum) {
            $num = $prNum.TrimStart('#')
            try {
                $json = gh pr view $num --json state,reviewDecision,comments,statusCheckRollup 2>$null
                if ($json) {
                    $pr = $json | ConvertFrom-Json
                    if ($pr.reviewDecision) { Write-Host "    Reviews: $($pr.reviewDecision.ToLower())" -ForegroundColor DarkGray }
                    if ($pr.statusCheckRollup) {
                        $total = @($pr.statusCheckRollup).Count
                        $passed = @($pr.statusCheckRollup | Where-Object { $_.conclusion -eq 'SUCCESS' }).Count
                        Write-Host "    Checks:  $passed/$total passed" -ForegroundColor DarkGray
                    }
                    if ($pr.comments) {
                        Write-Host "    Comments: $(@($pr.comments).Count)" -ForegroundColor DarkGray
                    }
                }
            } catch {
                Write-Host "    Warning: failed to fetch PR: $_" -ForegroundColor DarkYellow
            }
        }
    }

    Write-Host ""
}

# --- Navigation ---

function Resolve-WorktreePath {
    param([string]$Root, [string]$Prefix)

    $wtDir = Join-Path $Root '.worktrees'
    if (-not (Test-Path $wtDir)) {
        Write-Host "  No worktrees found in this repo." -ForegroundColor Red
        return $null
    }

    $dirs = @(Get-ChildItem -Directory $wtDir | Where-Object { $_.Name -like "$Prefix*" })

    if ($dirs.Count -eq 0) {
        $all = @(Get-ChildItem -Directory $wtDir | ForEach-Object { $_.Name })
        Write-Host "  No worktree matching '$Prefix'. Available:" -ForegroundColor Red
        foreach ($n in $all) { Write-Host "    $n" -ForegroundColor DarkGray }
        return $null
    }

    if ($dirs.Count -gt 1) {
        Write-Host "  Ambiguous — matches:" -ForegroundColor Red
        foreach ($d in $dirs) { Write-Host "    $($d.Name)" -ForegroundColor DarkGray }
        return $null
    }

    return $dirs[0].FullName
}

function Invoke-WorktreeJump {
    param([string]$Root, [string]$Prefix, [switch]$NewTab)

    $path = Resolve-WorktreePath -Root $Root -Prefix $Prefix
    if (-not $path) { return }

    $name = Split-Path $path -Leaf

    if ($NewTab) {
        if ($env:WT_SESSION) {
            & wt -w 0 nt --title $name -d $path
        } else {
            Start-Process pwsh -WorkingDirectory $path
        }
        Write-Host "  Opened $name in new terminal." -ForegroundColor Green
    } else {
        # Output path to pipeline — profile shim calls Set-Location
        return $path
    }
}

# --- Action Commands ---

function Invoke-Pipeline {
    param([string]$Root, [string]$Name)

    if (-not $Name) {
        Write-Host "  Usage: dev run <worktree-name>" -ForegroundColor Red
        return
    }

    $wtPath = Resolve-WorktreePath -Root $Root -Prefix $Name
    if (-not $wtPath) { return }

    $pipelineScript = Join-Path $Root 'scripts' 'pipeline.py'
    if (-not (Test-Path $pipelineScript)) {
        Write-Host "  No pipeline script found in this repo." -ForegroundColor Red
        return
    }

    # Check for existing pipeline
    $lockPath = Join-Path $wtPath '.workflow' 'pipeline.lock'
    if (Test-Path $lockPath) {
        $pidStr = (Get-Content $lockPath -Raw).Trim()
        $pid_ = 0
        if ([int]::TryParse($pidStr, [ref]$pid_)) {
            $proc = Get-Process -Id $pid_ -ErrorAction SilentlyContinue
            if ($proc) {
                Write-Host "  Pipeline already running (PID $pid_). Use 'dev status $(Split-Path $wtPath -Leaf)' to monitor." -ForegroundColor Red
                return
            }
        }
    }

    # Get spec from state.json
    $spec = $null
    $statePath = Join-Path $wtPath '.workflow' 'state.json'
    if (Test-Path $statePath) {
        try {
            $state = Get-Content $statePath -Raw | ConvertFrom-Json
            $spec = $state.spec
        } catch { Write-Host "  Warning: failed to read state: $_" -ForegroundColor DarkYellow }
    }

    $args_ = @($pipelineScript, '--worktree', $wtPath)
    if ($spec) { $args_ += @('--spec', $spec) }

    $proc = Start-Process python -ArgumentList $args_ -PassThru -WorkingDirectory $Root
    $procId = $proc.Id
    $proc.Dispose()
    Write-Host "  Pipeline started (PID $procId)." -ForegroundColor Green
    Write-Host "  Monitor: dev status $(Split-Path $wtPath -Leaf)" -ForegroundColor DarkGray
}

function Open-PullRequest {
    param([string]$Root, [string]$Name)

    if (-not $Name) {
        Write-Host "  Usage: dev pr <worktree-name>" -ForegroundColor Red
        return
    }

    $wtPath = Resolve-WorktreePath -Root $Root -Prefix $Name
    if (-not $wtPath) { return }

    $statePath = Join-Path $wtPath '.workflow' 'state.json'
    if (-not (Test-Path $statePath)) {
        Write-Host "  No PR found. Run the workflow to completion first." -ForegroundColor Red
        return
    }

    try {
        $state = Get-Content $statePath -Raw | ConvertFrom-Json
        if ($state.pr -and $state.pr.url) {
            Start-Process $state.pr.url
            Write-Host "  Opened $($state.pr.url)" -ForegroundColor Green
        } else {
            Write-Host "  No PR found. Run the workflow to completion first." -ForegroundColor Red
        }
    } catch {
        Write-Host "  Failed to read workflow state." -ForegroundColor Red
    }
}

function Invoke-Clean {
    param([string]$Root, [switch]$DryRun_)

    $wtDir = Join-Path $Root '.worktrees'
    if (-not (Test-Path $wtDir)) {
        Write-Host "  No worktrees found." -ForegroundColor DarkGray
        return
    }

    $mergedRaw = git -C $Root branch --merged main 2>$null
    $mergedBranches = @()
    if ($mergedRaw) {
        $mergedBranches = @($mergedRaw | ForEach-Object { $_.Trim().TrimStart('* ') } | Where-Object { $_ -ne 'main' -and $_ -ne 'master' })
    }

    $toClean = @()
    foreach ($dir in (Get-ChildItem -Directory $wtDir)) {
        $branch = git -C $dir.FullName branch --show-current 2>$null
        if ($branch -and $mergedBranches -contains $branch) {
            $toClean += @{ Name = $dir.Name; Path = $dir.FullName; Branch = $branch }
        }
    }

    if ($toClean.Count -eq 0) {
        Write-Host "  No merged worktrees found." -ForegroundColor DarkGray
        return
    }

    Write-Host ""
    Write-Host "  Found $($toClean.Count) worktrees merged into main:" -ForegroundColor Yellow
    foreach ($wt in $toClean) {
        Write-Host "    $($wt.Name) ($($wt.Branch))" -ForegroundColor White
    }
    Write-Host ""

    if ($DryRun_) {
        Write-Host "  Dry run — no changes made." -ForegroundColor DarkGray
        return
    }

    $confirm = Read-Host "  Remove all? [y/N]"
    if ($confirm -ne 'y' -and $confirm -ne 'Y') {
        Write-Host "  Cancelled." -ForegroundColor DarkGray
        return
    }

    $removed = 0
    foreach ($wt in $toClean) {
        git -C $Root worktree remove $wt.Path 2>$null
        git -C $Root branch -d $wt.Branch 2>$null
        $removed++
    }

    git -C $Root remote prune origin 2>$null

    Write-Host "  Removed $removed worktrees, pruned remote branches." -ForegroundColor Green
}

# --- Completions ---

function Get-Completions {
    param([string]$Root)

    # Subcommands
    $ReservedSubcommands | ForEach-Object { $_ }

    # Worktree names
    $wtDir = Join-Path $Root '.worktrees'
    if (Test-Path $wtDir) {
        Get-ChildItem -Directory $wtDir | ForEach-Object { $_.Name }
    }
}

# --- Help ---

function Show-Help {
    Write-Host ""
    Write-Host "  dev — worktree dashboard and workflow tool" -ForegroundColor White
    Write-Host ""
    Write-Host "  Usage:" -ForegroundColor Yellow
    Write-Host "    dev                     Dashboard (all worktrees)" -ForegroundColor DarkGray
    Write-Host "    dev <name>              cd to worktree (prefix match)" -ForegroundColor DarkGray
    Write-Host "    dev <name> -tab         Open in new terminal tab" -ForegroundColor DarkGray
    Write-Host "    dev status [name]       Detailed status" -ForegroundColor DarkGray
    Write-Host "    dev run <name>          Start pipeline" -ForegroundColor DarkGray
    Write-Host "    dev pr <name>           Open PR in browser" -ForegroundColor DarkGray
    Write-Host "    dev clean [--dry-run]   Remove merged worktrees" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "  Container (requires scripts/devcontainer.ps1):" -ForegroundColor Yellow
    Write-Host "    dev up/down/shell/claude/sync/reset/push" -ForegroundColor DarkGray
    Write-Host ""
}

# --- Main Dispatch ---

$root = Resolve-RepoRoot -Hint $RepoRoot
if (-not $root) {
    Write-Host "  Not in a git repository." -ForegroundColor Red
    return
}

# Handle completions
if ($Completions) {
    Get-Completions -Root $root
    return
}

# Parse args
$command = if ($Args_ -and $Args_.Count -gt 0) { $Args_[0] } else { $null }
$restArgs = if ($Args_ -and $Args_.Count -gt 1) { $Args_[1..($Args_.Count - 1)] } else { @() }

# No args — dashboard
if (-not $command) {
    Show-Dashboard -Root $root
    return
}

# Check --help / -h
if ($command -eq '--help' -or $command -eq '-h') {
    Show-Help
    return
}

# Reserved subcommands
switch ($command) {
    'help'   { Show-Help; return }
    'status' {
        $target = if ($restArgs.Count -gt 0) { $restArgs[0] } else { $null }
        Show-WorktreeStatus -Root $root -Name $target
        return
    }
    'run' {
        $target = if ($restArgs.Count -gt 0) { $restArgs[0] } else { $null }
        Invoke-Pipeline -Root $root -Name $target
        return
    }
    'pr' {
        $target = if ($restArgs.Count -gt 0) { $restArgs[0] } else { $null }
        Open-PullRequest -Root $root -Name $target
        return
    }
    'clean' {
        Invoke-Clean -Root $root -DryRun_:$DryRun
        return
    }
}

# Devcontainer delegation
if ($DevcontainerCommands -contains $command) {
    $dcScript = Join-Path $root 'scripts' 'devcontainer.ps1'
    if (Test-Path $dcScript) {
        & $dcScript @Args_
    } else {
        Write-Host "  No devcontainer script found in this repo." -ForegroundColor Red
    }
    return
}

# Worktree navigation (anything not a subcommand)
$result = Invoke-WorktreeJump -Root $root -Prefix $command -NewTab:$Tab
if ($result) {
    # Output path for profile shim to Set-Location
    return $result
}
