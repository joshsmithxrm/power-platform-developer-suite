<#
.SYNOPSIS
    Pack and install PPDS CLI as a global tool from local source.
.DESCRIPTION
    Use this when you need to test the actual installed tool behavior.
    For quick iteration during development, use ppds-dev.ps1 instead.
.EXAMPLE
    .\scripts\install-local.ps1
#>
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$nupkgsDir = Join-Path $repoRoot "nupkgs"
$cliProject = Join-Path $repoRoot "src\PPDS.Cli\PPDS.Cli.csproj"

Write-Host "Packing PPDS.Cli..." -ForegroundColor Cyan
dotnet pack $cliProject -c Release -o $nupkgsDir | Out-Null

# Find the latest package
$latestPackage = Get-ChildItem $nupkgsDir -Filter "PPDS.Cli.*.nupkg" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $latestPackage) {
    Write-Error "No package found in $nupkgsDir"
    exit 1
}

# Extract version from filename (PPDS.Cli.0.0.0-alpha.0.23.nupkg -> 0.0.0-alpha.0.23)
$version = $latestPackage.BaseName -replace "^PPDS\.Cli\.", ""

Write-Host "Installing version $version..." -ForegroundColor Cyan

# Uninstall if exists (ignore errors)
dotnet tool uninstall -g PPDS.Cli 2>$null

# Install
dotnet tool install --global --add-source $nupkgsDir PPDS.Cli --version $version

Write-Host ""
Write-Host "Installed! Run 'ppds --help' to test." -ForegroundColor Green
