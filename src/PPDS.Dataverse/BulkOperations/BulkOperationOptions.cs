using System;

namespace PPDS.Dataverse.BulkOperations;

/// <summary>
/// Configuration options for bulk operations.
/// </summary>
public class BulkOperationOptions
{
    /// <summary>
    /// Minimum allowed batch size (inclusive).
    /// </summary>
    public const int MinBatchSize = 1;

    /// <summary>
    /// Maximum allowed batch size (inclusive). Dataverse rejects values greater than 1000
    /// with an opaque error that can make the whole batch fail silently, so we validate
    /// up-front.
    /// </summary>
    public const int MaxBatchSize = 1000;

    private int _batchSize = 100;

    /// <summary>
    /// Gets or sets the number of records per batch.
    /// Must be between <see cref="MinBatchSize"/> and <see cref="MaxBatchSize"/> inclusive.
    /// Benchmarks show 100 is optimal for both standard and elastic tables.
    /// Default: 100
    /// </summary>
    /// <exception cref="BulkOperationValidationException">
    /// Thrown when the assigned value is outside the allowed range.
    /// </exception>
    public int BatchSize
    {
        get => _batchSize;
        set
        {
            if (value < MinBatchSize || value > MaxBatchSize)
            {
                throw new BulkOperationValidationException(
                    BulkOperationErrorCode.InvalidBatchSize,
                    $"BatchSize must be between {MinBatchSize} and {MaxBatchSize} (got {value}). " +
                    "Dataverse rejects larger batches with an opaque error that can cause the whole batch to fail silently.",
                    nameof(BatchSize));
            }

            _batchSize = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the target is an elastic table (Cosmos DB-backed).
    /// <para>
    /// When false (default, for standard SQL-backed tables):
    /// <list type="bullet">
    /// <item>Create/Update/Upsert: Uses all-or-nothing batch semantics (any error fails entire batch)</item>
    /// <item>Delete: Uses ExecuteMultiple with individual DeleteRequests</item>
    /// </list>
    /// </para>
    /// <para>
    /// When true (for elastic tables):
    /// <list type="bullet">
    /// <item>All operations support partial success with per-record error details</item>
    /// <item>Delete uses native DeleteMultiple API</item>
    /// <item>Consider reducing BatchSize to 100 for optimal performance</item>
    /// </list>
    /// </para>
    /// Default: false
    /// </summary>
    public bool ElasticTable { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to continue after individual record failures.
    /// Only applies to Delete operations on standard tables (ElasticTable = false).
    /// Elastic tables always support partial success automatically.
    /// Default: true
    /// </summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>
    /// Gets or sets which custom business logic to bypass during execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires the <c>prvBypassCustomBusinessLogic</c> privilege.
    /// By default, only System Administrators have this privilege.
    /// </para>
    /// <para>
    /// This bypasses custom plugins and workflows only. Microsoft's core system plugins
    /// and solution workflows are NOT bypassed.
    /// </para>
    /// <para>
    /// Does not affect Power Automate flows - use <see cref="BypassPowerAutomateFlows"/> for that.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Bypass sync plugins only (for performance during bulk loads)
    /// options.BypassCustomLogic = CustomLogicBypass.Synchronous;
    ///
    /// // Bypass async plugins only (prevent system job backlog)
    /// options.BypassCustomLogic = CustomLogicBypass.Asynchronous;
    ///
    /// // Bypass all custom logic
    /// options.BypassCustomLogic = CustomLogicBypass.All;
    ///
    /// // Combine with Power Automate bypass
    /// options.BypassCustomLogic = CustomLogicBypass.All;
    /// options.BypassPowerAutomateFlows = true;
    /// </code>
    /// </example>
    public CustomLogicBypass BypassCustomLogic { get; set; } = CustomLogicBypass.None;

    /// <summary>
    /// Gets or sets a value indicating whether to bypass Power Automate flows.
    /// When true, flows using "When a row is added, modified or deleted" triggers will not execute.
    /// No special privilege is required.
    /// Default: false
    /// </summary>
    public bool BypassPowerAutomateFlows { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to suppress duplicate detection.
    /// Default: false
    /// </summary>
    public bool SuppressDuplicateDetection { get; set; } = false;

    /// <summary>
    /// Gets or sets a tag value passed to plugin execution context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plugins can access this value via <c>context.SharedVariables["tag"]</c>.
    /// </para>
    /// <para>
    /// Useful for:
    /// <list type="bullet">
    /// <item>Identifying records created by bulk operations in plugin logic</item>
    /// <item>Audit trails (e.g., "Migration-2025-Q4", "ETL-Job-123")</item>
    /// <item>Conditional plugin behavior based on data source</item>
    /// </list>
    /// </para>
    /// <para>
    /// No special privileges required.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// options.Tag = "BulkImport-2025-12-24";
    ///
    /// // In a plugin:
    /// if (context.SharedVariables.TryGetValue("tag", out var tag)
    ///     &amp;&amp; tag?.ToString()?.StartsWith("BulkImport") == true)
    /// {
    ///     // Skip audit logging for bulk imports
    ///     return;
    /// }
    /// </code>
    /// </example>
    public string? Tag { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of batches to process in parallel.
    /// <para>
    /// When null (default), uses the ServiceClient's RecommendedDegreesOfParallelism
    /// which comes from the x-ms-dop-hint response header from Dataverse.
    /// </para>
    /// <para>
    /// Set to 1 for sequential processing, or a specific value to override
    /// Microsoft's recommendation.
    /// </para>
    /// Default: null (use RecommendedDegreesOfParallelism)
    /// </summary>
    public int? MaxParallelBatches { get; set; } = null;
}

/// <summary>
/// Exception thrown when bulk operation configuration fails validation.
/// </summary>
/// <remarks>
/// <para>
/// This exception lives in the Dataverse layer (which cannot reference PPDS.Cli).
/// The CLI's <c>ExceptionMapper</c> recognizes <see cref="BulkOperationValidationException"/>
/// and maps it to the corresponding <c>PpdsException</c> with the matching
/// <c>BulkOperation.*</c> error code.
/// </para>
/// <para>
/// Mirrors the precedent set by <c>PPDS.Dataverse.Query.Execution.QueryExecutionException</c>.
/// </para>
/// </remarks>
public class BulkOperationValidationException : ArgumentException
{
    /// <summary>
    /// Structured error code for programmatic handling.
    /// Uses the <c>BulkOperation.*</c> prefix (see <see cref="BulkOperationErrorCode"/>).
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Creates a new bulk operation validation exception.
    /// </summary>
    /// <param name="errorCode">The structured error code (use <see cref="BulkOperationErrorCode"/> constants).</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="paramName">Optional parameter name that failed validation.</param>
    public BulkOperationValidationException(string errorCode, string message, string? paramName = null)
        : base(message, paramName)
    {
        ErrorCode = errorCode ?? throw new ArgumentNullException(nameof(errorCode));
    }
}

/// <summary>
/// Structured error codes for bulk operation validation failures.
/// </summary>
/// <remarks>
/// Duplicated here (not referenced from PPDS.Cli) because PPDS.Dataverse cannot
/// reference PPDS.Cli. The CLI's ExceptionMapper bridges them.
/// </remarks>
public static class BulkOperationErrorCode
{
    /// <summary>BatchSize is out of the allowed range (1 to 1000).</summary>
    public const string InvalidBatchSize = "BulkOperation.InvalidBatchSize";
}
