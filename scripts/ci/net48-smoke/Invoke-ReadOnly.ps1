[CmdletBinding()]
param([Parameter(Mandatory)][string] $ReportPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSScriptRoot 'ReadOnly.psm1') -Force

$phase = 'binding'
$client = $null
$handler = $null
$exitCode = 1
try {
    # Existing job configuration only; never create or update authentication profiles.
    $target = $env:NET48_DATAVERSE_URL
    $application = $env:NET48_APP_ID
    $tenant = $env:NET48_TENANT_ID
    Assert-PreflightBinding $target $application $tenant
    $phase = 'authentication'
    if (-not $env:ACTIONS_ID_TOKEN_REQUEST_URL -or -not $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN) {
        throw 'OidcUnavailable'
    }
    # Same workload-identity exchange as PPDS.Auth's GitHubFederatedCredentialProvider.
    # This POST is to Entra only; every Dataverse request is GET.
    $oidcUri = [uri]($env:ACTIONS_ID_TOKEN_REQUEST_URL + '&audience=api://AzureADTokenExchange')
    if ($oidcUri.Scheme -ne 'https' -or $oidcUri.UserInfo -or
        -not $oidcUri.DnsSafeHost.EndsWith('.actions.githubusercontent.com')) { throw 'OidcEndpointMismatch' }
    $assertion = Invoke-RestMethod -Method Get -Uri $oidcUri -MaximumRedirection 0 -TimeoutSec 60 -Headers @{
        Authorization = 'Bearer ' + $env:ACTIONS_ID_TOKEN_REQUEST_TOKEN
    }
    $tokenResponse = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token" -MaximumRedirection 0 -TimeoutSec 60 -Body @{
        client_id = $application
        scope = $target.TrimEnd('/') + '/.default'
        grant_type = 'client_credentials'
        client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
        client_assertion = $assertion.value
    }
    $assertion = $null
    $accessToken = $tokenResponse.access_token
    $tokenResponse = $null
    $phase = 'read-only-queries'
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $read = {
        param($operation, $userId)
        Invoke-PreflightGet -Client $client -Target $target -AccessToken $accessToken -Operation $operation -UserId $userId
    }.GetNewClosure()
    $report = Invoke-Net48Preflight -Read $read -ApplicationId $application
    $exitCode = if ($report.status -eq 'passed') { 0 } else { 2 }
}
catch {
    # Never emit raw exceptions, response bodies, URLs, JWTs, or user/organization IDs.
    $known = @('TargetMismatch','IdentityMismatch','IncompleteIdentity','IncompleteCollisionResult','OidcUnavailable','OidcEndpointMismatch')
    $code = if ($_.Exception.Message -in $known) { $_.Exception.Message } else { 'ReadOnlyPreflightFailed' }
    $report = [ordered]@{ schemaVersion=1; status='failed'; phase=$phase; code=$code; mutationCalls=0; mutationAuthorized=$false }
}
finally {
    $accessToken = $null
    $assertion = $null
    $tokenResponse = $null
    if ($null -ne $client) { $client.Dispose() }
    if ($null -ne $handler) { $handler.Dispose() }
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8
Write-Output "Read-only preflight: $($report.status); mutation calls: 0."
exit $exitCode
