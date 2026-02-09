using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Represents a UNION or UNION ALL statement combining multiple SELECT queries.
/// Each SELECT is executed independently and results are concatenated.
/// UNION deduplicates; UNION ALL preserves all rows.
/// </summary>
public sealed class SqlUnionStatement : ISqlStatement
{
    /// <summary>The SELECT queries being combined (at least 2).</summary>
    public IReadOnlyList<SqlSelectStatement> Queries { get; }

    /// <summary>
    /// For each boundary between queries, whether it's UNION ALL (true) or UNION (false).
    /// Count is Queries.Count - 1.
    /// </summary>
    public IReadOnlyList<bool> IsUnionAll { get; }

    /// <summary>Optional trailing ORDER BY applied to the combined result.</summary>
    public IReadOnlyList<SqlOrderByItem>? OrderBy { get; }

    /// <summary>Optional TOP/LIMIT applied to the combined result.</summary>
    public int? Top { get; }

    /// <inheritdoc />
    public int SourcePosition { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlUnionStatement"/> class.</summary>
    public SqlUnionStatement(
        IReadOnlyList<SqlSelectStatement> queries,
        IReadOnlyList<bool> isUnionAll,
        IReadOnlyList<SqlOrderByItem>? orderBy = null,
        int? top = null,
        int sourcePosition = 0)
    {
        if (queries == null) throw new ArgumentNullException(nameof(queries));
        if (queries.Count < 2) throw new ArgumentException("UNION requires at least two queries.", nameof(queries));
        if (isUnionAll == null) throw new ArgumentNullException(nameof(isUnionAll));
        if (isUnionAll.Count != queries.Count - 1)
            throw new ArgumentException("IsUnionAll count must be queries count - 1.", nameof(isUnionAll));

        Queries = queries;
        IsUnionAll = isUnionAll;
        OrderBy = orderBy;
        Top = top;
        SourcePosition = sourcePosition;
    }
}
