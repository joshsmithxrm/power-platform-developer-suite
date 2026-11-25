using System.Collections.Generic;

namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Result of a relationship update: every cascade setting of the relationship, as read before the update and as sent.
/// </summary>
public sealed class UpdateRelationshipResult
{
    /// <summary>Gets the schema name of the relationship.</summary>
    public string SchemaName { get; init; } = "";

    /// <summary>Gets whether this was a dry run: the settings were read and computed, and nothing was sent.</summary>
    public bool WasDryRun { get; init; }

    /// <summary>
    /// Gets the six cascade settings (Assign, Delete, Merge, Reparent, Share, Unshare), each with the value read from
    /// the environment and the value sent. A setting the caller did not give is sent as read.
    /// </summary>
    public IReadOnlyList<CascadeSettingChange> CascadeSettings { get; init; } = [];
}

/// <summary>
/// One cascade setting of a relationship update.
/// </summary>
public sealed class CascadeSettingChange
{
    /// <summary>Gets the name of the setting: Assign, Delete, Merge, Reparent, Share or Unshare.</summary>
    public string Setting { get; init; } = "";

    /// <summary>Gets the value read from the environment before the update.</summary>
    public CascadeBehavior Before { get; init; }

    /// <summary>Gets the value sent: the requested value, or the value read when the setting was not given.</summary>
    public CascadeBehavior After { get; init; }

    /// <summary>Gets whether the caller gave this setting.</summary>
    public bool Requested { get; init; }

    /// <summary>Gets whether the value sent differs from the value read.</summary>
    public bool Changed => Before != After;
}
