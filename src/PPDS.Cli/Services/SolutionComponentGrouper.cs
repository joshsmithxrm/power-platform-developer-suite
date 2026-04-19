using PPDS.Dataverse.Services;

namespace PPDS.Cli.Services;

/// <summary>
/// A group of solution components that share the same component type.
/// </summary>
/// <remarks>
/// Produced by <see cref="SolutionComponentGrouper"/>. Consumed by the TUI
/// solutions detail dialog and (via the webview message contract) the VS Code
/// Solutions panel. Keeping the grouping logic in one place prevents the two
/// surfaces from drifting.
/// </remarks>
/// <param name="TypeName">
/// Human-readable component type name (e.g. <c>Entity</c>, <c>OptionSet</c>).
/// Matches <see cref="SolutionComponentInfo.ComponentTypeName"/>.
/// </param>
/// <param name="Components">
/// Components belonging to this type, preserving their original order within
/// the input list. Callers that need per-group sorting (e.g. alphabetical by
/// display name) must apply it themselves — this type is intentionally minimal.
/// </param>
public sealed record SolutionComponentGroup(
    string TypeName,
    IReadOnlyList<SolutionComponentInfo> Components);

/// <summary>
/// Groups <see cref="SolutionComponentInfo"/> instances by their component type name.
/// </summary>
/// <remarks>
/// <para>
/// Pure, deterministic transform — no side effects, no I/O. Shared between the
/// TUI <c>SolutionsScreen</c> component-dialog renderer and the Extension
/// <c>SolutionsPanel</c> (via the TypeScript mirror in
/// <c>src/PPDS.Extension/src/panels/webview/shared/group-components.ts</c>).
/// </para>
/// <para>
/// Both implementations follow the same contract:
/// </para>
/// <list type="number">
/// <item>group by <see cref="SolutionComponentInfo.ComponentTypeName"/> (exact match, no case-folding)</item>
/// <item>sort resulting groups alphabetically by type name (ordinal)</item>
/// <item>preserve component order within each group as received from the caller</item>
/// </list>
/// </remarks>
public static class SolutionComponentGrouper
{
    /// <summary>
    /// Groups the supplied components by <see cref="SolutionComponentInfo.ComponentTypeName"/>
    /// and returns the groups sorted alphabetically by type name.
    /// </summary>
    /// <param name="components">Components to group. May be empty; must not be null.</param>
    /// <returns>
    /// Alphabetically sorted groups. Never null. Empty input yields an empty list.
    /// </returns>
    public static IReadOnlyList<SolutionComponentGroup> Group(
        IEnumerable<SolutionComponentInfo> components)
    {
        ArgumentNullException.ThrowIfNull(components);

        // Sort semantics must match the TS mirror (`localeCompare` with no args).
        // Both surfaces use the current-culture comparer to preserve the
        // pre-extraction behavior (see `SolutionsScreen.OnCellActivated` before
        // refactor — it used the default LINQ `OrderBy(g => g.Key)` which
        // resolves to `Comparer<string>.Default`).
        return components
            .GroupBy(c => c.ComponentTypeName)
            .OrderBy(g => g.Key)
            .Select(g => new SolutionComponentGroup(g.Key, g.ToList()))
            .ToList();
    }
}
