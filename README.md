# Power Platform Developer Suite

[![Build](https://github.com/joshsmithxrm/power-platform-developer-suite/actions/workflows/build.yml/badge.svg)](https://github.com/joshsmithxrm/power-platform-developer-suite/actions/workflows/build.yml)
[![codecov](https://codecov.io/gh/joshsmithxrm/power-platform-developer-suite/graph/badge.svg)](https://codecov.io/gh/joshsmithxrm/power-platform-developer-suite)
[![Docs](https://img.shields.io/badge/docs-ppds--docs-blue)](https://joshsmithxrm.github.io/ppds-docs/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A comprehensive development platform for Microsoft Dataverse and Power Platform. Includes NuGet packages, CLI tool, VS Code extension, and TUI application.

## Packages

| Package | NuGet | Description |
|---------|-------|-------------|
| **PPDS.Plugins** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Plugins.svg)](https://www.nuget.org/packages/PPDS.Plugins/) | Declarative plugin registration attributes |
| **PPDS.Dataverse** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Dataverse.svg)](https://www.nuget.org/packages/PPDS.Dataverse/) | High-performance connection pooling and bulk operations |
| **PPDS.Migration** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Migration.svg)](https://www.nuget.org/packages/PPDS.Migration/) | High-performance data migration engine |
| **PPDS.Auth** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Auth.svg)](https://www.nuget.org/packages/PPDS.Auth/) | Authentication profiles and credential management |
| **PPDS.Cli** | [![NuGet](https://img.shields.io/nuget/v/PPDS.Cli.svg)](https://www.nuget.org/packages/PPDS.Cli/) | Unified CLI tool (.NET tool) |

## Compatibility

| Package | Target Frameworks |
|---------|-------------------|
| PPDS.Plugins | net462 |
| PPDS.Dataverse | net8.0, net9.0, net10.0 |
| PPDS.Migration | net8.0, net9.0, net10.0 |
| PPDS.Auth | net8.0, net9.0, net10.0 |
| PPDS.Cli | net8.0, net9.0, net10.0 |

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

## PPDS.Cli

Interactive TUI and CLI tool for Dataverse operations. Install as a .NET global tool:

```bash
dotnet tool install -g PPDS.Cli
```

Running `ppds` without arguments launches the interactive TUI:

```bash
ppds  # Launches interactive TUI with guided workflows
```

Or run specific commands directly:

```bash
# Create an auth profile (opens browser for login)
ppds auth create --name dev

# Select your environment
ppds env select --environment "My Environment"

# Export data
ppds data export --schema schema.xml --output data.zip

# Import data
ppds data import --data data.zip --mode Upsert
```

**Commands:**
- `ppds auth` - Authentication profiles (create, list, select, delete, update, who)
- `ppds env` - Environment discovery and selection (list, select, who)
- `ppds data` - Data operations (export, import, copy, schema, users, load)
- `ppds plugins` - Plugin registration (extract, deploy, diff, list, clean)
- `ppds metadata` - Entity browsing (entities, attributes, relationships, keys, optionsets)
- `ppds query` - Execute queries (fetch, sql)

See [PPDS.Cli documentation](src/PPDS.Cli/README.md) for details.

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
dotnet build PPDS.Sdk.sln

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

## Architecture Decisions

Key design decisions are documented as ADRs:

- [ADR-0001: Disable Affinity Cookie by Default](docs/adr/0001_DISABLE_AFFINITY_COOKIE.md)
- [ADR-0002: Multi-Connection Pooling](docs/adr/0002_MULTI_CONNECTION_POOLING.md)
- [ADR-0003: Throttle-Aware Connection Selection](docs/adr/0003_THROTTLE_AWARE_SELECTION.md)
- [ADR-0004: Throttle Recovery Strategy](docs/adr/0004_THROTTLE_RECOVERY_STRATEGY.md)
- [ADR-0005: DOP-Based Parallelism](docs/adr/0005_DOP_BASED_PARALLELISM.md)
- [ADR-0006: Connection Source Abstraction](docs/adr/0006_CONNECTION_SOURCE_ABSTRACTION.md)
- [ADR-0007: Unified CLI and Shared Authentication](docs/adr/0007_UNIFIED_CLI_AND_AUTH.md)
- [ADR-0008: CLI Output Architecture](docs/adr/0008_CLI_OUTPUT_ARCHITECTURE.md)
- [ADR-0009: CLI Command Taxonomy](docs/adr/0009_CLI_COMMAND_TAXONOMY.md)
- [ADR-0010: Published vs Unpublished Default](docs/adr/0010_PUBLISHED_UNPUBLISHED_DEFAULT.md)
- [ADR-0011: Deployment Settings Format](docs/adr/0011_DEPLOYMENT_SETTINGS_FORMAT.md)
- [ADR-0012: Hybrid Filter Design](docs/adr/0012_HYBRID_FILTER_DESIGN.md)
- [ADR-0013: CLI Dry-Run Convention](docs/adr/0013_CLI_DRY_RUN_CONVENTION.md)
- [ADR-0014: CSV Mapping Schema](docs/adr/0014_CSV_MAPPING_SCHEMA.md)

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

## License

MIT License - see [LICENSE](LICENSE) for details.
