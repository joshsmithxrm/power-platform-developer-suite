# Raw Web API Request

**Status:** Draft
**Last Updated:** 2025-07-25
**Code:** [src/PPDS.Cli/Services/WebApi/](../src/PPDS.Cli/Services/WebApi/)
**Surfaces:** CLI

---

## Overview

Sends authenticated HTTP requests to the Dataverse Web API from the command line. This lets developers test endpoints, debug custom APIs, invoke actions, and inspect metadata without leaving the terminal or maintaining separate auth tokens.

### Goals

- **Authenticated HTTP calls**: Execute arbitrary GET/POST/PATCH/DELETE requests against Dataverse Web API with automatic token management
- **Write protection**: Block mutating requests on production environments by default (consistent with DML safety guard pattern)
- **Composable output**: stdout contains response body only (or headers + body with `--include`), enabling piping to `jq` and other tools

### Non-Goals

- Batch ($batch) request support — deferred to follow-up
- Retry/pagination helpers — user is responsible for constructing correct URLs
- MCP/TUI/Extension surfaces — deferred; Application Service enables future surfaces
- Request history or saved requests — out of scope

---

## Architecture

```
┌──────────────────────┐      ┌────────────────────────────┐      ┌──────────────────────┐
│  CLI: ApiRequestCmd  │─────▶│  RawWebApiService          │─────▶│  HttpClient          │
│  (thin adapter)      │      │  (IRawWebApiService)       │      │  + Bearer token      │
└──────────────────────┘      └────────────────────────────┘      └──────────────────────┘
                                       │
                                       ├── IPowerPlatformTokenProvider.GetTokenForResourceAsync(envUrl)
                                       ├── Write protection: ProtectionLevel check
                                       └── Default OData headers (OData-Version, Accept)
```

The CLI command is a thin presentation adapter. All logic — auth, headers, write protection, response formatting — lives in `RawWebApiService`.

### Components

| Component           | Responsibility                                                                              |
| ------------------- | ------------------------------------------------------------------------------------------- |
| `IRawWebApiService` | Interface for sending raw Web API requests                                                  |
| `RawWebApiService`  | Acquires token, applies write guard, sends HTTP request, returns response                   |
| `ApiRequestCommand` | CLI adapter: parses flags, calls service, writes output to stdout/stderr                    |
| `WebApiWriteGuard`  | Determines whether a request is allowed based on HTTP method + environment protection level |

### Dependencies

- Uses auth patterns from: [authentication.md](./authentication.md)
- Reuses `ProtectionLevel` enum from `PPDS.Auth.Profiles`
- Reuses `DmlSafetyGuard.DetectProtectionLevel(envType)` logic (not the class itself — the service has its own guard)

---

## Specification

### Core Requirements

1. The service acquires a Dataverse-scoped token via `IPowerPlatformTokenProvider.GetTokenForResourceAsync(environmentUrl)` and sets the `Authorization: Bearer {token}` header on every request.
2. The service prepends the environment base URL to the user-supplied path (e.g., `--path /api/data/v9.2/accounts` → `https://org.crm.dynamics.com/api/data/v9.2/accounts`).
3. Default OData headers are applied unless explicitly overridden by user-supplied headers:
    - `OData-Version: 4.0`
    - `Accept: application/json`
    - `Content-Type: application/json` (when body is present)
4. User-supplied headers (`--header`) merge with defaults; user wins on conflict.
5. The write guard blocks POST/PATCH/DELETE/PUT on production environments (unless `--confirm` is supplied).
6. GET requests are always allowed regardless of environment type.
7. The response body is written to stdout. HTTP status line and headers are written to stdout only when `--include` is set.
8. Non-2xx responses produce a non-zero exit code and write the error body to stderr.

### Primary Flows

**Happy-path GET:**

1. **Resolve environment**: Use `--environment` URL (or active profile default)
2. **Acquire token**: `GetTokenForResourceAsync(environmentUrl)`
3. **Build request**: Combine base URL + path, merge headers, set auth
4. **Send**: `HttpClient.SendAsync(request, cancellationToken)`
5. **Output**: Write response body to stdout

**Mutating request on production:**

1. **Resolve environment** and detect `ProtectionLevel`
2. **Write guard fires**: Method is POST/PATCH/DELETE/PUT + level is Production
3. **Block**: Throw `PpdsException(ErrorCode = "Api.WriteBlocked")` unless `--confirm` is present
4. If `--confirm`: proceed with Steps 3-5 from happy-path

### Surface-Specific Behavior

#### CLI Surface

```
ppds api request [options]

Options:
  --path <path>            Required. Relative URL path (e.g., /api/data/v9.2/accounts)
  --method, -X <method>    HTTP method (default: GET)
  --body <json>            Request body as inline JSON string
  --body-file <file>       Request body from file (mutually exclusive with --body)
  --header, -H <header>    Additional header (repeatable, format: "Name: Value")
  --include, -i            Include HTTP status line and response headers in output
  --environment, -env <url> Target environment URL (default: active profile)
  --confirm                Bypass write protection on production environments
```

Exit codes:

- 0: 2xx response received
- 1: Non-2xx response (body written to stderr)
- 2: Request blocked by write guard (no HTTP call made)
- 3: Authentication or connectivity failure

### Constraints

- Application Service lives in `PPDS.Dataverse` (or new project `PPDS.WebApi` if warranted by dependency concerns)
- CLI command lives in `PPDS.Cli/Commands/Api/`
- Service accepts `IProgressReporter` for operations that may take >1s (large response streaming)
- Service propagates `CancellationToken` through all async paths
- No direct `Console.Write` in the service layer

### Validation Rules

| Field                    | Rule                       | Error                                                                  |
| ------------------------ | -------------------------- | ---------------------------------------------------------------------- |
| `--path`                 | Must start with `/`        | "Path must start with '/'. Example: /api/data/v9.2/accounts"           |
| `--method`               | Must be valid HTTP method  | "Invalid HTTP method '{value}'. Use GET, POST, PATCH, PUT, DELETE, HEAD, or OPTIONS." |
| `--body` + `--body-file` | Mutually exclusive         | "Cannot specify both --body and --body-file."                          |
| `--body-file`            | File must exist            | "Body file not found: '{path}'"                                        |
| `--header`               | Must contain `:` separator | "Invalid header format '{value}'. Expected 'Name: Value'."             |

---

## Acceptance Criteria

| ID    | Criterion                                                                            | Test                                                      | Status |
| ----- | ------------------------------------------------------------------------------------ | --------------------------------------------------------- | ------ |
| AC-01 | GET request to valid path returns response body on stdout with exit code 0           | `RawWebApiServiceTests.Get_ValidPath_ReturnsBody`         | 🔲     |
| AC-02 | POST request on production environment without --confirm is blocked with exit code 2 | `WebApiWriteGuardTests.Post_Production_NoConfirm_Blocks`  | 🔲     |
| AC-03 | POST request on production with --confirm sends the request                          | `RawWebApiServiceTests.Post_Production_WithConfirm_Sends` | 🔲     |
| AC-04 | POST/PATCH/DELETE on development environment is allowed without --confirm            | `WebApiWriteGuardTests.Mutating_Development_Allowed`      | 🔲     |
| AC-05 | --include outputs status line and headers before body                                | `ApiRequestCommandTests.Include_OutputsHeaders`           | 🔲     |
| AC-06 | Non-2xx response returns `IsSuccess = false` with status code and body preserved       | `RawWebApiServiceTests.Non2xx_ReturnsErrorResponse`       | 🔲     |
| AC-07 | User-supplied headers override defaults                                              | `RawWebApiServiceTests.UserHeaders_OverrideDefaults`      | 🔲     |
| AC-08 | --body-file reads body from file                                                     | `RawWebApiServiceTests.BodyFile_ReadsFromDisk`            | 🔲     |
| AC-09 | --body and --body-file together produces validation error                            | `ApiRequestCommandTests.Body_And_BodyFile_Conflict`       | 🔲     |
| AC-10 | Path without leading `/` produces validation error                                   | `ApiRequestCommandTests.Path_NoLeadingSlash_Error`        | 🔲     |
| AC-11 | Auth token is acquired for the correct environment URL resource                      | `RawWebApiServiceTests.Token_AcquiredForEnvironmentUrl`   | 🔲     |
| AC-12 | Default OData headers (OData-Version, Accept) are present on requests                | `RawWebApiServiceTests.DefaultHeaders_Applied`            | 🔲     |

### Edge Cases

| Scenario                            | Input                            | Expected Output                                     |
| ----------------------------------- | -------------------------------- | --------------------------------------------------- |
| Path with query string              | `/api/data/v9.2/accounts?$top=1` | Appended as-is to base URL                          |
| Empty response body (204)           | DELETE returning 204             | Exit 0, no stdout output                            |
| Large response body                 | Multi-MB JSON                    | Service returns string body; response streaming deferred to roadmap |
| Environment URL with trailing slash | `https://org.crm.dynamics.com/`  | Trailing slash stripped before combining with path  |

### Test Examples

```csharp
[Fact]
public async Task Get_ValidPath_ReturnsBody()
{
    // Arrange
    var mockHandler = new MockHttpMessageHandler();
    mockHandler.When("/api/data/v9.2/accounts")
        .Respond("application/json", "{\"value\":[]}");

    var service = CreateService(mockHandler, ProtectionLevel.Development);

    // Act
    var result = await service.SendAsync(new RawWebApiRequest
    {
        Path = "/api/data/v9.2/accounts",
        Method = HttpMethod.Get
    });

    // Assert
    Assert.Equal(200, result.StatusCode);
    Assert.Contains("\"value\":[]", result.Body);
}

[Fact]
public async Task Post_Production_NoConfirm_Throws()
{
    var service = CreateService(new MockHttpMessageHandler(), ProtectionLevel.Production);

    var ex = await Assert.ThrowsAsync<PpdsException>(() => service.SendAsync(new RawWebApiRequest
    {
        Path = "/api/data/v9.2/accounts",
        Method = HttpMethod.Post,
        Body = "{}",
        IsConfirmed = false
    }));

    Assert.Equal("Api.WriteBlocked", ex.ErrorCode);
}
```

---

## Core Types

### IRawWebApiService

Application Service interface. UI-agnostic; no console/display dependencies.

```csharp
public interface IRawWebApiService
{
    Task<RawWebApiResponse> SendAsync(
        RawWebApiRequest request,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);
}
```

### RawWebApiRequest

```csharp
public sealed class RawWebApiRequest
{
    public required string EnvironmentUrl { get; init; }
    public required string Path { get; init; }
    public HttpMethod Method { get; init; } = HttpMethod.Get;
    public string? Body { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public bool IsConfirmed { get; init; }
    public ProtectionLevel ProtectionLevel { get; init; } = ProtectionLevel.Production;
}
```

### RawWebApiResponse

```csharp
public sealed class RawWebApiResponse
{
    public int StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public string Body { get; init; } = string.Empty;
    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
}
```

### WebApiWriteGuard

```csharp
public static class WebApiWriteGuard
{
    public static bool IsMutating(HttpMethod method)
        => method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;

    public static bool IsBlocked(HttpMethod method, ProtectionLevel level, bool isConfirmed)
        => IsMutating(method) && level == ProtectionLevel.Production && !isConfirmed;
}
```

---

## Error Handling

### Error Types

| Error               | Condition                                   | Recovery                                                     |
| ------------------- | ------------------------------------------- | ------------------------------------------------------------ |
| `Api.WriteBlocked`  | Mutating method + Production + no --confirm | Add `--confirm` flag or switch to non-production environment |
| `Api.AuthFailed`    | Token acquisition fails                     | Check profile credentials, re-authenticate                   |
| `Api.InvalidPath`   | Path doesn't start with `/`                 | Fix path format                                              |
| `Api.InvalidHeader` | Header missing `:` separator                | Fix header format                                            |
| `Api.BodyConflict`  | Both --body and --body-file specified       | Use only one                                                 |

### Recovery Strategies

- **Auth failure**: Surface the inner exception's message in `UserMessage` (never GUIDs or stack traces)
- **Write blocked**: Include the environment URL and detected protection level in the error message so users understand why

---

## Design Decisions

### Why HttpClient + IPowerPlatformTokenProvider instead of IDataverseConnectionPool?

**Context:** Need to send raw HTTP requests to Dataverse Web API. The existing pool provides `IDataverseClient` which only supports `OrganizationRequest` (SDK-level operations).

**Decision:** Use `IPowerPlatformTokenProvider.GetTokenForResourceAsync(envUrl)` to get a Dataverse-scoped bearer token, then use `HttpClient` directly.

**Alternatives considered:**

- Use `IDataverseConnectionPool` and cast to `ServiceClient`: Tight coupling to concrete SDK type; breaks abstraction; `ServiceClient` internal HTTP methods aren't part of the public API.
- Use `ICredentialProvider.AccessToken`: Returns null for certificate/client-secret auth providers — not universally available.

**Consequences:**

- Positive: Clean, works with all auth methods, testable with mock `HttpMessageHandler`
- Negative: Doesn't share connection pool lifecycle (acceptable — raw HTTP calls are infrequent and don't benefit from pooling)

### Why block at Production only (not Test)?

**Context:** The DML safety guard treats Test environments differently (warn + confirm) vs. Production (block). For raw API, the granularity of "preview affected rows" doesn't apply.

**Decision:** Block mutating requests only on Production environments. Development/Sandbox/Test/Trial are unrestricted. This matches user expectations: if you're in a dev environment, you're expected to be making changes.

**Alternatives considered:**

- Block on Test too: Overly restrictive for an API tool; users testing custom APIs need to POST freely in test environments.
- Never block (always allow): Dangerous — accidental PATCH on production could corrupt data.

---

## Related Specs

- [authentication.md](./authentication.md) - Token acquisition infrastructure
- [query.md](./query.md) - DML safety guard pattern (reused concept, not code)

---

## Changelog

| Date       | Change       |
| ---------- | ------------ |
| 2025-07-25 | Initial spec |

---

## Roadmap

- MCP surface: Expose `IRawWebApiService` as an MCP tool
- Extension surface: "Send Request" from a .http file or command palette
- Batch support: `ppds api batch` for `$batch` requests
- Response streaming for very large payloads
