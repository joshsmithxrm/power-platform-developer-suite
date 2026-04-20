# What's in PPDS v1.0.0

**Power Platform Developer Suite** (PPDS) is a multi-surface developer toolkit for Microsoft Power Platform and Dataverse. One codebase ships as a CLI, TUI, VS Code extension, Model Context Protocol (MCP) server, and a set of reusable .NET libraries — so you can move between your terminal, editor, and AI assistant without giving up a single feature. v1.0.0 is the first public, stable release.

This document is a feature inventory for first-time readers. If you are tracking deltas from the beta releases, each package has its own `CHANGELOG.md`.

---

## Who this is for

- **Power Platform developers** automating solution packaging, plugin registration, data migration, and schema changes.
- **Dataverse administrators** who want a fast SQL-capable query surface across environments with safety guards.
- **CI/CD engineers** building stateless pipelines against Dataverse without long-lived profiles.
- **AI-assisted developers** using Claude Code, Claude Desktop, or other MCP clients to drive Power Platform tasks.

---

## Shipping surfaces

### CLI — `ppds`

Installed via `dotnet tool install -g PPDS.Cli`. Exposes every capability the libraries offer; scriptable, stderr-separated, and JSON-capable.

- **Authentication** — `ppds auth create|list|select|delete|update|name|clear|who` across interactive browser, device code, client secret, certificate file/store, managed identity, GitHub/Azure DevOps federated OIDC, and username/password. Secrets kept in platform-native secure storage (Windows Credential Manager, macOS Keychain, Linux Secret Service).
- **Environment-variable auth for CI/CD** — `PPDS_CLIENT_ID`, `PPDS_CLIENT_SECRET`, `PPDS_TENANT_ID`, `PPDS_ENVIRONMENT_URL` (all four required). Stateless; takes precedence over profiles.
- **Environments** — `ppds env list|select|who|config` with filtering, pre-save WhoAmI validation, label/type/color configuration, `Open in Maker` / `Open in Dynamics` helpers.
- **Data** — `ppds data export|import|copy|analyze|schema|users|load|update|delete|truncate` with bulk APIs, multi-profile connection pooling, CSV auto-mapping, SQL-like filters, alternate-key upsert, dry-run, and safety guards (`--bypass-plugins`, `--bypass-flows`, `--continue-on-error`, row caps, confirmation prompts).
- **Query** — `ppds query fetch|sql|explain` backed by the PPDS.Query engine. Full T-SQL with DML, CTEs, window functions, subqueries, UNION, variables, IF/ELSE. TDS endpoint routing via `--use-tds`. Cross-environment queries via `[env].[entity]` bracket syntax. Output as Text, JSON, or CSV.
- **Metadata browsing** — `ppds metadata entities|entity|attributes|relationships|keys|optionsets|optionset`.
- **Metadata authoring** — `ppds metadata table|column|relationship|choice|key create|update|delete` with validation, dry-run, and solution awareness. `ppds publish entity <name>...` for entity-type publishing.
- **Plugins** — `ppds plugins extract|deploy|diff|list|clean|register|unregister|update|download|get`. Attribute-driven registration via `PPDS.Plugins` attributes; `MetadataLoadContext` reflection (no assembly load); NuGet plugin-package support.
- **Custom APIs and Data Providers** — `ppds custom-apis` and `ppds data-providers` for full lifecycle management, including virtual-entity providers.
- **Solutions / Import Jobs / Environment Variables / Web Resources** — Dedicated command groups with Maker Portal URL helpers.
- **Users and Roles** — `ppds users` and `ppds roles` for user management and role assignment.
- **Flows, Connections, Connection References** — Including orphan detection via `ppds connectionreferences analyze`.
- **Deployment Settings** — `ppds deployment-settings generate|sync|validate` (PAC-compatible).
- **Global options** — `--quiet`, `--verbose`, `--debug`, `--correlation-id`, `--output-format Text|Json`, `--environment` override.
- **Self-update** — `ppds version --check` and `ppds update` query NuGet and install the latest.

### TUI

Shipped inside the CLI binary — run `ppds` with no arguments, `ppds interactive`, or `ppds -i`.

- Profile and environment selector with color theming and live search.
- SQL query editor with syntax highlighting, autocomplete, inline validation, resizable split, multi-tab architecture, query history, cross-environment banner, `Escape` cancels, elapsed-time spinner.
- Full screens: Solutions, Metadata Browser, Plugin Traces (with timeline waterfall), Web Resources, Connection References, Environment Variables, Migration, Import Jobs, Environment Selector.
- Session persistence — filter selections saved per environment per screen.
- TDS-endpoint toggle (`Ctrl+T`), F7/F8/F9 Linux terminal bindings.

### VS Code Extension

Published to the VS Code Marketplace stable channel. Thin UI layer that delegates all operations to the `ppds serve` daemon via JSON-RPC; authentication is managed through the CLI profile store.

- **Profile management** — Create, delete, rename, select profiles from the sidebar.
- **Environment management** — Browse and select Dataverse environments with color theming (3-pixel top border by environment type, 4-pixel left border by per-environment color, per-panel persistence); status-bar profile indicator with click-to-switch.
- **Notebooks (`.ppdsnb`)** — SSMS-like SQL + FetchXML experience with IntelliSense, FetchXML syntax highlighting, query history, CSV/JSON export per cell, notebook-environment selection.
- **Data Explorer panel** — Ad-hoc queries with virtual scrolling, three-state column sorting, cell selection, `Ctrl+A`/`Ctrl+C` TSV copy, row striping, cell tooltips.
- **Panels** — Solutions, Plugin Traces, Metadata Browser, Connection References, Environment Variables, Web Resources, Import Jobs, Plugin Registration. All eight panels include `findWidget` and `retainContextWhenHidden`.
- **Shared components** — `SolutionFilter`, `DataTable`, `SelectionManager`, `ListResult<T>` paging metadata.
- **Query hints banner** — Cross-environment banner when multi-profile queries execute.
- **Environment Details command** — Org and connection info for the active environment.

### MCP Server — `ppds-mcp-server`

Installed via `dotnet tool install -g PPDS.Mcp`. Exposes Power Platform capabilities to MCP-compatible AI assistants (Claude Code, Claude Desktop).

- **Query tools** — SQL (via PPDS.Query) and FetchXML execution.
- **Metadata tools** — `ppds_metadata_entities` and entity detail lookup for schema understanding.
- **Metadata authoring tools** — Schema CRUD for tables, columns, relationships, choices, and keys with dry-run support (honors `--read-only`).
- **Plugin tools** — `ppds_plugins_list`, `ppds_plugins_get`, plus assembly/package/type/step/image lookup and `ppds_plugin_traces_delete`.
- **Service endpoints, custom APIs, data providers** — `ppds_service_endpoints_list`, `ppds_custom_apis_list`, `ppds_data_providers_list`.
- **Connection References and Environment Variables** — `ppds_connection_references_list/get/analyze`, `ppds_environment_variables_list/get/set`.
- **Web Resources** — `ppds_web_resources_list/get/publish`.
- **Solutions tools** — `ppds_solutions_list`, `ppds_solutions_components`.
- **Session safety** — `--profile`, `--environment`, `--read-only`, `--allowed-env` for isolation and DML protection.
- **Pagination contract** — All list tools return `ListResult<T>` with `totalCount`, `wasTruncated`, `filtersApplied` — no silent truncation.

### NuGet libraries

All libraries target `net8.0`, `net9.0`, and `net10.0`. Install with `dotnet add package <name>`.

- **`PPDS.Auth`** — Profile-based authentication. Nine credential providers (interactive browser, device code, client secret, certificate file/store, managed identity, GitHub/Azure DevOps federated OIDC, username/password). Platform-native token cache via MSAL. Multi-cloud support (Public, GCC, GCC High, DoD, China, USNat, USSec). `IPowerPlatformTokenProvider` for Power Apps / Power Automate management APIs. `ICredentialProvider` for custom auth.
- **`PPDS.Dataverse`** — High-performance Dataverse connectivity. Multi-connection `IDataverseConnectionPool` with DOP-based parallelism from `RecommendedDegreesOfParallelism`, affinity cookie disabled for throughput, `RoundRobin` / `LeastConnections` / `ThrottleAware` strategies, automatic throttle routing and retry (TVP race, SQL deadlock). Bulk wrappers (`CreateMultiple`, `UpdateMultiple`, `UpsertMultiple`, `DeleteMultiple`) with progress reporting. Services: `IMetadataQueryService`, `IMetadataAuthoringService`, `IWebResourceService`, `IFlowService`, `IConnectionReferenceService`, `IDeploymentSettingsService`, `IPluginTraceService`, `IQueryExecutor`. `ListResult<T>` everywhere (no silent truncation).
- **`PPDS.Query`** — Production-grade SQL query engine. Full T-SQL via ScriptDom, Volcano iterator execution, FetchXML pushdown with hash/merge/nested-loop fallback, parallel partitioned aggregates for accurate `COUNT(*)` beyond the Dataverse 50K limit, TDS endpoint routing, cross-environment queries, `EXPLAIN`, DML safety guards, query hints (`USE_TDS`, `MAXDOP`, `BATCH_SIZE`, `BYPASS_PLUGINS`, etc.), ADO.NET provider (`PpdsDbConnection`, `PpdsDbCommand`, `PpdsDbDataReader`, `PpdsDbProviderFactory`), and FetchXML IntelliSense.
- **`PPDS.Migration`** — Data migration library with parallel export (configurable DOP), page-level parallelism for large entities via GUID range partitioning, tiered import with Tarjan's algorithm for dependency resolution, deferred field processing for circular references, CMT-format compatibility (`schema.xml` + `data.zip`), file column chunked transfer, owner impersonation via `CallerId`, state-transition handlers (10 built-in: SystemUser, Activity, BusinessUnit, Opportunity, Incident, Quote, SalesOrder, Lead, DuplicateRule, Product), date shifting (4 modes), structured warnings and pool-statistics in `summary.json`.
- **`PPDS.Plugins`** — Attribute-driven plugin registration (`PluginStepAttribute`, `PluginImageAttribute`, `CustomApiAttribute`, `CustomApiParameterAttribute`) replacing Plugin Registration Tool rituals. Code-first Custom API definition with enums for binding scope, parameter types, processing step types. Targets `net462` (Dataverse plugin sandbox requirement); strong-name-signed.

---

## Upgrade notes

Non-Windows users with profiles from a pre-release version (0.7.x or earlier) will need to re-authenticate after upgrading to v1.0.0. The pre-release non-Windows encryption path was insecure (XOR) and has been removed; existing credential values tagged `CLEARTEXT:` or `ENCRYPTED:` from the old path will not decrypt. Windows users are unaffected.

To re-authenticate:

```bash
# Remove the stale profile and create a new one
ppds auth delete --name <profile-name>
ppds auth create --name <profile-name>
```

Profiles created on v1.0.0 and later on macOS use Keychain and on Linux use libsecret; the insecure XOR fallback is gone.

---

## Known limitations

These items are explicitly deferred to v1.1+ and are not present in v1.0.0:

- **Code signing** — Extension and .NET tools are not Authenticode-signed. Installation via `dotnet tool install` and VSIX install does not trigger SmartScreen, so there is no standalone EXE distribution in v1.0.0. Marketplace Publisher Verification is tracked for v1.1.
- **Remote telemetry** — No opt-in pipeline in v1.0.0. Bug-reporting relies on local logs (`ppds logs`, `ppds logs dump`) and GitHub issues.
- **RPC DTO source generator** — CI drift check is included in v1.0.0, but the generator itself is deferred to v1.1.
- **Professional logo and marketplace rebrand** — v1.0.0 ships minimum-viable branding. Full rebrand with new logo tracked for v1.1.
- **Extracted TUI helpers (`TuiFilterHelper`, `TuiDataTableBuilder<T>`)** — Not extracted in v1.0.0.
- **RPC trace-mode debug logging** — Limited to Information-level in v1.0.0.

---

## Installation

```bash
# CLI (includes TUI)
dotnet tool install -g PPDS.Cli
ppds --help

# MCP server
dotnet tool install -g PPDS.Mcp

# VS Code extension
# Install from the VS Code Marketplace:
#   ext install JoshSmithXRM.power-platform-developer-suite

# Libraries for your own .NET projects
dotnet add package PPDS.Auth
dotnet add package PPDS.Dataverse
dotnet add package PPDS.Query
dotnet add package PPDS.Migration
dotnet add package PPDS.Plugins
```

### Supported platforms

- **.NET runtime** — .NET 8.0 or later (CLI, MCP, libraries). Plugins: .NET Framework 4.6.2 (Dataverse sandbox requirement).
- **OS** — Windows, macOS, Linux.
- **VS Code** — 1.109 or later.
- **Node.js** — 20 or later (contributors only).

---

## Report an issue

Use GitHub to report bugs or request features:

- **Issues** — https://github.com/joshsmithxrm/power-platform-developer-suite/issues
- **Include in the report** — the output of `ppds --version` (or `ppds version --check`), your OS, and the command or action that triggered the issue. `ppds logs dump` packages logs and environment info as a zip that you can attach.

---

## Further reading

- Per-package release notes — see the `CHANGELOG.md` in each `src/PPDS.*` directory.
- Root changelog index — [`CHANGELOG.md`](../CHANGELOG.md).
- Contributing — [`CONTRIBUTING.md`](../CONTRIBUTING.md).
