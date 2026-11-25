using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Query.Tests.Parsing;

/// <summary>
/// A text the SQL parser does not read to its end is refused (#1955). ScriptDom takes U+FFFF outside a string literal as
/// the end of the input and parsed what came before it without an error, so
/// <c>DELETE FROM account WHERE name = 'a' &lt;U+FFFF&gt; AND name = 'b'</c> was a DELETE on <c>name = 'a'</c> alone.
/// The sweeps place every UTF-16 code unit in four positions and require that each text is either refused or parsed into a
/// statement that holds both conditions as written.
/// </summary>
[Trait("Category", "Unit")]
public class QueryParserTextReadToTheEndTests
{
    private const string Delete = "DELETE FROM account WHERE ";
    private const string Update = "UPDATE account SET description = 'x' WHERE ";
    private const string Select = "SELECT name FROM account WHERE ";

    public enum Position
    {
        /// <summary>Between the two conditions: <c>name = 'a' ? AND name = 'b'</c>.</summary>
        BetweenConditions,

        /// <summary>Inside the first condition's column name: <c>na?me = 'a' AND name = 'b'</c>.</summary>
        InsideAnIdentifier,

        /// <summary>Before the first token.</summary>
        BeforeTheFirstToken,

        /// <summary>After the last token.</summary>
        AfterTheLastToken,
    }

    private static string Text(string head, Position position, char c) => position switch
    {
        Position.BetweenConditions => $"{head}name = 'a' {c} AND name = 'b'",
        Position.InsideAnIdentifier => $"{head}na{c}me = 'a' AND name = 'b'",
        Position.BeforeTheFirstToken => $"{c}{head}name = 'a' AND name = 'b'",
        _ => $"{head}name = 'a' AND name = 'b'{c}",
    };

    /// <summary>The column the first condition names, as the text spells it.</summary>
    private static string FirstColumn(Position position, char c)
        => position == Position.InsideAnIdentifier ? $"na{c}me" : "name";

    /// <summary>
    /// Null when <paramref name="sql"/> is refused by <c>Parse</c> and <c>TryParse</c> alike, or parsed by both into one
    /// statement of <paramref name="kind"/> whose WHERE is exactly the two conditions as written; otherwise what is wrong.
    /// </summary>
    private static string? Check(QueryParser parser, string sql, Type kind, string firstColumn)
    {
        TSqlFragment? parsed;
        try
        {
            parsed = parser.Parse(sql);
        }
        catch (QueryParseException)
        {
            parsed = null;
        }

        var tried = parser.TryParse(sql, out var triedFragment, out var errors);
        if (tried != (parsed is not null) || tried != (triedFragment is not null) || tried == errors.Count > 0)
            return "Parse and TryParse disagree";
        if (parsed is null)
            return null;

        var statements = ((TSqlScript)parsed).Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count != 1 || statements[0].GetType() != kind)
            return $"parsed as {string.Join(", ", statements.Select(s => s.GetType().Name))}";

        var where = statements[0] switch
        {
            DeleteStatement d => d.DeleteSpecification.WhereClause,
            UpdateStatement u => u.UpdateSpecification.WhereClause,
            SelectStatement { QueryExpression: QuerySpecification q } => q.WhereClause,
            _ => null,
        };
        if (where?.SearchCondition is not BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and)
            return "the WHERE is not two conditions joined by AND";
        if (!IsComparison(and.FirstExpression, firstColumn, "a", parsed.ScriptTokenStream))
            return "the first condition is not as written";
        if (!IsComparison(and.SecondExpression, "name", "b", parsed.ScriptTokenStream))
            return "the second condition is not as written";
        return null;
    }

    private static bool IsComparison(BooleanExpression expression, string column, string value, IList<TSqlParserToken> tokens)
    {
        if (expression is not BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } comparison ||
            comparison.SecondExpression is not StringLiteral literal || literal.Value != value)
            return false;

        // A column reference must name exactly the column the text spells (a part separator such as '.' included); any
        // other left side (an operator between two names, say) must be made of exactly the text's characters.
        return comparison.FirstExpression is ColumnReferenceExpression reference
            ? string.Join(".", reference.MultiPartIdentifier.Identifiers.Select(i => i.Value)) == column
            : string.Concat(Enumerable.Range(comparison.FirstExpression.FirstTokenIndex,
                comparison.FirstExpression.LastTokenIndex - comparison.FirstExpression.FirstTokenIndex + 1)
                .Select(i => tokens[i].Text)) == column;
    }

    public static TheoryData<Position> Positions => new()
    {
        Position.BetweenConditions, Position.InsideAnIdentifier, Position.BeforeTheFirstToken, Position.AfterTheLastToken,
    };

    /// <summary>Every UTF-16 code unit, U+0000 to U+FFFF, in a DELETE.</summary>
    [Theory]
    [MemberData(nameof(Positions))]
    public void EveryCodeUnit_InADelete_IsRefused_OrParsedWithBothConditions(Position position)
    {
        var wrong = new ConcurrentBag<string>();
        Parallel.For(0, 0x10000, () => new QueryParser(), (i, state, parser) =>
        {
            var c = (char)i;
            if (Check(parser, Text(Delete, position, c), typeof(DeleteStatement), FirstColumn(position, c)) is { } problem)
                wrong.Add($"U+{i:X4}: {problem}");
            return parser;
        }, _ => { });

        wrong.OrderBy(w => w, StringComparer.Ordinal).Take(20).Should().BeEmpty();
    }

    /// <summary>
    /// Where a code unit is admitted between, before or after the tokens of a statement, the parser reads it as white
    /// space or as a statement separator, never as part of another token: it neither joins two tokens nor splits one.
    /// </summary>
    [Theory]
    [InlineData(Position.BetweenConditions)]
    [InlineData(Position.BeforeTheFirstToken)]
    [InlineData(Position.AfterTheLastToken)]
    public void AnAdmittedCodeUnitOutsideTheTokens_IsWhiteSpaceOrASemicolon(Position position)
    {
        var wrong = new ConcurrentBag<string>();
        Parallel.For(0, 0x10000, () => new QueryParser(), (i, state, parser) =>
        {
            var sql = Text(Delete, position, (char)i);
            if (parser.TryParse(sql, out var fragment, out _))
            {
                var index = position switch
                {
                    Position.BetweenConditions => (Delete + "name = 'a' ").Length,
                    Position.BeforeTheFirstToken => 0,
                    _ => sql.Length - 1,
                };
                var holder = fragment!.ScriptTokenStream.Single(t => t.TokenType != TSqlTokenType.EndOfFile &&
                                                                     t.Offset <= index && index < t.Offset + t.Text.Length);
                if (holder.TokenType != TSqlTokenType.WhiteSpace && holder.TokenType != TSqlTokenType.Semicolon)
                    wrong.Add($"U+{i:X4} is read as {holder.TokenType}");
            }
            return parser;
        }, _ => { });

        wrong.OrderBy(w => w, StringComparer.Ordinal).Take(20).Should().BeEmpty();
    }

    /// <summary>A handful of representative code units in an UPDATE and a SELECT, in the same four positions.</summary>
    public static TheoryData<string, Position, int> Representative()
    {
        var data = new TheoryData<string, Position, int>();
        int[] units =
        [
            0xFFFF, 0xFFFE, 0x0000, 0x001A, 0x0009, 0x000A, 0x000D, 0x0020, 0x0085, 0x00A0, 0x200B, 0x2028, 0x2029,
            0xFEFF, 0x3000, 0xD800, 0xDC00, 'x', ';', '.', '-', '\'', '"', '[',
        ];
        foreach (var kind in new[] { "UPDATE", "SELECT" })
            foreach (var position in Enum.GetValues<Position>())
                foreach (var unit in units)
                    data.Add(kind, position, unit);
        return data;
    }

    [Theory]
    [MemberData(nameof(Representative))]
    public void ARepresentativeCodeUnit_InAnUpdateOrASelect_IsRefused_OrParsedWithBothConditions(string kind, Position position, int unit)
    {
        var c = (char)unit;
        var (head, type) = kind == "UPDATE" ? (Update, typeof(UpdateStatement)) : (Select, typeof(SelectStatement));

        Check(new QueryParser(), Text(head, position, c), type, FirstColumn(position, c)).Should().BeNull();
    }

    // ---- U+FFFF, the code unit the parser takes as the end of the text ------------------------------------------------

    [Theory]
    [MemberData(nameof(Positions))]
    public void UFFFF_IsRefused_InEveryPosition_AndInEveryStatementKind(Position position)
    {
        foreach (var head in new[] { Delete, Update, Select })
        {
            var sql = Text(head, position, '￿');
            var parser = new QueryParser();

            var ex = FluentActions.Invoking(() => parser.Parse(sql)).Should().Throw<QueryParseException>().Which;
            parser.TryParse(sql, out var fragment, out var errors).Should().BeFalse();
            fragment.Should().BeNull();

            // Inside the identifier the text the parser reads ends at "WHERE na", which its own syntax check refuses; in
            // every other position what precedes U+FFFF is a whole statement, and only the new check refuses it.
            if (position != Position.InsideAnIdentifier)
            {
                ex.Errors.Should().ContainSingle().Which.Number.Should().Be(QueryParser.TextNotReadErrorNumber);
                errors.Should().ContainSingle().Which.Number.Should().Be(QueryParser.TextNotReadErrorNumber);
            }
        }
    }

    [Fact]
    public void TheRefusal_NamesTheCodePoint_AndWhereReadingStopped_WithoutQuotingTheText()
    {
        var ex = FluentActions.Invoking(() => new QueryParser().Parse("DELETE FROM account WHERE name = 'secret' ￿ AND name = 'b'"))
            .Should().Throw<QueryParseException>().Which;

        ex.Message.Should().Be(
            "SQL parse error at line 1, column 43: the SQL parser does not read U+FFFF at character 43 or the text after it, " +
            "which would be dropped without an error. Remove that character.");
        var error = ex.Errors.Single();
        (error.Offset, error.Line, error.Column).Should().Be((42, 1, 43));
    }

    [Fact]
    public void ThePosition_CountsLinesAndCharacters_WithASurrogatePairAsOneCharacter()
    {
        var ex = FluentActions.Invoking(() => new QueryParser().Parse("SELECT 1;\r\nSELECT N'\U0001F600' AS x ￿"))
            .Should().Throw<QueryParseException>().Which;

        var error = ex.Errors.Single();
        (error.Line, error.Column).Should().Be((2, 18));
        error.Message.Should().Contain("U+FFFF at character 29 ");
    }

    /// <summary>The other ways the parser dropped text after U+FFFF without an error, each now refused.</summary>
    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'a' -- note￿\n AND name = 'b'")]
    [InlineData("UPDATE account SET name = 'x' WHERE name = 'a'; ￿ DELETE FROM account WHERE name = 'b'")]
    [InlineData("DELETE FROM account WHERE name = 'a'\nGO\n￿\nDELETE FROM account WHERE name = 'b'")]
    [InlineData("￿DELETE FROM account WHERE name = 'a'")]
    [InlineData("SELECT name FROM account WHERE name = 'a' ￿ OR name = 'b'")]
    public void TextDroppedAfterUFFFF_IsRefused(string sql)
    {
        var parser = new QueryParser();

        FluentActions.Invoking(() => parser.Parse(sql)).Should().Throw<QueryParseException>()
            .Which.Errors.Single().Number.Should().Be(QueryParser.TextNotReadErrorNumber);
        FluentActions.Invoking(() => parser.ParseBatch(sql)).Should().Throw<QueryParseException>();
        FluentActions.Invoking(() => parser.ParseStatement(sql)).Should().Throw<QueryParseException>();
    }

    /// <summary>A text read to its end is parsed as before: several batches, comments, white space at either end, nothing.</summary>
    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'a'\nGO\nDELETE FROM account WHERE name = 'b'\n", 2)]
    [InlineData("  /* lead */ SELECT name FROM account -- trail", 1)]
    [InlineData("SELECT N'\U0001F600' AS x;\r\n", 1)]
    [InlineData("", 0)]
    public void TextReadToItsEnd_IsParsed(string sql, int statements)
    {
        new QueryParser().ParseBatch(sql).Should().HaveCount(statements);
    }

    /// <summary>The check compares the token stream with the text, so a gap or a changed token text is refused too.</summary>
    [Fact]
    public void ATokenStreamThatDoesNotCoverTheText_IsRefused()
    {
        const string sql = "SELECT 1";
        var fragment = new TSql160Parser(true).Parse(new StringReader(sql), out _);
        QueryParser.FindTextNotRead(sql, fragment).Should().BeNull();

        fragment.ScriptTokenStream[0].Text = "SELECT ";
        QueryParser.FindTextNotRead(sql, fragment).Should().NotBeNull();

        fragment.ScriptTokenStream[0].Text = "SELECT";
        fragment.ScriptTokenStream.RemoveAt(1);
        QueryParser.FindTextNotRead(sql, fragment).Should().NotBeNull();

        QueryParser.FindTextNotRead(sql, null).Should().NotBeNull();
    }
}
