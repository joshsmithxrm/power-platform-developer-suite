using FluentAssertions;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Transpilation;
using Xunit;

namespace PPDS.Query.Tests.Planning;

/// <summary>
/// The whole-script rule (maintainer decision M, "One whole-script rule"): a script that contains a write runs only when
/// every statement in it is an admitted INSERT, UPDATE or DELETE. The planner refuses anything else before it plans or runs
/// anything, with <see cref="QueryErrorCode.DmlUnsupportedConstruct"/>, naming the first statement that is not admitted
/// and where it is. And the codes per refusal class (decision G), the values Dataverse stores differently, numbers PPDS
/// cannot hold, and the gate that the rows of a write are selected by Dataverse alone.
/// </summary>
[Trait("Category", "PlanUnit")]
public class DmlWholeScriptRuleTests
{
    private static void Plan(string sql)
        => new ExecutionPlanBuilder(new FetchXmlGeneratorService()).Plan(new QueryParser().Parse(sql));

    private static DmlSafetyRefusalException Refused(string sql)
        => FluentActions.Invoking(() => Plan(sql)).Should().Throw<DmlSafetyRefusalException>().Which;

    /// <summary>The cases the review proved, and every other statement kind of the decision's list.</summary>
    public static TheoryData<string, string> NotAdmittedInAScriptThatWrites => new()
    {
        // IF and WHILE: their conditions are evaluated by the engine ('a ' <> 'a' is false on SQL Server, true here)
        { "IF 'a ' <> 'a' DELETE FROM account WHERE name = 'n1'", "The statement (line 1, column 1) is an IF statement" },
        { "IF 'a ' = 'a' DELETE FROM account WHERE name = 'n1'", "is an IF statement" },
        { "IF N'\u00DF' = N'ss' DELETE FROM account WHERE name = 'n1'", "is an IF statement" },
        { "DELETE FROM account WHERE name = 'n1'; IF 'a ' <> 'a' DELETE FROM account WHERE name = 'n2'", "Statement 2 (line 1, column 40) is an IF statement" },
        { "DELETE FROM account WHERE name = 'n1'; DECLARE @x int = 1; IF @x = 1 DELETE FROM account WHERE name = 'n2'", "Statement 2 (line 1, column 40) is DECLARE" },
        { "DELETE FROM account WHERE name = 'n1'; SELECT * INTO #t FROM account", "Statement 2 (line 1, column 40) is SELECT ... INTO" },
        { "SELECT name FROM account WHERE revenue * 2 > 1500; DELETE FROM account WHERE name = 'n1'", "Statement 1 (line 1, column 1) is a SELECT" },
        { "SELECT name FROM account WHERE name = 'x'; DELETE FROM account WHERE name = 'n1'", "is a SELECT" },
        { "DECLARE @i int = 0; WHILE @i < 1 BEGIN DELETE FROM account WHERE name = 'n1'; SET @i = @i + 1 END", "is DECLARE" },
        { "WHILE 1 = 0 DELETE FROM account WHERE name = 'n1'", "is a WHILE loop" },
        { "DELETE FROM account WHERE name = 'n1'; PRINT 'done'", "is PRINT" },
        { "DELETE FROM account WHERE name = 'n1'; THROW 50000, 'x', 1", "is THROW" },
        { "BEGIN TRY SELECT 1/0 END TRY BEGIN CATCH DELETE FROM account WHERE name = 'n1' END CATCH", "is TRY ... CATCH" },
        { "DELETE FROM account WHERE name = 'n1'; RETURN", "is RETURN" },
        { "DECLARE c CURSOR FOR SELECT name FROM account; DELETE FROM account WHERE name = 'n1'", "is a cursor statement" },
        { "EXEC WhoAmI; DELETE FROM account WHERE name = 'n1'", "is EXECUTE" },
        { "CREATE TABLE #t (name nvarchar(10)); DELETE FROM account WHERE name = 'n1'", "is CREATE TABLE" },
        { "DECLARE @t TABLE (name nvarchar(10)); DELETE FROM account WHERE name = 'n1'", "is DECLARE of a table variable" },
        { "BEGIN DELETE FROM account WHERE name = 'n1' END", "is a BEGIN ... END block" },
        { "DECLARE @x int; SET @x = 1; DELETE FROM account WHERE name = 'n1'", "is DECLARE" },
        { "DELETE FROM account WHERE name = 'n1'; DROP TABLE #t", "is DROP TABLE" },
    };

    [Theory]
    [MemberData(nameof(NotAdmittedInAScriptThatWrites))]
    public void AScriptThatWrites_WithAnythingButAdmittedDml_IsRefusedBeforeAnythingIsPlanned(string sql, string named)
    {
        var refusal = Refused(sql);

        refusal.ErrorCode.Should().Be(QueryErrorCode.DmlUnsupportedConstruct);
        refusal.Message.Should().Contain(named)
            .And.Contain("This script changes data")
            .And.Contain("Nothing was executed.")
            .And.Contain("No row was read and nothing was written.");
    }

    [Fact]
    public void TheRefusal_NamesTheFirstOffendingStatement_ByLineAndColumn()
    {
        var refusal = Refused("DELETE FROM account WHERE name = 'n1';\nUPDATE account SET name = 'x' WHERE name = 'n2';\n  PRINT 'a';\nIF 1 = 1 PRINT 'b'");

        refusal.Message.Should().Contain("Statement 3 (line 3, column 3) is PRINT").And.NotContain("IF statement");
    }

    [Fact]
    public void AStatementRefusedByTheAllowlist_InAScript_IsNamedWithItsPosition()
    {
        var refusal = Refused("DELETE FROM account WHERE name = 'n1'; UPDATE account SET name = UPPER(name) WHERE name = 'n2'");

        refusal.ErrorCode.Should().Be(QueryErrorCode.DmlUnsupportedConstruct);
        refusal.Message.Should().StartWith("Statement 2 (line 1, column 40): ").And.Contain("the function UPPER");
    }

    [Fact]
    public void AScriptOfAdmittedWritesOnly_IsPlanned()
        => FluentActions.Invoking(() => Plan(
                "DELETE FROM account WHERE name = 'n1'; UPDATE account SET name = 'x' WHERE name = 'n2'; " +
                "INSERT INTO account (name) VALUES (N'y')"))
            .Should().NotThrow();

    [Theory]
    [InlineData("DECLARE @x int = 1; IF @x = 1 SELECT name FROM account")]
    [InlineData("SELECT name FROM account WHERE revenue * 2 > 1500; PRINT 'x'")]
    [InlineData("BEGIN TRY SELECT name FROM account END TRY BEGIN CATCH PRINT 'x' END CATCH")]
    public void AScriptWithNoWrite_IsNotAffected(string sql)
        => DmlStatementSupport.FindScriptRefusal(new QueryParser().Parse(sql)).Should().BeNull();

    [Fact]
    public void TheAdmittedStatementKinds_AreAClosedList_OfInsertUpdateAndDelete()
        => DmlStatementSupport.AdmittedScriptStatementKinds.Should().BeEquivalentTo(new[]
        {
            typeof(Microsoft.SqlServer.TransactSql.ScriptDom.InsertStatement),
            typeof(Microsoft.SqlServer.TransactSql.ScriptDom.UpdateStatement),
            typeof(Microsoft.SqlServer.TransactSql.ScriptDom.DeleteStatement)
        });

    [Fact]
    public void MergeAlone_KeepsItsOwnRefusal()
        => Refused("MERGE account AS t USING account AS s ON t.name = s.name WHEN MATCHED THEN DELETE;")
            .Message.Should().StartWith("MERGE is not supported");

    // ---- a code per refusal class (decision G) --------------------------------------------------------------------

    [Theory]
    [InlineData("UPDATE account SET name = UPPER(name) WHERE name = 'x'", QueryErrorCode.DmlUnsupportedConstruct)]
    [InlineData("DELETE TOP (1) FROM account WHERE name = 'x'", QueryErrorCode.DmlUnsupportedConstruct)]
    [InlineData("UPDATE account SET name = '' WHERE name = 'x'", QueryErrorCode.DmlValueStoredDifferently)]
    [InlineData("IF 1 = 1 DELETE FROM account WHERE name = 'x'", QueryErrorCode.DmlUnsupportedConstruct)]
    public void EachRefusalClass_HasItsOwnCode(string sql, string code)
        => Refused(sql).ErrorCode.Should().Be(code);

    [Fact]
    public void TheCodes_AreNamedLikeTheExistingDmlCodes()
    {
        QueryErrorCode.DmlUnsupportedConstruct.Should().Be("Query.DmlUnsupportedConstruct");
        QueryErrorCode.DmlValueStoredDifferently.Should().Be("Query.DmlValueStoredDifferently");
        QueryErrorCode.DmlMetadataUnavailable.Should().Be("Query.DmlMetadataUnavailable");
    }

    // ---- values Dataverse stores differently (sql-values AC-10, core requirement 4) -------------------------------

    private static IEnumerable<string> Writes(string value)
    {
        yield return $"UPDATE account SET name = {value} WHERE name = 'x'";
        yield return $"INSERT INTO account (name) VALUES ({value})";
        yield return $"INSERT INTO account (name) SELECT {value} AS v FROM account WHERE name = 'x'";
    }

    [Theory]
    [InlineData("''")]
    [InlineData("N''")]
    public void EmptyText_IsRefused_AndTheMessageSaysToWriteNull(string value)
    {
        foreach (var sql in Writes(value))
        {
            var refusal = Refused(sql);
            refusal.ErrorCode.Should().Be(QueryErrorCode.DmlValueStoredDifferently, sql);
            refusal.Message.Should().Contain("Dataverse stores empty text as NULL").And.Contain("Write NULL instead.", sql);
        }
    }

    [Theory]
    [InlineData("' '")]
    [InlineData("'   '")]
    [InlineData("' a'")]
    [InlineData("'a '")]
    [InlineData("N' a'")]
    [InlineData("'\ta'")]
    [InlineData("N'a\u00A0'")]
    [InlineData("N'\u3000a'")]
    [InlineData("N'a\u2028'")]
    [InlineData("'a\n'")]
    public void TextThatIsWhiteSpaceOrHasItAtEitherEnd_IsRefused_UntilB25AndB26AreInForce(string value)
    {
        foreach (var sql in Writes(value))
        {
            var refusal = Refused(sql);
            refusal.ErrorCode.Should().Be(QueryErrorCode.DmlValueStoredDifferently, sql);
            refusal.Message.Should().Contain("is not written in").And.Contain("Dataverse has not been shown to store such text unchanged")
                .And.Contain("Remove the white space at either end (or the invisible character), or write NULL.");
        }
    }

    [Theory]
    [InlineData("'a b'")]
    [InlineData("N'a\u00A0b'")]
    [InlineData("'x'")]
    public void TextWithWhiteSpaceInside_IsWritten(string value)
    {
        foreach (var sql in Writes(value))
            FluentActions.Invoking(() => Plan(sql)).Should().NotThrow(sql);
    }

    [Fact]
    public void TheWhiteSpaceRule_IsOneSwitch_ThatIsOffUntilTheRowsComeIntoForce()
        => DmlStatementSupport.WhiteSpaceEdgedTextIsWritten.Should().BeFalse();

    [Theory]
    [InlineData(" ", true)]
    [InlineData("a\u0085", true)]
    [InlineData("\u1680a", true)]
    [InlineData("a\u200A", true)]
    [InlineData("a\u202F", true)]
    [InlineData("\u205Fa", true)]
    [InlineData("a\u2029", true)]
    // Not White_Space, but invisible: refused at either end as white space is (review round 6, S12); inside the text, written.
    [InlineData("a\u200B", true)]
    [InlineData("\u180Ea", true)]
    [InlineData("a\uFEFF", true)]
    [InlineData("a\u200Bb", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void WhiteSpace_IsTheUnicodeWhiteSpaceProperty_AndThreeInvisibleCharacters(string text, bool edged)
        => DmlStatementSupport.IsWhiteSpaceEdged(text).Should().Be(edged);

    // ---- numbers PPDS cannot hold (review S3) -----------------------------------------------------------------------

    [Theory]
    [InlineData("UPDATE account SET revenue = 100000000000000000000000000000 WHERE name = 'x'")]
    [InlineData("UPDATE account SET revenue = -100000000000000000000000000000 WHERE name = 'x'")]
    [InlineData("INSERT INTO account (revenue) VALUES (1234567890123456789012345678901.5)")]
    [InlineData("DELETE FROM account WHERE revenue = 100000000000000000000000000000")]
    [InlineData("DELETE FROM account WHERE revenue IN (1, 100000000000000000000000000000)")]
    public void ANumberWithMoreDigitsThanPpdsCanHold_IsRefusedWithACode(string sql)
    {
        var refusal = Refused(sql);

        refusal.ErrorCode.Should().Be(QueryErrorCode.DmlUnsupportedConstruct);
        refusal.Message.Should().Contain("a number with more digits than PPDS can hold").And.Contain("at most 28 significant digits");
    }

    [Fact]
    public void ANumberThatFits_IsWritten()
        => FluentActions.Invoking(() => Plan("UPDATE account SET revenue = 79228162514264337593543950335 WHERE name = 'x'"))
            .Should().NotThrow();

    // ---- the rows of a write are selected by Dataverse alone (review MF7) ------------------------------------------

    private static FetchXmlScanNode Scan() => new("<fetch><entity name=\"account\"><attribute name=\"accountid\" /></entity></fetch>", "account");

    [Fact]
    public void TheSourceGate_RefusesAPlanWithAClientSideNode()
    {
        var filtered = new ClientFilterNode(Scan(), _ => true, "revenue * 2 > 1500");

        var refusal = FluentActions.Invoking(() => ExecutionPlanBuilder.EnsureDataverseEvaluatesTheSource(filtered, "DELETE", allowProjection: false))
            .Should().Throw<DmlSafetyRefusalException>().Which;

        refusal.ErrorCode.Should().Be(QueryErrorCode.DmlUnsupportedConstruct);
        refusal.Message.Should().Contain("cannot be selected by Dataverse alone");
    }

    [Fact]
    public void TheSourceGate_RefusesAProjectionWhereNoneIsAllowed_AndAdmitsOneScan()
    {
        var projected = new ProjectNode(Scan(), []);

        FluentActions.Invoking(() => ExecutionPlanBuilder.EnsureDataverseEvaluatesTheSource(projected, "UPDATE", allowProjection: false))
            .Should().Throw<DmlSafetyRefusalException>();
        FluentActions.Invoking(() => ExecutionPlanBuilder.EnsureDataverseEvaluatesTheSource(projected, "INSERT", allowProjection: true))
            .Should().NotThrow();
        FluentActions.Invoking(() => ExecutionPlanBuilder.EnsureDataverseEvaluatesTheSource(Scan(), "DELETE", allowProjection: false))
            .Should().NotThrow();
        FluentActions.Invoking(() => ExecutionPlanBuilder.EnsureDataverseEvaluatesTheSource(new PrefetchScanNode(Scan()), "DELETE", allowProjection: false))
            .Should().NotThrow();
    }

    // ---- the SELECT refusals in a CTE and a JOIN ON (review S6) -----------------------------------------------------

    [Theory]
    [InlineData("WITH t AS (SELECT TOP 1 WITH TIES name FROM account ORDER BY name) SELECT name FROM t")]
    [InlineData("WITH t AS (SELECT TOP 50 PERCENT name FROM account) SELECT name FROM t")]
    public void TopWithTiesOrPercent_InACte_IsRefused_NamingTheReplacement(string sql)
        => FluentActions.Invoking(() => Plan(sql)).Should().Throw<QueryParseException>()
            .WithMessage("*for example TOP (3), without PERCENT or WITH TIES*");

    [Theory]
    [InlineData("SELECT a.name FROM account a JOIN contact c ON c.lastname = NULL")]
    [InlineData("SELECT a.name FROM account a JOIN contact c ON c.parentcustomerid = a.accountid AND c.lastname = NULL")]
    public void ComparingWithNull_InAJoinOn_IsRefused_NamingTheReplacement(string sql)
        => FluentActions.Invoking(() => Plan(sql)).Should().Throw<QueryParseException>()
            .WithMessage("*Use IS NULL or IS NOT NULL*");
}
