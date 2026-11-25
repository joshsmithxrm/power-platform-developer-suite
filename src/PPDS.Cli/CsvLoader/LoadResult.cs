namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Result of a CSV load operation.
/// </summary>
public sealed record LoadResult
{
    /// <summary>
    /// Whether the load completed without errors.
    /// </summary>
    public bool Success => FailureCount == 0;

    /// <summary>
    /// Total number of rows in the CSV file.
    /// </summary>
    public int TotalRows { get; init; }

    /// <summary>
    /// Number of records successfully loaded.
    /// </summary>
    public int SuccessCount { get; init; }

    /// <summary>
    /// Number of records that failed to load.
    /// </summary>
    public int FailureCount { get; init; }

    /// <summary>
    /// Number of records created (for upsert operations).
    /// </summary>
    public int? CreatedCount { get; init; }

    /// <summary>
    /// Number of records updated (for upsert operations).
    /// </summary>
    public int? UpdatedCount { get; init; }

    /// <summary>
    /// Number of rows skipped due to validation errors.
    /// </summary>
    public int SkippedCount { get; init; }

    /// <summary>
    /// Duration of the load operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// List of errors encountered during loading.
    /// </summary>
    public IReadOnlyList<LoadError> Errors { get; init; } = [];

    /// <summary>
    /// List of warnings generated during loading.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Error encountered during CSV loading.
/// </summary>
public sealed record LoadError
{
    /// <summary>
    /// Row number in the CSV file (1-based, excluding header).
    /// </summary>
    public int RowNumber { get; init; }

    /// <summary>
    /// Column name where the error occurred.
    /// </summary>
    public string? Column { get; init; }

    /// <summary>
    /// Error code for categorization.
    /// </summary>
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// The problematic value from the CSV, if applicable.
    /// </summary>
    public string? Value { get; init; }
}

/// <summary>
/// Error codes for CSV loading operations.
/// </summary>
public static class LoadErrorCodes
{
    /// <summary>
    /// CSV file could not be parsed.
    /// </summary>
    public const string CsvParseError = "CSV_PARSE_ERROR";

    /// <summary>
    /// CSV column does not match any entity attribute.
    /// </summary>
    public const string ColumnNotFound = "COLUMN_NOT_FOUND";

    /// <summary>
    /// Value could not be converted to the target attribute type.
    /// </summary>
    public const string TypeCoercionFailed = "TYPE_COERCION_FAILED";

    /// <summary>
    /// Lookup value could not be resolved to a record.
    /// </summary>
    public const string LookupNotResolved = "LOOKUP_NOT_RESOLVED";

    /// <summary>
    /// Lookup value matched multiple records.
    /// </summary>
    public const string LookupDuplicate = "LOOKUP_DUPLICATE";

    /// <summary>
    /// Duplicate alternate key value in CSV.
    /// </summary>
    public const string DuplicateKey = "DUPLICATE_KEY";

    /// <summary>
    /// Dataverse rejected the record.
    /// </summary>
    public const string DataverseError = "DATAVERSE_ERROR";

    /// <summary>
    /// Required field is missing.
    /// </summary>
    public const string RequiredFieldMissing = "REQUIRED_FIELD_MISSING";
}
