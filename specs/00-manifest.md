# PPDS Specification Manifest

Progress tracking for specification generation. Claude reads this file each iteration to find the next pending subsystem.

## Legend

- **pending** - Not yet documented
- **complete** - Spec written and committed
- **needs-review** - Spec written but has gaps or questions

---

## Components

### 1. PPDS.Dataverse

| # | Subsystem | Status | Spec File |
|---|-----------|--------|-----------|
| 1.1 | Connection Pooling | complete | [01-connection-pooling.md](01-dataverse/01-connection-pooling.md) |
| 1.2 | Bulk Operations | complete | [02-bulk-operations.md](01-dataverse/02-bulk-operations.md) |
| 1.3 | Throttle Management | complete | [03-throttle-management.md](01-dataverse/03-throttle-management.md) |
| 1.4 | SQL Transpiler | complete | [04-sql-transpiler.md](01-dataverse/04-sql-transpiler.md) |
| 1.5 | Metadata Service | complete | [05-metadata-service.md](01-dataverse/05-metadata-service.md) |
| 1.6 | Query Executor | complete | [06-query-executor.md](01-dataverse/06-query-executor.md) |

### 2. PPDS.Auth

| # | Subsystem | Status | Spec File |
|---|-----------|--------|-----------|
| 2.1 | Profile Storage | complete | [01-profile-storage.md](02-auth/01-profile-storage.md) |
| 2.2 | Credential Providers | complete | [02-credential-providers.md](02-auth/02-credential-providers.md) |
| 2.3 | Token Management | complete | [03-token-management.md](02-auth/03-token-management.md) |
| 2.4 | Environment Discovery | complete | [04-environment-discovery.md](02-auth/04-environment-discovery.md) |
| 2.5 | Cloud Support | complete | [05-cloud-support.md](02-auth/05-cloud-support.md) |

### 3. PPDS.Migration

| # | Subsystem | Status | Spec File |
|---|-----------|--------|-----------|
| 3.1 | Dependency Analysis (Tarjan) | complete | [01-dependency-analysis.md](03-migration/01-dependency-analysis.md) |
| 3.2 | Export Pipeline | complete | [02-export-pipeline.md](03-migration/02-export-pipeline.md) |
| 3.3 | Import Pipeline | complete | [03-import-pipeline.md](03-migration/03-import-pipeline.md) |
| 3.4 | Circular References | complete | [04-circular-references.md](03-migration/04-circular-references.md) |
| 3.5 | CMT Compatibility | complete | [05-cmt-compatibility.md](03-migration/05-cmt-compatibility.md) |
| 3.6 | User Mapping | complete | [06-user-mapping.md](03-migration/06-user-mapping.md) |

### 4. PPDS.Cli Services

| # | Subsystem | Status | Spec File |
|---|-----------|--------|-----------|
| 4.1 | Application Services | complete | [01-application-services.md](04-cli-services/01-application-services.md) |
| 4.2 | Connection Service | complete | [02-connection-service.md](04-cli-services/02-connection-service.md) |
| 4.3 | Export Service | complete | [03-export-service.md](04-cli-services/03-export-service.md) |

### 5. PPDS.TUI

| # | Subsystem | Status | Spec File |
|---|-----------|--------|-----------|
| 5.1 | Architecture | pending | - |
| 5.2 | Testing Harness | pending | - |
| 5.3 | Dialogs | pending | - |

### 6. PPDS.Mcp

| # | Subsystem | Status | Spec File |
|---|-----------|--------|-----------|
| 6.1 | Server Architecture | pending | - |
| 6.2 | Tools | pending | - |

### 7. VS Code Extension

| # | Subsystem | Status | Spec File |
|---|-----------|--------|-----------|
| 7.1 | Architecture | pending | - |
| 7.2 | Features | pending | - |

### 8. PPDS.Plugins

| # | Subsystem | Status | Spec File |
|---|-----------|--------|-----------|
| 8.1 | Attributes | pending | - |
| 8.2 | Analyzers | pending | - |

---

## Summary

| Component | Total | Complete | Pending |
|-----------|-------|----------|---------|
| PPDS.Dataverse | 6 | 6 | 0 |
| PPDS.Auth | 5 | 5 | 0 |
| PPDS.Migration | 6 | 6 | 0 |
| PPDS.Cli Services | 3 | 3 | 0 |
| PPDS.TUI | 3 | 0 | 3 |
| PPDS.Mcp | 2 | 0 | 2 |
| VS Code Extension | 2 | 0 | 2 |
| PPDS.Plugins | 2 | 0 | 2 |
| **Total** | **29** | **20** | **9** |
