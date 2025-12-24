namespace PPDS.Dataverse.BulkOperations;

/// <summary>
/// Configuration options for bulk operations.
/// </summary>
public class BulkOperationOptions
{
    /// <summary>
    /// Gets or sets the number of records per batch.
    /// Benchmarks show 100 is optimal for both standard and elastic tables.
    /// Default: 100
    /// </summary>
    public int BatchSize { get; set; } = 100;

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
