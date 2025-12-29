<#
.SYNOPSIS
    Run PPDS CLI directly from source (no install needed).
.EXAMPLE
    .\scripts\ppds-dev.ps1 env who
    .\scripts\ppds-dev.ps1 auth create --name dev
    .\scripts\ppds-dev.ps1 data export --schema schema.xml -o data.zip
#>
param(
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$Arguments
)

$projectPath = Join-Path $PSScriptRoot "..\src\PPDS.Cli\PPDS.Cli.csproj"
dotnet run --project $projectPath -- @Arguments
