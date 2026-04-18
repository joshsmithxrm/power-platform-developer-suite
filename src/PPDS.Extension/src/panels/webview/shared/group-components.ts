/**
 * Shared grouping utility for solution components.
 *
 * Mirrors `PPDS.Cli.Services.SolutionComponentGrouper` on the C# side — both
 * surfaces (TUI `SolutionsScreen` and Extension `SolutionsPanel`) must agree
 * on grouping and sort semantics.
 *
 * Contract:
 *   1. Group by `componentTypeName` (exact match, no case-folding).
 *   2. Sort groups alphabetically by type name (current-locale `localeCompare`,
 *      matching the C# default `OrderBy` culture-sensitive comparer).
 *   3. Preserve component order within each group as received from the caller.
 */

/** Input shape — minimal subset of `SolutionComponentInfoDto` required for grouping. */
export interface GroupableComponent {
    readonly componentTypeName: string;
}

/** Output shape — a single group of components sharing the same type name. */
export interface GroupedComponents<T extends GroupableComponent> {
    readonly typeName: string;
    readonly components: readonly T[];
}

/**
 * Groups components by `componentTypeName` and returns groups sorted
 * alphabetically.
 *
 * @param components Components to group. Must not be null/undefined.
 * @returns Sorted groups. Empty input yields an empty array.
 */
export function groupComponentsByType<T extends GroupableComponent>(
    components: readonly T[],
): GroupedComponents<T>[] {
    const groupMap = new Map<string, T[]>();
    for (const component of components) {
        const typeName = component.componentTypeName;
        const group = groupMap.get(typeName);
        if (group) {
            group.push(component);
        } else {
            groupMap.set(typeName, [component]);
        }
    }

    return Array.from(groupMap.entries())
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([typeName, comps]) => ({ typeName, components: comps }));
}
