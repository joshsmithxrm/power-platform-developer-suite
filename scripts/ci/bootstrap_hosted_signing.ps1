param([ValidateSet('sign','prepare','verify')][string]$Stage)
$ErrorActionPreference = 'Stop'
if ($env:PPDS_ADMISSION -cne 'true' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or
    $env:GITHUB_RUN_ATTEMPT -cne '1' -or $env:PPDS_EXPECTED_SOURCE -cne $env:GITHUB_SHA -or
    $env:GITHUB_REPOSITORY -cne 'joshsmithxrm/power-platform-developer-suite' -or
    $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows') {
    throw 'Explicit first-attempt hosted test admission required'
}
# Run once before the action/key exists. Staging outside checkout keeps source clean.
$target = Join-Path $env:RUNNER_TEMP 'ppds-compat-transport'
New-Item -ItemType Directory -Path $target -ErrorAction Stop | Out-Null
$source = Join-Path $env:GITHUB_WORKSPACE 'scripts/ci/hosted-signing-artifacts'
foreach ($name in @('package.json','package-lock.json','cli.mjs','transport.mjs')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $target $name) -ErrorAction Stop
}
Push-Location -LiteralPath $target
try {
    & npm.cmd ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Locked artifact bootstrap failed' }
} finally { Pop-Location }
$arguments = @('--stage', $Stage, '--source', $env:GITHUB_SHA, '--experiment', $env:PPDS_EXPERIMENT,
               '--authorization', $env:PPDS_AUTHORIZATION, '--admit-network-connected-disposable-signing')
$argumentsJson = ConvertTo-Json -InputObject $arguments -Compress
# Hosted images can expose several Python installations. Match normal PATH
# resolution; serializing every Source produces one invalid executable path.
$python = (Get-Command python.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
"arguments=$argumentsJson" >> $env:GITHUB_OUTPUT
"python=$python" >> $env:GITHUB_OUTPUT
"PPDS_ARTIFACT_ADAPTER=$(Join-Path $target 'cli.mjs')" >> $env:GITHUB_ENV
