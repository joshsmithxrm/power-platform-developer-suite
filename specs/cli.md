# CLI

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-21
**Code:** [src/PPDS.Cli/Commands/](../src/PPDS.Cli/Commands/)

---

## Overview

The PPDS CLI provides a Unix-style command-line interface for Power Platform development operations. Built on System.CommandLine, it follows .NET CLI conventions with entity-aligned command taxonomy, structured output modes, and consistent patterns for safety operations like `--dry-run`.

### Goals

- **Scriptable automation**: JSON output mode enables piping to `jq`, `grep`, and CI/CD pipelines
- **Production safety**: `--dry-run` for destructive operations, structured exit codes for programmatic handling
- **Entity-aligned commands**: Command names map to Dataverse entities for discoverability
- **Daemon compatibility**: Clean stdout/stderr separation enables VS Code extension JSON-RPC integration

### Non-Goals

- PowerShell-style verb-noun cmdlets (see PPDS.Tools module)
- Interactive prompts during command execution (TUI handles interactive workflows)
- Streaming output during long operations (progress goes to stderr)

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                              System.CommandLine                               │
│                            (Parsing & Validation)                             │
└─────────────────────────────────────┬────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                             Command Groups                                    │
│   auth │ data │ solutions │ plugins │ plugintraces │ metadata │ flows │ ...  │
└─────────────────────────────────────┬────────────────────────────────────────┘
                                      │
         ┌────────────────────────────┼────────────────────────────────────────┐
         │                            │                                        │
         ▼                            ▼                                        ▼
┌─────────────────┐         ┌─────────────────┐                    ┌───────────────────┐
│   ILogger<T>    │         │  IOutputWriter  │                    │ IProgressReporter │
│  (stderr logs)  │         │ (stdout data)   │                    │ (stderr progress) │
└─────────────────┘         └─────────────────┘                    └───────────────────┘
```

Each command extracts parsed arguments, resolves services via DI, calls Application Services, and formats results through the appropriate output system.

### Components

| Component | Responsibility |
|-----------|----------------|
| **Command Groups** | Static factory classes grouping related commands (e.g., `DataCommandGroup`) |
| **Commands** | Individual operations with options, validation, and execution logic |
| **GlobalOptions** | Cross-cutting options: `--verbose`, `--quiet`, `--debug`, `--output-format` |
| **IOutputWriter** | Formats command results for text or JSON output |
| **IProgressReporter** | Reports operation progress for long-running commands |

### Dependencies

- Depends on: [architecture.md](./architecture.md) - Application Services pattern
- Depends on: [authentication.md](./authentication.md) - Profile resolution
- Depends on: [connection-pool.md](./connection-pool.md) - Multi-connection pooling

---

## Specification

### Core Requirements

1. All command results write to **stdout** (data), all diagnostics write to **stderr** (logs, progress)
2. Exit codes are deterministic and documented for scripting
3. `--dry-run` is available for all destructive operations
4. JSON output mode produces valid, parseable JSON with schema version

### Command Groups

| Group | Subcommands | Domain |
|-------|-------------|--------|
| `auth` | create, list, get, update, delete, select, who, token | Profile management |
| `data` | export, import, copy, analyze, load, update, delete, truncate | Data operations |
| `solutions` | list, export, import, publish, clone, deploy | Solution lifecycle |
| `plugins` | extract, deploy, list, diff, clean, download | Plugin registration |
| `plugintraces` | list, get | Plugin trace logs |
| `metadata` | entity, attribute, relationships | Schema queries |
| `query` | (default), history | FetchXML/SQL queries |
| `env` | list, select, who | Environment selection |
| `flows` | list, get, enable, disable | Cloud flow management |
| `serve` | (default) | RPC daemon mode |

### Output Modes

Controlled by `--output-format` (`-f`):

| Mode | Stream | Format | Use Case |
|------|--------|--------|----------|
| `text` | stdout | Human-readable tables | Interactive use |
| `json` | stdout | JSON with version envelope | Scripting, CI/CD |
| `csv` | stdout | Comma-separated values | Spreadsheet export |

### Verbosity Levels

| Flag | Log Level | Behavior |
|------|-----------|----------|
| `--quiet` / `-q` | Warning | Suppress info messages |
| (default) | Information | Standard operational logs |
| `--verbose` / `-v` | Debug | Include debug diagnostics |
| `--debug` | Trace | Full trace with stack traces |

Flags are mutually exclusive; validation prevents combinations.

### Exit Codes

| Code | Constant | Meaning |
|------|----------|---------|
| 0 | `Success` | Operation completed successfully |
| 1 | `PartialSuccess` | Some items failed (batch operations) |
| 2 | `Failure` | Operation failed |
| 3 | `InvalidArguments` | Invalid command-line arguments |
| 4 | `ConnectionError` | Failed to connect to Dataverse |
| 5 | `AuthError` | Authentication failed |
| 6 | `NotFoundError` | Resource not found |
| 7 | `MappingRequired` | Import mapping incomplete |
| 8 | `ValidationError` | Input validation failed |
| 9 | `Forbidden` | Action not permitted |
| 10 | `PreconditionFailed` | Operation blocked by current state |

---

## Core Types

### Command Factory Pattern

Commands use static factory methods ([`ExportCommand.cs:14-111`](../src/PPDS.Cli/Commands/Data/ExportCommand.cs#L14-L111)) returning configured `Command` objects:

```csharp
public static Command Create()
{
    var schemaOption = new Option<FileInfo>("--schema", "-s") { Required = true };
    var command = new Command("export", "Export data to ZIP file") { schemaOption };
    command.SetAction(async (pr, ct) => await ExecuteAsync(pr.GetValue(schemaOption), ct));
    return command;
}
```

### IOutputWriter

Abstraction for command result output ([`IOutputWriter.cs`](../src/PPDS.Cli/Infrastructure/Output/IOutputWriter.cs)):

```csharp
public interface IOutputWriter
{
    void WriteSuccess<T>(T data);
    void WriteError(StructuredError error);
    void WritePartialSuccess<T>(T data, IEnumerable<ItemResult> results);
}
```

Two implementations:
- `TextOutputWriter` - Human-readable format with optional colors
- `JsonOutputWriter` - Structured JSON with version envelope

### StructuredError

Hierarchical error representation ([`StructuredError.cs`](../src/PPDS.Cli/Infrastructure/Errors/StructuredError.cs)):

```csharp
public sealed record StructuredError(
    string Code,     // "Auth.ProfileNotFound"
    string Message,  // "Profile 'dev' not found"
    string? Details, // Additional context
    string? Target); // Error target (file, entity)
```

### GlobalOptions

Cross-cutting options ([`GlobalOptions.cs:12-89`](../src/PPDS.Cli/Infrastructure/GlobalOptions.cs#L12-L89)) added to all commands:

```csharp
public static void AddToCommand(Command command)
{
    command.Options.Add(QuietOption);
    command.Options.Add(VerboseOption);
    command.Options.Add(DebugOption);
    command.Options.Add(OutputFormatOption);
}
```

---

## Error Handling

### Error Categories

Error codes ([`ErrorCodes.cs`](../src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs)) follow `Category.Specific` format:

| Category | Examples | Recovery |
|----------|----------|----------|
| `Auth.*` | ProfileNotFound, Expired, InvalidCredentials | Re-authenticate or create profile |
| `Connection.*` | Failed, Throttled, Timeout | Check network, retry with backoff |
| `Validation.*` | RequiredField, InvalidValue, FileNotFound | Fix input and retry |
| `Operation.*` | NotFound, Cancelled, Duplicate | Check resource state |
| `Query.*` | ParseError, ExecutionFailed | Fix query syntax |

### Exception Mapping

All exceptions map to exit codes via `ExceptionMapper` ([`ExceptionMapper.cs`](../src/PPDS.Cli/Infrastructure/Errors/ExceptionMapper.cs)):

```csharp
catch (Exception ex)
{
    var error = ExceptionMapper.Map(ex, context: "export", debug: options.Debug);
    writer.WriteError(error);
    return ExceptionMapper.ToExitCode(ex);
}
```

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| No profile selected | Exit 6 (NotFound) with "No active profile" message |
| Network timeout | Exit 4 (ConnectionError) with retry suggestion |
| Empty result set | Exit 0 (Success) with empty array/table |
| Cancelled (Ctrl+C) | Exit 2 (Failure) with "Operation cancelled" |

---

## Design Decisions

### Why Three Output Systems?

**Context:** CLI used ~326 ad-hoc `Console.WriteLine` calls. Progress messages polluted piped output, breaking `ppds data export -f json | jq`.

**Decision:** Separate concerns into three systems:
- `ILogger<T>` → stderr (operational logs)
- `IOutputWriter` → stdout (command results)
- `IProgressReporter` → stderr (progress updates)

**Test results:**
| Scenario | Before | After |
|----------|--------|-------|
| `ppds data export -f json \| jq` | Parse error (progress mixed in) | Valid JSON parsed |
| VS Code daemon mode | Cannot parse output | Clean JSON-RPC response |

**Alternatives considered:**
- Single output abstraction: Rejected - loses stdout/stderr separation
- Merge progress into logging: Rejected - loses progress-specific semantics (ETA, throughput)

**Consequences:**
- Positive: Piping works, daemon mode viable, verbosity control
- Negative: All commands required migration, two output modes to maintain

### Why Entity-Aligned Command Taxonomy?

**Context:** Naming ambiguity: `ppds traces` could mean application traces, telemetry, etc. `ppds plugins traces` conflates registration and observability domains.

**Decision:** Command names align with Dataverse entity logical names:
- `ppds plugintraces` maps to `plugintracelog` entity
- `ppds plugins` handles `pluginassembly`, `plugintype`, `sdkmessageprocessingstep`
- `ppds importjobs` maps to `importjob` (not generic "jobs")

**Principles:**
1. Entity alignment - names map to Dataverse entities
2. Specificity over brevity - `plugintraces` not `traces`
3. Domain separation - traces ≠ registration

**Alternatives considered:**
- `ppds plugins traces`: Rejected - conflates domains
- `ppds traces`: Rejected - ambiguous

**Consequences:**
- Positive: Unambiguous, discoverable via `--help`, entity-aligned documentation
- Negative: Slightly longer command names

### Why `--dry-run` Convention?

**Context:** Inconsistent preview flags: some commands used `--what-if` (PowerShell), others used `--dry-run` (Unix).

**Decision:** Standardize on `--dry-run` for all CLI commands:
```bash
ppds plugins deploy --config registrations.json --dry-run
ppds data delete --entity account --filter "revenue lt 0" --dry-run
```

**Rationale:**
- PPDS.Cli follows Unix conventions (double-dash, lowercase)
- .NET CLI uses `--dry-run` (`dotnet nuget push --dry-run`)
- Cross-platform CLIs (AWS, kubectl, Docker) use `--dry-run`

**Two interfaces, two conventions:**
- CLI users → `--dry-run` (Unix)
- PowerShell users → `-WhatIf` (PPDS.Tools module)

**Console output when active:**
```
[Dry-Run Mode] No changes will be applied.
  [Dry-Run] Would create step: MyPlugin.PreCreate: Create of account
  [Dry-Run] Would update image: PreImage
```

**Consequences:**
- Positive: Consistent experience, .NET CLI alignment, ecosystem fit
- Negative: Breaking change from `--what-if` (acceptable in beta)

### Why JSON for Config, JSONL for Streaming?

**Context:** Without clear policy, risk format inconsistency (JSON/YAML/XML mix) and dependency bloat.

**Decision:** Standardize file formats by use case:

| Use Case | Format | Extension |
|----------|--------|-----------|
| CLI stdout output | JSON | (piped) |
| User config files | JSON | `.json` |
| Streaming data | JSON Lines | `.jsonl` |
| Summary reports | JSON | `.json` |
| Tabular export | CSV | `.csv` |

**YAML explicitly not supported:**
- Consistency with CLI output format
- No additional dependencies (YamlDotNet)
- JSON Schema provides IDE autocomplete
- Single format to document

**JSON Lines for streaming:**
```
{"recordId":"abc","entity":"account","error":"Duplicate key"}
{"recordId":"def","entity":"contact","error":"Missing reference"}
```
Benefits: append-only writes (crash-safe), line-by-line parsing (memory-efficient), easy to grep/tail

**Consequences:**
- Positive: Consistency, no YAML dependency, JSON Schema ecosystem
- Negative: Users cannot add comments to config files

### Why Binary Naming Contract?

**Context:** VS Code extension downloads CLI binaries. GitHub immutable releases (2024) broke workflow where assets were added after publish.

**Decision:** Binary naming convention is a **contract** between repositories:

| Platform | Asset Name |
|----------|------------|
| Windows x64 | `ppds-win-x64.exe` |
| Windows ARM64 | `ppds-win-arm64.exe` |
| macOS x64 | `ppds-osx-x64` |
| macOS ARM64 | `ppds-osx-arm64` |
| Linux x64 | `ppds-linux-x64` |

Pattern: `ppds-{os}-{arch}[.exe]`

**Release flow:**
1. `/release` creates **draft** release
2. Tag push triggers `release-cli.yml`
3. Workflow builds binaries and uploads to draft
4. Workflow publishes release

**Consequences:**
- Positive: Binaries always attached before visible, predictable extension downloads
- Negative: CLI release differs from library packages, draft visible in UI

---

## Extension Points

### Adding a New Command

1. **Create command file** in appropriate group directory:
   ```csharp
   public static class MyCommand
   {
       public static Command Create()
       {
           var option = new Option<string>("--entity", "-e") { Required = true };
           var command = new Command("mycommand", "Description") { option };
           GlobalOptions.AddToCommand(command);
           command.SetAction(async (pr, ct) => await ExecuteAsync(pr, ct));
           return command;
       }
   }
   ```

2. **Add validation** via `option.Validators.Add()` or `command.Validators.Add()`

3. **Register in command group**:
   ```csharp
   command.Subcommands.Add(MyCommand.Create());
   ```

### Adding a New Command Group

1. **Create group class** with static `Create()` factory
2. **Define shared options** as static readonly fields
3. **Register in `Program.cs`**:
   ```csharp
   rootCommand.Subcommands.Add(MyCommandGroup.Create());
   ```

---

## Configuration

### Global Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--output-format`, `-f` | Enum | `text` | Output format: text, json, csv |
| `--quiet`, `-q` | bool | false | Show only warnings and errors |
| `--verbose`, `-v` | bool | false | Show debug messages |
| `--debug` | bool | false | Show trace-level diagnostics |
| `--correlation-id` | string | auto | Distributed tracing ID |

### JSON Output Schema

```json
{
  "version": "1.0",
  "success": true,
  "data": { },
  "error": {
    "code": "Auth.ProfileNotFound",
    "message": "Profile 'production' not found.",
    "target": "production"
  },
  "timestamp": "2026-01-21T12:00:00Z"
}
```

---

## Testing

### Acceptance Criteria

- [ ] All commands produce valid JSON with `--output-format json`
- [ ] Piping works: `ppds data export -f json | jq '.data'`
- [ ] Exit codes match documented values for all error scenarios
- [ ] `--dry-run` available on all destructive commands
- [ ] `--verbose` and `--quiet` are mutually exclusive

### Test Examples

```csharp
[Fact]
public void ExitCodes_AuthError_Returns5()
{
    var exitCode = ExceptionMapper.ToExitCode(new PpdsAuthException("Test"));
    Assert.Equal(5, exitCode);
}

[Fact]
public void JsonOutput_ContainsVersionField()
{
    var writer = new JsonOutputWriter(new StringWriter());
    writer.WriteSuccess(new { name = "test" });
    var json = JsonDocument.Parse(output.ToString());
    Assert.Equal("1.0", json.RootElement.GetProperty("version").GetString());
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Application Services pattern, multi-interface design
- [application-services.md](./application-services.md) - Service patterns, IProgressReporter contract
- [authentication.md](./authentication.md) - Profile resolution for `--profile` option
- [error-handling.md](./error-handling.md) - PpdsException hierarchy and error codes
- [mcp.md](./mcp.md) - MCP server consuming same Application Services

---

## Roadmap

- REPL mode for interactive query exploration
- Shell completion scripts (bash, zsh, PowerShell)
- Progress bar rendering in text mode
