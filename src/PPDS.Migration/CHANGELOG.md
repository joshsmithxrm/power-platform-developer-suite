# Changelog - PPDS.Migration

All notable changes to PPDS.Migration will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-beta.1] - 2025-12-29

### Added

- Parallel export with configurable degree of parallelism
- Tiered import with automatic dependency resolution using Tarjan's algorithm
- Circular reference detection with deferred field processing
- CMT format compatibility (schema.xml and data.zip)
- Schema generation from Dataverse metadata
- Metadata-driven field filtering (include custom fields, exclude system fields)
- User mapping generation for cross-environment migrations (match by AAD Object ID or domain)
- Progress reporting with console and JSON output formats
- Security-first design: connection string redaction, no PII in logs
- DI integration via `AddDataverseMigration()` extension method
- Targets: `net8.0`, `net10.0`

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Migration-v1.0.0-beta.1...HEAD
[1.0.0-beta.1]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Migration-v1.0.0-beta.1
