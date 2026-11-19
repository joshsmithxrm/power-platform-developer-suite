# Copilot instructions

The project rules, build commands and test commands for every coding agent are in
[AGENTS.md](../AGENTS.md). Apply them when writing or reviewing code; the full
architecture principles are in [specs/CONSTITUTION.md](../specs/CONSTITUTION.md).

## Code review

Flag:

- Business logic, file I/O or Dataverse SDK calls in CLI commands, TUI screens, MCP tools or extension panels instead of an Application Service.
- `Console.WriteLine`, Spectre.Console or Terminal.Gui references inside Application Services; services report through `IProgressReporter` and return data.
- Raw exceptions thrown from Application Services, and `PpdsException.UserMessage` text that exposes GUIDs, stack traces or other technical detail.
- A pooled Dataverse client obtained outside a parallel loop and shared by its iterations.
- Single-record Dataverse calls inside loops where `CreateMultiple` or `UpdateMultiple` applies, and record counts done by retrieving records instead of a FetchXML aggregate.
- Missing `CancellationToken` propagation and sync-over-async (`.Result`, `.GetAwaiter().GetResult()`).
- Resource leaks (missing `using` or disposal), thread-safety problems in async code, and logic duplicated across files.

Do not suggest:

- Converting `foreach` loops with `if` conditions into LINQ `.Where()` chains.
- Replacing `if`/`else` with ternary operators.
- "Simplifying" explicit control flow.

These are deliberate style choices in this codebase.
