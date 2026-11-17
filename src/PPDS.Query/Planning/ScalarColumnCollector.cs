using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PPDS.Query.Planning;

/// <summary>
/// Collects every column an expression reads, at any depth and in any node kind (COALESCE, NULLIF, IIF, CASE
/// conditions, CONVERT, TRY_CAST, function arguments, ...), so the scan fetches each of them. A hand-written walker that
/// listed node kinds missed some, and the missing columns then evaluated as NULL. Subqueries are skipped: their columns
/// belong to another table.
/// </summary>
internal sealed class ScalarColumnCollector : TSqlFragmentVisitor
{
    private readonly List<ColumnReferenceExpression> _columns = new();

    /// <summary>The non-wildcard column references in <paramref name="fragment"/>, in order of appearance.</summary>
    public static IReadOnlyList<ColumnReferenceExpression> Collect(TSqlFragment fragment)
    {
        var collector = new ScalarColumnCollector();
        fragment.Accept(collector);
        return collector._columns;
    }

    public override void ExplicitVisit(ColumnReferenceExpression node)
    {
        if (node.ColumnType != ColumnType.Wildcard && node.MultiPartIdentifier?.Identifiers.Count > 0)
            _columns.Add(node);
    }

    public override void ExplicitVisit(ScalarSubquery node)
    {
        // Not descended: the subquery's columns are not columns of the outer row.
    }

    public override void ExplicitVisit(QueryDerivedTable node)
    {
    }

    public override void ExplicitVisit(ExistsPredicate node)
    {
    }

    public override void ExplicitVisit(InPredicate node)
    {
        // The left-hand expression and literal values are visited; an IN (subquery) is not.
        node.Expression?.Accept(this);
        foreach (var value in node.Values)
            value.Accept(this);
    }
}
