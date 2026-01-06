# Changelog - PPDS.Cli

All notable changes to PPDS.Cli will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-beta.10] - 2026-01-06

### Added

- **`ppds interactive` command** - Launch interactive TUI mode for profile/environment selection and SQL queries ([#192](https://github.com/joshsmithxrm/ppds-sdk/issues/192)):
  - Entry points: `ppds interactive`, `ppds -i`, `ppds --interactive`
  - Profile and environment selection with Global Discovery integration
  - SQL query wizard with results displayed in interactive table view
  - Arrow key navigation for query results (up/down for rows, left/right for columns)
  - Open record in browser (`O`) or copy URL to clipboard (`C`)
  - Query history with up/down arrow recall
  - Session-scoped connection pooling for faster subsequent queries
  - Graceful degradation when not in a TTY environment
- **Version header at startup** - CLI now outputs diagnostic header to stderr: version info (CLI, SDK, .NET runtime) and platform. Enables correlating issues to specific builds. Skipped for `--help`, `--version`, or no arguments. See [ADR-0022](../../docs/adr/0022_IMPORT_DIAGNOSTICS_ARCHITECTURE.md).
- **`ppds flows` command group** - Manage cloud flows ([#142](https://github.com/joshsmithxrm/ppds-sdk/issues/142)):
  - `ppds flows list` - List cloud flows (supports `--solution`, `--state`)
  - `ppds flows get <name>` - Get flow details by unique name
  - `ppds flows url <name>` - Get Power Automate maker URL for a flow
- **`ppds connections` command group** - List Power Platform connections ([#144](https://github.com/joshsmithxrm/ppds-sdk/issues/144)):
  - `ppds connections list` - List connections from Power Apps Admin API (supports `--connector`)
  - `ppds connections get <id>` - Get connection details by ID
- **`ppds connectionreferences` command group** - Manage connection references with orphan detection ([#143](https://github.com/joshsmithxrm/ppds-sdk/issues/143)):
  - `ppds connectionreferences list` - List connection references (supports `--solution`, `--orphaned`)
  - `ppds connectionreferences get <name>` - Get connection reference details by logical name
  - `ppds connectionreferences flows <name>` - List flows using a connection reference
  - `ppds connectionreferences connections <name>` - Show bound connection details
  - `ppds connectionreferences analyze` - Analyze flow-to-connection-reference relationships with orphan detection
- **`ppds deployment-settings` command group** - Generate and sync deployment settings files ([#145](https://github.com/joshsmithxrm/ppds-sdk/issues/145)):
  - `ppds deployment-settings generate` - Generate deployment settings file from current environment
  - `ppds deployment-settings sync` - Sync existing file with solution (preserves values, adds new entries, removes stale)
  - `ppds deployment-settings validate` - Validate deployment settings file against solution
- **`ppds solutions` command group** - Manage Power Platform solutions ([#137](https://github.com/joshsmithxrm/ppds-sdk/issues/137)):
  - `ppds solutions list` - List solutions in environment (supports `--include-managed`, `--filter`)
  - `ppds solutions get <name>` - Get solution details by unique name
  - `ppds solutions export <name>` - Export solution as ZIP file (supports `--managed`)
  - `ppds solutions import <file>` - Import solution ZIP file (supports `--overwrite`, `--publish-workflows`)
  - `ppds solutions components <name>` - List solution components (supports `--type` filtering)
  - `ppds solutions publish` - Publish all customizations
  - `ppds solutions url <name>` - Get Maker portal URL for a solution
- **`ppds importjobs` command group** - Monitor solution import jobs ([#138](https://github.com/joshsmithxrm/ppds-sdk/issues/138)):
  - `ppds importjobs list` - List recent import jobs (supports `--solution`, `--top`)
  - `ppds importjobs get <id>` - Get import job details
  - `ppds importjobs data <id>` - Get raw XML data for an import job
  - `ppds importjobs wait <id>` - Wait for import job to complete (supports `--timeout`, `--interval`)
  - `ppds importjobs url <id>` - Get Maker portal URL for an import job
- **`ppds environmentvariables` command group** - Manage environment variables ([#139](https://github.com/joshsmithxrm/ppds-sdk/issues/139)):
  - `ppds environmentvariables list` - List environment variables (supports `--solution`)
  - `ppds environmentvariables get <name>` - Get environment variable details by schema name
  - `ppds environmentvariables set <name> <value>` - Set environment variable value
  - `ppds environmentvariables export` - Export environment variables for deployment settings (supports `--solution`, `--output`)
  - `ppds environmentvariables url <name>` - Get Maker portal URL for an environment variable
- **`ppds users` command group** - Manage system users ([#119](https://github.com/joshsmithxrm/ppds-sdk/issues/119)):
  - `ppds users list` - List users (supports `--filter`, `--include-disabled`, `--top`)
  - `ppds users show <user>` - Show user details (by GUID or domain name)
  - `ppds users roles <user>` - List roles assigned to a user
- **`ppds roles` command group** - Manage security roles ([#119](https://github.com/joshsmithxrm/ppds-sdk/issues/119)):
  - `ppds roles list` - List security roles (supports `--filter`)
  - `ppds roles show <role>` - Show role details and assigned users (by GUID or name)
  - `ppds roles assign <role> --user <user>` - Assign a role to a user
  - `ppds roles remove <role> --user <user>` - Remove a role from a user
- **`ppds data truncate` command** - Delete ALL records from an entity for dev/test scenarios. See [ADR-0021](../../docs/adr/0021_TRUNCATE_COMMAND.md).
  - Required confirmation prompt (type `TRUNCATE <entity> <count>` to proceed)
  - `--dry-run` to preview record count without deleting
  - `--force` to skip confirmation for automation/CI
  - `--batch-size` to control delete batch size (default 1000, max 1000)
  - Safety features: `--bypass-plugins`, `--bypass-flows`, `--continue-on-error`
  - Progress reporting with deletion rate

### Fixed

- **`auth create -env` failed to resolve environment** - Creating profiles with InteractiveBrowser or DeviceCode auth and specifying `-env <url>` would fail with "Auth method '' is not supported" because the auth method wasn't passed to Global Discovery Service.

## [1.0.0-beta.9] - 2026-01-05

### Changed

- **Updated PPDS.Migration to v1.0.0-beta.4** - Includes CMT export compatibility fixes: boolean values now export as `True`/`False`, schema preserves relationships section, M2M export shows progress counts. ([#181](https://github.com/joshsmithxrm/ppds-sdk/issues/181), [#182](https://github.com/joshsmithxrm/ppds-sdk/issues/182), [#184](https://github.com/joshsmithxrm/ppds-sdk/issues/184))

## [1.0.0-beta.8] - 2026-01-05

### Added

- **`ppds data update` command** - Update records in Dataverse entities ([#135](https://github.com/joshsmithxrm/ppds-sdk/issues/135))
  - Single record update by GUID (`--id`) or alternate key (`--key`) with `--set field=value`
  - Bulk update from CSV file (`--file`) containing IDs and values
  - Query-based update with SQL-like filter (`--filter`)
  - `--set` option supports multiple fields: `--set "name=New Name,description=Updated"`
  - Automatic type coercion for strings, numbers, booleans, dates, GUIDs
  - Safety features: confirmation prompt, `--force` for automation, `--dry-run` for preview, `--limit` to cap updates
  - Supports `--bypass-plugins`, `--bypass-flows`, `--continue-on-error`
  - Column mapping via `--mapping` for CSV files
- **`ppds data delete` command** - Delete records from Dataverse entities ([#135](https://github.com/joshsmithxrm/ppds-sdk/issues/135))
  - Single record delete by GUID (`--id`) or alternate key (`--key`)
  - Bulk delete from CSV/JSON file (`--file`)
  - Query-based delete with SQL-like filter (`--filter`)
  - Safety features: confirmation prompt (type count to confirm), `--force` for automation, `--dry-run` for preview, `--limit` to cap deletions
  - Supports `--bypass-plugins`, `--bypass-flows`, `--continue-on-error`
  - Progress reporting for bulk operations
- **`ppds docs` command** - Opens CLI documentation in browser ([#165](https://github.com/joshsmithxrm/ppds-sdk/issues/165))
- **Documentation URL in help** - `ppds --help` now shows documentation URL ([#165](https://github.com/joshsmithxrm/ppds-sdk/issues/165))

### Changed

- **Improved `--filter` UX for metadata commands** - Filter without wildcards now performs contains search instead of exact match. `--filter zipcode` matches `ppds_zipcode`, `zipcode`, `zipcode_lookup`. Use wildcards for explicit patterns: `foo*` (starts with), `*foo` (ends with). ([#167](https://github.com/joshsmithxrm/ppds-sdk/issues/167))

## [1.0.0-beta.7] - 2026-01-04

### Added

- **`ppds query` command group** - Execute FetchXML and SQL queries against Dataverse ([#52](https://github.com/joshsmithxrm/ppds-sdk/issues/52)):
  - `ppds query fetch` - Execute FetchXML queries (from argument, file, or stdin)
  - `ppds query sql` - Execute SQL queries (transpiled to FetchXML)
  - Full SQL parser with support for SELECT, WHERE (all operators), JOINs, ORDER BY, GROUP BY, aggregates (COUNT, SUM, AVG, MIN, MAX)
  - Paging support with cookies for large result sets
  - `--show-fetchxml` option to preview transpiled SQL
  - Output formats: Text (table), JSON, and CSV (`-f csv > file.csv`)
  - RPC methods `query/fetch` and `query/sql` for daemon mode
- **`ppds metadata` command group** - Browse Dataverse entity metadata without exporting data ([#51](https://github.com/joshsmithxrm/ppds-sdk/issues/51)):
  - `ppds metadata entities` - List all entities (supports `--custom-only`, `--filter` for wildcard matching)
  - `ppds metadata entity <name>` - Get full entity details (supports `--include` for specific sections)
  - `ppds metadata attributes <entity>` - List attributes (supports `--type` filtering by Lookup, String, etc.)
  - `ppds metadata relationships <entity>` - List 1:N, N:1, N:N relationships (supports `--type` filtering)
  - `ppds metadata keys <entity>` - List alternate keys for an entity
  - `ppds metadata optionsets` - List global option sets (supports `--filter`)
  - `ppds metadata optionset <name>` - Get option set values and metadata
- **`ppds data load` command** - Load CSV data into Dataverse entities with auto-mapping, type coercion, lookup resolution, and bulk upsert operations ([#36](https://github.com/joshsmithxrm/ppds-sdk/issues/36))
  - Auto-maps CSV headers to entity attributes by name matching
  - `--generate-mapping` generates a mapping template with auto-matched columns and optionset values
  - Supports GUID auto-detection for lookups; field-based matching via mapping file
  - Type coercion for all Dataverse attribute types (strings, numbers, dates, booleans, optionsets, money, lookups)
  - Multi-profile support for connection pooling (`--profile app1,app2,app3`)
  - `--dry-run` mode for validation without writing
  - `--key` option for alternate key upsert semantics
  - JSON Schema for mapping files at `schemas/csv-mapping.schema.json`
- **Structured error handling** - All errors now return hierarchical error codes (`Auth.ProfileNotFound`, `Connection.Failed`, etc.) for reliable programmatic handling ([#77](https://github.com/joshsmithxrm/ppds-sdk/issues/77))
- **Expanded exit codes** - New exit codes 4 (ConnectionError), 5 (AuthError), 6 (NotFoundError) for finer-grained status ([#77](https://github.com/joshsmithxrm/ppds-sdk/issues/77))
- **Global options** - `--quiet`/`-q`, `--verbose`/`-v`, `--debug`, `--correlation-id` flags available on all commands ([#76](https://github.com/joshsmithxrm/ppds-sdk/issues/76))
- **JSON output schema versioning** - JSON output now includes `version: "1.0"` field for future compatibility ([#77](https://github.com/joshsmithxrm/ppds-sdk/issues/77))
- **ADR-0008** - Architecture decision record documenting CLI output architecture (ILogger, IOutputWriter, IProgressReporter separation)

### Changed

- **BREAKING: Renamed `--what-if` to `--dry-run`** - `ppds plugins deploy` and `ppds plugins clean` now use `--dry-run` for preview mode, aligning with Unix CLI conventions. PowerShell module (PPDS.Tools) will use `-WhatIf`. ([ADR-0013](docs/adr/0013_CLI_DRY_RUN_CONVENTION.md))
- **Progress output to stderr** - Progress messages now write to stderr, enabling clean piping: `ppds data export -f json | jq` ([#76](https://github.com/joshsmithxrm/ppds-sdk/issues/76))
- **Status messages to stderr** - Operational messages (connecting, authenticating, etc.) now write to stderr, keeping stdout for data only ([#76](https://github.com/joshsmithxrm/ppds-sdk/issues/76))
- **Centralized CSV column matching** - Extracted shared column-to-attribute matching logic to `ColumnMatcher` class for code reuse between data loading and mapping generation
- **Centralized schema versioning** - Schema version constants centralized in `CsvMappingSchema` class (single source of truth)
- **ADR-0014** - Architecture decision record documenting CSV mapping schema versioning and underscore prefix convention

## [1.0.0-beta.6] - 2026-01-03

### Added

- **Self-contained binary publishing** - CLI can now be published as self-contained single-file executables for Windows, macOS, and Linux (x64 and ARM64) ([#53](https://github.com/joshsmithxrm/ppds-sdk/issues/53))
- **Automated release workflow** - GitHub Actions workflow builds and publishes platform binaries with SHA256 checksums to GitHub Releases on `Cli-v*` tags ([#54](https://github.com/joshsmithxrm/ppds-sdk/issues/54))
- **`auth create --accept-cleartext-caching` option** - Linux-only flag to allow cleartext credential storage when libsecret is unavailable. Displays warning when cleartext storage is used. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **`env list --filter` option** - Filter environments by name, URL, or ID (case-insensitive). Aliases: `-fl`. ([#92](https://github.com/joshsmithxrm/ppds-sdk/issues/92))
- **`env who --environment` option** - Query a specific environment without changing the saved default. Supports ID, URL, unique name, or partial name. Aliases: `-env`. ([#93](https://github.com/joshsmithxrm/ppds-sdk/issues/93))

### Changed

- **BREAKING: Profile storage schema v2** - Profiles now use schema v2 with array storage and name-based active profile. Existing v1 profiles are automatically deleted on first load. Secrets stored in platform-native secure storage. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **`auth who` output ordering** - Reordered output fields to match PAC CLI format. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **PluginRegistrationService refactored to use early-bound entities** - Replaced all magic string attribute access with strongly-typed `PPDS.Dataverse.Generated` classes (`PluginAssembly`, `PluginPackage`, `PluginType`, `SdkMessageProcessingStep`, `SdkMessageProcessingStepImage`, `SdkMessage`, `SdkMessageFilter`, `SystemUser`). Provides compile-time type safety and IntelliSense for all Dataverse entity operations. ([#56](https://github.com/joshsmithxrm/ppds-sdk/issues/56))
- **`PluginRegistrationService` now requires logger** - Constructor now requires `ILogger<PluginRegistrationService>` for diagnostic output. ([#61](https://github.com/joshsmithxrm/ppds-sdk/issues/61))
- **BREAKING: Standardized output format flag** - Replaced `--json` / `-j` with `--output-format` / `-f` enum option (values: `Text`, `Json`) across all commands for consistency ([#73](https://github.com/joshsmithxrm/ppds-sdk/issues/73))
- **BREAKING: Moved schema and users commands to data group** - `ppds schema generate` → `ppds data schema`, `ppds users generate` → `ppds data users` ([#74](https://github.com/joshsmithxrm/ppds-sdk/issues/74))

### Fixed

- **Improved exception handling in `GetComponentTypeAsync`** - Replaced generic catch clause with specific `FaultException<OrganizationServiceFault>` and `FaultException` handlers. Logs failures at Debug level for troubleshooting while maintaining graceful degradation behavior. ([#61](https://github.com/joshsmithxrm/ppds-sdk/issues/61))
- **Environment resolution for service principals** - `ppds env select` now works with full URLs for service principals by trying direct Dataverse connection first, before falling back to Global Discovery (which requires user auth). ([#89](https://github.com/joshsmithxrm/ppds-sdk/issues/89))
- **`auth update --environment` now validates and resolves** - Previously only parsed the URL string without connecting. Now performs full resolution with org metadata population. ([#88](https://github.com/joshsmithxrm/ppds-sdk/issues/88))
- **`env select` validates connection before saving** - Now performs actual WhoAmI request to verify user has access before saving environment selection. Previously resolved metadata but didn't validate access. ([#91](https://github.com/joshsmithxrm/ppds-sdk/issues/91))
- **`auth clear` now clears all caches** - Clears MSAL token caches and secure credential store in addition to profile data. ([#90](https://github.com/joshsmithxrm/ppds-sdk/issues/90), [#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **`auth delete` now removes stored credentials** - Deleting a profile also removes associated credentials from secure storage. Credentials shared by multiple profiles are preserved until all profiles using them are deleted. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **`auth create` cleans up credentials on failure** - If authentication fails after credentials are stored, they are now cleaned up to prevent orphaned credentials in secure storage. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **`auth who` DisplayName null check** - Added null/empty check for Organization Friendly Name in `auth who` output for consistency with other environment fields. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **`auth who` shows expired token warning** - Token expiry line now shows `(EXPIRED)` in yellow when token is past expiry. JSON output includes `tokenStatus` field with values `"valid"` or `"expired"`. ([#94](https://github.com/joshsmithxrm/ppds-sdk/issues/94))
- **Missing Environment ID for service principal profiles** - `ppds auth create` with service principal now populates `EnvironmentId` from `ServiceClient.EnvironmentId`. ([#101](https://github.com/joshsmithxrm/ppds-sdk/issues/101))
- **Interactive auth fails with RefreshInstanceDetails error** - Fixed regression from [#98](https://github.com/joshsmithxrm/ppds-sdk/pull/98) where org metadata discovery failed when connecting to globaldisco.crm.dynamics.com (the discovery service, not an actual org). Now skips eager property access for discovery URLs.
- **`auth create --environment` validates access before saving** - For interactive auth with `--environment`, now validates actual connection to the resolved environment before saving the profile. Previously resolved via Global Discovery but didn't verify access.
- **Profile name validation** - Profile names for `auth create`, `auth update`, and `auth name` now enforce character restrictions: must start with letter/number and contain only letters, numbers, spaces, hyphens, or underscores.

### Removed

- **BREAKING: Removed `ppds schema list` command** - Entity listing functionality removed as part of command restructuring ([#74](https://github.com/joshsmithxrm/ppds-sdk/issues/74))

## [1.0.0-beta.5] - 2026-01-02

### Fixed

- **Thread-safety in PluginRegistrationService** - Changed entity type code cache to `ConcurrentDictionary` ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))

### Added

- `PluginRegistrationConfig.Validate()` method with `executionOrder` range checking (1-999999) - called before plugin deployment ([#82](https://github.com/joshsmithxrm/ppds-sdk/pull/82))

### Changed

- Extracted `ParseBypassPlugins` method to `DataCommandGroup` to eliminate duplication ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))
- Extracted constants in `PluginRegistrationService` (`ComponentTypePluginAssembly`, `StagePreValidation`, etc.) ([#82](https://github.com/joshsmithxrm/ppds-sdk/pull/82))
- Removed manual `Ctrl+C` handler - relies on `System.CommandLine` built-in cancellation ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))

## [1.0.0-beta.4] - 2026-01-01

### Fixed

- NuGet plugin package deployment now works correctly ([#62](https://github.com/joshsmithxrm/ppds-sdk/issues/62))
  - Package ID is read directly from `.nuspec` inside the nupkg (authoritative source)
  - Dataverse extracts `uniquename` from package content automatically - CLI no longer sets it
  - Solution association works on both create and update via `SolutionUniqueName` request parameter
  - Removed unnecessary publisher prefix querying logic

## [1.0.0-beta.3] - 2026-01-01

### Added

- `entityAlias` field in plugin image configuration (defaults to `name` if not specified)
- `unsecureConfiguration` field for plugin step configuration (renamed from `configuration` for clarity)
- `MainOperation` stage (30) support for Custom API plugin steps
- JSON schema for `plugin-registration.json` at `schemas/plugin-registration.schema.json`
- Zulu time format (`Z` suffix) for `generatedAt` timestamps in extracted config

### Fixed

- Solution component addition now works on UPDATE path (was only adding on CREATE) ([#59](https://github.com/joshsmithxrm/ppds-sdk/issues/59))
- Cross-platform path normalization uses forward slashes consistently
- Runtime ETC lookup for `pluginpackage` entity type code

## [1.0.0-beta.2] - 2025-12-30

### Added

- `ppds plugins` command group for plugin registration management:
  - `ppds plugins extract` - Extract `[PluginStep]`/`[PluginImage]` attributes from assembly (.dll) or NuGet package (.nupkg) to registrations.json
  - `ppds plugins deploy` - Deploy plugin registrations to Dataverse environment
  - `ppds plugins diff` - Compare configuration against environment state, detect drift
  - `ppds plugins list` - List registered plugins in environment
  - `ppds plugins clean` - Remove orphaned registrations not in configuration
- Plugin deployment options:
  - `--solution` to add components to a solution
  - `--clean` to remove orphaned steps during deployment
  - `--what-if` to preview changes without applying
- Full step registration field support:
  - `deployment` - ServerOnly (default), Offline, or Both
  - `runAsUser` - CallingUser (default) or systemuser GUID
  - `description` - Step documentation
  - `asyncAutoDelete` - Auto-delete async jobs on success
- Extract command enhancements:
  - `--solution` option to set solution on initial extract
  - `--force` option to skip merge and overwrite
  - Merge behavior: re-running extract preserves deployment settings from existing file
  - `[JsonExtensionData]` on all config models for forward compatibility
- List command enhancements:
  - `--package` filter to list specific packages
  - Full package hierarchy output: Package → Assembly → Type → Step → Image
  - Summary includes types and images with proper pluralization
  - Shows non-default step options (deployment, runAsUser, asyncAutoDelete)
- Uses `MetadataLoadContext` for safe, read-only assembly reflection
- JSON output for all plugin commands (`--json` flag)
- Supports both classic assemblies and NuGet plugin packages
- Connection pooling for plugin commands (improved performance)

## [1.0.0-beta.1] - 2025-12-29

### Added

- Unified CLI (`ppds`) replacing the former `ppds-migrate` tool
- Profile-based authentication using PPDS.Auth stored profiles
- Command structure:
  - `ppds auth` - Authentication profile management (create, list, select, delete, update, name, clear, who)
  - `ppds env` - Environment discovery and selection (list, select, who)
  - `ppds org` - Alias for `ppds env`
  - `ppds data` - Data operations (export, import, copy, analyze)
  - `ppds schema` - Schema generation (generate, list with entity detail view)
  - `ppds users` - User mapping for cross-environment migrations (generate)
- Multi-profile pooling for high-throughput scenarios (`--profile app1,app2,app3`)
- Support for all PPDS.Auth authentication methods via `ppds auth create`
- JSON output for all commands (`--json` flag)
- Verbose and debug logging options
- Environment override on data commands (`--environment`)
- Import options: bypass plugins, bypass flows, user mapping, strip owner fields, skip missing columns
- System.CommandLine 2.0.1 stable
- Packaged as .NET global tool (`ppds`)
- Targets: `net10.0`

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.10...HEAD
[1.0.0-beta.10]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.9...Cli-v1.0.0-beta.10
[1.0.0-beta.9]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.8...Cli-v1.0.0-beta.9
[1.0.0-beta.8]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.7...Cli-v1.0.0-beta.8
[1.0.0-beta.7]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.6...Cli-v1.0.0-beta.7
[1.0.0-beta.6]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.5...Cli-v1.0.0-beta.6
[1.0.0-beta.5]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.4...Cli-v1.0.0-beta.5
[1.0.0-beta.4]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.3...Cli-v1.0.0-beta.4
[1.0.0-beta.3]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.2...Cli-v1.0.0-beta.3
[1.0.0-beta.2]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.1...Cli-v1.0.0-beta.2
[1.0.0-beta.1]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Cli-v1.0.0-beta.1
