# Spec Generation Plan

**Repository:** PPDS (Power Platform Developer Suite)
**Created:** 2026-01-21
**Status:** In Progress

## Summary

PPDS already has a comprehensive `specs/IMPLEMENTATION_PLAN.md` with 11 systems identified and 32+ ADRs mapped. This plan confirms that existing structure and provides the execution strategy.

## Systems to Document

| # | Spec | Source | Priority | Status |
|---|------|--------|----------|--------|
| 1 | architecture.md | Cross-cutting | P0 | Complete |
| 2 | connection-pool.md | src/PPDS.Dataverse/Pooling/ | P1 | Complete |
| 3 | authentication.md | src/PPDS.Auth/ | P1 | Pending |
| 4 | cli.md | src/PPDS.Cli/Commands/ | P2 | Pending |
| 5 | application-services.md | src/PPDS.Cli/Services/ | P2 | Pending |
| 6 | migration.md | src/PPDS.Migration/ | P3 | Pending |
| 7 | tui.md | src/PPDS.Cli/Tui/ | P3 | Pending |
| 8 | error-handling.md | src/PPDS.Cli/Infrastructure/ | P4 | Pending |
| 9 | mcp.md | src/PPDS.Mcp/ | P4 | Pending |
| 10 | testing.md | tests/ | P5 | Pending |
| 11 | plugins.md | src/PPDS.Plugins/ | P5 | Pending |

## Priority Guidelines

- **P0**: Foundation (architecture.md) - always first, establishes patterns all others reference
- **P1**: Core infrastructure (connection pool, auth) - no internal dependencies
- **P2**: Interface layer (CLI, Application Services) - depends on P0
- **P3**: Feature systems (migration, TUI) - depends on P1-P2
- **P4**: Secondary systems (MCP, error handling) - depends on P1-P3
- **P5**: Standalone or auxiliary (testing, plugins)

## ADR Inventory

32+ ADRs to absorb into specs:

| ADR | Target Spec | Key Decision |
|-----|-------------|--------------|
| 0001 | connection-pool.md | Disable affinity cookie for 10x+ throughput |
| 0002 | connection-pool.md | Multi-connection pooling for quota multiplication |
| 0003 | connection-pool.md | Throttle-aware connection selection strategy |
| 0004 | connection-pool.md | Transparent wait on throttle recovery |
| 0005 | connection-pool.md | DOP-based parallelism, no adaptive ramping |
| 0006 | connection-pool.md | IConnectionSource abstraction |
| 0007 | authentication.md | PAC-compatible profile model |
| 0008 | cli.md | Three-system output (Logger, OutputWriter, ProgressReporter) |
| 0009 | cli.md | Entity-aligned command taxonomy |
| 0013 | cli.md | --dry-run convention |
| 0014 | migration.md | CSV mapping schema versioning |
| 0015 | architecture.md | Application Service Layer pattern |
| 0016 | cli.md | JSON for config, JSONL for streaming |
| 0018-mcp | mcp.md | MCP server tool selection, read-heavy principle |
| 0018-profile | authentication.md | Profile session isolation |
| 0019 | connection-pool.md | Semaphore-based fair queuing |
| 0020 | migration.md | Error report JSON with RecordId preservation |
| 0022 | migration.md | Import diagnostics, probe-once pattern |
| 0023 | cli.md | Binary release naming contract |
| 0024 | architecture.md | ~/.ppds/ shared local state |
| 0025 | application-services.md | IProgressReporter interface |
| 0026 | error-handling.md | PpdsException with ErrorCode |
| 0027-multi-interface | architecture.md | TUI-first, interface development order |
| 0027-operation-clock | application-services.md | Elapsed time ownership |
| 0027-unified-auth | authentication.md | HomeAccountId persistence, SSO |
| 0028 | tui.md, testing.md | TUI testing with IServiceProviderFactory |
| 0029 | testing.md | Unit/Integration/TUI category filters |
| 0032 | authentication.md | Native OS credential storage |

**Deferred ADRs** (future data-commands.md spec):
- 0010: Published vs unpublished default
- 0011: Deployment settings file format
- 0012: Hybrid filter design for plugin traces
- 0021: Truncate command specification

## Implementation Phases

### Phase 1: Foundation
Generate `architecture.md` first - establishes:
- Multi-interface TUI-first pattern
- Project dependency diagram
- Shared state layout (~/.ppds/)
- Application Services pattern

### Phase 2: Core Infrastructure (parallel)
- **connection-pool.md** - Pool architecture, 7 ADRs with benchmark data
- **authentication.md** - Profile model, 8+ credential providers, token caching

### Phase 3: Interface Layer (parallel)
- **cli.md** - Output architecture, command taxonomy, conventions
- **application-services.md** - Service patterns, IProgressReporter

### Phase 4: Feature Systems (parallel)
- **migration.md** - Import/export pipeline, diagnostics
- **tui.md** - Screen architecture, dialogs, testing

### Phase 5: Secondary Systems (parallel)
- **error-handling.md** - PpdsException hierarchy, error codes
- **mcp.md** - 13+ tools, integration patterns

### Phase 6: Auxiliary
- **testing.md** - Test categories, CI filtering
- **plugins.md** - Registration attributes, strong naming

## Critical Files per Spec

### architecture.md
- `docs/adr/0015*.md` - Application Service Layer ADR
- `docs/adr/0024*.md` - Shared local state ADR
- `docs/adr/0027*.md` - Multi-interface ADRs
- `src/PPDS.Cli/Services/` - Application Services implementations

### connection-pool.md
- `src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs` (59KB)
- `src/PPDS.Dataverse/Pooling/IDataverseConnectionPool.cs`
- `docs/adr/0001*.md` through `docs/adr/0006*.md`, `docs/adr/0019*.md`
- `docs/architecture/CONNECTION_POOLING_PATTERNS.md`

### authentication.md
- `src/PPDS.Auth/Profiles/ProfileStore.cs`
- `src/PPDS.Auth/Credentials/` - All credential providers
- `src/PPDS.Auth/ServiceClientFactory.cs`
- `docs/adr/0007*.md`, `docs/adr/0018-profile*.md`, `docs/adr/0032*.md`

### cli.md
- `src/PPDS.Cli/Program.cs`
- `src/PPDS.Cli/Commands/` - Command implementations
- `docs/adr/0008*.md`, `docs/adr/0009*.md`, `docs/adr/0013*.md`

### application-services.md
- `src/PPDS.Cli/Services/*.cs` - All service interfaces
- `docs/adr/0025*.md` - IProgressReporter
- `docs/adr/0027-operation-clock*.md`

### migration.md
- `src/PPDS.Migration/` - Core migration classes
- `docs/adr/0014*.md`, `docs/adr/0020*.md`, `docs/adr/0022*.md`

### tui.md
- `src/PPDS.Cli/Tui/` - All TUI screens and dialogs
- `docs/patterns/tui-loading.md`
- `docs/adr/0028*.md`

### mcp.md
- `src/PPDS.Mcp/Program.cs`
- `src/PPDS.Mcp/Tools/*.cs` - All MCP tools
- `docs/adr/0018-mcp*.md`

### error-handling.md
- `src/PPDS.Cli/Infrastructure/Errors/`
- `docs/adr/0026*.md`

### testing.md
- `tests/` - All test projects
- `docs/adr/0028*.md`, `docs/adr/0029*.md`

### plugins.md
- `src/PPDS.Plugins/` - Attribute definitions
- `PPDS.Plugins.snk` - Strong naming key

## Verification

After each spec is generated:
1. Verify all mapped ADRs are absorbed into Design Decisions
2. Check that code references use `file:line` format
3. Ensure no broken links to other specs
4. Validate template structure is followed

## Notes

1. **Pre-existing plan exists** - `specs/IMPLEMENTATION_PLAN.md` already maps ADRs to specs
2. **Rich ADR content** - 34 ADRs provide excellent source material for Design Decisions
3. **connection-pool.md is largest** - 7 ADRs including benchmark data from ADR-0005
4. **Some ADRs span specs** - ADR-0015 content splits between architecture.md and application-services.md
5. **Template ready** - `specs/SPEC-TEMPLATE.md` defines standard structure
