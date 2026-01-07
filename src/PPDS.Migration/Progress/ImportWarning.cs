namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Represents a warning that occurred during import.
    /// </summary>
    public class ImportWarning
    {
        /// <summary>
        /// Gets or sets the warning code.
        /// </summary>
        /// <remarks>
        /// Standard codes:
        /// <list type="bullet">
        ///   <item><c>BULK_NOT_SUPPORTED</c> - Entity fell back to individual operations</item>
        ///   <item><c>COLUMN_SKIPPED</c> - Column in source but not in target</item>
        ///   <item><c>SCHEMA_MISMATCH</c> - Non-fatal schema differences</item>
        ///   <item><c>USER_MAPPING_FALLBACK</c> - User reference fell back to current user</item>
        ///   <item><c>PLUGIN_REENABLE_FAILED</c> - Failed to re-enable plugin steps</item>
        /// </list>
        /// </remarks>
        public string Code { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the human-readable warning message.
        /// </summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the affected entity (optional).
        /// </summary>
        public string? Entity { get; init; }

        /// <summary>
        /// Gets or sets the impact assessment (optional).
        /// </summary>
        /// <example>"20 records affected", "Reduced throughput"</example>
        public string? Impact { get; init; }
    }

    /// <summary>
    /// Standard warning codes for import operations.
    /// </summary>
    public static class ImportWarningCodes
    {
        /// <summary>Entity does not support bulk operations, fell back to individual operations.</summary>
        public const string BulkNotSupported = "BULK_NOT_SUPPORTED";

        /// <summary>Column in source but not in target (skipped).</summary>
        public const string ColumnSkipped = "COLUMN_SKIPPED";

        /// <summary>Non-fatal schema differences detected.</summary>
        public const string SchemaMismatch = "SCHEMA_MISMATCH";

        /// <summary>User reference fell back to current user.</summary>
        public const string UserMappingFallback = "USER_MAPPING_FALLBACK";

        /// <summary>Failed to re-enable plugin steps after import.</summary>
        public const string PluginReenableFailed = "PLUGIN_REENABLE_FAILED";
    }
}
