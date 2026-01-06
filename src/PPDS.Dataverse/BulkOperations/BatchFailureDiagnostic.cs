using System;

namespace PPDS.Dataverse.BulkOperations;

/// <summary>
/// Diagnostic information for identifying the cause of a batch failure.
/// </summary>
/// <remarks>
/// <para>
/// When a batch fails with a "Does Not Exist" or similar reference error,
/// this diagnostic identifies which record(s) in the batch contained the
/// problematic reference.
/// </para>
/// <para>
/// This is particularly useful for self-referential entities where one
/// record references another record in the same batch that hasn't been
/// created yet.
/// </para>
/// </remarks>
public sealed class BatchFailureDiagnostic
{
    /// <summary>
    /// Gets the record ID that contains the problematic reference.
    /// </summary>
    public Guid RecordId { get; init; }

    /// <summary>
    /// Gets the index of this record within the failed batch.
    /// </summary>
    public int RecordIndex { get; init; }

    /// <summary>
    /// Gets the attribute/field name that contains the problematic reference.
    /// </summary>
    public string FieldName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the ID of the missing or problematic reference.
    /// </summary>
    public Guid ReferencedId { get; init; }

    /// <summary>
    /// Gets the logical name of the referenced entity.
    /// </summary>
    public string? ReferencedEntityName { get; init; }

    /// <summary>
    /// Gets the detected error pattern.
    /// </summary>
    /// <remarks>
    /// Common patterns:
    /// <list type="bullet">
    ///   <item><c>SELF_REFERENCE</c> - Record references itself before creation</item>
    ///   <item><c>MISSING_PARENT</c> - References a record not yet created in same batch</item>
    ///   <item><c>MISSING_REFERENCE</c> - References a record that doesn't exist in target</item>
    /// </list>
    /// </remarks>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>
    /// Gets a suggestion for resolving the issue.
    /// </summary>
    public string? Suggestion { get; init; }
}
