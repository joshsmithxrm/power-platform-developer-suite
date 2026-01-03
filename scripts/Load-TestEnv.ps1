<#
.SYNOPSIS
    Loads test environment variables from .env.local into the current PowerShell session.

.DESCRIPTION
    Reads the .env.local file from the repository root and sets each variable
    as a process-level environment variable. This makes credentials available
    for running integration tests locally.

    Variables are set at the Process level only - they will not persist after
    the PowerShell session ends.

.EXAMPLE
    . .\scripts\Load-TestEnv.ps1
    dotnet test --filter "Category=Integration"

.EXAMPLE
    . .\scripts\Load-TestEnv.ps1
    dotnet run --project src/PPDS.Cli -- auth test

.NOTES
    - Copy .env.example to .env.local and fill in your values
    - .env.local is gitignored and will not be committed
    - Run this script with dot-sourcing (. .\script.ps1) to set variables in current session
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$EnvFile = (Join-Path $PSScriptRoot '../.env.local'),

    [Parameter()]
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $EnvFile)) {
    $exampleFile = Join-Path $PSScriptRoot '../.env.example'
    Write-Warning ".env.local not found at: $EnvFile"
    Write-Warning "Copy .env.example to .env.local and fill in your values:"
    Write-Warning "  Copy-Item '$exampleFile' '$EnvFile'"
    return
}

$loaded = 0
$skipped = 0

Get-Content $EnvFile | ForEach-Object {
    $line = $_.Trim()

    # Skip empty lines and comments
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
        return
    }

    # Parse KEY=VALUE
    $eqIndex = $line.IndexOf('=')
    if ($eqIndex -gt 0) {
        $key = $line.Substring(0, $eqIndex).Trim()
        $value = $line.Substring($eqIndex + 1).Trim()

        # Remove surrounding quotes if present
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        # Skip placeholder values
        if ($value -match '^(your-|00000000-|https://yourorg)') {
            $skipped++
            if (-not $Quiet) {
                Write-Warning "Skipping $key (placeholder value)"
            }
            return
        }

        [Environment]::SetEnvironmentVariable($key, $value, 'Process')
        $loaded++

        if (-not $Quiet) {
            # Mask sensitive values in output
            $displayValue = if ($key -match '(SECRET|PASSWORD|TOKEN)') {
                '********'
            } else {
                $value
            }
            Write-Host "  $key = $displayValue" -ForegroundColor DarkGray
        }
    }
}

if (-not $Quiet) {
    if ($loaded -gt 0) {
        Write-Host "Loaded $loaded environment variable(s) from .env.local" -ForegroundColor Green
    }
    if ($skipped -gt 0) {
        Write-Host "Skipped $skipped placeholder value(s) - update .env.local with real values" -ForegroundColor Yellow
    }
}

# Verify minimum required variables
$required = @('DATAVERSE_URL', 'PPDS_TEST_APP_ID', 'PPDS_TEST_TENANT_ID')
$missing = $required | Where-Object { -not [Environment]::GetEnvironmentVariable($_) }

if ($missing.Count -gt 0 -and -not $Quiet) {
    Write-Warning "Missing required variables: $($missing -join ', ')"
    Write-Warning "Integration tests will be skipped without these."
}

# Check for at least one auth method
$hasClientSecret = [Environment]::GetEnvironmentVariable('PPDS_TEST_CLIENT_SECRET')
$hasCertificate = [Environment]::GetEnvironmentVariable('PPDS_TEST_CERT_PATH') -or
                  [Environment]::GetEnvironmentVariable('PPDS_TEST_CERT_BASE64')

if (-not $hasClientSecret -and -not $hasCertificate -and -not $Quiet) {
    Write-Warning "No authentication credentials found."
    Write-Warning "Set PPDS_TEST_CLIENT_SECRET or PPDS_TEST_CERT_PATH/PPDS_TEST_CERT_BASE64"
}
