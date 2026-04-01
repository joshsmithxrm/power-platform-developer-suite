using System;

namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Result of a create column operation.
/// </summary>
public sealed class CreateColumnResult
{
    /// <summary>Gets or sets the logical name of the created column.</summary>
    public string LogicalName { get; set; } = "";

    /// <summary>Gets or sets the metadata identifier of the created column.</summary>
    public Guid MetadataId { get; set; }

    /// <summary>Gets or sets whether the operation was a dry run.</summary>
    public bool WasDryRun { get; set; }

    /// <summary>Gets or sets the validation messages produced during the operation.</summary>
    public ValidationMessage[] ValidationMessages { get; set; } = [];
}
