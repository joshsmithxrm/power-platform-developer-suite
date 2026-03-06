# PPDS Constitution

Non-negotiable principles. Every spec, plan, implementation, and review MUST comply. Violations are defects — not style issues, not suggestions, not "nice to haves."

## How to Use This Document

- **Skills:** Read this before any implementation, review, or spec work
- **Subagents:** This is injected into your prompt — comply fully
- **Reviews:** Check every item — violation = finding

---

## Architecture Laws

**A1.** All business logic lives in Application Services (`src/PPDS.Cli/Services/`) — never in UI code. CLI commands, TUI screens, VS Code webviews, and MCP handlers are thin wrappers that call services.

**A2.** Application Services are the single code path — CLI, TUI, RPC, and MCP all call the same service methods. No duplicating logic across interfaces.

**A3.** Accept `IProgressReporter` for any operation expected to take >1 second. All UIs (CLI stderr, TUI status bar, VS Code progress, MCP notifications) need feedback.

## Dataverse Laws

**D1.** Use `IDataverseConnectionPool` for all Dataverse operations — never create `ServiceClient` directly. Creating a client per request is 42,000x slower than pooling.

**D2.** Never hold a pooled client across multiple operations. Pattern: get, use, dispose within a single method scope. Holding defeats pool parallelism.

**D3.** Use bulk APIs (`CreateMultiple`, `UpdateMultiple`) over `ExecuteMultiple`. Bulk APIs are 5x faster.

**D4.** Wrap all exceptions from Application Services in `PpdsException` with an `ErrorCode`. Raw exceptions prevent programmatic error handling by callers.

## Interface Laws

**I1.** CLI stdout is for data only — status messages, progress, and diagnostics go to `Console.Error.WriteLine` (stderr). Stdout must be pipeable.

**I2.** Generated entities in `src/PPDS.Dataverse/Generated/` are never hand-edited. They are regenerated from metadata.

**I3.** Every spec must have numbered acceptance criteria (AC-01, AC-02, ...) before implementation begins. No ACs = no implementation.

## Security Laws

**S1.** Never render untrusted data via `innerHTML` — use `textContent` or a proper escaping pipeline. Mixed escaped/unescaped data in the same structure is an architectural flaw, not a minor issue.

**S2.** Never use `shell: true` in process spawn without explicit justification documented in the spec. Default is `shell: false`.

**S3.** Never log secrets (`clientSecret`, `password`, `certificatePassword`, auth tokens). If RPC params contain secrets, they must be redacted before any tracing or logging.

## Resource Laws

**R1.** Every `IDisposable` gets disposed. No fire-and-forget subscriptions, no leaked event handlers, no orphaned timers. If a class holds disposable resources, it must implement `IDisposable` itself.

**R2.** `CancellationToken` must be threaded through the entire async call chain — never accepted as a parameter and then ignored. If a method accepts a token, it must pass it to every async call it makes.

**R3.** Event handlers and subscriptions must be cleaned up in `Dispose`. Every `+=` needs a corresponding `-=`. Every `.subscribe()` needs an `.unsubscribe()` or disposal mechanism.
