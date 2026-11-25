using FluentAssertions;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Transpilation;
using Xunit;

namespace PPDS.Query.Tests.Planning;

/// <summary>
/// IN (subquery) and EXISTS are planned only as top-level AND conjuncts. Anywhere else (inside OR, parentheses or NOT)
/// the planner used to route the statement back into itself until the process died of a stack overflow (SELECT live
/// audit, 2026-11); now it is a clean "not supported" error. Planner only, no Dataverse call.
/// </summary>
[Trait("Category", "PlanUnit")]
public class SubqueryPlacementTests
{
    private readonly ExecutionPlanBuilder _builder = new(new FetchXmlGeneratorService());

    [Theory]
    [InlineData("SELECT name FROM account WHERE name = 'a' OR accountid IN (SELECT parentaccountid FROM account)")]
    [InlineData("SELECT name FROM account WHERE (accountid IN (SELECT parentaccountid FROM account))")]
    [InlineData("SELECT name FROM account WHERE NOT (accountid IN (SELECT parentaccountid FROM account))")]
    [InlineData("SELECT name FROM account WHERE name = 'a' AND (name = 'b' OR accountid IN (SELECT parentaccountid FROM account))")]
    [InlineData("SELECT name FROM account a WHERE name = 'a' OR EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)")]
    [InlineData("SELECT name FROM account a WHERE (EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid))")]
    [InlineData("SELECT name FROM account a WHERE NOT (EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid))")]
    public void SubqueryOutsideATopLevelAnd_IsACleanError_NotAStackOverflow(string sql)
    {
        var fragment = new QueryParser().Parse(sql);

        var act = () => _builder.Plan(fragment);

        act.Should().Throw<QueryParseException>().WithMessage("*is supported only as a condition joined to the rest of the WHERE clause with AND*");
    }

    [Theory]
    [InlineData("SELECT name FROM account WHERE accountid IN (SELECT parentaccountid FROM account)")]
    [InlineData("SELECT name FROM account WHERE name = 'a' AND accountid IN (SELECT parentaccountid FROM account)")]
    public void TopLevelInSubquery_StillPlans(string sql)
    {
        var fragment = new QueryParser().Parse(sql);

        _builder.Plan(fragment).RootNode.Should().NotBeNull();
    }
}
