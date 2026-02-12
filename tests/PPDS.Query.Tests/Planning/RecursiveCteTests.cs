using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class RecursiveCteTests
{
    private readonly QueryParser _parser = new();
    private readonly ExecutionPlanBuilder _builder;

    public RecursiveCteTests()
    {
        var mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
        mockFetchXmlService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Returns(TranspileResult.Simple(
                "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

        _builder = new ExecutionPlanBuilder(mockFetchXmlService.Object);
    }

    // ────────────────────────────────────────────
    //  Recursive CTE produces RecursiveCteNode
    // ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_RecursiveCte_ProducesRecursiveCteNode()
    {
        var sql = @"
            WITH hierarchy AS (
                SELECT 1 AS level, 'root' AS name
                UNION ALL
                SELECT h.level + 1, 'child' AS name
                FROM hierarchy h
                WHERE h.level < 3
            )
            SELECT * FROM hierarchy";

        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        // Should contain a RecursiveCteNode somewhere in the plan tree
        var recursiveNode = FindNode<RecursiveCteNode>(result.RootNode);
        recursiveNode.Should().NotBeNull("Recursive CTE should produce a RecursiveCteNode");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_RecursiveCte_HasCorrectCteName()
    {
        var sql = @"
            WITH hierarchy AS (
                SELECT 1 AS level, 'root' AS name
                UNION ALL
                SELECT h.level + 1, 'child' AS name
                FROM hierarchy h
                WHERE h.level < 3
            )
            SELECT * FROM hierarchy";

        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var recursiveNode = FindNode<RecursiveCteNode>(result.RootNode);
        recursiveNode.Should().NotBeNull();
        recursiveNode!.CteName.Should().Be("hierarchy",
            "RecursiveCteNode should carry the CTE name");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_RecursiveCte_HasDefaultMaxRecursion()
    {
        var sql = @"
            WITH hierarchy AS (
                SELECT 1 AS level, 'root' AS name
                UNION ALL
                SELECT h.level + 1, 'child' AS name
                FROM hierarchy h
                WHERE h.level < 3
            )
            SELECT * FROM hierarchy";

        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var recursiveNode = FindNode<RecursiveCteNode>(result.RootNode);
        recursiveNode.Should().NotBeNull();
        recursiveNode!.MaxRecursion.Should().Be(100,
            "Default max recursion should be 100 (matching SQL Server)");
    }

    // ────────────────────────────────────────────
    //  Non-recursive CTE does NOT produce RecursiveCteNode
    // ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_NonRecursiveCte_DoesNotProduceRecursiveCteNode()
    {
        var sql = @"
            WITH recent AS (
                SELECT name FROM account WHERE statecode = 0
            )
            SELECT * FROM recent";

        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var recursiveNode = FindNode<RecursiveCteNode>(result.RootNode);
        recursiveNode.Should().BeNull("Non-recursive CTE should not produce RecursiveCteNode");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_NonRecursiveCte_WithUnionAll_DoesNotProduceRecursiveCteNode()
    {
        // A CTE with UNION ALL but no self-reference is NOT recursive
        var sql = @"
            WITH combined AS (
                SELECT name FROM account
                UNION ALL
                SELECT fullname AS name FROM contact
            )
            SELECT * FROM combined";

        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var recursiveNode = FindNode<RecursiveCteNode>(result.RootNode);
        recursiveNode.Should().BeNull(
            "CTE with UNION ALL but no self-reference should not produce RecursiveCteNode");
    }

    // ────────────────────────────────────────────
    //  Recursive CTE with self-reference in first branch
    // ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Plan_RecursiveCte_SelfReferenceInFirstBranch_ProducesRecursiveCteNode()
    {
        // Some recursive CTEs might have the recursive member first (unusual but valid SQL)
        var sql = @"
            WITH countdown AS (
                SELECT c.val - 1 AS val FROM countdown c WHERE c.val > 0
                UNION ALL
                SELECT 10 AS val
            )
            SELECT * FROM countdown";

        var fragment = _parser.Parse(sql);
        var result = _builder.Plan(fragment);

        var recursiveNode = FindNode<RecursiveCteNode>(result.RootNode);
        recursiveNode.Should().NotBeNull(
            "Recursive CTE with self-reference in first branch should still produce RecursiveCteNode");
    }

    // ────────────────────────────────────────────
    //  Helper: find node type in plan tree
    // ────────────────────────────────────────────

    private static T? FindNode<T>(IQueryPlanNode node) where T : class, IQueryPlanNode
    {
        if (node is T match) return match;
        foreach (var child in node.Children)
        {
            var found = FindNode<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}
