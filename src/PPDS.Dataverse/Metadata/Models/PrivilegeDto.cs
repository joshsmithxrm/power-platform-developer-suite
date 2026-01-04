using System;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Represents a security privilege for an entity.
/// </summary>
public sealed class PrivilegeDto
{
    /// <summary>
    /// Gets the privilege ID.
    /// </summary>
    [JsonPropertyName("privilegeId")]
    public Guid PrivilegeId { get; init; }

    /// <summary>
    /// Gets the privilege name.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Gets the privilege type (Create, Read, Write, Delete, Assign, Share, Append, AppendTo).
    /// </summary>
    [JsonPropertyName("privilegeType")]
    public required string PrivilegeType { get; init; }

    /// <summary>
    /// Gets whether the privilege can have a local scope.
    /// </summary>
    [JsonPropertyName("canBeLocal")]
    public bool CanBeLocal { get; init; }

    /// <summary>
    /// Gets whether the privilege can have a deep scope.
    /// </summary>
    [JsonPropertyName("canBeDeep")]
    public bool CanBeDeep { get; init; }

    /// <summary>
    /// Gets whether the privilege can have a global scope.
    /// </summary>
    [JsonPropertyName("canBeGlobal")]
    public bool CanBeGlobal { get; init; }

    /// <summary>
    /// Gets whether the privilege can have a basic scope.
    /// </summary>
    [JsonPropertyName("canBeBasic")]
    public bool CanBeBasic { get; init; }
}
