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
├── data      Data operations (export, import, copy, load, update, delete, schema, users)
├── plugins   Plugin registration management
├── query     Execute FetchXML and SQL queries
└── metadata  Browse entity and attribute metadata
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
| `ppds data schema` | Generate migration schema from metadata |
| `ppds data users` | Generate user mapping for cross-environment migrations |
| `ppds data load` | Load CSV data into an entity |
| `ppds data update` | Update records in an entity |
| `ppds data delete` | Delete records from an entity |
| `ppds data truncate` | Delete ALL records from an entity |

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
- `--output-format`, `-o` - Output format (Text or Json)
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
- `--output-format`, `-o` - Output format (Text or Json)
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

#### Schema

Generate migration schema from Dataverse metadata.

```bash
ppds data schema --entities account,contact --output schema.xml
```

Options:
- `--entities`, `-e` (required) - Entity logical names (comma-separated)
- `--output`, `-o` (required) - Output schema file path
- `--include-audit-fields` - Include createdon, createdby, etc.
- `--disable-plugins` - Set disableplugins=true on all entities
- `--include-attributes`, `-a` - Whitelist specific attributes
- `--exclude-attributes` - Blacklist specific attributes
- `--output-format`, `-o` - Output format (Text or Json)

#### Users

Generate user mapping for cross-environment migrations.

```bash
ppds data users \
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
- `--output-format`, `-o` - Output format (Text or Json)

Use the mapping file with import:
```bash
ppds data import --data data.zip --user-mapping user-mapping.xml
```

#### Load

Load CSV data into a Dataverse entity using bulk upsert.

```bash
ppds data load --entity account --file accounts.csv
```

Options:
- `--entity`, `-e` (required) - Target entity logical name
- `--file`, `-f` (required) - Path to CSV file
- `--key`, `-k` - Alternate key field(s) for upsert (comma-separated for composite)
- `--mapping`, `-m` - Path to column mapping JSON file
- `--generate-mapping` - Generate mapping template to file
- `--dry-run` - Validate without writing to Dataverse
- `--analyze` - Analyze mapping without loading data
- `--batch-size` - Records per batch (default: 100)
- `--bypass-plugins` - Bypass plugins: sync, async, or all
- `--bypass-flows` - Bypass Power Automate flows
- `--continue-on-error` - Continue on individual record failures
- `--force` - Skip unmatched columns (when auto-mapping is incomplete)
- `--profile`, `-p` - Profile name
- `--environment`, `-env` - Override environment URL
- `--output-format`, `-o` - Output format (Text or Json)

#### Update

Update records in a Dataverse entity. Unlike load (upsert), update fails if the record doesn't exist.

```bash
# Single record by GUID
ppds data update --entity account --id 00000000-0000-0000-0000-000000000001 --set "description=Updated"

# Single record by alternate key
ppds data update --entity account --key accountnumber=ACCT001 --set "description=Updated"

# Update multiple fields
ppds data update --entity account --id 00000000-0000-0000-0000-000000000001 --set "description=Updated,websiteurl=https://example.com"

# Bulk update from CSV file (each row has ID + fields to update)
ppds data update --entity account --file updates.csv --id-column accountid

# Query-based update
ppds data update --entity account --filter "statecode eq 0" --set "description=Active account"

# Dry-run (preview without updating)
ppds data update --entity account --filter "statecode eq 0" --set "description=Updated" --dry-run

# Non-interactive (for CI/CD)
ppds data update --entity account --filter "statecode eq 0" --set "description=Updated" --force
```

Options:
- `--entity`, `-e` (required) - Target entity logical name
- `--id` - Single record GUID to update
- `--key`, `-k` - Alternate key for lookup (field=value, comma-separated for composite)
- `--file` - Path to CSV file containing IDs and values to update
- `--id-column` - Column name for ID in CSV file (default: entity primary key)
- `--filter` - SQL-like filter expression to match records
- `--set`, `-s` - Field values to set (field=value, comma-separated)
- `--mapping`, `-m` - Path to column mapping JSON file (for --file mode)
- `--force` - Skip confirmation prompt (required for non-interactive)
- `--dry-run` - Preview records without updating
- `--limit` - Maximum records to update (fails if query returns more)
- `--batch-size` - Records per batch (default: 100)
- `--bypass-plugins` - Bypass plugins: sync, async, or all
- `--bypass-flows` - Bypass Power Automate flows
- `--continue-on-error` - Continue on individual record failures
- `--profile`, `-p` - Profile name
- `--environment`, `-env` - Override environment URL
- `--output-format`, `-o` - Output format (Text or Json)

**Safety Features:**
- All updates require confirmation by default (type `update N` where N is the count)
- Use `--force` to bypass confirmation in scripts/CI
- Use `--dry-run` to preview what would be updated

#### Delete

Delete records from a Dataverse entity. Supports single-record delete, bulk delete from file, and query-based delete.

```bash
# Single record by GUID
ppds data delete --entity account --id 00000000-0000-0000-0000-000000000001

# Single record by alternate key
ppds data delete --entity account --key accountnumber=ACCT001

# Bulk from CSV file
ppds data delete --entity account --file ids.csv --id-column accountid

# Query-based delete
ppds data delete --entity account --filter "name like '%test%'"

# Dry-run (preview without deleting)
ppds data delete --entity account --filter "name like '%test%'" --dry-run

# Non-interactive (for CI/CD)
ppds data delete --entity account --filter "name like '%test%'" --force --limit 500
```

Options:
- `--entity`, `-e` (required) - Target entity logical name
- `--id` - Single record GUID to delete
- `--key`, `-k` - Alternate key for lookup (field=value, comma-separated for composite)
- `--file` - Path to file containing record IDs (JSON array or CSV)
- `--id-column` - Column name for CSV file (default: entity primary key)
- `--filter` - SQL-like filter expression to match records
- `--force` - Skip confirmation prompt (required for non-interactive)
- `--dry-run` - Preview records without deleting
- `--limit` - Maximum records to delete (fails if query returns more)
- `--batch-size` - Records per batch (default: 100)
- `--bypass-plugins` - Bypass plugins: sync, async, or all
- `--bypass-flows` - Bypass Power Automate flows
- `--continue-on-error` - Continue on individual record failures
- `--profile`, `-p` - Profile name
- `--environment`, `-env` - Override environment URL
- `--output-format`, `-o` - Output format (Text or Json)

**Safety Features:**
- All deletes require confirmation by default (type `delete N` where N is the count)
- Use `--force` to bypass confirmation in scripts/CI
- Use `--dry-run` to preview what would be deleted
- Use `--limit` to cap the number of records (fails if query exceeds limit)

#### Truncate

Delete ALL records from an entity. Designed for dev/test scenarios where you need to clear an entire table.

```bash
# Preview record count (dry-run)
ppds data truncate --entity account --dry-run

# Delete all records (interactive confirmation required)
ppds data truncate --entity account

# Non-interactive (for CI/CD scripts)
ppds data truncate --entity account --force

# With custom batch size
ppds data truncate --entity account --batch-size 500 --force
```

Options:
- `--entity`, `-e` (required) - Target entity logical name
- `--dry-run` - Preview record count without deleting
- `--force` - Skip confirmation prompt (required for non-interactive)
- `--batch-size` - Records per delete batch (default: 1000, max: 1000)
- `--bypass-plugins` - Bypass plugins: sync, async, or all
- `--bypass-flows` - Bypass Power Automate flows
- `--continue-on-error` - Continue on individual record failures
- `--profile`, `-p` - Profile name
- `--environment`, `-env` - Override environment URL
- `--output-format`, `-o` - Output format (Text or Json)

**Safety Features:**
- Requires typing `TRUNCATE <entity> <count>` to confirm (e.g., `TRUNCATE account 5000`)
- Use `--force` to bypass confirmation in automation scripts
- Use `--dry-run` to preview record count before deleting
- Progress reporting shows deletion rate and estimated time remaining

**When to use Truncate vs Delete:**
- Use `truncate` when you need to delete ALL records from an entity
- Use `delete --filter` when you need to delete a subset of records
- Truncate is optimized for bulk deletion with progress reporting

### `ppds query`

Execute FetchXML and SQL queries against Dataverse.

| Command | Description |
|---------|-------------|
| `ppds query fetch` | Execute a FetchXML query |
| `ppds query sql` | Execute a SQL query (transpiled to FetchXML) |

#### Fetch (FetchXML)

Execute FetchXML queries directly:

```bash
# From argument
ppds query fetch '<fetch top="10"><entity name="account"><attribute name="name"/></entity></fetch>'

# From file
ppds query fetch --file queries/active-accounts.xml

# From stdin
cat query.xml | ppds query fetch --stdin

# Export to CSV file
ppds query fetch --file query.xml -f csv > results.csv
```

Options:
- `--file`, `-f` - Read FetchXML from file
- `--stdin` - Read FetchXML from stdin
- `--profile`, `-p` - Profile name
- `--environment`, `-env` - Override environment URL
- `--top`, `-t` - Limit number of results
- `--page` - Page number (1-based)
- `--paging-cookie` - Paging cookie for continuation
- `--count`, `-c` - Include total record count
- `--output-format` - Output format (Text, Json, or Csv)

#### SQL

Execute SQL queries (automatically transpiled to FetchXML):

```bash
# Basic query
ppds query sql "SELECT name, revenue FROM account WHERE statecode = 0"

# With limit and ordering
ppds query sql "SELECT name FROM account ORDER BY revenue DESC" --top 10

# Show the generated FetchXML
ppds query sql "SELECT name FROM account" --show-fetchxml

# Aggregates with GROUP BY
ppds query sql "SELECT statecode, COUNT(*) AS cnt FROM account GROUP BY statecode"

# JOIN queries
ppds query sql "SELECT a.name, c.fullname FROM account a INNER JOIN contact c ON a.primarycontactid = c.contactid"

# Export to CSV file
ppds query sql "SELECT name, revenue FROM account" -f csv > accounts.csv

# Export to JSON file
ppds query sql "SELECT * FROM contact" -f json > contacts.json
```

Options:
- `--file`, `-f` - Read SQL from file
- `--stdin` - Read SQL from stdin
- `--show-fetchxml` - Output the transpiled FetchXML instead of executing
- `--profile`, `-p` - Profile name
- `--environment`, `-env` - Override environment URL
- `--top`, `-t` - Limit number of results (applies if no TOP in query)
- `--page` - Page number (1-based)
- `--paging-cookie` - Paging cookie for continuation
- `--count`, `-c` - Include total record count
- `--output-format` - Output format (Text, Json, or Csv)

#### Supported SQL Syntax

| Feature | Example |
|---------|---------|
| SELECT columns | `SELECT name, accountid FROM account` |
| SELECT * | `SELECT * FROM account` |
| Column aliases | `SELECT name AS accountname FROM account` |
| Table aliases | `SELECT a.name FROM account a` |
| TOP N | `SELECT TOP 10 name FROM account` |
| DISTINCT | `SELECT DISTINCT name FROM account` |
| WHERE | `SELECT name FROM account WHERE statecode = 0` |
| Operators | `=`, `<>`, `!=`, `<`, `>`, `<=`, `>=` |
| LIKE | `WHERE name LIKE '%contoso%'` |
| IS NULL | `WHERE parentaccountid IS NULL` |
| IS NOT NULL | `WHERE parentaccountid IS NOT NULL` |
| IN | `WHERE statecode IN (0, 1, 2)` |
| AND / OR | `WHERE statecode = 0 AND revenue > 1000` |
| Parentheses | `WHERE (a = 1 OR b = 2) AND c = 3` |
| ORDER BY | `ORDER BY name ASC, revenue DESC` |
| INNER JOIN | `INNER JOIN contact c ON a.contactid = c.contactid` |
| LEFT JOIN | `LEFT JOIN contact c ON a.contactid = c.contactid` |
| COUNT(*) | `SELECT COUNT(*) FROM account` |
| COUNT(column) | `SELECT COUNT(accountid) FROM account` |
| COUNT(DISTINCT) | `SELECT COUNT(DISTINCT ownerid) FROM account` |
| SUM/AVG/MIN/MAX | `SELECT SUM(revenue) FROM account` |
| GROUP BY | `SELECT statecode, COUNT(*) FROM account GROUP BY statecode` |

#### Paging

For large result sets, use paging:

```bash
# First page
ppds query sql "SELECT name FROM account" --top 100

# Get the paging cookie from the result, then:
ppds query sql "SELECT name FROM account" --page 2 --paging-cookie "..."
```

### `ppds metadata`

Browse Dataverse entity and attribute metadata.

| Command | Description |
|---------|-------------|
| `ppds metadata entities` | List all entities |
| `ppds metadata entity <name>` | Get entity details |
| `ppds metadata attributes <entity>` | List attributes for an entity |
| `ppds metadata relationships <entity>` | List relationships for an entity |
| `ppds metadata keys <entity>` | List alternate keys for an entity |
| `ppds metadata optionsets` | List global option sets |
| `ppds metadata optionset <name>` | Get option set values |

#### Examples

```bash
# List all custom entities
ppds metadata entities --custom-only

# List entities matching a pattern (use * for contains)
ppds metadata entities --filter "*account*"

# Get details for a specific entity
ppds metadata entity account

# List lookup fields on an entity
ppds metadata attributes account --type Lookup

# View entity relationships
ppds metadata relationships account

# List alternate keys
ppds metadata keys account

# List global option sets
ppds metadata optionsets

# Get values for a specific option set
ppds metadata optionset statecode
```

Options:
- `--filter` - Filter by name pattern (supports `*` wildcard, e.g., `*account*`)
- `--custom-only` - Show only custom entities/attributes
- `--type` - Filter attributes by type (String, Lookup, DateTime, etc.)
- `--output-format`, `-o` - Output format (Text or Json)

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

## Global Options

These options are available on all commands:

| Option | Short | Description |
|--------|-------|-------------|
| `--output-format` | `-o` | Output format: `text` (default) or `json` |
| `--quiet` | `-q` | Show only warnings and errors |
| `--verbose` | `-v` | Show debug messages |
| `--debug` | | Show trace-level diagnostics |
| `--correlation-id` | | Correlation ID for distributed tracing |

---

## Exit Codes

| Code | Name | Description |
|------|------|-------------|
| 0 | Success | Operation completed successfully |
| 1 | PartialSuccess | Some items failed but operation completed |
| 2 | Failure | Operation failed |
| 3 | InvalidArguments | Invalid command-line arguments |
| 4 | ConnectionError | Failed to connect to Dataverse |
| 5 | AuthError | Authentication failed |
| 6 | NotFoundError | Resource not found (profile, environment, file) |

---

## Error Codes

Structured errors include a hierarchical code for programmatic handling:

| Category | Codes |
|----------|-------|
| `Auth.*` | ProfileNotFound, Expired, InvalidCredentials, InsufficientPermissions, NoActiveProfile, ProfileExists, CertificateError |
| `Connection.*` | Failed, Throttled, Timeout, EnvironmentNotFound, AmbiguousEnvironment, InvalidEnvironmentUrl |
| `Validation.*` | RequiredField, InvalidValue, FileNotFound, DirectoryNotFound, SchemaInvalid, InvalidArguments |
| `Operation.*` | NotFound, Duplicate, Dependency, PartialFailure, Cancelled, Internal, NotSupported |
| `Query.*` | ParseError, InvalidFetchXml, ExecutionFailed |

---

## JSON Output

The `--output-format json` option enables structured JSON output for tool integration:

```bash
ppds data export --schema schema.xml --output data.zip --output-format json
```

### Command Results

All commands return a consistent JSON envelope:

```json
{
  "version": "1.0",
  "success": true,
  "data": { ... },
  "timestamp": "2026-01-03T12:00:00Z"
}
```

Error response:
```json
{
  "version": "1.0",
  "success": false,
  "error": {
    "code": "Auth.ProfileNotFound",
    "message": "Profile 'production' not found.",
    "target": "production"
  },
  "timestamp": "2026-01-03T12:00:00Z"
}
```

Partial success (batch operations):
```json
{
  "version": "1.0",
  "success": true,
  "data": { ... },
  "results": [
    { "name": "account", "success": true },
    { "name": "contact", "success": false, "error": { "code": "Operation.NotFound", "message": "..." } }
  ],
  "timestamp": "2026-01-03T12:00:00Z"
}
```

### Progress Output

Progress messages are written to **stderr** (not stdout), enabling piping:

```bash
# Progress goes to stderr, JSON data goes to stdout
ppds data export --schema schema.xml --output data.zip -f json 2>/dev/null | jq '.data'
```

Progress format (stderr, one JSON object per line):
```json
{"phase":"export","entity":"account","current":450,"total":1000,"rps":287.5}
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
