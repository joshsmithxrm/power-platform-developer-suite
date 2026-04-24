# Changelog

All notable changes to the PPDS.Mcp package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-04-18

First stable release. Consolidates features developed across `1.0.0-beta.1` and `1.0.0-beta.2`.

### Added

- **MCP server for Power Platform** — Exposes Dataverse capabilities to MCP-compatible AI assistants (Claude Code, Claude Desktop, etc.). Distributed as a .NET tool (`ppds-mcp-server`) installable via `dotnet tool install -g PPDS.Mcp`.
- **Query tools** — SQL query execution via the PPDS.Query engine (ScriptDom parser, FetchXML generator) and FetchXML execution tools.
- **Metadata tools** — `ppds_metadata_entities` and entity metadata exploration for AI-driven schema understanding.
- **Metadata authoring tools** — Schema CRUD across tables, columns, relationships, choices, and alternate keys with dry-run support ([#764](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/764), [#766](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/766)).
- **Plugin registration tools** — `ppds_plugins_get` plus expanded `ppds_plugins_list` with assembly, package, type, step, and image lookup; `ppds_plugin_traces_delete` with age-based and filter-based cleanup ([#615](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/615), [#657](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/657)).
- **Service endpoints, custom APIs, data providers** — `ppds_service_endpoints_list`, `ppds_custom_apis_list`, `ppds_data_providers_list` for webhook, API, and virtual-entity provider discovery ([#657](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/657)).
- **Connection References and Environment Variables tools** — `ppds_connection_references_list/get/analyze`, `ppds_environment_variables_list/get/set` ([#617](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/617)).
- **Web Resources tools** — `ppds_web_resources_list/get/publish` for content access and publishing ([#618](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/618)).
- **Solutions and Metadata Browser tools** — `ppds_solutions_list`, `ppds_solutions_components` for discovery and analysis ([#610](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/610), [#616](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/616)).
- **Session locking and security** — `--profile`, `--environment`, `--read-only`, and `--allowed-env` flags for session isolation and DML protection.
- **DI-based architecture** — Injected `ProfileStore` and auth services; all PPDS.Auth authentication methods supported (interactive browser, device code, service principal, etc.).
- **`ListResult<T>` pagination model** — All list tools return `totalCount`, `wasTruncated`, and `filtersApplied` per Constitution I4 (no silent truncation) ([#651](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/651)).
- **Structured MCP error responses** — All tool exceptions now surface `errorCode`, `userMessage`, and `context`, giving MCP clients machine-readable failure details ([#868](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/868)).
- **Configurable log level** — `--log-level` flag and `PPDS_MCP_LOG_LEVEL` environment variable let operators adjust server verbosity without rebuilding ([#868](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/868)).

### Changed

- **Tool output schema** — All list tools follow the `ListResult<T>` contract with total counts and truncation indicators ([#651](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/651)).

### Fixed

- **Read-only compliance** — Metadata authoring tools honor the `--read-only` flag and refuse mutations when set ([#764](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/764)).
- **`ppds_env_select` self-switch no-op** — Selecting the already-active environment no longer triggers the allowlist check or re-saves the profile ([#868](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/868)).

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Mcp-v1.0.0...HEAD
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Mcp-v1.0.0
