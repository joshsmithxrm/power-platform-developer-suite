# ADR-0020: Import Error Reporting

## Status

Accepted

## Context

During large data imports, users see failure counts (e.g., "15,400 failed") but have no way to determine:
- **WHY** records failed (error messages buried in verbose logs)
- **WHICH** records failed (batch position shown, not record IDs)
- **HOW** to fix/retry (no actionable output)

The existing error flow captured `RecordId` in `BulkOperationError` but dropped it when converting to `MigrationError`, making it impossible to correlate failures back to source data.

## Decision

### 1. Preserve RecordId Through Error Pipeline

`MigrationError` now includes `RecordId` (a GUID identifier, not PII):

```csharp
public class MigrationError
{
    public Guid? RecordId { get; set; }  // NEW
    // ... existing properties
}
```

### 2. Structured Error Report Output

New `--error-report <file>` CLI option writes a comprehensive JSON report:

```json
{
  "version": "1.0",
  "generatedAt": "2026-01-05T20:30:00Z",
  "sourceFile": "data.zip",
  "targetEnvironment": "https://org.crm.dynamics.com",
  "summary": {
    "totalRecords": 43710,
    "successCount": 28310,
    "failureCount": 15400,
    "duration": "00:06:30",
    "errorPatterns": { "POOL_EXHAUSTION": 15400 }
  },
  "entitiesSummary": [...],
  "errors": [...],
  "retryManifest": {
    "version": "1.0",
    "failedRecordsByEntity": {
      "msdyn_postalcode": ["guid1", "guid2", ...]
    }
  }
}
```

### 3. Enhanced Console Output

- Per-entity failure breakdown at completion
- Sample RecordIds shown (truncated for readability)
- Error pattern detection with actionable suggestions

### 4. Retry Manifest

Embedded in the error report, the retry manifest groups failed RecordIds by entity, enabling future retry workflows.

## Error Patterns

The following patterns are automatically detected:

| Pattern | Description | Suggestion |
|---------|-------------|------------|
| `POOL_EXHAUSTION` | Connection pool exhausted | Reduce parallelism or add connections |
| `MISSING_USER` | systemuser reference not found | Use `--strip-owner-fields` |
| `MISSING_TEAM` | team reference not found | Create team in target |
| `MISSING_REFERENCE` | Lookup reference not found | Import referenced entity first |
| `MISSING_PARENT` | Self-referential parent missing | Multi-pass import |
| `DUPLICATE_RECORD` | Record already exists | Use `--mode Upsert` |
| `PERMISSION_DENIED` | Insufficient privileges | Check service principal roles |
| `REQUIRED_FIELD` | Required field missing | Map source fields |
| `BULK_NOT_SUPPORTED` | Bulk API not enabled | Falls back automatically |

## Consequences

### Positive

- Users can immediately understand why imports fail
- Failed records can be identified by ID for debugging
- Error reports enable automated retry workflows
- Pattern detection provides actionable guidance

### Negative

- Error report files can be large for many failures
- RecordId inclusion requires memory to track all errors

### Neutral

- Existing API surface unchanged (new properties are optional)
- Console output behavior preserved, enhanced with more detail

## Related ADRs

- **ADR-0008**: CLI Output Architecture (stderr for progress)
- **ADR-0019**: Pool-Managed Concurrency (pool exhaustion handling)
