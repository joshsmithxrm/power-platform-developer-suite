namespace PPDS.Dataverse.BulkOperations
{
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
        /// Gets or sets the business logic to bypass during execution.
        /// This is the recommended approach over <see cref="BypassCustomPluginExecution"/>.
        /// <para>
        /// Options:
        /// <list type="bullet">
        /// <item><c>null</c> - No bypass (default)</item>
        /// <item><c>"CustomSync"</c> - Bypass synchronous plugins and workflows</item>
        /// <item><c>"CustomAsync"</c> - Bypass asynchronous plugins and workflows</item>
        /// <item><c>"CustomSync,CustomAsync"</c> - Bypass all custom logic</item>
        /// </list>
        /// </para>
        /// Requires the prvBypassCustomBusinessLogic privilege.
        /// Default: null (no bypass)
        /// </summary>
        public string? BypassBusinessLogicExecution { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to bypass custom synchronous plugin execution.
        /// Consider using <see cref="BypassBusinessLogicExecution"/> instead for more control.
        /// Requires the prvBypassCustomPlugins privilege.
        /// Default: false
        /// </summary>
        public bool BypassCustomPluginExecution { get; set; } = false;

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
}
