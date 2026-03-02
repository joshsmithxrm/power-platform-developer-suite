# Changelog

All notable changes to the PPDS.Mcp package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
