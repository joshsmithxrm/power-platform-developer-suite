using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Query;

/// <summary>
/// Executes SQL queries against Dataverse's TDS Endpoint (read-only replica)
/// using the SQL Server wire protocol on port 5558.
/// </summary>
public interface ITdsQueryExecutor
{
    /// <summary>
    /// Executes a SQL query against the Dataverse TDS Endpoint and returns structured results.
    /// </summary>
    /// <param name="sql">The SQL SELECT query to execute.</param>
    /// <param name="maxRows">Optional maximum number of rows to return. If null, returns all rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query result containing records, columns, and execution metadata.</returns>
    /// <remarks>
    /// The TDS Endpoint is read-only. Only SELECT statements are supported.
    /// The connection uses the org URL on port 5558 with an MSAL access token.
    /// </remarks>
    Task<QueryResult> ExecuteSqlAsync(
        string sql,
        int? maxRows = null,
        CancellationToken cancellationToken = default);
}
