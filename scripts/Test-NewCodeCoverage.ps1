<#
.SYNOPSIS
    Checks that new source files have corresponding test files.

.DESCRIPTION
    Compares the current branch to main and identifies new .cs files in src/.
    For each new source file, checks if a corresponding test file exists.
    Returns a list of files missing tests.

.PARAMETER BaseBranch
    The branch to compare against. Defaults to 'main'.

.PARAMETER FailOnMissing
    If set, exits with code 1 when tests are missing. Otherwise just warns.

.EXAMPLE
    .\scripts\Test-NewCodeCoverage.ps1
    # Lists files missing tests (warning only)

.EXAMPLE
    .\scripts\Test-NewCodeCoverage.ps1 -FailOnMissing
    # Fails if any files are missing tests

.OUTPUTS
    PSCustomObject with properties:
    - MissingTests: Array of source files without corresponding test files
    - HasCoverage: Array of source files with corresponding test files
    - Excluded: Array of files excluded from check (interfaces, etc.)
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$BaseBranch = 'main',

    [Parameter()]
    [switch]$FailOnMissing
)

$ErrorActionPreference = 'Stop'

# Patterns for files that don't need tests
$excludePatterns = @(
    '^I[A-Z].*\.cs$',           # Interfaces (IFoo.cs)
    'Exception\.cs$',            # Exception classes (simple)
    'Extensions\.cs$',           # Extension methods (often thin wrappers)
    'Constants\.cs$',            # Constants
    'Options\.cs$',              # Options/settings classes (POCOs)
    'Args\.cs$',                 # Event args
    'Attribute\.cs$',            # Attributes
    'AssemblyInfo\.cs$',         # Assembly info
    'GlobalUsings\.cs$'          # Global usings
)

# Get new/modified files compared to base branch
$changedFiles = git diff --name-only --diff-filter=A $BaseBranch -- 'src/*.cs' 2>$null
if (-not $changedFiles) {
    Write-Host "No new source files detected." -ForegroundColor Green
    return @{
        MissingTests = @()
        HasCoverage = @()
        Excluded = @()
    }
}

$missing = @()
$covered = @()
$excluded = @()

foreach ($file in $changedFiles) {
    # Skip files matching exclude patterns
    $fileName = Split-Path $file -Leaf
    $shouldExclude = $false

    foreach ($pattern in $excludePatterns) {
        if ($fileName -match $pattern) {
            $shouldExclude = $true
            break
        }
    }

    if ($shouldExclude) {
        $excluded += $file
        continue
    }

    # Determine expected test file location
    # src/PPDS.Auth/Profiles/ProfileStore.cs -> tests/PPDS.Auth.Tests/Profiles/ProfileStoreTests.cs
    if ($file -match '^src/(PPDS\.[^/]+)/(.+)\.cs$') {
        $package = $Matches[1]
        $relativePath = $Matches[2]

        # Build expected test path
        $testFileName = (Split-Path $relativePath -Leaf) + 'Tests.cs'
        $testDir = Split-Path $relativePath -Parent

        if ($testDir) {
            $expectedTestPath = "tests/$package.Tests/$testDir/$testFileName"
        } else {
            $expectedTestPath = "tests/$package.Tests/$testFileName"
        }

        # Check if test file exists
        if (Test-Path $expectedTestPath) {
            $covered += $file
        } else {
            # Also check for test file in any subdirectory (flexible matching)
            $searchPattern = "tests/$package.Tests/**/$testFileName"
            $foundTests = Get-ChildItem -Path "tests/$package.Tests" -Filter $testFileName -Recurse -ErrorAction SilentlyContinue

            if ($foundTests) {
                $covered += $file
            } else {
                $missing += @{
                    SourceFile = $file
                    ExpectedTestPath = $expectedTestPath
                }
            }
        }
    }
}

# Output results
Write-Host ""
Write-Host "Test Coverage Check" -ForegroundColor Cyan
Write-Host "===================" -ForegroundColor Cyan
Write-Host ""

if ($excluded.Count -gt 0) {
    Write-Host "Excluded (no tests needed): $($excluded.Count)" -ForegroundColor DarkGray
    $excluded | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    Write-Host ""
}

if ($covered.Count -gt 0) {
    Write-Host "Has tests: $($covered.Count)" -ForegroundColor Green
    $covered | ForEach-Object { Write-Host "  [✓] $_" -ForegroundColor Green }
    Write-Host ""
}

if ($missing.Count -gt 0) {
    Write-Host "Missing tests: $($missing.Count)" -ForegroundColor Yellow
    foreach ($m in $missing) {
        Write-Host "  [!] $($m.SourceFile)" -ForegroundColor Yellow
        Write-Host "      Expected: $($m.ExpectedTestPath)" -ForegroundColor DarkYellow
    }
    Write-Host ""

    if ($FailOnMissing) {
        Write-Error "Test coverage check failed. $($missing.Count) file(s) missing tests."
        exit 1
    } else {
        Write-Warning "Consider adding tests for the above files."
    }
} else {
    Write-Host "All new source files have corresponding tests!" -ForegroundColor Green
}

# Return structured result for programmatic use
return @{
    MissingTests = $missing
    HasCoverage = $covered
    Excluded = $excluded
}
