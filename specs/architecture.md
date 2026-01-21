# Architecture

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-21
**Code:** [src/](../src/)

---

## Overview

PPDS (Power Platform Developer Suite) is a multi-interface platform providing CLI, TUI, RPC, and MCP server access to Power Platform/Dataverse development operations. All interfaces share common Application Services for business logic, ensuring consistent behavior whether users interact via command line, terminal UI, VS Code extension, or AI tools.

### Goals

- **Unified business logic**: Single code path for all interfaces via Application Services
- **TUI-first development**: Terminal UI serves as reference implementation for UI patterns
- **High-performance Dataverse connectivity**: Connection pooling, bulk operations, resilience
- **Extensible platform**: Clear patterns for adding commands, screens, credential providers, and MCP tools

### Non-Goals

- Web or desktop GUI (future consideration)
- Real-time collaboration features
- Plugin hosting runtime (compile-time analysis only)

---

## Architecture

```
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│   CLI Commands  │  │   TUI Screens   │  │   RPC Daemon    │  │   MCP Server    │
│ (System.Cmd)    │  │ (Terminal.Gui)  │  │ (StreamJsonRpc) │  │ (MCP Protocol)  │
└────────┬────────┘  └────────┬────────┘  └────────┬────────┘  └────────┬────────┘
         │                    │                    │                    │
         └────────────────────┴────────────────────┴────────────────────┘
                                       │
                            ┌──────────▼──────────┐
                            │  Application Services│
                            │  (ISqlQueryService,  │
                            │   IProfileService,   │
                            │   IExportService...) │
                            └──────────┬──────────┘
                                       │
         ┌─────────────────────────────┼─────────────────────────────┐
         │                             │                             │
┌────────▼────────┐         ┌──────────▼──────────┐       ┌──────────▼──────────┐
│   PPDS.Auth     │         │   PPDS.Dataverse    │       │   PPDS.Migration    │
│ (Credentials,   │         │ (Connection Pool,   │       │ (Export, Import,    │
│  Profiles)      │         │  Bulk Operations)   │       │  Dependency Sort)   │
└─────────────────┘         └─────────────────────┘       └─────────────────────┘
```

All user interfaces are thin adapters that delegate to Application Services. Services return domain objects; presentation layers format for their output medium.

### Components

| Component | Responsibility |
|-----------|----------------|
| **PPDS.Cli** | CLI commands, TUI screens, RPC daemon - all consuming Application Services |
| **PPDS.Mcp** | MCP server exposing Dataverse capabilities for AI integration |
| **PPDS.Auth** | Credential providers, profile storage, token caching |
| **PPDS.Dataverse** | Connection pooling, bulk operations, resilience, metadata |
| **PPDS.Migration** | High-performance data export/import with dependency ordering |
| **PPDS.Plugins** | Plugin development SDK with registration attributes |
| **PPDS.Analyzers** | Roslyn analyzers enforcing architectural rules |

### Dependencies

```
PPDS.Plugins (net462)           PPDS.Analyzers (netstandard2.0)
       │                                │
       │                     [Build-time analyzer - no runtime ref]
       │                                │
       ▼                                ▼
PPDS.Dataverse (net8.0+) ◄──────────────┬──────────────────────┐
       ▲                                │                      │
       │                                │                      │
PPDS.Migration ◄────────────┐           │                      │
       ▲                    │           │                      │
       │                    │           │                      │
PPDS.Auth (net8.0+)         │           │                      │
       ▲                    │           │                      │
       │                    │           │                      │
       └────────────────────┴───────────┴──────────────────────┘
                            │
                 ┌──────────┴──────────┐
                 │                     │
            PPDS.Cli (Exe)       PPDS.Mcp (Exe)
```

---

## Specification

### Module Structure

| Project | Framework | Type | Purpose |
|---------|-----------|------|---------|
| PPDS.Analyzers | netstandard2.0 | Library | Roslyn analyzers for architectural enforcement |
| PPDS.Auth | net8.0, 9.0, 10.0 | Library | Credential providers, profile storage, GDS integration |
| PPDS.Dataverse | net8.0, 9.0, 10.0 | Library | Connection pooling, bulk operations, resilience |
| PPDS.Migration | net8.0, 9.0, 10.0 | Library | Data export/import engine |
| PPDS.Plugins | net462 | Library | Plugin SDK (Dataverse sandbox compatibility) |
| PPDS.Cli | net8.0, 9.0, 10.0 | Exe | Main executable (`ppds`) with CLI + TUI + RPC |
| PPDS.Mcp | net8.0, 9.0, 10.0 | Exe | MCP server (`ppds-mcp-server`) for AI integration |

### Layering

**Layer 1 - Presentation (UI Adapters):**
- CLI commands parse arguments and call services
- TUI screens render UI and call services
- RPC handlers deserialize requests and call services
- MCP tools expose AI-friendly APIs via services

**Layer 2 - Application Services:**
- Encapsulate business logic in testable units
- Return domain objects (not formatted output)
- Accept `IProgressReporter` for long operations
- Throw `PpdsException` with `ErrorCode` for all errors

**Layer 3 - Domain Libraries:**
- PPDS.Auth handles all authentication flows
- PPDS.Dataverse provides connection pooling and data access
- PPDS.Migration orchestrates export/import operations

### Primary Flows

**CLI Command Execution:**

1. **Parse**: System.CommandLine parses arguments
2. **Service**: Command calls Application Service
3. **Execute**: Service performs business logic
4. **Format**: Command formats result via `IOutputWriter`
5. **Return**: Exit code based on success/failure

**TUI Interaction:**

1. **Display**: Screen renders via Terminal.Gui
2. **Input**: User interaction triggers action
3. **Service**: Action calls Application Service
4. **Update**: Screen refreshes with service result

**RPC/MCP Request:**

1. **Receive**: JSON-RPC or MCP protocol message
2. **Deserialize**: Parse request parameters
3. **Service**: Call Application Service
4. **Serialize**: Format response as JSON

---

## Core Types

### Application Services

Application Services ([`ServiceRegistration.cs:32-114`](../src/PPDS.Cli/Services/ServiceRegistration.cs#L32-L114)) encapsulate business logic shared across all interfaces.

```csharp
public interface ISqlQueryService
{
    string TranspileSql(string sql, int? topOverride = null);
    Task<SqlQueryResult> ExecuteAsync(SqlQueryRequest request, CancellationToken ct);
}
```

Services are registered via `AddCliApplicationServices()` and consumed by all presentation layers.

### Service Inventory

| Service | Interface | Scope |
|---------|-----------|-------|
| Profile management | `IProfileService` | Transient |
| Environment discovery | `IEnvironmentService` | Transient |
| SQL query execution | `ISqlQueryService` | Transient |
| Query history | `IQueryHistoryService` | Singleton |
| Data export | `IExportService` | Transient |
| Plugin registration | `IPluginRegistrationService` | Transient |
| Connection management | `IConnectionService` | Transient |
| TUI theming | `ITuiThemeService` | Singleton |

---

## Shared Local State

All interfaces share user data through `~/.ppds/` (or `%LOCALAPPDATA%\PPDS` on Windows).

### Directory Structure

```
~/.ppds/
├── profiles.json           # Auth profiles (name, URL, auth method)
├── msal_token_cache.bin    # MSAL token cache (encrypted)
├── ppds.credentials.dat    # Encrypted credentials (DPAPI/keychain)
├── settings.json           # User preferences
└── history/                # Query history per-environment
    ├── {env-hash-1}.json
    └── {env-hash-2}.json
```

### Path Resolution

The data directory is resolved by [`ProfilePaths.cs:44-66`](../src/PPDS.Auth/Profiles/ProfilePaths.cs#L44-L66):

```csharp
public static string DataDirectory
{
    get
    {
        var envOverride = Environment.GetEnvironmentVariable("PPDS_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride))
            return envOverride;

        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(LocalApplicationData), "PPDS")
            : Path.Combine(Environment.GetFolderPath(UserProfile), ".ppds");
    }
}
```

### Single Code Path

UIs never access files directly. All persistent state flows through Application Services:

```
CLI:     ppds auth list    → IProfileService.GetProfilesAsync()
TUI:     Profile Selector  → IProfileService.GetProfilesAsync()
VS Code: RPC call          → ppds serve → IProfileService.GetProfilesAsync()
```

---

## Error Handling

### Exception Hierarchy

All domain errors use `PpdsException` with hierarchical error codes:

| Exception | Purpose |
|-----------|---------|
| `PpdsException` | Base exception with `ErrorCode`, `UserMessage`, `Severity` |
| `PpdsAuthException` | Authentication failures with `RequiresReauthentication` flag |
| `PpdsThrottleException` | Rate limiting (429) with `RetryAfter` |
| `PpdsValidationException` | Input validation with `ValidationError` list |
| `PpdsNotFoundException` | Resource not found with `ResourceType`, `ResourceId` |

### Error Codes

Error codes follow `Category.Subcategory` format (defined in [`ErrorCodes.cs`](../src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs)):

| Category | Examples |
|----------|----------|
| `Profile.*` | NotFound, NoActiveProfile, NameInUse |
| `Auth.*` | Expired, InvalidCredentials, CertificateError |
| `Connection.*` | Failed, Throttled, Timeout |
| `Validation.*` | RequiredField, InvalidValue, SchemaInvalid |
| `Operation.*` | Cancelled, Timeout, Internal |
| `Query.*` | ParseError, ExecutionFailed |

### Exit Codes

CLI commands return standardized exit codes:

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | PartialSuccess (batch with some failures) |
| 2 | Failure (general) |
| 3 | InvalidArguments |
| 4 | ConnectionError |
| 5 | AuthError |
| 6 | NotFoundError |

---

## Design Decisions

### Why Application Service Layer?

**Context:** CLI, TUI, and RPC handlers duplicated business logic, causing inconsistent behavior and maintenance burden.

**Decision:** Extract Application Services that encapsulate business logic. All interfaces become thin adapters calling the same services.

**Consequences:**
- Positive: Single source of truth, testable in isolation, consistent behavior
- Negative: Additional indirection, more interface/implementation files

### Why TUI-First Development?

**Context:** Multiple interfaces (CLI, TUI, Extension, MCP) could develop UI patterns independently, leading to inconsistency.

**Decision:** TUI is the reference implementation for UI patterns. Extensions port TUI patterns rather than inventing their own.

**Development order:**
1. Application Service (testable business logic)
2. CLI Command (exposes service, defines parameters)
3. TUI Panel (reference UI implementation)
4. RPC Method (if extension needs new data)
5. MCP Tool (if AI analysis adds value)
6. Extension View (ports TUI patterns)

**Consequences:**
- Positive: Consistent UI patterns, clear development order, parallelizable
- Negative: Extension development blocked on TUI patterns

### Why Shared Local State?

**Context:** Each interface could implement its own storage, creating data silos where login from TUI isn't available in CLI.

**Decision:** All user data lives in `~/.ppds/`. UIs never read/write files directly; all access goes through Application Services.

**Consequences:**
- Positive: Login from any interface = available in all interfaces
- Negative: Requires discipline, service abstraction overhead

### Why Unified Authentication Session?

**Context:** MSAL token cache stores tokens keyed by `HomeAccountId`. If not persisted, each session forces re-authentication.

**Decision:** Persist `HomeAccountId` to `profiles.json` after successful authentication. All interfaces share the same token cache.

**Verification:**
1. Delete token cache, run `ppds auth who` (prompts for auth)
2. Check `profiles.json` - `homeAccountId` now populated
3. Run `ppds auth who` again - no prompt (uses cached token)
4. Start TUI, switch profiles - no prompt if token cached

---

## Extension Points

### Adding a CLI Command

1. **Create command class** with static `Create()` method:
   ```csharp
   public static class MyCommand
   {
       public static Command Create()
       {
           var command = new Command("mycommand", "Description");
           command.SetAction(async (pr, ct) => await ExecuteAsync(ct));
           return command;
       }
   }
   ```

2. **Register in command group** (e.g., `DataCommandGroup.cs`)
3. **Register group in `Program.cs`**

### Adding a TUI Screen

1. **Implement `ITuiScreen`** interface:
   ```csharp
   internal sealed class MyScreen : ITuiScreen
   {
       public View Content { get; }
       public string Title => "My Screen";
       public void OnActivated(IHotkeyRegistry registry) { }
   }
   ```

2. **Hook into navigation** via `TuiShell.AddScreen()`

### Adding a Credential Provider

1. **Implement `ICredentialProvider`** interface
2. **Add `AuthMethod` enum value**
3. **Register in `CredentialProviderFactory.CreateAsync()`**

### Adding an MCP Tool

1. **Create class with `[McpServerToolType]`** attribute:
   ```csharp
   [McpServerToolType]
   public sealed class MyTool
   {
       [McpServerTool(Name = "ppds_my_tool")]
       public async Task<Result> ExecuteAsync(string param, CancellationToken ct)
       {
           // Implementation
       }
   }
   ```

2. **Tool is auto-discovered** via `WithToolsFromAssembly()`

---

## Configuration

### Environment Variable

| Variable | Purpose | Default |
|----------|---------|---------|
| `PPDS_CONFIG_DIR` | Override data directory | `~/.ppds` or `%LOCALAPPDATA%\PPDS` |
| `NO_COLOR` | Disable colored output | (unset) |

### Profile Storage

Profiles are stored in `profiles.json` with the following structure:

| Field | Type | Purpose |
|-------|------|---------|
| `name` | string | User-defined profile name |
| `environmentUrl` | string | Dataverse environment URL |
| `authMethod` | enum | DeviceCode, ClientSecret, Certificate, etc. |
| `homeAccountId` | string? | MSAL account ID for token cache lookup |
| `applicationId` | string? | SPN application ID |
| `tenantId` | string? | Azure AD tenant |

---

## Testing

### Test Categories

| Category | Filter | Purpose |
|----------|--------|---------|
| Unit | `--filter Category!=Integration` | Fast, no network |
| Integration | `--filter Category=Integration` | Live Dataverse calls |
| TUI | `--filter Category=TuiUnit` | Terminal.Gui component tests |

### Acceptance Criteria

- [ ] All interfaces use Application Services for business logic
- [ ] Login from any interface works in all interfaces
- [ ] Error codes are consistent across all interfaces
- [ ] New components follow documented extension patterns

---

## Related Specs

- [connection-pool.md](./connection-pool.md) - Connection pooling architecture
- [authentication.md](./authentication.md) - Credential providers and profile storage
- [cli.md](./cli.md) - CLI output architecture and command taxonomy
- [application-services.md](./application-services.md) - Service patterns and IProgressReporter
- [tui.md](./tui.md) - TUI screen architecture and testing
- [mcp.md](./mcp.md) - MCP server and tool patterns
- [error-handling.md](./error-handling.md) - PpdsException hierarchy and error codes

---

## Roadmap

- VS Code extension porting TUI patterns
- Web dashboard for environment monitoring
- Plugin runtime analysis (not just compile-time)
