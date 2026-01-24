# PPDS Specification Generation Plan

**Repository:** ppds (Power Platform Developer Suite)
**Created:** 2026-01-23
**Status:** Ready for Approval

---

## Summary

| Metric | Count |
|--------|-------|
| Projects explored | 7 |
| Total source files | 467 |
| Total interfaces | 52 |
| Specs to generate | 12 |
| Excluded systems | 7 (with justification) |

---

## Tasks

### Spec Generation Order

- [x] **1. Generate spec: architecture.md** <!-- id: spec-arch -->
    - Source: Cross-cutting (PPDS.Cli/Infrastructure/)
    - Priority: P0 (Blocking - all others depend on this)
    - Key interfaces: IOperationProgress, IOutputWriter, PpdsException, ErrorCodes

- [x] **2. Generate spec: connection-pooling.md** <!-- id: spec-pool -->
    - Source: src/PPDS.Dataverse/Pooling/, Resilience/
    - Priority: P0 (Core infrastructure)
    - Key interfaces: IDataverseConnectionPool, IPooledClient, IConnectionSource, IThrottleTracker

- [x] **3. Generate spec: bulk-operations.md** <!-- id: spec-bulk -->
    - Source: src/PPDS.Dataverse/BulkOperations/
    - Priority: P1
    - Key interfaces: IBulkOperationExecutor

- [x] **4. Generate spec: authentication.md** <!-- id: spec-auth -->
    - Source: src/PPDS.Auth/ (all subdirectories)
    - Priority: P1
    - Key interfaces: ICredentialProvider, ISecureCredentialStore, IPowerPlatformTokenProvider, IGlobalDiscoveryService

- [x] **5. Generate spec: migration.md** <!-- id: spec-migration -->
    - Source: src/PPDS.Migration/ (all subdirectories)
    - Priority: P1
    - Key interfaces: IImporter, IExporter, IDependencyGraphBuilder, IExecutionPlanBuilder, CMT format interfaces

- [x] **6. Generate spec: query.md** <!-- id: spec-query -->
    - Source: src/PPDS.Dataverse/Sql/, Query/; src/PPDS.Cli/Services/Query/, History/
    - Priority: P2
    - Key interfaces: IQueryExecutor, ISqlQueryService, IQueryHistoryService

- [ ] **7. Generate spec: dataverse-services.md** <!-- id: spec-dv-svc -->
    - Source: src/PPDS.Dataverse/Services/, Metadata/
    - Priority: P2
    - Key interfaces: ISolutionService, IPluginTraceService, IFlowService, IRoleService, IUserService, +4 more

- [ ] **8. Generate spec: cli.md** <!-- id: spec-cli -->
    - Source: src/PPDS.Cli/Commands/, Infrastructure/Output/
    - Priority: P2
    - Key patterns: Command groups, output formatting, GlobalOptions

- [ ] **9. Generate spec: tui.md** <!-- id: spec-tui -->
    - Source: src/PPDS.Cli/Tui/
    - Priority: P2
    - Key interfaces: ITuiScreen, ITuiThemeService, ITuiErrorService, ITuiStateCapture

- [ ] **10. Generate spec: mcp.md** <!-- id: spec-mcp -->
    - Source: src/PPDS.Mcp/
    - Priority: P2
    - Key interfaces: IMcpConnectionPoolManager, tool registration patterns

- [ ] **11. Generate spec: plugins.md** <!-- id: spec-plugins -->
    - Source: src/PPDS.Plugins/, src/PPDS.Cli/Plugins/
    - Priority: P3
    - Key interfaces: IPluginRegistrationService, plugin attributes

- [ ] **12. Generate spec: analyzers.md** <!-- id: spec-analyzers -->
    - Source: src/PPDS.Analyzers/
    - Priority: P3
    - Covers: Roslyn analyzers enforcing NEVER rules

---

## Dependency Graph

```
architecture.md (1)
    |
    +-- connection-pooling.md (2)
    |       |
    |       +-- bulk-operations.md (3)
    |       +-- query.md (6)
    |       +-- dataverse-services.md (7) --> plugins.md (11)
    |       +-- mcp.md (10)
    |
    +-- authentication.md (4) --> mcp.md (10)
    |
    +-- cli.md (8) --> tui.md (9)
    |
    +-- analyzers.md (12)

migration.md (5) depends on: connection-pooling.md, bulk-operations.md
```

---

## Exploration Evidence

### Phase 1.2: Project Inventory

| Project | Subdirectory | Files | Key Interfaces | Notes |
|---------|--------------|-------|----------------|-------|
| PPDS.Analyzers | Rules/ | 3 | - | Roslyn analyzers |
| PPDS.Analyzers | (root) | 1 | - | DiagnosticIds |
| PPDS.Auth | Credentials/ | 22 | ICredentialProvider, ISecureCredentialStore, IPowerPlatformTokenProvider | MSAL, federated identity |
| PPDS.Auth | Profiles/ | 11 | - | Profile storage, encryption |
| PPDS.Auth | Discovery/ | 4 | IGlobalDiscoveryService | Environment discovery |
| PPDS.Auth | Cloud/ | 2 | - | Cloud endpoints |
| PPDS.Auth | Pooling/ | 1 | - | ProfileConnectionSource |
| PPDS.Cli | Commands/ | 60+ | - | 18 command groups |
| PPDS.Cli | Services/ | 19 | IConnectionService, IEnvironmentService, IExportService, IProfileService, ISqlQueryService, IQueryHistoryService | Application Services |
| PPDS.Cli | CsvLoader/ | 14 | - | CSV data import |
| PPDS.Cli | Infrastructure/ | 20 | IDaemonConnectionPoolManager, IOperationProgress, IServiceProviderFactory, IRpcLogger, IOutputWriter, IProgressReporter | Cross-cutting |
| PPDS.Cli | Plugins/ | 7 | IPluginRegistrationService | Registration, extraction |
| PPDS.Cli | Tui/ | 57 | ITuiScreen, ITuiThemeService, ITuiErrorService, ITuiStateCapture | Terminal UI |
| PPDS.Dataverse | Pooling/ | 12 | IDataverseConnectionPool, IConnectionSource, IPooledClient, IConnectionSelectionStrategy | Connection pool |
| PPDS.Dataverse | BulkOperations/ | 6 | IBulkOperationExecutor | Bulk API |
| PPDS.Dataverse | Services/ | 15 | ISolutionService, IPluginTraceService, IFlowService, IRoleService, IUserService, IImportJobService, IEnvironmentVariableService, IConnectionReferenceService, IDeploymentSettingsService | Domain services |
| PPDS.Dataverse | Query/ | 5 | IQueryExecutor | FetchXml/SQL |
| PPDS.Dataverse | Resilience/ | 7 | IThrottleTracker | Throttle handling |
| PPDS.Dataverse | Sql/ | 3 | - | AST nodes |
| PPDS.Dataverse | Metadata/ | 2 | IMetadataService | Schema queries |
| PPDS.Dataverse | Configuration/ | 7 | - | Connection string building |
| PPDS.Mcp | Tools/ | 13 | - | MCP tool implementations |
| PPDS.Mcp | Infrastructure/ | 4 | IMcpConnectionPoolManager | MCP pool management |
| PPDS.Migration | Import/ | 13 | IImporter, IImportPhaseProcessor, ISchemaValidator | Tiered import |
| PPDS.Migration | Export/ | 4 | IExporter | Parallel export |
| PPDS.Migration | Analysis/ | 4 | IDependencyGraphBuilder, IExecutionPlanBuilder | Dependency analysis |
| PPDS.Migration | Formats/ | 8 | ICmtDataReader, ICmtDataWriter, ICmtSchemaReader, ICmtSchemaWriter | CMT format |
| PPDS.Migration | Progress/ | 13 | IProgressReporter, IWarningCollector | Progress tracking |
| PPDS.Migration | Models/ | 10 | - | Domain models |
| PPDS.Migration | Schema/ | 3 | ISchemaGenerator | Schema generation |
| PPDS.Plugins | Attributes/ | 2 | - | Plugin attributes |
| PPDS.Plugins | Enums/ | 3 | - | Plugin enums |

### Phase 2: Significance Matrix

| Subdirectory | Files | Interface? | ADRs? | Verdict | Proof |
|--------------|-------|------------|-------|---------|-------|
| PPDS.Auth/Credentials/ | 22 | Yes (3) | - | SPEC NEEDED | authentication.md |
| PPDS.Auth/Profiles/ | 11 | No | - | SPEC NEEDED | authentication.md (same spec) |
| PPDS.Auth/Discovery/ | 4 | Yes (1) | - | SPEC NEEDED | authentication.md (same spec) |
| PPDS.Cli/Commands/ | 60+ | No | - | SPEC NEEDED | cli.md |
| PPDS.Cli/Services/ | 19 | Yes (7) | ADR-0015,24 | SPEC NEEDED | Split: architecture.md (patterns), cli.md (commands) |
| PPDS.Cli/Infrastructure/ | 20 | Yes (6) | ADR-0025,26 | SPEC NEEDED | architecture.md |
| PPDS.Cli/Tui/ | 57 | Yes (4) | ADR-0028 | SPEC NEEDED | tui.md |
| PPDS.Cli/CsvLoader/ | 14 | No | - | COVERED | migration.md (data import utility) |
| PPDS.Cli/Plugins/ | 7 | Yes (1) | - | SPEC NEEDED | plugins.md |
| PPDS.Dataverse/Pooling/ | 12 | Yes (4) | ADR-0002,05 | SPEC NEEDED | connection-pooling.md |
| PPDS.Dataverse/BulkOperations/ | 6 | Yes (1) | - | SPEC NEEDED | bulk-operations.md |
| PPDS.Dataverse/Services/ | 15 | Yes (9) | - | SPEC NEEDED | dataverse-services.md |
| PPDS.Dataverse/Query/ | 5 | Yes (1) | - | SPEC NEEDED | query.md |
| PPDS.Dataverse/Resilience/ | 7 | Yes (1) | - | SPEC NEEDED | connection-pooling.md (same spec) |
| PPDS.Dataverse/Sql/ | 3 | No | - | SPEC NEEDED | query.md (same spec) |
| PPDS.Dataverse/Metadata/ | 2 | Yes (1) | - | SPEC NEEDED | dataverse-services.md (same spec) |
| PPDS.Dataverse/Configuration/ | 7 | No | - | SKIP | Config DTOs, covered in spec Configuration sections |
| PPDS.Mcp/Tools/ | 13 | No | - | SPEC NEEDED | mcp.md |
| PPDS.Mcp/Infrastructure/ | 4 | Yes (1) | - | SPEC NEEDED | mcp.md (same spec) |
| PPDS.Migration/Import/ | 13 | Yes (3) | - | SPEC NEEDED | migration.md |
| PPDS.Migration/Export/ | 4 | Yes (1) | - | SPEC NEEDED | migration.md (same spec) |
| PPDS.Migration/Analysis/ | 4 | Yes (2) | - | SPEC NEEDED | migration.md (same spec) |
| PPDS.Migration/Formats/ | 8 | Yes (4) | - | SPEC NEEDED | migration.md (same spec) |
| PPDS.Migration/Progress/ | 13 | Yes (2) | - | SPEC NEEDED | migration.md (same spec) |
| PPDS.Migration/Models/ | 10 | No | - | SPEC NEEDED | migration.md (same spec) |
| PPDS.Plugins/Attributes/ | 2 | No | - | SPEC NEEDED | plugins.md |
| PPDS.Plugins/Enums/ | 3 | No | - | SKIP | <5 files, simple enums |
| PPDS.Analyzers/Rules/ | 3 | No | - | SPEC NEEDED | analyzers.md |

---

## Exclusions (Justified)

| Excluded Item | Files | Reason |
|---------------|-------|--------|
| Individual CLI commands | 60+ | Self-documenting via `--help`. Patterns covered in cli.md. |
| PPDS.Dataverse/Generated/ | 127 | Auto-generated early-bound entities. DO NOT edit per CLAUDE.md. |
| PPDS.Dataverse/Configuration/ | 7 | Config DTOs - covered in relevant spec Configuration sections. |
| PPDS.Plugins/Enums/ | 3 | <5 files, simple enums documented inline in plugins.md. |
| Each credential provider separately | 10 | All follow ICredentialProvider pattern. Covered in authentication.md. |
| Each TUI dialog separately | 17 | All follow TuiDialog base class. Patterns covered in tui.md. |
| Test projects | 444 | Test code follows source structure. Testing sections in each spec. |

---

## Verification Plan

After generating all specs:

1. **Cross-reference check**: Each spec's "Related Specs" section links correctly
2. **Interface coverage**: Every public interface (52 total) is documented in exactly one spec
3. **ADR absorption**: Verify historical ADRs are absorbed into Design Decisions sections
4. **Code pointer validation**: File:line references resolve correctly
5. **Template compliance**: Each spec follows SPEC-TEMPLATE.md structure

---

## Critical Files

| File | Purpose |
|------|---------|
| `specs/SPEC-TEMPLATE.md` | Template all specs must follow |
| `src/PPDS.Dataverse/Pooling/IDataverseConnectionPool.cs` | Core pooling interface |
| `src/PPDS.Cli/Infrastructure/Errors/PpdsException.cs` | Error model |
| `src/PPDS.Auth/Credentials/ICredentialProvider.cs` | Auth pattern |
| `src/PPDS.Migration/Import/IImporter.cs` | Migration entry point |
| `docs/adr/README.md` | ADR governance (absorption policy) |
