param(
    [Parameter(Mandatory)][string]$Directory,
    [string]$ExpectedPublicKey
)
$ErrorActionPreference = 'Stop'
$rows = @(foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.dll' -File) {
    # Metadata reads only: never load or execute package code.
    $identity = [Reflection.AssemblyName]::GetAssemblyName($file.FullName)
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName).ProductVersion
    if ($ExpectedPublicKey) {
        if ($identity.Name -ne 'PPDS.Plugins') { throw 'Unexpected assembly for Plugins signing gate' }
        $expected = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ExpectedPublicKey).Path)
        $actual = $identity.GetPublicKey()
        if (!$actual -or [Convert]::ToBase64String($actual) -cne [Convert]::ToBase64String($expected)) {
            throw 'Packaged Plugins full public key differs from the repository public key'
        }
        $sdk = Join-Path ${env:ProgramFiles(x86)} 'Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\sn.exe'
        if (!(Test-Path -LiteralPath $sdk -PathType Leaf)) { throw 'Required Windows SDK sn.exe is missing' }
        # -vf forces verification even if verification skipping is configured.
        $diagnostics = & $sdk -vf $file.FullName 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Plugins cryptographic strong-name verification failed: $diagnostics" }
    }
    [pscustomobject]@{name=$identity.Name; version=$version}
})
if ($rows.Count -eq 0) { throw 'No assemblies to inspect' }
ConvertTo-Json -InputObject $rows -Compress
