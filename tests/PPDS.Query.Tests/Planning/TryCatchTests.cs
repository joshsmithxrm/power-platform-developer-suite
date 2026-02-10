using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class TryCatchTests
{
    // ────────────────────────────────────────────
    //  AST: SqlTryCatchStatement construction
    // ────────────────────────────────────────────

    [Fact]
    public void SqlTryCatchStatement_Constructor_SetsProperties()
    {
        var tryBlock = new SqlBlockStatement(new List<ISqlStatement>(), 0);
        var catchBlock = new SqlBlockStatement(new List<ISqlStatement>(), 0);

        var stmt = new SqlTryCatchStatement(tryBlock, catchBlock, 42);

        stmt.TryBlock.Should().BeSameAs(tryBlock);
        stmt.CatchBlock.Should().BeSameAs(catchBlock);
        stmt.SourcePosition.Should().Be(42);
    }

    [Fact]
    public void SqlTryCatchStatement_Constructor_NullTryBlock_Throws()
    {
        var catchBlock = new SqlBlockStatement(new List<ISqlStatement>(), 0);
        var act = () => new SqlTryCatchStatement(null!, catchBlock, 0);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SqlTryCatchStatement_Constructor_NullCatchBlock_Throws()
    {
        var tryBlock = new SqlBlockStatement(new List<ISqlStatement>(), 0);
        var act = () => new SqlTryCatchStatement(tryBlock, null!, 0);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────
    //  ScriptExecutionNode: TRY/CATCH without error
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TryCatch_NoError_ExecutesTryBlock()
    {
        // DECLARE @x INT = 10
        // BEGIN TRY
        //   SET @x = 20
        // END TRY
        // BEGIN CATCH
        //   SET @x = -1
        // END CATCH
        // SELECT @x AS result  <-- should be 20

        var declare = new SqlDeclareStatement("@x", "INT",
            new SqlLiteralExpression(SqlLiteral.Number("10")), 0);

        var setInTry = new SqlSetVariableStatement("@x",
            new SqlLiteralExpression(SqlLiteral.Number("20")), 0);
        var tryBlock = new SqlBlockStatement(new List<ISqlStatement> { setInTry }, 0);

        var setInCatch = new SqlSetVariableStatement("@x",
            new SqlLiteralExpression(SqlLiteral.Number("-1")), 0);
        var catchBlock = new SqlBlockStatement(new List<ISqlStatement> { setInCatch }, 0);

        var tryCatch = new SqlTryCatchStatement(tryBlock, catchBlock, 0);

        var statements = new List<ISqlStatement> { declare, tryCatch };
        var node = new ScriptExecutionNode(statements);

        var evaluator = new ExpressionEvaluator();
        var scope = new VariableScope();

        var mockExecutor = new Mock<IQueryExecutor>();
        var context = new QueryPlanContext(mockExecutor.Object, evaluator, variableScope: scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        // After TRY block (no error), @x should be 20
        scope.Get("@x").Should().Be(20);
    }

    // ────────────────────────────────────────────
    //  ScriptExecutionNode: TRY/CATCH with error
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TryCatch_WithError_ExecutesCatchBlock()
    {
        // DECLARE @x INT = 10
        // BEGIN TRY
        //   SET @x = 1 / 0   <-- this will throw
        // END TRY
        // BEGIN CATCH
        //   SET @x = -99
        // END CATCH

        var declare = new SqlDeclareStatement("@x", "INT",
            new SqlLiteralExpression(SqlLiteral.Number("10")), 0);

        // 1 / 0 will throw DivideByZeroException
        var divByZero = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("1")),
            SqlBinaryOperator.Divide,
            new SqlLiteralExpression(SqlLiteral.Number("0")));
        var setInTry = new SqlSetVariableStatement("@x", divByZero, 0);
        var tryBlock = new SqlBlockStatement(new List<ISqlStatement> { setInTry }, 0);

        var setInCatch = new SqlSetVariableStatement("@x",
            new SqlLiteralExpression(SqlLiteral.Number("-99")), 0);
        var catchBlock = new SqlBlockStatement(new List<ISqlStatement> { setInCatch }, 0);

        var tryCatch = new SqlTryCatchStatement(tryBlock, catchBlock, 0);

        var statements = new List<ISqlStatement> { declare, tryCatch };
        var node = new ScriptExecutionNode(statements);

        var evaluator = new ExpressionEvaluator();
        var scope = new VariableScope();

        var mockExecutor = new Mock<IQueryExecutor>();
        var context = new QueryPlanContext(mockExecutor.Object, evaluator, variableScope: scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        // After CATCH block (error occurred), @x should be -99
        scope.Get("@x").Should().Be(-99);
    }

    // ────────────────────────────────────────────
    //  Error functions in CATCH block
    // ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TryCatch_ErrorMessageIsAvailable()
    {
        // BEGIN TRY
        //   SET @x = 1 / 0
        // END TRY
        // BEGIN CATCH
        //   SET @msg = ERROR_MESSAGE()
        // END CATCH

        var declareX = new SqlDeclareStatement("@x", "INT", null, 0);
        var declareMsg = new SqlDeclareStatement("@msg", "NVARCHAR", null, 0);

        var divByZero = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("1")),
            SqlBinaryOperator.Divide,
            new SqlLiteralExpression(SqlLiteral.Number("0")));
        var setInTry = new SqlSetVariableStatement("@x", divByZero, 0);
        var tryBlock = new SqlBlockStatement(new List<ISqlStatement> { setInTry }, 0);

        // In the CATCH, call ERROR_MESSAGE() through a function expression
        var errorMsgFunc = new SqlFunctionExpression("ERROR_MESSAGE", new List<ISqlExpression>());
        var setInCatch = new SqlSetVariableStatement("@msg", errorMsgFunc, 0);
        var catchBlock = new SqlBlockStatement(new List<ISqlStatement> { setInCatch }, 0);

        var tryCatch = new SqlTryCatchStatement(tryBlock, catchBlock, 0);

        var statements = new List<ISqlStatement> { declareX, declareMsg, tryCatch };
        var node = new ScriptExecutionNode(statements);

        var evaluator = new ExpressionEvaluator();
        var scope = new VariableScope();

        var mockExecutor = new Mock<IQueryExecutor>();
        var context = new QueryPlanContext(mockExecutor.Object, evaluator, variableScope: scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        // ERROR_MESSAGE() should have captured the division by zero message
        var msg = scope.Get("@msg");
        msg.Should().NotBeNull();
        msg.Should().BeOfType<string>();
        ((string)msg!).Should().NotBeEmpty();
    }

    // ────────────────────────────────────────────
    //  ExecutionPlanBuilder: TRY/CATCH plan detection
    // ────────────────────────────────────────────

    [Fact]
    public void Plan_TryCatch_ProducesScriptExecutionNode()
    {
        var parser = new QueryParser();
        var mockFetchXmlService = new Mock<IFetchXmlGeneratorService>();
        mockFetchXmlService
            .Setup(s => s.Generate(It.IsAny<TSqlFragment>()))
            .Returns(TranspileResult.Simple(
                "<fetch><entity name=\"account\"><all-attributes /></entity></fetch>"));

        var builder = new ExecutionPlanBuilder(mockFetchXmlService.Object);

        var sql = @"
            BEGIN TRY
                DECLARE @x INT = 1
            END TRY
            BEGIN CATCH
                DECLARE @y INT = 2
            END CATCH";

        var fragment = parser.Parse(sql);
        var result = builder.Plan(fragment);

        result.RootNode.Should().BeOfType<ScriptExecutionNode>();
    }
}
