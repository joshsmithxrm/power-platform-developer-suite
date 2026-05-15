namespace PPDS.Cli.Services.Schema.Models;

/// <summary>
/// Severity of a schema difference.
/// </summary>
public enum DiffSeverity
{
    /// <summary>Non-breaking difference (e.g. extra fields in target).</summary>
    Info,

    /// <summary>May cause data loss during import (truncation, missing option values).</summary>
    Warning,

    /// <summary>Will cause import to fail (missing required field, type mismatch).</summary>
    Error
}
