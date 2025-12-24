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

## Connection Configuration

All connection details are provided via environment variables:

### Single Environment (export, import, schema)

| Variable | Description |
|----------|-------------|
| `PPDS_URL` | Dataverse environment URL |
| `PPDS_CLIENT_ID` | Azure AD application (client) ID |
| `PPDS_CLIENT_SECRET` | Client secret value |
| `PPDS_TENANT_ID` | (Optional) Azure AD tenant ID |

### Source/Target (migrate)

| Variable | Description |
|----------|-------------|
| `PPDS_SOURCE_URL` | Source environment URL |
| `PPDS_SOURCE_CLIENT_ID` | Source client ID |
| `PPDS_SOURCE_CLIENT_SECRET` | Source client secret |
| `PPDS_SOURCE_TENANT_ID` | (Optional) Source tenant ID |
| `PPDS_TARGET_URL` | Target environment URL |
| `PPDS_TARGET_CLIENT_ID` | Target client ID |
| `PPDS_TARGET_CLIENT_SECRET` | Target client secret |
| `PPDS_TARGET_TENANT_ID` | (Optional) Target tenant ID |

## Usage

### Export

```bash
# Set connection via environment variables
export PPDS_URL="https://org.crm.dynamics.com"
export PPDS_CLIENT_ID="your-client-id"
export PPDS_CLIENT_SECRET="your-secret"
export PPDS_TENANT_ID="your-tenant-id"

# Export data
ppds-migrate export --schema ./schema.xml --output ./data.zip
```

### Import

```bash
export PPDS_URL="https://org.crm.dynamics.com"
export PPDS_CLIENT_ID="your-client-id"
export PPDS_CLIENT_SECRET="your-secret"

ppds-migrate import --data ./data.zip --bypass-plugins
```

### Analyze

```bash
# No connection required for schema analysis
ppds-migrate analyze --schema ./schema.xml --output-format json
```

### Migrate

```bash
# Set source environment
export PPDS_SOURCE_URL="https://source.crm.dynamics.com"
export PPDS_SOURCE_CLIENT_ID="source-client-id"
export PPDS_SOURCE_CLIENT_SECRET="source-secret"

# Set target environment
export PPDS_TARGET_URL="https://target.crm.dynamics.com"
export PPDS_TARGET_CLIENT_ID="target-client-id"
export PPDS_TARGET_CLIENT_SECRET="target-secret"

ppds-migrate migrate --schema ./schema.xml
```

### Generate Schema

```bash
export PPDS_URL="https://org.crm.dynamics.com"
export PPDS_CLIENT_ID="your-client-id"
export PPDS_CLIENT_SECRET="your-secret"

ppds-migrate schema generate --entities account,contact --output ./schema.xml
```

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Failure (operation could not complete) |
| 2 | Invalid arguments |

## JSON Progress Output

The `--json` flag enables structured JSON output for tool integration. This format is a **public contract** used by [PPDS.Tools](https://github.com/joshsmithxrm/ppds-tools) PowerShell cmdlets and potentially other integrations.

```bash
ppds-migrate export --schema ./schema.xml --output ./data.zip --json
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

## Security

Connection credentials are provided exclusively via environment variables to avoid exposure in:
- Command-line argument lists (visible in process listings)
- Shell history files
- CI/CD logs

**Best Practices:**

1. Set variables in CI/CD secrets, not in scripts
2. Use Azure Key Vault or similar for production credentials
3. Rotate secrets regularly

## Related

- [PPDS.Tools](https://github.com/joshsmithxrm/ppds-tools) - PowerShell cmdlets that wrap this CLI
- [PPDS.Dataverse](../PPDS.Dataverse/) - High-performance Dataverse connectivity
