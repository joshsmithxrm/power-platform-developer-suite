# Multi-Environment Configuration & Live Migration - Design Specification

**Status:** Phase 1 Complete (v1.0.0), Phases 2-3 Future
**Target:** Phase 1 complete in v1.0.0
**Author:** Claude Code
**Date:** 2025-12-23

---

## Problem Statement

Current configuration assumes a single Dataverse environment:

```json
{
  "Dataverse": {
    "Url": "https://org.crm.dynamics.com",
    "Connections": [...]
  }
}
```

This works for single-environment operations but doesn't support:

-   Multiple named environments (Dev, Test, Prod)
-   Data migration between environments
-   Environment-specific connection configurations

---

## Solution Overview

### Phase 1: Multi-Environment Configuration

Enable named environments in configuration, each with its own URL and connections.

### Phase 2: Live Migration - Simple Cases

Direct source-to-target data transfer for single entities without transformations.

### Phase 3: Live Migration - Advanced

Dependency ordering, transformations, data masking, and resume capability.

---

## Phase 1: Multi-Environment Configuration

### Configuration Model

```csharp
public class DataverseOptions
{
    /// <summary>
    /// Named environment configurations.
    /// If not specified, root-level Url/Connections are treated as single "Default" environment.
    /// </summary>
    public Dictionary<string, DataverseEnvironmentOptions>? Environments { get; set; }

    /// <summary>
    /// Default environment name for operations that don't specify one.
    /// </summary>
    public string DefaultEnvironment { get; set; } = "Default";

    #region Single-Environment Shorthand (Backwards Compatible)

    /// <summary>
    /// Dataverse URL for single-environment configuration.
    /// Ignored if Environments is specified.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Tenant ID for single-environment configuration.
    /// Ignored if Environments is specified.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Connections for single-environment configuration.
    /// Ignored if Environments is specified.
    /// </summary>
    public List<DataverseConnectionOptions>? Connections { get; set; }

    #endregion

    /// <summary>
    /// Pool options (shared across all environments).
    /// </summary>
    public PoolOptions Pool { get; set; } = new();

    /// <summary>
    /// Adaptive rate control options (per-environment state, shared config).
    /// </summary>
    public AdaptiveRateOptions AdaptiveRate { get; set; } = new();
}

public class DataverseEnvironmentOptions
{
    /// <summary>
    /// Dataverse environment URL.
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// Azure AD tenant ID.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Application User connections for this environment.
    /// </summary>
    public List<DataverseConnectionOptions> Connections { get; set; } = new();

    /// <summary>
    /// Optional description for documentation.
    /// </summary>
    public string? Description { get; set; }
}
```

### Configuration Examples

#### Multi-Environment

```json
{
    "Dataverse": {
        "DefaultEnvironment": "Development",
        "Environments": {
            "Production": {
                "Description": "Production environment - handle with care",
                "Url": "https://prod-org.crm.dynamics.com",
                "TenantId": "00000000-0000-0000-0000-000000000000",
                "Connections": [
                    {
                        "Name": "Primary",
                        "ClientId": "prod-client-1",
                        "ClientSecretKeyVaultUri": "https://vault.azure.net/secrets/prod-primary"
                    },
                    {
                        "Name": "Secondary",
                        "ClientId": "prod-client-2",
                        "ClientSecretKeyVaultUri": "https://vault.azure.net/secrets/prod-secondary"
                    }
                ]
            },
            "Development": {
                "Description": "Development environment",
                "Url": "https://dev-org.crm.dynamics.com",
                "TenantId": "00000000-0000-0000-0000-000000000000",
                "Connections": [
                    {
                        "Name": "Primary",
                        "ClientId": "dev-client",
                        "ClientSecret": "DEV_DATAVERSE_SECRET"
                    }
                ]
            },
            "UAT": {
                "Description": "User acceptance testing",
                "Url": "https://uat-org.crm.dynamics.com",
                "TenantId": "00000000-0000-0000-0000-000000000000",
                "Connections": [
                    {
                        "Name": "Primary",
                        "ClientId": "uat-client",
                        "ClientSecretKeyVaultUri": "https://vault.azure.net/secrets/uat-primary"
                    }
                ]
            }
        }
    }
}
```

#### Single-Environment (Backwards Compatible)

```json
{
    "Dataverse": {
        "Url": "https://org.crm.dynamics.com",
        "TenantId": "00000000-0000-0000-0000-000000000000",
        "Connections": [
            {
                "Name": "Primary",
                "ClientId": "...",
                "ClientSecret": "DATAVERSE_SECRET"
            }
        ]
    }
}
```

Internally treated as:

```json
{
  "Dataverse": {
    "DefaultEnvironment": "Default",
    "Environments": {
      "Default": {
        "Url": "https://org.crm.dynamics.com",
        "TenantId": "...",
        "Connections": [...]
      }
    }
  }
}
```

### Environment Resolution

```csharp
internal class EnvironmentResolver
{
    private readonly DataverseOptions _options;
    private readonly Dictionary<string, DataverseEnvironmentOptions> _environments;

    public EnvironmentResolver(DataverseOptions options)
    {
        _options = options;
        _environments = ResolveEnvironments(options);
    }

    private static Dictionary<string, DataverseEnvironmentOptions> ResolveEnvironments(DataverseOptions options)
    {
        // If Environments is specified, use it directly
        if (options.Environments != null && options.Environments.Count > 0)
        {
            return options.Environments;
        }

        // Otherwise, create implicit "Default" environment from root properties
        if (string.IsNullOrEmpty(options.Url))
        {
            throw new ConfigurationException("Either Environments or Url must be specified");
        }

        return new Dictionary<string, DataverseEnvironmentOptions>
        {
            ["Default"] = new DataverseEnvironmentOptions
            {
                Url = options.Url,
                TenantId = options.TenantId,
                Connections = options.Connections ?? new List<DataverseConnectionOptions>()
            }
        };
    }

    public DataverseEnvironmentOptions GetEnvironment(string? name = null)
    {
        var envName = name ?? _options.DefaultEnvironment;

        if (!_environments.TryGetValue(envName, out var env))
        {
            var available = string.Join(", ", _environments.Keys);
            throw new ConfigurationException(
                $"Environment '{envName}' not found. Available: {available}");
        }

        return env;
    }

    public IEnumerable<string> GetEnvironmentNames() => _environments.Keys;
}
```

### CLI Usage

```bash
# Uses DefaultEnvironment
ppds-migrate export --entity account --output ./data

# Explicit environment
ppds-migrate export --env Production --entity account --output ./data

# List configured environments
ppds-migrate environments list

# Show environment details
ppds-migrate environments show Production
```

---

## Phase 2: Live Migration - Simple Cases

### Overview

Direct data transfer: Source → Memory Buffer → Target

No intermediate files, streaming when possible.

### CLI Usage

```bash
# Basic live migration
ppds-migrate live --source Production --target Development --entity account

# Multiple entities
ppds-migrate live --source Production --target Development \
  --entity account,contact,opportunity

# With filtering
ppds-migrate live --source Production --target Development \
  --entity account \
  --filter "modifiedon gt 2024-01-01"

# Limit records (for testing)
ppds-migrate live --source Production --target Development \
  --entity account \
  --top 100

# Batch configuration
ppds-migrate live --source Production --target Development \
  --entity account \
  --batch-size 100 \
  --max-parallel-batches 10
```

### Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Live Migration Pipeline                       │
├─────────────────────────────────────────────────────────────────────┤
│                                                                       │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐              │
│  │   Source    │    │   Buffer    │    │   Target    │              │
│  │    Pool     │───▶│   Channel   │───▶│    Pool     │              │
│  │             │    │             │    │             │              │
│  │ • Read      │    │ • Bounded   │    │ • Write     │              │
│  │ • Throttle  │    │ • Backpress │    │ • Throttle  │              │
│  │ • Adaptive  │    │ • Batch     │    │ • Adaptive  │              │
│  └─────────────┘    └─────────────┘    └─────────────┘              │
│         │                  │                  │                      │
│         ▼                  ▼                  ▼                      │
│  ┌─────────────────────────────────────────────────────┐            │
│  │                  Progress Reporter                   │            │
│  │  • Records read/written                              │            │
│  │  • Throughput (records/sec)                          │            │
│  │  • Errors & retries                                  │            │
│  │  • ETA                                               │            │
│  └─────────────────────────────────────────────────────┘            │
│                                                                       │
└─────────────────────────────────────────────────────────────────────┘
```

### Core Components

```csharp
public interface ILiveMigrationService
{
    /// <summary>
    /// Migrates data from source to target environment.
    /// </summary>
    Task<LiveMigrationResult> MigrateAsync(
        LiveMigrationOptions options,
        IProgress<LiveMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public class LiveMigrationOptions
{
    /// <summary>
    /// Source environment name.
    /// </summary>
    public required string SourceEnvironment { get; set; }

    /// <summary>
    /// Target environment name.
    /// </summary>
    public required string TargetEnvironment { get; set; }

    /// <summary>
    /// Entities to migrate.
    /// </summary>
    public required List<string> Entities { get; set; }

    /// <summary>
    /// Optional FetchXML filter condition.
    /// </summary>
    public string? Filter { get; set; }

    /// <summary>
    /// Maximum records to migrate (0 = unlimited).
    /// </summary>
    public int TopCount { get; set; } = 0;

    /// <summary>
    /// Batch size for bulk operations.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum parallel batches for write operations.
    /// </summary>
    public int MaxParallelBatches { get; set; } = 10;

    /// <summary>
    /// Buffer capacity (batches). Controls backpressure.
    /// </summary>
    public int BufferCapacity { get; set; } = 5;

    /// <summary>
    /// Operation mode for existing records.
    /// </summary>
    public MigrationMode Mode { get; set; } = MigrationMode.Upsert;
}

public enum MigrationMode
{
    /// <summary>
    /// Create only - fail if record exists.
    /// </summary>
    Create,

    /// <summary>
    /// Update only - fail if record doesn't exist.
    /// </summary>
    Update,

    /// <summary>
    /// Upsert - create or update as needed.
    /// </summary>
    Upsert
}

public record LiveMigrationProgress
{
    public required string Entity { get; init; }
    public required int RecordsRead { get; init; }
    public required int RecordsWritten { get; init; }
    public required int RecordsFailed { get; init; }
    public required int TotalRecords { get; init; }
    public required double RecordsPerSecond { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required TimeSpan? EstimatedRemaining { get; init; }
    public required string? CurrentOperation { get; init; }
}

public record LiveMigrationResult
{
    public required bool Success { get; init; }
    public required int TotalRecordsRead { get; init; }
    public required int TotalRecordsWritten { get; init; }
    public required int TotalRecordsFailed { get; init; }
    public required TimeSpan Duration { get; init; }
    public required List<LiveMigrationEntityResult> EntityResults { get; init; }
    public required List<LiveMigrationError> Errors { get; init; }
}

public record LiveMigrationEntityResult
{
    public required string Entity { get; init; }
    public required int RecordsRead { get; init; }
    public required int RecordsWritten { get; init; }
    public required int RecordsFailed { get; init; }
    public required TimeSpan Duration { get; init; }
}

public record LiveMigrationError
{
    public required string Entity { get; init; }
    public required Guid? RecordId { get; init; }
    public required string Message { get; init; }
    public required string? ErrorCode { get; init; }
}
```

### Pipeline Implementation

```csharp
internal class LiveMigrationPipeline
{
    private readonly IDataverseConnectionPool _sourcePool;
    private readonly IDataverseConnectionPool _targetPool;
    private readonly IBulkOperationExecutor _executor;
    private readonly ILogger _logger;

    public async Task<LiveMigrationResult> ExecuteAsync(
        LiveMigrationOptions options,
        IProgress<LiveMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<LiveMigrationEntityResult>();
        var errors = new List<LiveMigrationError>();
        var stopwatch = Stopwatch.StartNew();

        foreach (var entity in options.Entities)
        {
            var entityResult = await MigrateEntityAsync(
                entity, options, progress, errors, cancellationToken);
            results.Add(entityResult);
        }

        return new LiveMigrationResult
        {
            Success = errors.Count == 0,
            TotalRecordsRead = results.Sum(r => r.RecordsRead),
            TotalRecordsWritten = results.Sum(r => r.RecordsWritten),
            TotalRecordsFailed = results.Sum(r => r.RecordsFailed),
            Duration = stopwatch.Elapsed,
            EntityResults = results,
            Errors = errors
        };
    }

    private async Task<LiveMigrationEntityResult> MigrateEntityAsync(
        string entity,
        LiveMigrationOptions options,
        IProgress<LiveMigrationProgress>? progress,
        List<LiveMigrationError> errors,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var recordsRead = 0;
        var recordsWritten = 0;
        var recordsFailed = 0;

        // Bounded channel for backpressure
        var channel = Channel.CreateBounded<List<Entity>>(
            new BoundedChannelOptions(options.BufferCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });

        // Producer: Read from source
        var readTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in ReadBatchesAsync(entity, options, cancellationToken))
                {
                    recordsRead += batch.Count;
                    await channel.Writer.WriteAsync(batch, cancellationToken);

                    progress?.Report(new LiveMigrationProgress
                    {
                        Entity = entity,
                        RecordsRead = recordsRead,
                        RecordsWritten = recordsWritten,
                        RecordsFailed = recordsFailed,
                        TotalRecords = 0, // Unknown until complete
                        RecordsPerSecond = recordsRead / stopwatch.Elapsed.TotalSeconds,
                        Elapsed = stopwatch.Elapsed,
                        EstimatedRemaining = null,
                        CurrentOperation = "Reading"
                    });
                }
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        // Consumer: Write to target
        var writeTask = Task.Run(async () =>
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken))
            {
                var result = await WriteBatchAsync(entity, batch, options, cancellationToken);
                recordsWritten += result.SuccessCount;
                recordsFailed += result.FailureCount;

                foreach (var error in result.Errors)
                {
                    errors.Add(new LiveMigrationError
                    {
                        Entity = entity,
                        RecordId = error.RecordId,
                        Message = error.Message,
                        ErrorCode = error.ErrorCode?.ToString()
                    });
                }

                progress?.Report(new LiveMigrationProgress
                {
                    Entity = entity,
                    RecordsRead = recordsRead,
                    RecordsWritten = recordsWritten,
                    RecordsFailed = recordsFailed,
                    TotalRecords = recordsRead, // Updated as we read
                    RecordsPerSecond = recordsWritten / stopwatch.Elapsed.TotalSeconds,
                    Elapsed = stopwatch.Elapsed,
                    EstimatedRemaining = EstimateRemaining(recordsRead, recordsWritten, stopwatch.Elapsed),
                    CurrentOperation = "Writing"
                });
            }
        }, cancellationToken);

        await Task.WhenAll(readTask, writeTask);

        return new LiveMigrationEntityResult
        {
            Entity = entity,
            RecordsRead = recordsRead,
            RecordsWritten = recordsWritten,
            RecordsFailed = recordsFailed,
            Duration = stopwatch.Elapsed
        };
    }

    private async IAsyncEnumerable<List<Entity>> ReadBatchesAsync(
        string entity,
        LiveMigrationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var client = await _sourcePool.GetClientAsync(cancellationToken: cancellationToken);

        var query = BuildQuery(entity, options);
        var batch = new List<Entity>(options.BatchSize);

        // Paging through results
        string? pagingCookie = null;
        var moreRecords = true;

        while (moreRecords && !cancellationToken.IsCancellationRequested)
        {
            query.PageInfo = new PagingInfo
            {
                Count = options.BatchSize,
                PagingCookie = pagingCookie,
                ReturnTotalRecordCount = false
            };

            var response = await client.RetrieveMultipleAsync(query, cancellationToken);

            if (response.Entities.Count > 0)
            {
                yield return response.Entities.ToList();
            }

            moreRecords = response.MoreRecords;
            pagingCookie = response.PagingCookie;
        }
    }

    private async Task<BulkOperationResult> WriteBatchAsync(
        string entity,
        List<Entity> batch,
        LiveMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var bulkOptions = new BulkOperationOptions
        {
            BatchSize = options.BatchSize,
            MaxParallelBatches = options.MaxParallelBatches
        };

        return options.Mode switch
        {
            MigrationMode.Create => await _executor.CreateMultipleAsync(batch, bulkOptions, cancellationToken),
            MigrationMode.Update => await _executor.UpdateMultipleAsync(batch, bulkOptions, cancellationToken),
            MigrationMode.Upsert => await _executor.UpsertMultipleAsync(batch, bulkOptions, cancellationToken),
            _ => throw new NotSupportedException($"Mode '{options.Mode}' not supported")
        };
    }
}
```

### Backpressure Handling

The bounded channel naturally handles backpressure:

```
Source reads fast, target throttled:
  → Channel fills to BufferCapacity
  → channel.Writer.WriteAsync blocks
  → Source slows down automatically
  → No memory explosion

Target catches up:
  → Channel has space
  → Source resumes reading
  → Pipeline flows smoothly
```

---

## Phase 3: Live Migration - Advanced (Future)

### Dependency Ordering

```csharp
public class LiveMigrationOptions
{
    // ... existing properties ...

    /// <summary>
    /// Automatically order entities by dependencies.
    /// Parents migrated before children.
    /// </summary>
    public bool AutoOrderByDependencies { get; set; } = true;
}
```

Analysis: Account → Contact → Opportunity (Contact.ParentCustomerId references Account)

### Transformations

```csharp
public class LiveMigrationOptions
{
    // ... existing properties ...

    /// <summary>
    /// Transformations to apply during migration.
    /// </summary>
    public List<IRecordTransformation>? Transformations { get; set; }
}

public interface IRecordTransformation
{
    /// <summary>
    /// Applies transformation to a record.
    /// Return null to skip the record.
    /// </summary>
    Entity? Transform(Entity record, TransformationContext context);
}

// Built-in transformations
public class FieldMappingTransformation : IRecordTransformation { }
public class DataMaskingTransformation : IRecordTransformation { }
public class LookupRemappingTransformation : IRecordTransformation { }
public class ExcludeFieldsTransformation : IRecordTransformation { }
```

### Data Masking (PII Protection)

```json
{
    "Transformations": [
        {
            "Type": "DataMasking",
            "Rules": [
                { "Field": "emailaddress1", "Method": "Email" },
                { "Field": "telephone1", "Method": "Phone" },
                { "Field": "address1_line1", "Method": "Redact" }
            ]
        }
    ]
}
```

### Resume Capability

```csharp
public class LiveMigrationOptions
{
    // ... existing properties ...

    /// <summary>
    /// Checkpoint file for resume capability.
    /// </summary>
    public string? CheckpointFile { get; set; }

    /// <summary>
    /// Resume from previous checkpoint if available.
    /// </summary>
    public bool Resume { get; set; } = false;
}
```

```bash
# Start migration with checkpoint
ppds-migrate live --source Prod --target Dev \
  --entity account,contact \
  --checkpoint ./migration.checkpoint

# Resume after failure
ppds-migrate live --source Prod --target Dev \
  --entity account,contact \
  --checkpoint ./migration.checkpoint \
  --resume
```

---

## File Changes

### Phase 1

| File                                                                    | Change                    |
| ----------------------------------------------------------------------- | ------------------------- |
| `src/PPDS.Dataverse/DependencyInjection/DataverseOptions.cs`            | Add Environments property |
| `src/PPDS.Dataverse/DependencyInjection/DataverseEnvironmentOptions.cs` | New class                 |
| `src/PPDS.Dataverse/Configuration/EnvironmentResolver.cs`               | New class                 |
| `src/PPDS.Migration.Cli/Commands/EnvironmentsCommand.cs`                | New command               |

### Phase 2

| File                                               | Change             |
| -------------------------------------------------- | ------------------ |
| `src/PPDS.Migration/Live/ILiveMigrationService.cs` | New interface      |
| `src/PPDS.Migration/Live/LiveMigrationService.cs`  | New implementation |
| `src/PPDS.Migration/Live/LiveMigrationPipeline.cs` | New pipeline       |
| `src/PPDS.Migration/Live/LiveMigrationOptions.cs`  | New options        |
| `src/PPDS.Migration.Cli/Commands/LiveCommand.cs`   | New command        |

### Phase 3

| File                                                | Change                     |
| --------------------------------------------------- | -------------------------- |
| `src/PPDS.Migration/Transformations/`               | New transformation classes |
| `src/PPDS.Migration/Live/CheckpointManager.cs`      | New checkpoint handling    |
| `src/PPDS.Migration/Analysis/DependencyAnalyzer.cs` | New dependency analysis    |

---

## References

-   [Dataverse Bulk Operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bulk-operations)
-   [System.Threading.Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)
-   [Producer-Consumer Patterns](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/how-to-implement-a-producer-consumer-dataflow-pattern)
