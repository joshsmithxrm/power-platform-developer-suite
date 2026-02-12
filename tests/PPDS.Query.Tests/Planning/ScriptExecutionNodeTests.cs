using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
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

    /// <summary>
    /// Helper to create a WhileStatement with a predicate and body block.
    /// </summary>
    private static WhileStatement MakeWhile(
        BooleanExpression predicate,
        params TSqlStatement[] bodyStatements)
    {
        var stmt = new WhileStatement();
        stmt.Predicate = predicate;
        stmt.Statement = MakeBlock(bodyStatements);
        return stmt;
    }

    /// <summary>
    /// Helper to create a SET @var = @var + value (additive assignment via binary expression).
    /// </summary>
    private static SetVariableStatement MakeSetAddition(
        string varName, string addendVarOrLiteral)
    {
        return MakeSetVariable(varName,
            new BinaryExpression
            {
                BinaryExpressionType = BinaryExpressionType.Add,
                FirstExpression = new VariableReference { Name = varName },
                SecondExpression = new IntegerLiteral { Value = addendVarOrLiteral }
            });
    }

    [Fact]
    public async Task WhileLoop_Break_ExitsLoop()
    {
        // DECLARE @i INT = 0;
        // WHILE @i < 10
        // BEGIN
        //   SET @i = @i + 1;
        //   IF @i = 3 BREAK
        // END
        // -- expect @i = 3
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@i", "INT", 0),
            MakeWhile(
                MakeComparison(
                    new VariableReference { Name = "@i" },
                    BooleanComparisonType.LessThan,
                    new IntegerLiteral { Value = "10" }),
                // SET @i = @i + 1
                MakeSetAddition("@i", "1"),
                // IF @i = 3 BREAK
                MakeIf(
                    MakeComparison(
                        new VariableReference { Name = "@i" },
                        BooleanComparisonType.Equals,
                        new IntegerLiteral { Value = "3" }),
                    new BreakStatement()))
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(3L, Convert.ToInt64(scope.Get("@i")));
    }

    [Fact]
    public async Task WhileLoop_Continue_SkipsIteration()
    {
        // DECLARE @i INT = 0;
        // DECLARE @sum INT = 0;
        // WHILE @i < 10
        // BEGIN
        //   SET @i = @i + 1;
        //   IF @i % 2 = 1 CONTINUE   -- skip odd numbers
        //   SET @sum = @sum + @i;     -- only accumulate even: 2+4+6+8+10 = 30
        // END
        // -- expect @sum = 30
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@i", "INT", 0),
            MakeDeclare("@sum", "INT", 0),
            MakeWhile(
                MakeComparison(
                    new VariableReference { Name = "@i" },
                    BooleanComparisonType.LessThan,
                    new IntegerLiteral { Value = "10" }),
                // SET @i = @i + 1
                MakeSetAddition("@i", "1"),
                // IF (@i % 2) = 1 CONTINUE
                MakeIf(
                    MakeComparison(
                        new BinaryExpression
                        {
                            BinaryExpressionType = BinaryExpressionType.Modulo,
                            FirstExpression = new VariableReference { Name = "@i" },
                            SecondExpression = new IntegerLiteral { Value = "2" }
                        },
                        BooleanComparisonType.Equals,
                        new IntegerLiteral { Value = "1" }),
                    new ContinueStatement()),
                // SET @sum = @sum + @i
                MakeSetVariable("@sum",
                    new BinaryExpression
                    {
                        BinaryExpressionType = BinaryExpressionType.Add,
                        FirstExpression = new VariableReference { Name = "@sum" },
                        SecondExpression = new VariableReference { Name = "@i" }
                    }))
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Equal(30L, Convert.ToInt64(scope.Get("@sum")));
    }

    // ────────────────────────────────────────────
    //  ExecuteScriptAsync helper (SQL text-based)
    // ────────────────────────────────────────────

    /// <summary>
    /// Parses a SQL script string, plans and executes it via ScriptExecutionNode,
    /// and returns the result rows. Useful for integration-style tests that verify
    /// end-to-end script behavior from SQL text.
    /// </summary>
    private static async Task<List<QueryRow>> ExecuteScriptAsync(string sql)
    {
        var (rows, _) = await ExecuteScriptWithScopeAsync(sql);
        return rows;
    }

    /// <summary>
    /// Parses a SQL script string, plans and executes it via ScriptExecutionNode,
    /// and returns both the result rows and the final variable scope. Useful for
    /// tests that verify variable state after SELECT @var = expr assignments.
    /// </summary>
    private static async Task<(List<QueryRow> rows, VariableScope scope)> ExecuteScriptWithScopeAsync(string sql)
    {
        var parser = new QueryParser();
        var statements = parser.ParseBatch(sql);

        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);
        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }
        return (rows, scope);
    }

    // ────────────────────────────────────────────
    //  SELECT @var = expr (Variable Assignment)
    // ────────────────────────────────────────────

    [Fact]
    public async Task SelectAssignment_SetsVariableFromExpression()
    {
        var sql = @"
            DECLARE @name NVARCHAR(100)
            SELECT @name = 'Hello World'";

        var (rows, scope) = await ExecuteScriptWithScopeAsync(sql);
        rows.Should().BeEmpty();
        scope.Get("@name").Should().Be("Hello World");
    }

    [Fact]
    public async Task SelectAssignment_MultipleVariables()
    {
        var sql = @"
            DECLARE @a INT
            DECLARE @b INT
            SELECT @a = 10, @b = 20";

        var (rows, scope) = await ExecuteScriptWithScopeAsync(sql);
        rows.Should().BeEmpty();
        scope.Get("@a").Should().Be(10);
        scope.Get("@b").Should().Be(20);
    }

    [Fact]
    public async Task SelectAssignment_SetsVariableFromExpression_AST()
    {
        // DECLARE @name NVARCHAR; SELECT @name = 'Hello World'
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var selectStmt = new SelectStatement();
        var querySpec = new QuerySpecification();
        var setVar = new SelectSetVariable
        {
            Variable = new VariableReference { Name = "@name" },
            Expression = new StringLiteral { Value = "Hello World" }
        };
        querySpec.SelectElements.Add(setVar);
        selectStmt.QueryExpression = querySpec;

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@name", "NVARCHAR"),
            selectStmt
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        await foreach (var _ in node.ExecuteAsync(ctx)) { }

        Assert.Equal("Hello World", scope.Get("@name"));
    }

    [Fact]
    public async Task SelectAssignment_MultipleVariables_AST()
    {
        // DECLARE @a INT; DECLARE @b INT; SELECT @a = 10, @b = 20
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var selectStmt = new SelectStatement();
        var querySpec = new QuerySpecification();
        querySpec.SelectElements.Add(new SelectSetVariable
        {
            Variable = new VariableReference { Name = "@a" },
            Expression = new IntegerLiteral { Value = "10" }
        });
        querySpec.SelectElements.Add(new SelectSetVariable
        {
            Variable = new VariableReference { Name = "@b" },
            Expression = new IntegerLiteral { Value = "20" }
        });
        selectStmt.QueryExpression = querySpec;

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@a", "INT"),
            MakeDeclare("@b", "INT"),
            selectStmt
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        await foreach (var _ in node.ExecuteAsync(ctx)) { }

        Assert.Equal(10, scope.Get("@a"));
        Assert.Equal(20, scope.Get("@b"));
    }

    [Fact]
    public async Task SelectAssignment_DoesNotProduceRows()
    {
        // SELECT @var = expr should NOT produce result rows (it's an assignment, not a query)
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var selectStmt = new SelectStatement();
        var querySpec = new QuerySpecification();
        querySpec.SelectElements.Add(new SelectSetVariable
        {
            Variable = new VariableReference { Name = "@x" },
            Expression = new IntegerLiteral { Value = "42" }
        });
        selectStmt.QueryExpression = querySpec;

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@x", "INT"),
            selectStmt
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        Assert.Empty(rows);
        Assert.Equal(42, scope.Get("@x"));
    }

    [Fact]
    public async Task SelectAssignment_ExpressionUsingOtherVariable()
    {
        // DECLARE @a INT = 5; DECLARE @b INT; SELECT @b = @a * 2
        var sql = @"
            DECLARE @a INT = 5
            DECLARE @b INT
            SELECT @b = @a * 2";

        var (rows, scope) = await ExecuteScriptWithScopeAsync(sql);
        rows.Should().BeEmpty();
        scope.Get("@b").Should().Be(10L);
    }

    // ────────────────────────────────────────────
    //  PRINT
    // ────────────────────────────────────────────

    [Fact]
    public async Task Print_RoutesMessageToProgress()
    {
        var sql = "PRINT 'Hello from SQL'";
        // PRINT should not throw and should produce no rows
        var rows = await ExecuteScriptAsync(sql);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Print_RoutesMessageToProgress_AST()
    {
        // Build PRINT statement using AST directly
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var printStmt = new PrintStatement
        {
            Expression = new StringLiteral { Value = "Hello from AST" }
        };

        var statements = new TSqlStatement[] { printStmt };
        var node = new ScriptExecutionNode(statements, builder, compiler);

        // Use a mock progress reporter to verify the message is routed
        var mockProgress = new Mock<IQueryProgressReporter>();
        var mockExecutor = new Mock<IQueryExecutor>();
        var ctx = new QueryPlanContext(
            mockExecutor.Object,
            progressReporter: mockProgress.Object,
            variableScope: scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        rows.Should().BeEmpty();
        mockProgress.Verify(p => p.ReportPhase("PRINT", "Hello from AST"), Times.Once);
    }

    [Fact]
    public async Task Print_WithVariableExpression_EvaluatesBeforeReporting()
    {
        // PRINT uses the ExpressionCompiler so variable references should work
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var statements = new TSqlStatement[]
        {
            MakeDeclare("@msg", "NVARCHAR"),
            MakeSetVariable("@msg", new StringLiteral { Value = "world" }),
            new PrintStatement
            {
                Expression = new VariableReference { Name = "@msg" }
            }
        };

        var node = new ScriptExecutionNode(statements, builder, compiler);
        var mockProgress = new Mock<IQueryProgressReporter>();
        var mockExecutor = new Mock<IQueryExecutor>();
        var ctx = new QueryPlanContext(
            mockExecutor.Object,
            progressReporter: mockProgress.Object,
            variableScope: scope);

        await foreach (var _ in node.ExecuteAsync(ctx)) { }

        mockProgress.Verify(p => p.ReportPhase("PRINT", "world"), Times.Once);
    }

    // ────────────────────────────────────────────
    //  THROW
    // ────────────────────────────────────────────

    [Fact]
    public async Task Throw_ThrowsWithUserMessage()
    {
        var sql = "THROW 50001, 'Custom error message', 1";
        var act = async () => await ExecuteScriptAsync(sql);
        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*Custom error message*");
    }

    [Fact]
    public async Task Throw_ThrowsWithUserMessage_AST()
    {
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var throwStmt = new ThrowStatement
        {
            ErrorNumber = new IntegerLiteral { Value = "50001" },
            Message = new StringLiteral { Value = "AST error message" },
            State = new IntegerLiteral { Value = "1" }
        };

        var statements = new TSqlStatement[] { throwStmt };
        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        Func<Task> act = async () =>
        {
            await foreach (var _ in node.ExecuteAsync(ctx)) { }
        };

        await act.Should().ThrowAsync<QueryExecutionException>()
            .WithMessage("*AST error message*");
    }

    // ────────────────────────────────────────────
    //  RAISERROR
    // ────────────────────────────────────────────

    [Fact]
    public async Task RaiseError_ThrowsWithFormattedMessage()
    {
        var sql = "RAISERROR('Error: %s', 16, 1, 'test')";
        var act = async () => await ExecuteScriptAsync(sql);
        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*Error: test*");
    }

    [Fact]
    public async Task RaiseError_HighSeverity_ThrowsException_AST()
    {
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var raiseError = new RaiseErrorStatement
        {
            FirstParameter = new StringLiteral { Value = "Formatted: %s and %d" },
            SecondParameter = new IntegerLiteral { Value = "16" },
            ThirdParameter = new IntegerLiteral { Value = "1" }
        };
        raiseError.OptionalParameters.Add(new StringLiteral { Value = "hello" });
        raiseError.OptionalParameters.Add(new IntegerLiteral { Value = "42" });

        var statements = new TSqlStatement[] { raiseError };
        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        Func<Task> act = async () =>
        {
            await foreach (var _ in node.ExecuteAsync(ctx)) { }
        };

        await act.Should().ThrowAsync<QueryExecutionException>()
            .WithMessage("*Formatted: hello and 42*");
    }

    [Fact]
    public async Task RaiseError_LowSeverity_DoesNotThrow_AST()
    {
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        var raiseError = new RaiseErrorStatement
        {
            FirstParameter = new StringLiteral { Value = "Informational message" },
            SecondParameter = new IntegerLiteral { Value = "10" },
            ThirdParameter = new IntegerLiteral { Value = "1" }
        };

        var statements = new TSqlStatement[] { raiseError };
        var node = new ScriptExecutionNode(statements, builder, compiler);

        var mockProgress = new Mock<IQueryProgressReporter>();
        var mockExecutor = new Mock<IQueryExecutor>();
        var ctx = new QueryPlanContext(
            mockExecutor.Object,
            progressReporter: mockProgress.Object,
            variableScope: scope);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(ctx))
        {
            rows.Add(row);
        }

        rows.Should().BeEmpty();
        mockProgress.Verify(p => p.ReportPhase("RAISERROR", "Informational message"), Times.Once);
    }

    // ────────────────────────────────────────────
    //  TRY/CATCH integration with THROW
    // ────────────────────────────────────────────

    [Fact]
    public async Task TryCatch_CatchesThrow()
    {
        var sql = @"
            BEGIN TRY
                THROW 50001, 'Intentional error', 1
            END TRY
            BEGIN CATCH
                SELECT ERROR_MESSAGE() AS msg
            END CATCH";
        var rows = await ExecuteScriptAsync(sql);
        rows.Should().HaveCount(1);
        rows[0].Values["msg"].Value.Should().Be("Intentional error");
    }

    [Fact]
    public async Task TryCatch_CatchesThrow_AST()
    {
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        // Build TRY { THROW 50001, 'caught', 1 } CATCH { ... }
        var throwStmt = new ThrowStatement
        {
            ErrorNumber = new IntegerLiteral { Value = "50001" },
            Message = new StringLiteral { Value = "caught" },
            State = new IntegerLiteral { Value = "1" }
        };

        var tryStmtList = new StatementList();
        tryStmtList.Statements.Add(throwStmt);

        // In the CATCH block, we just check that the error was stored in scope
        var catchStmtList = new StatementList();

        var tryCatch = new TryCatchStatement
        {
            TryStatements = tryStmtList,
            CatchStatements = catchStmtList
        };

        var statements = new TSqlStatement[] { tryCatch };
        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        await foreach (var _ in node.ExecuteAsync(ctx)) { }

        // After TRY/CATCH, the error info should be stored in scope
        scope.IsDeclared("@@ERROR_MESSAGE").Should().BeTrue();
        scope.Get("@@ERROR_MESSAGE").Should().Be("caught");
    }

    [Fact]
    public async Task TryCatch_CatchesRaiseError()
    {
        var sql = @"
            BEGIN TRY
                RAISERROR('Oops: %s', 16, 1, 'broke')
            END TRY
            BEGIN CATCH
                SELECT ERROR_MESSAGE() AS msg
            END CATCH";
        var rows = await ExecuteScriptAsync(sql);
        rows.Should().HaveCount(1);
        rows[0].Values["msg"].Value.Should().Be("Oops: broke");
    }

    [Fact]
    public async Task TryCatch_RaiseError_LowSeverity_NotCaught()
    {
        // RAISERROR with severity < 11 should NOT be caught by TRY/CATCH
        var sql = @"
            BEGIN TRY
                RAISERROR('Info only', 10, 1)
                SELECT 'reached' AS status
            END TRY
            BEGIN CATCH
                SELECT 'caught' AS status
            END CATCH";
        var rows = await ExecuteScriptAsync(sql);
        rows.Should().HaveCount(1);
        rows[0].Values["status"].Value.Should().Be("reached");
    }

    [Fact]
    public async Task Throw_BareThrow_RethrowsInCatch_AST()
    {
        var scope = new VariableScope();
        var (builder, compiler) = CreatePlanBuilderAndCompiler(scope);

        // Inner TRY { THROW 50001, 'original', 1 } CATCH { bare THROW }
        var innerThrow = new ThrowStatement
        {
            ErrorNumber = new IntegerLiteral { Value = "50001" },
            Message = new StringLiteral { Value = "original error" },
            State = new IntegerLiteral { Value = "1" }
        };

        var bareThrow = new ThrowStatement(); // No args = re-throw

        var innerTryList = new StatementList();
        innerTryList.Statements.Add(innerThrow);
        var innerCatchList = new StatementList();
        innerCatchList.Statements.Add(bareThrow);

        var innerTryCatch = new TryCatchStatement
        {
            TryStatements = innerTryList,
            CatchStatements = innerCatchList
        };

        // Outer TRY { innerTryCatch } CATCH { ... }
        var outerTryList = new StatementList();
        outerTryList.Statements.Add(innerTryCatch);
        var outerCatchList = new StatementList();

        var outerTryCatch = new TryCatchStatement
        {
            TryStatements = outerTryList,
            CatchStatements = outerCatchList
        };

        var statements = new TSqlStatement[] { outerTryCatch };
        var node = new ScriptExecutionNode(statements, builder, compiler);
        var ctx = CreateContext(scope);

        await foreach (var _ in node.ExecuteAsync(ctx)) { }

        // The bare THROW re-threw, which was caught by outer CATCH.
        // The outer CATCH stored the error message in scope.
        scope.IsDeclared("@@ERROR_MESSAGE").Should().BeTrue();
        scope.Get("@@ERROR_MESSAGE").Should().Be("original error");
    }
}
