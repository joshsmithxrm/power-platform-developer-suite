# Code Coverage Baseline

## Overview

This document records the baseline code coverage for each PPDS package, used to track progress toward the targets defined in [Issue #55](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/55).

## Current Baseline

| Package | Unit Tests | Estimated Coverage | Target | Status | Notes |
|---------|------------|-------------------|--------|--------|-------|
| **PPDS.Plugins** | 77 | ~95% | 95% | ✅ Met | Core attributes fully tested |
| **PPDS.Dataverse** | 351 | ~65% | 60% | ✅ Met | Unit + FakeXrmEasy mocked tests |
| **PPDS.Cli** | 210 + 27 E2E | ~45% | 60% | ⏳ Gap | Unit tests cover parsing; E2E covers auth/env/schema |
| **PPDS.Auth** | 282 | ~65% | 70% | ✅ Near | Profiles, credentials, discovery covered; credential providers in LiveTests |
| **PPDS.Migration** | 200 | ~55% | 50% | ✅ Met | Models, formats, analysis covered; orchestrators need integration |
| **Overall** | 1,147 | ~58% | 60% | ⏳ Near | Significant improvement from Phase 5 |

### Coverage Notes

- **Estimated Coverage**: Percentages are estimates based on test file coverage of source files. Run `dotnet test --collect:"XPlat Code Coverage"` for actual metrics.
- **PPDS.Auth**: Credential providers (ClientSecretCredentialProvider, etc.) are integration-heavy and tested via `PPDS.LiveTests/Authentication/`.
- **PPDS.Migration**: Orchestrators (TieredImporter, ParallelExporter) require Dataverse connections and are not unit-tested.
- **PPDS.Cli**: E2E tests cover auth, env, and schema commands. Data migration commands pending (see [Future Work](#future-work-cli-e2e-expansion)).

## Test Project Summary

| Test Project | Test Count | Scope |
|--------------|------------|-------|
| PPDS.Plugins.Tests | 77 | Attributes, enums |
| PPDS.Dataverse.Tests | 351 | Client, pooling, bulk operations, resilience |
| PPDS.Dataverse.IntegrationTests | ~50 | FakeXrmEasy mocked Dataverse operations |
| PPDS.Cli.Tests | 210 | Command structure, argument parsing |
| PPDS.Auth.Tests | 282 | Profiles, credentials, discovery, cloud |
| PPDS.Migration.Tests | 200 | Models, formats, analysis, import/export |
| PPDS.LiveTests | ~40 + 27 CLI | Live Dataverse + CLI E2E tests |

## Measurement Method

Coverage is collected using:
- **Collector**: `coverlet.collector` via `dotnet test --collect:"XPlat Code Coverage"`
- **Reporter**: [Codecov](https://codecov.io/gh/joshsmithxrm/power-platform-developer-suite)
- **Frameworks**: Merged from net8.0, net9.0, net10.0 test runs

## Targets by Package

From [Issue #55 - Integration Testing Infrastructure](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/55):

| Package | Unit Target | Integration Target | Rationale |
|---------|-------------|-------------------|-----------|
| PPDS.Plugins | 95% | N/A | Core attributes, simple logic |
| PPDS.Auth | 70% | Live auth tests | Complex auth flows; credential providers need real auth |
| PPDS.Dataverse | 60% | 80% mocked | Heavy external dependencies |
| PPDS.Migration | 50% | N/A | Orchestration code; importers/exporters need Dataverse |
| PPDS.Cli | 60% | E2E tests | Command parsing (unit) + execution (E2E) |

## Improvement Plan

### Phase 5 (Complete)
- [x] Create PPDS.Auth.Tests unit test project (282 tests)
- [x] Create PPDS.Migration.Tests unit test project (200 tests)
- [x] Add CLI E2E tests to PPDS.LiveTests (auth, env, schema commands)

### Future Work: CLI E2E Expansion

CLI E2E tests currently cover:
- `auth` commands (list, who, create, delete, select, clear)
- `env` commands (list, who, select)
- `data schema` command

**Not yet covered:**
- `data export` / `data import` / `data copy` / `data analyze` / `data users`
- `plugins deploy` / `plugins diff` / `plugins list` / `plugins extract` / `plugins clean`

**Plan**: Data migration E2E tests require known test data in the environment. These tests will be added when data import functionality is enhanced, following this sequence:

1. Create test data file (accounts for seeding)
2. Import test seeds environment with known data
3. Export test exports that known data
4. Workflow test validates round-trip

See [Issue #104](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/104) for details.

### Future: Coverage Gates
- [ ] Enable patch coverage enforcement (80% for new code)
- [ ] Add project-level coverage gates after baseline improvement

## CLI E2E Test Coverage Matrix

| Command Group | Commands | Unit Tests | E2E Tests | Status |
|---------------|----------|------------|-----------|--------|
| `auth` | create, delete, select, list, who, clear, update, name | ✅ Structure | ✅ 11 tests | Complete |
| `env` | list, select, who | ✅ Structure | ✅ 9 tests | Complete |
| `data schema` | generate | ✅ Structure | ✅ 7 tests | Complete |
| `data export` | export | ✅ Parsing | ❌ Pending | Needs test data |
| `data import` | import | ✅ Parsing | ❌ Pending | Needs test data |
| `data copy` | copy | ✅ Parsing | ❌ Pending | Needs test data |
| `data analyze` | analyze | ✅ Parsing | ❌ Pending | Lower priority |
| `data users` | generate | ✅ Parsing | ❌ Pending | Lower priority |
| `plugins deploy` | deploy | ✅ Parsing | ❌ Pending | Needs config |
| `plugins diff` | diff | ✅ Parsing | ❌ Pending | Lower priority |
| `plugins list` | list | ✅ Parsing | ❌ Pending | Lower priority |
| `plugins extract` | extract | ✅ Parsing | ❌ Pending | Unit tests sufficient |
| `plugins clean` | clean | ✅ Parsing | ❌ Pending | Only --dry-run |

## Codecov Configuration

Coverage thresholds are configured in [`codecov.yml`](../codecov.yml):
- **Project coverage**: Informational (no PR blocking)
- **Patch coverage**: Informational (no PR blocking)
- **Components**: Per-package tracking with individual targets

## References

- [Codecov Dashboard](https://codecov.io/gh/joshsmithxrm/power-platform-developer-suite)
- [Issue #55 - Integration Testing Infrastructure](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/55)
- [Issue #84 - Code Coverage Reporting](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/84)
- [Demo Test Scripts](../demo/scripts/) - PowerShell scripts for manual CLI validation
