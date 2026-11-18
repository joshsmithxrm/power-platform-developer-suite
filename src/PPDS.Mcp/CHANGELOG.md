# Changelog

All notable changes to the PPDS.Mcp package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.3.0] - Unreleased

### Added

- **Bounded Main-form authoring** - `ppds_forms_list`, `ppds_forms_get`, `ppds_forms_add_field` and `ppds_forms_publish` add inspection, an existing column in an existing section, verified draft saves and explicit table-scoped publication. Stable IDs and a fresh unpublished draft fingerprint support write-free preview and stale-draft preflight; results retain completed and uncertain phases. The catalog grows from 41 to 45 tools (#1215, partial).
- `ppds_forms_add_field` results gain `controlId`: the planned id on a dry run, or the id submitted in the draft write. It is omitted when the field was already in the section or the draft write failed; when the write outcome is unknown or verification failed, the id is unconfirmed (#1601).
- `ppds_metadata_update_choice` returns `validationMessages` (English-only label notices) and, on a dry run, `globalChoiceNameChecked: false`; `ppds_metadata_update_table` and `ppds_metadata_update_column` return `validationMessages`. Their label parameters are described as English (LCID 1033) (#1604).

### Changed

- Captured startup authentication/target defaults. Allowed switches affect future calls in that session without changing global defaults; creating or replacing startup authentication requires a server restart.
- Form writes require `confirmEnvironment` when effective protection requires acknowledgement. `--allowed-env` permits switching but does not authorize writes; `--read-only` rejects covered form mutations. Publication separately requires `confirmPublishEntity`, publishes all pending customizations for that table and verifies only the selected form (#1015, partial).
- Updated to consume CLI 1.5.0 and the coordinated library/runtime dependency refresh, including Microsoft.Extensions.Hosting 10.0.14 (#1501), MSAL 4.92.0 (#1513) and System.Text.Json 10.0.14 (#1565).
- `ppds_metadata_update_table`, `ppds_metadata_create_table` and `ppds_metadata_add_option_value` share the CLI metadata service's earlier return of pooled connections before cache invalidation, option-colour and publication follow-ups, so these calls can complete with one available pooled connection. Separate requests can still complete partially if a later step fails.
- `ppds_metadata_update_column` can change the display name and description of State, Status and unique-identifier (GUID) columns, including a table's primary ID; any other setting on these columns is rejected (#1550, #1559).
- **Compatibility exception for this release only** - Josh approved CLI 1.5.0 and MCP 1.3.0 on 2026-11-01 with explicit protected-write/session migration and custom-embedder signature incompatibilities. This is not a blanket backward-compatibility claim or a permanent version-policy waiver; see the [scoped exception and migration guide](https://github.com/joshsmithxrm/power-platform-developer-suite/blob/main/docs/releases/v1.8.0.md#one-release-compatibility-exception).

### Fixed

- Global-choice creation preserves omitted versus explicit-zero values and returns existing service warnings in additive validationMessages output (#1530). Explicit out-of-range values remain unchanged.
- Metadata option-add reports Connection.Timeout for normalized pool acquisition timeouts. The colour now travels on the insert itself; a failed or cancelled colour read-back afterwards is reported as `WRITE_NOT_CONFIRMED` (#1604). For failures before or during the insert, CLI timeout/cancellation exits remain 4/2; a timeout or cancellation during the colour read-back is `WRITE_NOT_CONFIRMED` (CLI exit 2). Raw service errors outside normalization retain their existing generic fallback.
- `ppds_metadata_update_choice` and `ppds_metadata_add_option_value` accept a global choice name that differs from the stored name only by case, through the shared CLI metadata service. The stored name is looked up only when Dataverse reports the option set as not found; other errors are unchanged. If several stored names match by case the call fails with `INVALID_CONSTRAINT`, as the CLI does. Recognition checks the Dataverse fault code `0x80040217` first and falls back to the English server message; that code is an assumption for this case, and behaviour on a non-English organisation is unverified (#1535, #1604).
- Metadata validation failures from the shared metadata service are reported with their own error code, message and `context.field` instead of the generic `Operation.Internal` (#1604).
- Colour on `ppds_metadata_add_option_value` is now stored; a colour that cannot be confirmed by reading it back returns `WRITE_NOT_CONFIRMED` (#1604).
- `ppds_forms_add_field` gives a repeated placement of a column its own control id (`<column>1`, `<column>2`, …, skipping other columns' logical names) instead of duplicating an existing id. Existing controls are untouched (#1601).
- When `confirmEnvironment` is required because the environment has no saved type, the error now adds that the environment is treated as Production and how to record its type. The error code is unchanged (#1533).
- Auth inspection reports the actual session target after `--environment` overrides and allowed environment switches, matching subsequent operations.
- Captured profile/target snapshots and pool identity prevent external default changes or reused profile names from redirecting in-flight operations.

### Security

- Metadata option-color failures (colour read-back failures now use `WRITE_NOT_CONFIRMED`, #1604) return structured MCP errors whose bounded service-owned messages omit SDK diagnostic text. Synthetic read/write checks cover fake ClientSecret and Bearer details; initial raw-failure fallback and covered cancellation/acquisition behavior remain. No repository-wide or log-privacy guarantee is implied.
- The CLI metadata service's fixed, diagnostic-free messages for covered SDK/catalog read failures also reach MCP `userMessage` text through `ppds_metadata_add_option_value` and `ppds_metadata_update_column` (primary ID labels). Error codes are unchanged.

- Rejected disallowed or malformed environment URLs before direct authenticated resolution. Name-based selection now uses discovery only, then enforces the session allowlist; final switching rechecks the resolved target.

### Limitations

- Draft fingerprints are preflight checks, not atomic concurrency protection; avoid concurrent editors and inspect applied/unknown phases before retrying. General FormXML/layout authoring is outside this increment, and an unmanaged solution does not bypass locked managed properties.

## [1.2.2] - 2026-10-30

### Security

- Refresh the bundled runtime graph with `System.Security.Cryptography.Pkcs` 10.0.13, `System.Security.Cryptography.Xml` 8.0.5, and MessagePack 2.5.307 through StreamJsonRpc 2.26.150.

### Changed

- Consume CLI 1.4.2, Auth 1.1.2, Dataverse 1.3.2, Migration 1.2.2, Query 1.0.2, and Plugins 3.0.1, with Microsoft.Extensions.Hosting 10.0.13. This is runtime and dependency servicing; the MCP tool surface is unchanged.

## [1.2.1] - 2026-09-12

### Changed

- **Runtime packages refreshed for the coordinated patch train** — consumes Auth 1.1.1, Dataverse 1.3.1, Migration 1.2.1, Query 1.0.1, and CLI 1.4.1, carrying `System.Security.Cryptography.Xml` 8.0.4 through the MCP runtime dependency graph ([#1402](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/1402)).

## [1.2.0] - 2026-07-15

### Added

- **`metadata_entity` attributes now include `attributeOf`** — each returned attribute carries the logical name of the attribute it extends when it is an auxiliary (lookup name/yomi) companion, and omits the field for real attributes. This lets agents deterministically distinguish schema plumbing from real, queryable columns without display-name heuristics ([#1368](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1368)).

## [1.1.0] - 2026-07-14

### Added

- **`ppds-mcp-server --version` prints the package version and exits** — previously the flag was documented in the README's bug-report instructions but unhandled: the server started normally and blocked silently waiting for MCP protocol messages on stdin. `--version` anywhere in the argument list now short-circuits startup, prints the MinVer-resolved version to stdout, and exits 0 ([#1273](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1273), [#1324](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1324)).

### Fixed

- **MCP `initialize` handshake now reports the real package version** — `serverInfo.version` previously reported the fixed `AssemblyVersion` (`1.0.0.0`) regardless of the installed package; it now reports the MinVer-stamped package version (e.g. `1.0.2-alpha.0.5`) with the `+<sha>` build-metadata suffix stripped ([#1273](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1273)).

## [1.0.1] - 2026-06-17

### Added

- **Publish requirement surfaced on metadata column updates** — `MetadataUpdateColumnResult` now carries `RequiresPublish` and `PublishHint`, so MCP clients are told when a column change must be published and given the exact `ppds metadata publish <entity>` command ([#1016](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/1016)).

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
- **Session locking and security** — `--profile`, `--environment`, `--read-only`, and `--allowed-env` flags for session isolation and DML protection; metadata authoring tools honor `--read-only` and refuse mutations when set ([#764](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/764)); selecting the already-active environment via `ppds_env_select` is a no-op and does not re-trigger the allowlist check ([#868](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/868)).
- **DI-based architecture** — Injected `ProfileStore` and auth services; all PPDS.Auth authentication methods supported (interactive browser, device code, service principal, etc.).
- **`ListResult<T>` pagination model** — All list tools return `totalCount`, `wasTruncated`, and `filtersApplied` per Constitution I4 (no silent truncation) ([#651](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/651)).
- **Structured MCP error responses** — All tool exceptions now surface `errorCode`, `userMessage`, and `context`, giving MCP clients machine-readable failure details ([#868](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/868)).
- **Configurable log level** — `--log-level` flag and `PPDS_MCP_LOG_LEVEL` environment variable let operators adjust server verbosity without rebuilding ([#868](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/868)).

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Mcp-v1.3.0...HEAD
[1.3.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Mcp-v1.2.2...Mcp-v1.3.0
[1.2.2]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Mcp-v1.2.1...Mcp-v1.2.2
[1.2.1]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Mcp-v1.2.0...Mcp-v1.2.1
[1.2.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Mcp-v1.1.0...Mcp-v1.2.0
[1.1.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Mcp-v1.0.1...Mcp-v1.1.0
[1.0.1]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Mcp-v1.0.0...Mcp-v1.0.1
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Mcp-v1.0.0
