using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Ast;

[Trait("Category", "TuiUnit")]
public class SqlStatementHierarchyTests
{
    [Fact]
    public void SqlSelectStatement_ImplementsISqlStatement()
    {
        var stmt = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Simple("name") },
            new SqlTableRef("account"));

        Assert.IsAssignableFrom<ISqlStatement>(stmt);
    }

    [Fact]
    public void SqlSelectStatement_SourcePosition_DefaultsToZero()
    {
        var stmt = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Simple("name") },
            new SqlTableRef("account"));

        Assert.Equal(0, stmt.SourcePosition);
    }

    [Fact]
    public void SqlSelectStatement_SourcePosition_CanBeSet()
    {
        var stmt = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Simple("name") },
            new SqlTableRef("account"),
            sourcePosition: 42);

        Assert.Equal(42, stmt.SourcePosition);
    }

    [Fact]
    public void SqlSelectStatement_Having_DefaultsToNull()
    {
        var stmt = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Simple("name") },
            new SqlTableRef("account"));

        Assert.Null(stmt.Having);
    }

    [Fact]
    public void SqlSelectStatement_Having_CanBeSet()
    {
        var having = new SqlComparisonCondition(
            SqlColumnRef.Simple("cnt"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("5"));

        var stmt = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Simple("name") },
            new SqlTableRef("account"),
            having: having);

        Assert.Same(having, stmt.Having);
    }

    [Fact]
    public void SqlSelectStatement_WithTop_PreservesHavingAndSourcePosition()
    {
        var having = new SqlComparisonCondition(
            SqlColumnRef.Simple("cnt"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("5"));

        var original = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Simple("name") },
            new SqlTableRef("account"),
            having: having,
            sourcePosition: 10);

        var withTop = original.WithTop(100);

        Assert.Equal(100, withTop.Top);
        Assert.Same(having, withTop.Having);
        Assert.Equal(10, withTop.SourcePosition);
    }

    [Fact]
    public void SqlComputedColumn_ImplementsISqlSelectColumn()
    {
        var expr = new SqlBinaryExpression(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlBinaryOperator.Multiply,
            new SqlLiteralExpression(SqlLiteral.Number("0.1")));

        var col = new SqlComputedColumn(expr, "tax");

        Assert.IsAssignableFrom<ISqlSelectColumn>(col);
        Assert.Equal("tax", col.Alias);
        Assert.Same(expr, col.Expression);
    }

    [Fact]
    public void SqlComputedColumn_AliasIsOptional()
    {
        var expr = new SqlLiteralExpression(SqlLiteral.Number("42"));
        var col = new SqlComputedColumn(expr);

        Assert.Null(col.Alias);
    }
}
