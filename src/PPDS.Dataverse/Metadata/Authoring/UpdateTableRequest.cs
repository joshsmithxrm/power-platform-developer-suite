namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to update an existing Dataverse table (entity).
/// </summary>
public sealed class UpdateTableRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the table.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the entity to update.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets the updated display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the updated plural display name.</summary>
    public string? PluralDisplayName { get; set; }

    /// <summary>Gets or sets the updated description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets whether the table supports activities.</summary>
    public bool? HasActivities { get; set; }

    /// <summary>Gets or sets whether the table supports notes (annotations).</summary>
    public bool? HasNotes { get; set; }

    /// <summary>Gets or sets whether change tracking is enabled.</summary>
    public bool? ChangeTrackingEnabled { get; set; }

    /// <summary>Gets or sets whether auditing is enabled.</summary>
    public bool? IsAuditEnabled { get; set; }

    /// <summary>Gets or sets whether quick create forms are enabled.</summary>
    public bool? IsQuickCreateEnabled { get; set; }

    /// <summary>Gets or sets whether duplicate detection is enabled.</summary>
    public bool? IsDuplicateDetectionEnabled { get; set; }

    /// <summary>Gets or sets whether the table is valid for queues.</summary>
    public bool? IsValidForQueue { get; set; }

    /// <summary>Gets or sets the entity color (hex string).</summary>
    public string? EntityColor { get; set; }

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
