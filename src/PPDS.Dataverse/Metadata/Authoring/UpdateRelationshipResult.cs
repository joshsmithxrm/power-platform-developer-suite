using System.Collections.Generic;

namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Result of a relationship update: every cascade setting of the relationship, as read before the update and as sent.
/// </summary>
public sealed class UpdateRelationshipResult
{
    /// <summary>Gets the schema name of the relationship as stored in the environment (as read).</summary>
    public string SchemaName { get; init; } = "";

    /// <summary>Gets whether this was a dry run: the settings were read and computed, and nothing was sent.</summary>
    public bool WasDryRun { get; init; }

    /// <summary>
    /// Gets whether the update was sent. False on a dry run, and false when every value given already equals the
    /// current one (see <see cref="HasChanges"/>): then nothing is sent.
    /// </summary>
    public bool WasSent { get; init; }

    /// <summary>Gets whether any cascade setting or the hierarchical flag differs from the value read.</summary>
    public bool HasChanges { get; init; }

    /// <summary>
    /// Gets the six cascade settings (Assign, Delete, Merge, Reparent, Share, Unshare), each with the value read from
    /// the environment and the value sent. A setting the caller did not give is sent as read.
    /// </summary>
    public IReadOnlyList<CascadeSettingChange> CascadeSettings { get; init; } = [];

    /// <summary>Gets the relationship's hierarchical flag as read before the update.</summary>
    public bool? IsHierarchicalBefore { get; init; }

    /// <summary>Gets the hierarchical flag sent: the requested value, or the value read when it was not given.</summary>
    public bool? IsHierarchicalAfter { get; init; }

    /// <summary>
    /// Gets whether the update changes Reparent or Share to NoCascade. Microsoft documents that Dataverse then starts a
    /// system job (inherited access rights cleanup) that removes access users had only through this cascading.
    /// </summary>
    public bool RemovesInheritedAccess { get; init; }
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
