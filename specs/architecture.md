# Architecture

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-23
**Code:** [src/](../src/)

---

## Overview

PPDS is a TUI-first multi-interface platform for Power Platform development. All business logic resides in Application Services, enabling CLI, TUI, RPC, and MCP interfaces to share identical behavior through a single code path.

### Goals

- **Unified Business Logic**: All interfaces delegate to Application Services; no UI-specific business logic
- **Multi-Interface Consistency**: Login in TUI = available in CLI = available in RPC
- **Testability**: Services are testable in isolation; UI adapters are thin
- **Extensibility**: New interfaces require only adapter implementation

### Non-Goals

- Framework-specific optimizations (Terminal.Gui internals deferred to [tui.md](./tui.md))
- Individual command documentation (self-documenting via `--help`)
- Dataverse protocol details (covered in [connection-pooling.md](./connection-pooling.md))

---

## Module Structure

```
ppds/
├── src/
│   ├── PPDS.Dataverse/       # Core: Connection pooling, bulk APIs, query
│   ├── PPDS.Auth/            # Core: Authentication, credential providers
│   ├── PPDS.Migration/       # Domain: Data export/import engine
│   ├── PPDS.Plugins/         # Core: Plugin attributes (net462)
│   ├── PPDS.Analyzers/       # Dev: Roslyn analyzers (netstandard2.0)
│   ├── PPDS.Cli/             # App: CLI + TUI + RPC daemon
│   └── PPDS.Mcp/             # App: MCP server for AI assistants
└── tests/
    └── PPDS.*.Tests/         # Test projects mirror source structure
```

### Dependency Graph

```
┌─────────────────────────────────────────────────────────┐
│                    Applications                          │
├──────────────────────────┬──────────────────────────────┤
│     PPDS.Cli (Exe)       │    PPDS.Mcp (Exe)            │
│  • CLI (System.CmdLine)  │  • MCP Server                │
│  • TUI (Terminal.Gui)    │  • ModelContextProtocol      │
│  • RPC (StreamJsonRpc)   │                              │
└──────────────────────────┴──────────────────────────────┘
          │ │ │ │                    │ │ │
          └─┼─┼─┼────────────────────┼─┼─┘
            │ │ │                    │ │
     ┌──────┴─┴─┴────────┬───────────┴─┴─────────────┐
     │                   │                           │
     ▼                   ▼                           ▼
┌─────────────┐   ┌─────────────┐   ┌─────────────────────┐
│  PPDS.Auth  │   │PPDS.Dataverse│   │   PPDS.Migration    │
│  • Profiles │   │ • Pool      │   │   • Export/Import   │
│  • Creds    │   │ • Bulk APIs │   │   • Dependencies    │
└─────────────┘   └──────┬──────┘   └──────────┬──────────┘
                         │                      │
                         └──────────────────────┘
                                    │
                    PPDS.Migration depends on PPDS.Dataverse
```

### Project Details

| Project | Frameworks | Type | Purpose |
|---------|------------|------|---------|
| PPDS.Dataverse | net8.0/9.0/10.0 | Library | Connection pooling, bulk APIs, query execution |
| PPDS.Auth | net8.0/9.0/10.0 | Library | Authentication profiles, credential providers |
| PPDS.Migration | net8.0/9.0/10.0 | Library | Data migration with dependency analysis |
| PPDS.Plugins | net462 | Library | Plugin attributes for Dataverse sandbox |
| PPDS.Analyzers | netstandard2.0 | Analyzer | Roslyn analyzers enforcing NEVER rules |
| PPDS.Cli | net8.0/9.0/10.0 | Tool | CLI application (distributed as `ppds`) |
| PPDS.Mcp | net8.0/9.0/10.0 | Tool | MCP server (distributed as `ppds-mcp-server`) |

---

## Layering

### Layer 1: Foundation (No Internal Dependencies)

**PPDS.Dataverse** - Dataverse connectivity primitives
- Connection pooling ([`Pooling/`](../src/PPDS.Dataverse/Pooling/))
- Bulk operations ([`BulkOperations/`](../src/PPDS.Dataverse/BulkOperations/))
- Query execution ([`Query/`](../src/PPDS.Dataverse/Query/))
- Throttle resilience ([`Resilience/`](../src/PPDS.Dataverse/Resilience/))

**PPDS.Auth** - Authentication infrastructure
- Credential providers ([`Credentials/`](../src/PPDS.Auth/Credentials/))
- Profile storage ([`Profiles/`](../src/PPDS.Auth/Profiles/))
- Global discovery ([`Discovery/`](../src/PPDS.Auth/Discovery/))

**PPDS.Plugins** - Plugin development (isolated, net462 only)

### Layer 2: Domain Logic

**PPDS.Migration** - Data migration engine (depends on: PPDS.Dataverse)
- Parallel export
- Dependency-aware import
- CMT format support

### Layer 3: Build-Time

**PPDS.Analyzers** - Roslyn analyzers (netstandard2.0)
- Enforces NEVER rules from CLAUDE.md at compile time

### Layer 4: Applications

**PPDS.Cli** - Primary application (depends on: Auth, Dataverse, Migration, Plugins)
- CLI commands via System.CommandLine
- TUI via Terminal.Gui
- RPC daemon via StreamJsonRpc
- Application Services layer

**PPDS.Mcp** - MCP server (depends on: Auth, Dataverse, Migration)
- AI assistant integration
- Tool registration for Claude, etc.

---

## Application Services

All business logic lives in Application Services ([`src/PPDS.Cli/Services/`](../src/PPDS.Cli/Services/)). UIs are thin adapters that delegate to services.

### Service Inventory

| Service | Interface | Lifetime | Purpose |
|---------|-----------|----------|---------|
| Profile management | `IProfileService` | Transient | Profile CRUD, authentication flows |
| Environment discovery | `IEnvironmentService` | Transient | Environment discovery, selection |
| SQL query execution | `ISqlQueryService` | Transient | Query transpilation, execution |
| Query history | `IQueryHistoryService` | Singleton | Per-environment history persistence |
| Data export | `IExportService` | Transient | CSV/TSV/JSON export with streaming |
| Plugin registration | `IPluginRegistrationService` | Transient | Plugin registration, extraction |
| Connection management | `IConnectionService` | Transient | Power Apps connection API access |
| TUI theming | `ITuiThemeService` | Singleton | Terminal UI theme management |

### Service Pattern

```csharp
public interface IExportService
{
    Task ExportCsvAsync(
        DataTable table,
        Stream stream,
        ExportOptions? options = null,
        IOperationProgress? progress = null,  // UI-agnostic progress
        CancellationToken cancellationToken = default);
}
```

Key principles:
- Return `IReadOnlyList<T>` for collections (immutable, UI-safe)
- Accept optional `IOperationProgress` for operations >1 second
- Throw `PpdsException` with `ErrorCode` for all errors
- Use `CancellationToken` as final parameter

### Registration

Services are registered via extension method ([`Services/ServiceRegistration.cs`](../src/PPDS.Cli/Services/ServiceRegistration.cs)):

```csharp
services.AddCliApplicationServices();  // All application services
services.RegisterDataverseServices();  // Dataverse domain services
services.AddDataverseMigration();      // Migration engine
```

---

## Cross-Cutting Concerns

### Error Handling

All errors flow through structured exceptions ([`Infrastructure/Errors/`](../src/PPDS.Cli/Infrastructure/Errors/)).

**Exception Hierarchy:**

```
PpdsException (base)
├── PpdsAuthException          # RequiresReauthentication flag
├── PpdsThrottleException      # RetryAfter TimeSpan
├── PpdsValidationException    # List of ValidationError
└── PpdsNotFoundException      # ResourceType, ResourceId
```

**PpdsException structure** ([`PpdsException.cs`](../src/PPDS.Cli/Infrastructure/Errors/PpdsException.cs)):

```csharp
public class PpdsException : Exception
{
    public string ErrorCode { get; init; }      // Machine-readable: "Auth.Expired"
    public string UserMessage { get; init; }    // Human-readable, safe to display
    public PpdsSeverity Severity { get; init; } // Error, Warning, Info
    public IDictionary<string, object>? Context { get; init; }  // Debug info
}
```

**Error codes** are hierarchical ([`ErrorCodes.cs`](../src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs)):

| Category | Example Codes |
|----------|---------------|
| Profile.* | `Profile.NotFound`, `Profile.NoActiveProfile` |
| Auth.* | `Auth.Expired`, `Auth.InvalidCredentials` |
| Connection.* | `Connection.Throttled`, `Connection.EnvironmentNotFound` |
| Validation.* | `Validation.RequiredField`, `Validation.InvalidValue` |
| Operation.* | `Operation.NotFound`, `Operation.PartialFailure` |
| Query.* | `Query.ParseError`, `Query.ExecutionFailed` |
| Plugin.* | `Plugin.NotFound`, `Plugin.NoContent`, `Plugin.ImageNotSupported`, `Plugin.HasChildren`, `Plugin.UserNotFound`, `Plugin.SetStateFailed` |

**Exit codes** ([`ExitCodes.cs`](../src/PPDS.Cli/Infrastructure/Errors/ExitCodes.cs)):

| Code | Meaning | Error Categories |
|------|---------|------------------|
| 0 | Success | - |
| 1 | PartialSuccess | `Operation.PartialFailure` |
| 2 | Failure | Generic errors |
| 3 | InvalidArguments | `Validation.*` |
| 4 | ConnectionError | `Connection.*` |
| 5 | AuthError | `Auth.*`, `Profile.*` |
| 6 | NotFoundError | `*.NotFound` |

### Progress Reporting

Two complementary interfaces for UI-agnostic progress:

**IOperationProgress** - General purpose ([`Infrastructure/IOperationProgress.cs`](../src/PPDS.Cli/Infrastructure/IOperationProgress.cs)):

```csharp
public interface IOperationProgress
{
    void ReportStatus(string message);                     // Indeterminate
    void ReportProgress(int current, int total, string?);  // Count-based
    void ReportProgress(double fraction, string?);         // Percentage
    void ReportComplete(string message);
    void ReportError(string message);
}
```

**IProgressReporter** - Migration-specific with metrics ([`Migration/Progress/IProgressReporter.cs`](../src/PPDS.Migration/Progress/IProgressReporter.cs)):

```csharp
public interface IProgressReporter
{
    void ReportProgress(ProgressSnapshot snapshot);  // With rate/ETA
    void ReportPhase(string phase, string? detail);
    void ReportWarning(string message);
    void ReportInfo(string message);
}
```

Each UI provides its own adapter:
- CLI: `ConsoleProgressReporter` - writes to stderr with timestamps
- TUI: `TuiOperationProgress` - updates ProgressBar/Spinner on main thread
- RPC: `JsonProgressReporter` - formats as JSON for VS Code

### Output Handling

All output flows through `IOutputWriter` ([`Infrastructure/Output/`](../src/PPDS.Cli/Infrastructure/Output/)):

```csharp
public interface IOutputWriter
{
    bool DebugMode { get; }
    bool IsJsonMode { get; }
    void WriteResult<T>(CommandResult<T> result);
    void WriteSuccess<T>(T data);
    void WriteError(StructuredError error);
    void WritePartialSuccess<T>(T data, IEnumerable<ItemResult> results);
}
```

Implementations:
- `TextOutputWriter` - Human-readable, respects NO_COLOR
- `JsonOutputWriter` - Structured JSON for piping/scripting

**Key principle:** Stdout for data only; stderr for operational messages.

### Dependency Injection

**ProfileServiceFactory** ([`Infrastructure/ProfileServiceFactory.cs`](../src/PPDS.Cli/Infrastructure/ProfileServiceFactory.cs)) creates fully-configured service providers:

```csharp
await using var provider = await ProfileServiceFactory.CreateFromProfileAsync(
    profileName,
    environmentUrl,
    deviceCodeCallback,
    cancellationToken);

var service = provider.GetRequiredService<IPluginRegistrationService>();
```

The factory:
1. Resolves profile and environment
2. Creates connection pool with throttle tracking
3. Registers all Dataverse and CLI services
4. Manages credential store lifecycle

---

## Design Patterns

### Factory Pattern

**ProfileServiceFactory** ([`ProfileServiceFactory.cs:1-369`](../src/PPDS.Cli/Infrastructure/ProfileServiceFactory.cs)) - Creates service providers with all dependencies wired.

**CredentialProviderFactory** ([`CredentialProviderFactory.cs`](../src/PPDS.Auth/Credentials/CredentialProviderFactory.cs)) - Creates credential providers based on auth method (Interactive, DeviceCode, ClientSecret, etc.).

### Strategy Pattern

**Connection selection** ([`Pooling/Strategies/`](../src/PPDS.Dataverse/Pooling/Strategies/)):

```csharp
public interface IConnectionSelectionStrategy
{
    string SelectConnection(
        IReadOnlyList<DataverseConnection> connections,
        IThrottleTracker throttleTracker,
        IReadOnlyDictionary<string, int> activeConnections);
}
```

Implementations:
- `RoundRobinStrategy` - Simple rotation
- `LeastConnectionsStrategy` - Fewest active clients
- `ThrottleAwareStrategy` - Avoids throttled connections

### Object Pool Pattern

**DataverseConnectionPool** ([`Pooling/DataverseConnectionPool.cs`](../src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs)) - Manages client lifecycle with automatic return on dispose.

### Decorator Pattern

**PooledClient** ([`Pooling/PooledClient.cs`](../src/PPDS.Dataverse/Pooling/PooledClient.cs)) - Wraps `IDataverseClient`:
- Returns to pool on dispose
- Detects throttle events via `ThrottleDetector`
- Resets client state on return

**ProfileConnectionSourceAdapter** ([`ProfileConnectionSourceAdapter.cs`](../src/PPDS.Cli/Infrastructure/ProfileConnectionSourceAdapter.cs)) - Bridges PPDS.Auth and PPDS.Dataverse without circular dependencies.

### Null Object Pattern

- `NullOperationProgress.Instance` - No-op progress for optional parameter
- `NullProgressReporter.Instance` - No-op for migration progress

### Observer Pattern

Progress interfaces (`IOperationProgress`, `IProgressReporter`) allow services to report progress without knowing which UI consumes it.

---

## Extension Points

### Adding a New Application Service

1. **Create interface** in `src/PPDS.Cli/Services/{Domain}/I{Name}Service.cs`
2. **Create implementation** in same directory
3. **Register** in `ServiceRegistration.cs`:

```csharp
services.AddTransient<IMyService, MyService>();
```

### Adding a New Credential Provider

1. **Implement** `ICredentialProvider` in `src/PPDS.Auth/Credentials/`
2. **Add case** to `CredentialProviderFactory.Create()`
3. **Add enum value** to `AuthMethod` if needed

### Adding a New Connection Selection Strategy

1. **Implement** `IConnectionSelectionStrategy` in `src/PPDS.Dataverse/Pooling/Strategies/`
2. **Wire up** via `DataverseConnectionPoolOptions.SelectionStrategy`

---

## Shared Local State

All persistent user data flows through Application Services:

```
~/.ppds/                        # Or %LOCALAPPDATA%\PPDS on Windows
├── profiles.json               # Auth profiles
├── msal_token_cache.bin        # MSAL token cache (encrypted)
├── ppds.credentials.dat        # Encrypted credentials
├── settings.json               # User preferences
└── history/                    # Query history per-environment
    ├── {env-hash-1}.json
    └── {env-hash-2}.json
```

**Key guarantee:** Login from any interface (CLI/TUI/RPC/MCP) = available in all interfaces.

---

## Testing

### Acceptance Criteria

- [ ] All Application Services have unit tests
- [ ] Error codes are tested for correct mapping
- [ ] Progress reporting works with null progress
- [ ] DI container resolves all services

### Test Categories

| Category | Filter | Purpose |
|----------|--------|---------|
| Unit | `--filter Category!=Integration` | Fast, isolated |
| Integration | `--filter Category=Integration` | Live Dataverse |
| TUI | `--filter Category=TuiUnit` | Terminal.Gui isolation |

### Test Examples

```csharp
[Fact]
public async Task ExportService_ReportsProgress_WhenProgressProvided()
{
    var progress = new TestOperationProgress();
    var service = new ExportService(NullLogger<ExportService>.Instance);

    await service.ExportCsvAsync(table, stream, progress: progress);

    Assert.True(progress.CompleteCalled);
    Assert.Contains(progress.Messages, m => m.Contains("Exported"));
}

[Fact]
public void PpdsException_PreservesErrorCode()
{
    var ex = new PpdsException(ErrorCodes.Auth.Expired, "Session expired");

    Assert.Equal("Auth.Expired", ex.ErrorCode);
    Assert.Equal("Session expired", ex.UserMessage);
}
```

---

## Design Decisions

### Why Application Services?

**Context:** CLI, TUI, and RPC handlers duplicated business logic for profile management, environment discovery, query execution, and data export.

**Decision:** Extract all business logic into Application Services. All UIs become thin adapters.

**Consequences:**
- Positive: Single source of truth, testable in isolation, consistent behavior
- Negative: Additional indirection, more files to maintain

### Why Structured Errors with ErrorCodes?

**Context:** Generic exceptions prevented programmatic error handling. Scripts couldn't distinguish "file not found" from "auth failed."

**Decision:** All services throw `PpdsException` with hierarchical `ErrorCode` (e.g., `Auth.Expired`).

**Consequences:**
- Positive: Scripts can catch specific errors, consistent exit codes, VS Code extension can map to help
- Negative: More verbose error creation, codes must stay in sync

### Why Two Progress Interfaces?

**Context:** Different operations need different progress granularity. Simple exports need count-based progress; migrations need phase/rate/ETA.

**Decision:** `IOperationProgress` for general operations, `IProgressReporter` for complex multi-phase operations.

**Consequences:**
- Positive: Right abstraction for each use case, cleaner service signatures
- Negative: Two interfaces to understand, potential confusion

### Why Persistent State Through Services Only?

**Context:** Each UI could implement its own file access, creating data silos where login from TUI wouldn't be available in CLI.

**Decision:** All file I/O flows through Application Services. UIs never read/write files directly.

**Consequences:**
- Positive: Unified session across all UIs, single code path for storage
- Negative: Requires discipline, indirection obscures file operations

---

## Related Specs

- [connection-pooling.md](./connection-pooling.md) - Connection pool implementation details
- [authentication.md](./authentication.md) - Credential providers and profiles
- [cli.md](./cli.md) - Command structure and output formatting
- [tui.md](./tui.md) - Terminal UI framework patterns

---

## Roadmap

- Unified logging configuration across all interfaces
- OpenTelemetry integration for distributed tracing
- Plugin system for custom Application Services
