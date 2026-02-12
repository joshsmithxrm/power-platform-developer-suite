using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Query.Execution;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Query.Tests.Execution;

[Trait("Category", "Unit")]
public class IsNotDistinctFromTests
{
    private readonly ExpressionCompiler _compiler = new();

    private static readonly IReadOnlyDictionary<string, QueryValue> EmptyRow =
        new Dictionary<string, QueryValue>();

    private static BooleanExpression ParsePredicate(string pred)
    {
        var parser = new QueryParser();
        var stmt = (SelectStatement)parser.ParseStatement($"SELECT 1 WHERE {pred}");
        var query = (QuerySpecification)stmt.QueryExpression;
        return query.WhereClause.SearchCondition;
    }

    private static IReadOnlyDictionary<string, QueryValue> MakeRow(
        params (string key, object? value)[] columns)
    {
        var dict = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in columns)
        {
            dict[key] = QueryValue.Simple(value);
        }
        return dict;
    }

    [Fact]
    public void IsNotDistinctFrom_BothNull_ReturnsTrue()
    {
        var pred = ParsePredicate("NULL IS NOT DISTINCT FROM NULL");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeTrue();
    }

    [Fact]
    public void IsNotDistinctFrom_BothEqual_ReturnsTrue()
    {
        var pred = ParsePredicate("a IS NOT DISTINCT FROM b");
        var compiled = _compiler.CompilePredicate(pred);

        var row = MakeRow(("a", 42), ("b", 42));
        compiled(row).Should().BeTrue();
    }

    [Fact]
    public void IsNotDistinctFrom_OneNull_ReturnsFalse()
    {
        var pred = ParsePredicate("a IS NOT DISTINCT FROM b");
        var compiled = _compiler.CompilePredicate(pred);

        var row = MakeRow(("a", 42), ("b", (object?)null));
        compiled(row).Should().BeFalse();
    }

    [Fact]
    public void IsNotDistinctFrom_DifferentValues_ReturnsFalse()
    {
        var pred = ParsePredicate("a IS NOT DISTINCT FROM b");
        var compiled = _compiler.CompilePredicate(pred);

        var row = MakeRow(("a", 42), ("b", 99));
        compiled(row).Should().BeFalse();
    }
}
