param(
    [Parameter(Mandatory)][string]$Directory,
    [string]$ExpectedPublicKey,
    [switch]$DelaySignedInput
)
$ErrorActionPreference = 'Stop'
if ($DelaySignedInput -and !$ExpectedPublicKey) { throw 'Delay-signed input requires an expected public key' }
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
        $stream = [IO.File]::OpenRead($file.FullName)
        $pe = $null
        try {
            $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
            $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            if (!$reader.IsAssembly -or $reader.AssemblyFiles.Count -ne 0) { throw 'Single-module Plugins assembly required' }
            $frameworks = @(
                foreach ($handle in $reader.GetAssemblyDefinition().GetCustomAttributes()) {
                    $attribute = $reader.GetCustomAttribute($handle)
                    if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
                    $member = $reader.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
                    if ($member.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
                    $type = $reader.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$member.Parent)
                    if ($reader.GetString($type.Namespace) -ceq 'System.Runtime.Versioning' -and
                        $reader.GetString($type.Name) -ceq 'TargetFrameworkAttribute') {
                        $blob = $reader.GetBlobReader($attribute.Value)
                        if ($blob.ReadUInt16() -ne 1) { throw 'Invalid framework attribute' }
                        $blob.ReadSerializedString()
                    }
                }
            )
            if ($frameworks.Count -ne 1 -or $frameworks[0] -cne '.NETFramework,Version=v4.6.2') { throw 'Plugins must target net462' }
        }
        finally {
            if ($null -ne $pe) { $pe.Dispose() }
            $stream.Dispose()
        }
        $sdk = Join-Path ${env:ProgramFiles(x86)} 'Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\sn.exe'
        if (!(Test-Path -LiteralPath $sdk -PathType Leaf)) { throw 'Required Windows SDK sn.exe is missing' }
        # -vf forces verification even if verification skipping is configured.
        if (!$DelaySignedInput) {
            $diagnostics = & $sdk -vf $file.FullName 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Plugins cryptographic strong-name verification failed: $diagnostics" }
        }
    }
    [pscustomobject]@{name=$identity.Name; version=$version}
})
if ($rows.Count -eq 0) { throw 'No assemblies to inspect' }
ConvertTo-Json -InputObject $rows -Compress
