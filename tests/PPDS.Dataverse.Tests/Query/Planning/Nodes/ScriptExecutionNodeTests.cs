using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "PlanUnit")]
public class ScriptExecutionNodeTests
{
    /// <summary>
    /// Creates a context with a mock executor that returns a single-row result
    /// for any FetchXML query. This allows SELECT statements to execute in the
    /// script without a live Dataverse connection.
    /// </summary>
    private static QueryPlanContext CreateContext(VariableScope? scope = null)
    {
        var mockExecutor = new Mock<IQueryExecutor>();

        // Return a single empty row for any FetchXML query so SELECT statements work
        var singleRowResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>(),
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = QueryValue.Simple("Test Account")
                }
            },
            Count = 1,
            MoreRecords = false
        };

        mockExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(singleRowResult);

        var evaluator = new ExpressionEvaluator();
        if (scope != null)
        {
            evaluator.VariableScope = scope;
        }
        return new QueryPlanContext(
            mockExecutor.Object,
            evaluator,
            variableScope: scope);
    }

    [Fact]
    public async Task IfWithTrueCondition_ExecutesThenBlock()
    {
        // DECLARE @x INT = 1; IF @x = 1 BEGIN SELECT name FROM account END
        var scope = new VariableScope();
        var statements = new ISqlStatement[]
        {
            new SqlDeclareStatement("@x", "INT",
                new SqlLiteralExpression(SqlLiteral.Number("1")), 0),
            new SqlIfStatement(
                new SqlExpressionCondition(
                    new SqlVariableExpression("@x"),
                    SqlComparisonOperator.Equal,
                    new SqlLiteralExpression(SqlLiteral.Number("1"))),
                new SqlBlockStatement(new ISqlStatement[]
                {
                    SqlParser.Parse("SELECT name FROM account")
                }, 0),
                null,
                0)
        };

        var node = new ScriptExecutionNode(statements);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // The THEN block SELECT should produce at least one row
        Assert.Single(rows);
    }

    [Fact]
    public async Task IfWithFalseCondition_ExecutesElseBlock()
    {
        // DECLARE @x INT = 0; IF @x = 1 BEGIN SELECT ... END ELSE BEGIN SELECT ... END
        var scope = new VariableScope();
        var statements = new ISqlStatement[]
        {
            new SqlDeclareStatement("@x", "INT",
                new SqlLiteralExpression(SqlLiteral.Number("0")), 0),
            new SqlIfStatement(
                new SqlExpressionCondition(
                    new SqlVariableExpression("@x"),
                    SqlComparisonOperator.Equal,
                    new SqlLiteralExpression(SqlLiteral.Number("1"))),
                new SqlBlockStatement(new ISqlStatement[]
                {
                    SqlParser.Parse("SELECT name FROM account")
                }, 0),
                new SqlBlockStatement(new ISqlStatement[]
                {
                    SqlParser.Parse("SELECT name FROM account")
                }, 0),
                0)
        };

        var node = new ScriptExecutionNode(statements);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // The ELSE block SELECT should produce at least one row
        Assert.Single(rows);
    }

    [Fact]
    public async Task IfWithFalseCondition_NoElse_YieldsNoRows()
    {
        // DECLARE @x INT = 0; IF @x = 1 BEGIN SELECT ... END (no ELSE)
        var scope = new VariableScope();
        var statements = new ISqlStatement[]
        {
            new SqlDeclareStatement("@x", "INT",
                new SqlLiteralExpression(SqlLiteral.Number("0")), 0),
            new SqlIfStatement(
                new SqlExpressionCondition(
                    new SqlVariableExpression("@x"),
                    SqlComparisonOperator.Equal,
                    new SqlLiteralExpression(SqlLiteral.Number("1"))),
                new SqlBlockStatement(new ISqlStatement[]
                {
                    SqlParser.Parse("SELECT name FROM account")
                }, 0),
                null,
                0)
        };

        var node = new ScriptExecutionNode(statements);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Empty(rows);
    }

    [Fact]
    public async Task BlockWithMultipleStatements_ExecutesAll_ReturnsLastResults()
    {
        // DECLARE @x INT = 10; SET @x = 20; SELECT name FROM account
        var scope = new VariableScope();
        var statements = new ISqlStatement[]
        {
            new SqlDeclareStatement("@x", "INT",
                new SqlLiteralExpression(SqlLiteral.Number("10")), 0),
            new SqlSetVariableStatement("@x",
                new SqlLiteralExpression(SqlLiteral.Number("20")), 0),
            SqlParser.Parse("SELECT name FROM account")
        };

        var node = new ScriptExecutionNode(statements);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // SELECT should return rows, and @x should be 20
        Assert.Single(rows);
        Assert.Equal(20, scope.Get("@x"));
    }

    [Fact]
    public async Task VariableScopePreserved_AcrossStatements()
    {
        // DECLARE @x INT = 10; SET @x = 20;
        var scope = new VariableScope();

        var statements = new ISqlStatement[]
        {
            new SqlDeclareStatement("@x", "INT",
                new SqlLiteralExpression(SqlLiteral.Number("10")), 0),
            new SqlSetVariableStatement("@x",
                new SqlLiteralExpression(SqlLiteral.Number("20")), 0),
        };

        var node = new ScriptExecutionNode(statements);
        var ctx = CreateContext(scope);

        // Execute the script (no SELECT, so no rows)
        await foreach (var _ in node.ExecuteAsync(ctx))
        {
            // no rows expected
        }

        // Verify variable was set
        Assert.Equal(20, scope.Get("@x"));
    }

    [Fact]
    public void Description_IncludesStatementCount()
    {
        var statements = new ISqlStatement[]
        {
            new SqlDeclareStatement("@x", "INT", null, 0),
            new SqlSetVariableStatement("@x",
                new SqlLiteralExpression(SqlLiteral.Number("1")), 0),
        };

        var node = new ScriptExecutionNode(statements);

        Assert.Contains("2 statements", node.Description);
    }

    [Fact]
    public void Constructor_ThrowsOnNullStatements()
    {
        Assert.Throws<ArgumentNullException>(() => new ScriptExecutionNode(null!));
    }
}
