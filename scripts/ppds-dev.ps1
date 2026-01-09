# Local development wrapper for ppds CLI
# Usage: .\scripts\ppds-dev.ps1 session list
param([Parameter(ValueFromRemainingArguments=$true)]$Args)

$projectPath = Join-Path $PSScriptRoot "..\src\PPDS.Cli\PPDS.Cli.csproj"
& dotnet run --project $projectPath --framework net10.0 -- @Args
