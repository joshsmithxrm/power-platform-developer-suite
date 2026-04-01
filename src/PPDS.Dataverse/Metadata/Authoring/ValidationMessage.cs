namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Represents a validation message produced during a dry-run or schema authoring operation.
/// </summary>
public sealed class ValidationMessage
{
    /// <summary>Gets or sets the name of the field that triggered the validation.</summary>
    public string Field { get; set; } = "";

    /// <summary>Gets or sets the validation rule identifier.</summary>
    public string Rule { get; set; } = "";

    /// <summary>Gets or sets the human-readable validation message.</summary>
    public string Message { get; set; } = "";
}
