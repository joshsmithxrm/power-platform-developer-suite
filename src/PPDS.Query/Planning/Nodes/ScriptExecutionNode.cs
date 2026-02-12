using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
    /// <summary>
    /// Internal flow-control exception thrown by BREAK statements to exit WHILE loops.
    /// </summary>
    private sealed class BreakException : Exception { }

    /// <summary>
    /// Internal flow-control exception thrown by CONTINUE statements to skip to the next WHILE iteration.
    /// </summary>
    private sealed class ContinueException : Exception { }

    private readonly IReadOnlyList<TSqlStatement> _statements;
    private readonly ExecutionPlanBuilder _planBuilder;
    private readonly ExpressionCompiler _expressionCompiler;
    private readonly SessionContext? _session;

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
    /// <param name="session">Optional session context for @@ERROR tracking.</param>
    public ScriptExecutionNode(
        IReadOnlyList<TSqlStatement> statements,
        ExecutionPlanBuilder planBuilder,
        ExpressionCompiler expressionCompiler,
        SessionContext? session = null)
    {
        _statements = statements ?? throw new ArgumentNullException(nameof(statements));
        _planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        _expressionCompiler = expressionCompiler ?? throw new ArgumentNullException(nameof(expressionCompiler));
        _session = session;
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

                case SelectStatement selectStmt when HasIntoTempTable(selectStmt):
                    await ExecuteSelectIntoAsync(
                        selectStmt, scope, context, cancellationToken);
                    break;

                case SelectStatement selectStmt when IsVariableAssignment(selectStmt):
                    await ExecuteSelectAssignmentAsync(
                        selectStmt, scope, context, cancellationToken);
                    break;

                case SelectStatement selectStmt when IsFromlessSelect(selectStmt):
                    lastResultRows = ExecuteFromlessSelect(selectStmt, scope);
                    break;

                case SelectStatement selectStmt when IsTempTableSelect(selectStmt):
                    lastResultRows = ExecuteTempTableSelect(selectStmt);
                    break;

                case PrintStatement printStmt:
                    ExecutePrint(printStmt, scope, context);
                    break;

                case ThrowStatement throwStmt:
                    ExecuteThrow(throwStmt, scope);
                    break;

                case RaiseErrorStatement raiseError:
                    ExecuteRaiseError(raiseError, scope, context);
                    break;

                case BreakStatement:
                    throw new BreakException();

                case ContinueStatement:
                    throw new ContinueException();

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

            try
            {
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
            }
            catch (BreakException)
            {
                break;
            }
            catch (ContinueException)
            {
                // Skip to next iteration — the for loop will re-evaluate the condition
                continue;
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
            var result = await CollectRowsAsync(
                ExecuteStatementListAsync(
                    tryStatements, scope, context, cancellationToken),
                cancellationToken);

            // TRY block completed successfully — reset @@ERROR state
            if (_session != null)
            {
                _session.ErrorNumber = 0;
                _session.ErrorMessage = string.Empty;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw; // Don't catch cancellation
        }
        catch (BreakException)
        {
            throw; // Don't catch BREAK — let it propagate to the enclosing WHILE loop
        }
        catch (ContinueException)
        {
            throw; // Don't catch CONTINUE — let it propagate to the enclosing WHILE loop
        }
        catch (Exception ex)
        {
            // Store error information in the variable scope so ERROR_MESSAGE() etc. can access it
            StoreErrorInfo(scope, ex);

            // Track @@ERROR and ERROR_MESSAGE() on the session
            if (_session != null)
            {
                _session.ErrorNumber = 50000; // Generic user-defined error number
                _session.ErrorMessage = ex.Message;
            }

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

    /// <summary>
    /// Returns true if the SELECT statement is a variable assignment form
    /// (e.g., SELECT @var = expr) rather than a normal result-producing SELECT.
    /// </summary>
    private static bool IsVariableAssignment(SelectStatement selectStmt)
    {
        if (selectStmt.QueryExpression is not QuerySpecification querySpec)
            return false;
        return querySpec.SelectElements.Any(e => e is SelectSetVariable);
    }

    /// <summary>
    /// Returns true if the SELECT statement has no FROM clause (e.g., SELECT 1+1 AS result,
    /// SELECT ERROR_MESSAGE() AS msg). These can be evaluated directly without hitting the
    /// plan builder, which requires a FROM clause for entity resolution.
    /// </summary>
    private static bool IsFromlessSelect(SelectStatement selectStmt)
    {
        if (selectStmt.QueryExpression is not QuerySpecification querySpec)
            return false;
        return querySpec.FromClause == null
               || querySpec.FromClause.TableReferences.Count == 0;
    }

    /// <summary>
    /// Returns true if the SELECT statement has an INTO clause targeting a temp table
    /// (e.g., SELECT ... INTO #temp FROM ...). These are handled by executing the SELECT,
    /// collecting results, and creating a temp table in the session context.
    /// </summary>
    private static bool HasIntoTempTable(SelectStatement selectStmt)
    {
        return selectStmt.Into != null
            && selectStmt.Into.BaseIdentifier?.Value?.StartsWith("#") == true;
    }

    /// <summary>
    /// Returns true if the SELECT statement reads from a temp table (e.g., SELECT * FROM #temp).
    /// These are handled by reading directly from the session context rather than going
    /// through the plan builder and FetchXML generation.
    /// </summary>
    private bool IsTempTableSelect(SelectStatement selectStmt)
    {
        if (_session == null)
            return false;

        if (selectStmt.QueryExpression is not QuerySpecification querySpec)
            return false;

        if (querySpec.FromClause?.TableReferences.Count != 1)
            return false;

        if (querySpec.FromClause.TableReferences[0] is not NamedTableReference named)
            return false;

        var tableName = named.SchemaObject?.BaseIdentifier?.Value;
        return tableName != null && tableName.StartsWith("#") && _session.TempTableExists(tableName);
    }

    /// <summary>
    /// Extracts the temp table name from a SELECT statement's FROM clause.
    /// </summary>
    private static string? GetTempTableNameFromSelect(SelectStatement selectStmt)
    {
        if (selectStmt.QueryExpression is not QuerySpecification querySpec)
            return null;

        if (querySpec.FromClause?.TableReferences.Count != 1)
            return null;

        if (querySpec.FromClause.TableReferences[0] is not NamedTableReference named)
            return null;

        var tableName = named.SchemaObject?.BaseIdentifier?.Value;
        return tableName != null && tableName.StartsWith("#") ? tableName : null;
    }

    /// <summary>
    /// Executes a SELECT ... INTO #temp statement. Runs the SELECT query (without the INTO),
    /// collects all result rows, creates a temp table in the session context with column names
    /// derived from the results, and populates it with the rows.
    /// </summary>
    private async Task ExecuteSelectIntoAsync(
        SelectStatement selectStmt,
        VariableScope scope,
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var session = _session
            ?? throw new InvalidOperationException(
                "SELECT INTO #temp requires a SessionContext.");

        var tempTableName = selectStmt.Into.BaseIdentifier.Value;
        if (!tempTableName.StartsWith("#"))
            tempTableName = "#" + tempTableName;

        // Execute the SELECT portion to get the result rows.
        // For FROM-less SELECTs (e.g., SELECT 1 AS id INTO #temp), evaluate directly.
        // For SELECTs with FROM, delegate to ExecuteDataStatementAsync (which handles plan building).
        List<QueryRow> sourceRows;
        if (IsFromlessSelect(selectStmt))
        {
            sourceRows = ExecuteFromlessSelect(selectStmt, scope);
        }
        else if (IsTempTableSelect(selectStmt))
        {
            sourceRows = ExecuteTempTableSelect(selectStmt);
        }
        else
        {
            sourceRows = await ExecuteDataStatementAsync(
                selectStmt, scope, context, cancellationToken);
        }

        // Derive column names from the first result row (or empty if no rows)
        var columns = sourceRows.Count > 0
            ? sourceRows[0].Values.Keys.ToList()
            : new List<string>();

        // Create the temp table and populate it
        session.CreateTempTable(tempTableName, columns);
        if (sourceRows.Count > 0)
        {
            session.InsertIntoTempTable(tempTableName, sourceRows);
        }
    }

    /// <summary>
    /// Executes a SELECT from a temp table. Reads rows from the session context
    /// and optionally applies column projection based on the SELECT list.
    /// Handles SELECT * and explicit column lists.
    /// </summary>
    private List<QueryRow> ExecuteTempTableSelect(SelectStatement selectStmt)
    {
        var session = _session
            ?? throw new InvalidOperationException(
                "SELECT FROM #temp requires a SessionContext.");

        var tableName = GetTempTableNameFromSelect(selectStmt)
            ?? throw new InvalidOperationException(
                "Cannot determine temp table name from SELECT statement.");

        var rows = session.GetTempTableRows(tableName);

        // Check if we need column projection (SELECT col1, col2 vs SELECT *)
        if (selectStmt.QueryExpression is QuerySpecification querySpec)
        {
            var hasStarElement = querySpec.SelectElements.Any(e => e is SelectStarExpression);

            if (!hasStarElement && querySpec.SelectElements.Count > 0)
            {
                // Project only the requested columns
                var projectedRows = new List<QueryRow>();
                foreach (var row in rows)
                {
                    var projected = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);
                    foreach (var element in querySpec.SelectElements)
                    {
                        if (element is SelectScalarExpression scalar)
                        {
                            var alias = scalar.ColumnName?.Value;
                            string? sourceColumn = null;

                            // Get the source column name from the expression
                            if (scalar.Expression is ColumnReferenceExpression colRef)
                            {
                                sourceColumn = colRef.MultiPartIdentifier?.Identifiers
                                    .LastOrDefault()?.Value;
                            }

                            var columnName = sourceColumn ?? alias ?? "column";
                            var outputName = alias ?? columnName;

                            if (row.Values.TryGetValue(columnName, out var value))
                            {
                                projected[outputName] = value;
                            }
                        }
                    }
                    projectedRows.Add(new QueryRow(projected, tableName));
                }
                return projectedRows;
            }
        }

        // SELECT * — return all columns as-is
        return rows.ToList();
    }

    /// <summary>
    /// Evaluates a FROM-less SELECT (e.g., SELECT ERROR_MESSAGE() AS msg, SELECT 1+1 AS result)
    /// by compiling each select element as a scalar expression and producing a single result row.
    /// </summary>
    private List<QueryRow> ExecuteFromlessSelect(
        SelectStatement selectStmt,
        VariableScope scope)
    {
        var querySpec = (QuerySpecification)selectStmt.QueryExpression;
        var emptyRow = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

        var resultValues = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in querySpec.SelectElements)
        {
            if (element is SelectScalarExpression scalar)
            {
                var alias = scalar.ColumnName?.Value
                    ?? scalar.Expression?.ToString()
                    ?? "column";

                var compiled = _expressionCompiler.CompileScalar(scalar.Expression);
                var value = compiled(emptyRow);
                resultValues[alias] = QueryValue.Simple(value);
            }
        }

        return new List<QueryRow> { new QueryRow(resultValues, "(expression)") };
    }

    /// <summary>
    /// Executes a SELECT @var = expr statement, assigning expression results
    /// to variables in scope. Supports both no-FROM (direct expression evaluation)
    /// and FROM clause (executes query, assigns from last row) forms.
    /// </summary>
    private async Task ExecuteSelectAssignmentAsync(
        SelectStatement selectStmt,
        VariableScope scope,
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var querySpec = (QuerySpecification)selectStmt.QueryExpression;

        if (querySpec.FromClause != null)
        {
            // Has FROM clause: execute query and assign from last row
            var rows = await ExecuteDataStatementAsync(
                selectStmt, scope, context, cancellationToken);

            if (rows.Count > 0)
            {
                var lastRow = rows[^1];
                foreach (var element in querySpec.SelectElements)
                {
                    if (element is SelectSetVariable setVar)
                    {
                        var compiled = _expressionCompiler.CompileScalar(setVar.Expression);
                        var value = compiled(lastRow.Values);
                        var varName = setVar.Variable.Name;
                        if (!varName.StartsWith("@"))
                            varName = "@" + varName;
                        scope.Set(varName, value);
                    }
                }
            }
        }
        else
        {
            // No FROM clause: evaluate expressions directly
            var emptyRow = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in querySpec.SelectElements)
            {
                if (element is SelectSetVariable setVar)
                {
                    var compiled = _expressionCompiler.CompileScalar(setVar.Expression);
                    var value = compiled(emptyRow);
                    var varName = setVar.Variable.Name;
                    if (!varName.StartsWith("@"))
                        varName = "@" + varName;
                    scope.Set(varName, value);
                }
            }
        }
    }

    /// <summary>
    /// Executes a PRINT statement. Evaluates the expression and routes the
    /// message to the context's progress reporter if available.
    /// Produces no rows.
    /// </summary>
    private void ExecutePrint(
        PrintStatement printStmt,
        VariableScope scope,
        QueryPlanContext context)
    {
        var message = EvaluateScalarAsString(printStmt.Expression, scope);
        context.ProgressReporter?.ReportPhase("PRINT", message);
    }

    /// <summary>
    /// Executes a THROW statement. If parameters are provided (error_number, message, state),
    /// throws a <see cref="QueryExecutionException"/> with the message. A bare THROW (no args)
    /// re-throws the current error from the CATCH block scope (@@ERROR_MESSAGE).
    /// </summary>
    private void ExecuteThrow(ThrowStatement throwStmt, VariableScope scope)
    {
        if (throwStmt.Message == null && throwStmt.ErrorNumber == null && throwStmt.State == null)
        {
            // Bare THROW — re-throw the current error from the CATCH context
            string? reThrowMessage = null;
            if (scope.IsDeclared("@@ERROR_MESSAGE"))
            {
                reThrowMessage = scope.Get("@@ERROR_MESSAGE")?.ToString();
            }

            throw new QueryExecutionException(
                QueryErrorCode.ExecutionFailed,
                reThrowMessage ?? "THROW statement executed with no error context.");
        }

        // THROW with explicit parameters: THROW error_number, 'message', state
        var message = EvaluateScalarAsString(throwStmt.Message, scope);
        throw new QueryExecutionException(QueryErrorCode.ExecutionFailed, message);
    }

    /// <summary>
    /// Executes a RAISERROR statement. The first parameter is the format string,
    /// the second is severity, the third is state. Optional parameters after state
    /// are format arguments that replace %s, %d, %i placeholders.
    /// Severity &gt;= 11 throws a <see cref="QueryExecutionException"/>;
    /// severity &lt; 11 is informational (routed to progress reporter like PRINT).
    /// </summary>
    private void ExecuteRaiseError(
        RaiseErrorStatement raiseError,
        VariableScope scope,
        QueryPlanContext context)
    {
        var formatString = EvaluateScalarAsString(raiseError.FirstParameter, scope);

        var severityValue = EvaluateScalarAsObject(raiseError.SecondParameter, scope);
        var severity = Convert.ToInt32(severityValue, CultureInfo.InvariantCulture);

        // Evaluate optional parameters for %s/%d/%i substitution
        var args = new List<object?>();
        foreach (var param in raiseError.OptionalParameters)
        {
            args.Add(EvaluateScalarAsObject(param, scope));
        }

        var formattedMessage = FormatRaiseErrorMessage(formatString, args);

        if (severity >= 11)
        {
            throw new QueryExecutionException(QueryErrorCode.ExecutionFailed, formattedMessage);
        }

        // Informational (severity < 11) — route to progress reporter like PRINT
        context.ProgressReporter?.ReportPhase("RAISERROR", formattedMessage);
    }

    /// <summary>
    /// Evaluates a ScriptDom <see cref="ScalarExpression"/> and returns the result as a string.
    /// Uses <see cref="ExpressionCompiler"/> for evaluation with the current variable scope.
    /// </summary>
    private string EvaluateScalarAsString(ScalarExpression expression, VariableScope scope)
    {
        var result = EvaluateScalarAsObject(expression, scope);
        return result?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Evaluates a ScriptDom <see cref="ScalarExpression"/> and returns the raw object result.
    /// </summary>
    private object? EvaluateScalarAsObject(ScalarExpression expression, VariableScope scope)
    {
        var compiled = _expressionCompiler.CompileScalar(expression);
        return compiled(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Formats a RAISERROR message string by replacing %s, %d, and %i placeholders
    /// with the corresponding format arguments, in order.
    /// </summary>
    private static string FormatRaiseErrorMessage(string format, IReadOnlyList<object?> args)
    {
        var argIndex = 0;
        return Regex.Replace(format, @"%[sdi]", match =>
        {
            if (argIndex >= args.Count)
                return match.Value;

            var arg = args[argIndex++];
            return match.Value switch
            {
                "%s" => arg?.ToString() ?? "(null)",
                "%d" or "%i" => Convert.ToInt64(arg ?? 0, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture),
                _ => match.Value
            };
        });
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
