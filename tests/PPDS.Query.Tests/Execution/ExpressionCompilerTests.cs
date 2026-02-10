using System.Globalization;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Execution.Functions;
using PPDS.Query.Execution;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Query.Tests.Execution;

[Trait("Category", "Unit")]
public class ExpressionCompilerTests
{
    private readonly ExpressionCompiler _compiler = new();

    private static readonly IReadOnlyDictionary<string, QueryValue> EmptyRow =
        new Dictionary<string, QueryValue>();

    // ════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════

    private static ScalarExpression ParseExpression(string expr)
    {
        var parser = new QueryParser();
        var stmt = (SelectStatement)parser.ParseStatement($"SELECT {expr}");
        var query = (QuerySpecification)stmt.QueryExpression;
        var col = (SelectScalarExpression)query.SelectElements[0];
        return col.Expression;
    }

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

    // ════════════════════════════════════════════════════════════════════
    //  1. Literals
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_IntegerLiteral_ReturnsInt()
    {
        var expr = ParseExpression("42");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(42);
    }

    [Fact]
    public void CompileScalar_LargeIntegerLiteral_ReturnsLong()
    {
        var expr = ParseExpression("3000000000");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(3000000000L);
    }

    [Fact]
    public void CompileScalar_StringLiteral_ReturnsString()
    {
        var expr = ParseExpression("'hello'");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("hello");
    }

    [Fact]
    public void CompileScalar_NullLiteral_ReturnsNull()
    {
        var expr = ParseExpression("NULL");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().BeNull();
    }

    [Fact]
    public void CompileScalar_NumericLiteral_ReturnsDecimal()
    {
        var expr = ParseExpression("3.14");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(3.14m);
    }

    [Fact]
    public void CompileScalar_RealLiteral_ReturnsDouble()
    {
        var expr = ParseExpression("2.5E3");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(2500.0);
    }

    // ════════════════════════════════════════════════════════════════════
    //  2. Column references
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_SimpleColumnReference_ReturnsValue()
    {
        var expr = ParseExpression("name");
        var compiled = _compiler.CompileScalar(expr);
        var row = MakeRow(("name", "Contoso"));

        compiled(row).Should().Be("Contoso");
    }

    [Fact]
    public void CompileScalar_ColumnReference_CaseInsensitive()
    {
        var expr = ParseExpression("Name");
        var compiled = _compiler.CompileScalar(expr);
        var row = MakeRow(("name", "Contoso"));

        compiled(row).Should().Be("Contoso");
    }

    [Fact]
    public void CompileScalar_QualifiedColumnReference_ReturnsValue()
    {
        var expr = ParseExpression("a.name");
        var compiled = _compiler.CompileScalar(expr);

        // Row has unqualified key — should match via simple name fallback
        var row = MakeRow(("name", "Fabrikam"));
        compiled(row).Should().Be("Fabrikam");
    }

    [Fact]
    public void CompileScalar_QualifiedColumnReference_FullNameMatch()
    {
        var expr = ParseExpression("t.col");
        var compiled = _compiler.CompileScalar(expr);

        var row = MakeRow(("t.col", "qualified"));
        compiled(row).Should().Be("qualified");
    }

    [Fact]
    public void CompileScalar_MissingColumn_ReturnsNull()
    {
        var expr = ParseExpression("nonexistent");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════════
    //  3. Binary operations — arithmetic
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("3 + 5", 8L)]
    [InlineData("10 - 3", 7L)]
    [InlineData("4 * 7", 28L)]
    [InlineData("20 / 4", 5L)]
    [InlineData("17 % 5", 2L)]
    public void CompileScalar_ArithmeticOperators_ReturnsExpected(string sql, object expected)
    {
        var expr = ParseExpression(sql);
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(expected);
    }

    [Fact]
    public void CompileScalar_DecimalArithmetic_ReturnsDecimal()
    {
        var expr = ParseExpression("1.5 + 2.5");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(4.0m);
    }

    [Fact]
    public void CompileScalar_DivideByZero_Throws()
    {
        var expr = ParseExpression("10 / 0");
        var compiled = _compiler.CompileScalar(expr);

        var act = () => compiled(EmptyRow);
        act.Should().Throw<DivideByZeroException>();
    }

    // ════════════════════════════════════════════════════════════════════
    //  4. String concatenation
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_StringConcatenation_WithAdd()
    {
        var expr = ParseExpression("'Hello' + ' ' + 'World'");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("Hello World");
    }

    [Fact]
    public void CompileScalar_StringConcatenation_NumberAndString()
    {
        var expr = ParseExpression("'Count: ' + 42");
        var compiled = _compiler.CompileScalar(expr);

        // String + int = string concatenation
        compiled(EmptyRow).Should().Be("Count: 42");
    }

    // ════════════════════════════════════════════════════════════════════
    //  5. Unary negation
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_UnaryNegation_Int()
    {
        var expr = ParseExpression("-42");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(-42);
    }

    [Fact]
    public void CompileScalar_UnaryNegation_Decimal()
    {
        var expr = ParseExpression("-3.14");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(-3.14m);
    }

    [Fact]
    public void CompileScalar_UnaryNegation_NullPropagates()
    {
        var expr = ParseExpression("-NULL");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════════
    //  6. CASE/WHEN
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_SearchedCase_MatchesFirstWhen()
    {
        var expr = ParseExpression("CASE WHEN 1 = 1 THEN 'yes' WHEN 1 = 2 THEN 'no' ELSE 'maybe' END");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("yes");
    }

    [Fact]
    public void CompileScalar_SearchedCase_MatchesSecondWhen()
    {
        var expr = ParseExpression("CASE WHEN 1 = 2 THEN 'first' WHEN 1 = 1 THEN 'second' ELSE 'else' END");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("second");
    }

    [Fact]
    public void CompileScalar_SearchedCase_FallsToElse()
    {
        var expr = ParseExpression("CASE WHEN 1 = 2 THEN 'yes' ELSE 'no' END");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("no");
    }

    [Fact]
    public void CompileScalar_SearchedCase_NoElse_ReturnsNull()
    {
        var expr = ParseExpression("CASE WHEN 1 = 2 THEN 'yes' END");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().BeNull();
    }

    [Fact]
    public void CompileScalar_SearchedCase_WithColumnData()
    {
        var expr = ParseExpression(
            "CASE WHEN status = 1 THEN 'Active' WHEN status = 2 THEN 'Inactive' ELSE 'Unknown' END");
        var compiled = _compiler.CompileScalar(expr);

        var row1 = MakeRow(("status", 1));
        var row2 = MakeRow(("status", 2));
        var row3 = MakeRow(("status", 99));

        compiled(row1).Should().Be("Active");
        compiled(row2).Should().Be("Inactive");
        compiled(row3).Should().Be("Unknown");
    }

    // ════════════════════════════════════════════════════════════════════
    //  7. IIF
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_IIF_TrueBranch()
    {
        var expr = ParseExpression("IIF(1 = 1, 'yes', 'no')");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("yes");
    }

    [Fact]
    public void CompileScalar_IIF_FalseBranch()
    {
        var expr = ParseExpression("IIF(1 = 2, 'yes', 'no')");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("no");
    }

    // ════════════════════════════════════════════════════════════════════
    //  8. IS NULL / IS NOT NULL
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompilePredicate_IsNull_True()
    {
        var pred = ParsePredicate("NULL IS NULL");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeTrue();
    }

    [Fact]
    public void CompilePredicate_IsNull_False()
    {
        var pred = ParsePredicate("1 IS NULL");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_IsNotNull_True()
    {
        var pred = ParsePredicate("1 IS NOT NULL");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeTrue();
    }

    [Fact]
    public void CompilePredicate_IsNotNull_False()
    {
        var pred = ParsePredicate("NULL IS NOT NULL");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_IsNull_WithColumn()
    {
        var pred = ParsePredicate("name IS NULL");
        var compiled = _compiler.CompilePredicate(pred);

        var rowWithNull = MakeRow(("name", (object?)null));
        var rowWithValue = MakeRow(("name", "Contoso"));

        compiled(rowWithNull).Should().BeTrue();
        compiled(rowWithValue).Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    //  9. LIKE / NOT LIKE
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompilePredicate_Like_PercentWildcard()
    {
        var pred = ParsePredicate("name LIKE '%oso'");
        var compiled = _compiler.CompilePredicate(pred);

        var matchRow = MakeRow(("name", "Contoso"));
        var noMatchRow = MakeRow(("name", "Fabrikam"));

        compiled(matchRow).Should().BeTrue();
        compiled(noMatchRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_Like_UnderscoreWildcard()
    {
        var pred = ParsePredicate("code LIKE 'A_C'");
        var compiled = _compiler.CompilePredicate(pred);

        var matchRow = MakeRow(("code", "ABC"));
        var noMatchRow = MakeRow(("code", "ABBC"));

        compiled(matchRow).Should().BeTrue();
        compiled(noMatchRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_Like_CombinedWildcards()
    {
        var pred = ParsePredicate("name LIKE '%soft%'");
        var compiled = _compiler.CompilePredicate(pred);

        var matchRow = MakeRow(("name", "Microsoft"));
        compiled(matchRow).Should().BeTrue();
    }

    [Fact]
    public void CompilePredicate_NotLike()
    {
        var pred = ParsePredicate("name NOT LIKE '%oso'");
        var compiled = _compiler.CompilePredicate(pred);

        var matchRow = MakeRow(("name", "Contoso"));
        var noMatchRow = MakeRow(("name", "Fabrikam"));

        compiled(matchRow).Should().BeFalse();
        compiled(noMatchRow).Should().BeTrue();
    }

    [Fact]
    public void CompilePredicate_Like_NullColumn_ReturnsFalse()
    {
        var pred = ParsePredicate("name LIKE '%test%'");
        var compiled = _compiler.CompilePredicate(pred);

        var nullRow = MakeRow(("name", (object?)null));
        compiled(nullRow).Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    //  10. IN / NOT IN
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompilePredicate_In_Matches()
    {
        var pred = ParsePredicate("status IN (1, 2, 3)");
        var compiled = _compiler.CompilePredicate(pred);

        var matchRow = MakeRow(("status", 2));
        compiled(matchRow).Should().BeTrue();
    }

    [Fact]
    public void CompilePredicate_In_NoMatch()
    {
        var pred = ParsePredicate("status IN (1, 2, 3)");
        var compiled = _compiler.CompilePredicate(pred);

        var noMatchRow = MakeRow(("status", 99));
        compiled(noMatchRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_NotIn_Matches()
    {
        var pred = ParsePredicate("status NOT IN (1, 2, 3)");
        var compiled = _compiler.CompilePredicate(pred);

        var outRow = MakeRow(("status", 99));
        compiled(outRow).Should().BeTrue();
    }

    [Fact]
    public void CompilePredicate_NotIn_InList()
    {
        var pred = ParsePredicate("status NOT IN (1, 2, 3)");
        var compiled = _compiler.CompilePredicate(pred);

        var inRow = MakeRow(("status", 2));
        compiled(inRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_In_StringValues()
    {
        var pred = ParsePredicate("name IN ('Alice', 'Bob', 'Charlie')");
        var compiled = _compiler.CompilePredicate(pred);

        var matchRow = MakeRow(("name", "Bob"));
        var noMatchRow = MakeRow(("name", "Dave"));

        compiled(matchRow).Should().BeTrue();
        compiled(noMatchRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_In_NullValue_ReturnsFalse()
    {
        var pred = ParsePredicate("status IN (1, 2, 3)");
        var compiled = _compiler.CompilePredicate(pred);

        var nullRow = MakeRow(("status", (object?)null));
        compiled(nullRow).Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    //  11. AND, OR, NOT
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompilePredicate_And_BothTrue()
    {
        var pred = ParsePredicate("1 = 1 AND 2 = 2");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeTrue();
    }

    [Fact]
    public void CompilePredicate_And_OneFalse()
    {
        var pred = ParsePredicate("1 = 1 AND 1 = 2");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_Or_OneFalse()
    {
        var pred = ParsePredicate("1 = 2 OR 2 = 2");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeTrue();
    }

    [Fact]
    public void CompilePredicate_Or_BothFalse()
    {
        var pred = ParsePredicate("1 = 2 OR 3 = 4");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_Not()
    {
        var pred = ParsePredicate("NOT 1 = 2");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeTrue();
    }

    [Fact]
    public void CompilePredicate_Not_NegatesTrue()
    {
        var pred = ParsePredicate("NOT 1 = 1");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_ComplexLogical()
    {
        var pred = ParsePredicate("(status = 1 OR status = 2) AND name IS NOT NULL");
        var compiled = _compiler.CompilePredicate(pred);

        var row1 = MakeRow(("status", 1), ("name", "Test"));
        var row2 = MakeRow(("status", 3), ("name", "Test"));
        var row3 = MakeRow(("status", 1), ("name", (object?)null));

        compiled(row1).Should().BeTrue();
        compiled(row2).Should().BeFalse();
        compiled(row3).Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    //  12. Comparison operators
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("1 = 1", true)]
    [InlineData("1 = 2", false)]
    [InlineData("1 <> 2", true)]
    [InlineData("1 <> 1", false)]
    [InlineData("1 != 2", true)]
    [InlineData("1 < 2", true)]
    [InlineData("2 < 1", false)]
    [InlineData("2 > 1", true)]
    [InlineData("1 > 2", false)]
    [InlineData("1 <= 1", true)]
    [InlineData("1 <= 2", true)]
    [InlineData("2 <= 1", false)]
    [InlineData("1 >= 1", true)]
    [InlineData("2 >= 1", true)]
    [InlineData("1 >= 2", false)]
    public void CompilePredicate_ComparisonOperators(string sql, bool expected)
    {
        var pred = ParsePredicate(sql);
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().Be(expected);
    }

    [Fact]
    public void CompilePredicate_ComparisonWithNull_ReturnsFalse()
    {
        var pred = ParsePredicate("NULL = NULL");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_StringComparison_CaseInsensitive()
    {
        var pred = ParsePredicate("'hello' = 'HELLO'");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeTrue();
    }

    // ════════════════════════════════════════════════════════════════════
    //  13. Nested expressions
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_NestedArithmetic()
    {
        var expr = ParseExpression("(2 + 3) * (10 - 6)");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(20L);
    }

    [Fact]
    public void CompileScalar_DeeplyNestedExpression()
    {
        var expr = ParseExpression("((1 + 2) * 3) + ((4 - 1) * 2)");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(15L);
    }

    [Fact]
    public void CompileScalar_ParenthesisExpression_PassesThrough()
    {
        var expr = ParseExpression("(42)");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(42);
    }

    // ════════════════════════════════════════════════════════════════════
    //  14. Functions
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_Function_Upper()
    {
        var expr = ParseExpression("UPPER('hello')");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("HELLO");
    }

    [Fact]
    public void CompileScalar_Function_Len()
    {
        var expr = ParseExpression("LEN('hello')");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(5);
    }

    [Fact]
    public void CompileScalar_Function_GetDate()
    {
        var before = DateTime.UtcNow.AddSeconds(-2);
        var expr = ParseExpression("GETDATE()");
        var compiled = _compiler.CompileScalar(expr);

        var result = compiled(EmptyRow);
        var after = DateTime.UtcNow.AddSeconds(2);
        result.Should().BeOfType<DateTime>();
        var dt = (DateTime)result!;
        // GETDATE may return local or UTC — just verify it is recent
        dt.Should().BeAfter(before.AddHours(-24));
        dt.Should().BeBefore(after.AddHours(24));
    }

    [Fact]
    public void CompileScalar_Function_WithColumnArg()
    {
        var expr = ParseExpression("UPPER(name)");
        var compiled = _compiler.CompileScalar(expr);

        var row = MakeRow(("name", "contoso"));
        compiled(row).Should().Be("CONTOSO");
    }

    // ════════════════════════════════════════════════════════════════════
    //  15. CAST
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_Cast_IntToVarchar()
    {
        var expr = ParseExpression("CAST(42 AS NVARCHAR(10))");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("42");
    }

    [Fact]
    public void CompileScalar_Cast_VarcharToInt()
    {
        var expr = ParseExpression("CAST('123' AS INT)");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(123);
    }

    [Fact]
    public void CompileScalar_Cast_NullPropagates()
    {
        var expr = ParseExpression("CAST(NULL AS INT)");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().BeNull();
    }

    [Fact]
    public void CompileScalar_Cast_DecimalToInt()
    {
        var expr = ParseExpression("CAST(3.14 AS INT)");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(3);
    }

    // ════════════════════════════════════════════════════════════════════
    //  16. CONVERT
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_Convert_IntToVarchar()
    {
        var expr = ParseExpression("CONVERT(NVARCHAR(10), 42)");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("42");
    }

    [Fact]
    public void CompileScalar_Convert_NullPropagates()
    {
        var expr = ParseExpression("CONVERT(INT, NULL)");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════════
    //  17. Variable references
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_VariableReference_ReturnsValue()
    {
        var scope = new VariableScope();
        scope.Declare("@id", "INT", 42);

        var compiler = new ExpressionCompiler(variableScopeAccessor: () => scope);
        var expr = ParseExpression("@id");
        var compiled = compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(42);
    }

    [Fact]
    public void CompileScalar_VariableReference_NoScope_Throws()
    {
        var compiler = new ExpressionCompiler(variableScopeAccessor: null);
        var expr = ParseExpression("@missing");
        var compiled = compiler.CompileScalar(expr);

        var act = () => compiled(EmptyRow);
        act.Should().Throw<QueryExecutionException>();
    }

    [Fact]
    public void CompileScalar_VariableReference_ChangesOverTime()
    {
        var scope = new VariableScope();
        scope.Declare("@counter", "INT", 1);

        var compiler = new ExpressionCompiler(variableScopeAccessor: () => scope);
        var expr = ParseExpression("@counter");
        var compiled = compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(1);

        scope.Set("@counter", 42);
        compiled(EmptyRow).Should().Be(42);
    }

    // ════════════════════════════════════════════════════════════════════
    //  18. NULL propagation in arithmetic
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_NullArithmetic_Add()
    {
        var expr = ParseExpression("42 + NULL");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().BeNull();
    }

    [Fact]
    public void CompileScalar_NullArithmetic_Multiply()
    {
        var expr = ParseExpression("NULL * 5");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().BeNull();
    }

    [Fact]
    public void CompileScalar_NullColumn_InArithmetic()
    {
        var expr = ParseExpression("revenue + 100");
        var compiled = _compiler.CompileScalar(expr);

        // Missing column is null
        compiled(EmptyRow).Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════════
    //  19. EXISTS throws NotSupportedException
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompilePredicate_ExistsPredicate_ThrowsNotSupported()
    {
        var pred = ParsePredicate("EXISTS (SELECT 1)");
        var act = () => _compiler.CompilePredicate(pred);

        act.Should().Throw<NotSupportedException>()
           .WithMessage("*EXISTS*");
    }

    // ════════════════════════════════════════════════════════════════════
    //  20. Static helper methods
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("42", 42)]
    [InlineData("3.14", 3.14)]
    [InlineData("3000000000", 3000000000L)]
    public void ParseNumber_VariousFormats(string input, object expected)
    {
        var result = ExpressionCompiler.ParseNumber(input);

        // For decimals, use approximate comparison
        if (expected is double d)
            result.Should().Be(decimal.Parse(input, CultureInfo.InvariantCulture));
        else
            result.Should().Be(expected);
    }

    [Fact]
    public void IsNumeric_TrueForNumericTypes()
    {
        ExpressionCompiler.IsNumeric(42).Should().BeTrue();
        ExpressionCompiler.IsNumeric(42L).Should().BeTrue();
        ExpressionCompiler.IsNumeric(42.0m).Should().BeTrue();
        ExpressionCompiler.IsNumeric(42.0).Should().BeTrue();
        ExpressionCompiler.IsNumeric(42.0f).Should().BeTrue();
    }

    [Fact]
    public void IsNumeric_FalseForNonNumeric()
    {
        ExpressionCompiler.IsNumeric("42").Should().BeFalse();
        ExpressionCompiler.IsNumeric(true).Should().BeFalse();
        ExpressionCompiler.IsNumeric(DateTime.Now).Should().BeFalse();
    }

    [Fact]
    public void MatchLikePattern_ExactMatch()
    {
        ExpressionCompiler.MatchLikePattern("hello", "hello").Should().BeTrue();
        ExpressionCompiler.MatchLikePattern("hello", "world").Should().BeFalse();
    }

    [Fact]
    public void MatchLikePattern_CaseInsensitive()
    {
        ExpressionCompiler.MatchLikePattern("Hello", "HELLO").Should().BeTrue();
    }

    [Fact]
    public void MatchLikePattern_PercentWildcard()
    {
        ExpressionCompiler.MatchLikePattern("hello world", "%world").Should().BeTrue();
        ExpressionCompiler.MatchLikePattern("hello world", "hello%").Should().BeTrue();
        ExpressionCompiler.MatchLikePattern("hello world", "%lo wo%").Should().BeTrue();
        ExpressionCompiler.MatchLikePattern("hello world", "%xyz%").Should().BeFalse();
    }

    [Fact]
    public void MatchLikePattern_UnderscoreWildcard()
    {
        ExpressionCompiler.MatchLikePattern("ABC", "A_C").Should().BeTrue();
        ExpressionCompiler.MatchLikePattern("ABBC", "A_C").Should().BeFalse();
    }

    [Fact]
    public void MatchLikePattern_EmptyPattern()
    {
        ExpressionCompiler.MatchLikePattern("", "").Should().BeTrue();
        ExpressionCompiler.MatchLikePattern("", "%").Should().BeTrue();
        ExpressionCompiler.MatchLikePattern("a", "").Should().BeFalse();
    }

    [Fact]
    public void CompareValues_Strings_CaseInsensitive()
    {
        ExpressionCompiler.CompareValues("abc", "ABC").Should().Be(0);
    }

    [Fact]
    public void CompareValues_Numbers_WithPromotion()
    {
        ExpressionCompiler.CompareValues(42, 42.0m).Should().Be(0);
        ExpressionCompiler.CompareValues(1, 2).Should().BeNegative();
        ExpressionCompiler.CompareValues(2, 1).Should().BePositive();
    }

    [Fact]
    public void CompareValues_StringVsNumber()
    {
        ExpressionCompiler.CompareValues(42, "42").Should().Be(0);
        ExpressionCompiler.CompareValues("42", 42).Should().Be(0);
    }

    [Fact]
    public void NegateValue_Types()
    {
        ExpressionCompiler.NegateValue(42).Should().Be(-42);
        ExpressionCompiler.NegateValue(42L).Should().Be(-42L);
        ExpressionCompiler.NegateValue(42.5m).Should().Be(-42.5m);
        ExpressionCompiler.NegateValue(42.5).Should().Be(-42.5);
    }

    // ════════════════════════════════════════════════════════════════════
    //  21. Delegate reusability — same compiled expression, different rows
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompiledExpression_CanBeReusedAcrossRows()
    {
        var expr = ParseExpression("price * quantity");
        var compiled = _compiler.CompileScalar(expr);

        var row1 = MakeRow(("price", 10m), ("quantity", 5));
        var row2 = MakeRow(("price", 20m), ("quantity", 3));

        compiled(row1).Should().Be(50m);
        compiled(row2).Should().Be(60m);
    }

    [Fact]
    public void CompiledPredicate_CanBeReusedAcrossRows()
    {
        var pred = ParsePredicate("amount > 100");
        var compiled = _compiler.CompilePredicate(pred);

        var row1 = MakeRow(("amount", 150));
        var row2 = MakeRow(("amount", 50));

        compiled(row1).Should().BeTrue();
        compiled(row2).Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    //  22. Boolean parenthesis
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompilePredicate_BooleanParenthesis()
    {
        var pred = ParsePredicate("(1 = 1)");
        var compiled = _compiler.CompilePredicate(pred);

        compiled(EmptyRow).Should().BeTrue();
    }

    // ════════════════════════════════════════════════════════════════════
    //  23. Edge cases
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_EmptyStringLiteral()
    {
        var expr = ParseExpression("''");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("");
    }

    [Fact]
    public void CompileScalar_ZeroLiteral()
    {
        var expr = ParseExpression("0");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(0);
    }

    [Fact]
    public void CompileScalar_NegativeZero()
    {
        var expr = ParseExpression("-0");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(0);
    }

    // ════════════════════════════════════════════════════════════════════
    //  24. Complex combined scenarios
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_ComplexExpression_CaseWithArithmetic()
    {
        var expr = ParseExpression(
            "CASE WHEN quantity > 0 THEN price * quantity ELSE 0 END");
        var compiled = _compiler.CompileScalar(expr);

        var row1 = MakeRow(("quantity", 5), ("price", 10m));
        var row2 = MakeRow(("quantity", 0), ("price", 10m));

        compiled(row1).Should().Be(50m);
        compiled(row2).Should().Be(0);
    }

    [Fact]
    public void CompilePredicate_ComplexPredicate_NestedAndOrNot()
    {
        var pred = ParsePredicate(
            "NOT (status = 3 OR (status = 1 AND name IS NULL))");
        var compiled = _compiler.CompilePredicate(pred);

        var row1 = MakeRow(("status", 1), ("name", "Active"));
        var row2 = MakeRow(("status", 3), ("name", "Active"));
        var row3 = MakeRow(("status", 1), ("name", (object?)null));

        compiled(row1).Should().BeTrue();
        compiled(row2).Should().BeFalse();
        compiled(row3).Should().BeFalse();
    }

    [Fact]
    public void CompilePredicate_InWithExpressions()
    {
        // IN with string values tested against a column
        var pred = ParsePredicate("region IN ('EMEA', 'APAC', 'NA')");
        var compiled = _compiler.CompilePredicate(pred);

        var emea = MakeRow(("region", "EMEA"));
        var latam = MakeRow(("region", "LATAM"));

        compiled(emea).Should().BeTrue();
        compiled(latam).Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════════
    //  25. Function with multiple arguments
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void CompileScalar_Function_Replace()
    {
        var expr = ParseExpression("REPLACE('hello world', 'world', 'there')");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("hello there");
    }

    [Fact]
    public void CompileScalar_Function_Substring()
    {
        var expr = ParseExpression("SUBSTRING('hello', 1, 3)");
        var compiled = _compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be("hel");
    }

    // ════════════════════════════════════════════════════════════════════
    //  26. Constructor defaults
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_Default_CreatesFunctionRegistry()
    {
        var compiler = new ExpressionCompiler();
        var expr = ParseExpression("UPPER('test')");
        var compiled = compiler.CompileScalar(expr);

        // Should not throw — default registry has built-in functions
        compiled(EmptyRow).Should().Be("TEST");
    }

    [Fact]
    public void Constructor_CustomRegistry()
    {
        var registry = FunctionRegistry.CreateDefault();
        var compiler = new ExpressionCompiler(registry);
        var expr = ParseExpression("LEN('test')");
        var compiled = compiler.CompileScalar(expr);

        compiled(EmptyRow).Should().Be(4);
    }
}
