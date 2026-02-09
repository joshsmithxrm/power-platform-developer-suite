using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;

namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// Validates SQL text and produces diagnostics (errors, warnings, info).
/// Combines parse-level validation with metadata-aware semantic checks.
/// </summary>
/// <remarks>
/// Validation sources:
/// <list type="number">
/// <item>Parse errors: extracted from <see cref="SqlParseException"/>.</item>
/// <item>Unknown entity: FROM/JOIN table names checked against metadata.</item>
/// <item>Unknown attribute: column references checked against entity attributes.</item>
/// </list>
/// Never throws — all issues are returned as <see cref="SqlDiagnostic"/> items.
/// </remarks>
public sealed class SqlValidator
{
    private readonly ICachedMetadataProvider? _metadataProvider;

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

        // Step 1: Parse the SQL
        ISqlStatement statement;
        try
        {
            var parser = new SqlParser(sql);
            statement = parser.ParseStatement();
        }
        catch (SqlParseException ex)
        {
            // Extract position info from the parse exception
            var position = ex.Position;
            // Use a reasonable length for the error span (token length or 1 character)
            var length = Math.Max(1, Math.Min(10, sql.Length - position));
            diagnostics.Add(new SqlDiagnostic(position, length, SqlDiagnosticSeverity.Error, ex.Message));
            return diagnostics;
        }
        catch (Exception ex)
        {
            // Unexpected error — report at position 0
            diagnostics.Add(new SqlDiagnostic(0, sql.Length, SqlDiagnosticSeverity.Error,
                $"Unexpected error: {ex.Message}"));
            return diagnostics;
        }

        // Step 2: Semantic validation (requires metadata)
        if (_metadataProvider != null)
        {
            await ValidateStatementAsync(statement, sql, diagnostics, cancellationToken);
        }

        return diagnostics;
    }

    /// <summary>
    /// Dispatches semantic validation based on statement type.
    /// </summary>
    private async Task ValidateStatementAsync(
        ISqlStatement statement, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct)
    {
        switch (statement)
        {
            case SqlSelectStatement select:
                await ValidateSelectAsync(select, sql, diagnostics, ct);
                break;

            case SqlInsertStatement insert:
                await ValidateInsertAsync(insert, sql, diagnostics, ct);
                break;

            case SqlUpdateStatement update:
                await ValidateUpdateAsync(update, sql, diagnostics, ct);
                break;

            case SqlDeleteStatement delete:
                await ValidateDeleteAsync(delete, sql, diagnostics, ct);
                break;

            case SqlBlockStatement block:
                foreach (var stmt in block.Statements)
                {
                    await ValidateStatementAsync(stmt, sql, diagnostics, ct);
                }
                break;

            case SqlIfStatement ifStmt:
                await ValidateStatementAsync(ifStmt.ThenBlock, sql, diagnostics, ct);
                if (ifStmt.ElseBlock != null)
                {
                    await ValidateStatementAsync(ifStmt.ElseBlock, sql, diagnostics, ct);
                }
                break;

            case SqlUnionStatement union:
                foreach (var query in union.Queries)
                {
                    await ValidateSelectAsync(query, sql, diagnostics, ct);
                }
                break;
        }
    }

    /// <summary>
    /// Validates a SELECT statement: checks FROM/JOIN entity names and column references.
    /// </summary>
    private async Task ValidateSelectAsync(
        SqlSelectStatement select, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct)
    {
        // Build a map of alias/table name → entity logical name for resolving columns
        var tableMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Validate FROM entity — search from the FROM keyword position
        var fromSearchFrom = sql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
        if (fromSearchFrom < 0) fromSearchFrom = 0;
        var fromValid = await ValidateEntityAsync(select.From, sql, diagnostics, ct, fromSearchFrom);
        if (fromValid)
        {
            tableMap[select.From.TableName] = select.From.TableName;
            if (select.From.Alias != null)
            {
                tableMap[select.From.Alias] = select.From.TableName;
            }
        }

        // Validate JOIN entities — advance searchFrom past each previous match
        var joinSearchFrom = fromSearchFrom + 4; // past "FROM"
        foreach (var join in select.Joins)
        {
            var joinKeywordPos = sql.IndexOf("JOIN", joinSearchFrom, StringComparison.OrdinalIgnoreCase);
            if (joinKeywordPos >= 0) joinSearchFrom = joinKeywordPos + 4; // past "JOIN"

            var joinValid = await ValidateEntityAsync(join.Table, sql, diagnostics, ct, joinSearchFrom);
            if (joinValid)
            {
                tableMap[join.Table.TableName] = join.Table.TableName;
                if (join.Table.Alias != null)
                {
                    tableMap[join.Table.Alias] = join.Table.TableName;
                }
            }
        }

        // Validate column references in SELECT columns
        foreach (var col in select.Columns)
        {
            if (col is SqlColumnRef colRef && !colRef.IsWildcard)
            {
                await ValidateColumnRefAsync(colRef, tableMap, select.From.TableName, sql, diagnostics, ct);
            }
            else if (col is SqlAggregateColumn aggCol && aggCol.Column != null && !aggCol.Column.IsWildcard)
            {
                await ValidateColumnRefAsync(aggCol.Column, tableMap, select.From.TableName, sql, diagnostics, ct);
            }
        }

        // Validate WHERE clause column references
        if (select.Where != null)
        {
            await ValidateConditionColumnsAsync(select.Where, tableMap, select.From.TableName, sql, diagnostics, ct);
        }

        // Validate GROUP BY column references
        foreach (var groupCol in select.GroupBy)
        {
            if (!groupCol.IsWildcard)
            {
                await ValidateColumnRefAsync(groupCol, tableMap, select.From.TableName, sql, diagnostics, ct);
            }
        }

        // Validate ORDER BY column references
        foreach (var orderItem in select.OrderBy)
        {
            if (!orderItem.Column.IsWildcard)
            {
                await ValidateColumnRefAsync(orderItem.Column, tableMap, select.From.TableName, sql, diagnostics, ct);
            }
        }

        // Validate JOIN ON column references
        foreach (var join in select.Joins)
        {
            if (!join.LeftColumn.IsWildcard)
            {
                await ValidateColumnRefAsync(join.LeftColumn, tableMap, select.From.TableName, sql, diagnostics, ct);
            }
            if (!join.RightColumn.IsWildcard)
            {
                await ValidateColumnRefAsync(join.RightColumn, tableMap, select.From.TableName, sql, diagnostics, ct);
            }
        }
    }

    /// <summary>
    /// Validates an INSERT statement: checks target entity and column names.
    /// </summary>
    private async Task ValidateInsertAsync(
        SqlInsertStatement insert, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var insertSearchFrom = sql.IndexOf("INTO", StringComparison.OrdinalIgnoreCase);
        if (insertSearchFrom < 0) insertSearchFrom = 0;
        var entityValid = await ValidateEntityNameAsync(insert.TargetEntity, sql, diagnostics, ct, insertSearchFrom);
        if (entityValid)
        {
            var attributes = await _metadataProvider!.GetAttributesAsync(insert.TargetEntity, ct);
            var attrNames = new HashSet<string>(
                attributes.Select(a => a.LogicalName), StringComparer.OrdinalIgnoreCase);

            foreach (var colName in insert.Columns)
            {
                if (!attrNames.Contains(colName))
                {
                    var pos = FindIdentifierPosition(sql, colName);
                    diagnostics.Add(new SqlDiagnostic(pos, colName.Length,
                        SqlDiagnosticSeverity.Warning,
                        $"Unknown attribute '{colName}' on entity '{insert.TargetEntity}'"));
                }
            }
        }

        // If INSERT ... SELECT, validate the source query too
        if (insert.SourceQuery != null)
        {
            await ValidateSelectAsync(insert.SourceQuery, sql, diagnostics, ct);
        }
    }

    /// <summary>
    /// Validates an UPDATE statement: checks target entity and column names.
    /// </summary>
    private async Task ValidateUpdateAsync(
        SqlUpdateStatement update, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var tableMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var updateSearchFrom = sql.IndexOf("UPDATE", StringComparison.OrdinalIgnoreCase);
        if (updateSearchFrom < 0) updateSearchFrom = 0;
        var targetValid = await ValidateEntityAsync(update.TargetTable, sql, diagnostics, ct, updateSearchFrom);
        if (targetValid)
        {
            tableMap[update.TargetTable.TableName] = update.TargetTable.TableName;
            if (update.TargetTable.Alias != null)
            {
                tableMap[update.TargetTable.Alias] = update.TargetTable.TableName;
            }

            // Validate SET clause columns
            var attributes = await _metadataProvider!.GetAttributesAsync(update.TargetTable.TableName, ct);
            var attrNames = new HashSet<string>(
                attributes.Select(a => a.LogicalName), StringComparer.OrdinalIgnoreCase);

            foreach (var setClause in update.SetClauses)
            {
                if (!attrNames.Contains(setClause.ColumnName))
                {
                    var pos = FindIdentifierPosition(sql, setClause.ColumnName);
                    diagnostics.Add(new SqlDiagnostic(pos, setClause.ColumnName.Length,
                        SqlDiagnosticSeverity.Warning,
                        $"Unknown attribute '{setClause.ColumnName}' on entity '{update.TargetTable.TableName}'"));
                }
            }
        }
    }

    /// <summary>
    /// Validates a DELETE statement: checks target entity.
    /// </summary>
    private async Task ValidateDeleteAsync(
        SqlDeleteStatement delete, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var deleteSearchFrom = sql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
        if (deleteSearchFrom < 0)
        {
            deleteSearchFrom = sql.IndexOf("DELETE", StringComparison.OrdinalIgnoreCase);
            if (deleteSearchFrom < 0) deleteSearchFrom = 0;
        }
        await ValidateEntityAsync(delete.TargetTable, sql, diagnostics, ct, deleteSearchFrom);
    }

    /// <summary>
    /// Validates a table reference against known entities.
    /// Returns true if the entity is known.
    /// </summary>
    private async Task<bool> ValidateEntityAsync(
        SqlTableRef tableRef, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct, int searchFrom = 0)
    {
        return await ValidateEntityNameAsync(tableRef.TableName, sql, diagnostics, ct, searchFrom);
    }

    /// <summary>
    /// Validates an entity name against known entities.
    /// Returns true if the entity is known.
    /// </summary>
    private async Task<bool> ValidateEntityNameAsync(
        string entityName, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct, int searchFrom = 0)
    {
        var entities = await _metadataProvider!.GetEntitiesAsync(ct);
        var entityNames = new HashSet<string>(
            entities.Select(e => e.LogicalName), StringComparer.OrdinalIgnoreCase);

        if (!entityNames.Contains(entityName))
        {
            var pos = FindIdentifierPosition(sql, entityName, searchFrom);
            diagnostics.Add(new SqlDiagnostic(pos, entityName.Length,
                SqlDiagnosticSeverity.Warning,
                $"Unknown entity '{entityName}'"));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a column reference against entity attributes.
    /// </summary>
    private async Task ValidateColumnRefAsync(
        SqlColumnRef colRef, Dictionary<string, string> tableMap,
        string defaultEntity, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct)
    {
        // Determine which entity this column belongs to
        string? entityName = null;

        if (colRef.TableName != null)
        {
            // Qualified reference: table.column
            if (tableMap.TryGetValue(colRef.TableName, out var mapped))
            {
                entityName = mapped;
            }
            else
            {
                // Unknown table alias — skip attribute validation (the entity error is already reported)
                return;
            }
        }
        else
        {
            // Unqualified: use default entity
            entityName = defaultEntity;
        }

        if (entityName == null) return;

        // Check if the entity is known before checking attributes
        var entities = await _metadataProvider!.GetEntitiesAsync(ct);
        if (!entities.Any(e => e.LogicalName.Equals(entityName, StringComparison.OrdinalIgnoreCase)))
        {
            // Entity not in metadata — skip attribute validation
            return;
        }

        var attributes = await _metadataProvider.GetAttributesAsync(entityName, ct);
        var attrNames = new HashSet<string>(
            attributes.Select(a => a.LogicalName), StringComparer.OrdinalIgnoreCase);

        if (!attrNames.Contains(colRef.ColumnName))
        {
            // For qualified refs (alias.column), search for "alias.column" to get precise position.
            // For unqualified refs, search from SELECT keyword to find first occurrence.
            int pos;
            if (colRef.TableName != null)
            {
                var qualifiedName = colRef.TableName + "." + colRef.ColumnName;
                var qualifiedPos = FindIdentifierPosition(sql, qualifiedName);
                pos = qualifiedPos + colRef.TableName.Length + 1; // point to column part
            }
            else
            {
                pos = FindIdentifierPosition(sql, colRef.ColumnName);
            }

            diagnostics.Add(new SqlDiagnostic(pos, colRef.ColumnName.Length,
                SqlDiagnosticSeverity.Warning,
                $"Unknown attribute '{colRef.ColumnName}' on entity '{entityName}'"));
        }
    }

    /// <summary>
    /// Extracts column references from conditions for validation.
    /// Walks the condition tree and validates any column references found.
    /// </summary>
    private async Task ValidateConditionColumnsAsync(
        ISqlCondition condition, Dictionary<string, string> tableMap,
        string defaultEntity, string sql, List<SqlDiagnostic> diagnostics,
        CancellationToken ct)
    {
        switch (condition)
        {
            case SqlComparisonCondition comp:
                await ValidateColumnRefAsync(comp.Column, tableMap, defaultEntity, sql, diagnostics, ct);
                break;

            case SqlLikeCondition like:
                await ValidateColumnRefAsync(like.Column, tableMap, defaultEntity, sql, diagnostics, ct);
                break;

            case SqlNullCondition nullCond:
                await ValidateColumnRefAsync(nullCond.Column, tableMap, defaultEntity, sql, diagnostics, ct);
                break;

            case SqlInCondition inCond:
                await ValidateColumnRefAsync(inCond.Column, tableMap, defaultEntity, sql, diagnostics, ct);
                break;

            case SqlLogicalCondition logical:
                foreach (var child in logical.Conditions)
                {
                    await ValidateConditionColumnsAsync(child, tableMap, defaultEntity, sql, diagnostics, ct);
                }
                break;
        }
    }

    /// <summary>
    /// Finds the position of an identifier in the SQL text.
    /// Falls back to position 0 if not found.
    /// </summary>
    private static int FindIdentifierPosition(string sql, string identifier, int searchFrom = 0)
    {
        // Case-insensitive search for the identifier, starting from a hint position
        var idx = sql.IndexOf(identifier, searchFrom, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? idx : 0;
    }
}
