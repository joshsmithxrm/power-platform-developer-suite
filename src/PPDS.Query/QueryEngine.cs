namespace PPDS.Query;

/// <summary>
/// Entry point for the PPDS Query Engine.
/// Provides SQL parsing, planning, and execution against Dataverse.
/// </summary>
public static class QueryEngine
{
    /// <summary>Assembly marker for PPDS.Query.</summary>
    public static readonly string Version = typeof(QueryEngine).Assembly
        .GetName().Version?.ToString() ?? "0.0.0";
}
