using System;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Query;

/// <summary>
/// Executes FetchXML queries against Dataverse and returns structured results.
/// </summary>
public interface IQueryExecutor
{
    /// <summary>
    /// Executes a FetchXML query and returns the results.
    /// </summary>
    /// <param name="fetchXml">The FetchXML query to execute.</param>
    /// <param name="pageNumber">Optional page number for paging (1-based). If null, uses page 1.</param>
    /// <param name="pagingCookie">Optional paging cookie from a previous query result for continuation.</param>
    /// <param name="includeCount">Whether to include the total record count in the result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query result containing records, columns, and paging information.</returns>
    Task<QueryResult> ExecuteFetchXmlAsync(
        string fetchXml,
        int? pageNumber = null,
        string? pagingCookie = null,
        bool includeCount = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a FetchXML query with automatic paging, returning all results.
    /// Use with caution for large result sets.
    /// </summary>
    /// <param name="fetchXml">The FetchXML query to execute.</param>
    /// <param name="maxRecords">Maximum total records to retrieve. Default is 5000.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query result containing all records up to maxRecords.</returns>
    Task<QueryResult> ExecuteFetchXmlAllPagesAsync(
        string fetchXml,
        int maxRecords = 5000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total record count for an entity using RetrieveTotalRecordCountRequest.
    /// This is a near-instant metadata read, not a full table scan.
    /// </summary>
    /// <param name="entityLogicalName">The logical name of the entity to count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total count, or null if not supported for this entity.</returns>
    Task<long?> GetTotalRecordCountAsync(
        string entityLogicalName,
        CancellationToken cancellationToken = default)
    {
        // Default implementation returns null (not supported) so existing
        // implementations don't break. Override in concrete classes to enable.
        return Task.FromResult<long?>(null);
    }

    /// <summary>
    /// Gets the min and max createdon dates for an entity, used for aggregate partitioning.
    /// </summary>
    /// <param name="entityLogicalName">The logical name of the entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of (Min, Max) DateTime values, or (null, null) if not available.</returns>
    Task<(DateTime? Min, DateTime? Max)> GetMinMaxCreatedOnAsync(
        string entityLogicalName,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<(DateTime?, DateTime?)>((null, null));
    }
}
