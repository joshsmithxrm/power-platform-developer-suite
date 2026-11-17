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
}
