namespace PPDS.Cli.Services.Query;

/// <summary>
/// The actual execution path used for a query.
/// </summary>
public enum QueryExecutionMode
{
    /// <summary>Query executed via FetchXML against Dataverse Web API.</summary>
    Dataverse,

    /// <summary>Query executed via TDS Endpoint (SQL Server wire protocol).</summary>
    Tds
}
