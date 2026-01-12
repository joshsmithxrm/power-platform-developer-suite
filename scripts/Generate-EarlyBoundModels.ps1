<#
.SYNOPSIS
    Generates early-bound entity classes from Dataverse metadata using pac modelbuilder.

.DESCRIPTION
    Uses the Power Platform CLI (pac) to generate strongly-typed entity classes
    for system/development entities used by PPDS. Generated classes provide
    compile-time type safety and IntelliSense instead of magic strings.

    Prerequisites:
    1. Install Power Platform CLI: https://learn.microsoft.com/en-us/power-platform/developer/cli/introduction
    2. Authenticate: pac auth create --deviceCode

.EXAMPLE
    .\scripts\Generate-EarlyBoundModels.ps1

.EXAMPLE
    # Regenerate after schema changes
    .\scripts\Generate-EarlyBoundModels.ps1 -Force

.NOTES
    - Generated files are checked into source control
    - No build-time Dataverse connection needed after generation
    - Re-run this script only when adding entities or after Dataverse schema changes
#>

[CmdletBinding()]
param(
    [Parameter()]
    [switch]$Force,

    [Parameter()]
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../src/PPDS.Dataverse/Generated')
)

$ErrorActionPreference = 'Stop'

# Entities used by PPDS CLI and Migration
$Entities = @(
    # Plugin registration
    'pluginassembly'
    'pluginpackage'
    'plugintype'
    'sdkmessage'
    'sdkmessagefilter'
    'sdkmessageprocessingstep'
    'sdkmessageprocessingstepimage'
    'plugintracelog'  # Plugin trace logs for debugging
    'serviceendpoint'  # WebHooks, Azure Service Bus, Event Hub
    # Custom APIs
    'customapi'
    'customapirequestparameter'
    'customapiresponseproperty'
    # Virtual entity data providers
    'entitydataprovider'
    # Solution/ALM
    'solution'
    'solutioncomponent'
    'asyncoperation'
    'importjob'
    # User/role management
    'systemuser'
    'role'
    'systemuserroles'
    'publisher'
    # Environment variables
    'environmentvariabledefinition'
    'environmentvariablevalue'
    # Flows and automation
    'workflow'
    # Connection references
    'connectionreference'
)

Write-Host "PPDS Early-Bound Model Generator" -ForegroundColor Cyan
Write-Host "=================================" -ForegroundColor Cyan
Write-Host ""

# Check for pac CLI
$pacPath = Get-Command pac -ErrorAction SilentlyContinue
if (-not $pacPath) {
    Write-Error @"
Power Platform CLI (pac) not found in PATH.

Install pac CLI:
  1. Via .NET tool: dotnet tool install --global Microsoft.PowerApps.CLI.Tool
  2. Via standalone: https://learn.microsoft.com/en-us/power-platform/developer/cli/introduction

After installation, restart your terminal and try again.
"@
    exit 1
}

Write-Host "Found pac CLI: $($pacPath.Source)" -ForegroundColor Green

# Check for pac auth
Write-Host "Checking authentication..." -ForegroundColor Gray
$authOutput = pac auth list 2>&1
if ($LASTEXITCODE -ne 0 -or $authOutput -match 'No profiles') {
    Write-Error @"
No pac authentication profile found.

Create an auth profile:
  pac auth create --deviceCode

This opens a browser for interactive login. The profile is stored locally
and persists across sessions.
"@
    exit 1
}

# Show active profile
$activeProfile = $authOutput | Select-String '\*' | Select-Object -First 1
if ($activeProfile) {
    Write-Host "Active profile: $($activeProfile.ToString().Trim())" -ForegroundColor Green
}

# Check output directory
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) {
    if (-not $Force) {
        $existingFiles = Get-ChildItem $OutputDirectory -Filter "*.cs" -ErrorAction SilentlyContinue
        if ($existingFiles.Count -gt 0) {
            Write-Host ""
            Write-Host "Output directory already contains $($existingFiles.Count) file(s):" -ForegroundColor Yellow
            Write-Host "  $OutputDirectory" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "Use -Force to regenerate, or delete the directory manually." -ForegroundColor Yellow
            exit 0
        }
    }
    else {
        Write-Host "Cleaning existing output directory..." -ForegroundColor Gray
        Remove-Item $OutputDirectory -Recurse -Force
    }
}

# Create output directory
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Write-Host "Output directory: $OutputDirectory" -ForegroundColor Gray

# Build entity filter
$entityFilter = $Entities -join ';'
Write-Host ""
Write-Host "Generating classes for $($Entities.Count) entities:" -ForegroundColor Cyan
$Entities | ForEach-Object { Write-Host "  - $_" -ForegroundColor Gray }
Write-Host ""

# Run pac modelbuilder
# Note: Most options are switches (present = enabled, absent = disabled)
# Only include switches we want enabled
$pacArgs = @(
    'modelbuilder', 'build'
    '--outdirectory', $OutputDirectory
    '--namespace', 'PPDS.Dataverse.Generated'
    '--entitynamesfilter', $entityFilter
    '--emitfieldsclasses'           # Generate Field constants class
    '--language', 'CSharp'
)

Write-Host "Running: pac $($pacArgs -join ' ')" -ForegroundColor Gray
Write-Host ""

& pac @pacArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "pac modelbuilder failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# Count generated files
$generatedFiles = Get-ChildItem $OutputDirectory -Filter "*.cs" -Recurse
Write-Host ""
Write-Host "Successfully generated $($generatedFiles.Count) file(s):" -ForegroundColor Green
$generatedFiles | ForEach-Object {
    Write-Host "  - $($_.Name)" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Review generated files in: $OutputDirectory" -ForegroundColor Gray
Write-Host "  2. Build solution: dotnet build" -ForegroundColor Gray
Write-Host "  3. Update code to use early-bound classes" -ForegroundColor Gray
Write-Host ""
