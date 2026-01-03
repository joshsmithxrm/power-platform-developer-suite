# ADR-0008: CLI Output Architecture

**Status:** Accepted
**Date:** 2026-01-03
**Authors:** Josh, Claude

## Context

The CLI has historically used ad-hoc `Console.WriteLine` calls (~326 occurrences) for output. This approach has several problems:

1. **Daemon mode incompatibility**: The VS Code extension plans to run the CLI as a daemon communicating via JSON-RPC. Ad-hoc console output cannot be reliably parsed.

2. **Scripting difficulties**: Users piping output (e.g., `ppds data export -f json | jq`) get progress messages mixed with data.

3. **Inconsistent error handling**: Errors lack structured codes, making programmatic handling unreliable.

4. **No separation of concerns**: Operational logs, progress updates, and command results all write to the same stream.

The existing `IProgressReporter` interface handles migration-specific progress but was writing to stdout (polluting pipeable output).

## Decision

Implement a three-system output architecture with clear separation of concerns:

```
+-----------------------------------------------------------------+
|                        CLI Command                               |
+-----------------------------------------------------------------+
|  ILogger<T>          |  IOutputWriter      |  IProgressReporter |
|  (operational logs)  |  (command results)  |  (migration ops)   |
+-----------------------------------------------------------------+
|      stderr          |      stdout         |      stderr        |
+-----------------------------------------------------------------+
```

### 1. ILogger<T> for Operational Logs

Standard Microsoft.Extensions.Logging for operational messages:
- Connecting, authenticating, retrying
- Debug/trace diagnostics
- Always writes to **stderr**

Verbosity controlled by global flags:
- `--quiet` / `-q`: LogLevel.Warning
- (default): LogLevel.Information
- `--verbose` / `-v`: LogLevel.Debug
- `--debug`: LogLevel.Trace

### 2. IOutputWriter for Command Results

New abstraction for command output:
- Success/error results
- Data payloads
- Always writes to **stdout** (except errors to stderr in text mode)

Two implementations:
- `TextOutputWriter`: Human-readable format
- `JsonOutputWriter`: Structured JSON with version field

### 3. IProgressReporter for Migration Progress

Existing interface with **fix**: Changed from stdout to **stderr**

- Domain-specific semantics (current/total, ETA, throughput)
- Different lifecycle (start-progress-complete)
- Already tested and working

### 4. Structured Errors

All exceptions map to `StructuredError` with hierarchical codes:

```csharp
public sealed record StructuredError(
    string Code,        // "Auth.ProfileNotFound"
    string Message,     // "Profile 'dev' not found"
    string? Details,    // Optional context
    string? Target);    // Optional target (file, entity)
```

Error code categories:
- `Auth.*`: ProfileNotFound, Expired, InvalidCredentials, etc.
- `Connection.*`: Failed, Throttled, Timeout, etc.
- `Validation.*`: RequiredField, InvalidValue, FileNotFound, etc.
- `Operation.*`: NotFound, Cancelled, Internal, etc.

### 5. Exit Codes

Expanded exit codes for programmatic handling:

| Code | Name | Description |
|------|------|-------------|
| 0 | Success | Operation completed successfully |
| 1 | PartialSuccess | Some items failed but operation completed |
| 2 | Failure | Operation failed |
| 3 | InvalidArguments | Invalid command-line arguments |
| 4 | ConnectionError | Failed to connect to Dataverse |
| 5 | AuthError | Authentication failed |
| 6 | NotFoundError | Resource not found |

## Rationale

### Why separate logs from results?

- **Unix convention**: stderr for diagnostics, stdout for data
- **Enables piping**: `ppds data export -f json | jq '.records'` works correctly
- **JSON-RPC semantics**: Responses (stdout) vs notifications (stderr) are distinct
- **Verbosity control**: Logs can be silenced without affecting results

### Why not merge IProgressReporter into ILogger?

- Domain-specific semantics (current/total, ETA, throughput)
- Different lifecycle (start-progress-complete vs discrete writes)
- Already tested and working for migration operations
- VS Code extension wants structured progress differently than logs

### Why ILogger<T> instead of custom abstraction?

- .NET standard pattern, familiar to developers
- Rich ecosystem (providers, filtering, scopes)
- Built-in DI support
- Future: can add Application Insights, Seq, etc.

### Why fix IProgressReporter to write to stderr?

- Progress is operational info, not data
- Piping JSON output was broken: `ppds data export -f json | jq` failed
- JSON-RPC: Progress = notifications (stderr), not responses (stdout)
- Unix convention compliance

## Alternatives Rejected

1. **Single output abstraction**: Loses stdout/stderr separation, breaks piping

2. **Merge progress into logging**: Loses progress-specific semantics (percentage, ETA)

3. **Custom logging abstraction**: Reinventing the wheel, no ecosystem

4. **Keep stdout for progress**: Breaks piping, violates Unix conventions

## Consequences

### Positive

- All commands produce machine-parseable output with `--output-format json`
- VS Code extension can reliably parse CLI output
- Piping works correctly: `ppds data export -f json | jq`
- Future daemon mode maps cleanly to JSON-RPC
- Consistent error codes enable programmatic error handling
- Verbosity flags give users control over noise level

### Negative

- All commands must migrate from Console.WriteLine
- Two output modes (text/json) to maintain
- Slight increase in code complexity

### Neutral

- Existing IProgressReporter contract unchanged (just writes to different stream)
- Commands need refactoring but logic unchanged

## Implementation

### New Files Created

```
src/PPDS.Cli/Infrastructure/
├── Errors/
│   ├── StructuredError.cs      # Core error record
│   ├── ErrorCodes.cs           # Hierarchical error codes
│   ├── ExceptionMapper.cs      # Exception → error mapping
│   └── ExitCodes.cs            # Exit code constants
├── Output/
│   ├── IOutputWriter.cs        # Output abstraction
│   ├── CommandResult.cs        # Result wrapper
│   ├── ItemResult.cs           # Batch item result
│   ├── TextOutputWriter.cs     # Human-readable output
│   └── JsonOutputWriter.cs     # JSON output
└── Logging/
    ├── CliLoggerOptions.cs     # Logger configuration
    ├── CliLoggingExtensions.cs # DI extensions
    ├── ConsoleTextLoggerProvider.cs
    ├── ConsoleJsonLoggerProvider.cs
    ├── LogContext.cs           # Correlation ID holder
    └── IRpcLogger.cs           # Daemon mode placeholder
```

### JSON Output Schema

```json
{
  "version": "1.0",
  "success": true,
  "data": { ... },
  "error": {
    "code": "Auth.ProfileNotFound",
    "message": "Profile 'production' not found.",
    "target": "production"
  },
  "timestamp": "2026-01-03T12:00:00Z"
}
```

## References

- Issue #76: Structured Logging for CLI Daemon Mode
- Issue #77: Structured Error Handling for CLI
- [Microsoft.Extensions.Logging](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging)
- [JSON-RPC 2.0 Specification](https://www.jsonrpc.org/specification)
