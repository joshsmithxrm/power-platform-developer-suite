# PPDS.Migration.Cli

High-performance Dataverse data migration CLI tool. Part of the [PPDS SDK](../../README.md).

## Installation

```bash
# Global install
dotnet tool install --global PPDS.Migration.Cli

# Local install (in project)
dotnet tool install PPDS.Migration.Cli

# Verify
ppds-migrate --version
```

## Commands

| Command | Description |
|---------|-------------|
| `export` | Export data from Dataverse to a ZIP file |
| `import` | Import data from a ZIP file into Dataverse |
| `analyze` | Analyze schema and display dependency graph |
| `migrate` | Migrate data from source to target environment |
| `schema generate` | Generate schema from Dataverse metadata |
| `schema list` | List available entities |
| `users generate` | Generate user mapping file for cross-environment migration |

## Authentication

The CLI supports three authentication modes via the `--auth` option:

| Mode | Flag | Description | Use Case |
|------|------|-------------|----------|
| **Interactive** | `--auth interactive` (default) | Device code flow - opens browser | Development, ad-hoc usage |
| **Environment Variables** | `--auth env` | Uses `DATAVERSE__*` environment variables | CI/CD pipelines |
| **Managed Identity** | `--auth managed` | Azure Managed Identity | Azure-hosted workloads |

### Interactive Authentication (Default)

```bash
ppds-migrate export --url https://contoso.crm.dynamics.com --schema schema.xml --output data.zip
```

The CLI will display a device code and prompt you to authenticate in a browser.

### Environment Variables (CI/CD)

Set these environment variables, then use `--auth env`:

| Variable | Description | Required |
|----------|-------------|----------|
| `DATAVERSE__URL` | Environment URL | Yes |
| `DATAVERSE__CLIENTID` | Azure AD Application ID | Yes |
| `DATAVERSE__CLIENTSECRET` | Azure AD Client Secret | Yes |
| `DATAVERSE__TENANTID` | Azure AD Tenant ID | Optional |

```bash
# Set environment variables
export DATAVERSE__URL="https://contoso.crm.dynamics.com"
export DATAVERSE__CLIENTID="00000000-0000-0000-0000-000000000000"
export DATAVERSE__CLIENTSECRET="your-secret"

# Run with env auth
ppds-migrate export --auth env --schema schema.xml --output data.zip
```

GitHub Actions example:
```yaml
env:
  DATAVERSE__URL: ${{ vars.DATAVERSE_URL }}
  DATAVERSE__CLIENTID: ${{ vars.DATAVERSE_CLIENT_ID }}
  DATAVERSE__CLIENTSECRET: ${{ secrets.DATAVERSE_CLIENT_SECRET }}

steps:
  - run: ppds-migrate export --auth env --schema schema.xml --output data.zip
```

### Managed Identity (Azure-Hosted)

For Azure Functions, App Service, or VMs with managed identity:

```bash
ppds-migrate export --auth managed --url https://contoso.crm.dynamics.com --schema schema.xml --output data.zip
```

## Usage

### Global Options

All commands support these options:

| Option | Description |
|--------|-------------|
| `--url` | Dataverse environment URL (e.g., `https://org.crm.dynamics.com`) |
| `--auth` | Authentication mode: `interactive` (default), `env`, `managed` |

### Export

```bash
ppds-migrate export --url https://contoso.crm.dynamics.com --schema ./schema.xml --output ./data.zip
```

Options:
- `--url` (required) - Dataverse environment URL
- `--schema`, `-s` (required) - Path to schema.xml file
- `--output`, `-o` (required) - Output ZIP file path
- `--parallel` - Degree of parallelism (default: 16)
- `--page-size` - FetchXML page size (default: 5000)
- `--json` - Output progress as JSON
- `--verbose`, `-v` - Verbose output
- `--debug` - Diagnostic logging output

### Import

```bash
ppds-migrate import --url https://contoso.crm.dynamics.com --data ./data.zip --mode Upsert
```

Options:
- `--url` (required) - Dataverse environment URL
- `--data`, `-d` (required) - Path to data.zip file
- `--mode` - Import mode: `Create`, `Update`, or `Upsert` (default: Upsert)
- `--user-mapping`, `-u` - Path to user mapping XML file
- `--strip-owner-fields` - Strip ownership fields, let Dataverse assign current user
- `--bypass-plugins` - Bypass custom plugin execution
- `--bypass-flows` - Bypass Power Automate flows
- `--continue-on-error` - Continue on individual record failures
- `--json` - Output progress as JSON
- `--verbose`, `-v` - Verbose output
- `--debug` - Diagnostic logging output

### Analyze

```bash
# No connection required - analyzes schema file locally
ppds-migrate analyze --schema ./schema.xml
```

### Generate Schema

```bash
ppds-migrate schema generate --url https://contoso.crm.dynamics.com \
  --entities account,contact \
  --output ./schema.xml
```

Options:
- `--url` (required) - Dataverse environment URL
- `--entities`, `-e` (required) - Entity logical names (comma-separated or multiple flags)
- `--output`, `-o` (required) - Output schema file path
- `--include-audit-fields` - Include audit fields (createdon, createdby, modifiedon, modifiedby, overriddencreatedon)
- `--disable-plugins` - Set disableplugins=true on all entities
- `--include-attributes`, `-a` - Only include these attributes (whitelist)
- `--exclude-attributes` - Exclude these attributes (blacklist)
- `--json` - Output progress as JSON
- `--verbose`, `-v` - Verbose output
- `--debug` - Diagnostic logging output

### List Entities

```bash
ppds-migrate schema list --url https://contoso.crm.dynamics.com --filter "account*"
```

Options:
- `--url` (required) - Dataverse environment URL
- `--filter`, `-f` - Filter entities by name pattern
- `--custom-only` - Show only custom entities
- `--json` - Output as JSON

### Generate User Mapping

Generate a user mapping file for cross-environment migrations. Maps users by Azure AD Object ID (preferred) or domain name fallback.

```bash
ppds-migrate users generate \
  --source-url "https://dev.crm.dynamics.com" \
  --target-url "https://qa.crm.dynamics.com" \
  --output ./user-mapping.xml
```

Options:
- `--source-url` (required) - Source environment URL
- `--target-url` (required) - Target environment URL
- `--output`, `-o` (required) - Output user mapping XML file path
- `--analyze` - Preview user differences without generating file
- `--json` - Output as JSON
- `--verbose`, `-v` - Verbose output
- `--debug` - Diagnostic logging output

Use the generated mapping file with import:
```bash
ppds-migrate import --url https://qa.crm.dynamics.com \
  --data ./data.zip \
  --user-mapping ./user-mapping.xml
```

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Failure (operation could not complete) |
| 2 | Invalid arguments |

## JSON Progress Output

The `--json` flag enables structured JSON output for tool integration. This format is a **public contract** used by [PPDS.Tools](https://github.com/joshsmithxrm/ppds-tools) PowerShell cmdlets.

```bash
ppds-migrate export --url https://contoso.crm.dynamics.com --schema ./schema.xml --output ./data.zip --json
```

**Output format (one JSON object per line):**

```json
{"phase":"analyzing","message":"Parsing schema...","timestamp":"2025-12-19T10:30:00Z"}
{"phase":"export","entity":"account","current":450,"total":1000,"rps":287.5,"timestamp":"2025-12-19T10:30:15Z"}
{"phase":"complete","duration":"00:05:23","recordsProcessed":1505,"errors":0,"timestamp":"2025-12-19T10:35:23Z"}
```

**Phases:**

| Phase | Fields | Description |
|-------|--------|-------------|
| `analyzing` | `message` | Schema parsing and dependency analysis |
| `export` | `entity`, `current`, `total`, `rps` | Exporting entity data |
| `import` | `entity`, `current`, `total`, `rps`, `tier` | Importing entity data |
| `deferred` | `entity`, `field`, `current`, `total` | Updating deferred lookup fields |
| `complete` | `duration`, `recordsProcessed`, `errors` | Operation finished |
| `error` | `message` | Error occurred |

## Security Best Practices

1. **Never pass secrets as CLI arguments** - Use environment variables or managed identity
2. **Use CI/CD secrets** - Store credentials in GitHub Actions secrets or Azure DevOps variables
3. **Use managed identity in Azure** - Avoid storing credentials entirely
4. **Rotate secrets regularly** - Follow your organization's credential rotation policy

## Related

- [PPDS.Dataverse](../PPDS.Dataverse/) - High-performance Dataverse connectivity
- [PPDS.Tools](https://github.com/joshsmithxrm/ppds-tools) - PowerShell cmdlets that wrap this CLI
