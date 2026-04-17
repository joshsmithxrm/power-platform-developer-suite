# Changelog

All notable changes to the PPDS.Mcp package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-beta.2] - 2026-04-17

### Added

- **Metadata authoring tools** — Schema CRUD across tables, columns, relationships, choices, and alternate keys with dry-run support. ([#764](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/764), [#766](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/766))
- **Plugin registration tools** — `ppds_plugins_get` plus expanded `ppds_plugins_list` with assembly, package, type, step, and image lookup. ([#657](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/657))
- **Service endpoints and custom APIs tools** — `ppds_service_endpoints_list` and `ppds_custom_apis_list` for webhook and API discovery. ([#657](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/657))
- **Data providers tool** — `ppds_data_providers_list` for virtual-entity provider enumeration. ([#657](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/657))
- **Connection References and Environment Variables tools** — `ppds_connection_references_list/get/analyze`, `ppds_environment_variables_list/get/set`. ([#617](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/617))
- **Web Resources tools** — `ppds_web_resources_list/get/publish` for content access and publishing. ([#618](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/618))
- **Plugin Traces delete tool** — `ppds_plugin_traces_delete` with age-based and filter-based cleanup. ([#615](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/615))
- **Solutions and Metadata Browser tools** — `ppds_solutions_list`, `ppds_solutions_components`, `ppds_metadata_entities` for discovery and analysis. ([#610](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/610), [#616](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/616))
- **`ListResult<T>` pagination model** — All list tools now return `totalCount`, `wasTruncated`, and `filtersApplied` per Constitution I4 (no silent truncation). ([#651](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/651))
- **Session locking and security** — Server supports `--profile`, `--environment`, `--read-only`, and `--allowed-env` flags for session isolation and DML protection.

### Changed

- **Tool output schema** — All list tools follow the `ListResult<T>` contract with total counts and truncation indicators. ([#651](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/651))

### Fixed

- **Read-only compliance** — Metadata authoring tools honor the `--read-only` flag and refuse mutations when set. ([#764](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/764))

## [1.0.0-beta.1] - 2026-03-02

### Added

- **MCP server** for exposing Power Platform capabilities to AI assistants (Claude Code, etc.)
- **Distributed as .NET tool** (`ppds-mcp-server`) installable via `dotnet tool install -g PPDS.Mcp`
- **12+ read-only Dataverse tools** for querying, metadata exploration, and analysis
- **SQL query execution** via MCP tools using PPDS.Query engine
- **FetchXML query execution** via MCP tools
- **Entity metadata exploration** for AI-driven schema understanding
- **Plugin registration analysis** and trace log inspection
- **DI-based architecture** with injected ProfileStore and auth services
- **Integration with PPDS.Query engine** (ScriptDom parser, FetchXML generator)
- **All PPDS.Auth authentication methods supported** (interactive browser, device code, service principal, etc.)

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Mcp-v1.0.0-beta.1...HEAD
[1.0.0-beta.1]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Mcp-v1.0.0-beta.1
