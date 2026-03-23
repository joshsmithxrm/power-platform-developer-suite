using System.Collections.Generic;

namespace PPDS.Dataverse.Models;

/// <summary>
/// Wraps a list result with metadata about the total dataset.
/// Implements Constitution I4: every reduction in displayed records must be visible and reversible.
/// </summary>
/// <typeparam name="T">The type of items in the result.</typeparam>
public sealed class ListResult<T>
{
    /// <summary>Gets the items returned by the query.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Gets the total number of records matching the query (before any top/page limit).</summary>
    public int TotalCount { get; init; }

    /// <summary>Gets whether the result was truncated by a top/page limit.</summary>
    public bool WasTruncated { get; init; }

    /// <summary>Gets the list of filters that were applied (for UI display, e.g. "active only", "root roles only").</summary>
    public IReadOnlyList<string> FiltersApplied { get; init; } = [];
}
