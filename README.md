# Power Platform Developer Suite

[![Build](https://github.com/joshsmithxrm/power-platform-developer-suite/actions/workflows/build.yml/badge.svg)](https://github.com/joshsmithxrm/power-platform-developer-suite/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0%2B-512BD4)](https://dotnet.microsoft.com/)
[![Docs](https://img.shields.io/badge/docs-ppds--docs-blue)](https://joshsmithxrm.github.io/ppds-docs/)

Developer platform for Microsoft Power Platform and Dataverse. PPDS ships a CLI, TUI, [VS Code extension](https://marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite), MCP server, and NuGet libraries — each surface independently consumable, all developed in parallel. Install only what you need.

## v1.0 Highlights

- SQL query engine with an SSMS-like experience, TDS endpoint routing, and DML support
- VS Code extension with profile/environment management, solutions browser, and `.ppdsnb` notebooks
- Interactive TUI with menu-driven workflows for exploration and one-off tasks
- MCP server exposing 20+ Dataverse tools to AI assistants
- Declarative plugin registration via attributes — no Plugin Registration Tool required
- Fast bulk data operations over pooled Dataverse connections

See [docs/whats-new-v1.md](docs/whats-new-v1.md) for the full v1.0 feature inventory.

## Quick Start

```bash
# Install the CLI tool
dotnet tool install -g PPDS.Cli

# Launch interactive TUI
ppds

# Or run commands directly
ppds auth create --name dev
ppds env select --environment "My Environment"
ppds data export --schema schema.xml --output data.zip
```

## Platform Overview

| Component | Type | Install | Requirement |
|-----------|------|---------|-------------|
| **ppds** | CLI + TUI | `dotnet tool install -g PPDS.Cli` | .NET 8.0+ (Windows / macOS / Linux) |
| **VS Code Extension** | IDE Extension | [Marketplace](https://marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite) | VS Code 1.109+ |
| **ppds-mcp-server** | MCP Server | `dotnet tool install -g PPDS.Mcp` | .NET 8.0+ |

### NuGet Libraries

| Package | NuGet | Description |
|---------|-------|-------------|
| **PPDS.Plugins** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Plugins.svg)](https://www.nuget.org/packages/PPDS.Plugins/) | Declarative plugin registration attributes (net462) |
| **PPDS.Dataverse** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Dataverse.svg)](https://www.nuget.org/packages/PPDS.Dataverse/) | High-performance connection pooling and bulk operations |
| **PPDS.Migration** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Migration.svg)](https://www.nuget.org/packages/PPDS.Migration/) | High-performance data migration engine |
| **PPDS.Auth** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Auth.svg)](https://www.nuget.org/packages/PPDS.Auth/) | Authentication profiles and credential management |
| **PPDS.Query** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Query.svg)](https://www.nuget.org/packages/PPDS.Query/) | SQL query engine with FetchXML transpilation and ADO.NET provider |
| **PPDS.Cli** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Cli.svg)](https://www.nuget.org/packages/PPDS.Cli/) | CLI tool with TUI (.NET tool) |
| **PPDS.Mcp** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Mcp.svg)](https://www.nuget.org/packages/PPDS.Mcp/) | MCP server for AI assistants (.NET tool) |

All libraries except PPDS.Plugins target net8.0, net9.0, and net10.0. Per-package documentation lives in each package's README.

## CLI Commands

| Command | Purpose |
|---------|---------|
| `ppds auth` | Authentication profiles (create, list, select, delete, update, who) |
| `ppds env` | Environment discovery and selection (list, select, who) |
| `ppds data` | Data operations (export, import, copy, schema, users, load, truncate) |
| `ppds plugins` | Plugin registration (extract, deploy, diff, list, clean) |
| `ppds metadata` | Schema browsing and authoring (entities, attributes, relationships, keys, optionsets) |
| `ppds query` | Execute queries (fetch, sql, explain, history) |
| `ppds serve` | Run RPC daemon for VS Code extension |

## Related Projects

| Project | Description |
|---------|-------------|
| [ppds-docs](https://joshsmithxrm.github.io/ppds-docs/) | Documentation site ([source](https://github.com/joshsmithxrm/ppds-docs)) |
| [ppds-tools](https://github.com/joshsmithxrm/ppds-tools) | PowerShell deployment module |
| [ppds-alm](https://github.com/joshsmithxrm/ppds-alm) | CI/CD pipeline templates |
| [ppds-demo](https://github.com/joshsmithxrm/ppds-demo) | Reference implementation |
| [Claude Code templates](templates/claude/INSTALL.md) | PPDS integration for Claude Code users |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, build instructions, and guidelines.

## License

PPDS is distributed under the MIT License — see [LICENSE](LICENSE) for the full text.

Third-party components and vendored source (including a subset of `microsoft/git-credential-manager` for credential storage) are attributed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
