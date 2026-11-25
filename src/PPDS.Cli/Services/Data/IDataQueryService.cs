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
    /// <returns>The record GUID when exactly one record matches, or <see langword="null"/> if none matches.</returns>
    /// <exception cref="PPDS.Cli.Infrastructure.Errors.PpdsException">
    /// Thrown with <c>Validation.KeyMatchesMultipleRecords</c> when more than one record matches (two rows are asked
    /// for; the message says whether the columns are a defined alternate key of the table), and with
    /// <c>Query.ExecutionFailed</c> when the Dataverse call fails.
    /// </exception>
    Task<Guid?> ResolveByAlternateKeyAsync(
        string entity,
        string keyString,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the ids of the records of <paramref name="entity"/> that the SQL condition <paramref name="whereFilter"/>
    /// selects, reading every page. The condition's structure is admitted by <see cref="DataFilter.Translate"/> before
    /// anything is read; then the table's column metadata is read and every column and value is admitted for its
    /// column's type (<see cref="AdmittedDataFilter.Bind"/>) before the first record is read.
    /// </summary>
    /// <param name="entity">Logical name of the entity.</param>
    /// <param name="whereFilter">SQL WHERE expression (without the <c>WHERE</c> keyword).</param>
    /// <param name="limit">
    /// Optional upper bound. Reading stops at the end of the first page on which more than <paramref name="limit"/> ids
    /// have been found, so the list then holds more than <paramref name="limit"/> ids (up to a page more) but not
    /// necessarily every match: a caller that refuses on it can say only that at least that many records match.
    /// Without it, every matching record is read.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching record GUIDs, each once.</returns>
    /// <exception cref="PPDS.Cli.Infrastructure.Errors.PpdsException">
    /// Thrown with <c>Validation.FilterNotSupported</c> (or another code of <see cref="DataFilter.Translate"/>) and with
    /// <c>Query.FilterMetadataUnavailable</c> before any record is read, or <c>Query.ExecutionFailed</c> when the
    /// Dataverse call fails or the pages are inconsistent.
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
