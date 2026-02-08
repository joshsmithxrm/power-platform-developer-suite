# Power Platform Developer Suite

[![Build](https://github.com/joshsmithxrm/power-platform-developer-suite/actions/workflows/build.yml/badge.svg)](https://github.com/joshsmithxrm/power-platform-developer-suite/actions/workflows/build.yml)
[![codecov](https://codecov.io/gh/joshsmithxrm/power-platform-developer-suite/graph/badge.svg)](https://codecov.io/gh/joshsmithxrm/power-platform-developer-suite)
[![Docs](https://img.shields.io/badge/docs-ppds--docs-blue)](https://joshsmithxrm.github.io/ppds-docs/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0%2B-512BD4)](https://dotnet.microsoft.com/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](CONTRIBUTING.md)

Pro-grade tooling for Power Platform developers. CLI, TUI, MCP server, VS Code extension, and NuGet libraries.

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

| Component | Type | Install |
|-----------|------|---------|
| **ppds** | CLI + TUI | `dotnet tool install -g PPDS.Cli` |
| **ppds-mcp-server** | MCP Server | `dotnet tool install -g PPDS.Mcp` |
| **VS Code Extension** | IDE Extension | [Marketplace](https://marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite) |

### NuGet Libraries

| Package | NuGet | Description |
|---------|-------|-------------|
| **PPDS.Plugins** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Plugins.svg)](https://www.nuget.org/packages/PPDS.Plugins/) | Declarative plugin registration attributes |
| **PPDS.Dataverse** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Dataverse.svg)](https://www.nuget.org/packages/PPDS.Dataverse/) | High-performance connection pooling and bulk operations |
| **PPDS.Migration** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Migration.svg)](https://www.nuget.org/packages/PPDS.Migration/) | High-performance data migration engine |
| **PPDS.Auth** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Auth.svg)](https://www.nuget.org/packages/PPDS.Auth/) | Authentication profiles and credential management |
| **PPDS.Cli** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Cli.svg)](https://www.nuget.org/packages/PPDS.Cli/) | CLI tool with TUI (.NET tool) |
| **PPDS.Mcp** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Mcp.svg)](https://www.nuget.org/packages/PPDS.Mcp/) | MCP server for AI assistants (.NET tool) |

### Compatibility

| Package | Target Frameworks |
|---------|-------------------|
| PPDS.Plugins | net462 |
| PPDS.Dataverse | net8.0, net9.0, net10.0 |
| PPDS.Migration | net8.0, net9.0, net10.0 |
| PPDS.Auth | net8.0, net9.0, net10.0 |
| PPDS.Cli | net8.0, net9.0, net10.0 |
| PPDS.Mcp | net8.0, net9.0, net10.0 |

---

## Interactive TUI

Running `ppds` without arguments launches the interactive Terminal User Interface:

```bash
ppds  # Launches interactive TUI with guided workflows
```

The TUI provides a menu-driven interface for all PPDS operations, ideal for exploration and one-off tasks.

---

## MCP Server

The MCP server enables AI assistants like Claude Code to interact with Dataverse:

```bash
# Install the MCP server
dotnet tool install -g PPDS.Mcp

# Add to Claude Code MCP settings
ppds-mcp-server
```

**Capabilities:**
- Query Dataverse using natural language
- Explore entity metadata
- Analyze plugin registrations
- Execute FetchXML and SQL queries

---

## VS Code Extension

The VS Code extension provides IDE integration via JSON-RPC with the PPDS daemon:

- Environment and profile management
- Query execution with results view
- Plugin deployment workflows

Install from the [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite).

---

## CLI Commands

| Command | Purpose |
|---------|---------|
| `ppds auth` | Authentication profiles (create, list, select, delete, update, who) |
| `ppds env` | Environment discovery and selection (list, select, who) |
| `ppds data` | Data operations (export, import, copy, schema, users, load, truncate) |
| `ppds plugins` | Plugin registration (extract, deploy, diff, list, clean) |
| `ppds metadata` | Entity browsing (entities, attributes, relationships, keys, optionsets) |
| `ppds query` | Execute queries (fetch, sql) |
| `ppds serve` | Run RPC daemon for VS Code extension |

---

## PPDS.Plugins

Declarative attributes for configuring Dataverse plugin registrations directly in code.

```bash
dotnet add package PPDS.Plugins
```

```csharp
[PluginStep(
    Message = "Create",
    EntityLogicalName = "account",
    Stage = PluginStage.PostOperation)]
[PluginImage(
    ImageType = PluginImageType.PreImage,
    Name = "PreImage",
    Attributes = "name,telephone1")]
public class AccountCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) { }
}
```

See [PPDS.Plugins on NuGet](https://www.nuget.org/packages/PPDS.Plugins/) for details.

---

## PPDS.Dataverse

High-performance Dataverse connectivity with connection pooling, throttle-aware routing, and bulk operations.

```bash
dotnet add package PPDS.Dataverse
```

```csharp
// Setup with typed configuration
services.AddDataverseConnectionPool(options =>
{
    options.Connections.Add(new DataverseConnection("Primary")
    {
        Url = "https://org.crm.dynamics.com",
        ClientId = "your-client-id",
        ClientSecret = Environment.GetEnvironmentVariable("DATAVERSE_SECRET")
    });
    options.Pool.DisableAffinityCookie = true; // 10x+ throughput improvement
});

// Usage
await using var client = await pool.GetClientAsync();
var account = await client.RetrieveAsync("account", id, new ColumnSet(true));
```

See [PPDS.Dataverse documentation](src/PPDS.Dataverse/README.md) for details.

---

## PPDS.Migration

High-performance data migration engine for Dataverse. Replaces CMT for automated pipeline scenarios with 3-8x performance improvement.

```bash
dotnet add package PPDS.Migration
```

```csharp
// Setup
services.AddDataverseConnectionPool(options =>
{
    options.Connections.Add(new DataverseConnection("Target")
    {
        Url = "https://org.crm.dynamics.com",
        ClientId = "your-client-id",
        ClientSecret = Environment.GetEnvironmentVariable("DATAVERSE_SECRET")
    });
});
services.AddDataverseMigration();

// Export
var exporter = serviceProvider.GetRequiredService<IExporter>();
await exporter.ExportAsync("schema.xml", "data.zip");

// Import with dependency resolution
var importer = serviceProvider.GetRequiredService<IImporter>();
await importer.ImportAsync("data.zip");
```

**Key Features:**
- Parallel export (all entities exported concurrently)
- Tiered import with automatic dependency resolution
- Circular reference detection with deferred field processing
- CMT format compatibility (drop-in replacement)
- Security-first: no PII in logs, connection string redaction

See [PPDS.Migration documentation](src/PPDS.Migration/README.md) for details.

---

## Development

### Prerequisites

- .NET SDK 10.0+ (8.0 and 9.0 also supported)
- Node.js 20+ (for extension development)
- PowerShell 7+ (for scripts)

### Opening the Project

**Recommended:** Open `ppds.code-workspace` in VS Code for full-stack development.

This provides:
- .NET solution navigation with C# Dev Kit
- Extension F5 debugging with correct path resolution
- Unified build/test tasks for both .NET and TypeScript
- Compound debugging for CLI + Extension integration testing

**Alternatives:**
- Open root folder for .NET-only development
- Open `extension/` folder for extension-only development

### Building

```bash
# Build .NET solution
dotnet build PPDS.sln

# Build extension
cd extension && npm run compile

# Or use VS Code tasks: Ctrl+Shift+B
```

### Testing

```bash
# Unit tests (fast, no external dependencies)
dotnet test --filter Category!=Integration

# Integration tests (requires Dataverse connection)
dotnet test --filter Category=Integration

# TUI tests
dotnet test --filter Category=TuiUnit
```

### Debugging (F5)

| Configuration | Purpose |
|---------------|---------|
| `.NET: Debug TUI` | Launch interactive TUI |
| `.NET: Debug CLI` | Debug CLI with custom args |
| `.NET: Debug Daemon` | Run RPC daemon for extension |
| `Extension: Run` | Launch extension dev host |
| `Full-Stack: Daemon + Extension` | Debug both sides of RPC |

---

## Patterns

- [Connection Pooling](docs/architecture/CONNECTION_POOLING_PATTERNS.md) - When and how to use connection pooling
- [Bulk Operations](docs/architecture/BULK_OPERATIONS_PATTERNS.md) - High-throughput data operations

---

## Claude Code Integration

PPDS provides templates for Claude Code users developing Power Platform solutions:

- **Consumer Guide** - Best practices for PPDS development
- **Recommended Settings** - Permission configuration for PPDS commands
- **Slash Commands** - Quick reference commands

See [templates/claude/INSTALL.md](templates/claude/INSTALL.md) for installation instructions.

---

## Related Projects

| Project | Description |
|---------|-------------|
| [ppds-docs](https://joshsmithxrm.github.io/ppds-docs/) | Documentation site ([source](https://github.com/joshsmithxrm/ppds-docs)) |
| [ppds-tools](https://github.com/joshsmithxrm/ppds-tools) | PowerShell deployment module |
| [ppds-alm](https://github.com/joshsmithxrm/ppds-alm) | CI/CD pipeline templates |
| [ppds-demo](https://github.com/joshsmithxrm/ppds-demo) | Reference implementation |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on contributing to PPDS.

## License

MIT License - see [LICENSE](LICENSE) for details.
