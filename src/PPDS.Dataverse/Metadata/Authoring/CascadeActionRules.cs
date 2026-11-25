using System;
using System.Collections.Generic;
using System.Linq;

namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// The cascade actions of a one-to-many relationship and the values Microsoft documents as valid for each
/// ("Configure table relationship cascading behavior", the Valid options of each action). The CLI and MCP metadata
/// authoring surfaces read their help and their checks from this one table.
/// </summary>
public static class CascadeActionRules
{
    private static readonly CascadeBehavior[] AssignLike =
        [CascadeBehavior.Active, CascadeBehavior.Cascade, CascadeBehavior.NoCascade, CascadeBehavior.UserOwned];

    private static readonly Dictionary<string, CascadeBehavior[]> Valid = new(StringComparer.Ordinal)
    {
        ["Assign"] = AssignLike,
        ["Delete"] = [CascadeBehavior.Cascade, CascadeBehavior.RemoveLink, CascadeBehavior.Restrict],
        ["Merge"] = [CascadeBehavior.Cascade, CascadeBehavior.NoCascade],
        ["Reparent"] = AssignLike,
        ["Share"] = AssignLike,
        ["Unshare"] = AssignLike,
    };

    /// <summary>
    /// What Microsoft documents about changing Reparent or Share to NoCascade ("Create and edit relationships between
    /// tables", "Inherited access rights cleanup"; "How to clean up inherited access").
    /// </summary>
    public const string InheritedAccessCleanupNotice =
        "Reparent or Share changes to NoCascade, so Dataverse starts the inherited access rights cleanup system job: users who could open related records only through that cascading lose that access. The job can fail; check it under System Jobs.";

    /// <summary>Gets the six cascade actions an update can change, in their fixed order.</summary>
    public static IReadOnlyList<string> Actions { get; } = ["Assign", "Delete", "Merge", "Reparent", "Share", "Unshare"];

    /// <summary>Gets the values Microsoft documents as valid for <paramref name="action"/>.</summary>
    /// <param name="action">One of <see cref="Actions"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is not one of <see cref="Actions"/>.</exception>
    public static IReadOnlyList<CascadeBehavior> ValidFor(string action)
        => Valid.TryGetValue(action, out var values)
            ? values
            : throw new ArgumentOutOfRangeException(nameof(action), action, "Not a cascade action.");

    /// <summary>Gets whether <paramref name="value"/> is documented as valid for <paramref name="action"/>.</summary>
    /// <param name="action">One of <see cref="Actions"/>.</param>
    /// <param name="value">The cascade behaviour.</param>
    public static bool IsValid(string action, CascadeBehavior value) => ValidFor(action).Contains(value);

    /// <summary>Gets the valid values for <paramref name="action"/> as a list for help and messages, such as "Cascade, RemoveLink or Restrict".</summary>
    /// <param name="action">One of <see cref="Actions"/>.</param>
    public static string Describe(string action)
    {
        var names = ValidFor(action).Select(v => v.ToString()).ToArray();
        return names.Length == 1 ? names[0] : string.Join(", ", names[..^1]) + " or " + names[^1];
    }
}
