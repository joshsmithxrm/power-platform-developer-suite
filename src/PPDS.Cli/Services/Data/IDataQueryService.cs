using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// Service for resolving and fetching Dataverse records used by data mutation commands
/// (Delete, Truncate, Update). All pool access and FetchXML/QueryExpression logic
/// lives here — commands are thin wrappers that only own UI concerns.
/// </summary>
public interface IDataQueryService
{
    /// <summary>
    /// Resolves a single record ID from an alternate-key string of the form
    /// <c>field1=value1,field2=value2</c>.
    /// </summary>
    /// <param name="entity">Logical name of the entity.</param>
    /// <param name="keyString">Comma-separated field=value pairs that uniquely identify the record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record GUID, or <see langword="null"/> if no match found.</returns>
    /// <exception cref="PPDS.Cli.Infrastructure.Errors.PpdsException">
    /// Thrown with <c>Query.ExecutionFailed</c> when the Dataverse call fails.
    /// </exception>
    Task<Guid?> ResolveByAlternateKeyAsync(
        string entity,
        string keyString,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a SQL WHERE clause filter against the entity and returns matching record IDs.
    /// Uses QueryParser + FetchXmlGenerator to transpile the SQL fragment.
    /// </summary>
    /// <param name="entity">Logical name of the entity.</param>
    /// <param name="whereFilter">SQL WHERE expression (without the <c>WHERE</c> keyword).</param>
    /// <param name="limit">Optional upper bound on returned records (uses <c>SELECT TOP N+1</c> for over-limit detection).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching record GUIDs.</returns>
    /// <exception cref="PPDS.Cli.Infrastructure.Errors.PpdsException">
    /// Thrown with <c>Query.ExecutionFailed</c> when the Dataverse call fails,
    /// or <c>Query.ParseError</c> when the SQL cannot be parsed.
    /// </exception>
    Task<IReadOnlyList<Guid>> QueryIdsByFilterAsync(
        string entity,
        string whereFilter,
        int? limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a batch of record IDs for the truncate loop (primary-key column only, no filter).
    /// </summary>
    /// <param name="entity">Logical name of the entity.</param>
    /// <param name="batchSize">Maximum number of IDs to return in this batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of record GUIDs (may be empty when the entity is fully deleted).</returns>
    /// <exception cref="PPDS.Cli.Infrastructure.Errors.PpdsException">
    /// Thrown with <c>Query.ExecutionFailed</c> when the Dataverse call fails.
    /// </exception>
    Task<IReadOnlyList<Guid>> FetchBatchIdsAsync(
        string entity,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total number of records in the entity using an aggregate FetchXML count.
    /// </summary>
    /// <param name="entity">Logical name of the entity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Record count (0 when the entity is empty).</returns>
    /// <exception cref="PPDS.Cli.Infrastructure.Errors.PpdsException">
    /// Thrown with <c>Query.ExecutionFailed</c> when the Dataverse call fails.
    /// </exception>
    Task<int> CountRecordsAsync(
        string entity,
        CancellationToken cancellationToken = default);
}
