namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// Severity level for SQL diagnostics.
/// </summary>
public enum SqlDiagnosticSeverity
{
    /// <summary>Error that prevents execution.</summary>
    Error,

    /// <summary>Warning about potential issues.</summary>
    Warning,

    /// <summary>Informational hint.</summary>
    Info
}

/// <summary>
/// A diagnostic message for a region of SQL text (error, warning, or info).
/// </summary>
/// <param name="Start">Character offset where the diagnostic starts.</param>
/// <param name="Length">Number of characters the diagnostic spans.</param>
/// <param name="Severity">The severity of the diagnostic.</param>
/// <param name="Message">Human-readable description of the issue.</param>
public record SqlDiagnostic(int Start, int Length, SqlDiagnosticSeverity Severity, string Message);
