# PPDS CLI

Unified command-line tool for Dataverse operations. Part of the [PPDS SDK](../../README.md).

## Installation

```bash
# Global install
dotnet tool install --global PPDS.Cli

# Verify
ppds --help
```

## Quick Start

```bash
# 1. Create an auth profile (opens browser for login)
ppds auth create --name dev

# 2. Select your environment
ppds env select --environment "My Environment"

# 3. Run commands (uses active profile automatically)
ppds data export --schema schema.xml --output data.zip
```

## Command Structure

```
ppds
├── auth      Authentication profile management
├── env       Environment discovery and selection
├── data      Data operations (export, import, copy, analyze)
├── schema    Schema generation and entity listing
└── users     User mapping for cross-environment migrations
```

---

## Authentication

The CLI uses stored authentication profiles. Create a profile once, then all commands use it automatically.

### Authentication Methods

| Method | Flags | Use Case |
|--------|-------|----------|
| Interactive Browser | (default) | Development - opens browser |
| Device Code | `--deviceCode` | Headless/SSH environments |
| Client Secret | `--applicationId` + `--clientSecret` + `--tenant` | CI/CD pipelines |
| Certificate File | `--applicationId` + `--certificateDiskPath` + `--tenant` | Automated jobs |
| Certificate Store | `--applicationId` + `--certificateThumbprint` + `--tenant` | Windows servers |
| Managed Identity | `--managedIdentity` | Azure-hosted workloads |
| GitHub OIDC | `--githubFederated` + `--applicationId` + `--tenant` | GitHub Actions |
| Azure DevOps OIDC | `--azureDevOpsFederated` + `--applicationId` + `--tenant` | Azure Pipelines |

### Examples

```bash
# Interactive auth (default - opens browser, or device code in headless environments)
ppds auth create --name dev

# Service principal for CI/CD
ppds auth create --name ci \
  --applicationId "00000000-0000-0000-0000-000000000000" \
  --clientSecret "$CLIENT_SECRET" \
  --tenant "00000000-0000-0000-0000-000000000000" \
  --environment "https://myorg.crm.dynamics.com"

# Certificate auth
ppds auth create --name prod \
  --applicationId "00000000-0000-0000-0000-000000000000" \
  --certificateDiskPath "/path/to/cert.pfx" \
  --certificatePassword "$CERT_PASSWORD" \
  --tenant "00000000-0000-0000-0000-000000000000"

# Managed Identity (Azure-hosted)
ppds auth create --name azure --managedIdentity

# GitHub Actions OIDC
ppds auth create --name github \
  --githubFederated \
  --applicationId "00000000-0000-0000-0000-000000000000" \
  --tenant "00000000-0000-0000-0000-000000000000"
```

---

## Commands Reference

### `ppds auth`

Manage authentication profiles stored on this computer.

| Command | Description |
|---------|-------------|
| `ppds auth create` | Create a new authentication profile |
| `ppds auth list` | List all profiles |
| `ppds auth select` | Set the active profile |
| `ppds auth delete` | Delete a profile |
| `ppds auth update` | Update profile settings |
| `ppds auth name` | Rename a profile |
| `ppds auth clear` | Delete all profiles and token cache |
| `ppds auth who` | Show current profile details |

### `ppds env`

Discover and select Dataverse environments.

| Command | Description |
|---------|-------------|
| `ppds env list` | List available environments via Global Discovery |
| `ppds env select` | Select environment for the current profile |
| `ppds env who` | Verify connection and show environment info |

```bash
# List environments accessible to current profile
ppds env list

# Select by name (partial match supported)
ppds env select --environment "Dev"

# Select by URL
ppds env select --environment "https://myorg.crm.dynamics.com"

# Verify connection
ppds env who
```

### `ppds data`

Data migration operations.

| Command | Description |
|---------|-------------|
| `ppds data export` | Export data to a ZIP file |
| `ppds data import` | Import data from a ZIP file |
| `ppds data copy` | Export + import in one operation |
| `ppds data analyze` | Analyze schema (offline, no connection) |

#### Export

```bash
ppds data export --schema schema.xml --output data.zip
```

Options:
- `--schema`, `-s` (required) - Path to schema.xml
- `--output`, `-o` (required) - Output ZIP file path
- `--profile`, `-p` - Profile name(s), comma-separated for pooling
- `--environment`, `-env` - Override environment URL
- `--parallel` - Max concurrent entity exports (default: CPU * 2)
- `--batch-size` - Records per API request (default: 5000)
- `--json`, `-j` - JSON output for tool integration
- `--verbose`, `-v` - Verbose logging
- `--debug` - Diagnostic logging

#### Import

```bash
ppds data import --data data.zip --mode Upsert
```

Options:
- `--data`, `-d` (required) - Path to data.zip
- `--mode`, `-m` - Import mode: Create, Update, Upsert (default: Upsert)
- `--profile`, `-p` - Profile name(s), comma-separated for pooling
- `--environment`, `-env` - Override environment URL
- `--user-mapping`, `-u` - User mapping XML file
- `--strip-owner-fields` - Let Dataverse assign current user as owner
- `--skip-missing-columns` - Skip columns not in target environment
- `--bypass-plugins` - Bypass plugins: sync, async, or all
- `--bypass-flows` - Bypass Power Automate flows
- `--continue-on-error` - Continue on individual record failures
- `--json`, `-j` - JSON output for tool integration
- `--verbose`, `-v` - Verbose logging
- `--debug` - Diagnostic logging

#### Copy

Export from source and import to target in one operation:

```bash
ppds data copy \
  --schema schema.xml \
  --source-env "Dev" \
  --target-env "QA"
```

#### Analyze

Analyze schema offline (no connection required):

```bash
ppds data analyze --schema schema.xml
```

### `ppds schema`

Schema generation and entity discovery.

| Command | Description |
|---------|-------------|
| `ppds schema generate` | Generate migration schema from metadata |
| `ppds schema list` | List available entities |

#### Generate

```bash
ppds schema generate --entities account,contact --output schema.xml
```

Options:
- `--entities`, `-e` (required) - Entity logical names (comma-separated)
- `--output`, `-o` (required) - Output schema file path
- `--include-audit-fields` - Include createdon, createdby, etc.
- `--disable-plugins` - Set disableplugins=true on all entities
- `--include-attributes`, `-a` - Whitelist specific attributes
- `--exclude-attributes` - Blacklist specific attributes

#### List

```bash
# List all entities
ppds schema list

# Filter by pattern
ppds schema list --filter "account*"

# Custom entities only
ppds schema list --custom-only

# Show detailed field info for an entity
ppds schema list --entity account
```

### `ppds users`

User mapping for cross-environment migrations.

```bash
ppds users generate \
  --source-env "Dev" \
  --target-env "QA" \
  --output user-mapping.xml
```

Options:
- `--source-env`, `-se` (required) - Source environment
- `--target-env`, `-te` (required) - Target environment
- `--output`, `-o` (required) - Output mapping file path
- `--source-profile`, `-sp` - Profile for source (default: active)
- `--target-profile`, `-tp` - Profile for target (default: active)
- `--analyze` - Preview without generating file

Use the mapping file with import:
```bash
ppds data import --data data.zip --user-mapping user-mapping.xml
```

---

## High-Throughput Pooling

For large migrations, use multiple Application User profiles to multiply API quota:

```bash
# Create profiles for each Application User
ppds auth create --name app1 --applicationId $ID1 --clientSecret $SECRET1 --tenant $TENANT --environment "Prod"
ppds auth create --name app2 --applicationId $ID2 --clientSecret $SECRET2 --tenant $TENANT --environment "Prod"
ppds auth create --name app3 --applicationId $ID3 --clientSecret $SECRET3 --tenant $TENANT --environment "Prod"

# Use all three for 3x API quota
ppds data import --data large-dataset.zip --profile app1,app2,app3
```

Each Application User gets 6,000 requests per 5-minute window. Three users = 18,000 requests per 5-minute window.

---

## CI/CD Examples

### GitHub Actions

```yaml
- name: Install PPDS CLI
  run: dotnet tool install --global PPDS.Cli

- name: Create auth profile
  run: |
    ppds auth create --name ci \
      --applicationId "${{ vars.DATAVERSE_CLIENT_ID }}" \
      --clientSecret "${{ secrets.DATAVERSE_CLIENT_SECRET }}" \
      --tenant "${{ vars.DATAVERSE_TENANT_ID }}" \
      --environment "${{ vars.DATAVERSE_URL }}"

- name: Export data
  run: ppds data export --schema schema.xml --output data.zip

- name: Import data
  run: ppds data import --data data.zip --mode Upsert
```

### GitHub Actions with OIDC (No Secrets)

```yaml
- name: Create auth profile (OIDC)
  run: |
    ppds auth create --name ci \
      --githubFederated \
      --applicationId "${{ vars.DATAVERSE_CLIENT_ID }}" \
      --tenant "${{ vars.DATAVERSE_TENANT_ID }}" \
      --environment "${{ vars.DATAVERSE_URL }}"
```

---

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Failure |
| 2 | Invalid arguments |

---

## JSON Output

The `--json` flag enables structured JSON output for tool integration:

```bash
ppds data export --schema schema.xml --output data.zip --json
```

Output format (one JSON object per line):
```json
{"phase":"analyzing","message":"Parsing schema...","timestamp":"2025-12-19T10:30:00Z"}
{"phase":"export","entity":"account","current":450,"total":1000,"rps":287.5}
{"phase":"complete","duration":"00:05:23","recordsProcessed":1505,"errors":0}
```

---

## Profile Storage

Profiles are stored locally:
- **Windows:** `%LOCALAPPDATA%\PPDS\profiles.json`
- **macOS/Linux:** `~/.ppds/profiles.json`

Secrets are encrypted using platform-native encryption (DPAPI on Windows).

---

## Related

- [PPDS.Auth](../PPDS.Auth/) - Authentication library used by the CLI
- [PPDS.Migration](../PPDS.Migration/) - Migration engine library
- [PPDS.Dataverse](../PPDS.Dataverse/) - Connection pooling and bulk operations
