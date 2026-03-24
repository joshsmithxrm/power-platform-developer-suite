# Release Preparation Design - March 2026

## Context

531 commits since the last release (January 14, 2026). Two new packages (PPDS.Query, PPDS.Mcp) have never been published. Major features: Query Engine v3, TUI overhaul, SQL IntelliSense, cross-environment queries, DML support.

## Packages to Release

| Package | Current Version | New Version | Commits | Summary |
|---------|----------------|-------------|---------|---------|
| PPDS.Query | (new) | Query-v1.0.0-beta.1 | 109 | Full SQL query engine - first release |
| PPDS.Cli | Cli-v1.0.0-beta.12 | Cli-v1.0.0-beta.13 | 137 | TUI overhaul, IntelliSense, DML, env config |
| PPDS.Dataverse | Dataverse-v1.0.0-beta.5 | Dataverse-v1.0.0-beta.6 | 93 | Query engine nodes, metadata caching, adaptive threading |
| PPDS.Auth | Auth-v1.0.0-beta.6 | Auth-v1.0.0-beta.7 | 22 | EnvironmentConfig, DI registration, safety settings |
| PPDS.Mcp | (new) | Mcp-v1.0.0-beta.1 | 11 | MCP server - first release |
| PPDS.Migration | Migration-v1.0.0-beta.6 | Migration-v1.0.0-beta.7 | 4 | Dependency updates only |
| PPDS.Plugins | Plugins-v2.0.0 | Skip | 4 | No functional changes |
| PPDS.Analyzers | v1.0.0 | Skip (internal) | - | Internal dev dependency |

## Work Items

### 1. Merge Dependabot PRs
- PR #554: @types/node 25.3.2 → 25.3.3 (extension)
- PR #555: minimatch 3.1.2 → 3.1.5 (extension, security)

### 2. Infrastructure: Update publish-nuget.yml
- Add `Query-v*` tag trigger and project mapping
- Add `Mcp-v*` tag trigger and project mapping

### 3. New Package: PPDS.Query
- Create `src/PPDS.Query/CHANGELOG.md` with v1.0.0-beta.1 release notes
- Create `src/PPDS.Query/README.md`

### 4. New Package: PPDS.Mcp
- Create `src/PPDS.Mcp/CHANGELOG.md` with v1.0.0-beta.1 release notes
- Create `src/PPDS.Mcp/README.md`

### 5. Changelog Updates: PPDS.Dataverse
Populate [Unreleased] → v1.0.0-beta.6 with:
- Query Engine v2 execution plan layer (Volcano iterator model)
- SQL parser extensions (DML, UNION, subqueries, window functions, variables, IF/ELSE, CASE/WHEN)
- Expression evaluator for client-side computation
- Parallel partitioned aggregates (accurate COUNT(*) beyond 50K)
- Adaptive aggregate retry with binary date-range splitting
- TDS Endpoint routing
- Prefetch scan node for page-ahead buffering
- Metadata query system
- Cached metadata provider (TTL-based for IntelliSense)
- DML safety guard
- Child record paging boundary detection for linked entities
- Adaptive thread management for 429 backoff

### 6. Changelog Updates: PPDS.Cli
Finalize [Unreleased] → v1.0.0-beta.13 with additions:
- Multi-tab architecture with query tabs
- Environment config commands (`ppds env config`, `ppds env type`)
- EnvironmentConfigDialog (label, type, color configuration)
- Environment-colored tabs with user-configurable themes
- DeviceCodeDialog with selectable code and auto-close
- TDS Endpoint toggle (Ctrl+T)
- F7/F8/F9 keybindings for Linux terminal compatibility
- Escape key to cancel running queries
- DML safety wiring with environment-specific settings
- Cross-environment query support wired to CLI
- Certificate Store and Username/Password auth in TUI
- DI refactoring (AuthServices, EnvironmentConfigStore, ProfileStore injection)
- Many TUI fixes (timer leaks, event leaks, splitter drag, cursor visibility, menu flicker)

### 7. Changelog Updates: PPDS.Auth
Populate [Unreleased] → v1.0.0-beta.7 with:
- EnvironmentConfig models and EnvironmentConfigStore
- AddAuthServices DI registration (ProfileStore, EnvironmentConfigStore, NativeCredentialStore)
- EnvironmentType enum (moved from CLI to Auth)
- QuerySafetySettings in EnvironmentConfig
- ProtectionLevel for environment protection enforcement
- Cross-environment DML policy enforcement
- Token cache scope fix for browser auth profile creation
- Disposal guards, typed catches, interface usage improvements

### 8. Changelog Updates: PPDS.Migration
Populate [Unreleased] → v1.0.0-beta.7 with:
- Dependency update: Microsoft.Data.SqlClient 5.2.2 → 6.1.4

### 9. Root CHANGELOG.md Index
- Add PPDS.Query entry with link and description
- Add PPDS.Mcp entry with link and description
- Add Query-v and Mcp-v tag format rows to versioning table

### 10. Root README.md
- Add PPDS.Query and PPDS.Mcp to package listing

## Dependency Order

Packages must be released in dependency order:
1. PPDS.Auth (no PPDS deps changing)
2. PPDS.Dataverse (depends on Auth)
3. PPDS.Query (depends on Dataverse)
4. PPDS.Migration (depends on Dataverse)
5. PPDS.Mcp (depends on Auth, Dataverse, Query, Migration)
6. PPDS.Cli (depends on all above)

Since these are NuGet packages with project references (not package references within the repo), the tags can be pushed in any order — MinVer determines version from tags, and the publish workflow builds the full solution.

## Separate Repo: ppds-docs
Major documentation gaps exist (separate work item for parallel session):
- New pages: PPDS.Query SDK reference, PPDS.Mcp guide, SQL Query guide, TUI guide
- Updates: CLI reference, SDK reference, Getting Started
- Fill placeholders: Data Migration, Plugin Deployment, Architecture
- Release blog post
