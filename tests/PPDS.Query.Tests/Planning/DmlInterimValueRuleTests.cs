using System.Reflection;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Transpilation;
using Xunit;

namespace PPDS.Query.Tests.Planning;

/// <summary>
/// The interim rule for values written by DML (maintainer decisions of 2026-11-19 and 2026-11-24): until the typed SQL
/// value engine proves a construct, INSERT and UPDATE write only a string, whole-number or decimal literal, NULL, a minus
/// sign on a whole-number or decimal literal, <c>CAST('&lt;guid&gt;' AS uniqueidentifier)</c> in the canonical spelling, or
/// a column of the statement's table. Everything else is refused by the planner before anything runs.
/// </summary>
[Trait("Category", "PlanUnit")]
public class DmlInterimValueRuleTests
{
    private const string Guid = "6F9619FF-8B86-D011-B42D-00C04FC964FF";

    private static void Plan(string sql)
        => new ExecutionPlanBuilder(new FetchXmlGeneratorService()).Plan(new QueryParser().Parse(sql));

    private static string? Refusal(string sql)
    {
        try
        {
            Plan(sql);
            return null;
        }
        catch (DmlSafetyRefusalException e) when (e.ErrorCode == QueryErrorCode.DmlUnsupportedConstruct)
        {
            return e.Message;
        }
    }

    /// <summary>The same value written by UPDATE SET, INSERT ... SELECT and (unless it is a column) INSERT VALUES.</summary>
    private static IEnumerable<string> Statements(string value, bool inValues = true)
    {
        yield return $"UPDATE account SET name = {value} WHERE name = 'x'";
        yield return $"INSERT INTO account (name) SELECT {value} AS v FROM account WHERE name = 'x'";
        if (inValues)
            yield return $"INSERT INTO account (name) VALUES ({value})";
    }

    // ── admitted ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("'text'")]
    [InlineData("N'naïve'")]
    [InlineData("5")]
    [InlineData("0")]
    [InlineData("2.5")]
    [InlineData("-5")]
    [InlineData("-2.5")]
    [InlineData("NULL")]
    [InlineData("name")]
    [InlineData("account.name")]
    [InlineData("revenue")]
    public void LiteralColumnOrNull_IsAdmitted(string value)
    {
        foreach (var sql in Statements(value, inValues: !value.Contains("name", StringComparison.Ordinal) && value != "revenue"))
            Refusal(sql).Should().BeNull(sql);
    }

    [Theory]
    [InlineData("CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier)")]
    [InlineData("CAST('6f9619ff-8b86-d011-b42d-00c04fc964ff' AS uniqueidentifier)")]
    [InlineData("CAST('6f9619FF-8b86-D011-b42d-00C04fc964FF' AS uniqueidentifier)")]
    [InlineData("CAST('{6F9619FF-8B86-D011-B42D-00C04FC964FF}' AS uniqueidentifier)")]
    [InlineData("CAST('{6f9619ff-8b86-d011-b42d-00c04fc964ff}' AS uniqueidentifier)")]
    [InlineData("CAST(N'6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier)")]
    [InlineData("CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS UNIQUEIDENTIFIER)")]
    public void GuidCast_InTheCanonicalSpelling_IsAdmitted(string value)
    {
        foreach (var sql in Statements(value))
            Refusal(sql).Should().BeNull(sql);
    }

    [Fact]
    public void AdmittedValues_ArePlannedIntoTheWrite()
    {
        Plan($"UPDATE account SET name = 'a', revenue = -2.5, numberofemployees = -5, description = NULL, " +
             $"parentaccountid = CAST('{{{Guid}}}' AS uniqueidentifier), telephone1 = fax WHERE name = 'x'");
        Plan($"INSERT INTO account (name, revenue, parentaccountid) VALUES (N'a', -2.5, CAST('{Guid}' AS uniqueidentifier)), ('b', 3, NULL)");
    }

    // ── the GUID cast: only the canonical spelling ────────────────────────

    /// <summary>
    /// SQL Server refuses the first four spellings (error 8169) and reads the fifth as the first 36 characters, where
    /// the engine's .NET parser accepts or refuses them differently (#1875); the rest are not GUIDs in either.
    /// </summary>
    [Theory]
    [InlineData("6F9619FF8B86D011B42D00C04FC964FF", "no dashes")]
    [InlineData("(6F9619FF-8B86-D011-B42D-00C04FC964FF)", "parentheses")]
    [InlineData(" 6F9619FF-8B86-D011-B42D-00C04FC964FF", "a leading space")]
    [InlineData("6F9619FF-8B86-D011-B42D-00C04FC964FF ", "a trailing space")]
    [InlineData("6F9619FF-8B86-D011-B42D-00C04FC964FFjunk", "characters after the 36th")]
    [InlineData("{6F9619FF-8B86-D011-B42D-00C04FC964FF", "an opening brace only")]
    [InlineData("6F9619FF-8B86-D011-B42D-00C04FC964FF}", "a closing brace only")]
    [InlineData("{6F9619FF8B86D011B42D00C04FC964FF}", "braces without dashes")]
    [InlineData("{ 6F9619FF-8B86-D011-B42D-00C04FC964FF }", "spaces inside the braces")]
    [InlineData("6F9619FF-8B86-D011-B42D-00C04FC964F", "35 characters")]
    [InlineData("6F9619FG-8B86-D011-B42D-00C04FC964FF", "a character that is not hexadecimal")]
    [InlineData("6F9619FF-8B86D011-B42D-00C04FC964FF0", "a dash in the wrong place")]
    [InlineData("{0x6F9619FF,0x8B86,0xD011,{0xB4,0x2D,0x00,0xC0,0x4F,0xC9,0x64,0xFF}}", ".NET's hexadecimal form")]
    [InlineData("", "an empty string")]
    public void GuidCast_InAnyOtherSpelling_IsRefused(string text, string why)
    {
        foreach (var sql in Statements($"CAST('{text}' AS uniqueidentifier)"))
            Refusal(sql).Should().NotBeNull($"{why}: {sql}").And.Contain("CAST('<guid>' AS uniqueidentifier)");
    }

    [Fact]
    public void GuidCast_WithATrailingLineBreak_IsRefused()
    {
        // A .NET regular expression's $ would match before a final line break; the rule must not.
        Refusal($"UPDATE account SET parentaccountid = CAST('{Guid}\n' AS uniqueidentifier) WHERE name = 'x'").Should().NotBeNull();
    }

    [Theory]
    [InlineData("CAST(name AS uniqueidentifier)")]
    [InlineData("CAST(NULL AS uniqueidentifier)")]
    [InlineData("CAST(5 AS uniqueidentifier)")]
    [InlineData("CONVERT(uniqueidentifier, '6F9619FF-8B86-D011-B42D-00C04FC964FF')")]
    [InlineData("TRY_CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier)")]
    [InlineData("CAST(('6F9619FF-8B86-D011-B42D-00C04FC964FF') AS uniqueidentifier)")]
    [InlineData("CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS nvarchar(36))")]
    public void OtherGuidConversions_AreRefused(string value)
    {
        foreach (var sql in Statements(value))
            Refusal(sql).Should().NotBeNull(sql);
    }

    // ── negative numbers: a minus sign on a number literal only ───────────

    [Theory]
    [InlineData("-revenue", "a minus sign on something other than a number literal")]
    [InlineData("-'5'", "a minus sign on something other than a number literal")]
    [InlineData("-(5)", "a minus sign on something other than a number literal")]
    [InlineData("- -5", "a minus sign on something other than a number literal")]
    [InlineData("-NULL", "a minus sign on something other than a number literal")]
    [InlineData("-2.5E0", "a minus sign on something other than a number literal")]
    [InlineData("+5", "a unary operator")]
    [InlineData("~1", "a unary operator")]
    public void AMinusSignOnAnythingButANumberLiteral_IsRefused(string value, string construct)
    {
        foreach (var sql in Statements(value))
            Refusal(sql).Should().NotBeNull(sql).And.Contain(construct);
    }

    // ── every construct family the branch used to admit, and the ones it already refused ────────────

    [Theory]
    // arithmetic
    [InlineData("revenue + 1", "(arithmetic)")]
    [InlineData("revenue - 1", "(arithmetic)")]
    [InlineData("revenue * 2", "(arithmetic)")]
    [InlineData("revenue / 2", "(arithmetic)")]
    [InlineData("revenue % 2", "(arithmetic)")]
    [InlineData("1 + 2", "(arithmetic)")]
    [InlineData("5 - -1", "(arithmetic)")]
    // string concatenation
    [InlineData("name + 'x'", "(+ with text)")]
    [InlineData("'a' + 'b'", "(+ with text)")]
    [InlineData("CONCAT(name, 'x')", "(the function CONCAT)")]
    // text functions
    [InlineData("UPPER(name)", "(the function UPPER)")]
    [InlineData("LOWER(name)", "(the function LOWER)")]
    [InlineData("LTRIM(name)", "(the function LTRIM)")]
    [InlineData("RTRIM(name)", "(the function RTRIM)")]
    [InlineData("TRIM(name)", "(the function TRIM)")]
    [InlineData("LEN(name)", "(the function LEN)")]
    [InlineData("SUBSTRING(name, 1, 2)", "(the function SUBSTRING)")]
    [InlineData("REPLACE(name, 'a', 'b')", "(the function REPLACE)")]
    [InlineData("LEFT(name, 1)", "(the function LEFT)")]
    [InlineData("RIGHT(name, 1)", "(the function RIGHT)")]
    [InlineData("UPPER('a')", "(the function UPPER)")]
    // numeric functions
    [InlineData("ABS(revenue)", "(the function ABS)")]
    [InlineData("ROUND(revenue, 1)", "(the function ROUND)")]
    [InlineData("ABS(-5)", "(the function ABS)")]
    // date and other functions
    [InlineData("GETDATE()", "(the function GETDATE)")]
    [InlineData("GETUTCDATE()", "(the function GETUTCDATE)")]
    [InlineData("DATEADD(day, 1, createdon)", "(the function DATEADD)")]
    [InlineData("NEWID()", "(the function NEWID)")]
    // conditional values
    [InlineData("ISNULL(name, 'x')", "(the function ISNULL)")]
    [InlineData("COALESCE(name, 'x')", "(COALESCE)")]
    [InlineData("NULLIF(name, 'x')", "(NULLIF)")]
    [InlineData("IIF(revenue > 1, 'a', 'b')", "(IIF)")]
    [InlineData("CASE WHEN revenue > 1 THEN 'a' ELSE 'b' END", "(CASE)")]
    [InlineData("CASE revenue WHEN 1 THEN 'a' END", "(CASE)")]
    [InlineData("COALESCE('a', 'b')", "(COALESCE)")]
    // conversions other than the GUID form
    [InlineData("CAST(revenue AS int)", "(a CAST other than CAST('<guid>' AS uniqueidentifier))")]
    [InlineData("CAST(name AS nvarchar(10))", "(a CAST other than CAST('<guid>' AS uniqueidentifier))")]
    [InlineData("CAST('5' AS decimal(10, 2))", "(a CAST other than CAST('<guid>' AS uniqueidentifier))")]
    [InlineData("CAST(5 AS nvarchar(1))", "(a CAST other than CAST('<guid>' AS uniqueidentifier))")]
    [InlineData("CONVERT(int, revenue)", "(CONVERT)")]
    [InlineData("CONVERT(nvarchar(10), revenue, 0)", "(CONVERT)")]
    [InlineData("TRY_CAST(revenue AS int)", "(TRY_CAST)")]
    [InlineData("TRY_CONVERT(int, revenue)", "(TRY_CONVERT)")]
    [InlineData("PARSE('5' AS int)", "(PARSE)")]
    // subqueries and variables
    [InlineData("(SELECT TOP 1 name FROM account)", "(a subquery)")]
    [InlineData("@v", "the variable @v")]
    [InlineData("@@ROWCOUNT", "the variable @@ROWCOUNT")]
    // parentheses and other literal kinds
    [InlineData("(5)", "(parentheses)")]
    [InlineData("('x')", "(parentheses)")]
    [InlineData("(name)", "(parentheses)")]
    [InlineData("2.5E0", "(a floating-point literal)")]
    [InlineData("$5", "(a literal of this kind)")]
    [InlineData("0x0A", "(a literal of this kind)")]
    public void EveryOtherConstruct_IsRefused_NamingIt(string value, string construct)
    {
        // No DECLARE before a variable: a script that writes may not contain one (the whole-script rule), so the
        // variable itself is what the planner names.
        foreach (var sql in Statements(value))
        {
            var message = Refusal(sql);
            message.Should().NotBeNull(sql);
            message.Should().Contain(construct, sql).And.Contain("Nothing was executed.");
        }
    }

    [Fact]
    public void AComputedValueInVALUES_SaysVALUESTakesLiteralsOnly()
        => Refusal("INSERT INTO account (name) VALUES (UPPER('a'))").Should().Contain("VALUES takes literal values only");

    [Fact]
    public void AColumnInVALUES_IsStillRefused()
        => Refusal("INSERT INTO account (name) VALUES (name)").Should().Contain("VALUES takes constants");

    [Fact]
    public void TheRefusal_SaysWhatIsAllowedUntilTheTypedEngineProvesMore()
        => Refusal("UPDATE account SET revenue = revenue * 2 WHERE name = 'x'").Should()
            .Contain("Until the typed SQL value engine proves each kind of expression");

    // ── scripts: refused before the first statement runs ──────────────────

    [Theory]
    [InlineData("INSERT INTO account (name) VALUES ('a'); UPDATE account SET name = UPPER(name) WHERE name = 'a'")]
    [InlineData("BEGIN UPDATE account SET name = 'b' WHERE name = 'a'; UPDATE account SET revenue = revenue + 1 WHERE name = 'b' END")]
    [InlineData("IF 1 = 0 UPDATE account SET name = CONCAT(name, 'x') WHERE name = 'a' ELSE DELETE FROM account WHERE name = 'a'")]
    [InlineData("BEGIN TRY DELETE FROM account WHERE name = 'a' END TRY BEGIN CATCH UPDATE account SET name = LOWER(name) WHERE name = 'b' END CATCH")]
    [InlineData("DECLARE @i int = 0; WHILE @i < 1 BEGIN UPDATE account SET revenue = -revenue WHERE name = 'a'; SET @i = @i + 1 END")]
    public void AScriptWithARefusedValueAnywhere_IsRefusedWhenItIsPlanned(string sql)
    {
        // Planning throws, so no statement of the script (not even the first, admitted one) runs.
        Refusal(sql).Should().NotBeNull();
    }

    [Fact]
    public void AScriptOfAdmittedStatements_IsPlanned()
        => Refusal("INSERT INTO account (name) VALUES ('a'); UPDATE account SET name = description WHERE name = 'a'; DELETE FROM account WHERE name = 'b'")
            .Should().BeNull();

    // ── fail closed when the parser grows ─────────────────────────────────

    /// <summary>
    /// The node kinds the rule admits. Adding one to <see cref="DmlStatementSupport.AdmittedValueNodeKinds"/> fails here
    /// until it is listed, so widening the rule is a decision made in review.
    /// </summary>
    private static readonly Type[] Listed =
    [
        typeof(StringLiteral), typeof(IntegerLiteral), typeof(NumericLiteral), typeof(NullLiteral),
        typeof(ColumnReferenceExpression), typeof(UnaryExpression), typeof(CastCall)
    ];

    [Fact]
    public void TheAdmittedNodeKinds_AreExactlyTheListedOnes()
        => DmlStatementSupport.AdmittedValueNodeKinds.Should().BeEquivalentTo(Listed);

    /// <summary>
    /// Every concrete scalar expression node kind ScriptDom defines, including any a later ScriptDom version adds (and
    /// subclasses of the admitted ones), is refused unless it is one of the listed kinds.
    /// </summary>
    [Fact]
    public void EveryOtherScalarNodeKindOfTheParser_IsRefused()
    {
        var kinds = typeof(ScalarExpression).Assembly.GetTypes()
            .Where(t => typeof(ScalarExpression).IsAssignableFrom(t) && t is { IsAbstract: false, IsClass: true })
            .ToList();
        kinds.Should().HaveCountGreaterThan(30, "the parser defines many scalar node kinds");

        foreach (var kind in kinds.Where(k => !Listed.Contains(k)))
        {
            var node = (ScalarExpression)(Activator.CreateInstance(kind, nonPublic: true)
                ?? System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(kind));
            DmlStatementSupport.IsAdmittedValue(node).Should().BeFalse($"{kind.Name} is not a listed node kind");
        }
    }

    [Fact]
    public void AListedKind_IsAdmittedOnlyInItsAdmittedForm()
    {
        DmlStatementSupport.IsAdmittedValue(new UnaryExpression { UnaryExpressionType = UnaryExpressionType.Negative, Expression = new IntegerLiteral { Value = "5" } }).Should().BeTrue();
        DmlStatementSupport.IsAdmittedValue(new UnaryExpression { UnaryExpressionType = UnaryExpressionType.Positive, Expression = new IntegerLiteral { Value = "5" } }).Should().BeFalse();
        DmlStatementSupport.IsAdmittedValue(new UnaryExpression { UnaryExpressionType = UnaryExpressionType.Negative, Expression = new RealLiteral { Value = "5E0" } }).Should().BeFalse();
        DmlStatementSupport.IsAdmittedValue(new UnaryExpression()).Should().BeFalse();
        DmlStatementSupport.IsAdmittedValue(new CastCall()).Should().BeFalse();
        DmlStatementSupport.IsAdmittedValue(new CastCall
        {
            DataType = new SqlDataTypeReference { SqlDataTypeOption = SqlDataTypeOption.UniqueIdentifier },
            Parameter = new StringLiteral { Value = Guid }
        }).Should().BeTrue();
        DmlStatementSupport.IsAdmittedValue(new CastCall
        {
            DataType = new SqlDataTypeReference { SqlDataTypeOption = SqlDataTypeOption.NVarChar },
            Parameter = new StringLiteral { Value = Guid }
        }).Should().BeFalse();
        DmlStatementSupport.IsAdmittedValue(new ColumnReferenceExpression { ColumnType = ColumnType.Wildcard }).Should().BeFalse();
    }

    // ---- text without N (maintainer decision, 2026-11-24, "Refuse it") ----------------------------------------

    [Theory]
    [InlineData("abc ~!@#$%^&*()_+{}|:<>?", false, true)]
    [InlineData("\u007F", false, true)]                  // the last 7-bit character
    [InlineData("\u0080", false, false)]                 // the first one outside
    [InlineData("na\u00EFve", false, false)]             // one accented Latin character: refused, the conservative reading
    [InlineData("\u041F\u0440\u0438\u0432\u0435\u0442", false, false)] // Cyrillic
    [InlineData("\u6F22\u5B57", false, false)]           // CJK
    [InlineData("\uD83D\uDE00", false, false)]           // outside the BMP (a surrogate pair)
    [InlineData("na\u00EFve", true, true)]
    [InlineData("\u041F\u0440\u0438\u0432\u0435\u0442", true, true)]
    [InlineData("", false, true)]
    public void ATextLiteral_IsAdmitted_WhenItHasNOrIsSevenBitAscii(string text, bool national, bool admitted)
        => DmlStatementSupport.IsAdmittedValue(new StringLiteral { Value = text, IsNational = national }).Should().Be(admitted);

    [Theory]
    [InlineData("UPDATE account SET name = '\u041F\u0440\u0438\u0432\u0435\u0442' WHERE name = 'x'", "UPDATE ... SET")]
    [InlineData("UPDATE account SET name = 'na\u00EFve' WHERE name = 'x'", "UPDATE ... SET")]
    [InlineData("INSERT INTO account (name) VALUES ('na\u00EFve')", "INSERT ... VALUES")]
    [InlineData("INSERT INTO account (name) SELECT 'na\u00EFve' AS v FROM account WHERE name = 'x'", "INSERT ... SELECT")]
    public void ATextLiteralWithoutN_OutsideSevenBitAscii_IsRefused_AndTheMessageNamesTheNForm(string sql, string context)
    {
        var refusal = Refusal(sql);

        refusal.Should().Contain("(text without the N prefix that holds characters outside 7-bit ASCII)")
            .And.Contain($"is not supported in {context}.")
            .And.Contain("Nothing was executed.")
            .And.Contain("Write it with the N prefix: N'");
    }

    [Theory]
    [InlineData("UPDATE account SET name = N'na\u00EFve' WHERE name = 'x'")]
    [InlineData("INSERT INTO account (name) VALUES (N'\u041F\u0440\u0438\u0432\u0435\u0442')")]
    [InlineData("UPDATE account SET name = 'plain ascii' WHERE name = 'x'")]
    // In a condition, text with N is sent to Dataverse as typed.
    [InlineData("UPDATE account SET name = 'x' WHERE name = N'na\u00EFve'")]
    [InlineData("DELETE FROM account WHERE name IN (N'\u041F\u0440\u0438\u0432\u0435\u0442', 'x')")]
    [InlineData("DELETE FROM account WHERE name LIKE N'\u041F%'")]
    [InlineData("DELETE FROM account WHERE name BETWEEN N'\u00E0' AND N'\u00E9'")]
    public void TextWithN_AndAsciiWithoutN_AreAdmitted_InValuesAndConditions(string sql)
        => Refusal(sql).Should().BeNull();

    /// <summary>
    /// Text without N that is not 7-bit ASCII, in the condition of a write (coordinator's conservative reading of the
    /// maintainer's "Refuse it", applied to conditions): SQL Server converts it to the code page before comparing, so it
    /// would select other rows than the text says.
    /// </summary>
    [Theory]
    [InlineData("UPDATE account SET name = N'x' WHERE name = '\u041F\u0440\u0438\u0432\u0435\u0442'", "the WHERE clause of UPDATE")]
    [InlineData("UPDATE account SET name = N'x' WHERE name <> 'na\u00EFve'", "the WHERE clause of UPDATE")]
    [InlineData("DELETE FROM account WHERE name IN ('x', '\u041F\u0440\u0438\u0432\u0435\u0442')", "the WHERE clause of DELETE")]
    [InlineData("DELETE FROM account WHERE name LIKE '\u041F%'", "the WHERE clause of DELETE")]
    [InlineData("DELETE FROM account WHERE name BETWEEN 'a' AND 'na\u00EFve'", "the WHERE clause of DELETE")]
    [InlineData("DELETE FROM account WHERE name = 'x' OR (name = '\u00E9t\u00E9' AND revenue > 1)", "the WHERE clause of DELETE")]
    [InlineData("INSERT INTO account (name) SELECT name FROM account WHERE name = '\u041F\u0440\u0438\u0432\u0435\u0442'", "the WHERE clause of INSERT ... SELECT")]
    public void TextWithoutN_OutsideSevenBitAscii_InTheConditionOfAWrite_IsRefused_AndNamesTheNForm(string sql, string context)
    {
        var refusal = Refusal(sql);

        refusal.Should().Contain("(text without the N prefix that holds characters outside 7-bit ASCII)")
            .And.Contain($"is not supported in {context}.")
            .And.Contain("Write it with the N prefix: N'")
            .And.Contain("before comparing");
    }

    [Fact]
    public void TheCanonicalGuidCheck_IsTheOneTheRuleUses()
    {
        DmlStatementSupport.IsCanonicalGuidText(Guid).Should().BeTrue();
        DmlStatementSupport.IsCanonicalGuidText("{" + Guid + "}").Should().BeTrue();
        DmlStatementSupport.IsCanonicalGuidText(Guid + "\n").Should().BeFalse();
        DmlStatementSupport.IsCanonicalGuidText(" " + Guid).Should().BeFalse();
    }
}
