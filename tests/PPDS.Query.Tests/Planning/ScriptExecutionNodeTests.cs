using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;
using ExpressionCompiler = PPDS.Query.Execution.ExpressionCompiler;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "PlanUnit")]
public class ScriptExecutionNodeTests
{
    /// <summary>
    /// Creates an ExecutionPlanBuilder and ExpressionCompiler with a variable scope accessor.
    /// </summary>
    private static (ExecutionPlanBuilder builder, ExpressionCompiler compiler) CreatePlanBuilderAndCompiler(
        VariableScope scope)
    {
        var mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
        mockFetchXmlService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Returns(TranspileResult.Simple(
                "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

        var builder = new ExecutionPlanBuilder(mockFetchXmlService.Object);

        var compiler = new ExpressionCompiler(
            variableScopeAccessor: () => scope);

        return (builder, compiler);
    }

    /// <summary>
    /// Creates a context with a mock executor.
    /// </summary>
    private static QueryPlanContext CreateContext(VariableScope? scope = null)
    {
        var mockExecutor = new Mock<IQueryExecutor>();

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

        return new QueryPlanContext(
            mockExecutor.Object,
            variableScope: scope);
    }

    /// <summary>
    /// Helper to create a ScriptDom DeclareVariableStatement.
    /// </summary>
    private static DeclareVariableStatement MakeDeclare(string varName, string typeName, int? initialValue = null)
    {
        var decl = new DeclareVariableElement();
        decl.VariableName = new Identifier { Value = varName.TrimStart('@') };
        decl.DataType = new SqlDataTypeReference
        {
            SqlDataTypeOption = typeName.ToUpperInvariant() switch
            {
                "INT" => SqlDataTypeOption.Int,
                "NVARCHAR" => SqlDataTypeOption.NVarChar,
                _ => SqlDataTypeOption.VarChar
            }
        };
        if (initialValue.HasValue)
        {
            decl.Value = new IntegerLiteral { Value = initialValue.Value.ToString() };
        }
        var stmt = new DeclareVariableStatement();
        stmt.Declarations.Add(decl);
        return stmt;
    }

    /// <summary>
    /// Helper to create a ScriptDom SetVariableStatement.
    /// </summary>
    private static SetVariableStatement MakeSetVariable(string varName, ScalarExpression expression)
    {
        var stmt = new SetVariableStatement();
        stmt.Variable = new VariableReference { Name = varName };
        stmt.Expression = expression;
        return stmt;
    }

    /// <summary>
    /// Helper to create an IfStatement with optional else.
    /// </summary>
    private static IfStatement MakeIf(
        BooleanExpression predicate,
        TSqlStatement thenStatement,
        TSqlStatement? elseStatement = null)
    {
        var stmt = new IfStatement();
        stmt.Predicate = predicate;
        stmt.ThenStatement = thenStatement;
        stmt.ElseStatement = elseStatement;
        return stmt;
    }

    /// <summary>
    /// Helper to create a BooleanComparisonExpression.
    /// </summary>
    private static BooleanComparisonExpression MakeComparison(
        ScalarExpression left, BooleanComparisonType compType, ScalarExpression right)
    {
        return new BooleanComparisonExpression
        {
            FirstExpression = left,
            ComparisonType = compType,
            SecondExpression = right
        };
    }

    /// <summary>
    /// Helper to wrap statements in a BeginEndBlockStatement.
    /// </summary>
    private static BeginEndBlockStatement MakeBlock(params TSqlStatement[] statements)
    {
        var block = new BeginEndBlockStatement();
        block.StatementList = new StatementList();
        foreach (var s in statements)
        {
            block.StatementList.Statements.Add(s);
        }
        return block;
    }

    [Fact]
    public async Task IfWithTrueCondition_ExecutesThenBlock()
    {
        // DECLARE @x INT = 1; IF @x = 1 BEGIN DECLARE @y INT = 99 END
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@x", "INT", 1),
            MakeIf(
                MakeComparison(
                    new VariableReference { Name = "@x" },
                    BooleanComparisonType.Equals,
                    new IntegerLiteral { Value = "1" }),
                MakeBlock(MakeDeclare("@y", "INT", 99)))
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // The THEN block should have declared @y with value 99
        Assert.Equal(99, scope.Get("@y"));
    }

    [Fact]
    public async Task IfWithFalseCondition_ExecutesElseBlock()
    {
        // DECLARE @x INT = 0; IF @x = 1 BEGIN DECLARE @y INT = 99 END ELSE BEGIN DECLARE @y INT = -1 END
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@x", "INT", 0),
            MakeIf(
                MakeComparison(
                    new VariableReference { Name = "@x" },
                    BooleanComparisonType.Equals,
                    new IntegerLiteral { Value = "1" }),
                MakeBlock(MakeDeclare("@y", "INT", 99)),
                MakeBlock(MakeDeclare("@y", "INT", -1)))
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        // The ELSE block should have declared @y with value -1
        Assert.Equal(-1, scope.Get("@y"));
    }

    [Fact]
    public async Task IfWithFalseCondition_NoElse_YieldsNoRows()
    {
        // DECLARE @x INT = 0; IF @x = 1 BEGIN DECLARE @y INT = 99 END (no ELSE)
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@x", "INT", 0),
            MakeIf(
                MakeComparison(
                    new VariableReference { Name = "@x" },
                    BooleanComparisonType.Equals,
                    new IntegerLiteral { Value = "1" }),
                MakeBlock(MakeDeclare("@y", "INT", 99)))
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Empty(rows);
        Assert.False(scope.IsDeclared("@y"));
    }

    [Fact]
    public async Task BlockWithMultipleStatements_ExecutesAll()
    {
        // DECLARE @x INT = 10; SET @x = 20
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@x", "INT", 10),
            MakeSetVariable("@x", new IntegerLiteral { Value = "20" }),
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        await foreach (var _ in node.ExecuteAsync(ctx))
        {
            // no rows expected from DECLARE/SET
        }

        Assert.Equal(20, scope.Get("@x"));
    }

    [Fact]
    public async Task VariableScopePreserved_AcrossStatements()
    {
        // DECLARE @x INT = 10; SET @x = 20;
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@x", "INT", 10),
            MakeSetVariable("@x", new IntegerLiteral { Value = "20" }),
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
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
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@x", "INT"),
            MakeSetVariable("@x", new IntegerLiteral { Value = "1" }),
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);

        Assert.Contains("2 statements", node.Description);
    }

    [Fact]
    public void Constructor_ThrowsOnNullStatements()
    {
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        Assert.Throws<ArgumentNullException>(
            () => new ScriptExecutionNode(null!, builder, compiler));
    }

    [Fact]
    public void Constructor_ThrowsOnNullPlanBuilder()
    {
        var scope = new VariableScope();
        var (_, compiler) = CreatePlanBuilderAndCompiler(scope);
        var statements = new TSqlStatement[] { MakeDeclare("@x", "INT") };

        Assert.Throws<ArgumentNullException>(
            () => new ScriptExecutionNode(statements, null!, compiler));
    }

    [Fact]
    public void Constructor_ThrowsOnNullCompiler()
    {
        var scope = new VariableScope();
        var (builder, _) = CreatePlanBuilderAndCompiler(scope);
        var statements = new TSqlStatement[] { MakeDeclare("@x", "INT") };

        Assert.Throws<ArgumentNullException>(
            () => new ScriptExecutionNode(statements, builder, null!));
    }

    [Fact]
    public async Task MultipleDeclareInOneStatement_DeclaresAll()
    {
        // DECLARE @a INT = 1, @b INT = 2
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var declA = new DeclareVariableElement();
        declA.VariableName = new Identifier { Value = "a" };
        declA.DataType = new SqlDataTypeReference { SqlDataTypeOption = SqlDataTypeOption.Int };
        declA.Value = new IntegerLiteral { Value = "1" };

        var declB = new DeclareVariableElement();
        declB.VariableName = new Identifier { Value = "b" };
        declB.DataType = new SqlDataTypeReference { SqlDataTypeOption = SqlDataTypeOption.Int };
        declB.Value = new IntegerLiteral { Value = "2" };

        var declareStmt = new DeclareVariableStatement();
        declareStmt.Declarations.Add(declA);
        declareStmt.Declarations.Add(declB);

        var statements = new TSqlStatement[] { declareStmt };
        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        await foreach (var _ in node.ExecuteAsync(ctx)) { }

        Assert.Equal(1, scope.Get("@a"));
        Assert.Equal(2, scope.Get("@b"));
    }
}
