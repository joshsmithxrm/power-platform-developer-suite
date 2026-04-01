namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to update an existing column (attribute) on a Dataverse table.
/// </summary>
public sealed class UpdateColumnRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the table.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the entity containing the column.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the column to update.</summary>
    public string ColumnLogicalName { get; set; } = "";

    /// <summary>Gets or sets the updated display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the updated description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the updated requirement level.</summary>
    public string? RequiredLevel { get; set; }

    /// <summary>Gets or sets the updated maximum length.</summary>
    public int? MaxLength { get; set; }

    /// <summary>Gets or sets the updated minimum value.</summary>
    public double? MinValue { get; set; }

    /// <summary>Gets or sets the updated maximum value.</summary>
    public double? MaxValue { get; set; }

    /// <summary>Gets or sets the updated precision.</summary>
    public int? Precision { get; set; }

    /// <summary>Gets or sets the updated format.</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets whether auditing is enabled for the column.</summary>
    public bool? IsAuditEnabled { get; set; }

    /// <summary>Gets or sets whether the column is secured (field-level security).</summary>
    public bool? IsSecured { get; set; }

    /// <summary>Gets or sets whether the column is valid for Advanced Find.</summary>
    public bool? IsValidForAdvancedFind { get; set; }

    /// <summary>Gets or sets the updated auto-number format string.</summary>
    public string? AutoNumberFormat { get; set; }

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
