# Changelog - PPDS.Migration

All notable changes to PPDS.Migration and PPDS.Migration.Cli will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - Unreleased

### Added

#### PPDS.Migration (Library)

- Parallel export with configurable degree of parallelism
- Tiered import with automatic dependency resolution using Tarjan's algorithm
- Circular reference detection with deferred field processing
- CMT format compatibility (schema.xml and data.zip)
- Progress reporting with console and JSON output formats
- Security-first design: connection string redaction, no PII in logs
- DI integration via `AddDataverseMigration()` extension method
- Targets: `net8.0`, `net10.0`

#### PPDS.Migration.Cli (CLI Tool)

- Commands: `export`, `import`, `analyze`, `migrate`, `schema generate`, `schema list`, `config list`
- Unified configuration model using same `DataverseOptions` as PPDS.Dataverse SDK
- Standard .NET configuration layering: appsettings.json → User Secrets → Environment variables
- `--secrets-id` global option for cross-process User Secrets sharing
- `--env` option to select named environment from configuration
- JSON progress output for tool integration (`--json` flag)
- Support for bypass options (plugins, flows) during import
- Packaged as .NET global tool (`ppds-migrate`)
- Comprehensive unit test suite
- Targets: `net8.0`, `net10.0`

[1.0.0]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Migration-v1.0.0
