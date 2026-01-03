# Changelog - PPDS.Cli

All notable changes to PPDS.Cli will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Structured error handling** - All errors now return hierarchical error codes (`Auth.ProfileNotFound`, `Connection.Failed`, etc.) for reliable programmatic handling ([#77](https://github.com/joshsmithxrm/ppds-sdk/issues/77))
- **Expanded exit codes** - New exit codes 4 (ConnectionError), 5 (AuthError), 6 (NotFoundError) for finer-grained status ([#77](https://github.com/joshsmithxrm/ppds-sdk/issues/77))
- **Global options** - `--quiet`/`-q`, `--verbose`/`-v`, `--debug`, `--correlation-id` flags available on all commands ([#76](https://github.com/joshsmithxrm/ppds-sdk/issues/76))
- **JSON output schema versioning** - JSON output now includes `version: "1.0"` field for future compatibility ([#77](https://github.com/joshsmithxrm/ppds-sdk/issues/77))
- **ADR-0008** - Architecture decision record documenting CLI output architecture (ILogger, IOutputWriter, IProgressReporter separation)

### Changed

- **Progress output to stderr** - Progress messages now write to stderr, enabling clean piping: `ppds data export -f json | jq` ([#76](https://github.com/joshsmithxrm/ppds-sdk/issues/76))
- **Status messages to stderr** - Operational messages (connecting, authenticating, etc.) now write to stderr, keeping stdout for data only ([#76](https://github.com/joshsmithxrm/ppds-sdk/issues/76))

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

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.5...HEAD
[1.0.0-beta.5]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.4...Cli-v1.0.0-beta.5
[1.0.0-beta.4]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.3...Cli-v1.0.0-beta.4
[1.0.0-beta.3]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.2...Cli-v1.0.0-beta.3
[1.0.0-beta.2]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.1...Cli-v1.0.0-beta.2
[1.0.0-beta.1]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Cli-v1.0.0-beta.1
