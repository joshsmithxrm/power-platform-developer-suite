namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Request to create a new column (attribute) on a Dataverse table.
/// </summary>
public sealed class CreateColumnRequest
{
    /// <summary>Gets or sets the unique name of the solution containing the table.</summary>
    public string SolutionUniqueName { get; set; } = "";

    /// <summary>Gets or sets the logical name of the entity to add the column to.</summary>
    public string EntityLogicalName { get; set; } = "";

    /// <summary>Gets or sets the schema name for the new column.</summary>
    public string SchemaName { get; set; } = "";

    /// <summary>Gets or sets the display name of the column.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Gets or sets the description of the column.</summary>
    public string Description { get; set; } = "";

    /// <summary>Gets or sets the column type.</summary>
    public SchemaColumnType ColumnType { get; set; }

    /// <summary>Gets or sets the requirement level. Valid values: "None", "Recommended", "Required".</summary>
    public string? RequiredLevel { get; set; }

    /// <summary>Gets or sets the maximum length for String/Memo columns.</summary>
    public int? MaxLength { get; set; }

    /// <summary>Gets or sets the minimum value for Integer, Decimal, Double, and Money columns.</summary>
    public double? MinValue { get; set; }

    /// <summary>Gets or sets the maximum value for Integer, Decimal, Double, and Money columns.</summary>
    public double? MaxValue { get; set; }

    /// <summary>Gets or sets the precision for Decimal, Double, and Money columns.</summary>
    public int? Precision { get; set; }

    /// <summary>Gets or sets the format for String, Integer, or DateTime columns.</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets the date/time behavior. Valid values: "UserLocal", "DateOnly", "TimeZoneIndependent".</summary>
    public string? DateTimeBehavior { get; set; }

    /// <summary>Gets or sets the name of an existing global option set for Choice/Choices columns.</summary>
    public string? OptionSetName { get; set; }

    /// <summary>Gets or sets the local option definitions for new Choice/Choices columns.</summary>
    public OptionDefinition[]? Options { get; set; }

    /// <summary>Gets or sets the default value for Choice or Boolean columns.</summary>
    public int? DefaultValue { get; set; }

    /// <summary>Gets or sets the label for the true value of a Boolean column.</summary>
    public string? TrueLabel { get; set; }

    /// <summary>Gets or sets the label for the false value of a Boolean column.</summary>
    public string? FalseLabel { get; set; }

    /// <summary>Gets or sets the maximum file size in KB for Image/File columns.</summary>
    public int? MaxSizeInKB { get; set; }

    /// <summary>Gets or sets whether the Image column can store the full-size image.</summary>
    public bool? CanStoreFullImage { get; set; }

    /// <summary>Gets or sets the IME mode for Money columns.</summary>
    public string? ImeMode { get; set; }

    /// <summary>Gets or sets the auto-number format string for auto-number columns (String type).</summary>
    public string? AutoNumberFormat { get; set; }

    /// <summary>Gets or sets whether auditing is enabled for the column.</summary>
    public bool? IsAuditEnabled { get; set; }

    /// <summary>Gets or sets whether the column is secured (field-level security).</summary>
    public bool? IsSecured { get; set; }

    /// <summary>Gets or sets whether the column is valid for Advanced Find.</summary>
    public bool? IsValidForAdvancedFind { get; set; }

    /// <summary>Gets or sets whether this is a dry-run (validation only, no changes persisted).</summary>
    public bool DryRun { get; set; }
}
