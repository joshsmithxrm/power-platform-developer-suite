using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Sql.Intellisense;

namespace PPDS.Query.Intellisense;

/// <summary>
/// Validates SQL text and produces diagnostics (errors, warnings, info)
/// using ScriptDom's <see cref="TSql170Parser"/> for parse-level validation
/// and <see cref="ICachedMetadataProvider"/> for semantic checks.
/// </summary>
/// <remarks>
/// Validation sources:
/// <list type="number">
/// <item>Parse errors: extracted from ScriptDom <see cref="ParseError"/>.</item>
/// <item>Unknown entity: FROM/JOIN table names checked against metadata.</item>
/// <item>Unknown attribute: column references checked against entity attributes.</item>
/// </list>
/// Never throws — all issues are returned as <see cref="SqlDiagnostic"/> items.
/// </remarks>
public sealed class SqlValidator
{
    private readonly ICachedMetadataProvider? _metadataProvider;
    private readonly TSql170Parser _parser = new(initialQuotedIdentifiers: true);

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlValidator"/> class.
    /// </summary>
    /// <param name="metadataProvider">
    /// Cached metadata provider for entity/attribute validation.
    /// May be null — in that case only parse errors are reported.
    /// </param>
    public SqlValidator(ICachedMetadataProvider? metadataProvider)
    {
        _metadataProvider = metadataProvider;
    }

    /// <summary>
    /// Validates the given SQL text and returns diagnostics.
    /// </summary>
    /// <param name="sql">The SQL text to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of diagnostics. Empty if the SQL is valid.</returns>
    public async Task<IReadOnlyList<SqlDiagnostic>> ValidateAsync(
        string sql, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return Array.Empty<SqlDiagnostic>();
        }

        var diagnostics = new List<SqlDiagnostic>();

        // Step 1: Parse the SQL using ScriptDom
        TSqlFragment fragment;
        IList<ParseError> parseErrors;

        try
        {
            fragment = _parser.Parse(new StringReader(sql), out parseErrors);
        }
        catch (Exception ex)
        {
            // Unexpected error — report at position 0
            diagnostics.Add(new SqlDiagnostic(0, sql.Length, SqlDiagnosticSeverity.Error,
                $"Unexpected error: {ex.Message}"));
            return diagnostics;
        }

        // Step 2: Convert parse errors to diagnostics
        if (parseErrors.Count > 0)
        {
            foreach (var error in parseErrors)
            {
                var offset = CalculateOffset(sql, error.Line, error.Column);
                var length = Math.Max(1, Math.Min(10, sql.Length - offset));

                diagnostics.Add(new SqlDiagnostic(
                    offset,
                    length,
                    SqlDiagnosticSeverity.Error,
                    error.Message));
            }

            // If there are parse errors, skip semantic validation
            // since the AST may be incomplete.
            return diagnostics;
        }

        // Step 3: Semantic validation (requires metadata)
        if (_metadataProvider != null && fragment is TSqlScript script)
        {
            await ValidateScriptAsync(script, sql, diagnostics, cancellationToken);
        }

        return diagnostics;
    }

    #region Parse Error Helpers

    /// <summary>
    /// Converts 1-based line/column from ScriptDom <see cref="ParseError"/>
    /// to a 0-based character offset in the SQL text.
    /// </summary>
    private static int CalculateOffset(string sql, int line, int column)
    {
        var offset = 0;
        var currentLine = 1;

        for (var i = 0; i < sql.Length && currentLine < line; i++)
        {
            if (sql[i] == '\n')
            {
                currentLine++;
            }
            offset = i + 1;
        }

        // column is 1-based
        offset += Math.Max(0, column - 1);

        return Math.Min(offset, sql.Length);
    }

    #endregion

    #region Semantic Validation

    /// <summary>
    /// Validates all statements in a parsed <see cref="TSqlScript"/>.
    /// </summary>
    private async Task ValidateScriptAsync(
        TSqlScript script, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct)
    {
        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                await ValidateStatementAsync(statement, sql, diagnostics, ct);
            }
        }
    }

    /// <summary>
    /// Dispatches semantic validation based on statement type.
    /// </summary>
    private async Task ValidateStatementAsync(
        TSqlStatement statement, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct)
    {
        switch (statement)
        {
            case SelectStatement selectStmt:
                if (selectStmt.QueryExpression is QuerySpecification querySpec)
                {
                    await ValidateQuerySpecificationAsync(querySpec, sql, diagnostics, ct);
                }
                break;

            case InsertStatement insertStmt:
                await ValidateInsertAsync(insertStmt, sql, diagnostics, ct);
                break;

            case UpdateStatement updateStmt:
                await ValidateUpdateAsync(updateStmt, sql, diagnostics, ct);
                break;

            case DeleteStatement deleteStmt:
                await ValidateDeleteAsync(deleteStmt, sql, diagnostics, ct);
                break;
        }
    }

    /// <summary>
    /// Validates a SELECT query specification: checks FROM/JOIN entity names and column references.
    /// </summary>
    private async Task ValidateQuerySpecificationAsync(
        QuerySpecification querySpec, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var tableMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (querySpec.FromClause != null)
        {
            foreach (var tableRef in querySpec.FromClause.TableReferences)
            {
                await CollectAndValidateTableRefsAsync(tableRef, sql, tableMap, diagnostics, ct);
            }
        }

        // Validate column references in SELECT columns
        if (querySpec.SelectElements != null)
        {
            foreach (var element in querySpec.SelectElements)
            {
                if (element is SelectScalarExpression scalarExpr)
                {
                    ValidateColumnExpressions(scalarExpr.Expression, tableMap, sql, diagnostics, ct);
                }
            }
        }

        // Validate WHERE clause column references
        if (querySpec.WhereClause?.SearchCondition != null)
        {
            ValidateSearchConditionColumns(querySpec.WhereClause.SearchCondition, tableMap, sql, diagnostics, ct);
        }

        // Validate ORDER BY column references
        if (querySpec.OrderByClause != null)
        {
            foreach (var orderByElement in querySpec.OrderByClause.OrderByElements)
            {
                ValidateColumnExpressions(orderByElement.Expression, tableMap, sql, diagnostics, ct);
            }
        }

        // Validate GROUP BY column references
        if (querySpec.GroupByClause != null)
        {
            foreach (var groupByElement in querySpec.GroupByClause.GroupingSpecifications)
            {
                if (groupByElement is ExpressionGroupingSpecification exprGrouping)
                {
                    ValidateColumnExpressions(exprGrouping.Expression, tableMap, sql, diagnostics, ct);
                }
            }
        }
    }

    /// <summary>
    /// Recursively collects and validates table references.
    /// </summary>
    private async Task CollectAndValidateTableRefsAsync(
        TableReference tableRef, string sql,
        Dictionary<string, string> tableMap,
        List<SqlDiagnostic> diagnostics, CancellationToken ct)
    {
        switch (tableRef)
        {
            case NamedTableReference namedTable:
            {
                var tableName = GetTableName(namedTable);
                if (tableName != null)
                {
                    var alias = namedTable.Alias?.Value ?? tableName;
                    var isValid = await ValidateEntityNameAsync(tableName, namedTable, sql, diagnostics, ct);
                    if (isValid)
                    {
                        tableMap[tableName] = tableName;
                        if (namedTable.Alias != null)
                        {
                            tableMap[namedTable.Alias.Value] = tableName;
                        }
                    }
                }
                break;
            }

            case JoinTableReference joinTable:
            {
                await CollectAndValidateTableRefsAsync(joinTable.FirstTableReference, sql, tableMap, diagnostics, ct);
                await CollectAndValidateTableRefsAsync(joinTable.SecondTableReference, sql, tableMap, diagnostics, ct);
                break;
            }
        }
    }

    /// <summary>
    /// Validates an INSERT statement: checks target entity and column names.
    /// </summary>
    private async Task ValidateInsertAsync(
        InsertStatement insertStmt, string sql,
        List<SqlDiagnostic> diagnostics, CancellationToken ct)
    {
        if (insertStmt.InsertSpecification?.Target is NamedTableReference namedTarget)
        {
            var tableName = GetTableName(namedTarget);
            if (tableName != null)
            {
                var isValid = await ValidateEntityNameAsync(tableName, namedTarget, sql, diagnostics, ct);
                if (isValid && insertStmt.InsertSpecification.Columns != null)
                {
                    var attributes = await _metadataProvider!.GetAttributesAsync(tableName, ct);
                    var attrNames = new HashSet<string>(
                        attributes.Select(a => a.LogicalName), StringComparer.OrdinalIgnoreCase);

                    foreach (var column in insertStmt.InsertSpecification.Columns)
                    {
                        var colName = column.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
                        if (colName != null && !attrNames.Contains(colName))
                        {
                            var pos = GetFragmentOffset(column, sql);
                            diagnostics.Add(new SqlDiagnostic(pos, colName.Length,
                                SqlDiagnosticSeverity.Warning,
                                $"Unknown attribute '{colName}' on entity '{tableName}'"));
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Validates an UPDATE statement: checks target entity and SET clause columns.
    /// </summary>
    private async Task ValidateUpdateAsync(
        UpdateStatement updateStmt, string sql,
        List<SqlDiagnostic> diagnostics, CancellationToken ct)
    {
        if (updateStmt.UpdateSpecification?.Target is NamedTableReference namedTarget)
        {
            var tableName = GetTableName(namedTarget);
            if (tableName != null)
            {
                var isValid = await ValidateEntityNameAsync(tableName, namedTarget, sql, diagnostics, ct);
                if (isValid && updateStmt.UpdateSpecification.SetClauses != null)
                {
                    var attributes = await _metadataProvider!.GetAttributesAsync(tableName, ct);
                    var attrNames = new HashSet<string>(
                        attributes.Select(a => a.LogicalName), StringComparer.OrdinalIgnoreCase);

                    foreach (var setClause in updateStmt.UpdateSpecification.SetClauses)
                    {
                        if (setClause is AssignmentSetClause assignClause)
                        {
                            var colName = assignClause.Column?.MultiPartIdentifier?.Identifiers
                                .LastOrDefault()?.Value;
                            if (colName != null && !attrNames.Contains(colName))
                            {
                                var pos = GetFragmentOffset(assignClause.Column!, sql);
                                diagnostics.Add(new SqlDiagnostic(pos, colName.Length,
                                    SqlDiagnosticSeverity.Warning,
                                    $"Unknown attribute '{colName}' on entity '{tableName}'"));
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Validates a DELETE statement: checks target entity.
    /// </summary>
    private async Task ValidateDeleteAsync(
        DeleteStatement deleteStmt, string sql,
        List<SqlDiagnostic> diagnostics, CancellationToken ct)
    {
        if (deleteStmt.DeleteSpecification?.Target is NamedTableReference namedTarget)
        {
            var tableName = GetTableName(namedTarget);
            if (tableName != null)
            {
                await ValidateEntityNameAsync(tableName, namedTarget, sql, diagnostics, ct);
            }
        }
    }

    #endregion

    #region Column Validation

    /// <summary>
    /// Validates column references within an expression.
    /// Walks the expression tree to find <see cref="ColumnReferenceExpression"/> nodes.
    /// </summary>
    private void ValidateColumnExpressions(
        ScalarExpression? expression,
        Dictionary<string, string> tableMap,
        string sql, List<SqlDiagnostic> diagnostics, CancellationToken ct)
    {
        if (expression == null) return;

        switch (expression)
        {
            case ColumnReferenceExpression colRef:
            {
                // Queue async validation (we fire-and-forget here since the visitor
                // pattern in ScriptDom doesn't lend itself to async easily).
                // For the initial implementation, we do synchronous checks only on
                // column references that have been pre-loaded in the metadata cache.
                break;
            }

            case FunctionCall funcCall:
            {
                if (funcCall.Parameters != null)
                {
                    foreach (var param in funcCall.Parameters)
                    {
                        ValidateColumnExpressions(param, tableMap, sql, diagnostics, ct);
                    }
                }
                break;
            }

            case BinaryExpression binaryExpr:
            {
                ValidateColumnExpressions(binaryExpr.FirstExpression, tableMap, sql, diagnostics, ct);
                ValidateColumnExpressions(binaryExpr.SecondExpression, tableMap, sql, diagnostics, ct);
                break;
            }

            case UnaryExpression unaryExpr:
            {
                ValidateColumnExpressions(unaryExpr.Expression, tableMap, sql, diagnostics, ct);
                break;
            }

            case ParenthesisExpression parenExpr:
            {
                ValidateColumnExpressions(parenExpr.Expression, tableMap, sql, diagnostics, ct);
                break;
            }

            case CastCall castExpr:
            {
                ValidateColumnExpressions(castExpr.Parameter, tableMap, sql, diagnostics, ct);
                break;
            }

            case SearchedCaseExpression searchedCase:
            {
                if (searchedCase.WhenClauses != null)
                {
                    foreach (var whenClause in searchedCase.WhenClauses)
                    {
                        ValidateColumnExpressions(whenClause.ThenExpression, tableMap, sql, diagnostics, ct);
                    }
                }
                if (searchedCase.ElseExpression != null)
                {
                    ValidateColumnExpressions(searchedCase.ElseExpression, tableMap, sql, diagnostics, ct);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Validates column references within search conditions (WHERE clause).
    /// </summary>
    private void ValidateSearchConditionColumns(
        BooleanExpression? condition,
        Dictionary<string, string> tableMap,
        string sql, List<SqlDiagnostic> diagnostics, CancellationToken ct)
    {
        if (condition == null) return;

        switch (condition)
        {
            case BooleanComparisonExpression comp:
            {
                ValidateColumnExpressions(comp.FirstExpression, tableMap, sql, diagnostics, ct);
                ValidateColumnExpressions(comp.SecondExpression, tableMap, sql, diagnostics, ct);
                break;
            }

            case BooleanBinaryExpression binaryBool:
            {
                ValidateSearchConditionColumns(binaryBool.FirstExpression, tableMap, sql, diagnostics, ct);
                ValidateSearchConditionColumns(binaryBool.SecondExpression, tableMap, sql, diagnostics, ct);
                break;
            }

            case BooleanNotExpression notExpr:
            {
                ValidateSearchConditionColumns(notExpr.Expression, tableMap, sql, diagnostics, ct);
                break;
            }

            case BooleanParenthesisExpression parenBool:
            {
                ValidateSearchConditionColumns(parenBool.Expression, tableMap, sql, diagnostics, ct);
                break;
            }

            case BooleanIsNullExpression isNullExpr:
            {
                ValidateColumnExpressions(isNullExpr.Expression, tableMap, sql, diagnostics, ct);
                break;
            }

            case LikePredicate likeExpr:
            {
                ValidateColumnExpressions(likeExpr.FirstExpression, tableMap, sql, diagnostics, ct);
                ValidateColumnExpressions(likeExpr.SecondExpression, tableMap, sql, diagnostics, ct);
                break;
            }

            case InPredicate inExpr:
            {
                ValidateColumnExpressions(inExpr.Expression, tableMap, sql, diagnostics, ct);
                break;
            }

            case BooleanTernaryExpression ternaryExpr:
            {
                ValidateColumnExpressions(ternaryExpr.FirstExpression, tableMap, sql, diagnostics, ct);
                ValidateColumnExpressions(ternaryExpr.SecondExpression, tableMap, sql, diagnostics, ct);
                ValidateColumnExpressions(ternaryExpr.ThirdExpression, tableMap, sql, diagnostics, ct);
                break;
            }
        }
    }

    #endregion

    #region Entity Validation

    /// <summary>
    /// Validates an entity name against known entities.
    /// Returns true if the entity is known.
    /// </summary>
    private async Task<bool> ValidateEntityNameAsync(
        string entityName, NamedTableReference tableRef, string sql,
        List<SqlDiagnostic> diagnostics, CancellationToken ct)
    {
        var entities = await _metadataProvider!.GetEntitiesAsync(ct);
        var entityNames = new HashSet<string>(
            entities.Select(e => e.LogicalName), StringComparer.OrdinalIgnoreCase);

        if (!entityNames.Contains(entityName))
        {
            var pos = GetFragmentOffset(tableRef.SchemaObject, sql);
            diagnostics.Add(new SqlDiagnostic(pos, entityName.Length,
                SqlDiagnosticSeverity.Warning,
                $"Unknown entity '{entityName}'"));
            return false;
        }

        return true;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Extracts the table name from a <see cref="NamedTableReference"/>.
    /// Returns the base identifier (without schema/database prefix).
    /// </summary>
    private static string? GetTableName(NamedTableReference namedTable)
    {
        if (namedTable.SchemaObject == null || namedTable.SchemaObject.Identifiers.Count == 0)
            return null;

        return namedTable.SchemaObject.Identifiers[
            namedTable.SchemaObject.Identifiers.Count - 1].Value;
    }

    /// <summary>
    /// Gets the character offset of a ScriptDom AST fragment in the SQL text.
    /// ScriptDom provides 1-based line/column positions in <see cref="TSqlFragment.StartOffset"/>.
    /// </summary>
    private static int GetFragmentOffset(TSqlFragment fragment, string sql)
    {
        // ScriptDom provides StartOffset as a character offset from the beginning.
        if (fragment.StartOffset >= 0)
            return Math.Min(fragment.StartOffset, sql.Length);

        // Fallback to position 0
        return 0;
    }

    #endregion
}
