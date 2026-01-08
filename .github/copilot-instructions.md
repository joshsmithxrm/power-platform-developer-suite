# Copilot Code Review Instructions

This file guides GitHub Copilot's code review behavior for the PPDS repository.

## Architecture (ADRs 0015, 0024, 0025, 0026)

### Layer Rules

- CLI/TUI are THIN presentation adapters - no business logic
- Application Services own all business logic
- Services are UI-agnostic - no `Console.WriteLine`, no Spectre/Terminal.Gui references

### File I/O (ADR-0024: Shared Local State)

- UIs never read/write files directly
- All file access through Application Services
- WRONG: `File.ReadAllText` in command handler
- CORRECT: `await _profileService.GetProfilesAsync()`

### Progress Reporting (ADR-0025: UI-Agnostic Progress)

- Services accept `IProgressReporter`, not write to console
- UIs implement adapters for their display medium
- Services return data, presentation layers format it

### Error Handling (ADR-0026: Structured Error Model)

- Services throw `PpdsException` with `ErrorCode` and `UserMessage`
- Never expose technical details (GUIDs, stack traces) in `UserMessage`
- UIs format errors for their medium

### Dataverse Patterns

- Use bulk APIs (`CreateMultiple`, `UpdateMultiple`) not loops with single operations
- Use FetchXML aggregate for counting, not retrieve-and-count
- Propagate `CancellationToken` through all async methods
- Get pool client INSIDE parallel loops, not outside

## Style Preferences

Do NOT suggest:

- Converting `foreach` loops with `if` conditions to LINQ `.Where()` chains
- Replacing `if/else` with ternary operators
- "Simplifying" explicit control flow

These are intentional style choices per project conventions.

## Valuable Findings

DO flag:

- Code duplication across files
- Resource leaks (missing disposal, using statements)
- Missing `CancellationToken` propagation
- Sync-over-async patterns (`.GetAwaiter().GetResult()`)
- Thread safety issues in async code
