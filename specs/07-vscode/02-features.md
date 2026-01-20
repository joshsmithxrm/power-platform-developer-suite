# VS Code Extension: Features

## Overview

The VS Code Extension exposes Power Platform capabilities through JSON-RPC methods provided by the `ppds serve` daemon. This specification documents all available features accessible via RPC, their parameters, response types, and usage patterns. The extension currently implements one UI command (`ppds.listProfiles`) with the full RPC interface available for expansion.

## Feature Categories

### Authentication (3 methods)

| Method | Purpose | Implemented in Extension |
|--------|---------|-------------------------|
| `auth/list` | List all profiles | Yes (`ppds.listProfiles`) |
| `auth/who` | Get active profile details | No |
| `auth/select` | Switch active profile | No |

### Environment (2 methods)

| Method | Purpose | Implemented in Extension |
|--------|---------|-------------------------|
| `env/list` | Discover available environments | No |
| `env/select` | Select environment for profile | No |

### Data Query (2 methods)

| Method | Purpose | Implemented in Extension |
|--------|---------|-------------------------|
| `query/sql` | Execute SQL queries | No |
| `query/fetch` | Execute FetchXML queries | No |

### Plugins (1 method)

| Method | Purpose | Implemented in Extension |
|--------|---------|-------------------------|
| `plugins/list` | List registered plugins | No |

### Solutions (2 methods)

| Method | Purpose | Implemented in Extension |
|--------|---------|-------------------------|
| `solutions/list` | List solutions | No |
| `solutions/components` | Get solution components | No |

### Schema (1 method)

| Method | Purpose | Implemented in Extension |
|--------|---------|-------------------------|
| `schema/list` | Get entity schema | No (placeholder) |

### Profile Management (1 method)

| Method | Purpose | Implemented in Extension |
|--------|---------|-------------------------|
| `profiles/invalidate` | Invalidate cached pools | No |

## RPC Method Specifications

### auth/list

Lists all authentication profiles.

**Parameters:** None

**Response:** `AuthListResponse`

| Field | Type | Description |
|-------|------|-------------|
| `activeProfile` | string? | Name of active profile |
| `activeProfileIndex` | int? | 1-based index of active profile |
| `profiles` | ProfileInfo[] | All profiles |

**ProfileInfo:**

| Field | Type | Description |
|-------|------|-------------|
| `index` | int | 1-based profile index |
| `name` | string? | User-assigned name |
| `identity` | string | Display identity |
| `authMethod` | string | DeviceCode, ClientSecret, etc. |
| `cloud` | string | Public, UsGov, China |
| `environment` | EnvironmentSummary? | Bound environment |
| `isActive` | bool | Whether currently active |
| `createdAt` | DateTimeOffset? | Creation time |
| `lastUsedAt` | DateTimeOffset? | Last usage time |

---

### auth/who

Gets detailed information about the active profile.

**Parameters:** None

**Response:** `AuthWhoResponse`

| Field | Type | Description |
|-------|------|-------------|
| `index` | int | Profile index |
| `name` | string? | Profile name |
| `authMethod` | string | Authentication method |
| `cloud` | string | Cloud environment |
| `tenantId` | string? | Azure AD tenant ID |
| `username` | string? | User principal name |
| `objectId` | string? | Azure AD object ID |
| `applicationId` | string? | Service principal app ID |
| `tokenExpiresOn` | DateTimeOffset? | Token expiration |
| `tokenStatus` | string? | "valid" or "expired" |
| `environment` | EnvironmentDetails? | Bound environment details |
| `createdAt` | DateTimeOffset? | Profile creation time |
| `lastUsedAt` | DateTimeOffset? | Last usage time |

**Errors:**

| Code | Condition |
|------|-----------|
| `Auth.NoActiveProfile` | No profile configured |

---

### auth/select

Selects a profile as active.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `index` | int? | One of | Profile index (1-based) |
| `name` | string? | One of | Profile name |

**Response:** `AuthSelectResponse`

| Field | Type | Description |
|-------|------|-------------|
| `index` | int | Selected profile index |
| `name` | string? | Profile name |
| `identity` | string | Display identity |
| `environment` | string? | Bound environment name |

**Errors:**

| Code | Condition |
|------|-----------|
| `Validation.RequiredField` | Neither index nor name provided |
| `Validation.InvalidArguments` | Both index and name provided |
| `Auth.ProfileNotFound` | Profile not found |

---

### env/list

Lists available Dataverse environments.

**Parameters:**

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `filter` | string? | No | null | Substring filter (name, URL, ID) |

**Response:** `EnvListResponse`

| Field | Type | Description |
|-------|------|-------------|
| `filter` | string? | Applied filter (echoed) |
| `environments` | EnvironmentInfo[] | Discovered environments |

**EnvironmentInfo:**

| Field | Type | Description |
|-------|------|-------------|
| `id` | Guid | Organization ID |
| `environmentId` | string? | Power Platform environment ID |
| `friendlyName` | string | Display name |
| `uniqueName` | string | Technical name |
| `apiUrl` | string | API URL (use for selection) |
| `url` | string? | Web application URL |
| `type` | string? | Production, Sandbox, Developer, Trial |
| `state` | string | Enabled or Disabled |
| `region` | string? | Geographic region |
| `version` | string? | Dataverse version |
| `isActive` | bool | Whether currently selected |

**Errors:**

| Code | Condition |
|------|-----------|
| `Auth.NoActiveProfile` | No profile configured |

---

### env/select

Selects an environment for the active profile.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `environment` | string | Yes | URL, display name, or unique name |

**Response:** `EnvSelectResponse`

| Field | Type | Description |
|-------|------|-------------|
| `url` | string | API URL |
| `displayName` | string | Display name |
| `uniqueName` | string? | Technical name |
| `environmentId` | string? | Power Platform ID |
| `resolutionMethod` | string | Direct, Discovery, or Api |

**Errors:**

| Code | Condition |
|------|-----------|
| `Validation.RequiredField` | Empty environment parameter |
| `Auth.NoActiveProfile` | No profile configured |
| `Connection.EnvironmentNotFound` | Environment not found |

---

### query/sql

Executes SQL queries transpiled to FetchXML.

**Parameters:**

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `sql` | string | Yes | - | SQL SELECT statement |
| `top` | int? | No | null | Maximum rows |
| `page` | int? | No | null | Page number |
| `pagingCookie` | string? | No | null | Paging cookie |
| `count` | bool | No | false | Include total count |
| `showFetchXml` | bool | No | false | Return FetchXML only |

**Response:** `QueryResultResponse`

| Field | Type | Description |
|-------|------|-------------|
| `success` | bool | Whether query succeeded |
| `entityName` | string? | Primary entity |
| `columns` | QueryColumnInfo[] | Column metadata |
| `records` | Dict<string, object?>[] | Result records |
| `count` | int | Records returned |
| `totalCount` | int? | Total records (if count=true) |
| `moreRecords` | bool | Pagination indicator |
| `pagingCookie` | string? | Cookie for next page |
| `pageNumber` | int | Current page number |
| `isAggregate` | bool | Whether aggregate query |
| `executedFetchXml` | string? | Generated FetchXML |
| `executionTimeMs` | long | Query duration |

**Errors:**

| Code | Condition |
|------|-----------|
| `Validation.RequiredField` | Empty sql parameter |
| `Query.ParseError` | SQL syntax error |
| `Auth.NoActiveProfile` | No profile configured |
| `Connection.EnvironmentNotFound` | No environment selected |

---

### query/fetch

Executes FetchXML queries directly.

**Parameters:**

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `fetchXml` | string | Yes | - | FetchXML query |
| `top` | int? | No | null | Maximum rows (injected if not in XML) |
| `page` | int? | No | null | Page number |
| `pagingCookie` | string? | No | null | Paging cookie |
| `count` | bool | No | false | Include total count |

**Response:** `QueryResultResponse` (same as query/sql)

**Errors:**

| Code | Condition |
|------|-----------|
| `Validation.RequiredField` | Empty fetchXml parameter |
| `Auth.NoActiveProfile` | No profile configured |
| `Connection.EnvironmentNotFound` | No environment selected |

---

### plugins/list

Lists registered plugins in the environment.

**Parameters:**

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `assembly` | string? | No | null | Filter by assembly name |
| `package` | string? | No | null | Filter by package name |

**Response:** `PluginsListResponse`

| Field | Type | Description |
|-------|------|-------------|
| `assemblies` | PluginAssemblyInfo[] | Standalone assemblies |
| `packages` | PluginPackageInfo[] | Plugin packages |

**PluginAssemblyInfo:**

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Assembly name |
| `version` | string? | Version string |
| `publicKeyToken` | string? | Strong name token |
| `types` | PluginTypeInfoDto[] | Plugin types |

**PluginTypeInfoDto:**

| Field | Type | Description |
|-------|------|-------------|
| `typeName` | string | Full type name |
| `steps` | PluginStepInfo[] | Registered steps |

**PluginStepInfo:**

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Step name |
| `message` | string | Message (Create, Update, etc.) |
| `entity` | string | Primary entity |
| `stage` | string | PreValidation, PreOperation, PostOperation |
| `mode` | string | Synchronous or Asynchronous |
| `executionOrder` | int | Order of execution |
| `filteringAttributes` | string? | Comma-separated attributes |
| `isEnabled` | bool | Whether step is active |
| `description` | string? | Step description |
| `deployment` | string | ServerOnly or Both |
| `runAsUser` | string? | Impersonating user name |
| `asyncAutoDelete` | bool | Auto-delete async jobs |
| `images` | PluginImageInfo[] | Pre/Post images |

**Errors:**

| Code | Condition |
|------|-----------|
| `Auth.NoActiveProfile` | No profile configured |
| `Connection.EnvironmentNotFound` | No environment selected |

---

### solutions/list

Lists solutions in the environment.

**Parameters:**

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| `filter` | string? | No | null | Filter by name |
| `includeManaged` | bool | No | false | Include managed solutions |

**Response:** `SolutionsListResponse`

| Field | Type | Description |
|-------|------|-------------|
| `solutions` | SolutionInfoDto[] | Matching solutions |

**SolutionInfoDto:**

| Field | Type | Description |
|-------|------|-------------|
| `id` | Guid | Solution ID |
| `uniqueName` | string | Unique name |
| `friendlyName` | string | Display name |
| `version` | string? | Version string |
| `isManaged` | bool | Whether managed |
| `publisherName` | string? | Publisher name |
| `description` | string? | Description |
| `createdOn` | DateTime? | Creation date |
| `modifiedOn` | DateTime? | Last modified date |
| `installedOn` | DateTime? | Installation date |

**Errors:**

| Code | Condition |
|------|-----------|
| `Auth.NoActiveProfile` | No profile configured |
| `Connection.EnvironmentNotFound` | No environment selected |

---

### solutions/components

Gets components for a solution.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `uniqueName` | string | Yes | Solution unique name |
| `componentType` | int? | No | Filter by type code |

**Response:** `SolutionComponentsResponse`

| Field | Type | Description |
|-------|------|-------------|
| `solutionId` | Guid | Solution ID |
| `uniqueName` | string | Solution unique name |
| `components` | SolutionComponentInfoDto[] | Components |

**SolutionComponentInfoDto:**

| Field | Type | Description |
|-------|------|-------------|
| `id` | Guid | Component ID |
| `objectId` | Guid | Referenced object ID |
| `componentType` | int | Type code |
| `componentTypeName` | string | Type name |
| `rootComponentBehavior` | int | Behavior setting |
| `isMetadata` | bool | Whether metadata component |

**Component Type Codes:**

| Code | Type |
|------|------|
| 1 | Entity |
| 2 | Attribute |
| 9 | OptionSet |
| 26 | View |
| 29 | Workflow |
| 61 | WebResource |
| 69 | PluginAssembly |
| 92 | PluginStep |

**Errors:**

| Code | Condition |
|------|-----------|
| `Validation.RequiredField` | Empty uniqueName |
| `Solution.NotFound` | Solution not found |
| `Auth.NoActiveProfile` | No profile configured |
| `Connection.EnvironmentNotFound` | No environment selected |

---

### schema/list

Gets entity schema (attributes/fields).

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `entity` | string | Yes | Entity logical name |

**Response:** `SchemaListResponse`

| Field | Type | Description |
|-------|------|-------------|
| `entity` | string | Entity name |
| `attributes` | AttributeInfo[] | Entity attributes |

**Status:** Placeholder - returns `Operation.NotSupported` error

---

### profiles/invalidate

Invalidates cached connection pools for a profile.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `profileName` | string | Yes | Profile name to invalidate |

**Response:** `ProfilesInvalidateResponse`

| Field | Type | Description |
|-------|------|-------------|
| `profileName` | string | Invalidated profile |
| `invalidated` | bool | Whether successful |

**Errors:**

| Code | Condition |
|------|-----------|
| `Validation.RequiredField` | Empty profileName |

## Notifications

### auth/deviceCode

Sent by daemon when device code authentication is required.

**Payload:**

| Field | Type | Description |
|-------|------|-------------|
| `userCode` | string | Code to enter on device |
| `verificationUri` | string | URL to visit |
| `expiresOn` | DateTimeOffset | Expiration time |
| `message` | string | User-friendly instruction |

**Extension Handling:** Not yet implemented. Future: Show notification with link.

## Error Codes

### Authentication

| Code | Description |
|------|-------------|
| `Auth.NoActiveProfile` | No profile configured |
| `Auth.ProfileNotFound` | Specified profile not found |

### Validation

| Code | Description |
|------|-------------|
| `Validation.RequiredField` | Required parameter missing |
| `Validation.InvalidArguments` | Invalid parameter combination |

### Connection

| Code | Description |
|------|-------------|
| `Connection.EnvironmentNotFound` | No environment selected or not found |

### Query

| Code | Description |
|------|-------------|
| `Query.ParseError` | SQL syntax error |

### Solution

| Code | Description |
|------|-------------|
| `Solution.NotFound` | Solution not found |

### Operation

| Code | Description |
|------|-------------|
| `Operation.NotSupported` | Feature not yet implemented |

## Extension Command Implementation

### ppds.listProfiles

Currently the only implemented extension command.

**Flow:**

```typescript
const listProfilesCmd = vscode.commands.registerCommand('ppds.listProfiles', async () => {
    const result = await daemonClient.listProfiles();

    if (result.profiles.length === 0) {
        vscode.window.showInformationMessage('No authentication profiles found. Use "ppds auth create" to create one.');
        return;
    }

    const items = result.profiles.map(p => ({
        label: p.name ?? `Profile ${p.index}`,
        description: p.identity,
        detail: p.environment ? `${p.environment.displayName} (${p.authMethod})` : p.authMethod,
        picked: p.isActive
    }));

    const selected = await vscode.window.showQuickPick(items, {
        title: 'Authentication Profiles',
        placeHolder: result.activeProfile ? `Active: ${result.activeProfile}` : 'No active profile'
    });

    if (selected) {
        vscode.window.showInformationMessage(`Selected profile: ${selected.label}`);
    }
});
```

## Future UI Features

The following VS Code UI elements can be built using the available RPC methods:

### Tree Views

| View | RPC Methods | Description |
|------|-------------|-------------|
| Profiles | auth/list, auth/select | Profile switcher |
| Environments | env/list, env/select | Environment picker |
| Solutions | solutions/list, solutions/components | Solution explorer |
| Plugins | plugins/list | Plugin browser |

### Webviews

| Webview | RPC Methods | Description |
|---------|-------------|-------------|
| Query Editor | query/sql, query/fetch | Interactive query tool |
| Schema Browser | schema/list | Entity/field explorer |
| Plugin Debugger | plugins/list | Step configuration viewer |

### Status Bar

| Item | RPC Methods | Description |
|------|-------------|-------------|
| Active Profile | auth/who | Show current identity |
| Environment | auth/who | Show current environment |

## Related

- [VS Code Extension: Architecture](01-architecture.md) - Extension architecture
- [PPDS.Mcp: Tools](../06-mcp/02-tools.md) - Similar features via MCP

## Source Files

| File | Purpose |
|------|---------|
| [extension/src/extension.ts](../../extension/src/extension.ts) | Command registration |
| [extension/src/daemonClient.ts](../../extension/src/daemonClient.ts) | RPC client implementation |
| [src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs](../../src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs) | RPC method implementations |
| [src/PPDS.Cli/Commands/Serve/Handlers/RpcException.cs](../../src/PPDS.Cli/Commands/Serve/Handlers/RpcException.cs) | Error handling |
| [src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs](../../src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs) | Error code definitions |
