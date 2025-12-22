# Bulk Operations Pattern

## When to Use

Use bulk operations when:

- Importing or syncing data (100+ records)
- Mass updates or deletes
- Initial data loads
- Throughput matters more than individual record handling

## When NOT to Use

Use single operations for:

- User-initiated single record changes
- Operations requiring complex per-record logic
- When you need individual success/failure handling per record

## Basic Pattern

```csharp
public class DataImporter
{
    private readonly IBulkOperationExecutor _bulk;

    public DataImporter(IBulkOperationExecutor bulk) => _bulk = bulk;

    public async Task ImportAccountsAsync(IEnumerable<Entity> accounts)
    {
        var result = await _bulk.CreateMultipleAsync("account", accounts);

        Console.WriteLine($"Created: {result.SuccessCount}");
        Console.WriteLine($"Failed: {result.FailureCount}");
        Console.WriteLine($"Duration: {result.Duration}");
    }
}
```

## Available Operations

| Method | API Used | Use Case |
|--------|----------|----------|
| `CreateMultipleAsync` | CreateMultiple | Insert new records |
| `UpdateMultipleAsync` | UpdateMultiple | Update existing records |
| `UpsertMultipleAsync` | UpsertMultiple | Insert or update (by alternate key) |
| `DeleteMultipleAsync` | DeleteMultiple | Remove records |

## Throughput Comparison

| Approach | Throughput |
|----------|------------|
| Single requests | ~50K records/hour |
| ExecuteMultiple | ~2M records/hour |
| **CreateMultiple/UpsertMultiple** | **~10M records/hour** |

## Handling Errors

```csharp
var result = await _bulk.UpsertMultipleAsync("account", entities,
    new BulkOperationOptions { ContinueOnError = true });

if (!result.IsSuccess)
{
    foreach (var error in result.Errors)
    {
        _logger.LogError(
            "Record {Index} failed: [{Code}] {Message}",
            error.Index,
            error.ErrorCode,
            error.Message);
    }
}
```

## Bypass Options

For maximum throughput during data loads:

```csharp
var options = new BulkOperationOptions
{
    BatchSize = 1000,                      // Max per request
    ContinueOnError = true,                // Don't stop on failures
    BypassCustomPluginExecution = true,    // Skip plugins
    BypassPowerAutomateFlows = true,       // Skip flows
    SuppressDuplicateDetection = true      // Skip duplicate rules
};

var result = await _bulk.CreateMultipleAsync("account", accounts, options);
```

### Bypass Considerations

| Option | Effect | Risk |
|--------|--------|------|
| `BypassCustomPluginExecution` | Skips all custom plugins | Business logic not enforced |
| `BypassPowerAutomateFlows` | Skips Power Automate triggers | Automation not triggered |
| `SuppressDuplicateDetection` | Skips duplicate detection | May create duplicates |

Only use bypass options when:
- You control the data quality
- Business logic is handled elsewhere
- You'll validate/reconcile after import

## Batching

Records are automatically batched (default: 1000 per request). Adjust for your scenario:

```csharp
// Smaller batches for complex records
new BulkOperationOptions { BatchSize = 100 }

// Max batch for simple records
new BulkOperationOptions { BatchSize = 1000 }
```

## Upsert Pattern

Use alternate keys for upsert operations:

```csharp
var accounts = externalData.Select(d => new Entity("account")
{
    // Alternate key for matching
    KeyAttributes = new KeyAttributeCollection
    {
        { "accountnumber", d.ExternalId }
    },
    Attributes =
    {
        ["name"] = d.Name,
        ["telephone1"] = d.Phone
    }
});

await _bulk.UpsertMultipleAsync("account", accounts);
```

## UpsertMultiple Pitfalls

### Duplicate Key Error with Alternate Keys

When using `UpsertMultiple` with alternate keys, set the key column in `KeyAttributes` ONLY. Do not also set it in `Attributes`.

```csharp
// ✅ Correct - key column only in KeyAttributes
var entity = new Entity("account");
entity.KeyAttributes["accountnumber"] = "ACCT-001";
entity["name"] = "Contoso";
entity["telephone1"] = "555-1234";

// ❌ Wrong - causes "An item with the same key has already been added"
var entity = new Entity("account");
entity.KeyAttributes["accountnumber"] = "ACCT-001";
entity["accountnumber"] = "ACCT-001";  // DO NOT SET THIS
entity["name"] = "Contoso";
```

**Why it happens:** Dataverse's `ClassifyEntitiesForUpdateAndCreateV2` processor copies `KeyAttributes` values into `Attributes` internally. When the attribute already exists, `Dictionary.Insert` throws a duplicate key exception.

**Symptoms:**
- Error: `An item with the same key has already been added`
- Stack trace includes `UpsertMultipleProcessor.ClassifyEntitiesForUpdateAndCreateV2`
- ALL batches fail (not just some), even though records are unique

**Sources:**
- [Power Platform Community Thread](https://community.powerplatform.com/forums/thread/details/?threadid=b86c1b19-3f91-ef11-ac21-6045bdd3c2dc)
- [Microsoft Docs: Bulk Operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bulk-operations)

## Parallel Bulk Operations

For very large datasets, parallelize across connections:

```csharp
var batches = allRecords.Chunk(10000); // 10K per task

var tasks = batches.Select(batch =>
    _bulk.UpsertMultipleAsync("account", batch));

var results = await Task.WhenAll(tasks);

var totalSuccess = results.Sum(r => r.SuccessCount);
var totalFailed = results.Sum(r => r.FailureCount);
```
