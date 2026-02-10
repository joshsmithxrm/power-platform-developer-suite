namespace PPDS.Query.Planning;

/// <summary>
/// Join types supported by the query engine join nodes.
/// </summary>
public enum JoinType
{
    /// <summary>INNER JOIN: only matching rows from both sides.</summary>
    Inner,

    /// <summary>LEFT JOIN: all rows from left side, matching rows from right (or nulls).</summary>
    Left
}
