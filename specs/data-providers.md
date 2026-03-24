# Data Providers

**Status:** Draft
**Last Updated:** 2026-03-23
**Code:** [src/PPDS.Cli/Services/](../src/PPDS.Cli/Services/) | [src/PPDS.Extension/src/panels/](../src/PPDS.Extension/src/panels/)
**Surfaces:** All

---

## Overview

Data providers connect virtual entities to external data sources. Each provider maps CRUD operations (Retrieve, RetrieveMultiple, Create, Update, Delete) to plugin implementations that handle data access for virtual tables. A data source entity defines the virtual table; a data provider binds plugins to handle operations on that table.

### Goals

- **Full CRUD**: Register and manage data providers and data sources across all surfaces (CLI, TUI, Extension, MCP)
- **CUD plugin binding**: Expose Create, Update, and Delete plugin bindings — not hidden behind feature flags like PRT
- **Transparency**: Show all fields and operations that Dataverse supports, no artificial gatekeeping

### Non-Goals

- Virtual entity definition/metadata management (use Maker Portal or metadata tools)
- OData v4 provider management (built-in, not user-registerable)
- Code-first annotations for data providers (unlike Custom APIs, data providers are infrastructure configuration, not something you'd annotate on a plugin class)

---

## Architecture

```
┌──────────────────┐     ┌──────────────────┐     ┌──────────────────┐
│   CLI Commands   │     │  Extension Panel  │     │    TUI Screen    │
│  data-providers  │     │  (PluginsPanel)   │     │ (PluginReg...)   │
│  data-sources    │     │                   │     │                  │
└────────┬─────────┘     └────────┬──────────┘     └────────┬─────────┘
         │                        │                         │
         │              ┌─────────▼──────────┐              │
         └─────────────▶│   RPC Endpoints    │◀─────────────┘
                        │ dataProviders/*    │
                        │ dataSources/*      │
                        └─────────┬──────────┘
                                  │
                        ┌─────────▼──────────┐
                        │ IDataProviderSvc   │
                        └─────────┬──────────┘
                                  │
                   ┌──────────────┼──────────────┐
                   ▼                             ▼
         ┌──────────────────┐          ┌──────────────────┐
         │    Dataverse     │          │    Dataverse      │
         │ entitydatasource │          │ entitydataprovider │
         └──────────────────┘          └───────────────────┘
```

Data providers share the Plugins panel container defined in [plugins.md](./plugins.md). The service layer is `IDataProviderService` (new, follows the Application Services pattern). CLI commands live under `ppds data-providers` and `ppds data-sources`. RPC endpoints use the `dataProviders/` and `dataSources/` namespaces.

### Components

| Component | Responsibility |
|-----------|----------------|
| `IDataProviderService` | CRUD operations for `entitydataprovider` and `entitydatasource` entities |
| `DataProviderCommands` | CLI command group under `ppds data-providers` |
| `DataSourceCommands` | CLI command group under `ppds data-sources` |
| `PluginsPanel` | Shared Extension webview — renders data source/provider nodes alongside assemblies |
| `PluginRegistrationScreen` | Shared TUI screen — renders data source/provider nodes in tree |

### Dependencies

- Depends on: [plugins.md](./plugins.md) (shared Plugins panel, plugin type registration for operation bindings)
- Uses patterns from: [architecture.md](./architecture.md) (Application Services, Connection Pooling)
- Uses: [connection-pooling.md](./connection-pooling.md) for Dataverse access

---

## Specification

### Dataverse Entities

Data providers span two Dataverse entities. An `entitydatasource` defines a virtual table's data source configuration; an `entitydataprovider` binds up to five plugin types to handle operations on that table.

#### `entitydataprovider`

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `Name` | string | Yes | Display name of the data provider |
| `DataSourceId` | Guid | Yes | FK to `entitydatasource` |
| `SolutionId` | Guid | Yes | Solution containing this provider |
| `RetrievePlugin` | Guid? | No | Plugin type for Retrieve operations |
| `RetrieveMultiplePlugin` | Guid? | No | Plugin type for RetrieveMultiple operations |
| `CreatePlugin` | Guid? | No | Plugin type for Create operations |
| `UpdatePlugin` | Guid? | No | Plugin type for Update operations |
| `DeletePlugin` | Guid? | No | Plugin type for Delete operations |

PRT hides the Create, Update, and Delete plugin bindings behind a feature flag (`CUDEnableState`). We expose all five plugin operation bindings unconditionally.

#### `entitydatasource`

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `DisplayName` | string | Yes | Human-readable display name |
| `Name` / `LogicalName` | string | Yes | Logical name, format: `{prefix}_{name}` |
| `PluralName` | string | Yes | Plural display name |
| `SolutionId` | Guid | Yes | Solution containing this data source |

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| DataProviderName | Non-empty | "Data provider name is required" |
| Solution | Non-empty, must exist | "Solution is required" |
| DataSource | Non-empty, must exist | "Data source is required" |
| Assembly | Required if any plugin is selected | "Assembly is required when plugins are specified" |
| RetrievePlugin | Recommended but not required | Warning: "No Retrieve plugin specified — virtual entity will not support read operations" |
| DataSource Name | Alphanumeric + underscore only | "Data source name must contain only letters, numbers, and underscores" |
| DataSource LogicalName | `{prefix}_{name}` must be unique | "Data source logical name already exists" |
| DataSource DisplayName | Non-empty | "Display name is required" |
| DataSource PluralName | Non-empty | "Plural name is required" |

### Core Requirements

1. Data providers and data sources are created, read, updated, and deleted through `IDataProviderService`
2. All operations use the connection pool — never store or hold a single client
3. Data sources appear as root-level nodes in the shared Plugins panel (Assembly view)
4. Data providers appear as children of their parent data source
5. All five plugin operation bindings (Retrieve, RetrieveMultiple, Create, Update, Delete) are visible and configurable
6. No managed component gatekeeping — all operations available on all items
7. Operations >1 second accept `IProgressReporter` (Constitution A3): cascade unregister

### Primary Flows

**Register Data Source:**

1. **Validate**: Display name, logical name, plural name, and solution are provided
2. **Check uniqueness**: Verify logical name (`{prefix}_{name}`) doesn't already exist
3. **Create**: Create `entitydatasource` record in Dataverse
4. **Solution**: Add to specified solution
5. **Confirm**: Return created data source ID

**Register Data Provider:**

1. **Resolve references**: Look up data source ID, solution ID, and plugin type IDs for each operation binding
2. **Validate**: Name, data source, and solution are provided; assembly is provided if any plugins are specified
3. **Create**: Create `entitydataprovider` record with plugin bindings
4. **Confirm**: Return created data provider ID

**Update Data Provider:**

1. **Lookup**: Find by name or ID
2. **Resolve**: Look up new plugin type IDs if changed
3. **Update**: Update `entitydataprovider` record with new bindings
4. **Confirm**: Return updated summary

**Unregister Data Provider:**

1. **Lookup**: Find by name or ID
2. **Delete**: Delete `entitydataprovider` record
3. **Confirm**: Return deletion summary

**Unregister Data Source:**

1. **Lookup**: Find by name or ID
2. **Check children**: Query for data providers referencing this data source
3. **Cascade**: If providers exist and `force` is true, delete providers first; if `force` is false, return error with child count
4. **Delete**: Delete `entitydatasource` record
5. **Confirm**: Return deletion summary

### Surface-Specific Behavior

#### CLI Surface

Two command groups: `ppds data-providers` and `ppds data-sources`. All commands support `--json` for machine-readable output.

**`ppds data-sources list [--solution <name>]`**

Lists all data source entities in the environment.

```bash
ppds data-sources list
ppds data-sources list --solution MySolution
ppds data-sources list --json
```

Output (table format):

```
DisplayName            LogicalName              PluralName               Solution
────────────────────   ──────────────────────   ──────────────────────   ────────────
Virtual Contacts       cr123_virtualcontact     Virtual Contacts         MySolution
External Products      cr123_externalproduct    External Products        MySolution
```

**`ppds data-sources get <name-or-id>`**

Shows detailed information for a specific data source including its providers.

```bash
ppds data-sources get cr123_virtualcontact
ppds data-sources get 12345678-1234-1234-1234-123456789abc
```

**`ppds data-sources register <display-name> --name <logical-name> --plural <plural-name> --solution <name>`**

```bash
ppds data-sources register "Virtual Contacts" \
    --name cr123_virtualcontact \
    --plural "Virtual Contacts" \
    --solution MySolution
```

| Option | Required | Description |
|--------|----------|-------------|
| `<display-name>` | Yes | Data source display name |
| `--name` | Yes | Logical name (format: `{prefix}_{name}`) |
| `--plural` | Yes | Plural display name |
| `--solution` | Yes | Solution unique name |

**`ppds data-sources update <name-or-id> [--display-name <name>] [--plural <name>]`**

```bash
ppds data-sources update cr123_virtualcontact --display-name "External Contacts"
ppds data-sources update cr123_virtualcontact --plural "External Contacts"
```

**`ppds data-sources unregister <name-or-id> [--force]`**

```bash
ppds data-sources unregister cr123_virtualcontact
ppds data-sources unregister cr123_virtualcontact --force
```

| Option | Description |
|--------|-------------|
| `--force` | Delete child data providers before unregistering data source |

**`ppds data-providers list [--data-source <name-or-id>]`**

Lists all data providers, optionally filtered by data source.

```bash
ppds data-providers list
ppds data-providers list --data-source cr123_virtualcontact
ppds data-providers list --json
```

Output (table format):

```
Name                   DataSource               Retrieve   RetrMultiple   Create   Update   Delete
────────────────────   ──────────────────────   ────────   ────────────   ──────   ──────   ──────
Contact Provider       cr123_virtualcontact     Yes        Yes            No       No       No
Product Provider       cr123_externalproduct    Yes        Yes            Yes      Yes      Yes
```

**`ppds data-providers get <name-or-id>`**

Shows detailed information for a specific data provider including all plugin bindings.

```bash
ppds data-providers get "Contact Provider"
ppds data-providers get 12345678-1234-1234-1234-123456789abc
```

**`ppds data-providers register <name> --data-source <name-or-id> --solution <name> --assembly <name> [plugin options]`**

```bash
ppds data-providers register "Contact Provider" \
    --data-source cr123_virtualcontact \
    --solution MySolution \
    --assembly MyPlugin \
    --retrieve RetrieveContactPlugin \
    --retrieve-multiple RetrieveMultipleContactPlugin \
    --create CreateContactPlugin \
    --update UpdateContactPlugin \
    --delete DeleteContactPlugin
```

| Option | Required | Description |
|--------|----------|-------------|
| `<name>` | Yes | Data provider display name |
| `--data-source` | Yes | Data source name or ID |
| `--solution` | Yes | Solution unique name |
| `--assembly` | Conditional | Assembly name (required if any plugin is specified) |
| `--retrieve` | No | Plugin type name for Retrieve |
| `--retrieve-multiple` | No | Plugin type name for RetrieveMultiple |
| `--create` | No | Plugin type name for Create |
| `--update` | No | Plugin type name for Update |
| `--delete` | No | Plugin type name for Delete |

**`ppds data-providers update <name-or-id> [plugin options]`**

```bash
ppds data-providers update "Contact Provider" \
    --create CreateContactPlugin \
    --update UpdateContactPlugin
```

**`ppds data-providers unregister <name-or-id>`**

```bash
ppds data-providers unregister "Contact Provider"
```

#### Extension Surface

Data sources appear as root-level nodes in the Assembly view of the shared Plugins panel (defined in [plugins.md](./plugins.md)). Data providers are children of data sources. Each provider shows its plugin bindings in the detail panel.

**Node rendering:**

| Node Type | Icon | Label Format |
|-----------|------|-------------|
| Data Source | file-cabinet icon | `{DisplayName}` |
| Data Provider | plug icon | `{Name}` |
| Plugin Binding | symbol-method icon | `{Operation}: {PluginTypeName}` |

**Tree structure (Assembly view):**

```
├─ 📦 MyPlugin.Package (1.0.0)
│  └─ ⚙️ MyPlugin.dll
│     └─ ...
├─ 🌐 My Webhook
│  └─ ⚡ Update of account (PostOp, Async)
├─ 📨 myorg_ApproveInvoice
│  └─ ...
├─ 🗃️ Virtual Contacts
│  └─ 🔌 Contact Provider
│     ├─ Retrieve: RetrieveContactPlugin
│     ├─ RetrieveMultiple: RetrieveMultipleContactPlugin
│     ├─ Create: CreateContactPlugin
│     ├─ Update: UpdateContactPlugin
│     └─ Delete: DeleteContactPlugin
└─ 🗃️ External Products
   └─ 🔌 Product Provider
      ├─ Retrieve: RetrieveProductPlugin
      └─ RetrieveMultiple: RetrieveMultipleProductPlugin
```

**Context menu actions:**

| Action | Node Types | Behavior |
|--------|-----------|----------|
| Register Data Source | Root | Opens data source registration form |
| Register Data Provider | Data Source | Opens data provider registration form |
| Update | Data Source, Data Provider | Opens update form |
| Unregister | Data Source, Data Provider | Confirmation dialog (data source shows cascade preview) |

**Data provider registration form:**

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| Name | Text | Yes | Data provider display name |
| Solution | Dropdown | Yes | With "Create New Solution" option |
| Data Source | Dropdown | Yes | With "New Data Source" dialog option |
| Assembly | Dropdown | Yes (conditional) | Loads registered plugin assemblies |
| Retrieve Plugin | Dropdown | No | Filtered to types in selected assembly |
| RetrieveMultiple Plugin | Dropdown | No | Filtered to types in selected assembly |
| Create Plugin | Dropdown | No | PRT hides this — we show it |
| Update Plugin | Dropdown | No | PRT hides this — we show it |
| Delete Plugin | Dropdown | No | PRT hides this — we show it |

Assembly dropdown selection cascades to populate all five plugin dropdowns with types from the selected assembly.

**Data source creation sub-dialog:**

Launched from the "New Data Source" option in the Data Source dropdown within the registration form.

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| Display Name | Text | Yes | Human-readable name |
| Name | Text | Yes | Alphanumeric + underscore only |
| Plural Name | Text | Yes | Plural display name |
| Solution | Dropdown | Yes | Pre-filled from parent form |

The logical name is computed as `{prefix}_{name}` where `prefix` comes from the solution's publisher prefix.

#### TUI Surface

Data sources appear as root-level nodes in the `PluginRegistrationScreen` tree, loaded alongside packages, standalone assemblies, service endpoints, and custom APIs.

**Tree display:**

```
├─ Package A
│  └─ Assembly A1
│     └─ ...
├─ Assembly B
├─ 🌐 My Webhook
│  └─ Step S1
├─ 📨 myorg_ApproveInvoice
│  └─ ...
├─ 🗃️ Virtual Contacts
│  └─ 🔌 Contact Provider
│     ├─ Retrieve: RetrieveContactPlugin
│     └─ RetrieveMultiple: RetrieveMultipleContactPlugin
└─ 🗃️ External Products
```

**Interactions:**

| Hotkey | Action |
|--------|--------|
| Enter | Show detail panel for selected data source or provider |
| F5 | Refresh tree (reloads data sources alongside all other node types) |

Read-only browsing — detail panel shows data source info and provider bindings. No creation or modification from TUI; use CLI or Extension.

**Detail panel (Data Source selected):**

Displays DisplayName, LogicalName, PluralName, Solution, IsManaged, provider count.

**Detail panel (Data Provider selected):**

Displays Name, DataSource, Solution, IsManaged, and all five plugin bindings (showing plugin type name or "Not configured" for each).

#### MCP Surface

Read-only tool for AI-assisted browsing.

| Tool | Parameters | Returns |
|------|-----------|---------|
| `dataProviders_list` | (none) | All data sources with their providers and plugin binding summaries |

---

## Core Types

### IDataProviderService

Service interface for all data provider and data source CRUD operations.

```csharp
public interface IDataProviderService
{
    // Data Sources
    Task<List<DataSourceInfo>> ListDataSourcesAsync(
        string? solutionName = null, CancellationToken cancellationToken = default);
    Task<DataSourceInfo?> GetDataSourceAsync(
        string nameOrId, CancellationToken cancellationToken = default);
    Task<Guid> RegisterDataSourceAsync(
        DataSourceRegistration registration, CancellationToken cancellationToken = default);
    Task UpdateDataSourceAsync(
        Guid id, DataSourceUpdateRequest request,
        CancellationToken cancellationToken = default);
    Task UnregisterDataSourceAsync(
        Guid id, bool force = false,
        CancellationToken cancellationToken = default);

    // Data Providers
    Task<List<DataProviderInfo>> ListDataProvidersAsync(
        Guid? dataSourceId = null, CancellationToken cancellationToken = default);
    Task<DataProviderInfo?> GetDataProviderAsync(
        string nameOrId, CancellationToken cancellationToken = default);
    Task<Guid> RegisterDataProviderAsync(
        DataProviderRegistration registration, CancellationToken cancellationToken = default);
    Task UpdateDataProviderAsync(
        Guid id, DataProviderUpdateRequest request,
        CancellationToken cancellationToken = default);
    Task UnregisterDataProviderAsync(
        Guid id, CancellationToken cancellationToken = default);
}
```

### Info Types

**DataSourceInfo:**

| Property | Type | Description |
|----------|------|-------------|
| `Id` | Guid | Data source entity ID |
| `DisplayName` | string | Human-readable display name |
| `LogicalName` | string | Logical name (`{prefix}_{name}`) |
| `PluralName` | string | Plural display name |
| `SolutionId` | Guid | Containing solution ID |
| `SolutionName` | string? | Containing solution name |
| `IsManaged` | bool | Managed solution component |
| `ProviderCount` | int | Number of associated data providers |
| `Providers` | List\<DataProviderInfo\> | Associated providers (populated on get, not list) |
| `CreatedOn` | DateTime? | Creation timestamp |
| `ModifiedOn` | DateTime? | Last modified timestamp |

**DataProviderInfo:**

| Property | Type | Description |
|----------|------|-------------|
| `Id` | Guid | Data provider entity ID |
| `Name` | string | Display name |
| `DataSourceId` | Guid | Parent data source ID |
| `DataSourceName` | string? | Parent data source logical name |
| `SolutionId` | Guid | Containing solution ID |
| `SolutionName` | string? | Containing solution name |
| `RetrievePluginId` | Guid? | Plugin type ID for Retrieve |
| `RetrievePluginName` | string? | Plugin type name for Retrieve |
| `RetrieveMultiplePluginId` | Guid? | Plugin type ID for RetrieveMultiple |
| `RetrieveMultiplePluginName` | string? | Plugin type name for RetrieveMultiple |
| `CreatePluginId` | Guid? | Plugin type ID for Create |
| `CreatePluginName` | string? | Plugin type name for Create |
| `UpdatePluginId` | Guid? | Plugin type ID for Update |
| `UpdatePluginName` | string? | Plugin type name for Update |
| `DeletePluginId` | Guid? | Plugin type ID for Delete |
| `DeletePluginName` | string? | Plugin type name for Delete |
| `IsManaged` | bool | Managed solution component |
| `CreatedOn` | DateTime? | Creation timestamp |
| `ModifiedOn` | DateTime? | Last modified timestamp |

### Registration/Update Types

```csharp
public record DataSourceRegistration(
    string DisplayName,
    string LogicalName,
    string PluralName,
    string SolutionName
);

public record DataSourceUpdateRequest(
    string? DisplayName = null,
    string? PluralName = null
);

public record DataProviderRegistration(
    string Name,
    Guid DataSourceId,
    string SolutionName,
    Guid? RetrievePluginId = null,
    Guid? RetrieveMultiplePluginId = null,
    Guid? CreatePluginId = null,
    Guid? UpdatePluginId = null,
    Guid? DeletePluginId = null
);

public record DataProviderUpdateRequest(
    Guid? RetrievePluginId = null,
    Guid? RetrieveMultiplePluginId = null,
    Guid? CreatePluginId = null,
    Guid? UpdatePluginId = null,
    Guid? DeletePluginId = null
);
```

Note: `DataSourceId` and `SolutionName` cannot be changed after creation on a data provider — they are excluded from `DataProviderUpdateRequest`. `LogicalName` cannot be changed after creation on a data source — it is excluded from `DataSourceUpdateRequest`.

---

## API/Contracts

### RPC Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| POST | `dataProviders/list` | List data providers, optionally filtered by data source |
| POST | `dataProviders/get` | Get details for a specific data provider |
| POST | `dataProviders/register` | Create data provider with plugin bindings |
| POST | `dataProviders/update` | Modify plugin bindings |
| POST | `dataProviders/unregister` | Delete data provider |
| POST | `dataSources/list` | List data source entities |
| POST | `dataSources/get` | Get details for a specific data source including providers |
| POST | `dataSources/register` | Create data source entity |
| POST | `dataSources/update` | Update data source |
| POST | `dataSources/unregister` | Delete data source (with optional cascade) |

### Request/Response Examples

**dataSources/list**

Request:
```json
{
    "solutionName": null
}
```

Response:
```json
{
    "dataSources": [
        {
            "id": "11111111-1111-1111-1111-111111111111",
            "displayName": "Virtual Contacts",
            "logicalName": "cr123_virtualcontact",
            "pluralName": "Virtual Contacts",
            "solutionName": "MySolution",
            "isManaged": false,
            "providerCount": 1
        }
    ]
}
```

**dataSources/get**

Request:
```json
{
    "nameOrId": "cr123_virtualcontact"
}
```

Response:
```json
{
    "id": "11111111-1111-1111-1111-111111111111",
    "displayName": "Virtual Contacts",
    "logicalName": "cr123_virtualcontact",
    "pluralName": "Virtual Contacts",
    "solutionName": "MySolution",
    "isManaged": false,
    "providers": [
        {
            "id": "22222222-2222-2222-2222-222222222222",
            "name": "Contact Provider",
            "retrievePluginName": "RetrieveContactPlugin",
            "retrieveMultiplePluginName": "RetrieveMultipleContactPlugin",
            "createPluginName": null,
            "updatePluginName": null,
            "deletePluginName": null
        }
    ]
}
```

**dataProviders/register**

Request:
```json
{
    "name": "Contact Provider",
    "dataSourceId": "11111111-1111-1111-1111-111111111111",
    "solutionName": "MySolution",
    "retrievePluginId": "33333333-3333-3333-3333-333333333333",
    "retrieveMultiplePluginId": "44444444-4444-4444-4444-444444444444",
    "createPluginId": null,
    "updatePluginId": null,
    "deletePluginId": null
}
```

Response:
```json
{
    "id": "22222222-2222-2222-2222-222222222222"
}
```

**dataProviders/update**

Request:
```json
{
    "id": "22222222-2222-2222-2222-222222222222",
    "createPluginId": "55555555-5555-5555-5555-555555555555",
    "updatePluginId": "66666666-6666-6666-6666-666666666666"
}
```

Response:
```json
{
    "success": true
}
```

**dataProviders/unregister**

Request:
```json
{
    "id": "22222222-2222-2222-2222-222222222222"
}
```

Response:
```json
{
    "success": true
}
```

**dataSources/register**

Request:
```json
{
    "displayName": "Virtual Contacts",
    "logicalName": "cr123_virtualcontact",
    "pluralName": "Virtual Contacts",
    "solutionName": "MySolution"
}
```

Response:
```json
{
    "id": "11111111-1111-1111-1111-111111111111"
}
```

**dataSources/unregister**

Request:
```json
{
    "id": "11111111-1111-1111-1111-111111111111",
    "force": true
}
```

Response:
```json
{
    "dataSourcesDeleted": 1,
    "providersDeleted": 1,
    "totalDeleted": 2
}
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `DataSource.NotFound` | Data source name or ID doesn't exist | Use `list` to find valid names/IDs |
| `DataSource.DuplicateName` | LogicalName already used by another data source | Choose a unique logical name |
| `DataSource.InvalidName` | Name contains invalid characters | Use only alphanumeric characters and underscores |
| `DataSource.CascadeConstraint` | Data providers reference this data source on unregister without force | Use `--force` to delete providers first |
| `DataProvider.NotFound` | Data provider name or ID doesn't exist | Use `list` to find valid names/IDs |
| `DataProvider.DataSourceNotFound` | Referenced data source doesn't exist | Create data source first |
| `DataProvider.PluginTypeNotFound` | Referenced plugin type doesn't exist | Register plugin assembly and type first |
| `DataProvider.AssemblyRequired` | Plugin specified but no assembly selected | Select an assembly before specifying plugins |
| `DataProvider.SolutionNotFound` | Referenced solution doesn't exist | Create solution first or use an existing one |

### Recovery Strategies

- **Not found**: Use `ppds data-sources list` or `ppds data-providers list` to get valid names/IDs
- **Plugin type not found**: Register the assembly and type first with `ppds plugins register assembly` and `ppds plugins register type`
- **Cascade constraint**: Add `--force` to unregister child providers first
- **Validation errors**: Fix the input per the validation rules table

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Data provider with no plugins bound | Valid — provider exists but no operations are handled |
| Data provider with only Retrieve plugins | Valid — most common configuration |
| Data provider with only CUD plugins | Valid but unusual — no read capability |
| Data source with no providers | Valid — data source exists but is not yet wired to plugins |
| Delete data source with providers | Blocked without `--force`; cascade deletes providers with `--force` |
| Plugin type used by both steps and data provider | Valid — same type can back steps and a provider operation |
| Multiple providers for one data source | Dataverse allows only one provider per data source — second registration returns error |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `ppds data-sources register` creates `entitydatasource` record with correct DisplayName, LogicalName, PluralName, and SolutionId | `DataProviderServiceTests.RegisterDataSource_CreatesRecord` | 🔲 |
| AC-02 | `ppds data-sources register` rejects logical names with invalid characters (spaces, hyphens, special chars) | `DataProviderServiceTests.RegisterDataSource_InvalidName_Rejects` | 🔲 |
| AC-03 | `ppds data-sources register` rejects duplicate logical names | `DataProviderServiceTests.RegisterDataSource_DuplicateName_Rejects` | 🔲 |
| AC-04 | `ppds data-sources list` returns all data sources with provider counts | `DataProviderServiceTests.ListDataSources_ReturnsWithCounts` | 🔲 |
| AC-05 | `ppds data-sources get` returns data source detail with associated providers | `DataProviderServiceTests.GetDataSource_IncludesProviders` | 🔲 |
| AC-06 | `ppds data-sources update` modifies only specified fields (DisplayName, PluralName) | `DataProviderServiceTests.UpdateDataSource_ModifiesOnlySpecified` | 🔲 |
| AC-07 | `ppds data-sources unregister` without `--force` fails when providers exist | `DataProviderServiceTests.UnregisterDataSource_FailsWithChildren` | 🔲 |
| AC-08 | `ppds data-sources unregister --force` deletes providers before deleting data source | `DataProviderServiceTests.UnregisterDataSource_Force_CascadeDeletes` | 🔲 |
| AC-09 | `ppds data-providers register` creates `entitydataprovider` with all five plugin bindings | `DataProviderServiceTests.RegisterProvider_AllFivePlugins` | 🔲 |
| AC-10 | `ppds data-providers register` with no plugin arguments creates provider with null bindings | `DataProviderServiceTests.RegisterProvider_NoPlugins_NullBindings` | 🔲 |
| AC-11 | `ppds data-providers update` modifies plugin bindings without affecting other fields | `DataProviderServiceTests.UpdateProvider_ModifiesBindingsOnly` | 🔲 |
| AC-12 | `ppds data-providers unregister` deletes provider record | `DataProviderServiceTests.UnregisterProvider_DeletesRecord` | 🔲 |
| AC-13 | `ppds data-providers get` returns all five plugin binding names (not just Retrieve/RetrieveMultiple) | `DataProviderServiceTests.GetProvider_ShowsAllFiveBindings` | 🔲 |
| AC-14 | Extension: Data sources appear as root-level file-cabinet nodes in Assembly view | `PluginsPanelTests.DataSources_ShowInAssemblyView` | 🔲 |
| AC-15 | Extension: Data providers appear as children of data source nodes | `PluginsPanelTests.DataProviders_ShowAsChildren` | 🔲 |
| AC-16 | Extension: Registration form shows all five plugin dropdowns (CUD not hidden) | `PluginsPanelTests.DataProviderForm_ShowsAllFivePlugins` | 🔲 |
| AC-17 | Extension: Assembly dropdown cascades to populate plugin type dropdowns | `PluginsPanelTests.DataProviderForm_AssemblyCascade` | 🔲 |
| AC-18 | Extension: "New Data Source" option in dropdown opens inline creation sub-dialog | `PluginsPanelTests.DataProviderForm_NewDataSourceDialog` | 🔲 |
| AC-19 | RPC: `dataSources/list` returns all data sources with provider counts | `DataProviderRpcTests.ListDataSources_ReturnsAll` | 🔲 |
| AC-20 | RPC: `dataSources/register` creates data source and returns ID | `DataProviderRpcTests.RegisterDataSource_ReturnsId` | 🔲 |
| AC-21 | RPC: `dataProviders/register` creates provider with plugin bindings and returns ID | `DataProviderRpcTests.RegisterProvider_ReturnsId` | 🔲 |
| AC-22 | RPC: `dataProviders/update` modifies plugin bindings | `DataProviderRpcTests.UpdateProvider_ModifiesBindings` | 🔲 |
| AC-23 | RPC: `dataProviders/unregister` deletes provider | `DataProviderRpcTests.UnregisterProvider_Deletes` | 🔲 |
| AC-24 | TUI: Data sources appear as root nodes alongside packages, assemblies, endpoints, and custom APIs | `PluginRegistrationScreenTests.TreeShowsDataSources` | 🔲 |
| AC-25 | TUI: Selecting data provider node shows detail panel with all five plugin bindings | `PluginRegistrationScreenTests.DataProviderDetail_ShowsAllBindings` | 🔲 |
| AC-26 | MCP: `dataProviders_list` returns all data sources with providers (read-only) | `McpDataProviderTests.List_ReturnsAll` | 🔲 |
| AC-27 | No managed component gatekeeping — all operations available on managed data providers and data sources | `DataProviderServiceTests.ManagedComponents_OperationsNotBlocked` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Data source with no providers | Get data source | Valid data source with empty providers list |
| Data provider with no plugins | Register with no plugin flags | Valid provider with all bindings null |
| Provider with only CUD plugins | Retrieve=null, RetrieveMultiple=null, Create/Update/Delete set | Warning logged but registration succeeds |
| Duplicate data source logical name | Register with existing logical name | PpdsException with `DataSource.DuplicateName` |
| Invalid data source name characters | Name with spaces or hyphens | PpdsException with `DataSource.InvalidName` |
| Data source unregister with providers | Unregister without `--force` | PpdsException with `DataSource.CascadeConstraint` and provider count |

### Test Examples

```csharp
[Fact]
public async Task RegisterProvider_AllFivePlugins_CreatesWithBindings()
{
    // Arrange
    var service = new DataProviderService(pool, logger);
    var registration = new DataProviderRegistration(
        Name: "Contact Provider",
        DataSourceId: existingDataSourceId,
        SolutionName: "MySolution",
        RetrievePluginId: retrieveTypeId,
        RetrieveMultiplePluginId: retrieveMultipleTypeId,
        CreatePluginId: createTypeId,
        UpdatePluginId: updateTypeId,
        DeletePluginId: deleteTypeId);

    // Act
    var id = await service.RegisterDataProviderAsync(registration);

    // Assert
    id.Should().NotBeEmpty();
    var provider = await service.GetDataProviderAsync(id.ToString());
    provider.Should().NotBeNull();
    provider!.RetrievePluginId.Should().Be(retrieveTypeId);
    provider.RetrieveMultiplePluginId.Should().Be(retrieveMultipleTypeId);
    provider.CreatePluginId.Should().Be(createTypeId);
    provider.UpdatePluginId.Should().Be(updateTypeId);
    provider.DeletePluginId.Should().Be(deleteTypeId);
}

[Fact]
public async Task RegisterDataSource_InvalidName_ThrowsValidationError()
{
    // Arrange
    var service = new DataProviderService(pool, logger);
    var registration = new DataSourceRegistration(
        DisplayName: "Virtual Contacts",
        LogicalName: "cr123_virtual-contact",  // Hyphen not allowed
        PluralName: "Virtual Contacts",
        SolutionName: "MySolution");

    // Act & Assert
    var act = () => service.RegisterDataSourceAsync(registration);
    await act.Should().ThrowAsync<PpdsException>()
        .WithMessage("*only letters, numbers, and underscores*");
}

[Fact]
public async Task UnregisterDataSource_WithProviders_RequiresForce()
{
    // Arrange — data source has one provider
    var service = new DataProviderService(pool, logger);

    // Act & Assert
    var act = () => service.UnregisterDataSourceAsync(dataSourceId, force: false);
    await act.Should().ThrowAsync<PpdsException>()
        .WithMessage("*1 data provider*--force*");
}
```

---

## Design Decisions

### Why expose CUD plugins?

**Context:** PRT hides Create, Update, and Delete plugin bindings behind a feature flag (`CUDEnableState`). Only Retrieve and RetrieveMultiple are visible by default. However, Dataverse fully supports CUD operations on virtual entities backed by data providers.

**Decision:** Show all five plugin operation bindings unconditionally.

**Alternatives considered:**
- Match PRT behavior (hide CUD): Rejected — violates our transparency principle; hiding supported features confuses users who need them
- Feature flag like PRT: Rejected — adds complexity without user benefit; users who don't need CUD can leave fields empty

**Consequences:**
- Positive: Users can bind CUD plugins without workarounds or configuration hacks
- Positive: Consistent with our "show everything Dataverse supports" philosophy
- Negative: Form has five plugin dropdowns instead of two — slightly more complex UI

### Why two CLI command groups (data-providers + data-sources)?

**Context:** Data providers and data sources are different Dataverse entities with different lifecycles. A data source is a virtual table definition; a data provider is the plugin binding for that table.

**Decision:** Two separate CLI command groups: `ppds data-sources` and `ppds data-providers`.

**Alternatives considered:**
- Single `ppds data-providers` group with sub-commands for sources: Rejected — conflates two distinct entities with different CRUD operations
- Nested under `ppds plugins data-providers`: Rejected — data providers are peers of assemblies in the tree, not children of the plugin system

**Consequences:**
- Positive: Clean separation of concerns matching the Dataverse entity model
- Positive: Each command group has straightforward CRUD semantics
- Negative: Users must learn two command groups instead of one

### Why no code-first annotations for data providers?

**Context:** Plugins and Custom APIs support code-first registration via attributes (`PluginStepAttribute`, `CustomApiAttribute`). Data providers are a different category of configuration.

**Decision:** No `DataProviderAttribute` — data providers are registered imperatively or via UI forms.

**Alternatives considered:**
- `DataProviderAttribute` on plugin classes: Rejected — a data provider binds multiple plugin types to operations, not one plugin to one step; the annotation model doesn't map cleanly
- YAML/JSON configuration file: Rejected — would be a third configuration format alongside `registrations.json` annotations and imperative CLI

**Consequences:**
- Positive: Simpler mental model — annotations are for plugin step/API definitions, not infrastructure wiring
- Positive: Data provider configuration involves choosing existing plugin types, which is naturally a UI/CLI task
- Negative: No extract/deploy workflow for data providers (they must be registered imperatively or via forms)

### Why DataSourceId is immutable on data providers?

**Context:** A data provider is fundamentally bound to a specific data source — changing the data source would change which virtual entity the provider serves.

**Decision:** `DataSourceId` cannot be changed after creation. Changing the data source requires unregister + re-register.

**Alternatives considered:**
- Allow data source reassignment: Rejected — semantically a new provider, not an update to an existing one
- Transparent delete-and-recreate: Rejected — loses the entity GUID, may break references

**Consequences:**
- Positive: Clear semantics — a provider is permanently bound to its data source
- Negative: Changing the data source requires two operations instead of one

---

## Related Specs

- [plugins.md](./plugins.md) - Shared Plugins panel container, plugin type registration used for operation bindings
- [architecture.md](./architecture.md) - Application Services pattern used by IDataProviderService
- [connection-pooling.md](./connection-pooling.md) - Connection pool used for Dataverse access
- [service-endpoints.md](./service-endpoints.md) - Another entity type in the shared Plugins panel
- [custom-apis.md](./custom-apis.md) - Another entity type in the shared Plugins panel

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-23 | Initial spec |

---

## Roadmap

- Data provider health check tool (verify plugin types exist and are deployable)
- Data provider cloning (copy provider configuration from one data source to another)
- Bulk data source registration from metadata export
- Data provider testing tool (invoke Retrieve/RetrieveMultiple against virtual entity from Extension/CLI)
