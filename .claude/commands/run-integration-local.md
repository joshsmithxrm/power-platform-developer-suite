# Run Integration Tests Locally

Loads `.env.local` credentials and runs integration tests with appropriate filters.

## Usage

`/run-integration-local [filter]`

Examples:
- `/run-integration-local` - Run all integration tests
- `/run-integration-local FullyQualifiedName~Cli` - Run CLI E2E tests only
- `/run-integration-local FullyQualifiedName~Authentication` - Run auth tests only

## Process

### 1. Verify Environment File

Check for `.env.local`:

```powershell
if (-not (Test-Path ".env.local")) {
    Write-Error ".env.local not found. Copy from .env.example and configure credentials."
    exit 1
}
```

If missing, show error and stop.

### 2. Load Environment Variables

```powershell
Get-Content .env.local | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') {
        $name = $matches[1].Trim()
        $value = $matches[2].Trim()
        if ($name -and -not $name.StartsWith('#')) {
            # Remove surrounding quotes if present
            if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
                ($value.StartsWith("'") -and $value.EndsWith("'"))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
            Write-Host "  Loaded: $name" -ForegroundColor DarkGray
        }
    }
}
```

### 3. Verify Required Credentials

Check that required variables are set:

```powershell
$required = @('DATAVERSE_URL', 'PPDS_TEST_APP_ID', 'PPDS_TEST_CLIENT_SECRET', 'PPDS_TEST_TENANT_ID')
$missing = $required | Where-Object { -not [Environment]::GetEnvironmentVariable($_) }
if ($missing) {
    Write-Error "Missing required variables: $($missing -join ', ')"
    exit 1
}
```

If any missing, show error listing which ones and stop.

### 4. Run Tests

Default filter (all integration tests):

```bash
dotnet test --configuration Release --filter "Category=Integration" --verbosity normal
```

With custom filter (if provided):

```bash
dotnet test --configuration Release --filter "[user-provided-filter]" --verbosity normal
```

### 5. Report Results

After tests complete:
- If all pass: Report success count
- If failures: Show failed tests, suggest `/fix-tests` command

## Output Format

```
Run Integration Tests (Local)
=============================
Loading .env.local...
  Loaded: DATAVERSE_URL
  Loaded: PPDS_TEST_APP_ID
  Loaded: PPDS_TEST_CLIENT_SECRET
  Loaded: PPDS_TEST_TENANT_ID

[✓] Credentials configured

Running: dotnet test --filter "Category=Integration"
...

Results: 24 passed, 0 failed
```

## Behavior

| Situation | Action |
|-----------|--------|
| `.env.local` missing | Error with instructions to create from `.env.example` |
| Credentials incomplete | Error listing missing variables |
| Tests fail | Report failures, suggest `/fix-tests` |
| Tests pass | Report success |

## Common Filters

| Filter | Purpose |
|--------|---------|
| `Category=Integration` | All integration tests (default) |
| `FullyQualifiedName~Cli` | CLI E2E tests only |
| `FullyQualifiedName~Authentication` | Auth tests only |
| `FullyQualifiedName~BulkOperation` | Bulk operation tests |
| `FullyQualifiedName~Pooling` | Connection pool tests |

## When to Use

- Before pushing integration test changes
- Debugging CI failures locally
- Verifying Dataverse connectivity
- Testing new integration test coverage

## Related

- [docs/INTEGRATION_TESTING.md](docs/INTEGRATION_TESTING.md) - Full testing guide
- `/fix-tests` - Iteratively fix test failures
- `/pre-pr` - Full PR validation
