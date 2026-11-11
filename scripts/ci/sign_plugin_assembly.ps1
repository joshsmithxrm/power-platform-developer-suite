# Source preparation only. Production policy must remain inactive until AC-64 is met.
# This step trusts the reviewed runner/tool and receives an already admitted DLL.
# It never loads the target assembly, runs build machinery, or resolves a tool via PATH.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputDll,
    [Parameter(Mandatory)][string]$OutputDll,
    [Parameter(Mandatory)][string]$Policy,
    [Parameter(Mandatory)][string]$Request,
    [Parameter(Mandatory)][string]$PublicKey,
    [Parameter(Mandatory)][string]$Receipt
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$lockedInputs = [Collections.Generic.List[IO.FileStream]]::new()
$ownedOutput = $false
$ownedReceipt = $false
$ownedKey = $false
$keyPath = $null
$keyStream = $null
$keyBytes = $null
$completed = $false

function Assert-Condition([bool]$Condition, [string]$Reason) {
    if (-not $Condition) { throw $Reason }
}

function Get-SafePath([string]$Path, [bool]$MustExist) {
    Assert-Condition ([IO.Path]::IsPathFullyQualified($Path)) 'Absolute paths are required'
    $full = [IO.Path]::GetFullPath($Path)
    Assert-Condition ($full -match '^[A-Za-z]:\\' -and $full.Substring(2) -notmatch ':') 'Local filesystem paths are required'
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            Assert-Condition (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Reparse paths are not allowed'
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
    if ($MustExist) {
        Assert-Condition (Test-Path -LiteralPath $full -PathType Leaf) 'Required input file is missing'
    }
    else {
        Assert-Condition (-not (Test-Path -LiteralPath $full)) 'Output paths must not exist'
        Assert-Condition (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($full)) -PathType Container) 'Output parent must already exist'
    }
    return $full
}

function Read-LockedBytes([string]$Path, [long]$Maximum) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $lockedInputs.Add($stream)
    Assert-Condition ($stream.Length -gt 0 -and $stream.Length -le $Maximum) 'Input size is outside its bound'
    $bytes = [byte[]]::new([int]$stream.Length)
    $stream.ReadExactly($bytes)
    return ,$bytes
}

function Get-Digest([byte[]]$Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Assert-JsonKeys([Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Element.EnumerateObject()) {
            Assert-Condition ($names.Add($property.Name)) 'Duplicate JSON fields are not allowed'
            Assert-JsonKeys $property.Value
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($value in $Element.EnumerateArray()) { Assert-JsonKeys $value }
    }
}

function Read-Json([byte[]]$Bytes) {
    $json = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    $document = [Text.Json.JsonDocument]::Parse($json)
    try {
        Assert-Condition ($document.RootElement.ValueKind -eq [Text.Json.JsonValueKind]::Object) 'JSON object required'
        Assert-JsonKeys $document.RootElement
        return $document.RootElement.Clone()
    }
    finally { $document.Dispose() }
}

function Get-String([Text.Json.JsonElement]$Object, [string]$Name) {
    $value = $Object.GetProperty($Name)
    Assert-Condition ($value.ValueKind -eq [Text.Json.JsonValueKind]::String) 'JSON string required'
    return $value.GetString()
}

function Get-Hex([Text.Json.JsonElement]$Object, [string]$Name, [int]$Length = 64) {
    $value = Get-String $Object $Name
    Assert-Condition ($value -cmatch ('^[0-9a-f]{' + $Length + '}$')) 'Invalid digest or source identity'
    return $value
}

function Get-AssemblyMetadata([string]$Path, [byte[]]$ExpectedKey, [bool]$RequireDelaySign) {
    # GetAssemblyName reads metadata; Assembly.Load/LoadFrom/LoadFile are forbidden.
    $identity = [Reflection.AssemblyName]::GetAssemblyName($Path)
    Assert-Condition ($identity.Name -ceq 'PPDS.Plugins') 'Unexpected assembly identity'
    Assert-Condition ([Convert]::ToBase64String($identity.GetPublicKey()) -ceq [Convert]::ToBase64String($ExpectedKey)) 'Unexpected full public key'
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $pe = $null
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        Assert-Condition ($pe.HasMetadata -and $null -ne $pe.PEHeaders.CorHeader) 'Managed PE required'
        $cor = $pe.PEHeaders.CorHeader
        Assert-Condition (($cor.Flags -band [Reflection.PortableExecutable.CorFlags]::ILOnly) -ne 0) 'IL-only assembly required'
        Assert-Condition (($pe.PEHeaders.CoffHeader.Characteristics -band [Reflection.PortableExecutable.Characteristics]::Dll) -ne 0) 'DLL required'
        Assert-Condition ($cor.ManagedNativeHeaderDirectory.Size -eq 0) 'Native precompiled payload is not allowed'
        $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        Assert-Condition ($reader.IsAssembly -and $reader.AssemblyFiles.Count -eq 0) 'Single-module assembly required'
        $definition = $reader.GetAssemblyDefinition()
        $tfmCount = 0
        foreach ($handle in $definition.GetCustomAttributes()) {
            $attribute = $reader.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
            $member = $reader.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if ($member.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
            $type = $reader.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$member.Parent)
            if ($reader.GetString($type.Namespace) -cne 'System.Runtime.Versioning' -or
                $reader.GetString($type.Name) -cne 'TargetFrameworkAttribute') { continue }
            Assert-Condition ($reader.GetString($member.Name) -ceq '.ctor') 'Unexpected target framework constructor'
            Assert-Condition ($type.ResolutionScope.Kind -eq [Reflection.Metadata.HandleKind]::AssemblyReference) 'Unexpected target framework scope'
            $scope = $reader.GetAssemblyReference([Reflection.Metadata.AssemblyReferenceHandle]$type.ResolutionScope)
            Assert-Condition ($reader.GetString($scope.Name) -ceq 'mscorlib') 'Framework target attribute required'
            $blob = $reader.GetBlobBytes($attribute.Value)
            $expected = [Text.Encoding]::UTF8.GetBytes('.NETFramework,Version=v4.6.2')
            Assert-Condition ($blob.Length -ge $expected.Length + 5 -and $blob[0] -eq 1 -and $blob[1] -eq 0 -and $blob[2] -eq $expected.Length) 'net462 target required'
            for ($i = 0; $i -lt $expected.Length; $i++) {
                Assert-Condition ($blob[$i + 3] -eq $expected[$i]) 'net462 target required'
            }
            $tfmCount++
        }
        Assert-Condition ($tfmCount -eq 1) 'Exactly one net462 target attribute required'
        if ($RequireDelaySign) {
            Assert-Condition (($cor.Flags -band [Reflection.PortableExecutable.CorFlags]::StrongNameSigned) -eq 0) 'Delay-signed input required'
            $signature = $cor.StrongNameSignatureDirectory
            Assert-Condition ($signature.Size -gt 0 -and $signature.Size -le 2048) 'Reserved signature area required'
            foreach ($value in $pe.GetSectionData($signature.RelativeVirtualAddress).GetContent(0, $signature.Size)) {
                Assert-Condition ($value -eq 0) 'Input signature area must be empty'
            }
        }
        return [ordered]@{
            name = $identity.Name
            version = $identity.Version.ToString()
            culture = $identity.CultureName
            public_key = [Convert]::ToBase64String($identity.GetPublicKey())
            mvid = $reader.GetGuid($reader.GetModuleDefinition().Mvid).ToString()
        }
    }
    finally {
        if ($null -ne $pe) { $pe.Dispose() }
        $stream.Dispose()
    }
}

function Invoke-StrongName([string]$Tool, [string[]]$Arguments, [string]$WorkingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Tool
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $WorkingDirectory
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment.Clear()
    foreach ($name in @('SYSTEMROOT', 'WINDIR', 'TEMP', 'TMP')) {
        $value = [Environment]::GetEnvironmentVariable($name, 'Process')
        if ($value) { $start.Environment[$name] = $value }
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        Assert-Condition ($process.Start()) 'Signing tool failed to start'
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Signing tool timed out'
        }
        # Diagnostics are deliberately not emitted: no key bytes or paths enter logs.
        $null = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        Assert-Condition ($process.ExitCode -eq 0) 'Signing tool rejected the operation'
    }
    finally { $process.Dispose() }
}

try {
    Assert-Condition ($IsWindows) 'Windows signing worker required'
    $policyPath = Get-SafePath $Policy $true
    $requestPath = Get-SafePath $Request $true
    $inputPath = Get-SafePath $InputDll $true
    $publicPath = Get-SafePath $PublicKey $true
    $outputPath = Get-SafePath $OutputDll $false
    $receiptPath = Get-SafePath $Receipt $false
    Assert-Condition (-not [StringComparer]::OrdinalIgnoreCase.Equals($outputPath, $receiptPath)) 'Output paths must differ'
    $policyBytes = Read-LockedBytes $policyPath 65536
    $policyValue = Read-Json $policyBytes
    Assert-Condition ($policyValue.GetProperty('schema').GetInt32() -eq 1) 'Unsupported signing policy'
    Assert-Condition ((Get-String $policyValue 'producer_assurance') -ceq 'REVIEWED_CONTROLLED_PRODUCER') 'Producer assurance remains unselected'
    Assert-Condition ($policyValue.GetProperty('activation_approved').ValueKind -eq [Text.Json.JsonValueKind]::True) 'Signing activation remains held'
    Assert-Condition ($policyValue.GetProperty('compatibility_verified').ValueKind -eq [Text.Json.JsonValueKind]::True) 'Real compatibility remains unverified'
    $null = Get-Hex $policyValue 'compatibility_receipt_sha256'
    $expectedPublicHash = Get-Hex $policyValue 'public_key_sha256'
    $signer = $policyValue.GetProperty('signer')
    Assert-Condition ($signer.ValueKind -eq [Text.Json.JsonValueKind]::Object) 'Signer identity required'
    $toolPath = Get-SafePath (Get-String $signer 'path') $true
    Assert-Condition ([IO.Path]::GetFileName($toolPath) -ceq 'sn.exe') 'Pinned Microsoft strong-name tool required'
    $expectedToolHash = Get-Hex $signer 'sha256'
    $expectedToolVersion = Get-String $signer 'version'
    Assert-Condition ($expectedToolVersion -cmatch '^[0-9]+(?:\.[0-9]+){1,3}$') 'Pinned tool version required'
    $toolBytes = Read-LockedBytes $toolPath 16777216
    Assert-Condition ((Get-Digest $toolBytes) -ceq $expectedToolHash) 'Signer hash mismatch'
    Assert-Condition ([Diagnostics.FileVersionInfo]::GetVersionInfo($toolPath).ProductVersion -ceq $expectedToolVersion) 'Signer version mismatch'

    $requestValue = Read-Json (Read-LockedBytes $requestPath 65536)
    Assert-Condition ($requestValue.GetProperty('schema').GetInt32() -eq 1) 'Unsupported signing request'
    $source = Get-Hex $requestValue 'source' 40
    $tag = Get-String $requestValue 'tag'
    Assert-Condition ($tag -cmatch '^Plugins-v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') 'Canonical Plugins tag required'
    $runId = Get-String $requestValue 'run_id'
    $runAttempt = Get-String $requestValue 'run_attempt'
    Assert-Condition ($runId -cmatch '^[1-9][0-9]*$' -and $runAttempt -cmatch '^[1-9][0-9]*$') 'Exact run and attempt required'
    $policyHash = Get-Hex $requestValue 'policy_sha256'
    $inputHash = Get-Hex $requestValue 'input_dll_sha256'
    Assert-Condition ($policyHash -ceq (Get-Digest $policyBytes)) 'Policy binding mismatch'
    Assert-Condition ((Get-Hex $requestValue 'public_key_sha256') -ceq $expectedPublicHash) 'Public key request mismatch'
    $publicBytes = Read-LockedBytes $publicPath 4096
    Assert-Condition ((Get-Digest $publicBytes) -ceq $expectedPublicHash) 'Public key hash mismatch'
    $inputBytes = Read-LockedBytes $inputPath 16777216
    Assert-Condition ((Get-Digest $inputBytes) -ceq $inputHash) 'Input assembly hash mismatch'
    $before = Get-AssemblyMetadata $inputPath $publicBytes $true

    $tempRoot = [Environment]::GetEnvironmentVariable('RUNNER_TEMP', 'Process')
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($tempRoot)) 'Runner temporary directory required'
    $tempProbe = Join-Path $tempRoot ('ppds-sign-' + [Guid]::NewGuid().ToString('N') + '.snk')
    $keyPath = Get-SafePath $tempProbe $false
    # All admission checks above precede the sole secret read. Workflow admission
    # must additionally run before GitHub selects the secret-bearing step.
    $secret = [Environment]::GetEnvironmentVariable('PLUGINS_SNK_BASE64', 'Process')
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($secret) -and $secret.Length -le 32768) 'Signing secret unavailable'
    try { $keyBytes = [Convert]::FromBase64String($secret) }
    finally { $secret = $null }
    Assert-Condition ($keyBytes.Length -ge 512 -and $keyBytes.Length -le 16384) 'Signing key size rejected'
    $outputStream = [IO.File]::Open($outputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $ownedOutput = $true
    try { $outputStream.Write($inputBytes); $outputStream.Flush($true) }
    finally { $outputStream.Dispose() }
    try {
        $keyStream = [IO.File]::Open($keyPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
        $ownedKey = $true
        $keyStream.Write($keyBytes)
        $keyStream.Flush($true)
        [Array]::Clear($keyBytes, 0, $keyBytes.Length)
        Invoke-StrongName $toolPath @('-R', $outputPath, $keyPath) ([IO.Path]::GetDirectoryName($outputPath))
    }
    finally {
        if ($null -ne $keyStream) { $keyStream.Dispose(); $keyStream = $null }
        if ($ownedKey) { [IO.File]::Delete($keyPath); $ownedKey = $false; $keyPath = $null }
        if ($null -ne $keyBytes) { [Array]::Clear($keyBytes, 0, $keyBytes.Length); $keyBytes = $null }
    }
    $null = Get-SafePath $outputPath $true
    # Keep the exact bytes read-locked through verification and receipt creation.
    $signedBytes = Read-LockedBytes $outputPath 16777216
    Invoke-StrongName $toolPath @('-vf', $outputPath) ([IO.Path]::GetDirectoryName($outputPath))
    Invoke-StrongName $toolPath @('-D', $inputPath, $outputPath) ([IO.Path]::GetDirectoryName($outputPath))
    $after = Get-AssemblyMetadata $outputPath $publicBytes $false
    Assert-Condition (($before | ConvertTo-Json -Compress) -ceq ($after | ConvertTo-Json -Compress)) 'Signed assembly identity changed'
    $record = [ordered]@{
        schema = 1; source = $source; tag = $tag; run_id = $runId; run_attempt = $runAttempt
        policy_sha256 = $policyHash; input_dll_sha256 = $inputHash; public_key_sha256 = $expectedPublicHash
        signed_dll_sha256 = Get-Digest $signedBytes; signer_sha256 = $expectedToolHash; tool_version = $expectedToolVersion
    }
    $recordBytes = [Text.UTF8Encoding]::new($false).GetBytes(($record | ConvertTo-Json -Compress) + "`n")
    $receiptStream = [IO.File]::Open($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $ownedReceipt = $true
    try { $receiptStream.Write($recordBytes); $receiptStream.Flush($true) }
    finally { $receiptStream.Dispose() }
    $completed = $true
}
catch {
    # Never emit caught exception text, process output, the secret, or key path.
    [Console]::Error.WriteLine('Plugins signing failed; no valid receipt is available.')
    throw 'Plugins signing failed; no valid receipt is available.'
}
finally {
    if ($null -ne $keyStream) { $keyStream.Dispose() }
    if ($null -ne $keyBytes) { [Array]::Clear($keyBytes, 0, $keyBytes.Length) }
    foreach ($stream in $lockedInputs) { $stream.Dispose() }
    try {
        if ($ownedKey) { [IO.File]::Delete($keyPath) }
        if (-not $completed) {
            if ($ownedReceipt) { [IO.File]::Delete($receiptPath) }
            if ($ownedOutput) { [IO.File]::Delete($outputPath) }
        }
    }
    catch { throw 'Plugins signing cleanup failed; discard the signing worker and its outputs.' }
}
