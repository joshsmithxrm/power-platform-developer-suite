Set-StrictMode -Version Latest

function Get-ValueHash([string] $Value) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant().TrimEnd('/')))).ToLowerInvariant()
}

function Assert-PreflightBinding([string] $Target, [string] $ApplicationId, [string] $TenantId) {
    $uri = [uri]$Target
    if ($uri.Scheme -ne 'https' -or $uri.Port -ne 443 -or $uri.UserInfo -or
        $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/') { throw 'TargetMismatch' }
    if ((Get-ValueHash $Target) -ne '6dd5d9f084b0b9689223b7924f4ee57324944008ad1c378fbffee9dede14200a' -or
        (Get-ValueHash $ApplicationId) -ne 'fa2e7b2d2d02fa62b27718bf268c5d7b064f3f602472a57c901cd61f2f126f13' -or
        (Get-ValueHash $TenantId) -ne '131876ad90ad84a1506e99b8435235b9d46564275fb852ff7986ee14bfd6a9fe') {
        throw 'TargetMismatch'
    }
}

function Get-PreflightReadPath {
    param(
        [ValidateSet('Identity','Organization','Caller','Package','Assembly','Type','Step','Account')]
        [string] $Operation,
        [guid] $UserId = [guid]::Empty
    )
    switch ($Operation) {
        'Identity' { '/api/data/v9.2/WhoAmI' }
        'Organization' { "/api/data/v9.2/RetrieveCurrentOrganization(AccessType=@p1)?@p1=Microsoft.Dynamics.CRM.EndpointAccessType'Default'" }
        'Caller' {
            if ($UserId -eq [guid]::Empty) { throw 'IdentityMismatch' }
            '/api/data/v9.2/systemusers(' + $UserId.ToString('D') + ')?$select=applicationid'
        }
        'Package' { '/api/data/v9.2/pluginpackages?$select=pluginpackageid&$filter=name eq ''ppds1450_61e24a05'' or uniquename eq ''ppds1450_61e24a05''&$top=2' }
        'Assembly' { '/api/data/v9.2/pluginassemblies?$select=pluginassemblyid&$filter=name eq ''Ppds1450Smoke_61e24a05''&$top=2' }
        'Type' { '/api/data/v9.2/plugintypes?$select=plugintypeid&$filter=typename eq ''Ppds1450Smoke.MarkerPlugin''&$top=2' }
        'Step' {
            $fetch = "<fetch top='2'><entity name='sdkmessageprocessingstep'><attribute name='sdkmessageprocessingstepid'/><filter><condition attribute='name' operator='eq' value='MarkerPlugin: Create of account'/></filter><link-entity name='plugintype' from='plugintypeid' to='eventhandler'><filter><condition attribute='typename' operator='eq' value='Ppds1450Smoke.MarkerPlugin'/></filter></link-entity></entity></fetch>"
            '/api/data/v9.2/sdkmessageprocessingsteps?fetchXml=' + [uri]::EscapeDataString($fetch)
        }
        'Account' { '/api/data/v9.2/accounts?$select=accountid&$filter=startswith(name,''PPDS1450_61e24a05_'')&$top=2' }
    }
}

function Invoke-PreflightGet {
    param([System.Net.Http.HttpClient] $Client, [string] $Target, [string] $AccessToken,
        [string] $Operation, [guid] $UserId = [guid]::Empty)
    # No caller-controlled URL, method, headers, body, batch, or pagination.
    $path = Get-PreflightReadPath -Operation $Operation -UserId $UserId
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Target.TrimEnd('/') + $path)
    $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $AccessToken)
    $request.Headers.Accept.ParseAdd('application/json')
    $request.Headers.Add('OData-Version', '4.0')
    $response = $null
    try {
        $response = $Client.SendAsync($request).GetAwaiter().GetResult()
        $null = $response.EnsureSuccessStatusCode()
        $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        ConvertFrom-Json -InputObject $json -AsHashtable
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
    }
}

function Invoke-Net48Preflight {
    param([scriptblock] $Read, [string] $ApplicationId)
    $identity = & $Read 'Identity' ([guid]::Empty)
    $org = (& $Read 'Organization' ([guid]::Empty)).Detail
    if ($identity.OrganizationId -ne $org.OrganizationId -or
        (Get-ValueHash $org.OrganizationId) -ne 'eb7fb25db9331b810dcfa6b3a594c12e13a1bd4f8ac914fb876eb1a6b2e5a626' -or
        (Get-ValueHash $org.EnvironmentId) -ne 'fac8340da9ea87cc695eb4a44f8123418d13961ff556438dc1fd8991beb66237' -or
        (Get-ValueHash $org.TenantId) -ne '131876ad90ad84a1506e99b8435235b9d46564275fb852ff7986ee14bfd6a9fe') {
        throw 'IdentityMismatch'
    }
    $caller = & $Read 'Caller' ([guid]$identity.UserId)
    if ([guid]$caller.applicationid -ne [guid]$ApplicationId) { throw 'IdentityMismatch' }
    if ([string]::IsNullOrWhiteSpace($org.OrganizationVersion) -or
        [string]::IsNullOrWhiteSpace([string]$org.OrganizationType)) { throw 'IncompleteIdentity' }

    $collisions = [ordered]@{}
    foreach ($operation in @('Package','Assembly','Type','Step','Account')) {
        $rows = & $Read $operation ([guid]::Empty)
        if (-not $rows.ContainsKey('value')) { throw 'IncompleteCollisionResult' }
        $collisions[$operation] = @($rows.value).Count -gt 0
    }
    $hasCollision = @($collisions.Values | Where-Object { $_ }).Count -gt 0
    [ordered]@{
        schemaVersion = 1
        checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        status = if ($hasCollision) { 'collision' } else { 'passed' }
        targetAndApplicationVerified = $true
        organizationType = $org.OrganizationType
        organizationVersion = $org.OrganizationVersion
        collisions = $collisions
        queryScope = 'Each collision query is capped at two rows; only presence is reported.'
        deploymentPreview = @{
            kind = 'Scope inferred from collision reads and the reviewed extracted configuration; not CLI dry-run.'
            package = 'ppds1450_61e24a05'
            packageVersion = '1.0.0'
            assembly = 'Ppds1450Smoke_61e24a05'
            pluginType = 'Ppds1450Smoke.MarkerPlugin'
            step = 'MarkerPlugin: Create of account'
            stage = 'PreOperation'
            mode = 'Synchronous'
            executionOrder = 1
            images = 0
            accounts = @('PPDS1450_61e24a05_first','PPDS1450_61e24a05_second')
            expected = if ($hasCollision) { 'Stop; existing data requires review.' } else { 'Create package and one step; later update that same package and step.' }
            cleanup = 'Only two recorded new account IDs and the recorded new package ID with its descendants.'
        }
        cliDryRun = 'Not invoked: this preflight excludes authentication-profile changes and all deployment paths.'
        mutationAuthorized = $false
        mutationCalls = 0
    }
}

Export-ModuleMember -Function Get-ValueHash,Assert-PreflightBinding,Get-PreflightReadPath,Invoke-PreflightGet,Invoke-Net48Preflight
