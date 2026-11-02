Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReadOnly.psm1') -Force
$checks = 0
function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Assert-Rejected([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-True $rejected 'Unsafe input was accepted.'
}

# The actual HTTP boundary is exercised with an in-memory handler: no sockets/auth.
Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public sealed class PreflightRecordingHandler : HttpMessageHandler {
    public int Calls;
    public string Method;
    public string Path;
    public bool HadBody;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        Calls++;
        Method = request.Method.Method;
        Path = request.RequestUri.PathAndQuery;
        HadBody = request.Content != null;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("{\"value\":[]}")
        });
    }
}
'@
$handler = [PreflightRecordingHandler]::new()
$client = [Net.Http.HttpClient]::new($handler)
try {
    foreach ($operation in @('Identity','Organization','Caller','Package','Assembly','Type','Step','Account')) {
        $null = Invoke-PreflightGet -Client $client -Target 'https://fixture.invalid' -AccessToken 'synthetic-token' -Operation $operation -UserId '00000000-0000-0000-0000-000000000001'
        Assert-True ($handler.Method -eq 'GET' -and -not $handler.HadBody) 'A data request was not a bodyless GET.'
        Assert-True ($handler.Path.StartsWith('/api/data/v9.2/')) 'Unexpected data route.'
    }
    foreach ($invalid in @('POST','DELETE','$batch','https://other.invalid','../accounts','Package?extra=1')) {
        Assert-Rejected { Invoke-PreflightGet -Client $client -Target 'https://fixture.invalid' -AccessToken 'synthetic-token' -Operation $invalid }
    }
    Assert-True ($handler.Calls -eq 8) 'Rejected routes reached the HTTP handler.'
    Assert-Rejected { Get-PreflightReadPath -Operation Caller -UserId '../accounts' }
    Assert-Rejected { Get-PreflightReadPath -Operation Caller }
}
finally { $client.Dispose(); $handler.Dispose() }

foreach ($target in @('http://fixture.invalid','https://fixture.invalid:444','https://user@fixture.invalid','https://fixture.invalid/path','https://fixture.invalid?x=1','https://fixture.invalid#fragment','https://fixture.invalid')) {
    Assert-Rejected { Assert-PreflightBinding $target 'wrong-app' 'wrong-tenant' }
}
Assert-True ((Get-ValueHash 'abc') -eq 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad') 'Binding hash is incorrect.'
$script:identityReads = 0
$badIdentityRead = {
    param($operation, $userId)
    $script:identityReads++
    if ($operation -eq 'Identity') { return @{OrganizationId='00000000-0000-0000-0000-000000000001'} }
    return @{Detail=@{OrganizationId='00000000-0000-0000-0000-000000000002'}}
}
Assert-Rejected { Invoke-Net48Preflight -Read $badIdentityRead -ApplicationId '00000000-0000-0000-0000-000000000003' }
Assert-True ($script:identityReads -eq 2) 'Identity mismatch did not stop before collision reads.'

$runner = Get-Content (Join-Path $PSScriptRoot 'Invoke-ReadOnly.ps1') -Raw
$module = Get-Content (Join-Path $PSScriptRoot 'ReadOnly.psm1') -Raw
Assert-True ($runner.Contains('$handler.AllowAutoRedirect = $false')) 'Redirect boundary was removed.'
Assert-True ([regex]::Matches($module, 'HttpMethod\]::Get').Count -eq 1) 'Unexpected Dataverse HTTP method construction.'
Assert-True (-not [regex]::IsMatch($module, 'HttpMethod\]::(Post|Put|Patch|Delete)')) 'Mutation method present.'
Assert-True ($runner.Contains('https://login.microsoftonline.com/$tenant/oauth2/v2.0/token')) 'Token exchange endpoint changed.'
Assert-True ([regex]::Matches($runner, 'Invoke-RestMethod -Method Post').Count -eq 1) 'Unexpected POST operation.'
Assert-True (-not [regex]::IsMatch($runner + $module, '(?i)auth create|auth select|plugins deploy|plugins register|plugins unregister|SetEnvironmentVariable')) 'Profile or mutation command present.'
foreach ($file in @('ReadOnly.psm1','Invoke-ReadOnly.ps1')) {
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) "Parse failed for $file."
}
Write-Output "Read-only boundary checks passed: $checks. No network or authentication used."
