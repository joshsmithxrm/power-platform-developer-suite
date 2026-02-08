using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query;

namespace PPDS.Dataverse.Metadata;

/// <summary>
/// Executes metadata queries against Dataverse, returning results as QueryRow format.
/// Maps virtual metadata tables to appropriate Dataverse metadata API calls.
/// </summary>
public interface IMetadataQueryExecutor
{
    /// <summary>
    /// Queries metadata for a virtual table.
    /// </summary>
    /// <param name="tableName">The metadata table name (entity, attribute, relationship_1_n, etc.).</param>
    /// <param name="requestedColumns">Columns to return (null = all).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The metadata rows.</returns>
    Task<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>> QueryMetadataAsync(
        string tableName,
        IReadOnlyList<string>? requestedColumns = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the available columns for a metadata virtual table.
    /// </summary>
    IReadOnlyList<string> GetAvailableColumns(string tableName);

    /// <summary>
    /// Returns true if the table name is a known metadata virtual table.
    /// </summary>
    bool IsMetadataTable(string schemaQualifiedName);
}
