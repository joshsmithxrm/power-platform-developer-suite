param(
    [Parameter(Mandatory)][string]$Directory,
    [string]$ExpectedPublicKey,
    [switch]$DelaySignedInput
)
$ErrorActionPreference = 'Stop'
# Callers see the throw text as before. For a plain `throw` (the failures this script raises itself;
# the engine raises a few errors of the same exception type, which only ever match no known message)
# the first line of the message is also written as an anchored line, so a caller can classify the
# failure without parsing PowerShell's error rendering, which includes the failing source line and
# terminal colour codes. .NET and cmdlet errors get no such line.
trap {
    if ($_.Exception.GetType().FullName -ceq 'System.Management.Automation.RuntimeException') {
        [Console]::Error.WriteLine('PPDS_INSPECTOR_MESSAGE=' + ($_.Exception.Message -split '\r?\n')[0])
    }
    break
}
# The caller has checked that the tool exists.
function Invoke-StrongNameVerification([string]$Tool, [string]$Assembly) {
    # A native command that reported no exit code leaves $LASTEXITCODE unset. Clear any stale one
    # first so an unset value can be told apart from the result of an earlier call.
    $global:LASTEXITCODE = $null
    $notRun = 'Strong-name verification tool did not report an exit code; without PATHEXT in the environment PowerShell starts a native executable detached and cannot observe it'
    try {
        # -vf forces verification even if verification skipping is configured.
        $diagnostics = & $Tool -vf $Assembly 2>&1
    }
    catch [System.Management.Automation.CommandNotFoundException] { throw $notRun }
    if ($null -eq $LASTEXITCODE) { throw $notRun }
    if ($LASTEXITCODE -ne 0) { throw "Plugins cryptographic strong-name verification failed: $diagnostics" }
}
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
        if (!$DelaySignedInput) { Invoke-StrongNameVerification $sdk $file.FullName }
    }
    [pscustomobject]@{name=$identity.Name; version=$version}
})
if ($rows.Count -eq 0) { throw 'No assemblies to inspect' }
ConvertTo-Json -InputObject $rows -Compress
