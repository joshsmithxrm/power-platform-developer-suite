# Changelog - PPDS.Cli

All notable changes to PPDS.Cli will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Cli-v1.0.0-beta.1...HEAD
[1.0.0-beta.1]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Cli-v1.0.0-beta.1
