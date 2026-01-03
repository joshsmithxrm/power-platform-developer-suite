# Changelog - PPDS.Dataverse

All notable changes to PPDS.Dataverse will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- **Replaced Newtonsoft.Json with System.Text.Json** - Removed external dependency; uses built-in JSON serialization with case-insensitive property matching ([#72](https://github.com/joshsmithxrm/ppds-sdk/issues/72))

## [1.0.0-beta.2] - 2026-01-02

### Fixed

- **Double-checked locking in ConnectionStringSource** - Added `volatile` modifier to `_client` field for correct multi-threaded behavior ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))

### Changed

- Extracted throttle detection logic into `ThrottleDetector` class for cleaner separation of concerns in `PooledClient` ([#82](https://github.com/joshsmithxrm/ppds-sdk/pull/82))
- Improved input validation for `DataverseConnection` name parameter ([#82](https://github.com/joshsmithxrm/ppds-sdk/pull/82))

## [1.0.0-beta.1] - 2025-12-29

### Added

- Multi-connection pool supporting multiple Application Users for load distribution
- DOP-based parallelism using server's `RecommendedDegreesOfParallelism` (`x-ms-dop-hint` header)
- Connection selection strategies: RoundRobin, LeastConnections, ThrottleAware
- Throttle tracking with automatic routing away from throttled connections
- `IConnectionSource` abstraction for custom authentication methods (ADR-0006)
- `ServiceClientSource` for integrating pre-authenticated ServiceClient instances
- `CredentialProviderSource` for integration with PPDS.Auth credential providers
- Bulk operation wrappers: CreateMultiple, UpdateMultiple, UpsertMultiple, DeleteMultiple
- `IProgress<ProgressSnapshot>` support for real-time progress reporting
- Full `appsettings.json` configuration support for all options
- DI integration via `AddDataverseConnectionPool()` extension method
- Affinity cookie disabled by default for improved throughput (ADR-0001)
- TVP race condition retry (SQL error 3732/2812)
- SQL deadlock retry (SQL error 1205)
- Connection validation with background health checks
- Security-first design: connection string redaction, sensitive data attributes
- Targets: `net8.0`, `net10.0`

### Changed

- Removed rate control presets (`Conservative`, `Balanced`, `Aggressive`) in favor of DOP-based parallelism
- Removed adaptive rate control in favor of server-recommended limits

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Dataverse-v1.0.0-beta.2...HEAD
[1.0.0-beta.2]: https://github.com/joshsmithxrm/ppds-sdk/compare/Dataverse-v1.0.0-beta.1...Dataverse-v1.0.0-beta.2
[1.0.0-beta.1]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Dataverse-v1.0.0-beta.1
