# PPDS.Cli Services: Export Service

## Overview

The Export Service provides data export capabilities for query results, supporting multiple formats (CSV, TSV, JSON) and clipboard operations. It is designed to be UI-agnostic, accepting `DataTable` inputs and writing to streams, enabling use from CLI commands, TUI wizards, and RPC handlers. The service implements proper CSV escaping rules, configurable options for headers and date formats, and progress reporting for large exports.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `IExportService` | Export data to various formats (CSV, TSV, JSON, clipboard) |
| `IOperationProgress` | UI-agnostic progress reporting callback |

### Classes

| Class | Purpose |
|-------|---------|
| `ExportService` | Stream-based export implementation with format-specific handling |
| `NullOperationProgress` | No-op progress reporter singleton for when progress is not needed |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `ExportOptions` | Configuration for export operations (headers, date format, encoding, BOM, row/column selection) |
| `QueryColumnType` | Column type metadata for JSON type restoration (from PPDS.Dataverse) |

## Behaviors

### Normal Operation

1. **CSV/TSV Export**: Writes delimited data with proper escaping to output stream
2. **JSON Export**: Serializes records as JSON array with type restoration from metadata
3. **Clipboard Formatting**: Returns tab-separated text optimized for spreadsheet paste
4. **Progress Reporting**: Reports status, progress percentage, and completion for large exports

### Export Formats

| Format | Method | Delimiter | Escaping |
|--------|--------|-----------|----------|
| CSV | `ExportCsvAsync` | Comma (`,`) | RFC 4180 compliant |
| TSV | `ExportTsvAsync` | Tab (`\t`) | RFC 4180 style quoting |
| JSON | `ExportJsonAsync` | N/A | Standard JSON serialization |
| Clipboard | `FormatForClipboard` | Tab (`\t`) | Tabs/newlines replaced with spaces |

### CSV Escaping Rules (RFC 4180)

| Condition | Action |
|-----------|--------|
| Field contains delimiter | Wrap in double quotes |
| Field contains double quote | Double the quote (`"` → `""`) |
| Field contains newline | Wrap in double quotes |
| Field contains carriage return | Wrap in double quotes |

### Lifecycle

- **Initialization**: Constructor requires `ILogger<ExportService>`
- **Operation**: Stateless - each export method operates independently
- **Cleanup**: Leaves stream open after export (`leaveOpen: true`)

### Cell Value Formatting

| Type | Format |
|------|--------|
| `DateTime` | Configurable via `DateTimeFormat` option (default: `yyyy-MM-dd HH:mm:ss`) |
| `DateTimeOffset` | Same as DateTime |
| `bool` | `"true"` / `"false"` |
| `decimal`, `double`, `float` | General format (`"G"`) |
| `null`, `DBNull` | Empty string |
| Other | `ToString()` |

### JSON Type Restoration

When exporting to JSON with `columnTypes` metadata:

| QueryColumnType | JSON Type |
|-----------------|-----------|
| `Guid` | String (GUID format) |
| `Integer` | Number (int) |
| `BigInt` | Number (long) |
| `Decimal`, `Money` | Number (decimal) |
| `Double` | Number (double) |
| `Boolean` | Boolean |
| `DateTime` | String (ISO 8601 format) |
| Others | String |

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Empty DataTable | Writes only headers (if enabled) | No error |
| Null value in cell | Writes empty string | Handled gracefully |
| DBNull value in cell | Writes empty string | Same as null |
| Invalid row index | Skips silently | No exception |
| Invalid column index | Skips silently | Defensive handling |
| Large export (>100 rows) | Reports progress every 100 rows | Prevents UI freeze |
| Cancellation requested | Throws `OperationCanceledException` | Checks each row |
| Null logger | Throws `ArgumentNullException` | Constructor validation |
| Null options | Uses default `ExportOptions` | Safe default |
| Null progress | No-op | Null-conditional calls |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `ArgumentNullException` | Null logger in constructor | Provide logger |
| `OperationCanceledException` | Cancellation token triggered | Normal cancellation flow |
| `IOException` | Stream write failure | Check disk space, permissions |

## Dependencies

- **Internal**:
  - `PPDS.Cli.Infrastructure.IOperationProgress` - Progress reporting interface
  - `PPDS.Dataverse.Query.QueryColumnType` - Column type metadata for JSON export
- **External**:
  - `System.Data.DataTable` - Input data structure
  - `System.Text.Json` - JSON serialization
  - `Microsoft.Extensions.Logging` - Logging abstraction

## Configuration

### ExportOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `IncludeHeaders` | `bool` | `true` | Whether to include column headers in output |
| `DateTimeFormat` | `string` | `"yyyy-MM-dd HH:mm:ss"` | Format string for DateTime values |
| `Encoding` | `Encoding` | `UTF8` | Text encoding for output |
| `IncludeBom` | `bool` | `true` | Whether to include BOM for UTF-8 (Excel compatibility) |
| `ColumnIndices` | `IReadOnlyList<int>?` | `null` | Specific columns to export (null = all) |
| `RowIndices` | `IReadOnlyList<int>?` | `null` | Specific rows to export (null = all) |

### StreamWriter Configuration

- Buffer size: 65,536 bytes (64KB)
- Leaves stream open: Yes
- UTF-8 encoding with configurable BOM

## Thread Safety

- Service is stateless and thread-safe for concurrent export operations
- Each export creates its own `StreamWriter`
- No shared mutable state between calls
- Progress reporting callbacks should be thread-safe if called from multiple threads

## Progress Reporting

### IOperationProgress Interface

| Method | Purpose |
|--------|---------|
| `ReportStatus(message)` | Initial status message (e.g., "Exporting 500 rows...") |
| `ReportProgress(current, total, message?)` | Progress update during operation |
| `ReportProgress(fraction, message?)` | Progress as percentage (0.0-1.0) |
| `ReportComplete(message)` | Completion message (e.g., "Exported 500 rows.") |
| `ReportError(message)` | Error notification |

### Progress Frequency

- Status reported at start of export
- Progress reported every 100 rows (for exports >100 rows)
- Completion reported at end

## Related

- [PPDS.Cli Services: Application Services](01-application-services.md) - Architectural context
- [ADR-0015: Application Service Layer](../docs/adr/0015_APPLICATION_SERVICE_LAYER.md) - Service layer design
- [ADR-0025: UI-Agnostic Progress](../docs/adr/0025_UI_AGNOSTIC_PROGRESS.md) - Progress reporting pattern

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Cli/Services/Export/IExportService.cs` | Interface and ExportOptions definition |
| `src/PPDS.Cli/Services/Export/ExportService.cs` | Implementation with CSV, TSV, JSON, clipboard support |
| `src/PPDS.Cli/Infrastructure/IOperationProgress.cs` | Progress reporting interface and NullOperationProgress |
| `src/PPDS.Dataverse/Query/QueryColumnType.cs` | Column type enumeration for JSON type restoration |
| `tests/PPDS.Cli.Tests/Services/Export/ExportServiceTests.cs` | Comprehensive unit tests |
