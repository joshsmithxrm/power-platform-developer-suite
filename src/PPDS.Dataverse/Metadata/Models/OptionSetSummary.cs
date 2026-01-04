using System;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Summary information for a global option set in list views.
/// </summary>
public sealed class OptionSetSummary
{
    /// <summary>
    /// Gets the unique metadata identifier.
    /// </summary>
    [JsonPropertyName("metadataId")]
    public Guid MetadataId { get; init; }

    /// <summary>
    /// Gets the option set name.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Gets the option set display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the option set type (Picklist, State, Status, Boolean).
    /// </summary>
    [JsonPropertyName("optionSetType")]
    public required string OptionSetType { get; init; }

    /// <summary>
    /// Gets whether this option set is global.
    /// </summary>
    [JsonPropertyName("isGlobal")]
    public bool IsGlobal { get; init; }

    /// <summary>
    /// Gets whether this option set is custom.
    /// </summary>
    [JsonPropertyName("isCustomOptionSet")]
    public bool IsCustomOptionSet { get; init; }

    /// <summary>
    /// Gets whether this option set is part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; init; }

    /// <summary>
    /// Gets the description of the option set.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Gets the number of options in the set.
    /// </summary>
    [JsonPropertyName("optionCount")]
    public int OptionCount { get; init; }
}
