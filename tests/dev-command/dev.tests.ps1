BeforeAll {
    $script:DevScript = Join-Path $PSScriptRoot '..\..\scripts\dev.ps1'

    function New-TestRepo {
        <#
        .SYNOPSIS
            Creates a temp git repo with optional worktrees and workflow state for testing.
        #>
        param(
            [string[]]$Worktrees,
            [hashtable]$WorkflowStates  # key = worktree name, value = state hashtable
        )

        $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "dev-test-$(Get-Random)"
        New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

        # Init main repo
        Push-Location $tmpDir
        git init --initial-branch main 2>$null | Out-Null
        git commit --allow-empty -m 'init' 2>$null | Out-Null
        Pop-Location

        # Create worktrees
        if ($Worktrees) {
            $wtDir = Join-Path $tmpDir '.worktrees'
            New-Item -ItemType Directory -Path $wtDir -Force | Out-Null

            foreach ($name in $Worktrees) {
                $wtPath = Join-Path $wtDir $name
                git -C $tmpDir worktree add $wtPath -b "feat/$name" 2>$null | Out-Null

                # Add workflow state if provided
                if ($WorkflowStates -and $WorkflowStates.ContainsKey($name)) {
                    $wfDir = Join-Path $wtPath '.workflow'
                    New-Item -ItemType Directory -Path $wfDir -Force | Out-Null
                    $WorkflowStates[$name] | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $wfDir 'state.json') -Encoding UTF8
                }
            }
        }

        return $tmpDir
    }

    function Remove-TestRepo {
        param([string]$Path)
        if ($Path -and (Test-Path $Path)) {
            # Remove worktrees first
            $wtDir = Join-Path $Path '.worktrees'
            if (Test-Path $wtDir) {
                Get-ChildItem -Directory $wtDir | ForEach-Object {
                    git -C $Path worktree remove $_.FullName --force 2>$null | Out-Null
                }
            }
            Remove-Item $Path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    function Invoke-Dev {
        <#
        .SYNOPSIS
            Run dev.ps1 and capture Write-Host output as strings.
        #>
        param(
            [string]$Root,
            [string[]]$Arguments
        )

        if ($Arguments) {
            $output = pwsh -NoProfile -Command "& '$script:DevScript' -RepoRoot '$Root' $($Arguments -join ' ')" 2>&1
            return $output
        } else {
            $output = pwsh -NoProfile -Command "& '$script:DevScript' -RepoRoot '$Root'" 2>&1
            return $output
        }
    }
}

Describe "dashboard" {
    BeforeAll {
        $script:repo = New-TestRepo -Worktrees @('alpha', 'beta', 'gamma') -WorkflowStates @{
            'alpha' = @{ branch = 'feat/alpha'; spec = 'specs/alpha.md' }
            'beta'  = @{ branch = 'feat/beta'; gates = @{ passed = '2026-01-01T00:00:00Z'; commit_ref = 'abc1234' }; verify = @{ cli = '2026-01-01T00:00:00Z' }; qa = @{ cli = '2026-01-01T00:00:00Z' }; review = @{ passed = '2026-01-01T00:00:00Z'; findings = 0 }; pr = @{ url = 'https://github.com/test/test/pull/1'; created = '2026-01-01T00:00:00Z' } }
        }
    }

    AfterAll {
        Remove-TestRepo -Path $script:repo
    }

    It "groups by attention priority" {
        $output = Invoke-Dev -Root $script:repo
        $text = $output -join "`n"

        # Dirty worktrees go to "attention" even if they have PRs (attention takes priority)
        $text | Should -Match 'Needs attention'
        $text | Should -Match 'alpha'
        $text | Should -Match 'beta'
    }

    It "shows all status columns" {
        $output = Invoke-Dev -Root $script:repo
        $text = $output -join "`n"

        # All worktree names present
        $text | Should -Match 'alpha'
        $text | Should -Match 'beta'
        $text | Should -Match 'gamma'
    }

    It "shows worktree count" {
        $output = Invoke-Dev -Root $script:repo
        $text = $output -join "`n"

        $text | Should -Match '3 worktrees'
    }

    It "hides empty groups" {
        $output = Invoke-Dev -Root $script:repo
        $text = $output -join "`n"

        # "In progress" should not appear (no running pipelines)
        $text | Should -Not -Match 'In progress'
        # "Ready to clean" should not appear (no merged branches)
        $text | Should -Not -Match 'Ready to clean'
    }
}

Describe "navigation" {
    BeforeAll {
        $script:repo = New-TestRepo -Worktrees @('feature-auth', 'feature-api', 'bugfix-login')
    }

    AfterAll {
        Remove-TestRepo -Path $script:repo
    }

    It "cd to worktree by prefix" {
        $output = Invoke-Dev -Root $script:repo -Arguments @('bugfix')
        $text = $output -join "`n"

        $text | Should -Match 'bugfix-login'
    }

    It "rejects ambiguous prefix" {
        $output = Invoke-Dev -Root $script:repo -Arguments @('feature')
        $text = $output -join "`n"

        $text | Should -Match 'Ambiguous'
        $text | Should -Match 'feature-auth'
        $text | Should -Match 'feature-api'
    }

    It "shows error for no match" {
        $output = Invoke-Dev -Root $script:repo -Arguments @('xyz')
        $text = $output -join "`n"

        $text | Should -Match "No worktree matching 'xyz'"
        $text | Should -Match 'Available'
    }
}

Describe "status" {
    BeforeAll {
        $script:repo = New-TestRepo -Worktrees @('my-feature') -WorkflowStates @{
            'my-feature' = @{
                branch  = 'feat/my-feature'
                started = '2026-03-26T00:00:00Z'
                spec    = 'specs/my-feature.md'
                gates   = @{ passed = '2026-03-26T01:00:00Z'; commit_ref = 'abc1234' }
                verify  = @{ cli = '2026-03-26T01:10:00Z'; tui = '2026-03-26T01:15:00Z' }
            }
        }
    }

    AfterAll {
        Remove-TestRepo -Path $script:repo
    }

    It "shows full workflow detail" {
        $output = Invoke-Dev -Root $script:repo -Arguments @('status', 'my-feature')
        $text = $output -join "`n"

        $text | Should -Match 'my-feature'
        $text | Should -Match 'gates'
        $text | Should -Match 'verify'
        $text | Should -Match 'qa.*not completed'
        $text | Should -Match 'review.*not completed'
        $text | Should -Match 'Pipeline.*not running'
    }
}

Describe "pipeline" {
    It "detects dead pipeline" {
        $repo = New-TestRepo -Worktrees @('stuck')

        # Create a lock file with a dead PID
        $wfDir = Join-Path $repo '.worktrees' 'stuck' '.workflow'
        New-Item -ItemType Directory -Path $wfDir -Force | Out-Null
        '99999' | Set-Content (Join-Path $wfDir 'pipeline.lock') -Encoding UTF8

        $output = Invoke-Dev -Root $repo
        $text = $output -join "`n"

        $text | Should -Match 'Needs attention'
        $text | Should -Match 'stuck'

        Remove-TestRepo -Path $repo
    }
}

Describe "fallback" {
    It "works without conventions" {
        $tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "dev-bare-$(Get-Random)"
        New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
        Push-Location $tmpDir
        git init --initial-branch main 2>$null | Out-Null
        git commit --allow-empty -m 'init' 2>$null | Out-Null
        Pop-Location

        $output = Invoke-Dev -Root $tmpDir
        $text = $output -join "`n"

        # Should show branch info, not error
        $text | Should -Match 'main'
        $text | Should -Not -Match 'error'
        $text | Should -Not -Match 'not found'

        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Describe "completion" {
    BeforeAll {
        $script:repo = New-TestRepo -Worktrees @('alpha', 'beta')
    }

    AfterAll {
        Remove-TestRepo -Path $script:repo
    }

    It "completes names and subcommands" {
        $output = pwsh -NoProfile -Command "& '$script:DevScript' -RepoRoot '$($script:repo)' -Completions" 2>&1
        $text = $output -join "`n"

        # Subcommands
        $text | Should -Match 'status'
        $text | Should -Match 'clean'
        $text | Should -Match 'help'

        # Worktree names
        $text | Should -Match 'alpha'
        $text | Should -Match 'beta'
    }
}

Describe "performance" {
    It "renders under 2 seconds" {
        $repo = New-TestRepo -Worktrees @('wt1', 'wt2', 'wt3', 'wt4', 'wt5', 'wt6')

        $elapsed = Measure-Command {
            pwsh -NoProfile -Command "& '$script:DevScript' -RepoRoot '$repo'" 2>$null
        }

        $elapsed.TotalMilliseconds | Should -BeLessThan 2000

        Remove-TestRepo -Path $repo
    }
}
