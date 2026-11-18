using FluentAssertions;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Query.Execution;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using PPDS.Query.Transpilation;
using Xunit;

namespace PPDS.Query.Tests.Planning;

/// <summary>
/// The DML allowlist is enforced by the planner itself, without the CLI guard, so explain, scripts and the ADO.NET
/// provider cannot plan a statement the engine does not honour.
/// </summary>
[Trait("Category", "PlanUnit")]
public class DmlAllowlistPlannerTests
{
    private static QueryPlanResult Plan(string sql, SessionContext? session = null)
        => new ExecutionPlanBuilder(new FetchXmlGeneratorService(), session).Plan(new QueryParser().Parse(sql));

    [Theory]
    [InlineData("DELETE TOP (2) FROM account WHERE name = 'x'", "TOP")]
    [InlineData("DELETE FROM account WHERE NOT (name = 'x')", "NOT")]
    [InlineData("UPDATE account SET name = GETDATE() WHERE name = 'x'", "GETDATE")]
    [InlineData("UPDATE account SET name = 'y' FROM account JOIN contact ON contact.parentcustomerid = account.accountid", "FROM is supported only")]
    [InlineData("DELETE account FROM (SELECT accountid FROM account) AS account", "FROM is supported only")]
    [InlineData("INSERT INTO account (name) SELECT UPPER(name) FROM account", "needs an alias")]
    [InlineData("INSERT INTO [QA].dbo.account (name) VALUES ('a')", "another environment")]
    [InlineData("MERGE account AS t USING contact AS s ON t.name = s.lastname WHEN NOT MATCHED THEN INSERT (name) VALUES (s.lastname);", "MERGE")]
    [InlineData("BEGIN TRY DELETE TOP (1) FROM account WHERE name = 'x' END TRY BEGIN CATCH END CATCH", "TOP")]
    public void UnsupportedDml_IsRefusedByThePlanner(string sql, string named)
    {
        // Scripts are planned lazily, statement by statement, so plan and execute the first data statement.
        var act = () => PlanAndPlanInner(sql);

        act.Should().Throw<DmlSafetyRefusalException>()
            .Where(e => e.ErrorCode == QueryErrorCode.DmlBlocked && e.Message.Contains(named));
    }

    private static void PlanAndPlanInner(string sql)
    {
        var result = Plan(sql);
        if (result.RootNode is ScriptExecutionNode script)
            _ = script.BuildDryRunPreview(); // plans every data statement of the script
    }

    [Fact]
    public void ImpersonationIsRefusedByThePlanner_EvenWithASession()
    {
        var act = () => Plan("EXECUTE AS USER = 'someone@contoso.com'", new SessionContext());

        act.Should().Throw<DmlSafetyRefusalException>().Where(e => e.Message.Contains("EXECUTE AS"));
    }

    [Fact]
    public void AliasTarget_IsPlannedAgainstTheAliasedTable()
    {
        var result = Plan("UPDATE a SET name = UPPER(a.name) FROM account a WHERE a.name = 'x'");

        var node = result.RootNode.Should().BeOfType<DmlExecuteNode>().Subject;
        node.EntityLogicalName.Should().Be("account");
        result.FetchXml.Should().Contain("<attribute name=\"accountid\"").And.Contain("<attribute name=\"name\"");
    }

    [Fact]
    public void SetExpression_FetchesEveryColumnItReads()
    {
        var result = Plan("UPDATE account SET name = COALESCE(description, NULLIF(name, 'x'), CASE revenue WHEN 1 THEN 'a' END) WHERE name = 'x'");

        foreach (var column in new[] { "description", "name", "revenue" })
            result.FetchXml.Should().Contain($"<attribute name=\"{column}\"");
    }

    [Fact]
    public void MoreThanOneBatch_IsRefusedByThePlanner()
    {
        var act = () => Plan("SELECT name FROM account\nGO\nDELETE FROM account WHERE name = 'x'");

        act.Should().Throw<QueryParseException>().WithMessage("*more than one batch*");
    }

    // ---- round 1: shapes not proven live are refused by the planner too ------------------------------

    [Theory]
    [InlineData("UPDATE account SET name = 'x' WHERE name = 'y' OPTION (MAXDOP 1)", "OPTION")]
    [InlineData("DELETE FROM account WHERE revenue = 2.5E0", "floating-point literal")]
    [InlineData("UPDATE account SET revenue = 2.5E0 WHERE name = 'y'", "floating-point literal")]
    [InlineData("UPDATE account SET name = CAST(name AS nvarchar) WHERE name = 'y'", "nvarchar(n) with a length")]
    [InlineData("UPDATE account SET name = CAST(name AS nvarchar(max)) WHERE name = 'y'", "nvarchar(n) with a length")]
    [InlineData("UPDATE account SET name = CONVERT(nvarchar, revenue) WHERE name = 'y'", "nvarchar(n) with a length")]
    [InlineData("UPDATE account SET revenue = CAST(revenue AS decimal) WHERE name = 'y'", "decimal(p, s)")]
    [InlineData("UPDATE account SET revenue = CAST(revenue AS decimal(10)) WHERE name = 'y'", "decimal(p, s)")]
    [InlineData("UPDATE account SET revenue = CAST(revenue AS numeric(10, 2)) WHERE name = 'y'", "decimal(p, s)")]
    [InlineData("UPDATE account SET revenue = TRY_CAST(revenue AS decimal) WHERE name = 'y'", "int or decimal(p, s)")]
    [InlineData("UPDATE account SET name = TRIM(TRAILING FROM name) WHERE name = 'y'", "LEADING, TRAILING, BOTH or FROM")]
    [InlineData("UPDATE account SET name = TRIM('x' FROM name) WHERE name = 'y'", "LEADING, TRAILING, BOTH or FROM")]
    [InlineData("UPDATE account SET name = LTRIM(name, 'x') WHERE name = 'y'", "takes one argument")]
    [InlineData("UPDATE account SET name = SUBSTRING(name, 1, -1) WHERE name = 'y'", "SUBSTRING a start of 1")]
    [InlineData("UPDATE account SET name = SUBSTRING(name, 0, 3) WHERE name = 'y'", "SUBSTRING a start of 1")]
    [InlineData("UPDATE account SET name = SUBSTRING(name, revenue, 3) WHERE name = 'y'", "SUBSTRING a start of 1")]
    [InlineData("UPDATE account SET revenue = ROUND(revenue, -1) WHERE name = 'y'", "number of decimal places of 0 or more")]
    [InlineData("UPDATE account SET revenue = ROUND(revenue, 1, 1) WHERE name = 'y'", "number of decimal places of 0 or more")]
    [InlineData("INSERT INTO account (name) VALUES (UPPER('a'))", "literal values only")]
    [InlineData("INSERT INTO account (numberofemployees) VALUES (1 + 2)", "literal values only")]
    [InlineData("INSERT INTO account (name) VALUES (CONCAT('b', 1))", "literal values only")]
    [InlineData("INSERT INTO account (revenue) VALUES (CAST(5 AS decimal(10, 2)))", "literal values only")]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account", "TOP without ORDER BY")]
    [InlineData("INSERT INTO account (name) SELECT TOP 5001 name FROM account ORDER BY name", "at most 5000")]
    [InlineData("INSERT INTO account (name) SELECT name FROM contact", "from another table")]
    [InlineData("INSERT INTO account (description, name) SELECT name AS description, UPPER(description) AS u FROM account", "also the name of a column")]
    public void NotProvenLive_IsRefusedByThePlanner(string sql, string named)
    {
        var act = () => PlanAndPlanInner(sql);

        act.Should().Throw<DmlSafetyRefusalException>()
            .Where(e => e.ErrorCode == QueryErrorCode.DmlBlocked && e.Message.Contains(named));
    }

    [Fact]
    public void InListLongerThanProven_IsRefused()
    {
        var values = string.Join(", ", Enumerable.Range(0, DmlStatementSupport.MaxInValues + 1));

        var act = () => Plan($"DELETE FROM account WHERE numberofemployees IN ({values})");

        act.Should().Throw<DmlSafetyRefusalException>().Where(e => e.Message.Contains("at most 1000 values"));
    }

    [Fact]
    public void InListOfExactlyTheProvenLength_IsPlanned()
    {
        var values = string.Join(", ", Enumerable.Range(0, DmlStatementSupport.MaxInValues));

        Plan($"DELETE FROM account WHERE numberofemployees IN ({values})").RootNode.Should().BeOfType<DmlExecuteNode>();
    }

    [Fact]
    public void ValuesWithMoreRowsThanSqlServerAllows_IsRefused()
    {
        var rows = string.Join(", ", Enumerable.Range(0, DmlStatementSupport.MaxValuesRows + 1).Select(i => $"('r{i}')"));

        var act = () => Plan($"INSERT INTO account (name) VALUES {rows}");

        act.Should().Throw<DmlSafetyRefusalException>().Where(e => e.Message.Contains("at most 1000"));
    }

    [Theory]
    [InlineData("INSERT INTO account (name, revenue, accountid) VALUES ('a', -2.5, CAST('00000000-0000-0000-0000-000000000001' AS uniqueidentifier))")]
    [InlineData("INSERT INTO account (name) SELECT TOP (0) name FROM account ORDER BY name")]
    [InlineData("INSERT INTO account (name) SELECT name FROM account ORDER BY name")]
    [InlineData("INSERT INTO account (name, description) SELECT s.name, CONCAT(s.name, '!') AS c FROM account s WHERE s.name LIKE 'a%' OR s.revenue BETWEEN 1 AND 2 ORDER BY s.name, s.revenue")]
    [InlineData("UPDATE account SET name = SUBSTRING(name, 1, 0), revenue = ROUND(revenue, 2) WHERE name = 'y'")]
    [InlineData("UPDATE account SET name = CAST(name AS nvarchar(3)), revenue = TRY_CONVERT(decimal(3, 2), revenue) WHERE name = 'y'")]
    public void ProvenShapes_ArePlanned(string sql)
    {
        PlanAndPlanInner(sql);
    }

    // ---- B-S2: the engine itself never applies row limits or paging to the rows DML selects -------------

    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'x'")]
    [InlineData("UPDATE account SET name = 'y' WHERE name = 'x'")]
    [InlineData("INSERT INTO account (name) SELECT name FROM account WHERE name = 'x'")]
    public void RowLimitsOrPaging_AreRefusedForDml_InThePlanner(string sql)
    {
        var builder = new ExecutionPlanBuilder(new FetchXmlGeneratorService());
        foreach (var options in new[]
                 {
                     new QueryPlanOptions { MaxRows = 10 },
                     new QueryPlanOptions { PageNumber = 2 },
                     new QueryPlanOptions { PagingCookie = "<cookie/>" }
                 })
        {
            var act = () => builder.Plan(new QueryParser().Parse(sql), options);

            act.Should().Throw<DmlSafetyRefusalException>().Where(e => e.Message.Contains("row limit, page number or paging cookie"));
        }
    }

    // ---- B-S7: upper-case and bracketed names are written as Dataverse logical names -------------------

    [Fact]
    public void UpperCaseAndBracketedNames_ArePlannedAsLogicalNames()
    {
        var node = Plan("UPDATE [ACCOUNT] SET [NAME] = 'x' WHERE [NAME] = 'y'").RootNode.Should().BeOfType<DmlExecuteNode>().Subject;

        node.EntityLogicalName.Should().Be("account");
        node.SetClauses!.Single().ColumnName.Should().Be("name");
    }

    [Fact]
    public void UpperCaseInsert_IsPlannedAsLogicalNames()
    {
        var node = Plan("INSERT INTO ACCOUNT (NAME) VALUES ('x')").RootNode.Should().BeOfType<DmlExecuteNode>().Subject;

        node.EntityLogicalName.Should().Be("account");
        node.InsertColumns!.Single().Should().Be("name");
    }
}
