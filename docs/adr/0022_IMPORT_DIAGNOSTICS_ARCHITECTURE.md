# ADR-0022: Import Diagnostics Architecture

## Status

Accepted

## Context

After analyzing a production import to Andromeda (78,681 records, 13:24 duration), several diagnostic gaps were identified:

1. **No version info** logged at startup - can't correlate issues to specific builds
2. **Bulk operation probe waste** - 117 team records sent to UpsertMultiple before detecting it's unsupported
3. **Limited field-level context** - errors say "Does Not Exist" but don't identify which lookup field failed
4. **Error report lacks execution context** - can't determine what CLI options were used

These gaps make troubleshooting difficult, especially when analyzing import logs after the fact.

## Decision

### 1. Startup Version Header

CLI now outputs diagnostic header to stderr at startup:

```
PPDS CLI v1.2.3 (SDK v1.2.3, .NET 8.0.1)
Platform: Microsoft Windows 10.0.22631
```

**Rationale:** Per ADR-0008, stderr is for status/progress. Version info enables correlating issues to specific builds without affecting stdout data streams.

**Skip conditions:** `--help`, `--version`, or no arguments (to avoid noise in help output).

### 2. Bulk Operation Probe-Once Pattern

Instead of sending all records to bulk API and falling back on failure, we now:

1. **Probe with 1 record** first
2. If probe fails with "not enabled on entity", cache result and use individual operations
3. If probe succeeds, process remaining records in bulk

```csharp
// Old: Waste 117 records before detecting failure
bulkResult = await _bulkExecutor.UpsertMultipleAsync(entityName, allRecords, ...);
if (IsBulkNotSupportedFailure(bulkResult, allRecords.Count))
    bulkResult = await ExecuteIndividualOperationsAsync(entityName, allRecords, ...);

// New: Waste only 1 record (probe)
var probeResult = await _bulkExecutor.UpsertMultipleAsync(entityName, firstRecord, ...);
if (IsBulkNotSupportedFailure(probeResult, 1))
{
    _bulkNotSupportedEntities[entityName] = true;
    bulkResult = await ExecuteIndividualOperationsAsync(entityName, allRecords, ...);
}
```

**Cache scope:** Per-import-session only. Not persisted because entity capabilities can change between environments/deployments.

**Alternatives considered:**
- **Hardcoded known-unsupported list:** Rejected - requires maintenance, won't catch new entities
- **Metadata query:** Rejected - Microsoft's EntityMetadata SDK doesn't expose bulk operation capability

### 3. Error Report v1.1 with Execution Context

Error report JSON now includes execution context:

```json
{
  "version": "1.1",
  "executionContext": {
    "cliVersion": "1.2.3",
    "sdkVersion": "1.2.3",
    "runtimeVersion": "8.0.1",
    "platform": "Microsoft Windows 10.0.22631",
    "importMode": "Upsert",
    "stripOwnerFields": true,
    "bypassPlugins": false,
    "userMappingProvided": false
  },
  // ... existing fields
}
```

**Backward compatibility:** Version field bumped from "1.0" to "1.1". Consumers should check version and ignore unknown fields.

### 4. Field-Level Error Context

`BulkOperationError` now includes:

```csharp
public class BulkOperationError
{
    // ... existing fields

    /// <summary>Field name extracted from error message, if identifiable.</summary>
    public string? FieldName { get; init; }

    /// <summary>Safe description of field value (e.g., "msdyn_postalcode:1798cdb9...").</summary>
    public string? FieldValueDescription { get; init; }
}
```

**Extraction patterns:**
- `attribute 'fieldname'` or `field 'fieldname'`
- `'fieldname' contains invalid data`

**Value description:** Sanitized to avoid PII - shows type and truncated ID only.

## Consequences

### Positive

- Version info enables correlating issues to specific builds
- Bulk probe reduces wasted records from N to 1 for unsupported entities
- Field-level context makes debugging lookup failures easier
- Execution context helps reproduce and understand import behavior

### Negative

- Version header adds 2 lines of output to all CLI commands
- Probe pattern adds latency for first record of each entity (minimal)
- Error report JSON is slightly larger with new fields

### Neutral

- Existing API surface unchanged (new properties are optional)
- Cache is instance-scoped, no persistence complexity

## Related ADRs

- **ADR-0008**: CLI Output Architecture (stderr for version header)
- **ADR-0020**: Import Error Reporting (this extends that work)
