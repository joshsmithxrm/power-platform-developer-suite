<#
.SYNOPSIS
    Profile integration for dev command. Source this file or copy to your PowerShell profile.
.DESCRIPTION
    Provides the dev() function and tab completion. The function resolves the git repo root,
    calls scripts/dev.ps1 (from dotfiles or repo fallback), and handles Set-Location for
    worktree navigation.
.EXAMPLE
    # Add to $PROFILE:
    . C:\VS\ppdsw\ppds\scripts\dev-profile.ps1
#>

#region dev (Worktree Dashboard + Workflow Tool)
function _Resolve-DevScriptContext {
    $root = git rev-parse --show-toplevel 2>$null
    if ($root) {
        # Resolve to main repo root when inside a worktree
        $commonDir = git -C $root rev-parse --path-format=absolute --git-common-dir 2>$null
        if ($commonDir -and (Split-Path $commonDir -Leaf) -eq '.git') {
            $root = Split-Path $commonDir
        }
    }

    if (-not $root) {
        return @{ Error = "Not in a git repository." }
    }

    # Find dev.ps1: dotfiles first, then repo fallback
    $devScript = $null
    $dotfilesPath = Join-Path $env:USERPROFILE 'dotfiles\scripts\dev.ps1'
    $repoPath = Join-Path $root 'scripts\dev.ps1'

    if (Test-Path $dotfilesPath) { $devScript = $dotfilesPath }
    elseif (Test-Path $repoPath) { $devScript = $repoPath }

    if (-not $devScript) {
        return @{ Error = "dev.ps1 not found (checked dotfiles and repo)" }
    }

    return @{ Root = $root; DevScript = $devScript }
}

function dev {
    $context = _Resolve-DevScriptContext
    if ($context.Error) {
        Write-Host "  $($context.Error)" -ForegroundColor Red
        return
    }

    # Call dev.ps1 — capture pipeline output for navigation (Set-Location)
    $result = & $context.DevScript -RepoRoot $context.Root @args
    if ($result -and (Test-Path $result -ErrorAction SilentlyContinue)) {
        Set-Location $result
    }
}

Register-ArgumentCompleter -CommandName dev -ScriptBlock {
    param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)

    $context = _Resolve-DevScriptContext
    if ($context.Error) { return }

    & $context.DevScript -RepoRoot $context.Root -Completions 2>$null |
        Where-Object { $_ -like "$wordToComplete*" } |
        ForEach-Object {
            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
        }
}
#endregion
