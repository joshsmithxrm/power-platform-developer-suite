# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.5.0] - 2026-03-03

Complete ground-up rebuild of the extension. The new architecture uses a thin VS Code UI layer that delegates all operations to the `ppds serve` daemon via JSON-RPC, replacing the previous self-contained approach.

### Added

- Profile management — create, delete, rename, select profiles from the sidebar
- Environment discovery — browse and select Dataverse environments
- Solutions browser — explore solutions with expandable component groups
- Dataverse notebooks (.ppdsnb) — SSMS-like query experience with SQL and FetchXML
- SQL IntelliSense — autocomplete for tables and columns
- FetchXML IntelliSense — autocomplete for elements and attributes
- FetchXML syntax highlighting
- Data Explorer — webview panel for quick ad-hoc queries
- Query history — automatic persistence of executed queries
- Results export — save query results as CSV or JSON
- Environment status bar indicator
- Virtual scrolling for large result sets

### Changed

- Architecture: thin UI layer delegating to `ppds serve` daemon via JSON-RPC (was self-contained with direct Dataverse API calls)
- Build system: esbuild (was webpack)
- Test framework: Vitest + Playwright (was Jest)
- Authentication: managed by CLI profiles (was MSAL in-extension)

### Removed

- Direct Dataverse API calls (now handled by daemon)
- In-extension MSAL authentication (now managed by CLI)
- Plugin Trace viewer (will be re-added in a future release)
- Connection References viewer (will be re-added in a future release)
- Environment Variables viewer (will be re-added in a future release)
- Metadata Browser (will be re-added in a future release)
- Web Resources viewer (will be re-added in a future release)
- Import Job viewer (will be re-added in a future release)

## [0.3.4] - 2026-01-01

_Last stable release of the legacy architecture. See [archived repository](https://github.com/joshsmithxrm/power-platform-developer-suite/tree/archived) for full history._
