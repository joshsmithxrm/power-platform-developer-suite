namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to create a new Dataverse table (entity).
/// </summary>
public sealed class CreateTableRequest
{
    /// <summary>Gets or sets the unique name of the solution to add the table to.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the schema name for the new table. Prefix is auto-validated against the solution publisher.</summary>
    public string SchemaName { get; set; } = "";

    /// <summary>Gets or sets the display name of the table.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Gets or sets the plural display name of the table.</summary>
    public string PluralDisplayName { get; set; } = "";

    /// <summary>Gets or sets the description of the table.</summary>
    public string Description { get; set; } = "";

    /// <summary>Gets or sets the ownership type. Valid values: "UserOwned" or "OrganizationOwned".</summary>
    public string OwnershipType { get; set; } = "";

    /// <summary>Gets or sets the schema name for the primary attribute.</summary>
    public string? PrimaryAttributeSchemaName { get; set; }

    /// <summary>Gets or sets the display name for the primary attribute.</summary>
    public string? PrimaryAttributeDisplayName { get; set; }

    /// <summary>Gets or sets the max length for the primary attribute.</summary>
    public int? PrimaryAttributeMaxLength { get; set; }

    /// <summary>Gets or sets whether the table supports activities.</summary>
    public bool? HasActivities { get; set; }

    /// <summary>Gets or sets whether the table supports notes (annotations).</summary>
    public bool? HasNotes { get; set; }

    /// <summary>Gets or sets whether the table is an activity entity.</summary>
    public bool? IsActivity { get; set; }

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
