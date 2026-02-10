using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Query.Execution;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Executes a sequence of SQL statements (multi-statement scripts) including
/// DECLARE, SET, IF/ELSE branching, WHILE loops, and TRY/CATCH. Evaluates
/// statements sequentially, managing variable scope across blocks.
/// Returns rows from the LAST SELECT/DML statement.
///
/// This node works directly with ScriptDom <see cref="TSqlStatement"/> types,
/// using the <see cref="ExecutionPlanBuilder"/> for inner statement planning and
/// <see cref="ExpressionCompiler"/> for expression/predicate compilation.
/// </summary>
public sealed class ScriptExecutionNode : IQueryPlanNode
{
    private readonly IReadOnlyList<TSqlStatement> _statements;
    private readonly ExecutionPlanBuilder _planBuilder;
    private readonly ExpressionCompiler _expressionCompiler;

    /// <inheritdoc />
    public string Description => $"ScriptExecution: {_statements.Count} statements";

    /// <inheritdoc />
    public long EstimatedRows => -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Creates a ScriptExecutionNode for a list of ScriptDom statements.
    /// </summary>
    /// <param name="statements">The ordered list of ScriptDom statements to execute.</param>
    /// <param name="planBuilder">Plan builder used to plan inner SELECT/DML statements.</param>
    /// <param name="expressionCompiler">Compiler for scalar expressions and predicates.</param>
    public ScriptExecutionNode(
        IReadOnlyList<TSqlStatement> statements,
        ExecutionPlanBuilder planBuilder,
        ExpressionCompiler expressionCompiler)
    {
        _statements = statements ?? throw new ArgumentNullException(nameof(statements));
        _planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        _expressionCompiler = expressionCompiler ?? throw new ArgumentNullException(nameof(expressionCompiler));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scope = context.VariableScope ?? new VariableScope();

        await foreach (var row in ExecuteStatementListAsync(
            _statements, scope, context, cancellationToken))
        {
            yield return row;
        }
    }

    /// <summary>
    /// Executes a list of statements sequentially. Yields rows from the
    /// last result-producing statement (SELECT/DML/IF with results).
    /// </summary>
    private async IAsyncEnumerable<QueryRow> ExecuteStatementListAsync(
        IReadOnlyList<TSqlStatement> statements,
        VariableScope scope,
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<QueryRow>? lastResultRows = null;

        foreach (var statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (statement)
            {
                case DeclareVariableStatement declare:
                    ExecuteDeclare(declare, scope);
                    break;

                case SetVariableStatement setVar:
                    ExecuteSetVariable(setVar, scope);
                    break;

                case IfStatement ifStmt:
                    var ifRows = await ExecuteIfAsync(
                        ifStmt, scope, context, cancellationToken);
                    if (ifRows != null)
                    {
                        lastResultRows = ifRows;
                    }
                    break;

                case WhileStatement whileStmt:
                    var whileRows = await ExecuteWhileAsync(
                        whileStmt, scope, context, cancellationToken);
                    if (whileRows != null)
                    {
                        lastResultRows = whileRows;
                    }
                    break;

                case TryCatchStatement tryCatch:
                    var tryCatchRows = await ExecuteTryCatchAsync(
                        tryCatch, scope, context, cancellationToken);
                    if (tryCatchRows != null)
                    {
                        lastResultRows = tryCatchRows;
                    }
                    break;

                case BeginEndBlockStatement block:
                    var blockStatements = block.StatementList.Statements
                        .Cast<TSqlStatement>().ToList();
                    var blockRows = await CollectRowsAsync(
                        ExecuteStatementListAsync(
                            blockStatements, scope, context, cancellationToken),
                        cancellationToken);
                    if (blockRows.Count > 0 || lastResultRows == null)
                    {
                        lastResultRows = blockRows;
                    }
                    break;

                default:
                    // SELECT, INSERT, UPDATE, DELETE -- plan and execute
                    lastResultRows = await ExecuteDataStatementAsync(
                        statement, scope, context, cancellationToken);
                    break;
            }
        }

        // Yield rows from the last result-producing statement
        if (lastResultRows != null)
        {
            foreach (var row in lastResultRows)
            {
                yield return row;
            }
        }
    }

    private void ExecuteDeclare(
        DeclareVariableStatement declare,
        VariableScope scope)
    {
        foreach (var decl in declare.Declarations)
        {
            var varName = decl.VariableName.Value;
            if (!varName.StartsWith("@"))
                varName = "@" + varName;

            var typeName = FormatDataTypeReference(decl.DataType);

            object? initialValue = null;
            if (decl.Value != null)
            {
                var compiledExpr = _expressionCompiler.CompileScalar(decl.Value);
                initialValue = compiledExpr(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase));
            }

            scope.Declare(varName, typeName, initialValue);
        }
    }

    private void ExecuteSetVariable(
        SetVariableStatement setVar,
        VariableScope scope)
    {
        var varName = setVar.Variable.Name;
        if (!varName.StartsWith("@"))
            varName = "@" + varName;

        var compiledExpr = _expressionCompiler.CompileScalar(setVar.Expression);
        var value = compiledExpr(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase));
        scope.Set(varName, value);
    }

    private async Task<List<QueryRow>?> ExecuteIfAsync(
        IfStatement ifStmt,
        VariableScope scope,
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var compiledPredicate = _expressionCompiler.CompilePredicate(ifStmt.Predicate);
        var conditionResult = compiledPredicate(
            new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase));

        if (conditionResult)
        {
            var thenStatements = UnwrapStatement(ifStmt.ThenStatement);
            return await CollectRowsAsync(
                ExecuteStatementListAsync(
                    thenStatements, scope, context, cancellationToken),
                cancellationToken);
        }

        if (ifStmt.ElseStatement != null)
        {
            var elseStatements = UnwrapStatement(ifStmt.ElseStatement);
            return await CollectRowsAsync(
                ExecuteStatementListAsync(
                    elseStatements, scope, context, cancellationToken),
                cancellationToken);
        }

        return null;
    }

    private async Task<List<QueryRow>?> ExecuteWhileAsync(
        WhileStatement whileStmt,
        VariableScope scope,
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        const int maxIterations = 10000;
        List<QueryRow>? lastRows = null;

        for (var i = 0; i < maxIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var compiledPredicate = _expressionCompiler.CompilePredicate(whileStmt.Predicate);
            var conditionResult = compiledPredicate(
                new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase));

            if (!conditionResult)
                break;

            var bodyStatements = UnwrapStatement(whileStmt.Statement);
            var iterRows = await CollectRowsAsync(
                ExecuteStatementListAsync(
                    bodyStatements, scope, context, cancellationToken),
                cancellationToken);

            if (iterRows.Count > 0)
            {
                lastRows ??= new List<QueryRow>();
                lastRows.AddRange(iterRows);
            }

            if (i == maxIterations - 1)
            {
                throw new InvalidOperationException(
                    $"WHILE loop exceeded maximum iteration count of {maxIterations}.");
            }
        }

        return lastRows;
    }

    private async Task<List<QueryRow>?> ExecuteTryCatchAsync(
        TryCatchStatement tryCatch,
        VariableScope scope,
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var tryStatements = tryCatch.TryStatements?.Statements
                ?.Cast<TSqlStatement>().ToList()
                ?? new List<TSqlStatement>();
            return await CollectRowsAsync(
                ExecuteStatementListAsync(
                    tryStatements, scope, context, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // Don't catch cancellation
        }
        catch (Exception ex)
        {
            // Store error information in the variable scope so ERROR_MESSAGE() etc. can access it
            StoreErrorInfo(scope, ex);

            var catchStatements = tryCatch.CatchStatements?.Statements
                ?.Cast<TSqlStatement>().ToList()
                ?? new List<TSqlStatement>();
            return await CollectRowsAsync(
                ExecuteStatementListAsync(
                    catchStatements, scope, context, cancellationToken),
                cancellationToken);
        }
    }

    /// <summary>
    /// Stores exception information in the variable scope for access via ERROR_MESSAGE(),
    /// ERROR_NUMBER(), ERROR_SEVERITY(), ERROR_STATE(), ERROR_PROCEDURE(), ERROR_LINE().
    /// Uses @@ERROR_* convention for internal error tracking.
    /// </summary>
    private static void StoreErrorInfo(VariableScope scope, Exception ex)
    {
        // Use internal variable names that ERROR_MESSAGE() etc. can read
        const string errorMessageVar = "@@ERROR_MESSAGE";
        const string errorNumberVar = "@@ERROR_NUMBER";
        const string errorSeverityVar = "@@ERROR_SEVERITY";
        const string errorStateVar = "@@ERROR_STATE";

        // Declare if not already declared, then set
        if (!scope.IsDeclared(errorMessageVar))
            scope.Declare(errorMessageVar, "NVARCHAR");
        scope.Set(errorMessageVar, ex.Message);

        if (!scope.IsDeclared(errorNumberVar))
            scope.Declare(errorNumberVar, "INT");
        scope.Set(errorNumberVar, ex.HResult != 0 ? ex.HResult : 50000);

        if (!scope.IsDeclared(errorSeverityVar))
            scope.Declare(errorSeverityVar, "INT");
        scope.Set(errorSeverityVar, 16);

        if (!scope.IsDeclared(errorStateVar))
            scope.Declare(errorStateVar, "INT");
        scope.Set(errorStateVar, 1);
    }

    private async Task<List<QueryRow>> ExecuteDataStatementAsync(
        TSqlStatement statement,
        VariableScope scope,
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var options = new QueryPlanOptions
        {
            VariableScope = scope
        };

        var planResult = _planBuilder.PlanStatement(statement, options);

        var rows = new List<QueryRow>();
        await foreach (var row in planResult.RootNode.ExecuteAsync(context, cancellationToken))
        {
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Unwraps a single TSqlStatement into a list of statements.
    /// If the statement is a BeginEndBlockStatement, returns its inner statements.
    /// Otherwise, returns a single-element list.
    /// </summary>
    private static IReadOnlyList<TSqlStatement> UnwrapStatement(TSqlStatement statement)
    {
        if (statement is BeginEndBlockStatement block)
        {
            return block.StatementList.Statements.Cast<TSqlStatement>().ToList();
        }

        return new[] { statement };
    }

    private static async Task<List<QueryRow>> CollectRowsAsync(
        IAsyncEnumerable<QueryRow> source,
        CancellationToken cancellationToken)
    {
        var rows = new List<QueryRow>();
        await foreach (var row in source.WithCancellation(cancellationToken))
        {
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Formats a ScriptDom DataTypeReference to a string for VariableScope.
    /// </summary>
    private static string FormatDataTypeReference(DataTypeReference dataType)
    {
        if (dataType is SqlDataTypeReference sqlType)
        {
            var name = sqlType.SqlDataTypeOption.ToString().ToUpperInvariant();
            if (sqlType.Parameters.Count > 0)
            {
                var parms = string.Join(", ", sqlType.Parameters.Select(p => p.Value));
                return $"{name}({parms})";
            }
            return name;
        }
        return "NVARCHAR";
    }
}
