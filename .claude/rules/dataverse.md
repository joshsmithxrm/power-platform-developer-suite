---
paths: ["src/PPDS.Dataverse/**", "src/PPDS.Cli/Services/**"]
---

# Dataverse Conventions

## Connection Pool (NEVER violate)

- Use `IDataverseConnectionPool` for all Dataverse access — never create `ServiceClient` per request
- Acquire from pool, use, release — never hold a pooled client across multiple queries (defeats parallelism)
- For multi-request scenarios, acquire/release per request within the pool

## Bulk APIs (ALWAYS prefer)

- Use `CreateMultiple`, `UpdateMultiple` over `ExecuteMultiple`
- Batch sizes: 1000 records per bulk call (Dataverse limit)

## Error Handling

- Wrap all exceptions in `PpdsException` with `ErrorCode` — enables programmatic handling
- Never throw raw exceptions from Application Services
- ErrorCode must be specific enough for callers to distinguish failure modes

## Generated Code

- Never edit files in `src/PPDS.Dataverse/Generated/` — auto-generated early-bound entities
- Regenerate via `pac modelbuilder build` when schema changes

## Progress Reporting

- Accept `IProgressReporter` for any operation expected to take >1 second
- All UIs (CLI, TUI, Extension) need feedback — single code path via Application Services
