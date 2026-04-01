using System;

namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Result of a create alternate key operation.
/// </summary>
public sealed class CreateKeyResult
{
    /// <summary>Gets or sets the schema name of the created key.</summary>
    public string SchemaName { get; set; } = "";

    /// <summary>Gets or sets the metadata identifier of the created key.</summary>
    public Guid MetadataId { get; set; }

    /// <summary>Gets or sets whether the operation was a dry run.</summary>
    public bool WasDryRun { get; set; }

    /// <summary>Gets or sets the validation messages produced during the operation.</summary>
    public ValidationMessage[] ValidationMessages { get; set; } = [];
}
