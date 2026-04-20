# Custom APIs

**Status:** Draft
**Last Updated:** 2026-03-23
**Code:** [src/PPDS.Plugins/](../src/PPDS.Plugins/) | [src/PPDS.Cli/Services/](../src/PPDS.Cli/Services/) | [src/PPDS.Extension/src/panels/](../src/PPDS.Extension/src/panels/)
**Surfaces:** All

---

## Overview

Custom APIs let developers define new Dataverse messages backed by plugin implementations. Unlike standard plugins that hook into existing messages (Create, Update, Delete), Custom APIs create new endpoints callable via the Dataverse SDK and Web API. They are the modern replacement for custom process actions (workflow-based), offering better performance and a cleaner development model.

### Goals

- **Code-First Registration**: Define Custom APIs via `CustomApiAttribute` and `CustomApiParameterAttribute` annotations, following the same philosophy as `PluginStepAttribute`
- **Full CRUD**: Create, read, update, and delete Custom APIs across all surfaces (CLI, TUI, Extension, MCP)
- **Extract/Deploy Workflow**: Integrate with `ppds plugins extract` and `ppds plugins deploy` for configuration-driven deployment

### Non-Goals

- Custom process actions (legacy workflow-based mechanism, replaced by Custom APIs)
- Custom API privileges management (Dataverse security role configuration is out of scope)
- Plugin runtime/execution (Dataverse's responsibility)

---

## Architecture

```
┌─────────────────────┐
│   Plugin Assembly   │  ← Developer annotates classes with CustomApiAttribute
│  (PPDS.Plugins ref) │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  AssemblyExtractor  │  ← Reads CustomApiAttribute via MetadataLoadContext
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│ registrations.json  │  ← Version-controlled configuration (customApis section)
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  ICustomApiService  │  ← Upserts to Dataverse
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│     Dataverse       │  ← customapi, customapirequestparameter,
│  (Live Environment) │     customapiresponseproperty
└─────────────────────┘
```

Custom APIs use three Dataverse entities: `customapi` (the API definition), `customapirequestparameter` (input parameters), and `customapiresponseproperty` (output parameters). Each API is backed by a plugin type. Custom APIs appear as root-level nodes in the shared Plugins panel (defined in [plugins.md](./plugins.md)).

### Components

| Component | Responsibility |
|-----------|----------------|
| `PPDS.Plugins` | `CustomApiAttribute`, `CustomApiParameterAttribute`, and supporting enums |
| `AssemblyExtractor` | Reads Custom API metadata from DLLs alongside plugin step metadata |
| `ICustomApiService` | CRUD operations for `customapi`, `customapirequestparameter`, `customapiresponseproperty` |
| `CustomApiCommands` | CLI command group under `ppds custom-apis` |
| `PluginsPanel` | Shared Extension webview — renders Custom API nodes alongside assemblies |
| `PluginRegistrationScreen` | Shared TUI screen — renders Custom API nodes in tree |

### Dependencies

- Depends on: [plugins.md](./plugins.md) (shared Plugins panel, plugin type registration, extract/deploy workflow)
- Uses patterns from: [architecture.md](./architecture.md) (Application Services, Connection Pooling)
- Uses: [connection-pooling.md](./connection-pooling.md) for Dataverse access

---

## Specification

### Dataverse Entities

Custom APIs span three Dataverse entities with a parent-child relationship.

#### `customapi`

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `UniqueName` | string | Yes | Unique identifier, format: `{prefix}_{name}` |
| `DisplayName` | string | Yes | Human-readable display name |
| `Name` | string | Yes | Logical name |
| `Description` | string | No | Free-text description |
| `PluginTypeId` | Guid | No | The implementing plugin type (nullable — can be set or cleared after creation via `set-plugin`) |
| `BindingType` | OptionSet | Yes | Global (0), Entity (1), EntityCollection (2) |
| `BoundEntityLogicalName` | string | Conditional | Required when BindingType = Entity or EntityCollection |
| `AllowedCustomProcessingStepType` | OptionSet | Yes | None (0), AsyncOnly (1), SyncAndAsync (2) |
| `IsFunction` | bool | Yes | GET semantics (true) vs POST semantics (false) |
| `IsPrivate` | bool | Yes | Hidden from public API documentation |
| `ExecutePrivilegeName` | string | No | Security privilege required to execute |

#### `customapirequestparameter`

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `UniqueName` | string | Yes | Unique identifier within the API |
| `DisplayName` | string | Yes | Human-readable display name |
| `Name` | string | Yes | Logical name |
| `Description` | string | No | Free-text description |
| `Type` | OptionSet | Yes | Parameter data type (see table below) |
| `LogicalEntityName` | string | Conditional | Required when Type is Entity (3), EntityCollection (4), or EntityReference (5) |
| `IsOptional` | bool | Yes | Whether the parameter can be omitted by callers |
| `CustomAPIId` | Guid | Yes | Parent Custom API |

#### `customapiresponseproperty`

Same schema as `customapirequestparameter` except: no `IsOptional` field (output properties are always returned).

#### Parameter Types

| Name | Value | Notes |
|------|-------|-------|
| Boolean | 0 | |
| DateTime | 1 | |
| Decimal | 2 | |
| Entity | 3 | Requires `LogicalEntityName` |
| EntityCollection | 4 | Requires `LogicalEntityName` |
| EntityReference | 5 | Requires `LogicalEntityName` |
| Float | 6 | |
| Integer | 7 | |
| Money | 8 | |
| Picklist | 9 | |
| String | 10 | |
| StringArray | 11 | |
| Guid | 12 | |

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| UniqueName | Non-empty, must contain `_` prefix separator | "UniqueName must use format {prefix}_{name}" |
| DisplayName | Non-empty | "DisplayName is required" |
| Name | Non-empty | "Name is required" |
| PluginTypeId | Must reference existing plugin type | "Plugin type not found" |
| BoundEntityLogicalName | Required when BindingType = Entity or EntityCollection | "BoundEntityLogicalName is required for entity-bound APIs" |
| BoundEntityLogicalName | Must be null/empty when BindingType = Global | "BoundEntityLogicalName must be empty for global APIs" |
| LogicalEntityName (param) | Required when Type is Entity, EntityCollection, or EntityReference | "LogicalEntityName is required for entity-typed parameters" |
| Parameter UniqueName | Unique within the API's parameters of the same direction | "Parameter name must be unique within the API" |

### Core Requirements

1. Custom APIs are created, read, updated, and deleted through `ICustomApiService`
2. All operations use the connection pool — never store or hold a single client
3. Custom APIs appear as root-level nodes in the shared Plugins panel (Assembly view)
4. `ppds plugins extract` reads `CustomApiAttribute` and produces custom API entries in `registrations.json`
5. `ppds plugins deploy` upserts custom APIs, request parameters, and response properties
6. No managed component gatekeeping — all operations available on all items
7. Operations >1 second accept `IProgressReporter` (Constitution A3): deploy, cascade unregister

### Primary Flows

**Code-First Extract → Deploy:**

1. **Annotate**: Developer adds `CustomApiAttribute` and `CustomApiParameterAttribute` to plugin classes
2. **Build**: Compile assembly referencing `PPDS.Plugins`
3. **Extract**: Run `ppds plugins extract <assembly.dll>` — extractor reads `CustomApiAttribute` alongside `PluginStepAttribute`
4. **Deploy**: Run `ppds plugins deploy registrations.json` — deployer upserts `customapi`, then upserts request parameters and response properties
5. **Clean** (optional): Run with `--clean` to remove orphaned custom APIs and parameters

**Imperative Registration:**

1. **Register API**: `ppds custom-apis register <unique-name> --plugin <type> --assembly <name>`
2. **Add Parameters**: `ppds custom-apis add-parameter <api-name> <param-name> --type <type> --direction <input|output>`
3. **Verify**: `ppds custom-apis get <unique-name>` to inspect

**Update Flow:**

1. **Lookup**: Find by unique name or ID
2. **Validate**: Apply validation rules to changed fields
3. **Update**: Update `customapi` record
4. **Confirm**: Return updated summary

**Unregister Flow:**

1. **Lookup**: Find by unique name or ID
2. **Check children**: Query for request parameters and response properties
3. **Cascade**: If children exist and `force` is true, delete parameters/properties first; if `force` is false, return error with child count
4. **Delete**: Delete `customapi` record
5. **Confirm**: Return deletion summary

### Surface-Specific Behavior

#### CLI Surface

Commands live under `ppds custom-apis`. All commands support `--json` for machine-readable output.

**`ppds custom-apis list [--solution <name>]`**

Lists all custom APIs in the environment.

```bash
ppds custom-apis list
ppds custom-apis list --solution MySolution
ppds custom-apis list --json
```

Output (table format):

```
UniqueName                 DisplayName         Binding     Plugin Type              Params
────────────────────────   ─────────────────   ─────────   ──────────────────────   ──────
myorg_ApproveInvoice       Approve Invoice     Entity      ApproveInvoicePlugin     2 in, 1 out
myorg_CalculateDiscount    Calculate Discount  Global      DiscountPlugin           1 in, 1 out
myorg_BulkImport           Bulk Import         Global      BulkImportPlugin         3 in, 0 out
```

**`ppds custom-apis get <unique-name-or-id>`**

Shows detailed information for a specific custom API including all parameters.

```bash
ppds custom-apis get myorg_ApproveInvoice
ppds custom-apis get 12345678-1234-1234-1234-123456789abc
```

**`ppds custom-apis register`**

```bash
ppds custom-apis register myorg_ApproveInvoice \
    --plugin ApproveInvoicePlugin \
    --assembly MyPlugin \
    --display-name "Approve Invoice" \
    --binding-type Entity \
    --bound-entity invoice \
    --solution MySolution
```

| Option | Required | Description |
|--------|----------|-------------|
| `<unique-name>` | Yes | API unique name (format: `{prefix}_{name}`) |
| `--plugin` | Yes | Implementing plugin type name |
| `--assembly` | Yes | Assembly containing the plugin type |
| `--display-name` | No | Display name (defaults to unique name) |
| `--binding-type` | No | Global (default), Entity, or EntityCollection |
| `--bound-entity` | Conditional | Required when binding-type is Entity or EntityCollection |
| `--is-function` | No | Mark as function (GET semantics) |
| `--is-private` | No | Hide from public API docs |
| `--allowed-processing` | No | None (default), AsyncOnly, or SyncAndAsync |
| `--privilege` | No | Security privilege name required to execute |
| `--description` | No | API description |
| `--solution` | No | Solution unique name |

**`ppds custom-apis update <unique-name-or-id> [options]`**

```bash
ppds custom-apis update myorg_ApproveInvoice --display-name "Approve Invoice v2"
ppds custom-apis update myorg_ApproveInvoice --is-private
```

**`ppds custom-apis unregister <unique-name-or-id> [--force]`**

```bash
ppds custom-apis unregister myorg_ApproveInvoice
ppds custom-apis unregister myorg_ApproveInvoice --force
```

| Option | Description |
|--------|-------------|
| `--force` | Delete child parameters and properties before unregistering API |

**`ppds custom-apis add-parameter`**

```bash
ppds custom-apis add-parameter myorg_ApproveInvoice ApproverNote \
    --type String \
    --direction input \
    --optional

ppds custom-apis add-parameter myorg_ApproveInvoice ApprovalId \
    --type Guid \
    --direction output
```

| Option | Required | Description |
|--------|----------|-------------|
| `<api-name>` | Yes | Parent Custom API unique name |
| `<param-name>` | Yes | Parameter unique name |
| `--type` | Yes | Boolean, DateTime, Decimal, Entity, EntityCollection, EntityReference, Float, Integer, Money, Picklist, String, StringArray, Guid |
| `--direction` | Yes | `input` or `output` |
| `--optional` | No | Mark as optional (input parameters only) |
| `--entity` | Conditional | Required when type is Entity, EntityCollection, or EntityReference |
| `--display-name` | No | Display name (defaults to parameter name) |
| `--description` | No | Parameter description |

**`ppds custom-apis remove-parameter <api-name> <param-name>`**

```bash
ppds custom-apis remove-parameter myorg_ApproveInvoice ApproverNote
```

**`ppds custom-apis set-plugin <unique-name-or-id> --plugin <type-name> --assembly <assembly-name>`**

Sets, changes, or clears the implementing plugin type for a Custom API.

```bash
# Link a plugin type to a Custom API
ppds custom-apis set-plugin myorg_ApproveInvoice \
    --plugin ApproveInvoicePlugin \
    --assembly MyPlugin

# Clear the plugin type (unlink)
ppds custom-apis set-plugin myorg_ApproveInvoice --none
```

| Option | Required | Description |
|--------|----------|-------------|
| `<unique-name-or-id>` | Yes | Custom API unique name or ID |
| `--plugin` | Conditional | Plugin type name (required unless `--none`) |
| `--assembly` | Conditional | Assembly containing the plugin type (required with `--plugin`) |
| `--none` | No | Clear the plugin type (unlink) |

#### Extension Surface

Custom APIs appear as root-level nodes in the Assembly view of the shared Plugins panel (defined in [plugins.md](./plugins.md)). In Message view, custom APIs appear alongside SDK messages.

**Node rendering:**

| Node Type | Icon | Label Format |
|-----------|------|-------------|
| Custom API | mail icon | `{UniqueName}` |
| Request Parameter | arrow-right icon | `{Name} ({Type})` |
| Response Property | arrow-left icon | `{Name} ({Type})` |

**Tree structure (Assembly view):**

```
├─ 📦 MyPlugin.Package (1.0.0)
│  └─ ⚙️ MyPlugin.dll
│     └─ ...
├─ 🌐 My Webhook
│  └─ ⚡ Update of account (PostOp, Async)
├─ 📨 myorg_ApproveInvoice
│  ├─ → ApproverNote (String, optional)
│  ├─ → InvoiceId (EntityReference)
│  └─ ← ApprovalId (Guid)
├─ 📨 myorg_CalculateDiscount
│  ├─ → Amount (Decimal)
│  └─ ← DiscountedAmount (Decimal)
└─ 🗃️ Virtual Data Source
```

**Tree structure (Message view):**

Custom APIs appear alongside standard SDK messages. Each custom API is a message node, and child steps registered on the custom API message appear beneath it.

```
├─ Create
│  ├─ account
│  │  └─ ⚡ Create of account (PreOp, Sync)
│  └─ contact
│     └─ ⚡ Create of contact (PostOp, Async)
├─ myorg_ApproveInvoice         ← Custom API as message
│  └─ invoice
│     └─ ⚡ (Plugin step bound to this message)
└─ Update
   └─ ...
```

**Context menu actions:**

| Action | Node Types | Behavior |
|--------|-----------|----------|
| Register Custom API | Root | Opens custom API registration form |
| Update | Custom API | Opens update form |
| Add Parameter | Custom API | Opens parameter form |
| Edit Parameter | Parameter | Opens parameter edit form |
| Remove Parameter | Parameter | Confirmation dialog, then deletes |
| Unregister | Custom API | Confirmation dialog with cascade preview |

**Registration form:**

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| Unique Name | Text | Yes | Format: `{prefix}_{name}` |
| Display Name | Text | Yes | |
| Name | Text | Yes | |
| Description | Textarea | No | |
| Plugin Type | Autocomplete | Yes | Loaded from registered plugin types |
| Binding Type | Select | Yes | Global (default), Entity, EntityCollection |
| Bound Entity | Autocomplete | Conditional | Enabled only when BindingType = Entity or EntityCollection |
| Allowed Processing | Select | Yes | None (default), AsyncOnly, SyncAndAsync |
| Is Function | Checkbox | No | GET vs POST semantics |
| Is Private | Checkbox | No | Hide from public API docs |
| Execute Privilege Name | Text | No | Security privilege name |

**Parameter form (sub-form with add/edit/delete rows):**

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| Name | Text | Yes | Parameter unique name |
| Display Name | Text | Yes | |
| Type | Select | Yes | All 13 parameter types |
| Direction | Radio | Yes | Input or Output |
| Is Optional | Checkbox | No | Enabled only when Direction = Input |
| Logical Entity Name | Autocomplete | Conditional | Enabled only when Type is Entity, EntityCollection, or EntityReference |
| Description | Text | No | |

#### TUI Surface

Custom APIs appear as root-level nodes in the `PluginRegistrationScreen` tree, loaded alongside packages, standalone assemblies, and service endpoints.

**Tree display:**

```
├─ Package A
│  └─ Assembly A1
│     └─ ...
├─ Assembly B
├─ 🌐 My Webhook
│  └─ Step S1
├─ 📨 myorg_ApproveInvoice
│  ├─ → ApproverNote (String, optional)
│  └─ ← ApprovalId (Guid)
└─ 📨 myorg_CalculateDiscount
```

**Interactions:**

| Hotkey | Action |
|--------|--------|
| Enter | Show detail panel for selected custom API or parameter |
| F5 | Refresh tree (reloads custom APIs alongside assemblies and endpoints) |

Read-only browsing — detail panel shows API info and parameters. No creation or modification from TUI; use CLI or Extension.

**Detail panel (Custom API selected):**

Displays UniqueName, DisplayName, Name, Description, BindingType, BoundEntityLogicalName, AllowedCustomProcessingStepType, IsFunction, IsPrivate, ExecutePrivilegeName, PluginTypeName, IsManaged, request parameter count, response property count.

#### MCP Surface

Read-only tool for AI-assisted browsing.

| Tool | Parameters | Returns |
|------|-----------|---------|
| `customApis_list` | (none) | All custom APIs with request/response parameter summaries |

---

## Core Types

### CustomApiAttribute

Defines custom API registration configuration. Applied to plugin classes to declare a new Dataverse message.

```csharp
[CustomApi(
    UniqueName = "myorg_ApproveInvoice",
    DisplayName = "Approve Invoice",
    BindingType = ApiBindingType.Entity,
    BoundEntity = "invoice",
    IsFunction = false)]
[CustomApiParameter(
    Name = "ApproverNote",
    Type = ApiParameterType.String,
    IsOptional = true,
    Direction = ParameterDirection.Input)]
[CustomApiParameter(
    Name = "ApprovalId",
    Type = ApiParameterType.Guid,
    Direction = ParameterDirection.Output)]
public class ApproveInvoicePlugin : PluginBase { }
```

**CustomApiAttribute properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `UniqueName` | string | Required | Unique identifier, format: `{prefix}_{name}` |
| `DisplayName` | string | Required | Human-readable display name |
| `Name` | string | null | Logical name (defaults to UniqueName if not set) |
| `Description` | string | null | Free-text description |
| `BindingType` | ApiBindingType | Global | Global, Entity, or EntityCollection |
| `BoundEntity` | string | null | Entity logical name (required when BindingType = Entity or EntityCollection) |
| `AllowedCustomProcessingStepType` | ApiProcessingStepType | None | None, AsyncOnly, or SyncAndAsync |
| `IsFunction` | bool | false | GET semantics (true) vs POST semantics (false) |
| `IsPrivate` | bool | false | Hidden from public API documentation |
| `ExecutePrivilegeName` | string | null | Security privilege required to execute |

### CustomApiParameterAttribute

Defines a request parameter or response property. Multiple attributes per class are supported.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | string | Required | Parameter unique name |
| `DisplayName` | string | null | Display name (defaults to Name if not set) |
| `Description` | string | null | Free-text description |
| `Type` | ApiParameterType | Required | Data type (13 options) |
| `Direction` | ParameterDirection | Required | Input or Output |
| `IsOptional` | bool | false | Whether the parameter can be omitted (input only) |
| `LogicalEntityName` | string | null | Required when Type is Entity, EntityCollection, or EntityReference |

### Enums

```csharp
public enum ApiBindingType
{
    Global = 0,           // Not bound to any entity
    Entity = 1,           // Bound to a single entity
    EntityCollection = 2  // Bound to an entity collection
}

public enum ApiParameterType
{
    Boolean = 0,
    DateTime = 1,
    Decimal = 2,
    Entity = 3,
    EntityCollection = 4,
    EntityReference = 5,
    Float = 6,
    Integer = 7,
    Money = 8,
    Picklist = 9,
    String = 10,
    StringArray = 11,
    Guid = 12
}

public enum ParameterDirection
{
    Input = 0,
    Output = 1
}

public enum ApiProcessingStepType
{
    None = 0,         // No custom processing steps allowed
    AsyncOnly = 1,    // Only async steps allowed
    SyncAndAsync = 2  // Both sync and async steps allowed
}
```

### ICustomApiService

Service interface for all Custom API CRUD operations.

```csharp
public interface ICustomApiService
{
    // Query
    Task<List<CustomApiInfo>> ListAsync(
        string? solutionName = null, CancellationToken cancellationToken = default);
    Task<CustomApiInfo?> GetByNameOrIdAsync(
        string nameOrId, CancellationToken cancellationToken = default);

    // Create
    Task<Guid> RegisterAsync(
        CustomApiRegistration registration, string? solutionName = null,
        CancellationToken cancellationToken = default);

    // Update
    Task UpdateAsync(
        Guid id, CustomApiUpdateRequest request,
        CancellationToken cancellationToken = default);

    // Delete
    Task<UnregisterResult> UnregisterAsync(
        Guid id, bool force = false,
        CancellationToken cancellationToken = default);

    // Plugin Type Linking
    Task SetPluginTypeAsync(
        Guid customApiId, string? pluginTypeName, string? assemblyName,
        CancellationToken cancellationToken = default);

    // Parameters
    Task<Guid> AddParameterAsync(
        Guid customApiId, CustomApiParameterRegistration parameter,
        CancellationToken cancellationToken = default);
    Task RemoveParameterAsync(
        Guid parameterId, CancellationToken cancellationToken = default);
    Task<List<CustomApiParameterInfo>> ListParametersAsync(
        Guid customApiId, CancellationToken cancellationToken = default);
}
```

### Info Types

**CustomApiInfo:**

| Property | Type | Description |
|----------|------|-------------|
| `Id` | Guid | Custom API ID |
| `UniqueName` | string | Unique identifier |
| `DisplayName` | string | Human-readable name |
| `Name` | string | Logical name |
| `Description` | string? | Free-text description |
| `BindingType` | string | Global, Entity, or EntityCollection |
| `BoundEntityLogicalName` | string? | Bound entity (null for Global) |
| `AllowedCustomProcessingStepType` | string | None, AsyncOnly, or SyncAndAsync |
| `IsFunction` | bool | GET vs POST semantics |
| `IsPrivate` | bool | Hidden from public API docs |
| `ExecutePrivilegeName` | string? | Required privilege name |
| `PluginTypeId` | Guid? | Implementing plugin type ID |
| `PluginTypeName` | string? | Implementing plugin type name |
| `IsManaged` | bool | Whether this is a managed solution component |
| `RequestParameterCount` | int | Number of input parameters |
| `ResponsePropertyCount` | int | Number of output properties |
| `RequestParameters` | List\<CustomApiParameterInfo\> | Input parameters (populated on get, not list) |
| `ResponseProperties` | List\<CustomApiParameterInfo\> | Output properties (populated on get, not list) |
| `CreatedOn` | DateTime? | Creation timestamp |
| `ModifiedOn` | DateTime? | Last modified timestamp |

**CustomApiParameterInfo:**

| Property | Type | Description |
|----------|------|-------------|
| `Id` | Guid | Parameter ID |
| `UniqueName` | string | Parameter unique name |
| `DisplayName` | string | Display name |
| `Name` | string | Logical name |
| `Description` | string? | Free-text description |
| `Type` | string | Parameter data type name |
| `LogicalEntityName` | string? | Entity name for entity-typed parameters |
| `IsOptional` | bool | Whether parameter is optional (always false for output) |
| `Direction` | string | Input or Output |
| `IsManaged` | bool | Whether this is a managed solution component |
| `CustomApiId` | Guid | Parent Custom API ID |

### Registration/Update Types

```csharp
public record CustomApiRegistration(
    string UniqueName,
    string DisplayName,
    string? Name = null,
    string? Description = null,
    Guid PluginTypeId = default,
    ApiBindingType BindingType = ApiBindingType.Global,
    string? BoundEntityLogicalName = null,
    ApiProcessingStepType AllowedCustomProcessingStepType = ApiProcessingStepType.None,
    bool IsFunction = false,
    bool IsPrivate = false,
    string? ExecutePrivilegeName = null
);

public record CustomApiUpdateRequest(
    string? DisplayName = null,
    string? Description = null,
    ApiProcessingStepType? AllowedCustomProcessingStepType = null,
    bool? IsFunction = null,
    bool? IsPrivate = null,
    string? ExecutePrivilegeName = null
);

public record CustomApiParameterRegistration(
    string UniqueName,
    string? DisplayName = null,
    string? Description = null,
    ApiParameterType Type = default,
    ParameterDirection Direction = ParameterDirection.Input,
    bool IsOptional = false,
    string? LogicalEntityName = null
);
```

Note: `UniqueName`, `BindingType`, and `BoundEntityLogicalName` cannot be changed after creation — they are excluded from `CustomApiUpdateRequest`. `PluginTypeId` is changed via the dedicated `SetPluginTypeAsync` method (not through update) because it requires resolving a plugin type by name and assembly.

---

## API/Contracts

### RPC Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| POST | `customApis/list` | List all custom APIs with parameter counts |
| POST | `customApis/get` | Get details for specific API including all parameters |
| POST | `customApis/register` | Create custom API |
| POST | `customApis/update` | Modify custom API |
| POST | `customApis/unregister` | Delete with cascade |
| POST | `customApis/addParameter` | Add request parameter or response property |
| POST | `customApis/removeParameter` | Delete a parameter or property |
| POST | `customApis/setPlugin` | Set, change, or clear implementing plugin type |

### Request/Response Examples

**customApis/list**

Request:
```json
{
    "solutionName": null
}
```

Response:
```json
{
    "customApis": [
        {
            "id": "11111111-1111-1111-1111-111111111111",
            "uniqueName": "myorg_ApproveInvoice",
            "displayName": "Approve Invoice",
            "bindingType": "Entity",
            "boundEntityLogicalName": "invoice",
            "isFunction": false,
            "isPrivate": false,
            "pluginTypeName": "MyPlugin.ApproveInvoicePlugin",
            "isManaged": false,
            "requestParameterCount": 2,
            "responsePropertyCount": 1
        }
    ]
}
```

**customApis/get**

Request:
```json
{
    "nameOrId": "myorg_ApproveInvoice"
}
```

Response:
```json
{
    "id": "11111111-1111-1111-1111-111111111111",
    "uniqueName": "myorg_ApproveInvoice",
    "displayName": "Approve Invoice",
    "name": "myorg_ApproveInvoice",
    "description": "Approves an invoice with optional note",
    "bindingType": "Entity",
    "boundEntityLogicalName": "invoice",
    "allowedCustomProcessingStepType": "None",
    "isFunction": false,
    "isPrivate": false,
    "executePrivilegeName": null,
    "pluginTypeId": "22222222-2222-2222-2222-222222222222",
    "pluginTypeName": "MyPlugin.ApproveInvoicePlugin",
    "isManaged": false,
    "requestParameters": [
        {
            "id": "33333333-3333-3333-3333-333333333333",
            "uniqueName": "ApproverNote",
            "displayName": "Approver Note",
            "type": "String",
            "isOptional": true,
            "direction": "Input"
        },
        {
            "id": "44444444-4444-4444-4444-444444444444",
            "uniqueName": "InvoiceId",
            "displayName": "Invoice ID",
            "type": "EntityReference",
            "logicalEntityName": "invoice",
            "isOptional": false,
            "direction": "Input"
        }
    ],
    "responseProperties": [
        {
            "id": "55555555-5555-5555-5555-555555555555",
            "uniqueName": "ApprovalId",
            "displayName": "Approval ID",
            "type": "Guid",
            "direction": "Output"
        }
    ]
}
```

**customApis/register**

Request:
```json
{
    "uniqueName": "myorg_ApproveInvoice",
    "displayName": "Approve Invoice",
    "pluginTypeId": "22222222-2222-2222-2222-222222222222",
    "bindingType": "Entity",
    "boundEntityLogicalName": "invoice",
    "isFunction": false,
    "isPrivate": false,
    "solutionName": "MySolution"
}
```

Response:
```json
{
    "id": "11111111-1111-1111-1111-111111111111"
}
```

**customApis/addParameter**

Request:
```json
{
    "customApiId": "11111111-1111-1111-1111-111111111111",
    "uniqueName": "ApproverNote",
    "displayName": "Approver Note",
    "type": "String",
    "direction": "Input",
    "isOptional": true
}
```

Response:
```json
{
    "id": "33333333-3333-3333-3333-333333333333"
}
```

**customApis/setPlugin**

Request (link):
```json
{
    "nameOrId": "myorg_ApproveInvoice",
    "pluginTypeName": "ApproveInvoicePlugin",
    "assemblyName": "MyPlugin"
}
```

Request (unlink — `assemblyName` is ignored when `pluginTypeName` is null):
```json
{
    "nameOrId": "myorg_ApproveInvoice",
    "pluginTypeName": null
}
```

Response:
```json
{
    "success": true
}
```

**customApis/unregister**

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
    "customApisDeleted": 1,
    "parametersDeleted": 2,
    "propertiesDeleted": 1,
    "totalDeleted": 4
}
```

---

## Configuration

### registrations.json Extension

Custom APIs are added to the existing `registrations.json` schema (defined in [plugins.md](./plugins.md)) under a new `customApis` section.

```json
{
    "version": "1.0",
    "generatedAt": "2026-03-23T00:00:00Z",
    "assemblies": [ /* existing plugin assembly configs */ ],
    "customApis": [
        {
            "uniqueName": "myorg_ApproveInvoice",
            "displayName": "Approve Invoice",
            "name": "myorg_ApproveInvoice",
            "description": "Approves an invoice with optional note",
            "pluginTypeName": "MyPlugin.ApproveInvoicePlugin",
            "assemblyName": "MyPlugin",
            "bindingType": "Entity",
            "boundEntityLogicalName": "invoice",
            "allowedCustomProcessingStepType": "None",
            "isFunction": false,
            "isPrivate": false,
            "executePrivilegeName": null,
            "requestParameters": [
                {
                    "uniqueName": "ApproverNote",
                    "displayName": "Approver Note",
                    "type": "String",
                    "isOptional": true,
                    "description": null
                },
                {
                    "uniqueName": "InvoiceId",
                    "displayName": "Invoice ID",
                    "type": "EntityReference",
                    "logicalEntityName": "invoice",
                    "isOptional": false,
                    "description": null
                }
            ],
            "responseProperties": [
                {
                    "uniqueName": "ApprovalId",
                    "displayName": "Approval ID",
                    "type": "Guid",
                    "description": null
                }
            ]
        }
    ]
}
```

### Config Model Types

**CustomApiConfig:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `uniqueName` | string | "" | API unique name |
| `displayName` | string | "" | Display name |
| `name` | string | "" | Logical name |
| `description` | string? | null | Free-text description |
| `pluginTypeName` | string | "" | Fully qualified plugin type name |
| `assemblyName` | string | "" | Assembly containing the plugin type |
| `bindingType` | string | "Global" | Global, Entity, or EntityCollection |
| `boundEntityLogicalName` | string? | null | Bound entity logical name |
| `allowedCustomProcessingStepType` | string | "None" | None, AsyncOnly, or SyncAndAsync |
| `isFunction` | bool | false | GET vs POST semantics |
| `isPrivate` | bool | false | Hidden from public API docs |
| `executePrivilegeName` | string? | null | Required privilege name |
| `requestParameters` | List | [] | Input parameter configs |
| `responseProperties` | List | [] | Output property configs |
| `ExtensionData` | Dictionary? | null | Round-trip preservation |

**CustomApiParameterConfig:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `uniqueName` | string | "" | Parameter unique name |
| `displayName` | string | "" | Display name |
| `type` | string | "" | Data type name |
| `logicalEntityName` | string? | null | Entity name for entity-typed params |
| `isOptional` | bool | false | Whether optional (input only) |
| `description` | string? | null | Free-text description |
| `ExtensionData` | Dictionary? | null | Round-trip preservation |

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `CustomApi.NotFound` | Custom API name or ID doesn't exist | Use `list` to find valid names/IDs |
| `CustomApi.DuplicateName` | UniqueName already used by another API | Choose a unique name |
| `CustomApi.PluginTypeNotFound` | Referenced plugin type doesn't exist | Register plugin type first via `ppds plugins register type` |
| `CustomApi.InvalidBindingType` | BoundEntityLogicalName missing for Entity binding | Provide BoundEntityLogicalName when BindingType = Entity or EntityCollection |
| `CustomApi.InvalidParameterType` | LogicalEntityName missing for entity-typed parameter | Provide LogicalEntityName for Entity, EntityCollection, EntityReference types |
| `CustomApi.DuplicateParameter` | Parameter name already exists in the same direction | Choose a unique parameter name |
| `CustomApi.CascadeConstraint` | Parameters/properties exist on unregister without force | Use `--force` to delete children |
| `CustomApi.InvalidUniqueName` | UniqueName doesn't contain prefix separator | Use format `{prefix}_{name}` |

### Recovery Strategies

- **Not found**: Use `ppds custom-apis list` to get valid names/IDs
- **Plugin type not found**: Register the assembly and type first with `ppds plugins register assembly` and `ppds plugins register type`
- **Cascade constraint**: Add `--force` to unregister children first
- **Validation errors**: Fix the input per the validation rules table

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Custom API with no parameters | Valid — API can have zero parameters |
| Custom API with only output properties | Valid — response-only API |
| Global binding with BoundEntityLogicalName set | Validation error |
| Entity binding without BoundEntityLogicalName | Validation error |
| Output parameter marked IsOptional | IsOptional ignored (output is always required) |
| Plugin type used by both steps and Custom API | Valid — same type can back steps and an API |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Extracting assembly with `CustomApiAttribute` produces valid JSON including all API fields and parameters | `AssemblyExtractorTests.Extract_CustomApiAttribute_ProducesConfig` | 🔲 |
| AC-02 | Extracting assembly with multiple `CustomApiParameterAttribute` produces correct request/response split based on Direction | `AssemblyExtractorTests.Extract_CustomApiParameters_SplitsByDirection` | 🔲 |
| AC-03 | `ppds plugins deploy` creates `customapi` record in Dataverse with correct PluginTypeId | `CustomApiServiceTests.Deploy_CreatesCustomApi` | 🔲 |
| AC-04 | `ppds plugins deploy` creates `customapirequestparameter` records for input parameters | `CustomApiServiceTests.Deploy_CreatesRequestParameters` | 🔲 |
| AC-05 | `ppds plugins deploy` creates `customapiresponseproperty` records for output parameters | `CustomApiServiceTests.Deploy_CreatesResponseProperties` | 🔲 |
| AC-06 | `ppds plugins deploy` is idempotent — re-deploying same configuration preserves GUIDs | `CustomApiServiceTests.Deploy_Idempotent_PreservesGuids` | 🔲 |
| AC-07 | `ppds custom-apis list` returns all custom APIs with parameter counts | `CustomApiServiceTests.List_ReturnsAllWithCounts` | 🔲 |
| AC-08 | `ppds custom-apis get` returns full detail including request parameters and response properties | `CustomApiServiceTests.Get_ReturnsDetailWithParameters` | 🔲 |
| AC-09 | `ppds custom-apis register` creates custom API with Entity binding and BoundEntityLogicalName | `CustomApiServiceTests.Register_EntityBinding_SetsBoundEntity` | 🔲 |
| AC-10 | `ppds custom-apis register` with Global binding rejects BoundEntityLogicalName | `CustomApiServiceTests.Register_GlobalBinding_RejectsBoundEntity` | 🔲 |
| AC-11 | `ppds custom-apis update` modifies only specified fields, leaves others unchanged | `CustomApiServiceTests.Update_ModifiesOnlySpecifiedFields` | 🔲 |
| AC-12 | `ppds custom-apis unregister` without `--force` fails when parameters exist, returns child count | `CustomApiServiceTests.Unregister_FailsWithChildren` | 🔲 |
| AC-13 | `ppds custom-apis unregister --force` deletes parameters and properties before deleting API | `CustomApiServiceTests.Unregister_Force_DeletesChildren` | 🔲 |
| AC-14 | `ppds custom-apis add-parameter` with entity-typed parameter validates LogicalEntityName is provided | `CustomApiServiceTests.AddParameter_EntityType_RequiresLogicalEntityName` | 🔲 |
| AC-15 | Extension: Custom APIs appear as root-level mail-icon nodes in Assembly view | `PluginsPanelTests.CustomApis_ShowInAssemblyView` | 🔲 |
| AC-16 | Extension: Registration form disables BoundEntityLogicalName when BindingType = Global | `PluginsPanelTests.CustomApiForm_ConditionalBoundEntity` | 🔲 |
| AC-17 | Extension: Parameter form disables IsOptional when Direction = Output | `PluginsPanelTests.CustomApiParamForm_DisablesOptionalForOutput` | 🔲 |
| AC-18 | Extension: Parameter form enables LogicalEntityName only for Entity/EntityCollection/EntityReference types | `PluginsPanelTests.CustomApiParamForm_ConditionalEntityName` | 🔲 |
| AC-19 | RPC: `customApis/list` returns all APIs with parameter counts | `CustomApiRpcTests.List_ReturnsApis` | 🔲 |
| AC-20 | RPC: `customApis/get` returns full detail with request parameters and response properties | `CustomApiRpcTests.Get_ReturnsDetailWithParams` | 🔲 |
| AC-21 | RPC: `customApis/register` creates API and returns ID | `CustomApiRpcTests.Register_CreatesAndReturnsId` | 🔲 |
| AC-22 | RPC: `customApis/addParameter` creates parameter and returns ID | `CustomApiRpcTests.AddParameter_CreatesAndReturnsId` | 🔲 |
| AC-23 | RPC: `customApis/removeParameter` deletes parameter | `CustomApiRpcTests.RemoveParameter_Deletes` | 🔲 |
| AC-24 | TUI: Custom APIs appear as root nodes alongside packages, assemblies, and endpoints | `PluginRegistrationScreenTests.TreeShowsCustomApis` | 🔲 |
| AC-25 | TUI: Selecting custom API node shows detail panel with API info and parameter counts | `PluginRegistrationScreenTests.CustomApiDetail_ShowsInfo` | 🔲 |
| AC-26 | MCP: `customApis_list` returns all APIs (read-only) | `McpCustomApiTests.List_ReturnsAll` | 🔲 |
| AC-27 | No managed component gatekeeping — all operations available on managed custom APIs; no IsManaged check before update/unregister/remove-parameter. Dataverse is the authority. | `CustomApiServiceTests.ManagedApi_OperationsNotBlocked` | 🔲 |
| AC-28 | `ppds custom-apis set-plugin <api> --plugin <type> --assembly <name>` sets PluginTypeId on the Custom API | `CustomApiServiceTests.SetPlugin_SetsPluginTypeId` | 🔲 |
| AC-29 | `ppds custom-apis set-plugin <api> --none` clears PluginTypeId (sets to null) | `CustomApiServiceTests.SetPlugin_None_ClearsPluginTypeId` | 🔲 |
| AC-30 | `set-plugin` with non-existent plugin type throws `PpdsException` with `CustomApi.PluginTypeNotFound` | `CustomApiServiceTests.SetPlugin_InvalidType_ThrowsNotFound` | 🔲 |
| AC-31 | RPC: `customApis/setPlugin` with pluginTypeName+assemblyName sets plugin type and returns success | `CustomApiRpcTests.SetPlugin_Link_ReturnsSuccess` | 🔲 |
| AC-32 | RPC: `customApis/setPlugin` with pluginTypeName=null clears plugin type and returns success | `CustomApiRpcTests.SetPlugin_Unlink_ReturnsSuccess` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Assembly with no CustomApiAttribute | DLL with only PluginStepAttribute | Empty customApis array in config |
| Custom API with zero parameters | CustomApiAttribute only, no CustomApiParameterAttribute | Valid API with empty parameter lists |
| Multiple Custom APIs in one class | Not supported — one CustomApiAttribute per class | Validation error during extraction |
| Output parameter with IsOptional=true | CustomApiParameterAttribute(Direction=Output, IsOptional=true) | IsOptional ignored, stored as false |
| Parameter type Entity without LogicalEntityName | Type=Entity, LogicalEntityName=null | Validation error |
| Deploy with unknown plugin type | Config references non-existent type | PpdsException with CustomApi.PluginTypeNotFound |
| set-plugin with same plugin type already linked | Same pluginTypeName+assemblyName | Idempotent success (no-op update) |
| set-plugin unlink with assemblyName provided | pluginTypeName=null, assemblyName="Foo" | assemblyName ignored, plugin type cleared |

### Test Examples

```csharp
[Fact]
public void Extract_CustomApiAttribute_ProducesConfig()
{
    // Arrange
    var extractor = AssemblyExtractor.Create("TestPlugin.dll");

    // Act
    var config = extractor.Extract();

    // Assert
    config.CustomApis.Should().HaveCount(1);
    var api = config.CustomApis[0];
    api.UniqueName.Should().Be("test_ApproveInvoice");
    api.BindingType.Should().Be("Entity");
    api.BoundEntityLogicalName.Should().Be("invoice");
    api.RequestParameters.Should().HaveCount(2);
    api.ResponseProperties.Should().HaveCount(1);
}

[Fact]
public async Task Register_EntityBinding_RequiresBoundEntity()
{
    // Arrange
    var service = new CustomApiService(pool, logger);
    var registration = new CustomApiRegistration(
        UniqueName: "myorg_Test",
        DisplayName: "Test",
        PluginTypeId: existingPluginTypeId,
        BindingType: ApiBindingType.Entity,
        BoundEntityLogicalName: null); // Missing!

    // Act & Assert
    var act = () => service.RegisterAsync(registration);
    await act.Should().ThrowAsync<PpdsException>()
        .WithMessage("*BoundEntityLogicalName*required*");
}

[Fact]
public async Task AddParameter_EntityType_RequiresLogicalEntityName()
{
    // Arrange
    var service = new CustomApiService(pool, logger);
    var param = new CustomApiParameterRegistration(
        UniqueName: "TargetEntity",
        Type: ApiParameterType.EntityReference,
        Direction: ParameterDirection.Input,
        LogicalEntityName: null); // Missing!

    // Act & Assert
    var act = () => service.AddParameterAsync(customApiId, param);
    await act.Should().ThrowAsync<PpdsException>()
        .WithMessage("*LogicalEntityName*required*");
}
```

---

## Design Decisions

### Why code-first annotations?

**Context:** Custom APIs can be registered manually via the Plugin Registration Tool, Power Apps maker portal, or deployment XML. Each approach separates configuration from code.

**Decision:** Use `CustomApiAttribute` and `CustomApiParameterAttribute` on plugin classes — same philosophy as `PluginStepAttribute`.

**Alternatives considered:**
- Manual PRT registration: Rejected — not automatable, easy to get out of sync
- Separate YAML/JSON config files: Rejected — configuration drift from code
- Power Apps maker portal: Rejected — manual, no version control

**Consequences:**
- Positive: Single source of truth in code, version-controllable, type-safe
- Positive: IntelliSense support for all properties
- Positive: Consistent pattern with existing `PluginStepAttribute`
- Negative: Requires reference to PPDS.Plugins assembly

### Why Custom APIs and not custom process actions?

**Context:** Dataverse supports two mechanisms for creating custom messages: custom process actions (backed by workflows) and Custom APIs (backed by plugins). Microsoft has indicated Custom APIs are the modern replacement.

**Decision:** Support only Custom APIs. Custom process actions are explicitly out of scope.

**Alternatives considered:**
- Support both: Rejected — custom process actions are legacy, add complexity without value
- Support only custom process actions: Rejected — deprecated path

**Consequences:**
- Positive: Cleaner architecture, one mechanism to maintain
- Positive: Better performance (plugin-backed vs. workflow-backed)
- Negative: Users with existing custom process actions must migrate manually (or use PRT)

### Why separate customApis section in registrations.json?

**Context:** Custom APIs could be nested under their parent assembly's type (since each API references a plugin type), or stored as a separate top-level section.

**Decision:** Separate `customApis` section at the root level of `registrations.json`.

**Alternatives considered:**
- Nest under assembly → type: Rejected — Custom APIs are logically separate from plugin steps; nesting implies a parent-child relationship that doesn't exist at the registration level
- Separate configuration file: Rejected — fragments the deployment artifact unnecessarily

**Consequences:**
- Positive: Clear separation between plugin step registration and Custom API registration
- Positive: Assembly and type references are by name (resolved during deploy), so ordering doesn't matter
- Negative: Deploy must resolve `pluginTypeName` → PluginTypeId at deployment time

### Why BindingType is immutable but PluginTypeId is mutable?

**Context:** Dataverse does not allow changing `BindingType` or `BoundEntityLogicalName` on an existing `customapi` record. However, `PluginTypeId` *can* be changed via direct update — it is the reference to the implementing plugin, not a structural property of the API.

**Decision:** Exclude `BindingType` and `BoundEntityLogicalName` from `CustomApiUpdateRequest`. Provide a dedicated `SetPluginTypeAsync` method for changing or clearing the implementing plugin type, keeping it separate from general updates because it requires plugin type resolution by name and assembly.

**Alternatives considered:**
- Include PluginTypeId in CustomApiUpdateRequest: Rejected — the RPC and CLI interfaces resolve by name+assembly, not raw Guid. A separate method with a clear contract is cleaner.
- Require unregister + re-register to change plugin type: Rejected — PluginTypeId is mutable in Dataverse, and forcing re-registration loses the entity GUID and breaks references.

**Consequences:**
- Positive: Binding is truly immutable (matches Dataverse enforcement)
- Positive: Plugin type can be changed without losing the Custom API's GUID
- Positive: Dedicated command is discoverable (`set-plugin`) vs. buried in update flags
- Negative: One more service method and RPC endpoint

---

## Extension Points

### Adding a New Parameter Type

If Dataverse adds new parameter types in future:

1. **Add value to `ApiParameterType` enum** in `src/PPDS.Plugins/Enums/ApiParameterType.cs`
2. **Update extractor** in `AssemblyExtractor` to map the new type
3. **Update validation** if the new type requires `LogicalEntityName`

### Adding Custom API to a New Surface

1. **Add RPC handler** following the `customApis/` namespace pattern
2. **Call `ICustomApiService`** — never duplicate business logic in UI code
3. **Update this spec** with surface-specific behavior

---

## Related Specs

- [plugins.md](./plugins.md) - Shared Plugins panel container, plugin type registration, extract/deploy workflow
- [architecture.md](./architecture.md) - Application Services pattern used by ICustomApiService
- [connection-pooling.md](./connection-pooling.md) - Connection pool used for Dataverse access
- [service-endpoints.md](./service-endpoints.md) - Another entity type in the shared Plugins panel
- [data-providers.md](./data-providers.md) - Another entity type in the shared Plugins panel

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-26 | v1 completion: add set-plugin command (#427), remove IsManaged gatekeeping (#660), add new ACs |
| 2026-03-23 | Initial spec |

---

## Roadmap

- Custom API privilege management (create/assign security privileges)
- Custom API testing tool (invoke API directly from Extension/CLI with sample payloads)
- Import/export custom API definitions independent of full plugin deployment
- Bulk parameter management (add/remove multiple parameters in one operation)
- Custom API usage analytics (show which APIs are called most frequently via plugin traces)
