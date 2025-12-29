# PPDS.Migration

High-performance Dataverse data migration engine. A drop-in replacement for CMT (Configuration Migration Tool) with 3-8x performance improvement through parallel operations and modern bulk APIs.

## Installation

```bash
dotnet add package PPDS.Migration
```

## Quick Start

```csharp
using Microsoft.Extensions.DependencyInjection;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.DependencyInjection;
using PPDS.Migration.Export;
using PPDS.Migration.Import;

// Configure services
var services = new ServiceCollection();

services.AddDataverseConnectionPool(options =>
{
    options.Connections.Add(new DataverseConnection("Target", connectionString));
});

services.AddDataverseMigration(options =>
{
    options.Export.DegreeOfParallelism = 8;
    options.Import.BatchSize = 1000;
    options.Import.UseBulkApis = true;
});

var provider = services.BuildServiceProvider();

// Export
var exporter = provider.GetRequiredService<IExporter>();
var exportResult = await exporter.ExportAsync("schema.xml", "data.zip");

// Import
var importer = provider.GetRequiredService<IImporter>();
var importResult = await importer.ImportAsync("data.zip");
```

## Features

### Parallel Export
- All entities exported concurrently (no dependencies during export)
- Configurable degree of parallelism
- FetchXML paging with paging cookie support

### Tiered Import
- Automatic dependency resolution using Tarjan's algorithm
- Entities grouped into tiers based on lookup dependencies
- Entities within a tier processed in parallel

### Circular Reference Handling
- Automatic detection of circular references (e.g., Account ↔ Contact)
- Deferred field processing: import records first with null lookups, then update
- No manual intervention required

### CMT Compatibility
- Reads CMT schema.xml format
- Produces CMT-compatible data.zip
- Drop-in replacement for existing pipelines

### Security
- Connection strings never logged
- No PII/record data in logs
- Only entity names, counts, and timing information reported

## Architecture

```
Export Flow:
schema.xml → SchemaAnalyzer → ParallelExporter → data.zip
                                    ↓
                            (N parallel workers)

Import Flow:
data.zip → DependencyGraphBuilder → ExecutionPlanBuilder → TieredImporter
                    ↓                       ↓                    ↓
            Tarjan's SCC              Tier ordering       Parallel within tier
                                                                  ↓
                                                          DeferredFieldProcessor
                                                                  ↓
                                                          RelationshipProcessor
```

## Configuration

### ExportOptions

| Option | Default | Description |
|--------|---------|-------------|
| DegreeOfParallelism | CPU * 2 | Maximum concurrent entity exports |
| PageSize | 5000 | Records per API request |

### ImportOptions

| Option | Default | Description |
|--------|---------|-------------|
| BatchSize | 1000 | Records per bulk operation |
| UseBulkApis | true | Use CreateMultiple/UpsertMultiple |
| BypassCustomPluginExecution | false | Skip custom plugins |
| BypassPowerAutomateFlows | false | Skip flows |
| ContinueOnError | true | Continue on individual failures |
| Mode | Upsert | Create, Update, or Upsert |

## Performance

| Scenario | CMT | PPDS.Migration | Improvement |
|----------|-----|----------------|-------------|
| Export 50 entities, 100K records | ~2 hours | ~15 min | 8x |
| Import 50 entities, 100K records | ~4 hours | ~1.5 hours | 2.5x |

## Requirements

- .NET 8.0 or .NET 10.0
- PPDS.Dataverse (connection pooling)

## Related

- [PPDS.Dataverse](https://www.nuget.org/packages/PPDS.Dataverse/) - Connection pooling and bulk operations
- [PPDS.Migration.Cli](https://www.nuget.org/packages/PPDS.Migration.Cli/) - Command-line tool

## License

MIT License
