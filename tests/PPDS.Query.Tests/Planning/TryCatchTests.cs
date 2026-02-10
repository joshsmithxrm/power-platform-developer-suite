using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
using PPDS.Query.Planning.Nodes;
using Xunit;
using ExpressionCompiler = PPDS.Query.Execution.ExpressionCompiler;

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

        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var declare = MakeDeclare("@x", "INT", 10);

        var setInTry = MakeSetVariable("@x", new IntegerLiteral { Value = "20" });
        var tryStatements = new StatementList();
        tryStatements.Statements.Add(setInTry);

        var setInCatch = MakeSetVariable("@x", new IntegerLiteral { Value = "-1" });
        var catchStatements = new StatementList();
        catchStatements.Statements.Add(setInCatch);

        var tryCatch = new TryCatchStatement
        {
            TryStatements = tryStatements,
            CatchStatements = catchStatements
        };

        var statements = new TSqlStatement[] { declare, tryCatch };
        var node = new ScriptExecutionNode(statements, builder, compiler);

        var mockExecutor = new Mock<IQueryExecutor>();
        var evaluator = new ExpressionEvaluator();
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

        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var declare = MakeDeclare("@x", "INT", 10);

        // 1 / 0 will throw DivideByZeroException
        var divByZero = new BinaryExpression
        {
            FirstExpression = new IntegerLiteral { Value = "1" },
            BinaryExpressionType = BinaryExpressionType.Divide,
            SecondExpression = new IntegerLiteral { Value = "0" }
        };
        var setInTry = MakeSetVariable("@x", divByZero);
        var tryStatements = new StatementList();
        tryStatements.Statements.Add(setInTry);

        var setInCatch = MakeSetVariable("@x", new IntegerLiteral { Value = "-99" });
        var catchStatements = new StatementList();
        catchStatements.Statements.Add(setInCatch);

        var tryCatch = new TryCatchStatement
        {
            TryStatements = tryStatements,
            CatchStatements = catchStatements
        };

        var statements = new TSqlStatement[] { declare, tryCatch };
        var node = new ScriptExecutionNode(statements, builder, compiler);

        var mockExecutor = new Mock<IQueryExecutor>();
        var evaluator = new ExpressionEvaluator();
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

        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var declareX = MakeDeclare("@x", "INT");
        var declareMsg = MakeDeclare("@msg", "NVARCHAR");

        var divByZero = new BinaryExpression
        {
            FirstExpression = new IntegerLiteral { Value = "1" },
            BinaryExpressionType = BinaryExpressionType.Divide,
            SecondExpression = new IntegerLiteral { Value = "0" }
        };
        var setInTry = MakeSetVariable("@x", divByZero);
        var tryStatements = new StatementList();
        tryStatements.Statements.Add(setInTry);

        // ERROR_MESSAGE() call
        var errorMsgFunc = new FunctionCall();
        errorMsgFunc.FunctionName = new Identifier { Value = "ERROR_MESSAGE" };
        var setInCatch = MakeSetVariable("@msg", errorMsgFunc);
        var catchStatements = new StatementList();
        catchStatements.Statements.Add(setInCatch);

        var tryCatch = new TryCatchStatement
        {
            TryStatements = tryStatements,
            CatchStatements = catchStatements
        };

        var statements = new TSqlStatement[] { declareX, declareMsg, tryCatch };
        var node = new ScriptExecutionNode(statements, builder, compiler);

        var mockExecutor = new Mock<IQueryExecutor>();
        var evaluator = new ExpressionEvaluator();
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

    // ────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────

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

    private static SetVariableStatement MakeSetVariable(string varName, ScalarExpression expression)
    {
        var stmt = new SetVariableStatement();
        stmt.Variable = new VariableReference { Name = varName };
        stmt.Expression = expression;
        return stmt;
    }

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
}
