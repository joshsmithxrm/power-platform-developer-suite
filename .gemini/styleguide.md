# PPDS Coding Standards

This style guide informs Gemini Code Assist about PPDS SDK architecture and patterns.

## Architecture (ADRs 0015, 0024, 0025, 0026)

### Layer Rules

- **CLI/TUI are THIN presentation adapters** - no business logic
- **Application Services own all business logic** - located in `PPDS.Cli/Services/`
- Services accept `IProgressReporter`, not `Console.WriteLine`
- UIs never read/write files directly - use Application Services
- Services return domain objects, presentation layers format output

### Shared Local State (ADR-0024)

All user data lives in `~/.ppds/`:
- `profiles.json` - Auth profiles
- `history/` - Query history
- `settings.json` - User preferences

UIs call services for all persistent state - no direct file I/O.

## Dataverse Patterns

### Bulk Operations

- Use `CreateMultiple`, `UpdateMultiple`, `DeleteMultiple` - not loops with single operations
- Single-record operations in loops cause 5-10x slowdown
- Reference: `BulkOperationExecutor.cs`

### Counting Records

- Use FetchXML aggregate queries for counting
- WRONG: Retrieve records and count in memory
- CORRECT: `<fetch aggregate='true'><attribute aggregate='count'/></fetch>`

### CancellationToken

- All async methods must accept and propagate `CancellationToken`
- Enables graceful shutdown on Ctrl+C
- Check `cancellationToken.ThrowIfCancellationRequested()` in long loops

### Connection Pool

- Get client INSIDE parallel loops, not outside
- Use `pool.GetTotalRecommendedParallelism()` as DOP ceiling
- Don't hold single client across parallel iterations

## Concurrency

### Sync-over-Async

- NEVER use `.GetAwaiter().GetResult()` - causes deadlocks
- NEVER use `.Result` or `.Wait()` on tasks
- If sync context required, use `async void` event handlers

### Fire-and-Forget

- NEVER call async methods without await in constructors
- Use `Loaded` events for async initialization
- Race conditions occur when async completes before UI ready

### UI Thread Safety

- UI property updates must be on main thread
- Use `Application.MainLoop.Invoke(() => ...)` in Terminal.Gui
- Async methods resume on thread pool after await

### Re-entrancy

- Add guards for async operations triggered by UI events
- User can scroll/click faster than operations complete
- Pattern: `if (_isLoading) return; _isLoading = true; try { ... } finally { _isLoading = false; }`

## Error Handling (ADR-0026)

### Structured Exceptions

- Services throw `PpdsException` with `ErrorCode` and `UserMessage`
- `ErrorCode` enables programmatic handling (retry on throttle, re-auth on expired)
- `UserMessage` is safe to display - no GUIDs, stack traces, technical details

### User Messages

- WRONG: "FaultException: ErrorCode -2147204784"
- CORRECT: "Your session has expired. Please log in again."

## Style Preferences

These are intentional choices - do NOT flag:

- `foreach` with `if` instead of LINQ `.Where()`
- Explicit `if/else` instead of ternary operators
- `catch (Exception ex)` at CLI command entry points

## Focus Areas

Please DO flag:

- Code duplication across files
- Missing `CancellationToken` propagation
- Sync-over-async patterns
- Resource leaks (missing disposal)
- Thread safety issues
- API limits (e.g., TopCount > 5000)
- Inefficient patterns (single-record loops, counting by retrieval)
