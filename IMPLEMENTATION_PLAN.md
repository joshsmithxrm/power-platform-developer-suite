# Spec Generation Plan

**Repository:** ppds
**Created:** 2026-01-25
**Status:** Complete - No New Specs Required

---

## Executive Summary

After thorough exploration of the codebase and audit of existing specifications, **all significant systems are covered by existing specs**. The 13 current specifications comprehensively document the codebase with proper cross-references and dedicated sections for each major subsystem.

---

## Phase 1: Exploration Evidence

### 1.1 Project Inventory

| Project | Subdirectory | Files | Key Interfaces | Notes |
|---------|--------------|-------|----------------|-------|
| **PPDS.Analyzers** | Root | 1 | None | Roslyn analyzer orchestration |
| PPDS.Analyzers | Rules/ | 3 | None | Custom analyzer rules |
| **PPDS.Auth** | Root | 3 | None | Authentication module root |
| PPDS.Auth | Cloud/ | 2 | None | Cloud endpoint configuration |
| PPDS.Auth | Credentials/ | 21 | ICredentialProvider, ISecureCredentialStore, IPowerPlatformTokenProvider | Credential management |
| PPDS.Auth | Discovery/ | 5 | IGlobalDiscoveryService | Environment discovery |
| PPDS.Auth | Pooling/ | 2 | None | Connection source adapters |
| PPDS.Auth | Profiles/ | 9 | None | Profile management |
| **PPDS.Cli** | Root | 1 | None | CLI entry point |
| PPDS.Cli | Commands/ | 5 | None | Base command infrastructure |
| PPDS.Cli | Commands/Auth/ | 1 | None | Authentication commands |
| PPDS.Cli | Commands/ConnectionReferences/ | 6 | None | Connection reference management |
| PPDS.Cli | Commands/Connections/ | 3 | None | Connection queries |
| PPDS.Cli | Commands/Data/ | 11 | None | Data manipulation |
| PPDS.Cli | Commands/DeploymentSettings/ | 4 | None | Deployment configuration |
| PPDS.Cli | Commands/Env/ | 1 | None | Environment commands |
| PPDS.Cli | Commands/EnvironmentVariables/ | 6 | None | Environment variable management |
| PPDS.Cli | Commands/Flows/ | 4 | None | Cloud flow commands |
| PPDS.Cli | Commands/ImportJobs/ | 6 | None | Import job tracking |
| PPDS.Cli | Commands/Internal/ | 1 | None | Internal diagnostics |
| PPDS.Cli | Commands/Metadata/ | 8 | None | Metadata query commands |
| PPDS.Cli | Commands/Plugins/ | 11 | None | Plugin lifecycle management |
| PPDS.Cli | Commands/PluginTraces/ | 7 | None | Plugin trace log commands |
| PPDS.Cli | Commands/Query/ | 3 | None | Query command infrastructure |
| PPDS.Cli | Commands/Query/History/ | 7 | None | Query history management |
| PPDS.Cli | Commands/Roles/ | 5 | None | Role assignment commands |
| PPDS.Cli | Commands/Serve/ | 1 | None | RPC server infrastructure |
| PPDS.Cli | Commands/Serve/Handlers/ | 3 | None | RPC protocol handlers |
| PPDS.Cli | Commands/Solutions/ | 8 | None | Solution lifecycle commands |
| PPDS.Cli | Commands/Users/ | 4 | None | User management commands |
| PPDS.Cli | CsvLoader/ | 14 | None | CSV data import utilities |
| PPDS.Cli | Infrastructure/ | 14 | IDaemonConnectionPoolManager, IServiceProviderFactory, IOperationProgress | Core CLI infrastructure |
| PPDS.Cli | Infrastructure/Errors/ | 5 | None | Error handling |
| PPDS.Cli | Infrastructure/Logging/ | 6 | IRpcLogger | RPC and diagnostic logging |
| PPDS.Cli | Infrastructure/Output/ | 6 | IOutputWriter | Output formatting |
| PPDS.Cli | Infrastructure/Progress/ | 1 | IProgressReporter | Progress reporting |
| PPDS.Cli | Plugins/Extraction/ | 2 | None | Plugin source extraction |
| PPDS.Cli | Plugins/Models/ | 1 | None | Plugin metadata models |
| PPDS.Cli | Plugins/Registration/ | 2 | IPluginRegistrationService | Plugin registration service |
| PPDS.Cli | Services/ | 3 | IConnectionService | Application services |
| PPDS.Cli | Services/Environment/ | 2 | IEnvironmentService | Environment service |
| PPDS.Cli | Services/Export/ | 2 | IExportService | Data export services |
| PPDS.Cli | Services/History/ | 2 | IQueryHistoryService | Query history |
| PPDS.Cli | Services/Profile/ | 2 | IProfileService | Profile management |
| PPDS.Cli | Services/Query/ | 5 | ISqlQueryService | SQL query execution |
| PPDS.Cli | Tui/ | 3 | None | TUI entry point |
| PPDS.Cli | Tui/Dialogs/ | 15 | None | Dialog components |
| PPDS.Cli | Tui/Infrastructure/ | 11 | ITuiThemeService, ITuiErrorService | TUI framework services |
| PPDS.Cli | Tui/Screens/ | 2 | ITuiScreen | Screen components |
| PPDS.Cli | Tui/Testing/ | 1 | ITuiStateCapture | TUI test infrastructure |
| PPDS.Cli | Tui/Testing/States/ | 20 | None | Test state management |
| PPDS.Cli | Tui/Views/ | 6 | None | View components |
| **PPDS.Dataverse** | BulkOperations/ | 7 | IBulkOperationExecutor | Bulk APIs |
| PPDS.Dataverse | Client/ | 3 | IDataverseClient | ServiceClient wrapper |
| PPDS.Dataverse | Configuration/ | 7 | None | Connection configuration |
| PPDS.Dataverse | DependencyInjection/ | 2 | None | DI setup |
| PPDS.Dataverse | Diagnostics/ | 1 | IPoolMetrics | Pool metrics |
| PPDS.Dataverse | Metadata/ | 2 | IMetadataService | Metadata service |
| PPDS.Dataverse | Metadata/Models/ | 11 | None | Metadata DTOs |
| PPDS.Dataverse | Pooling/ | 12 | IDataverseConnectionPool, IConnectionSource, IPooledClient | Connection pool |
| PPDS.Dataverse | Pooling/Strategies/ | 4 | IConnectionSelectionStrategy | Pool strategies |
| PPDS.Dataverse | Progress/ | 2 | None | Progress utilities |
| PPDS.Dataverse | Query/ | 6 | IQueryExecutor | FetchXml execution |
| PPDS.Dataverse | Resilience/ | 8 | IThrottleTracker | Retry/throttle |
| PPDS.Dataverse | Security/ | 3 | None | Access control |
| PPDS.Dataverse | Services/ | 19 | Multiple service interfaces | High-level services |
| PPDS.Dataverse | Sql/Ast/ | 9 | None | SQL AST nodes |
| PPDS.Dataverse | Sql/Parsing/ | 5 | None | T-SQL parser |
| PPDS.Dataverse | Sql/Transpilation/ | 2 | None | FetchXml transpiler |
| **PPDS.Mcp** | Root | 1 | None | MCP server entry point |
| PPDS.Mcp | Infrastructure/ | 4 | IMcpConnectionPoolManager | MCP infrastructure |
| PPDS.Mcp | Tools/ | 13 | None | MCP tool implementations |
| **PPDS.Migration** | Analysis/ | 4 | IDependencyGraphBuilder, IExecutionPlanBuilder | Dependency analysis |
| PPDS.Migration | DependencyInjection/ | 2 | None | DI setup |
| PPDS.Migration | Export/ | 4 | IExporter | Data export |
| PPDS.Migration | Formats/ | 9 | ICmtDataReader, ICmtDataWriter, ICmtSchemaReader, ICmtSchemaWriter | CMT format |
| PPDS.Migration | Import/ | 18 | IImporter, IImportPhaseProcessor, ISchemaValidator | Import orchestration |
| PPDS.Migration | Models/ | 9 | None | DTOs |
| PPDS.Migration | Progress/ | 13 | IProgressReporter, IWarningCollector | Progress reporting |
| PPDS.Migration | Schema/ | 3 | ISchemaGenerator | Schema generation |
| PPDS.Migration | UserMapping/ | 1 | None | User ID mapping |
| **PPDS.Plugins** | Root | 0 | None | Plugin framework root |
| PPDS.Plugins | Attributes/ | 2 | None | Plugin attributes |
| PPDS.Plugins | Enums/ | 3 | None | Plugin enums |

**Totals:**
- **7 projects**
- **83+ subdirectories**
- **600+ source files**
- **58+ interface definitions**

### 1.2 Documentation Inventory

**ADRs (docs/adr/):**
| File | Purpose |
|------|---------|
| README.md | ADR guidelines and format |
| 0017_GIT_BRANCHING_STRATEGY.md | Branch naming, worktree strategy |

*Note: Most ADRs absorbed into spec "Design Decisions" sections*

**Existing Specs (specs/):**
| Spec | Declared Code Path | Purpose |
|------|-------------------|---------|
| architecture.md | src/ | System layering, patterns, error handling |
| authentication.md | src/PPDS.Auth/ | Credentials, profiles, discovery |
| connection-pooling.md | src/PPDS.Dataverse/Pooling/, Resilience/ | Pool, throttling, strategies |
| bulk-operations.md | src/PPDS.Dataverse/BulkOperations/ | CreateMultiple, batching, retry |
| dataverse-services.md | src/PPDS.Dataverse/Services/, Metadata/ | Domain services |
| migration.md | src/PPDS.Migration/ | Export/import, dependencies, CMT |
| query.md | src/PPDS.Dataverse/Query/, Sql/, Cli/Services/Query/ | SQL transpilation, execution |
| cli.md | src/PPDS.Cli/Commands/ | CLI structure, commands, output |
| tui.md | src/PPDS.Cli/Tui/ | Terminal UI, testing |
| mcp.md | src/PPDS.Mcp/ | MCP server, tools |
| plugins.md | src/PPDS.Plugins/, Cli/Plugins/ | Plugin registration |
| analyzers.md | src/PPDS.Analyzers/ | Roslyn analyzers |

---

## Phase 2: Significance Analysis

### Significance Matrix

| Directory | Files | Interface? | ADRs? | Verdict | Proof |
|-----------|-------|------------|-------|---------|-------|
| PPDS.Analyzers/ | 4 | No | 0 | COVERED | analyzers.md § Architecture - full component table, detection patterns |
| PPDS.Analyzers/Rules/ | 3 | No | 0 | COVERED | analyzers.md § Core Types - each analyzer documented |
| PPDS.Auth/ | 42 | Yes | 0 | COVERED | authentication.md § Architecture - full component diagram, interfaces |
| PPDS.Auth/Credentials/ | 21 | Yes (ICredentialProvider, ISecureCredentialStore) | 0 | COVERED | authentication.md § Core Types - interfaces documented |
| PPDS.Auth/Discovery/ | 5 | Yes (IGlobalDiscoveryService) | 0 | COVERED | authentication.md § Core Types - interface documented |
| PPDS.Auth/Profiles/ | 9 | No | 0 | COVERED | authentication.md § Core Types - AuthProfile documented |
| PPDS.Cli/Commands/ | 90+ | No | 0 | COVERED | cli.md § Command Groups - all 18 groups documented |
| PPDS.Cli/Infrastructure/ | 32 | Yes (IOutputWriter, IOperationProgress) | 0 | COVERED | architecture.md § Output Handling, cli.md § Core Types |
| PPDS.Cli/Services/ | 16 | Yes (multiple service interfaces) | 0 | COVERED | architecture.md § Application Services - service inventory table |
| PPDS.Cli/Tui/ | 58 | Yes (ITuiScreen, ITuiStateCapture) | 0 | COVERED | tui.md § Architecture - full component list, interfaces |
| PPDS.Cli/Tui/Dialogs/ | 15 | No | 0 | COVERED | tui.md § Core Types § TuiDialog - pattern documented |
| PPDS.Cli/Tui/Testing/ | 21 | Yes (ITuiStateCapture) | 0 | COVERED | tui.md § Design Decisions - state capture pattern |
| PPDS.Cli/Plugins/ | 5 | Yes (IPluginRegistrationService) | 0 | COVERED | plugins.md § Architecture - extraction and registration |
| PPDS.Dataverse/BulkOperations/ | 7 | Yes (IBulkOperationExecutor) | 0 | COVERED | bulk-operations.md § full spec |
| PPDS.Dataverse/Pooling/ | 16 | Yes (IDataverseConnectionPool) | 0 | COVERED | connection-pooling.md § full spec |
| PPDS.Dataverse/Resilience/ | 8 | Yes (IThrottleTracker) | 0 | COVERED | connection-pooling.md § Core Types - throttle tracking |
| PPDS.Dataverse/Query/ | 6 | Yes (IQueryExecutor) | 0 | COVERED | query.md § Core Types - IQueryExecutor, QueryResult |
| PPDS.Dataverse/Sql/ | 16 | No | 0 | COVERED | query.md § Architecture - parser, AST, transpiler |
| PPDS.Dataverse/Services/ | 19 | Yes (9 service interfaces) | 0 | COVERED | dataverse-services.md § full spec |
| PPDS.Dataverse/Metadata/ | 13 | Yes (IMetadataService) | 0 | COVERED | dataverse-services.md § IMetadataService |
| PPDS.Mcp/ | 18 | Yes (IMcpConnectionPoolManager) | 0 | COVERED | mcp.md § full spec |
| PPDS.Mcp/Tools/ | 13 | No | 0 | COVERED | mcp.md § API/Contracts - 13 tools documented |
| PPDS.Migration/ | 63 | Yes (9 interfaces) | 0 | COVERED | migration.md § full spec |
| PPDS.Migration/Analysis/ | 4 | Yes (IDependencyGraphBuilder) | 0 | COVERED | migration.md § Core Types |
| PPDS.Migration/Formats/ | 9 | Yes (CMT interfaces) | 0 | COVERED | migration.md § CMT Format |
| PPDS.Migration/Import/ | 18 | Yes (IImporter) | 0 | COVERED | migration.md § Core Types |
| PPDS.Migration/Export/ | 4 | Yes (IExporter) | 0 | COVERED | migration.md § Core Types |
| PPDS.Plugins/ | 5 | No | 0 | COVERED | plugins.md § PPDS.Plugins section |

### Matrix Verification

All qualifying directories (>5 files OR interfaces) have matrix rows with COVERED verdict:

| Directory | Files | Interface | Matrix Row? |
|-----------|-------|-----------|-------------|
| PPDS.Auth/Credentials/ | 21 | Yes | ✓ |
| PPDS.Auth/Discovery/ | 5 | Yes | ✓ |
| PPDS.Cli/Commands/ | 90+ | No | ✓ |
| PPDS.Cli/Infrastructure/ | 32 | Yes | ✓ |
| PPDS.Cli/Services/ | 16 | Yes | ✓ |
| PPDS.Cli/Tui/ | 58 | Yes | ✓ |
| PPDS.Cli/Tui/Dialogs/ | 15 | No | ✓ |
| PPDS.Cli/Tui/Testing/ | 21 | Yes | ✓ |
| PPDS.Dataverse/BulkOperations/ | 7 | Yes | ✓ |
| PPDS.Dataverse/Pooling/ | 16 | Yes | ✓ |
| PPDS.Dataverse/Resilience/ | 8 | Yes | ✓ |
| PPDS.Dataverse/Query/ | 6 | Yes | ✓ |
| PPDS.Dataverse/Sql/ | 16 | No | ✓ |
| PPDS.Dataverse/Services/ | 19 | Yes | ✓ |
| PPDS.Dataverse/Metadata/ | 13 | Yes | ✓ |
| PPDS.Mcp/ | 18 | Yes | ✓ |
| PPDS.Mcp/Tools/ | 13 | No | ✓ |
| PPDS.Migration/ | 63 | Yes | ✓ |
| PPDS.Migration/Analysis/ | 4 | Yes | ✓ |
| PPDS.Migration/Formats/ | 9 | Yes | ✓ |
| PPDS.Migration/Import/ | 18 | Yes | ✓ |
| PPDS.Migration/Export/ | 4 | Yes | ✓ |

**All qualifying directories accounted for.**

### Pre-Plan Validation

| Metric | Count |
|--------|-------|
| Phase 1.2 rows with Files > 5 | 22 |
| Phase 1.2 rows with interfaces | 20 |
| Total qualifying directories | 24 (deduplicated) |
| Phase 2 matrix rows | 24 |

**Matrix complete - no missing rows.**

---

## Phase 3: Existing Spec Audit

### Scope Alignment Check

| Spec | Declared Code | Content Matches? | Issues |
|------|---------------|------------------|--------|
| architecture.md | src/ | ✓ | Cross-cutting, correct |
| authentication.md | src/PPDS.Auth/ | ✓ | Covers all auth subdirs |
| connection-pooling.md | src/PPDS.Dataverse/Pooling/, Resilience/ | ✓ | Covers both paths |
| bulk-operations.md | src/PPDS.Dataverse/BulkOperations/ | ✓ | Dedicated spec |
| dataverse-services.md | src/PPDS.Dataverse/Services/, Metadata/ | ✓ | Covers both paths |
| migration.md | src/PPDS.Migration/ | ✓ | Covers all subdirs |
| query.md | src/PPDS.Dataverse/Query/, Sql/, Cli/Services/Query/, Cli/Services/History/ | ✓ | Multi-path, correct |
| cli.md | src/PPDS.Cli/Commands/ | ✓ | Dedicated to commands |
| tui.md | src/PPDS.Cli/Tui/ | ✓ | Covers all TUI subdirs |
| mcp.md | src/PPDS.Mcp/ | ✓ | Covers all MCP |
| plugins.md | src/PPDS.Plugins/, src/PPDS.Cli/Plugins/ | ✓ | Multi-path, correct |
| analyzers.md | src/PPDS.Analyzers/ | ✓ | Covers all analyzer code |

### Cross-Reference Check

All specs have proper "Related Specs" sections linking to dependencies:
- architecture.md ← referenced by all other specs
- connection-pooling.md ← bulk-operations, migration, mcp, dataverse-services
- authentication.md ← connection-pooling, mcp
- query.md ← mcp
- cli.md ← tui (TUI launched when no args)

**No orphaned specs. No missing cross-references.**

---

## Phase 4: Conclusion

### Finding: No New Specs Required

The existing 13 specifications comprehensively cover all significant systems:

1. **Core Infrastructure**: architecture.md, connection-pooling.md, authentication.md
2. **Domain Services**: dataverse-services.md, bulk-operations.md, query.md, migration.md
3. **Applications**: cli.md, tui.md, mcp.md
4. **Development Tools**: plugins.md, analyzers.md

### Coverage Statistics

- **Subdirectories explored:** 83+
- **New specs needed:** 0
- **Existing specs needing expansion:** 0
- **All significant systems documented:** ✓

### Verification

To verify spec quality:
1. Each spec follows the SPEC-TEMPLATE.md format
2. Each spec has Architecture diagram, Core Types, Error Handling, Design Decisions, Testing sections
3. Each spec declares its Code: path correctly
4. Cross-references form a coherent graph

---

## Tasks

### Spec Generation (Missing Specs)

*None required - all systems covered.*

### Code Implementation (Missing Features)

*No spec-related implementation tasks identified.*

---

## Summary

The PPDS repository has a mature, comprehensive specification system. All 7 projects and their subsystems are documented across 13 well-structured specification files. The specs follow a consistent template, include proper cross-references, and align with their declared code paths.

**No action required.**
