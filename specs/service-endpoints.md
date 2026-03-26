# Service Endpoints

**Status:** Draft
**Last Updated:** 2026-03-23
**Code:** [src/PPDS.Cli/Services/](../src/PPDS.Cli/Services/) | [src/PPDS.Extension/src/panels/](../src/PPDS.Extension/src/panels/)
**Surfaces:** All

---

## Overview

Service endpoints enable event-driven integration by delivering Dataverse pipeline events to external systems. Webhooks are HTTP endpoints that receive event payloads via POST requests; service endpoints are Azure Service Bus destinations (Queue, Topic, EventHub). Both use the same Dataverse entity (`serviceendpoint`) and appear as root-level nodes in the shared Plugins panel alongside plugin assemblies.

### Goals

- **Register and manage**: Full CRUD for service endpoints and webhooks across all surfaces (CLI, TUI, Extension, MCP)
- **Full transparency**: Show all endpoints — no hiding, no artificial gatekeeping on managed components

### Non-Goals

- Azure Event Grid, Managed Data Lake, Container Storage (future contract types)
- Plugin profiler integration (deferred)
- Step registration on service endpoints (covered by step registration in [plugins.md](./plugins.md) — use `--event-handler-type serviceEndpoint` with `ppds plugins register step`, or `eventHandlerType: "serviceEndpoint"` in RPC `plugins/registerStep`)

---

## Architecture

```
┌──────────────────┐     ┌──────────────────┐     ┌──────────────────┐
│   CLI Commands   │     │  Extension Panel  │     │    TUI Screen    │
│  service-endpts  │     │  (PluginsPanel)   │     │ (PluginReg...)   │
└────────┬─────────┘     └────────┬──────────┘     └────────┬─────────┘
         │                        │                         │
         │              ┌─────────▼──────────┐              │
         └─────────────▶│   RPC Endpoints    │◀─────────────┘
                        │ serviceEndpoints/* │
                        └─────────┬──────────┘
                                  │
                        ┌─────────▼──────────┐
                        │ IServiceEndpoint   │
                        │     Service        │
                        └─────────┬──────────┘
                                  │
                        ┌─────────▼──────────┐
                        │     Dataverse      │
                        │  serviceendpoint   │
                        └────────────────────┘
```

Service endpoints share the Plugins panel container defined in [plugins.md](./plugins.md). The service layer is `IServiceEndpointService` (new, follows the Application Services pattern). CLI commands live under `ppds service-endpoints`. RPC endpoints use the `serviceEndpoints/` namespace.

### Components

| Component | Responsibility |
|-----------|----------------|
| `IServiceEndpointService` | CRUD operations for `serviceendpoint` entity |
| `ServiceEndpointCommands` | CLI command group under `ppds service-endpoints` |
| `PluginsPanel` | Shared Extension webview — renders endpoint nodes alongside assemblies |
| `PluginRegistrationScreen` | Shared TUI screen — renders endpoint nodes in tree |

### Dependencies

- Depends on: [plugins.md](./plugins.md) (shared Plugins panel, step registration)
- Uses patterns from: [architecture.md](./architecture.md) (Application Services, Connection Pooling)
- Uses: [connection-pooling.md](./connection-pooling.md) for Dataverse access

---

## Specification

### Dataverse Entity: `serviceendpoint`

All service endpoints and webhooks are stored in the same Dataverse entity. The `Contract` field determines the endpoint type and which fields are relevant.

#### Contract Types

| Contract | Value | Description |
|----------|-------|-------------|
| OneWay | 1 | One-way Service Bus contract (legacy) |
| Queue | 2 | Azure Service Bus Queue |
| Rest | 3 | REST endpoint (legacy) |
| TwoWay | 4 | Two-way Service Bus contract (legacy) |
| Topic | 5 | Azure Service Bus Topic |
| EventHub | 7 | Azure Event Hub |
| Webhook | 8 | HTTP Webhook |

PPDS focuses on Queue (2), Topic (5), EventHub (7), and Webhook (8). Legacy contracts (OneWay, TwoWay, Rest) are displayed read-only.

#### Common Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `Name` | string | Yes | Display name, must be unique across all service endpoints |
| `Description` | string | No | Free-text description |
| `Contract` | OptionSet | Yes | Endpoint type (see table above) |

#### Service Bus Fields (Queue, Topic, EventHub)

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `NamespaceAddress` | string | Yes | Service Bus namespace URI (sb:// prefix) |
| `Path` | string | Yes | Queue name, topic name, or event hub name |
| `SolutionNamespace` | string | Derived | Extracted from `NamespaceAddress` (the namespace portion) |
| `AuthType` | OptionSet | Yes | SASKey or SASToken |
| `SASKeyName` | string | Conditional | Required when AuthType = SASKey |
| `SASKey` | string | Conditional | Required when AuthType = SASKey (exactly 44 characters) |
| `SASToken` | string | Conditional | Required when AuthType = SASToken |
| `MessageFormat` | OptionSet | Yes | .NETBinary (1), XML (2), JSON (3) |
| `UserClaim` | OptionSet | Yes | None (0) or UserId (1) |
| `ConnectionMode` | OptionSet | Yes | Normal (1) or Federated (2) — always Normal |

EventHub excludes .NETBinary from MessageFormat options.

#### Webhook Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `Url` | string | Yes | Absolute HTTP(S) URI |
| `AuthType` | OptionSet | Yes | HttpHeader (5), WebhookKey (4), HttpQueryString (6) |
| `AuthValue` | string | Yes | Varies by AuthType (see below) |

**AuthValue format by AuthType:**

| AuthType | Format | Example |
|----------|--------|---------|
| WebhookKey (4) | Plain string | `abc123secret` |
| HttpHeader (5) | XML settings | `<settings><setting name="x-api-key" value="abc123" /></settings>` |
| HttpQueryString (6) | XML settings | `<settings><setting name="code" value="abc123" /></settings>` |

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| Name | Non-empty, unique across all service endpoints | "Name is required" / "Name must be unique" |
| NamespaceAddress | Must start with `sb://` | "Namespace address must use sb:// scheme" |
| SASKey | Exactly 44 characters when AuthType = SASKey | "SAS key must be exactly 44 characters" |
| URL | Well-formed absolute URI (http:// or https://) | "URL must be a valid absolute URI" |
| AuthValue (HttpHeader/QueryString) | At least one key/value pair in XML settings | "At least one auth key/value pair is required" |
| Path | Non-empty for Queue, Topic, EventHub | "Path is required" |
| MessageFormat | Not .NETBinary when Contract = EventHub | ".NETBinary format is not supported for Event Hub" |

### Core Requirements

1. Service endpoints and webhooks are created, read, updated, and deleted through `IServiceEndpointService`
2. All operations use the connection pool — never store or hold a single client
3. Service endpoints appear as root-level nodes in the shared Plugins panel (Assembly view)
4. Child steps registered on a service endpoint are displayed underneath the endpoint node
5. No managed component gatekeeping — all operations available on all items
6. Operations >1 second accept `IProgressReporter` (Constitution A3): cascade unregister

### Primary Flows

**Register Webhook:**

1. **Validate**: Check name uniqueness, URL well-formedness, auth configuration
2. **Create**: Create `serviceendpoint` record with Contract=Webhook(8), Url, AuthType, AuthValue
3. **Solution**: Optionally add to solution
4. **Confirm**: Return endpoint ID and summary

**Register Service Bus Endpoint:**

1. **Validate**: Check name uniqueness, namespace address scheme, SAS key length, path non-empty
2. **Create**: Create `serviceendpoint` record with Contract (Queue/Topic/EventHub), namespace, path, auth, message format
3. **Solution**: Optionally add to solution
4. **Confirm**: Return endpoint ID and summary

**Update Endpoint:**

1. **Lookup**: Find by name or ID
2. **Validate**: Apply contract-specific validation to changed fields
3. **Update**: Update `serviceendpoint` record
4. **Confirm**: Return updated summary

**Unregister Endpoint:**

1. **Lookup**: Find by name or ID
2. **Check children**: Query for steps registered on this endpoint
3. **Cascade**: If steps exist and `force` is true, delete steps first; if `force` is false, return error with child count
4. **Delete**: Delete `serviceendpoint` record
5. **Confirm**: Return deletion summary

### Surface-Specific Behavior

#### CLI Surface

Commands live under `ppds service-endpoints`. All commands support `--json` for machine-readable output.

**`ppds service-endpoints list`**

Lists all service endpoints and webhooks.

```bash
ppds service-endpoints list
ppds service-endpoints list --json
```

Output (table format):

```
Name                    Type        URL / Namespace              Steps
────────────────────    ────────    ─────────────────────────    ─────
My Webhook              Webhook     https://example.com/hook     2
Order Queue             Queue       sb://myorg.servicebus.net    5
Audit Topic             Topic       sb://myorg.servicebus.net    1
Telemetry Hub           EventHub    sb://myorg.servicebus.net    0
```

**`ppds service-endpoints get <name-or-id>`**

Shows detailed information for a specific endpoint.

```bash
ppds service-endpoints get "My Webhook"
ppds service-endpoints get 12345678-1234-1234-1234-123456789abc
```

**`ppds service-endpoints register webhook`**

```bash
ppds service-endpoints register webhook "My Webhook" \
    --url https://example.com/hook \
    --auth-type WebhookKey \
    --auth-value "mysecretkey123"

ppds service-endpoints register webhook "Header Auth Hook" \
    --url https://example.com/hook \
    --auth-type HttpHeader \
    --auth-key x-api-key \
    --auth-value "abc123"
```

| Option | Required | Description |
|--------|----------|-------------|
| `<name>` | Yes | Endpoint display name |
| `--url` | Yes | Webhook URL (absolute URI) |
| `--auth-type` | Yes | WebhookKey, HttpHeader, or HttpQueryString |
| `--auth-value` | Yes | Secret value (plain string for WebhookKey) |
| `--auth-key` | Conditional | Header/query param name (required for HttpHeader/HttpQueryString) |
| `--description` | No | Endpoint description |
| `--solution` | No | Solution unique name |

**`ppds service-endpoints register queue|topic|eventhub`**

```bash
ppds service-endpoints register queue "Order Queue" \
    --namespace sb://myorg.servicebus.windows.net \
    --path order-processing \
    --sas-key-name RootManageSharedAccessKey \
    --sas-key "base64encodedkey44charslong1234567890ABCD=="

ppds service-endpoints register topic "Audit Topic" \
    --namespace sb://myorg.servicebus.windows.net \
    --path audit-events \
    --sas-key-name RootManageSharedAccessKey \
    --sas-key "base64encodedkey44charslong1234567890ABCD==" \
    --message-format JSON

ppds service-endpoints register eventhub "Telemetry Hub" \
    --namespace sb://myorg.servicebus.windows.net \
    --path telemetry \
    --sas-key-name RootManageSharedAccessKey \
    --sas-key "base64encodedkey44charslong1234567890ABCD==" \
    --message-format JSON
```

| Option | Required | Description |
|--------|----------|-------------|
| `<name>` | Yes | Endpoint display name |
| `--namespace` | Yes | Service Bus namespace (sb:// URI) |
| `--path` | Yes | Queue, topic, or event hub name |
| `--sas-key-name` | Conditional | SAS key name (required for SASKey auth) |
| `--sas-key` | Conditional | SAS key value (required for SASKey auth, 44 chars) |
| `--sas-token` | Conditional | SAS token (required for SASToken auth) |
| `--message-format` | No | NETBinary, XML, or JSON (default: JSON) |
| `--user-claim` | No | None or UserId (default: None) |
| `--description` | No | Endpoint description |
| `--solution` | No | Solution unique name |

**`ppds service-endpoints update <name-or-id>`**

```bash
ppds service-endpoints update "My Webhook" --url https://new-url.com/hook
ppds service-endpoints update "Order Queue" --path new-queue-name
```

**`ppds service-endpoints unregister <name-or-id>`**

```bash
ppds service-endpoints unregister "My Webhook"
ppds service-endpoints unregister "Order Queue" --force
```

| Option | Description |
|--------|-------------|
| `--force` | Delete child steps before unregistering endpoint |

#### Extension Surface

Service endpoints and webhooks appear as root-level nodes in the Assembly view of the shared Plugins panel (defined in [plugins.md](./plugins.md)).

**Node rendering:**

| Node Type | Icon | Label Format |
|-----------|------|-------------|
| Webhook | globe icon | `{Name}` |
| Service Bus (Queue/Topic/EventHub) | satellite icon | `{Name} ({Contract})` |
| Child Step | same as plugin steps | Same format as plugin step nodes |

**Tree structure (Assembly view):**

```
├─ 📦 MyPlugin.Package (1.0.0)
│  └─ ⚙️ MyPlugin.dll
│     └─ ...
├─ 🌐 My Webhook
│  └─ ⚡ Update of account (PostOp, Async)
├─ 📡 Order Queue (Queue)
│  ├─ ⚡ Create of order (PostOp, Async)
│  └─ ⚡ Update of order (PostOp, Async)
├─ 📡 Audit Topic (Topic)
│  └─ ⚡ Update of account (PostOp, Async)
└─ 📡 Telemetry Hub (EventHub)
```

**Context menu actions:**

| Action | Behavior |
|--------|----------|
| Update | Opens endpoint update form |
| Unregister | Confirmation dialog with cascade preview → `serviceEndpoints/unregister` RPC |
| Register Step | Opens step registration form pre-bound to this endpoint |

**Registration forms:**

The registration form uses conditional field visibility based on the selected contract type and auth type.

**Contract type selection determines visible fields:**

| Field | Webhook | Queue | Topic | EventHub |
|-------|---------|-------|-------|----------|
| URL | Yes | — | — | — |
| Auth Type (HTTP) | Yes | — | — | — |
| Auth Key/Value | Yes | — | — | — |
| Namespace Address | — | Yes | Yes | Yes |
| Path | — | Yes | Yes | Yes |
| Auth Type (SAS) | — | Yes | Yes | Yes |
| SAS Key Name/Key | — | Conditional | Conditional | Conditional |
| SAS Token | — | Conditional | Conditional | Conditional |
| Message Format | — | All 3 | All 3 | XML, JSON only |
| User Claim | — | Yes | Yes | Yes |

**Auth type selection determines visible auth fields:**

For webhooks:
- WebhookKey → single Auth Value text input
- HttpHeader → Auth Key + Auth Value text inputs
- HttpQueryString → Auth Key + Auth Value text inputs

For Service Bus:
- SASKey → SAS Key Name + SAS Key text inputs
- SASToken → SAS Token text input

**Detail panel:**

Selecting an endpoint node shows its properties in the detail panel. Webhook detail shows Name, Description, URL, Auth Type (auth values are write-only — not displayed). Service Bus detail shows Name, Description, Contract, Namespace, Path, Message Format, User Claim, Connection Mode.

#### TUI Surface

Service endpoints and webhooks appear as root-level nodes in the `PluginRegistrationScreen` tree, loaded alongside packages and standalone assemblies.

**Tree display:**

```
├─ Package A
│  └─ Assembly A1
│     └─ ...
├─ Assembly B
├─ 🌐 My Webhook
│  └─ Step S1
├─ 📡 Order Queue
│  ├─ Step S2
│  └─ Step S3
└─ 📡 Audit Topic
```

**Interactions:**

| Hotkey | Action |
|--------|--------|
| Delete | Open `ConfirmDestructiveActionDialog` with cascade preview |
| Enter | Show detail panel for selected endpoint |
| F5 | Refresh tree (reloads endpoints alongside assemblies) |

No creation from TUI — use CLI or Extension to register new endpoints. Update and unregister are available.

#### MCP Surface

Read-only tool for AI-assisted browsing.

| Tool | Parameters | Returns |
|------|-----------|---------|
| `serviceEndpoints_list` | (none) | All endpoints and webhooks with child step counts |

---

## Core Types

### IServiceEndpointService

Service interface for all service endpoint and webhook CRUD operations.

```csharp
public interface IServiceEndpointService
{
    Task<List<ServiceEndpointInfo>> ListAsync(CancellationToken cancellationToken = default);
    Task<ServiceEndpointInfo?> GetByNameOrIdAsync(string nameOrId, CancellationToken cancellationToken = default);
    Task<Guid> RegisterAsync(ServiceEndpointRegistration registration, string? solutionName = null, CancellationToken cancellationToken = default);
    Task UpdateAsync(Guid id, ServiceEndpointUpdateRequest request, CancellationToken cancellationToken = default);
    Task<UnregisterResult> UnregisterAsync(Guid id, bool force = false, CancellationToken cancellationToken = default);
}
```

### ServiceEndpointInfo

Return type for query and lookup operations.

| Property | Type | Description |
|----------|------|-------------|
| `Id` | Guid | Service endpoint ID |
| `Name` | string | Display name |
| `Description` | string? | Free-text description |
| `Contract` | string | Queue, Topic, EventHub, Webhook, OneWay, TwoWay, Rest |
| `NamespaceAddress` | string? | Service Bus namespace URI |
| `Path` | string? | Queue/topic/event hub name |
| `Url` | string? | Webhook URL |
| `AuthType` | string? | Authentication type |
| `MessageFormat` | string? | Message serialization format |
| `UserClaim` | string? | User claim setting |
| `ConnectionMode` | string? | Normal or Federated |
| `IsManaged` | bool | Whether this is a managed solution component |
| `StepCount` | int | Number of child steps |
| `CreatedOn` | DateTime? | Creation timestamp |
| `ModifiedOn` | DateTime? | Last modified timestamp |

### ServiceEndpointRegistration

Request type for creating endpoints. Uses a discriminated approach — contract type determines which fields are relevant.

```csharp
public record ServiceEndpointRegistration(
    string Name,
    ServiceEndpointContract Contract,
    string? Description = null,
    // Webhook fields
    string? Url = null,
    WebhookAuthType? WebhookAuthType = null,
    string? AuthKey = null,
    string? AuthValue = null,
    // Service Bus fields
    string? NamespaceAddress = null,
    string? Path = null,
    SasAuthType? SasAuthType = null,
    string? SasKeyName = null,
    string? SasKey = null,
    string? SasToken = null,
    MessageFormat? MessageFormat = null,
    UserClaim? UserClaim = null
);
```

### ServiceEndpointUpdateRequest

Request type for updating endpoints. All fields optional — only non-null fields are applied.

```csharp
public record ServiceEndpointUpdateRequest(
    string? Name = null,
    string? Description = null,
    string? Url = null,
    string? AuthKey = null,
    string? AuthValue = null,
    string? NamespaceAddress = null,
    string? Path = null,
    string? SasKeyName = null,
    string? SasKey = null,
    string? SasToken = null,
    MessageFormat? MessageFormat = null,
    UserClaim? UserClaim = null
);
```

### Enums

```csharp
public enum ServiceEndpointContract
{
    OneWay = 1,
    Queue = 2,
    Rest = 3,
    TwoWay = 4,
    Topic = 5,
    EventHub = 7,
    Webhook = 8
}

public enum WebhookAuthType
{
    WebhookKey = 4,
    HttpHeader = 5,
    HttpQueryString = 6
}

public enum SasAuthType
{
    SASKey = 1,
    SASToken = 2
}

public enum MessageFormat
{
    NETBinary = 1,
    XML = 2,
    JSON = 3
}

public enum UserClaim
{
    None = 0,
    UserId = 1
}
```

---

## API/Contracts

### RPC Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| POST | `serviceEndpoints/list` | List all endpoints and webhooks with child steps |
| POST | `serviceEndpoints/get` | Get details for specific endpoint |
| POST | `serviceEndpoints/register` | Create endpoint or webhook |
| POST | `serviceEndpoints/update` | Modify endpoint |
| POST | `serviceEndpoints/unregister` | Delete with cascade |

### Request/Response Examples

**serviceEndpoints/list**

Request:
```json
{}
```

Response:
```json
{
    "endpoints": [
        {
            "id": "12345678-1234-1234-1234-123456789abc",
            "name": "My Webhook",
            "description": "Sends order updates",
            "contract": "Webhook",
            "url": "https://example.com/hook",
            "authType": "WebhookKey",
            "isManaged": false,
            "stepCount": 2,
            "createdOn": "2026-01-15T10:30:00Z",
            "modifiedOn": "2026-02-20T14:45:00Z"
        },
        {
            "id": "87654321-4321-4321-4321-cba987654321",
            "name": "Order Queue",
            "description": null,
            "contract": "Queue",
            "namespaceAddress": "sb://myorg.servicebus.windows.net",
            "path": "order-processing",
            "authType": "SASKey",
            "messageFormat": "JSON",
            "userClaim": "None",
            "connectionMode": "Normal",
            "isManaged": false,
            "stepCount": 5,
            "createdOn": "2026-01-10T08:00:00Z",
            "modifiedOn": "2026-03-01T12:00:00Z"
        }
    ]
}
```

**serviceEndpoints/get**

Request:
```json
{
    "nameOrId": "My Webhook"
}
```

Response:
```json
{
    "id": "12345678-1234-1234-1234-123456789abc",
    "name": "My Webhook",
    "description": "Sends order updates",
    "contract": "Webhook",
    "url": "https://example.com/hook",
    "authType": "WebhookKey",
    "isManaged": false,
    "stepCount": 2,
    "steps": [
        {
            "id": "aaaabbbb-cccc-dddd-eeee-ffffggggaaaa",
            "name": "Update of account (PostOp, Async)",
            "message": "Update",
            "primaryEntity": "account",
            "stage": "PostOperation",
            "mode": "Asynchronous",
            "isEnabled": true
        }
    ]
}
```

**serviceEndpoints/register**

Request (Webhook):
```json
{
    "name": "My Webhook",
    "contract": "Webhook",
    "url": "https://example.com/hook",
    "webhookAuthType": "WebhookKey",
    "authValue": "mysecretkey123",
    "description": "Sends order updates"
}
```

Request (Queue):
```json
{
    "name": "Order Queue",
    "contract": "Queue",
    "namespaceAddress": "sb://myorg.servicebus.windows.net",
    "path": "order-processing",
    "sasAuthType": "SASKey",
    "sasKeyName": "RootManageSharedAccessKey",
    "sasKey": "base64encodedkey44charslong1234567890ABCD==",
    "messageFormat": "JSON",
    "userClaim": "None"
}
```

Response:
```json
{
    "id": "12345678-1234-1234-1234-123456789abc"
}
```

**serviceEndpoints/unregister**

Request:
```json
{
    "id": "12345678-1234-1234-1234-123456789abc",
    "force": true
}
```

Response:
```json
{
    "endpointsDeleted": 1,
    "stepsDeleted": 2,
    "imagesDeleted": 1,
    "totalDeleted": 4
}
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `ServiceEndpoint.NotFound` | Endpoint name or ID doesn't exist | Use `list` to find valid names/IDs |
| `ServiceEndpoint.DuplicateName` | Name already used by another endpoint | Choose a unique name |
| `ServiceEndpoint.InvalidUrl` | Webhook URL is not a well-formed absolute URI | Fix URL format |
| `ServiceEndpoint.InvalidSasKey` | SAS key is not exactly 44 characters | Provide correct SAS key |
| `ServiceEndpoint.InvalidNamespace` | Namespace address doesn't start with `sb://` | Use `sb://` prefix |
| `ServiceEndpoint.MissingAuth` | No auth key/value pair for HttpHeader/HttpQueryString | Provide at least one key/value pair |
| `ServiceEndpoint.CascadeConstraint` | Child steps exist on unregister without force | Use `--force` to delete children |
| `ServiceEndpoint.InvalidMessageFormat` | .NETBinary used with EventHub | Use XML or JSON for EventHub |

### Recovery Strategies

- **Not found**: Use `ppds service-endpoints list` to get valid names/IDs
- **Cascade constraint**: Add `--force` to unregister children first
- **Validation errors**: Fix the input per the validation rules table

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Endpoint with no steps | Unregister succeeds without `--force` |
| Update webhook URL only | Other fields (auth) remain unchanged |
| SAS key with trailing whitespace | Trim before length check |
| Legacy contract type (OneWay/TwoWay/Rest) | Displayed in list/get but cannot be created via PPDS |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `register webhook` with WebhookKey auth creates `serviceendpoint` record with Contract=8, correct URL and auth value | `ServiceEndpointServiceTests.Register_Webhook_WebhookKey_CreatesRecord` | 🔲 |
| AC-02 | `register webhook` with HttpHeader auth creates `serviceendpoint` with XML-formatted AuthValue containing key/value pair | `ServiceEndpointServiceTests.Register_Webhook_HttpHeader_CreatesXmlAuth` | 🔲 |
| AC-03 | `register webhook` with HttpQueryString auth creates `serviceendpoint` with XML-formatted AuthValue | `ServiceEndpointServiceTests.Register_Webhook_HttpQueryString_CreatesXmlAuth` | 🔲 |
| AC-04 | `register queue` creates `serviceendpoint` with Contract=2, namespace, path, SAS auth, message format | `ServiceEndpointServiceTests.Register_Queue_CreatesRecord` | 🔲 |
| AC-05 | `register topic` creates `serviceendpoint` with Contract=5 | `ServiceEndpointServiceTests.Register_Topic_CreatesRecord` | 🔲 |
| AC-06 | `register eventhub` creates `serviceendpoint` with Contract=7 and rejects .NETBinary format | `ServiceEndpointServiceTests.Register_EventHub_RejectsNETBinary` | 🔲 |
| AC-07 | `list` returns all endpoints and webhooks with contract type, URL/namespace, and step counts | `ServiceEndpointServiceTests.List_ReturnsAllEndpoints` | 🔲 |
| AC-08 | `get` returns full detail including child steps | `ServiceEndpointServiceTests.Get_ReturnsDetailWithSteps` | 🔲 |
| AC-09 | `update` modifies only specified fields, leaves others unchanged | `ServiceEndpointServiceTests.Update_ModifiesOnlySpecifiedFields` | 🔲 |
| AC-10 | `unregister` without `--force` fails when child steps exist, returns child count | `ServiceEndpointServiceTests.Unregister_FailsWithChildren` | 🔲 |
| AC-11 | `unregister --force` deletes child steps then endpoint, returns deletion counts | `ServiceEndpointServiceTests.Unregister_Force_DeletesChildren` | 🔲 |
| AC-12 | SAS key validation rejects keys that are not exactly 44 characters | `ServiceEndpointServiceTests.Validate_SasKey_MustBe44Chars` | 🔲 |
| AC-13 | Name uniqueness validation rejects duplicate endpoint names | `ServiceEndpointServiceTests.Validate_Name_MustBeUnique` | 🔲 |
| AC-14 | Extension: Webhooks appear with globe icon and Service Bus endpoints with satellite icon in Assembly view | `PluginsPanelTests.ServiceEndpoints_ShowCorrectIcons` | 🔲 |
| AC-15 | Extension: Registration form shows conditional fields based on contract type selection | `PluginsPanelTests.ServiceEndpointForm_ConditionalFields` | 🔲 |
| AC-16 | Extension: Registration form shows conditional auth fields based on auth type selection | `PluginsPanelTests.ServiceEndpointForm_ConditionalAuthFields` | 🔲 |
| AC-17 | RPC: `serviceEndpoints/list` returns all endpoints with child step counts | `ServiceEndpointRpcTests.List_ReturnsEndpoints` | 🔲 |
| AC-18 | RPC: `serviceEndpoints/register` creates endpoint and returns ID | `ServiceEndpointRpcTests.Register_CreatesAndReturnsId` | 🔲 |
| AC-19 | RPC: `serviceEndpoints/unregister` with force deletes endpoint and children | `ServiceEndpointRpcTests.Unregister_CascadeDeletes` | 🔲 |
| AC-20 | TUI: Service endpoints appear as root nodes alongside packages and assemblies | `PluginRegistrationScreenTests.TreeShowsServiceEndpoints` | 🔲 |
| AC-21 | MCP: `serviceEndpoints_list` returns all endpoints (read-only) | `McpServiceEndpointTests.List_ReturnsAll` | 🔲 |
| AC-22 | No managed component gatekeeping — all operations available on managed endpoints; no IsManaged check before update/unregister. Dataverse is the authority. | `ServiceEndpointServiceTests.ManagedEndpoint_OperationsNotBlocked` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Endpoint with no steps | Unregister without force | Succeeds, returns 1 endpoint deleted |
| SAS key exactly 44 chars | Valid base64 key | Registration succeeds |
| SAS key 43 or 45 chars | Invalid length key | Validation error |
| Webhook URL without scheme | `example.com/hook` | Validation error: must be absolute URI |
| Webhook URL with http:// | `http://example.com/hook` | Registration succeeds (not HTTPS-only) |
| EventHub with .NETBinary | Contract=EventHub, Format=NETBinary | Validation error |
| Empty auth key for HttpHeader | `--auth-type HttpHeader --auth-key ""` | Validation error: auth key required |
| Legacy contract in list | OneWay endpoint exists in env | Displayed in list with contract type shown |

### Test Examples

```csharp
[Fact]
public async Task Register_Webhook_WebhookKey_CreatesEndpoint()
{
    // Arrange
    var service = new ServiceEndpointService(pool, logger);
    var registration = new ServiceEndpointRegistration(
        Name: "Test Webhook",
        Contract: ServiceEndpointContract.Webhook,
        Url: "https://example.com/hook",
        WebhookAuthType: WebhookAuthType.WebhookKey,
        AuthValue: "mysecretkey123");

    // Act
    var id = await service.RegisterAsync(registration);

    // Assert
    id.Should().NotBeEmpty();
}

[Fact]
public async Task Register_Queue_InvalidSasKey_ThrowsValidation()
{
    // Arrange
    var service = new ServiceEndpointService(pool, logger);
    var registration = new ServiceEndpointRegistration(
        Name: "Test Queue",
        Contract: ServiceEndpointContract.Queue,
        NamespaceAddress: "sb://myorg.servicebus.windows.net",
        Path: "test-queue",
        SasAuthType: SasAuthType.SASKey,
        SasKeyName: "RootManageSharedAccessKey",
        SasKey: "tooshort");

    // Act & Assert
    var act = () => service.RegisterAsync(registration);
    await act.Should().ThrowAsync<PpdsException>()
        .WithMessage("*44 characters*");
}

[Fact]
public async Task Unregister_WithChildren_NoForce_ThrowsCascadeError()
{
    // Arrange
    var service = new ServiceEndpointService(pool, logger);
    var endpointId = /* endpoint with 2 child steps */;

    // Act & Assert
    var act = () => service.UnregisterAsync(endpointId, force: false);
    await act.Should().ThrowAsync<PpdsException>()
        .WithMessage("*2 step*");
}
```

---

## Design Decisions

### Why webhooks and service endpoints in one spec?

**Context:** Webhooks and service endpoints use different protocols (HTTP vs. Azure Service Bus) but share the same Dataverse entity (`serviceendpoint`), appear in the same UI locations, and share the same service layer.

**Decision:** Combine into a single spec covering all `serviceendpoint` contract types.

**Alternatives considered:**
- Separate specs for webhooks and service endpoints: Rejected — would duplicate shared entity fields, validation rules, and UI integration patterns

**Consequences:**
- Positive: Single source of truth for the `serviceendpoint` entity
- Positive: Shared UI, CLI, and RPC patterns documented once
- Negative: Spec is somewhat larger, covering multiple contract types

### Why no Federated connection mode?

**Context:** The `ConnectionMode` field on `serviceendpoint` supports Normal (1) and Federated (2). The Plugin Registration Tool always sets Normal and does not expose Federated in its UI.

**Decision:** Default to Normal. Do not expose ConnectionMode as a user-facing option.

**Alternatives considered:**
- Expose as advanced option: Rejected — Federated mode is extremely rare and poorly documented by Microsoft

**Consequences:**
- Positive: Simpler registration forms and CLI options
- Negative: Users needing Federated mode must use other tools (this is acceptable given rarity)

### Why auth values are write-only?

**Context:** SAS keys, SAS tokens, webhook keys, and HTTP auth values are sensitive credentials. Dataverse returns null for these fields on read operations.

**Decision:** Display auth type but not auth values in detail views. Show "(set)" indicator when a value exists.

**Alternatives considered:**
- Attempt to display values: Not possible — Dataverse doesn't return them
- Hide auth type entirely: Rejected — users need to know which auth mechanism is configured

**Consequences:**
- Positive: Consistent with Dataverse security model
- Positive: No accidental credential exposure in logs or screenshots
- Negative: Users cannot verify exact credential values after registration

---

## Related Specs

- [plugins.md](./plugins.md) - Shared Plugins panel container, step registration (steps can bind to service endpoints as event handlers)
- [architecture.md](./architecture.md) - Application Services pattern used by IServiceEndpointService
- [connection-pooling.md](./connection-pooling.md) - Connection pool used for Dataverse access
- [custom-apis.md](./custom-apis.md) - Another entity type in the shared Plugins panel

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-26 | v1 completion: remove IsManaged gatekeeping (#660), clarify step registration cross-reference (#65) |
| 2026-03-23 | Initial spec |

---

## Roadmap

- Azure Event Grid contract support
- Managed Data Lake export endpoints
- Container Storage endpoints
- Bulk import/export of endpoint configurations
- SAS token rotation workflow (detect expiring tokens, prompt renewal)
