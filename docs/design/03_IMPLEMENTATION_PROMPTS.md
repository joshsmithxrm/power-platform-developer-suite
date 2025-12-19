# Implementation Prompts

**Purpose:** Prompts for implementing PPDS.Dataverse and PPDS.Migration components
**Usage:** Copy the relevant prompt to begin implementation of each component

---

## Table of Contents

### PPDS.Dataverse
1. [Project Setup](#prompt-1-ppdsdataverse-project-setup)
2. [Core Client Abstraction](#prompt-2-core-client-abstraction)
3. [Connection Pool](#prompt-3-connection-pool)
4. [Connection Selection Strategies](#prompt-4-connection-selection-strategies)
5. [Throttle Tracking](#prompt-5-throttle-tracking)
6. [Bulk Operations](#prompt-6-bulk-operations)
7. [DI Extensions](#prompt-7-di-extensions)
8. [Unit Tests](#prompt-8-ppdsdataverse-unit-tests)

### PPDS.Migration
9. [Project Setup](#prompt-9-ppdsmigration-project-setup)
10. [Schema Parser](#prompt-10-schema-parser)
11. [Dependency Graph Builder](#prompt-11-dependency-graph-builder)
12. [Execution Plan Builder](#prompt-12-execution-plan-builder)
13. [Parallel Exporter](#prompt-13-parallel-exporter)
14. [Tiered Importer](#prompt-14-tiered-importer)
15. [Progress Reporting](#prompt-15-progress-reporting)
16. [CLI Tool](#prompt-16-cli-tool)

---

## PPDS.Dataverse Prompts

### Prompt 1: PPDS.Dataverse Project Setup

```
Create the PPDS.Dataverse project in the ppds-sdk repository.

## Context
- Repository: C:\VS\ppds\sdk
- Existing project: PPDS.Plugins (see src/PPDS.Plugins/PPDS.Plugins.csproj for patterns)
- Design doc: C:\VS\ppds\tmp\sdk-design\01_PPDS_DATAVERSE_DESIGN.md

## Requirements

1. Create project structure:
   ```
   src/PPDS.Dataverse/
   ├── PPDS.Dataverse.csproj
   ├── Client/
   ├── Pooling/
   ├── Pooling/Strategies/
   ├── BulkOperations/
   ├── Resilience/
   ├── Diagnostics/
   └── DependencyInjection/
   ```

2. Configure PPDS.Dataverse.csproj:
   - Target frameworks: net8.0;net10.0
   - Enable nullable reference types
   - Enable XML documentation
   - Strong name signing (generate new PPDS.Dataverse.snk)
   - NuGet metadata matching PPDS.Plugins style
   - Package dependencies:
     - Microsoft.PowerPlatform.Dataverse.Client (1.1.*)
     - Microsoft.Extensions.DependencyInjection.Abstractions (8.0.*)
     - Microsoft.Extensions.Logging.Abstractions (8.0.*)
     - Microsoft.Extensions.Options (8.0.*)

3. Add project to PPDS.Sdk.sln

4. Create placeholder files with namespace declarations for each folder

Do NOT implement functionality yet - just project scaffolding.
```

---

### Prompt 2: Core Client Abstraction

```
Implement the core client abstraction for PPDS.Dataverse.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Dataverse
- Design doc: C:\VS\ppds\tmp\sdk-design\01_PPDS_DATAVERSE_DESIGN.md (see "Core Interfaces" section)

## Requirements

1. Create Client/IDataverseClient.cs:
   - Inherit from IOrganizationServiceAsync2
   - Add properties: IsReady, RecommendedDegreesOfParallelism, ConnectedOrgId, ConnectedOrgFriendlyName, LastError, LastException
   - Add Clone() method returning IDataverseClient

2. Create Client/DataverseClient.cs:
   - Wrap ServiceClient from Microsoft.PowerPlatform.Dataverse.Client
   - Constructor takes ServiceClient instance
   - Implement all IOrganizationServiceAsync2 methods by delegating to ServiceClient
   - Implement additional IDataverseClient properties

3. Create Client/DataverseClientOptions.cs:
   - CallerId (Guid?)
   - CallerAADObjectId (Guid?)
   - MaxRetryCount (int)
   - RetryPauseTime (TimeSpan)

Follow patterns from PPDS.Plugins for XML documentation style.
All public members must have XML documentation.
```

---

### Prompt 3: Connection Pool

```
Implement the connection pool for PPDS.Dataverse.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Dataverse
- Design doc: C:\VS\ppds\tmp\sdk-design\01_PPDS_DATAVERSE_DESIGN.md
- Original implementation for reference: C:\VS\ppds\tmp\DataverseConnectionPooling\DataverseConnectionPool.cs

## Requirements

1. Create Pooling/IDataverseConnectionPool.cs (from design doc)

2. Create Pooling/IPooledClient.cs (from design doc)

3. Create Pooling/PooledClient.cs:
   - Wraps IDataverseClient
   - Tracks ConnectionId, ConnectionName, CreatedAt, LastUsedAt
   - On Dispose/DisposeAsync, returns connection to pool

4. Create Pooling/DataverseConnection.cs:
   - Name, ConnectionString, Weight, MaxPoolSize properties

5. Create Pooling/ConnectionPoolOptions.cs (from design doc)

6. Create Pooling/PoolStatistics.cs:
   - TotalConnections, ActiveConnections, IdleConnections, ThrottledConnections
   - RequestsServed, ThrottleEvents

7. Create Pooling/DataverseConnectionPool.cs:
   - Implements IDataverseConnectionPool
   - Uses ConcurrentDictionary<string, ConcurrentQueue<PooledClient>> for per-connection pools
   - Uses SemaphoreSlim for connection limiting
   - DO NOT lock around ConcurrentQueue operations (they're already thread-safe)
   - Configures ServiceClient with EnableAffinityCookie = false by default
   - Background validation task for idle connection cleanup
   - Implements IAsyncDisposable for graceful shutdown

## Key improvements over original:
- Multiple connection sources
- No unnecessary locks around ConcurrentQueue
- Bounded iteration instead of recursion
- Per-connection pool tracking
```

---

### Prompt 4: Connection Selection Strategies

```
Implement connection selection strategies for PPDS.Dataverse.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Dataverse
- Design doc: C:\VS\ppds\tmp\sdk-design\01_PPDS_DATAVERSE_DESIGN.md

## Requirements

1. Create Pooling/Strategies/IConnectionSelectionStrategy.cs:
   ```csharp
   public interface IConnectionSelectionStrategy
   {
       string SelectConnection(
           IReadOnlyList<DataverseConnection> connections,
           IThrottleTracker throttleTracker,
           IReadOnlyDictionary<string, int> activeConnections);
   }
   ```

2. Create Pooling/Strategies/RoundRobinStrategy.cs:
   - Simple rotation through connections
   - Use Interlocked.Increment for thread-safe counter

3. Create Pooling/Strategies/LeastConnectionsStrategy.cs:
   - Select connection with fewest active clients
   - Fall back to first connection on tie

4. Create Pooling/Strategies/ThrottleAwareStrategy.cs:
   - Filter out throttled connections (use IThrottleTracker)
   - Among available connections, use round-robin
   - If ALL connections throttled, wait for shortest throttle to expire

5. Create Pooling/ConnectionSelectionStrategy.cs (enum):
   - RoundRobin, LeastConnections, ThrottleAware

6. Update DataverseConnectionPool to use strategy pattern
```

---

### Prompt 5: Throttle Tracking

```
Implement throttle tracking for PPDS.Dataverse.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Dataverse
- Design doc: C:\VS\ppds\tmp\sdk-design\01_PPDS_DATAVERSE_DESIGN.md

## Requirements

1. Create Resilience/IThrottleTracker.cs (from design doc)

2. Create Resilience/ThrottleState.cs:
   - ConnectionName (string)
   - ThrottledAt (DateTime)
   - ExpiresAt (DateTime)
   - RetryAfter (TimeSpan)

3. Create Resilience/ThrottleTracker.cs:
   - Uses ConcurrentDictionary<string, ThrottleState>
   - RecordThrottle() stores throttle with expiry time
   - IsThrottled() checks if current time < expiry
   - GetAvailableConnections() returns non-throttled connections
   - Background cleanup of expired throttle states

4. Create Resilience/ResilienceOptions.cs (from design doc)

5. Create Resilience/ServiceProtectionException.cs:
   - Custom exception for 429/throttle scenarios
   - Properties: ConnectionName, RetryAfter, ErrorCode
   - Error codes: -2147015902 (requests), -2147015903 (execution time), -2147015898 (concurrent)

6. Update DataverseClient to detect throttle responses and call ThrottleTracker
```

---

### Prompt 6: Bulk Operations

```
Implement bulk operations for PPDS.Dataverse.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Dataverse
- Design doc: C:\VS\ppds\tmp\sdk-design\01_PPDS_DATAVERSE_DESIGN.md

## Requirements

1. Create BulkOperations/IBulkOperationExecutor.cs (from design doc)

2. Create BulkOperations/BulkOperationOptions.cs (from design doc)

3. Create BulkOperations/BulkOperationResult.cs:
   - SuccessCount (int)
   - FailureCount (int)
   - Errors (IReadOnlyList<BulkOperationError>)
   - Duration (TimeSpan)

4. Create BulkOperations/BulkOperationError.cs:
   - Index (int) - position in input collection
   - RecordId (Guid?)
   - ErrorCode (int)
   - Message (string)

5. Create BulkOperations/BulkOperationExecutor.cs:
   - Constructor takes IDataverseConnectionPool
   - CreateMultipleAsync: Uses CreateMultipleRequest
   - UpdateMultipleAsync: Uses UpdateMultipleRequest
   - UpsertMultipleAsync: Uses UpsertMultipleRequest
   - DeleteMultipleAsync: Uses DeleteMultipleRequest
   - Batch records according to BatchSize option
   - Apply BypassCustomPluginExecution via request parameters
   - Collect errors but continue if ContinueOnError = true
   - Track timing for result

## Notes:
- CreateMultiple/UpdateMultiple/UpsertMultiple require Dataverse 9.2.23083+
- Fall back to ExecuteMultiple for older versions
- Maximum batch size is 1000 records
```

---

### Prompt 7: DI Extensions

```
Implement dependency injection extensions for PPDS.Dataverse.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Dataverse
- Design doc: C:\VS\ppds\tmp\sdk-design\01_PPDS_DATAVERSE_DESIGN.md

## Requirements

1. Create DependencyInjection/DataverseOptions.cs (from design doc):
   - Connections (List<DataverseConnection>)
   - Pool (ConnectionPoolOptions)
   - Resilience (ResilienceOptions)
   - BulkOperations (BulkOperationOptions)

2. Create DependencyInjection/ServiceCollectionExtensions.cs:
   - AddDataverseConnectionPool(Action<DataverseOptions> configure)
   - AddDataverseConnectionPool(IConfiguration, string sectionName = "Dataverse")
   - Validate that at least one connection is configured
   - Register: IThrottleTracker (singleton), IDataverseConnectionPool (singleton), IBulkOperationExecutor (transient)

3. Ensure pool applies .NET performance settings on first initialization:
   ```csharp
   ThreadPool.SetMinThreads(100, 100);
   ServicePointManager.DefaultConnectionLimit = 65000;
   ServicePointManager.Expect100Continue = false;
   ServicePointManager.UseNagleAlgorithm = false;
   ```

4. Add validation for options:
   - At least one connection required
   - MaxPoolSize >= MinPoolSize
   - Timeouts are positive
```

---

### Prompt 8: PPDS.Dataverse Unit Tests

```
Create unit tests for PPDS.Dataverse.

## Context
- Project: C:\VS\ppds\sdk\tests\PPDS.Dataverse.Tests
- Reference: C:\VS\ppds\sdk\tests\PPDS.Plugins.Tests for patterns

## Requirements

1. Create test project:
   ```
   tests/PPDS.Dataverse.Tests/
   ├── PPDS.Dataverse.Tests.csproj
   ├── Pooling/
   │   ├── DataverseConnectionPoolTests.cs
   │   ├── RoundRobinStrategyTests.cs
   │   ├── LeastConnectionsStrategyTests.cs
   │   └── ThrottleAwareStrategyTests.cs
   ├── Resilience/
   │   └── ThrottleTrackerTests.cs
   ├── BulkOperations/
   │   └── BulkOperationExecutorTests.cs
   └── DependencyInjection/
       └── ServiceCollectionExtensionsTests.cs
   ```

2. Test dependencies:
   - xUnit
   - Moq
   - FluentAssertions
   - Microsoft.Extensions.DependencyInjection (for DI tests)

3. Key test scenarios:

   DataverseConnectionPoolTests:
   - GetClientAsync returns client from pool
   - Client returns to pool on dispose
   - Pool respects MaxPoolSize
   - Pool evicts idle connections
   - Multiple connections are rotated

   ThrottleTrackerTests:
   - RecordThrottle marks connection as throttled
   - IsThrottled returns true within expiry window
   - IsThrottled returns false after expiry
   - GetAvailableConnections excludes throttled

   ThrottleAwareStrategyTests:
   - Skips throttled connections
   - Falls back when all throttled
   - Uses round-robin among available

4. Use mocks for ServiceClient (don't hit real Dataverse)
```

---

## PPDS.Migration Prompts

### Prompt 9: PPDS.Migration Project Setup

```
Create the PPDS.Migration project in the ppds-sdk repository.

## Context
- Repository: C:\VS\ppds\sdk
- Design doc: C:\VS\ppds\tmp\sdk-design\02_PPDS_MIGRATION_DESIGN.md
- Depends on: PPDS.Dataverse (must be created first)

## Requirements

1. Create project structure:
   ```
   src/PPDS.Migration/
   ├── PPDS.Migration.csproj
   ├── Analysis/
   ├── Export/
   ├── Import/
   ├── Models/
   ├── Progress/
   ├── Formats/
   └── DependencyInjection/
   ```

2. Configure PPDS.Migration.csproj:
   - Target frameworks: net8.0;net10.0
   - Enable nullable reference types
   - Enable XML documentation
   - Strong name signing (generate new PPDS.Migration.snk)
   - NuGet metadata matching ecosystem style
   - Project reference to PPDS.Dataverse
   - Package dependencies:
     - System.IO.Compression (for ZIP handling)

3. Add project to PPDS.Sdk.sln

4. Create placeholder files with namespace declarations

Do NOT implement functionality yet - just project scaffolding.
```

---

### Prompt 10: Schema Parser

```
Implement the CMT schema parser for PPDS.Migration.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Migration
- Design doc: C:\VS\ppds\tmp\sdk-design\02_PPDS_MIGRATION_DESIGN.md

## Requirements

1. Create Models/MigrationSchema.cs (from design doc)
2. Create Models/EntitySchema.cs (from design doc)
3. Create Models/FieldSchema.cs (from design doc)
4. Create Models/RelationshipSchema.cs (from design doc)

5. Create Formats/ICmtSchemaReader.cs:
   ```csharp
   public interface ICmtSchemaReader
   {
       Task<MigrationSchema> ReadAsync(string path, CancellationToken ct = default);
       Task<MigrationSchema> ReadAsync(Stream stream, CancellationToken ct = default);
   }
   ```

6. Create Formats/CmtSchemaReader.cs:
   - Parse CMT schema.xml format using XDocument
   - Extract entities, fields, relationships
   - Handle all field types: string, int, decimal, datetime, lookup, customer, owner, etc.
   - Identify lookup targets from lookupType attribute

## CMT Schema Format Reference:
```xml
<entities>
  <entity name="account" displayname="Account" primaryidfield="accountid"
          primarynamefield="name" disableplugins="false">
    <fields>
      <field name="name" displayname="Account Name" type="string" customfield="false" />
      <field name="primarycontactid" displayname="Primary Contact" type="lookup"
             lookupType="contact" customfield="false" />
    </fields>
    <relationships>
      <relationship name="accountleads_association" m2m="true" relatedEntityName="lead" />
    </relationships>
  </entity>
</entities>
```
```

---

### Prompt 11: Dependency Graph Builder

```
Implement the dependency graph builder for PPDS.Migration.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Migration
- Design doc: C:\VS\ppds\tmp\sdk-design\02_PPDS_MIGRATION_DESIGN.md
- CMT Investigation: C:\VS\ppds\tmp\sdk-design\reference\CMT_INVESTIGATION_REPORT.md

## Requirements

1. Create Models/DependencyGraph.cs (from design doc)
2. Create Models/EntityNode.cs
3. Create Models/DependencyEdge.cs
4. Create Models/DependencyType.cs (enum)
5. Create Models/CircularReference.cs

6. Create Analysis/IDependencyGraphBuilder.cs:
   ```csharp
   public interface IDependencyGraphBuilder
   {
       DependencyGraph Build(MigrationSchema schema);
   }
   ```

7. Create Analysis/DependencyGraphBuilder.cs:
   - Iterate all entities and their lookup/customer/owner fields
   - Create edges from entity to lookup target
   - Detect circular references using Tarjan's SCC algorithm
   - Topologically sort non-circular entities into tiers
   - Place circular reference groups in their own tier

## Algorithm:
1. Build adjacency list from schema
2. Run Tarjan's algorithm to find strongly connected components (SCCs)
3. SCCs with >1 node are circular references
4. Condense SCCs into single nodes
5. Topological sort the condensed graph
6. Expand back to get tier assignments

## Example Output:
```
Tier 0: [currency, subject, uomschedule]     # No dependencies
Tier 1: [businessunit, uom]                   # Depends on Tier 0
Tier 2: [systemuser, team]                    # Depends on Tier 1
Tier 3: [account, contact]                    # Circular - together
```
```

---

### Prompt 12: Execution Plan Builder

```
Implement the execution plan builder for PPDS.Migration.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Migration
- Design doc: C:\VS\ppds\tmp\sdk-design\02_PPDS_MIGRATION_DESIGN.md

## Requirements

1. Create Models/ExecutionPlan.cs (from design doc)
2. Create Models/ImportTier.cs
3. Create Models/DeferredField.cs:
   - EntityLogicalName (string)
   - FieldLogicalName (string)
   - TargetEntity (string)

4. Create Analysis/IExecutionPlanBuilder.cs:
   ```csharp
   public interface IExecutionPlanBuilder
   {
       ExecutionPlan Build(DependencyGraph graph);
   }
   ```

5. Create Analysis/ExecutionPlanBuilder.cs:
   - Convert tiers from graph into ImportTier objects
   - For circular references, determine which fields to defer:
     - For A ↔ B circular: defer the field pointing from higher-order to lower-order entity
     - Example: account.primarycontactid deferred, contact.parentcustomerid NOT deferred
   - Extract M2M relationships for final processing phase

## Deferred Field Selection Logic:
For circular reference [account ↔ contact]:
1. If account is imported before contact:
   - account.primarycontactid → DEFER (contact doesn't exist yet)
   - contact.parentcustomerid → KEEP (account exists)
2. Both entities go in same tier, processed in parallel
3. After ALL entities done, update deferred fields

## Output Example:
```csharp
new ExecutionPlan
{
    Tiers = [...],
    DeferredFields = {
        ["account"] = ["primarycontactid"]
    },
    ManyToManyRelationships = [...]
}
```
```

---

### Prompt 13: Parallel Exporter

```
Implement the parallel exporter for PPDS.Migration.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Migration
- Design doc: C:\VS\ppds\tmp\sdk-design\02_PPDS_MIGRATION_DESIGN.md
- Uses: PPDS.Dataverse.IDataverseConnectionPool

## Requirements

1. Create Export/IExporter.cs (from design doc)

2. Create Export/ExportOptions.cs (from design doc)

3. Create Export/ExportResult.cs:
   - EntitiesExported (int)
   - RecordsExported (int)
   - Duration (TimeSpan)
   - EntityResults (IReadOnlyList<EntityExportResult>)

4. Create Export/EntityExportResult.cs:
   - EntityLogicalName (string)
   - RecordCount (int)
   - Duration (TimeSpan)

5. Create Export/ParallelExporter.cs:
   - Constructor takes IDataverseConnectionPool, ICmtSchemaReader
   - Use Parallel.ForEachAsync with DegreeOfParallelism option
   - For each entity:
     - Get connection from pool
     - Build FetchXML from schema
     - Page through results (use paging cookie)
     - Collect records
   - After all entities, package into ZIP

6. Create Export/EntityExporter.cs (helper class):
   - Exports single entity using FetchXML
   - Handles paging with paging cookie
   - Reports progress per page

7. Create Formats/ICmtDataWriter.cs and CmtDataWriter.cs:
   - Write data.xml in CMT format
   - Create ZIP with data.xml and schema copy

## Key: Export has NO dependencies - all entities can be parallel!
```

---

### Prompt 14: Tiered Importer

```
Implement the tiered importer for PPDS.Migration.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Migration
- Design doc: C:\VS\ppds\tmp\sdk-design\02_PPDS_MIGRATION_DESIGN.md
- Uses: PPDS.Dataverse.IDataverseConnectionPool, IBulkOperationExecutor

## Requirements

1. Create Import/IImporter.cs (from design doc)

2. Create Import/ImportOptions.cs (from design doc)

3. Create Import/ImportResult.cs:
   - TiersProcessed (int)
   - RecordsImported (int)
   - RecordsUpdated (int) - deferred field updates
   - RelationshipsProcessed (int)
   - Errors (IReadOnlyList<ImportError>)
   - Duration (TimeSpan)

4. Create Import/TieredImporter.cs:
   - Constructor takes IDataverseConnectionPool, IBulkOperationExecutor, IExecutionPlanBuilder
   - Process flow:
     1. Read data from ZIP
     2. Build execution plan (or accept pre-built)
     3. For each tier:
        - Process entities in parallel (within tier)
        - Use bulk operations (CreateMultiple/UpsertMultiple)
        - Track old→new ID mappings
        - Set deferred fields to null
        - Wait for tier completion
     4. Process deferred fields (update with resolved lookups)
     5. Process M2M relationships

5. Create Import/EntityImporter.cs:
   - Import single entity using bulk operations
   - Track ID mappings
   - Report progress

6. Create Import/IDeferredFieldProcessor.cs and DeferredFieldProcessor.cs:
   - After all records exist, update deferred lookup fields
   - Use ID mappings to resolve old→new GUIDs

7. Create Import/IRelationshipProcessor.cs and RelationshipProcessor.cs:
   - Associate M2M relationships after all entities imported

8. Create Models/IdMapping.cs:
   - Dictionary<Guid, Guid> for old→new ID mapping
   - Per-entity mappings

## Key: Tiers are sequential, entities WITHIN tier are parallel!
```

---

### Prompt 15: Progress Reporting

```
Implement progress reporting for PPDS.Migration.

## Context
- Project: C:\VS\ppds\sdk\src\PPDS.Migration
- Design doc: C:\VS\ppds\tmp\sdk-design\02_PPDS_MIGRATION_DESIGN.md

## Requirements

1. Create Progress/IProgressReporter.cs (from design doc)

2. Create Progress/ProgressEventArgs.cs (from design doc)

3. Create Progress/MigrationPhase.cs (enum)

4. Create Progress/ConsoleProgressReporter.cs:
   - Write human-readable progress to Console
   - Show progress bars for record counts
   - Show elapsed time and ETA

5. Create Progress/JsonProgressReporter.cs:
   - Write JSON lines to TextWriter
   - One JSON object per line (JSONL format)
   - Include all fields from ProgressEventArgs
   - Used by CLI and VS Code extension integration

## JSON Output Format:
```json
{"phase":"analyzing","message":"Parsing schema..."}
{"phase":"export","entity":"account","current":450,"total":1000,"rps":287.5}
{"phase":"import","tier":0,"entity":"currency","current":5,"total":5}
{"phase":"deferred","entity":"account","field":"primarycontactid","current":450,"total":1000}
{"phase":"m2m","relationship":"accountleads","current":100,"total":200}
{"phase":"complete","duration":"00:45:23","recordsProcessed":15420}
```

6. Wire progress reporting into Exporter and Importer:
   - Report at configurable intervals (not every record)
   - Calculate records per second
```

---

### Prompt 16: CLI Tool

```
Implement the ppds-migrate CLI tool in the tools repository.

## Context
- Repository: C:\VS\ppds\tools
- Design doc: C:\VS\ppds\tmp\sdk-design\02_PPDS_MIGRATION_DESIGN.md
- References: PPDS.Migration NuGet package

## Requirements

1. Create project:
   ```
   tools/src/PPDS.Migration.Cli/
   ├── PPDS.Migration.Cli.csproj
   ├── Program.cs
   └── Commands/
       ├── ExportCommand.cs
       ├── ImportCommand.cs
       ├── AnalyzeCommand.cs
       └── MigrateCommand.cs
   ```

2. Configure as .NET tool:
   ```xml
   <PackAsTool>true</PackAsTool>
   <ToolCommandName>ppds-migrate</ToolCommandName>
   ```

3. Use System.CommandLine for argument parsing

4. Commands:

   export:
   - --connection (required): Dataverse connection string
   - --schema (required): Path to schema.xml
   - --output (required): Output ZIP path
   - --parallel: Degree of parallelism (default: CPU count * 2)
   - --json: Output progress as JSON

   import:
   - --connection (required): Dataverse connection string
   - --data (required): Path to data.zip
   - --batch-size: Records per batch (default: 1000)
   - --bypass-plugins: Bypass custom plugin execution
   - --continue-on-error: Continue on individual failures
   - --json: Output progress as JSON

   analyze:
   - --schema (required): Path to schema.xml
   - --output-format: json or text (default: text)

   migrate:
   - --source-connection (required): Source Dataverse connection
   - --target-connection (required): Target Dataverse connection
   - --schema (required): Path to schema.xml
   - (combines export + import)

5. Exit codes:
   - 0: Success
   - 1: Partial success (some records failed)
   - 2: Failure

## Example Usage:
```bash
ppds-migrate export --connection "AuthType=..." --schema schema.xml --output data.zip --json
ppds-migrate import --connection "AuthType=..." --data data.zip --batch-size 1000 --bypass-plugins
ppds-migrate analyze --schema schema.xml --output-format json
```
```

---

## Implementation Order

### Recommended Sequence

1. **PPDS.Dataverse** (foundation - must be first)
   1. Project Setup (Prompt 1)
   2. Core Client Abstraction (Prompt 2)
   3. Connection Pool (Prompt 3)
   4. Connection Selection Strategies (Prompt 4)
   5. Throttle Tracking (Prompt 5)
   6. Bulk Operations (Prompt 6)
   7. DI Extensions (Prompt 7)
   8. Unit Tests (Prompt 8)

2. **PPDS.Migration** (depends on PPDS.Dataverse)
   1. Project Setup (Prompt 9)
   2. Schema Parser (Prompt 10)
   3. Dependency Graph Builder (Prompt 11)
   4. Execution Plan Builder (Prompt 12)
   5. Parallel Exporter (Prompt 13)
   6. Tiered Importer (Prompt 14)
   7. Progress Reporting (Prompt 15)

3. **CLI Tool** (depends on PPDS.Migration)
   1. CLI Tool (Prompt 16)

4. **PowerShell Integration** (wraps CLI)
   - Add cmdlets to PPDS.Tools that call ppds-migrate CLI

---

## Related Documents

- [Package Strategy](00_PACKAGE_STRATEGY.md) - Overall architecture
- [PPDS.Dataverse Design](01_PPDS_DATAVERSE_DESIGN.md) - Connection pooling design
- [PPDS.Migration Design](02_PPDS_MIGRATION_DESIGN.md) - Migration engine design
