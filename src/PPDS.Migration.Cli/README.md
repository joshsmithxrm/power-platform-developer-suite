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
| `config list` | List available environments from configuration |

## Configuration

The CLI uses the same configuration model as [PPDS.Dataverse](../PPDS.Dataverse/), following standard .NET configuration patterns.

### Configuration Sources (Priority Order)

Configuration is loaded from multiple sources, with later sources overriding earlier ones:

1. **appsettings.json** - Base configuration file
2. **appsettings.{Environment}.json** - Environment-specific overrides (e.g., `appsettings.Development.json`)
3. **User Secrets** - Development-time secrets (via `--secrets-id`)
4. **Environment variables** - Runtime overrides (`Dataverse__*` format)

### appsettings.json Structure

```json
{
  "Dataverse": {
    "DefaultEnvironment": "Dev",
    "Environments": {
      "Dev": {
        "Url": "https://contoso-dev.crm.dynamics.com",
        "Connections": [
          {
            "Name": "Primary",
            "ClientId": "00000000-0000-0000-0000-000000000000"
          }
        ]
      },
      "QA": {
        "Url": "https://contoso-qa.crm.dynamics.com",
        "Connections": [
          {
            "Name": "Primary",
            "ClientId": "00000000-0000-0000-0000-000000000000"
          }
        ]
      },
      "Prod": {
        "Url": "https://contoso.crm.dynamics.com",
        "Connections": [
          {
            "Name": "Primary",
            "ClientId": "00000000-0000-0000-0000-000000000000"
          }
        ]
      }
    }
  }
}
```

### User Secrets (Local Development)

Store sensitive credentials in User Secrets, not in appsettings.json:

```bash
# Initialize User Secrets (in your project directory)
dotnet user-secrets init

# Set credentials for each environment
dotnet user-secrets set "Dataverse:Environments:Dev:Connections:0:ClientSecret" "your-dev-secret"
dotnet user-secrets set "Dataverse:Environments:QA:Connections:0:ClientSecret" "your-qa-secret"
dotnet user-secrets set "Dataverse:Environments:Prod:Connections:0:ClientSecret" "your-prod-secret"
```

### Environment Variables (CI/CD)

For CI/CD pipelines, use environment variables with the `Dataverse__` prefix (double underscore):

```bash
# Single environment
export Dataverse__Environments__Dev__Url="https://contoso-dev.crm.dynamics.com"
export Dataverse__Environments__Dev__Connections__0__ClientId="your-client-id"
export Dataverse__Environments__Dev__Connections__0__ClientSecret="your-secret"
```

GitHub Actions example:
```yaml
env:
  Dataverse__Environments__Dev__Url: ${{ vars.DEV_URL }}
  Dataverse__Environments__Dev__Connections__0__ClientId: ${{ vars.DEV_CLIENT_ID }}
  Dataverse__Environments__Dev__Connections__0__ClientSecret: ${{ secrets.DEV_CLIENT_SECRET }}
```

### Cross-Process Invocation

When calling the CLI from another .NET application (e.g., the demo project), use `--secrets-id` to share User Secrets:

```bash
ppds-migrate export --env Dev --secrets-id ppds-dataverse-demo --schema schema.xml --output data.zip
```

This allows the CLI to read secrets from the calling application's User Secrets store.

## Usage

### Export

```bash
ppds-migrate export --env Dev --schema ./schema.xml --output ./data.zip
```

Options:
- `--env` (required) - Environment name from configuration
- `--schema`, `-s` (required) - Path to schema.xml file
- `--output`, `-o` (required) - Output ZIP file path
- `--config` - Path to configuration file (default: appsettings.json in CWD)
- `--secrets-id` - User Secrets ID for cross-process secret sharing
- `--parallel` - Degree of parallelism (default: processor count * 2)
- `--page-size` - FetchXML page size (default: 5000)
- `--include-files` - Export file attachments
- `--json` - Output progress as JSON
- `--verbose`, `-v` - Verbose output

### Import

```bash
ppds-migrate import --env Dev --data ./data.zip
```

Options:
- `--env` (required) - Environment name from configuration
- `--data`, `-d` (required) - Path to data.zip file
- `--config` - Path to configuration file
- `--secrets-id` - User Secrets ID
- `--batch-size` - Records per batch (default: 1000)
- `--bypass-plugins` - Bypass custom plugin execution
- `--bypass-flows` - Bypass Power Automate flows
- `--continue-on-error` - Continue on individual record failures
- `--mode` - Import mode: Create, Update, or Upsert (default: Upsert)
- `--user-mapping`, `-u` - Path to user mapping XML file
- `--json` - Output progress as JSON
- `--verbose`, `-v` - Verbose output

### Analyze

```bash
# No connection required - analyzes schema file locally
ppds-migrate analyze --schema ./schema.xml --output-format json
```

### Migrate

```bash
ppds-migrate migrate --source-env Dev --target-env Prod --schema ./schema.xml
```

Options:
- `--source-env` (required) - Source environment name
- `--target-env` (required) - Target environment name
- `--schema`, `-s` (required) - Path to schema.xml file
- `--config` - Path to configuration file
- `--secrets-id` - User Secrets ID
- `--temp-dir` - Temporary directory for intermediate data
- `--batch-size` - Records per batch (default: 1000)
- `--bypass-plugins` - Bypass plugins on target
- `--bypass-flows` - Bypass flows on target
- `--json` - Output progress as JSON
- `--verbose`, `-v` - Verbose output

### Generate Schema

```bash
ppds-migrate schema generate --env Dev --entities account,contact --output ./schema.xml
```

Options:
- `--env` (required) - Environment name from configuration
- `--entities`, `-e` (required) - Entity logical names (comma-separated or multiple flags)
- `--output`, `-o` (required) - Output schema file path
- `--config` - Path to configuration file
- `--secrets-id` - User Secrets ID
- `--include-system-fields` - Include system fields (createdon, modifiedon, etc.)
- `--include-relationships` - Include relationship definitions (default: true)
- `--disable-plugins` - Set disableplugins=true on all entities
- `--include-attributes`, `-a` - Only include these attributes (whitelist)
- `--exclude-attributes` - Exclude these attributes (blacklist)
- `--exclude-patterns` - Exclude attributes matching patterns (e.g., 'new_*')
- `--json` - Output progress as JSON
- `--verbose`, `-v` - Verbose output

### List Entities

```bash
ppds-migrate schema list --env Dev --filter "account*"
```

### List Environments

```bash
ppds-migrate config list
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
ppds-migrate export --env Dev --schema ./schema.xml --output ./data.zip --json
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

1. **Never commit secrets to source control** - Use User Secrets for local development
2. **Use CI/CD secrets** - Store credentials in GitHub Actions secrets or Azure DevOps variables
3. **Use Key Vault in production** - For deployed scenarios, integrate with Azure Key Vault
4. **Rotate secrets regularly** - Follow your organization's credential rotation policy

## Related

- [PPDS.Dataverse](../PPDS.Dataverse/) - High-performance Dataverse connectivity (same configuration model)
- [PPDS.Tools](https://github.com/joshsmithxrm/ppds-tools) - PowerShell cmdlets that wrap this CLI
