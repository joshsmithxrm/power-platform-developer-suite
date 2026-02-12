namespace PPDS.Query.Planning;

/// <summary>
/// Join types supported by the query engine join nodes.
/// </summary>
public enum JoinType
{
    /// <summary>INNER JOIN: only matching rows from both sides.</summary>
    Inner,

    /// <summary>LEFT JOIN: all rows from left side, matching rows from right (or nulls).</summary>
    Left,

    /// <summary>RIGHT JOIN: all rows from right side, matching rows from left (or nulls).</summary>
    Right,

    /// <summary>FULL OUTER JOIN: all rows from both sides (nulls where no match).</summary>
    FullOuter,

    /// <summary>CROSS JOIN: Cartesian product of both sides (no predicate).</summary>
    Cross
}
