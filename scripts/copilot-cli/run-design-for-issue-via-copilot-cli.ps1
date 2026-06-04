<# 
This script is intended to allow for design for an issue to start via Copilot CLI.
It will create a new worktree and branch for the issue, initialize workflow state, and
then open a new Copilot CLI session in that worktree with a prompt to do design. It 
should be run from the main repository root, and that root must be on the 'main' branch.

Example usage:
& .\run-design-for-issue-via-copilot-cli.ps1 -IssueDerivedName mda-commands -Issues 1165 -WorkType enhancement

Then, once you approve design and spec in Copilot CLI, to do implementation in claude:

open a terminal to the worktree folder and run:
    python scripts/pipeline.py --worktree . --plan .plans/2026-06-02-model-driven-apps.md --from implement

    I tried this and it seemed to work ok, though I did see some warnings in the terminal:
    [06:24:15] pipeline: RESUME plan=.plans\2026-06-02-model-driven-apps.md name=model-driven-apps branch=model-driven-apps from_stage=implement
    [06:24:16] implement: START ceiling=7200s mode=interactive model=sonnet
    WARN claude --bg banner parse failed; stdout head: 'backgrounded Â· c379434b Â· implement\n  claude agents             list sessions\n  claude attach c379434b    open in this terminal\n  claude logs c379434b      show recent output\n  claude stop c379434b '
    [06:25:18] implement: HEARTBEAT elapsed=61s output_bytes=161707 git_changes=0 commits=1 children=0 activity=active short=c379434b
    [06:26:18] implement: HEARTBEAT elapsed=121s output_bytes=475996 git_changes=0 commits=1 children=0 activity=active short=c379434b

Manual cutover to Claude:
please give me a robust prompt that will let me pickup from here in claude. what we've done, where we're at, and how it should resume


#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9]+(?:-[a-z0-9]+)*$')]
    [string]$IssueDerivedName,

    [Parameter(Mandatory = $true)]
    [string[]]$Issues,

    [ValidateSet('bug', 'enhancement', 'docs', 'feature', 'refactor', 'performance')]
    [string]$WorkType,

    [string]$RepoRoot = (Join-Path $PSScriptRoot '../..' -Resolve)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Normalize-PathString {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Get-GitOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $gitArgs = @('-C', $WorkingDirectory) + $Arguments
    $output = & git @gitArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed.`n$output"
    }

    if ($output -is [array]) {
        return ($output -join [Environment]::NewLine).Trim()
    }

    return ([string]$output).Trim()
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$repoRootResolved = Normalize-PathString $RepoRoot

if (-not (Test-Path -LiteralPath $repoRootResolved -PathType Container)) {
    throw "RepoRoot does not exist: $repoRootResolved"
}

$actualTopLevel = Normalize-PathString (Get-GitOutput -Arguments @('rev-parse', '--show-toplevel') -WorkingDirectory $repoRootResolved)
if ($actualTopLevel -ne $repoRootResolved) {
    throw "RepoRoot must point to the main repository root.`nProvided: $repoRootResolved`nDetected: $actualTopLevel"
}

$currentBranch = Get-GitOutput -Arguments @('rev-parse', '--abbrev-ref', 'HEAD') -WorkingDirectory $repoRootResolved
if ($currentBranch -ne 'main') {
    throw "RepoRoot must be on branch 'main'. Current branch: '$currentBranch'."
}

$worktreeList = Get-GitOutput -Arguments @('worktree', 'list', '--porcelain') -WorkingDirectory $repoRootResolved
$primaryWorktree = $null

foreach ($line in ($worktreeList -split "`r?`n")) {
    if ($line.StartsWith('worktree ')) {
        $primaryWorktree = Normalize-PathString $line.Substring('worktree '.Length).Trim()
        break
    }
}

if (-not $primaryWorktree) {
    throw "Could not determine the primary worktree from 'git worktree list --porcelain'."
}

if ($primaryWorktree -ne $repoRootResolved) {
    throw "RepoRoot is not the primary worktree.`nRepoRoot: $repoRootResolved`nPrimary:  $primaryWorktree"
}

$python = (Get-Command python -ErrorAction Stop).Source | Select-Object -First 1
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source | Select-Object -First 1

$branch = "feat/$IssueDerivedName"
$worktreePath = Join-Path (Join-Path $repoRootResolved '.worktrees') $IssueDerivedName

Write-Host "Creating worktree '$IssueDerivedName' on branch '$branch'..."
Push-Location $repoRootResolved
try {
    Invoke-Checked -FilePath $python -Arguments @('scripts/worktree-create.py', '--name', $IssueDerivedName) -WorkingDirectory $repoRootResolved
} finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $worktreePath -PathType Container)) {
    throw "Expected worktree path was not created: $worktreePath"
}

Write-Host 'Initializing workflow state...'
Push-Location $repoRootResolved
try {
    Invoke-Checked -FilePath $python -Arguments @('scripts/workflow-state.py', '--worktree-path', $worktreePath, 'init', $branch) -WorkingDirectory $repoRootResolved
    Invoke-Checked -FilePath $python -Arguments @('scripts/workflow-state.py', '--worktree-path', $worktreePath, 'set', 'phase', 'starting') -WorkingDirectory $repoRootResolved
    Invoke-Checked -FilePath $python -Arguments (@('scripts/workflow-state.py', '--worktree-path', $worktreePath, 'append', 'issues') + $Issues) -WorkingDirectory $repoRootResolved
    Invoke-Checked -FilePath $python -Arguments @('scripts/workflow-state.py', '--worktree-path', $worktreePath, 'set', 'work_type', $WorkType) -WorkingDirectory $repoRootResolved
} finally {
    Pop-Location
}

Write-Host "Opening new Copilot CLI session in $worktreePath ..."
$prompt = "I want to do design only for #$($Issues -join ', '). This current repo is my fork, the real repo is joshsmithxrm/power-platform-developer-suite. Background: I am running this session in Copilot CLI but the workflows/skills/etc were designed to work specifically with claude. Please make sure to read the constitution and any other files that claude would read."
$copilotCommand = "copilot -C '$($worktreePath.Replace("'", "''"))' --model gpt-5.4 --name '$($branch.Replace("'", "''"))' -i '$($prompt.Replace("'", "''"))'"
try {
    Start-Process wt -ArgumentList @('new-tab', '-d', $worktreePath, '--', $pwsh, '-NoExit', '-Command', $copilotCommand) -ErrorAction Stop | Out-Null
} catch {
    throw "Failed to open Windows Terminal tab (wt). $($_.Exception.Message)"
}

Write-Host 'Done.'
Write-Host "RepoRoot:  $repoRootResolved"
Write-Host "Worktree:  $worktreePath"
Write-Host "Branch:    $branch"
Write-Host "Issues:    $($Issues -join ', ')"