# PPDS.Cli Services: Connection Service

## Overview

The Connection Service provides access to Power Platform connections via the Power Apps Admin API. Unlike Dataverse entities, Power Platform connections are managed through a separate REST API, enabling listing and retrieval of connection metadata including status, connector information, and ownership details. This service is essential for understanding connection references in solutions and diagnosing connectivity issues.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `IConnectionService` | Query Power Platform connections from the Power Apps Admin API |

### Classes

| Class | Purpose |
|-------|---------|
| `ConnectionService` | Implements connection queries via Power Apps Admin API REST calls |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `ConnectionInfo` | Connection metadata including ID, connector, status, and ownership |
| `ConnectionStatus` | Enum indicating connection health (Connected, Error, Unknown) |
| `PowerPlatformToken` | Access token for Power Platform APIs (from PPDS.Auth) |

## Behaviors

### Normal Operation

1. **List Connections**: Queries `/providers/Microsoft.PowerApps/scopes/admin/environments/{environmentId}/connections` endpoint
2. **Get Connection**: Queries `/providers/Microsoft.PowerApps/scopes/admin/environments/{environmentId}/connections/{connectionId}` endpoint
3. **Token Acquisition**: Uses `IPowerPlatformTokenProvider.GetFlowApiTokenAsync()` to obtain Bearer token with `service.powerapps.com` audience
4. **Response Mapping**: Transforms Power Apps API JSON responses to `ConnectionInfo` records

### API Details

| Operation | HTTP | Endpoint | API Version |
|-----------|------|----------|-------------|
| List | GET | `/providers/Microsoft.PowerApps/scopes/admin/environments/{env}/connections` | 2016-11-01 |
| Get | GET | `/providers/Microsoft.PowerApps/scopes/admin/environments/{env}/connections/{id}` | 2016-11-01 |
| Filter | OData | `$filter=properties/apiId eq '{connectorId}'` | - |

### Lifecycle

- **Initialization**: Constructor requires `IPowerPlatformTokenProvider`, `CloudEnvironment`, `environmentId`, and `ILogger`
- **Operation**: Creates new `HttpRequestMessage` per call with Bearer token authorization
- **Cleanup**: `HttpClient` instance is created per-service (not pooled)

### Connection Status Mapping

| API Status Array | `ConnectionStatus` |
|------------------|-------------------|
| Contains "Error" status | `Error` |
| Has statuses, no errors | `Connected` |
| No statuses array | `Unknown` |

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Empty results | Returns empty `List<ConnectionInfo>` | No exception thrown |
| Connection not found | Returns `null` from `GetAsync` | HTTP 404 handled gracefully |
| Forbidden/Unauthorized | Throws `InvalidOperationException` | Special message about SPN limitations |
| HTTP error | Throws `HttpRequestException` | Includes status code and body |
| Null token provider | Throws `ArgumentNullException` | Constructor validation |
| Null environment ID | Throws `ArgumentNullException` | Constructor validation |
| Null logger | Throws `ArgumentNullException` | Constructor validation |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `InvalidOperationException` | HTTP 401/403 from API | Use interactive auth (device code) instead of SPN |
| `HttpRequestException` | Other HTTP errors | Check network, permissions, environment ID |
| `ArgumentNullException` | Null constructor parameters | Provide required dependencies |
| `JsonException` | Malformed API response | Contact Microsoft support or retry |

### SPN Limitation Warning

Service principals have limited access to the Power Apps Admin Connections API. The service detects 401/403 responses and throws an `InvalidOperationException` with guidance to use user-delegated authentication (interactive or device code) for full functionality.

## Dependencies

- **Internal**:
  - `PPDS.Auth.Cloud.CloudEnvironment` - Cloud environment enumeration
  - `PPDS.Auth.Cloud.CloudEndpoints` - Cloud-specific API URLs
  - `PPDS.Auth.Credentials.IPowerPlatformTokenProvider` - Token acquisition for Power Platform APIs
  - `PPDS.Auth.Credentials.PowerPlatformToken` - Token response model
- **External**:
  - `System.Net.Http` - HTTP client for REST calls
  - `System.Text.Json` - JSON deserialization
  - `Microsoft.Extensions.Logging` - Logging abstraction

### Architectural Note

The service lives in `PPDS.Cli` rather than `PPDS.Dataverse` because it requires `IPowerPlatformTokenProvider` from `PPDS.Auth`, and `PPDS.Dataverse` does not reference `PPDS.Auth` (to avoid circular dependencies).

## Configuration

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `tokenProvider` | `IPowerPlatformTokenProvider` | Yes | Token acquisition for Power Platform APIs |
| `cloud` | `CloudEnvironment` | Yes | Target cloud (Public, UsGov, UsGovHigh, UsGovDod, China) |
| `environmentId` | `string` | Yes | Power Platform environment ID (GUID format) |
| `logger` | `ILogger<ConnectionService>` | Yes | Logging interface |

### Cloud-Specific Endpoints

| Cloud | Power Apps API URL |
|-------|-------------------|
| Public | `https://api.powerapps.com` |
| UsGov | `https://gov.api.powerapps.us` |
| UsGovHigh | `https://high.api.powerapps.us` |
| UsGovDod | `https://api.apps.appsplatform.us` |
| China | `https://api.powerapps.cn` |

## Thread Safety

- The service creates a new `HttpClient` instance in the constructor (not thread-safe for disposal)
- Token acquisition via `IPowerPlatformTokenProvider` is thread-safe
- Multiple concurrent calls are safe as each request creates new `HttpRequestMessage`
- Service instances should be scoped to a single operation or short-lived context

## CLI Commands

The service is exposed through the `ppds connections` command group:

| Command | Description |
|---------|-------------|
| `ppds connections list` | List all connections in the environment |
| `ppds connections list --connector <id>` | Filter by connector ID |
| `ppds connections get <id>` | Get a specific connection by ID |

### Output Formats

Both commands support JSON output via `--json` flag for programmatic consumption.

## Related

- [PPDS.Cli Services: Application Services](01-application-services.md) - Architectural context
- [PPDS.Auth: Token Management](../02-auth/03-token-management.md) - Token acquisition patterns

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Cli/Services/IConnectionService.cs` | Interface and DTO definitions |
| `src/PPDS.Cli/Services/ConnectionService.cs` | Implementation with API integration |
| `src/PPDS.Cli/Commands/Connections/ConnectionsCommandGroup.cs` | CLI command group definition |
| `src/PPDS.Cli/Commands/Connections/ListCommand.cs` | List connections command |
| `src/PPDS.Cli/Commands/Connections/GetCommand.cs` | Get connection command |
| `src/PPDS.Auth/Cloud/CloudEndpoints.cs` | Cloud-specific endpoint URLs |
| `src/PPDS.Auth/Cloud/CloudEnvironment.cs` | Cloud environment enumeration |
| `src/PPDS.Auth/Credentials/IPowerPlatformTokenProvider.cs` | Token provider interface |
| `tests/PPDS.Cli.Tests/Services/ConnectionServiceTests.cs` | Unit tests |
| `tests/PPDS.Cli.Tests/Commands/Connections/ConnectionsCommandGroupTests.cs` | Command structure tests |
