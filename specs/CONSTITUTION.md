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

**I4.** Never silently hide, truncate, or filter data. Every reduction in displayed records must be (a) visible — show "X of Y" with the reason, and (b) reversible — provide a toggle, filter, or option to show the full dataset. Service methods that apply filters or limits must return total counts alongside results so every consumer (CLI, TUI, Extension, MCP) can communicate what was filtered. Security-correct exclusions (e.g., secrets) still require visibility ("N secrets excluded") but need not be reversible.

**I5.** Extension panel names use the plural noun for collection views (Solutions, Plugin Traces) and a descriptive tool name for interactive tools (Data Explorer, Plugin Registration, Metadata Browser). Test: if "list of {name}" makes sense, it's a collection — use the noun. If the panel has its own interaction model beyond listing, use a descriptive name.

**I6.** Every spec AC must have a corresponding passing test before implementation is complete. The spec AC table `Test` column must reference the test method; the `Status` column must show ✅. Untested ACs are incomplete implementations — not tech debt, not follow-up items.

## Session Laws

**SS1.** Running sessions are independent — changing the active profile or environment in one surface (CLI, TUI, Extension, MCP) does not affect other running surfaces. The persisted active profile is a default for new sessions, not a live binding.

**SS2.** MCP sessions are locked at startup — the profile and environment are captured when the server starts and cannot be changed by external profile switches. Environment switching within a session is controlled by the `--allowed-env` allowlist; if no allowlist is configured, the session is locked to its initial environment.

## Security Laws

**S1.** Never render untrusted data via `innerHTML` — use `textContent` or a proper escaping pipeline. Mixed escaped/unescaped data in the same structure is an architectural flaw, not a minor issue.

**S2.** Never use `shell: true` in process spawn without explicit justification documented in the spec. Default is `shell: false`.

**S3.** Never log secrets (`clientSecret`, `password`, `certificatePassword`, auth tokens). If RPC params contain secrets, they must be redacted before any tracing or logging.

## Resource Laws

**R1.** Every `IDisposable` gets disposed. No fire-and-forget subscriptions, no leaked event handlers, no orphaned timers. If a class holds disposable resources, it must implement `IDisposable` itself.

**R2.** `CancellationToken` must be threaded through the entire async call chain — never accepted as a parameter and then ignored. If a method accepts a token, it must pass it to every async call it makes.

**R3.** Event handlers and subscriptions must be cleaned up in `Dispose`. Every `+=` needs a corresponding `-=`. Every `.subscribe()` needs an `.unsubscribe()` or disposal mechanism.

## Spec Laws

**SL1.** One spec per domain concept, named after the thing — not the project, surface, or enhancement. `plugin-traces.md` not `tui-plugin-traces.md`. `data-explorer.md` not `vscode-data-explorer-monaco-editor.md`. Cross-cutting architectural patterns are legitimate standalone specs.

**SL2.** Specs are living documents — updated as the feature evolves. Plans (`.plans/`) are ephemeral and consumed by implementation. Project coordination documents (parity, polish, audit) are plans, not specs. Specs must never reference plans — a spec must stand on its own. If a plan contains design context the spec needs, absorb that content into the spec; do not link to the plan.

**SL3.** Surface-specific behavior (TUI screen layout, Extension panel wiring, MCP tool schema) lives in surface sections within the domain spec, not in separate spec files.

**SL4.** Spec frontmatter `**Code:**` must contain grep-friendly path prefixes, not prose like "Multiple (see spec)". Every spec with an implementation must have at least one code path. System-wide specs (architecture, governance) use `**Code:** System-wide`.

**SL5.** Specs for removed features are deleted. No archival, no deprecation ceremony, no tombstones.
