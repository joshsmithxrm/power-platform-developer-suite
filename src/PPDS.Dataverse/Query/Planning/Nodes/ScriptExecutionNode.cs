using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Executes a sequence of SQL statements (multi-statement scripts) including
/// IF/ELSE branching. Evaluates statements sequentially, managing variable
/// scope across blocks. Returns rows from the LAST SELECT/DML statement.
/// </summary>
public sealed class ScriptExecutionNode : IQueryPlanNode
{
    private readonly IReadOnlyList<ISqlStatement> _statements;
    private readonly QueryPlanner _planner;

    /// <inheritdoc />
    public string Description => $"ScriptExecution: {_statements.Count} statements";

    /// <inheritdoc />
    public long EstimatedRows => -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Creates a ScriptExecutionNode for a list of statements.
    /// </summary>
    /// <param name="statements">The ordered list of statements to execute.</param>
    /// <param name="planner">The planner used to plan inner SELECT/DML statements.</param>
    public ScriptExecutionNode(IReadOnlyList<ISqlStatement> statements, QueryPlanner? planner = null)
    {
        _statements = statements ?? throw new ArgumentNullException(nameof(statements));
        _planner = planner ?? new QueryPlanner();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var scope = context.VariableScope ?? new VariableScope();
        var evaluator = context.ExpressionEvaluator;

        // Set the variable scope on the expression evaluator
        evaluator.VariableScope = scope;

        await foreach (var row in ExecuteStatementListAsync(
            _statements, scope, evaluator, context, cancellationToken))
        {
            yield return row;
        }
    }

    /// <summary>
    /// Executes a list of statements sequentially. Yields rows from the
    /// last result-producing statement (SELECT/DML/IF with results).
    /// </summary>
    private async IAsyncEnumerable<QueryRow> ExecuteStatementListAsync(
        IReadOnlyList<ISqlStatement> statements,
        VariableScope scope,
        IExpressionEvaluator evaluator,
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<QueryRow>? lastResultRows = null;

        foreach (var statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (statement)
            {
                case SqlDeclareStatement declare:
                    ExecuteDeclare(declare, scope, evaluator);
                    break;

                case SqlSetVariableStatement setVar:
                    ExecuteSetVariable(setVar, scope, evaluator);
                    break;

                case SqlIfStatement ifStmt:
                    var ifRows = await ExecuteIfAsync(
                        ifStmt, scope, evaluator, context, cancellationToken);
                    if (ifRows != null)
                    {
                        lastResultRows = ifRows;
                    }
                    break;

                case SqlWhileStatement whileStmt:
                    var whileRows = await ExecuteWhileAsync(
                        whileStmt, scope, evaluator, context, cancellationToken);
                    if (whileRows != null)
                    {
                        lastResultRows = whileRows;
                    }
                    break;

                case SqlTryCatchStatement tryCatch:
                    var tryCatchRows = await ExecuteTryCatchAsync(
                        tryCatch, scope, evaluator, context, cancellationToken);
                    if (tryCatchRows != null)
                    {
                        lastResultRows = tryCatchRows;
                    }
                    break;

                case SqlBlockStatement block:
                    var blockRows = await CollectRowsAsync(
                        ExecuteStatementListAsync(
                            block.Statements, scope, evaluator, context, cancellationToken),
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

    private static void ExecuteDeclare(
        SqlDeclareStatement declare,
        VariableScope scope,
        IExpressionEvaluator evaluator)
    {
        object? initialValue = null;
        if (declare.InitialValue != null)
        {
            initialValue = evaluator.Evaluate(
                declare.InitialValue,
                new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase));
        }
        scope.Declare(declare.VariableName, declare.TypeName, initialValue);
    }

    private static void ExecuteSetVariable(
        SqlSetVariableStatement setVar,
        VariableScope scope,
        IExpressionEvaluator evaluator)
    {
        var value = evaluator.Evaluate(
            setVar.Value,
            new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase));
        scope.Set(setVar.VariableName, value);
    }

    private async Task<List<QueryRow>?> ExecuteIfAsync(
        SqlIfStatement ifStmt,
        VariableScope scope,
        IExpressionEvaluator evaluator,
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var conditionResult = evaluator.EvaluateCondition(
            ifStmt.Condition,
            new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase));

        if (conditionResult)
        {
            return await CollectRowsAsync(
                ExecuteStatementListAsync(
                    ifStmt.ThenBlock.Statements, scope, evaluator, context, cancellationToken),
                cancellationToken);
        }

        if (ifStmt.ElseBlock != null)
        {
            return await CollectRowsAsync(
                ExecuteStatementListAsync(
                    ifStmt.ElseBlock.Statements, scope, evaluator, context, cancellationToken),
                cancellationToken);
        }

        return null;
    }

    private async Task<List<QueryRow>?> ExecuteWhileAsync(
        SqlWhileStatement whileStmt,
        VariableScope scope,
        IExpressionEvaluator evaluator,
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        const int maxIterations = 10000;
        List<QueryRow>? lastRows = null;

        for (var i = 0; i < maxIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var conditionResult = evaluator.EvaluateCondition(
                whileStmt.Condition,
                new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase));

            if (!conditionResult)
                break;

            var iterRows = await CollectRowsAsync(
                ExecuteStatementListAsync(
                    whileStmt.Body.Statements, scope, evaluator, context, cancellationToken),
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
        SqlTryCatchStatement tryCatch,
        VariableScope scope,
        IExpressionEvaluator evaluator,
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CollectRowsAsync(
                ExecuteStatementListAsync(
                    tryCatch.TryBlock.Statements, scope, evaluator, context, cancellationToken),
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

            return await CollectRowsAsync(
                ExecuteStatementListAsync(
                    tryCatch.CatchBlock.Statements, scope, evaluator, context, cancellationToken),
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
        ISqlStatement statement,
        VariableScope scope,
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var options = new QueryPlanOptions
        {
            VariableScope = scope
        };

        var planResult = _planner.Plan(statement, options);

        var rows = new List<QueryRow>();
        await foreach (var row in planResult.RootNode.ExecuteAsync(context, cancellationToken))
        {
            rows.Add(row);
        }
        return rows;
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
}
