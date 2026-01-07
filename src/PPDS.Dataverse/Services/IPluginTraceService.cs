using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for querying and managing plugin trace logs.
/// </summary>
public interface IPluginTraceService
{
    /// <summary>
    /// Lists plugin trace logs with optional filtering.
    /// </summary>
    /// <param name="filter">Optional filter criteria.</param>
    /// <param name="top">Maximum number of results (default: 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of plugin trace summaries.</returns>
    Task<List<PluginTraceInfo>> ListAsync(
        PluginTraceFilter? filter = null,
        int top = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific plugin trace by ID with full details.
    /// </summary>
    /// <param name="traceId">The trace ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The trace details, or null if not found.</returns>
    Task<PluginTraceDetail?> GetAsync(
        Guid traceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all traces related by correlation ID.
    /// </summary>
    /// <param name="correlationId">The correlation ID.</param>
    /// <param name="top">Maximum number of results (default: 1000).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of related trace summaries.</returns>
    Task<List<PluginTraceInfo>> GetRelatedAsync(
        Guid correlationId,
        int top = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a timeline hierarchy from traces with the given correlation ID.
    /// </summary>
    /// <param name="correlationId">The correlation ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Root timeline nodes with nested children.</returns>
    Task<List<TimelineNode>> BuildTimelineAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a single plugin trace by ID.
    /// </summary>
    /// <param name="traceId">The trace ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if deleted, false if not found.</returns>
    Task<bool> DeleteAsync(
        Guid traceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes multiple plugin traces by ID.
    /// </summary>
    /// <param name="traceIds">The trace IDs to delete.</param>
    /// <param name="progress">Progress callback reporting count of deleted traces.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Count of deleted traces.</returns>
    Task<int> DeleteByIdsAsync(
        IEnumerable<Guid> traceIds,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes plugin traces matching the filter criteria.
    /// </summary>
    /// <param name="filter">Filter criteria for traces to delete.</param>
    /// <param name="progress">Progress callback reporting count of deleted traces.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Count of deleted traces.</returns>
    Task<int> DeleteByFilterAsync(
        PluginTraceFilter filter,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all plugin traces.
    /// </summary>
    /// <param name="progress">Progress callback reporting count of deleted traces.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Count of deleted traces.</returns>
    Task<int> DeleteAllAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes plugin traces older than the specified age.
    /// </summary>
    /// <param name="olderThan">Delete traces older than this timespan.</param>
    /// <param name="progress">Progress callback reporting count of deleted traces.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Count of deleted traces.</returns>
    Task<int> DeleteOlderThanAsync(
        TimeSpan olderThan,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current plugin trace logging setting.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current trace setting.</returns>
    Task<PluginTraceSettings> GetSettingsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the plugin trace logging setting.
    /// </summary>
    /// <param name="setting">The new trace setting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetSettingsAsync(
        PluginTraceLogSetting setting,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts traces matching the filter criteria (for dry-run operations).
    /// </summary>
    /// <param name="filter">Optional filter criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Count of matching traces.</returns>
    Task<int> CountAsync(
        PluginTraceFilter? filter = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Plugin trace log summary information for list views.
/// </summary>
public record PluginTraceInfo
{
    /// <summary>Gets the trace ID.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the plugin type name (class name).</summary>
    public required string TypeName { get; init; }

    /// <summary>Gets the message name (Create, Update, etc.).</summary>
    public string? MessageName { get; init; }

    /// <summary>Gets the primary entity name.</summary>
    public string? PrimaryEntity { get; init; }

    /// <summary>Gets the execution mode.</summary>
    public PluginTraceMode Mode { get; init; }

    /// <summary>Gets the operation type.</summary>
    public PluginTraceOperationType OperationType { get; init; }

    /// <summary>Gets the execution depth (1 = top level).</summary>
    public int Depth { get; init; }

    /// <summary>Gets the creation date/time.</summary>
    public required DateTime CreatedOn { get; init; }

    /// <summary>Gets the execution duration in milliseconds.</summary>
    public int? DurationMs { get; init; }

    /// <summary>Gets whether the trace has an exception.</summary>
    public bool HasException { get; init; }

    /// <summary>Gets the correlation ID for related traces.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>Gets the request ID.</summary>
    public Guid? RequestId { get; init; }

    /// <summary>Gets the plugin step ID.</summary>
    public Guid? PluginStepId { get; init; }
}

/// <summary>
/// Full plugin trace log details.
/// </summary>
public sealed record PluginTraceDetail : PluginTraceInfo
{
    /// <summary>Gets the constructor duration in milliseconds.</summary>
    public int? ConstructorDurationMs { get; init; }

    /// <summary>Gets the execution start time.</summary>
    public DateTime? ExecutionStartTime { get; init; }

    /// <summary>Gets the constructor start time.</summary>
    public DateTime? ConstructorStartTime { get; init; }

    /// <summary>Gets the exception details (stack trace, etc.).</summary>
    public string? ExceptionDetails { get; init; }

    /// <summary>Gets the message block (trace output).</summary>
    public string? MessageBlock { get; init; }

    /// <summary>Gets the unsecured configuration.</summary>
    public string? Configuration { get; init; }

    /// <summary>Gets the secured configuration.</summary>
    public string? SecureConfiguration { get; init; }

    /// <summary>Gets the profile XML.</summary>
    public string? Profile { get; init; }

    /// <summary>Gets the organization ID.</summary>
    public Guid? OrganizationId { get; init; }

    /// <summary>Gets the persistence key.</summary>
    public Guid? PersistenceKey { get; init; }

    /// <summary>Gets whether the trace was system-created.</summary>
    public bool IsSystemCreated { get; init; }

    /// <summary>Gets the created by user ID.</summary>
    public Guid? CreatedById { get; init; }

    /// <summary>Gets the created on behalf of user ID.</summary>
    public Guid? CreatedOnBehalfById { get; init; }
}

/// <summary>
/// Filter criteria for querying plugin traces.
/// </summary>
public sealed record PluginTraceFilter
{
    /// <summary>Gets or sets the plugin type name filter (contains).</summary>
    public string? TypeName { get; init; }

    /// <summary>Gets or sets the message name filter.</summary>
    public string? MessageName { get; init; }

    /// <summary>Gets or sets the primary entity filter (contains).</summary>
    public string? PrimaryEntity { get; init; }

    /// <summary>Gets or sets the execution mode filter.</summary>
    public PluginTraceMode? Mode { get; init; }

    /// <summary>Gets or sets the operation type filter.</summary>
    public PluginTraceOperationType? OperationType { get; init; }

    /// <summary>Gets or sets the minimum depth filter.</summary>
    public int? MinDepth { get; init; }

    /// <summary>Gets or sets the maximum depth filter.</summary>
    public int? MaxDepth { get; init; }

    /// <summary>Gets or sets the created after filter.</summary>
    public DateTime? CreatedAfter { get; init; }

    /// <summary>Gets or sets the created before filter.</summary>
    public DateTime? CreatedBefore { get; init; }

    /// <summary>Gets or sets the minimum duration filter (ms).</summary>
    public int? MinDurationMs { get; init; }

    /// <summary>Gets or sets the maximum duration filter (ms).</summary>
    public int? MaxDurationMs { get; init; }

    /// <summary>Gets or sets whether to filter for errors only.</summary>
    public bool? HasException { get; init; }

    /// <summary>Gets or sets the correlation ID filter.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>Gets or sets the request ID filter.</summary>
    public Guid? RequestId { get; init; }

    /// <summary>Gets or sets the plugin step ID filter.</summary>
    public Guid? PluginStepId { get; init; }

    /// <summary>Gets or sets the order by field (default: createdon desc).</summary>
    public string? OrderBy { get; init; }
}

/// <summary>
/// Plugin trace execution mode.
/// </summary>
public enum PluginTraceMode
{
    /// <summary>Synchronous execution (blocks the user transaction).</summary>
    Synchronous = 0,

    /// <summary>Asynchronous execution (background processing).</summary>
    Asynchronous = 1
}

/// <summary>
/// Plugin trace operation type.
/// </summary>
public enum PluginTraceOperationType
{
    /// <summary>Unknown operation type.</summary>
    Unknown = 0,

    /// <summary>Plugin assembly execution.</summary>
    Plugin = 1,

    /// <summary>Custom workflow activity execution.</summary>
    WorkflowActivity = 2
}

/// <summary>
/// Plugin trace logging setting.
/// </summary>
public enum PluginTraceLogSetting
{
    /// <summary>No tracing.</summary>
    Off = 0,

    /// <summary>Log only exceptions.</summary>
    Exception = 1,

    /// <summary>Log all executions.</summary>
    All = 2
}

/// <summary>
/// Plugin trace settings information.
/// </summary>
public sealed record PluginTraceSettings
{
    /// <summary>Gets the current trace logging setting.</summary>
    public required PluginTraceLogSetting Setting { get; init; }

    /// <summary>Gets the display name of the setting.</summary>
    public string SettingName => Setting switch
    {
        PluginTraceLogSetting.Off => "Off",
        PluginTraceLogSetting.Exception => "Exception",
        PluginTraceLogSetting.All => "All",
        _ => "Unknown"
    };
}

/// <summary>
/// A node in the plugin execution timeline hierarchy.
/// </summary>
public sealed record TimelineNode
{
    /// <summary>Gets the trace information for this node.</summary>
    public required PluginTraceInfo Trace { get; init; }

    /// <summary>Gets the child nodes (nested plugin calls).</summary>
    public IReadOnlyList<TimelineNode> Children { get; init; } = Array.Empty<TimelineNode>();

    /// <summary>Gets the depth level in the hierarchy (0 = root).</summary>
    public int HierarchyDepth { get; init; }

    /// <summary>Gets the offset percentage for timeline visualization.</summary>
    public double OffsetPercent { get; init; }

    /// <summary>Gets the width percentage for timeline visualization.</summary>
    public double WidthPercent { get; init; }
}
