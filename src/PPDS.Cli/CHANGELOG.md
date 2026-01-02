# Changelog - PPDS.Cli

All notable changes to PPDS.Cli will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **Environment resolution for service principals** - `ppds env select` now works with full URLs for service principals by trying direct Dataverse connection first, before falling back to Global Discovery (which requires user auth). ([#89](https://github.com/joshsmithxrm/ppds-sdk/issues/89))
- **`auth update --environment` now validates and resolves** - Previously only parsed the URL string without connecting. Now performs full resolution with org metadata population. ([#88](https://github.com/joshsmithxrm/ppds-sdk/issues/88))

### Changed

- **BREAKING: Standardized output format flag** - Replaced `--json` / `-j` with `--output-format` / `-f` enum option (values: `Text`, `Json`) across all commands for consistency ([#73](https://github.com/joshsmithxrm/ppds-sdk/issues/73))
- **BREAKING: Moved schema and users commands to data group** - `ppds schema generate` → `ppds data schema`, `ppds users generate` → `ppds data users` ([#74](https://github.com/joshsmithxrm/ppds-sdk/issues/74))

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
