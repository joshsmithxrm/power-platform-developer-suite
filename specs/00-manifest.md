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
| 2.4 | Environment Discovery | pending | - |
| 2.5 | Cloud Support | pending | - |

### 3. PPDS.Migration

| # | Subsystem | Status | Spec File |
|---|-----------|--------|-----------|
| 3.1 | Dependency Analysis (Tarjan) | pending | - |
| 3.2 | Export Pipeline | pending | - |
| 3.3 | Import Pipeline | pending | - |
| 3.4 | Circular References | pending | - |
| 3.5 | CMT Compatibility | pending | - |
| 3.6 | User Mapping | pending | - |

### 4. PPDS.Cli Services

| # | Subsystem | Status | Spec File |
|---|-----------|--------|-----------|
| 4.1 | Application Services | pending | - |
| 4.2 | Connection Service | pending | - |
| 4.3 | Export Service | pending | - |

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
| PPDS.Auth | 5 | 3 | 2 |
| PPDS.Migration | 6 | 0 | 6 |
| PPDS.Cli Services | 3 | 0 | 3 |
| PPDS.TUI | 3 | 0 | 3 |
| PPDS.Mcp | 2 | 0 | 2 |
| VS Code Extension | 2 | 0 | 2 |
| PPDS.Plugins | 2 | 0 | 2 |
| **Total** | **29** | **9** | **20** |
