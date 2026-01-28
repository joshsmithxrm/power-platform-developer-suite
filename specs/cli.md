# CLI

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-23
**Code:** [src/PPDS.Cli/Commands/](../src/PPDS.Cli/Commands/)

---

## Overview

The PPDS CLI provides a command-line interface for Power Platform development operations. Built on System.CommandLine, it offers 18 command groups for managing authentication, querying Dataverse, deploying plugins, and automating DevOps workflows. All commands delegate to Application Services for business logic, ensuring consistency with TUI and RPC interfaces.

### Goals

- **Scriptable Operations**: Every Dataverse operation available as a command with JSON output mode
- **Self-Documenting**: Commands describe themselves via `--help`; no separate manual needed
- **Pipeline-Friendly**: Stdout for data, stderr for operational messages; enables `| jq` workflows

### Non-Goals

- Individual command documentation (self-documenting via `--help`)
- TUI implementation details (covered in [tui.md](./tui.md))
- Application Services internals (covered in [architecture.md](./architecture.md))

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        Program.cs                            │
│                    (Entry point, routing)                    │
└───────────────────────────┬─────────────────────────────────┘
                            │
        ┌───────────────────┼───────────────────┐
        ▼                   ▼                   ▼
┌───────────────┐   ┌───────────────┐   ┌───────────────┐
│ AuthCommand   │   │ DataCommand   │   │ PluginsCommand│  ... 15 more
│ Group         │   │ Group         │   │ Group         │
└───────┬───────┘   └───────┬───────┘   └───────┬───────┘
        │                   │                   │
        ▼                   ▼                   ▼
┌─────────────────────────────────────────────────────────────┐
│                     GlobalOptions                            │
│            (--quiet, --verbose, --debug, -f)                 │
└───────────────────────────┬─────────────────────────────────┘
                            │
        ┌───────────────────┼───────────────────┐
        ▼                   ▼                   ▼
┌───────────────┐   ┌───────────────┐   ┌───────────────┐
│ IOutputWriter │   │ProfileService │   │ExceptionMapper│
│ (text/json)   │   │Factory        │   │ (exit codes)  │
└───────────────┘   └───────────────┘   └───────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `Program.cs` | Entry point, command registration, TUI dispatch |
| Command Groups | Static factories creating related commands |
| `GlobalOptions` | Shared options (verbosity, output format, correlation ID) |
| `IOutputWriter` | Output formatting (text vs JSON) |
| `ProfileServiceFactory` | DI container creation with Dataverse connection |
| `ExceptionMapper` | Maps exceptions to structured errors and exit codes |

### Dependencies

- Depends on: [architecture.md](./architecture.md) for Application Services
- Depends on: [connection-pooling.md](./connection-pooling.md) for Dataverse access
- Depends on: [authentication.md](./authentication.md) for profile management

---

## Specification

### Core Requirements

1. All commands must support `--output-format json` for scripting
2. Stdout contains only data; stderr contains operational messages
3. Commands return structured exit codes for automation
4. Long operations must accept `CancellationToken` for Ctrl+C handling
5. Commands must validate inputs before connecting to Dataverse

### Entry Point

The CLI entry point ([`Program.cs:39-95`](../src/PPDS.Cli/Program.cs#L39-L95)) handles three modes:

1. **TUI Mode**: No arguments → launches Terminal.Gui interface
2. **Help Mode**: `--help`, `-h`, `--version` → skips version header
3. **CLI Mode**: Arguments present → executes command with version header

### Command Registration

Commands are explicitly registered in [`Program.cs:59-84`](../src/PPDS.Cli/Program.cs#L59-L84):

```csharp
rootCommand.Subcommands.Add(AuthCommandGroup.Create());
rootCommand.Subcommands.Add(DataCommandGroup.Create());
rootCommand.Subcommands.Add(PluginsCommandGroup.Create());
// ... 15 more groups
```

Hidden internal commands appear only when `PPDS_INTERNAL=1` (line 81-84).

### Primary Flows

**Command Execution Flow:**

1. **Parse**: System.CommandLine parses arguments and validates
2. **Validate**: Command validators check cross-field constraints
3. **Connect**: `ProfileServiceFactory` creates service provider with connection pool
4. **Execute**: Command calls Application Service method
5. **Format**: `IOutputWriter` formats result based on output format
6. **Exit**: Return appropriate exit code

**Example** ([`SqlCommand.cs:104-218`](../src/PPDS.Cli/Commands/Query/SqlCommand.cs#L104-L218)):

```csharp
await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
    profile, environment, verbose, debug, deviceCodeCallback, cancellationToken);

var sqlQueryService = serviceProvider.GetRequiredService<ISqlQueryService>();
var result = await sqlQueryService.ExecuteAsync(request, cancellationToken);

writer.WriteSuccess(result);
return ExitCodes.Success;
```

### Constraints

- Maximum two levels of command nesting (e.g., `query history list`)
- Commands must not hold console lock during async operations
- All file paths must be validated before Dataverse operations

---

## Core Types

### GlobalOptions

Shared CLI options added via `AddToCommand()` ([`GlobalOptions.cs:19-110`](../src/PPDS.Cli/Infrastructure/GlobalOptions.cs#L19-L110)):

```csharp
public static class GlobalOptions
{
    public static readonly Option<bool> Quiet;      // -q: warnings only
    public static readonly Option<bool> Verbose;    // -v: debug messages
    public static readonly Option<bool> Debug;      // trace output
    public static readonly Option<OutputFormat> OutputFormat;  // -f: text|json|csv
    public static readonly Option<string?> CorrelationId;      // distributed tracing
}
```

Options are mutually exclusive (validated at line 80-91).

### GlobalOptionValues

Parsed option values ([`GlobalOptions.cs:115-146`](../src/PPDS.Cli/Infrastructure/GlobalOptions.cs#L115-L146)):

```csharp
public sealed class GlobalOptionValues
{
    public bool Quiet { get; init; }
    public bool Verbose { get; init; }
    public bool Debug { get; init; }
    public OutputFormat OutputFormat { get; init; }
    public string? CorrelationId { get; init; }
    public bool IsJsonMode => OutputFormat == OutputFormat.Json;
}
```

### OutputFormat

Output modes ([`OutputFormat.cs:6-16`](../src/PPDS.Cli/Commands/OutputFormat.cs#L6-L16)):

| Format | Purpose |
|--------|---------|
| `Text` | Human-readable output (default) |
| `Json` | Machine-readable, pipeable to `jq` |
| `Csv` | Spreadsheet-compatible for query results |

### IOutputWriter

Output abstraction ([`IOutputWriter.cs:20-71`](../src/PPDS.Cli/Infrastructure/Output/IOutputWriter.cs#L20-L71)):

```csharp
public interface IOutputWriter
{
    bool DebugMode { get; }
    bool IsJsonMode { get; }
    void WriteResult<T>(CommandResult<T> result);
    void WriteSuccess<T>(T data);
    void WriteError(StructuredError error);
    void WritePartialSuccess<T>(T data, IEnumerable<ItemResult> results);
    void WriteMessage(string message);
    void WriteWarning(string message);
}
```

Implementations:
- `TextOutputWriter` - Colored console output, respects `NO_COLOR`
- `JsonOutputWriter` - Structured JSON with schema versioning

---

## Command Groups

The CLI organizes 80+ commands into 18 groups:

| Group | Commands | Purpose |
|-------|----------|---------|
| `auth` | create, list, select, delete, update, name, clear, who | Profile management |
| `env` | list, select, show | Environment discovery |
| `data` | export, import, copy, analyze, schema, users, load, update, delete, truncate | Data operations |
| `plugins` | extract, deploy, register, diff, list, get, clean, download, update, unregister | Plugin lifecycle |
| `plugin-traces` | list, get, delete, settings, timeline, related | Trace log access |
| `query` | fetch, sql, history (subgroup) | FetchXML/SQL execution |
| `metadata` | entities, entity, attributes, keys, relationships, optionset, optionsets | Schema inspection |
| `solutions` | list, get, export, import, publish, components, url | Solution management |
| `import-jobs` | list, get, data, url, wait | Import monitoring |
| `env-variables` | list, get, set, export, url | Environment variables |
| `flows` | list, get, url | Power Automate flows |
| `connections` | list, get | Connection management |
| `connection-refs` | list, get, analyze, connections, flows | Connection references |
| `deployment-settings` | generate, validate, sync | ALM settings files |
| `users` | list, show, roles | User management |
| `roles` | list, show, assign, remove | Security roles |
| `serve` | (daemon mode) | RPC server for IDE |
| `docs` | (opens documentation) | Documentation link |

### Command Group Pattern

Groups use static factory pattern ([`AuthCommandGroup.cs:30-44`](../src/PPDS.Cli/Commands/Auth/AuthCommandGroup.cs#L30-L44)):

```csharp
public static class AuthCommandGroup
{
    public static Command Create()
    {
        var command = new Command("auth", "Manage authentication profiles");
        command.Subcommands.Add(CreateCreateCommand());
        command.Subcommands.Add(CreateListCommand());
        // ... more subcommands
        return command;
    }
}
```

### Shared Options Pattern

Groups define reusable options as static fields:

```csharp
public static readonly Option<string?> ProfileOption = new("--profile", "-p");
public static readonly Option<string?> EnvironmentOption = new("--environment", "-env");
```

Commands add these explicitly to maintain flexibility.

---

## Error Handling

### Exit Codes

Standard exit codes ([`ExitCodes.cs:6-40`](../src/PPDS.Cli/Infrastructure/Errors/ExitCodes.cs#L6-L40)):

| Code | Constant | Meaning |
|------|----------|---------|
| 0 | `Success` | Operation completed |
| 1 | `PartialSuccess` | Some records failed |
| 2 | `Failure` | Could not complete |
| 3 | `InvalidArguments` | Bad input |
| 4 | `ConnectionError` | Dataverse unreachable |
| 5 | `AuthError` | Authentication failed |
| 6 | `NotFoundError` | Resource not found |
| 7 | `MappingRequired` | Auto-mapping incomplete |
| 8 | `ValidationError` | Schema mismatch |
| 9 | `Forbidden` | Managed component protection |
| 10 | `PreconditionFailed` | State prevents operation |

### Exception Mapping

`ExceptionMapper` converts exceptions to exit codes:

```csharp
catch (Exception ex)
{
    var error = ExceptionMapper.Map(ex, context: "operation", debug: globalOptions.Debug);
    writer.WriteError(error);
    return ExceptionMapper.ToExitCode(ex);
}
```

### Error Output

Errors flow to stderr with structured format:

**Text mode:**
```
Error: Profile 'dev' not found
  Target: dev
  Code: Profile.NotFound (debug only)
```

**JSON mode:**
```json
{
  "version": "1.0",
  "success": false,
  "error": {
    "code": "Profile.NotFound",
    "message": "Profile 'dev' not found",
    "target": "dev"
  }
}
```

### Recovery Strategies

| Error Category | Recovery |
|----------------|----------|
| `Auth.*` | Re-run `auth create` or `auth select` |
| `Connection.Throttled` | Wait and retry (automatic via pool) |
| `Validation.*` | Fix input and retry |
| `*.NotFound` | Verify resource exists |

---

## Design Decisions

### Why Static Factories Over Inheritance?

**Context:** Command frameworks often use base classes with virtual methods for common behavior.

**Decision:** Commands use static factory methods (`Create()`) that return fully-configured `Command` instances.

**Consequences:**
- Positive: No inheritance hierarchy to maintain; each command is self-contained
- Positive: Easy to understand; entire command defined in one method
- Negative: More boilerplate for adding global options to each command

### Why Explicit Option Registration?

**Context:** System.CommandLine doesn't support true global options that automatically apply to all subcommands.

**Decision:** Each command explicitly calls `GlobalOptions.AddToCommand(command)`.

**Consequences:**
- Positive: Commands can opt out of specific options (e.g., skip `--output-format`)
- Positive: Clear what options each command supports
- Negative: Repetitive; easy to forget adding global options

### Why Stdout/Stderr Separation?

**Context:** Users want to pipe command output to tools like `jq` or redirect to files.

**Decision:** Data goes to stdout; operational messages (progress, warnings) go to stderr.

**Test results:**
| Scenario | Result |
|----------|--------|
| `ppds query sql "SELECT..." \| jq` | Works: only JSON data reaches jq |
| `ppds data export > file.csv` | Works: progress messages don't corrupt file |

**Alternatives considered:**
- Single stream with markers: Rejected - breaks simple piping
- Silent by default: Rejected - loses valuable progress feedback

**Consequences:**
- Positive: Unix pipeline compatibility
- Positive: Redirect data without losing progress
- Negative: Must be careful where code writes output

### Why Version Header on Stderr?

**Context:** Showing version helps diagnostics but shouldn't interfere with output.

**Decision:** Version header (`ppds vX.Y.Z - connected as...`) writes to stderr on every command except help/version.

**Consequences:**
- Positive: Log files include version for debugging
- Positive: Doesn't affect piped output
- Negative: Slightly noisy for quick commands

---

## Extension Points

### Adding a New Command Group

1. **Create group file** in `src/PPDS.Cli/Commands/{GroupName}/{GroupName}CommandGroup.cs`
2. **Implement factory method:**

```csharp
public static class MyCommandGroup
{
    public static Command Create()
    {
        var command = new Command("my-group", "Description");
        command.Subcommands.Add(CreateListCommand());
        return command;
    }

    private static Command CreateListCommand()
    {
        var command = new Command("list", "List items");
        GlobalOptions.AddToCommand(command);
        command.SetAction(async (parseResult, ct) => { /* ... */ });
        return command;
    }
}
```

3. **Register** in `Program.cs`:
```csharp
rootCommand.Subcommands.Add(MyCommandGroup.Create());
```

### Adding a Command to Existing Group

1. **Create command factory method** in the group file
2. **Add to group's `Create()` method:**
```csharp
command.Subcommands.Add(CreateNewCommand());
```

### Adding a Global Option

1. **Add option** to `GlobalOptions.cs`
2. **Add to `AddToCommand()`** method
3. **Add property** to `GlobalOptionValues`
4. **Update `GetValues()`** to read the option

---

## Configuration

### Environment Variables

| Variable | Purpose |
|----------|---------|
| `NO_COLOR` | Disables colored output (standard) |
| `PPDS_INTERNAL` | Shows internal/debug commands when `=1` |

### User Configuration

User preferences stored in `~/.ppds/settings.json`:

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `defaultProfile` | string | null | Profile to use when `--profile` omitted |
| `defaultOutputFormat` | string | "text" | Default for `--output-format` |

---

## Testing

### Acceptance Criteria

- [ ] All commands support `--help` with accurate descriptions
- [ ] `--output-format json` produces valid, parseable JSON
- [ ] Exit codes match documented meanings
- [ ] Ctrl+C cancels long-running operations cleanly
- [ ] Commands work with piped input/output

### Test Categories

| Category | Filter | Purpose |
|----------|--------|---------|
| Unit | `--filter Category!=Integration` | Command parsing, validation |
| Integration | `--filter Category=Integration` | Live Dataverse commands |

### Test Examples

```csharp
[Fact]
public void SqlCommand_Validates_ExactlyOneSource()
{
    var result = new RootCommand()
        .Parse("query sql --file a.sql --stdin");

    Assert.Contains("Only one SQL source allowed", result.Errors[0].Message);
}

[Fact]
public void GlobalOptions_Validates_MutuallyExclusive()
{
    var result = new RootCommand()
        .Parse("query sql --quiet --verbose");

    Assert.Contains("mutually exclusive", result.Errors[0].Message);
}

[Fact]
public async Task ExitCode_IsAuthError_WhenProfileNotFound()
{
    var exitCode = await Program.Main(new[] { "query", "sql", "-p", "nonexistent" });

    Assert.Equal(ExitCodes.AuthError, exitCode);
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Application Services that commands delegate to
- [authentication.md](./authentication.md) - Profile management commands use these
- [connection-pooling.md](./connection-pooling.md) - How commands connect to Dataverse
- [tui.md](./tui.md) - TUI launched when no arguments provided
- [mcp.md](./mcp.md) - MCP server shares Application Services with CLI

---

## Roadmap

- Tab completion support for common shells (bash, zsh, PowerShell)
- Interactive prompts for missing required options
- Command aliasing in user configuration
