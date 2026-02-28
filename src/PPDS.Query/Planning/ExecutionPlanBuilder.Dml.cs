using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Query.Planning.Partitioning;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Execution;
using PPDS.Query.Parsing;
using PPDS.Query.Planning.Nodes;

namespace PPDS.Query.Planning;

public sealed partial class ExecutionPlanBuilder
{
    // ═══════════════════════════════════════════════════════════════════
    //  INSERT planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans an INSERT statement. Handles both INSERT VALUES and INSERT SELECT.
    /// </summary>
    private QueryPlanResult PlanInsert(InsertStatement insert, QueryPlanOptions options)
    {
        var targetEntity = GetInsertTargetEntity(insert);
        var columns = GetInsertColumns(insert);

        IQueryPlanNode rootNode;

        // Check if this is INSERT ... SELECT
        var selectSource = GetInsertSelectSource(insert);
        if (selectSource != null)
        {
            // INSERT SELECT: render the source SELECT as SQL, parse, and plan via ScriptDom path
            var generator = s_scriptGenerator;
            generator.GenerateScript(selectSource, out var selectSql);
            var sourceResult = ParseAndPlanSyntheticSelect(selectSql, options);

            var sourceColumns = ExtractSelectColumnNamesFromQuerySpec(selectSource);
            rootNode = DmlExecuteNode.InsertSelect(
                targetEntity,
                columns,
                sourceResult.RootNode,
                sourceColumns: sourceColumns,
                rowCap: options.DmlRowCap ?? int.MaxValue);
        }
        else
        {
            // INSERT VALUES: compile ScriptDom expressions directly to delegates
            var compiledRows = new List<IReadOnlyList<CompiledScalarExpression>>();
            if (insert.InsertSpecification.InsertSource is ValuesInsertSource valuesSource)
            {
                foreach (var rowValue in valuesSource.RowValues)
                {
                    var compiledRow = new List<CompiledScalarExpression>();
                    foreach (var colVal in rowValue.ColumnValues)
                    {
                        compiledRow.Add(_expressionCompiler.CompileScalar(colVal));
                    }
                    compiledRows.Add(compiledRow);
                }
            }

            rootNode = DmlExecuteNode.InsertValues(
                targetEntity,
                columns,
                compiledRows,
                rowCap: options.DmlRowCap ?? int.MaxValue);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = $"-- DML: INSERT INTO {targetEntity}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = targetEntity
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UPDATE planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans an UPDATE statement. Creates a SELECT to find matching records, wraps with DmlExecuteNode.
    /// Builds a synthetic SQL SELECT string from the UPDATE's target, FROM/JOIN, and WHERE clauses,
    /// parses it via QueryParser, and plans through the normal ScriptDom SELECT path.
    /// </summary>
    private QueryPlanResult PlanUpdate(UpdateStatement update, QueryPlanOptions options)
    {
        // Extract entity name directly from ScriptDom
        if (update.UpdateSpecification.Target is not NamedTableReference targetNamed)
            throw new QueryParseException("UPDATE target must be a named table.");
        var entityName = GetMultiPartName(targetNamed.SchemaObject);
        var baseName = targetNamed.SchemaObject.BaseIdentifier?.Value ?? entityName;

        // Compile SET clauses directly from ScriptDom AST to delegates
        var compiledClauses = new List<CompiledSetClause>();
        var referencedColumnNames = new List<string>();

        foreach (var setClause in update.UpdateSpecification.SetClauses)
        {
            if (setClause is AssignmentSetClause assignment)
            {
                var colName = assignment.Column?.MultiPartIdentifier?.Identifiers?.Count > 0
                    ? assignment.Column.MultiPartIdentifier.Identifiers[
                        assignment.Column.MultiPartIdentifier.Identifiers.Count - 1].Value
                    : "unknown";
                var compiled = _expressionCompiler.CompileScalar(assignment.NewValue);
                compiledClauses.Add(new CompiledSetClause(colName, compiled));

                // Also extract column names referenced in the expression for the SELECT
                var refCols = ExtractColumnNamesFromScriptDom(assignment.NewValue);
                referencedColumnNames.AddRange(refCols);
            }
        }

        // Build a synthetic SELECT SQL string to find matching records.
        // Use baseName (not entityName) for columns since Dataverse columns are simple identifiers.
        // Use ScriptDom GenerateScript for the FROM clause to preserve multi-part names correctly.
        var selectCols = new List<string> { $"[{baseName}id]" };
        foreach (var colName in referencedColumnNames.Distinct())
        {
            if (!selectCols.Contains($"[{colName}]", StringComparer.OrdinalIgnoreCase)
                && !selectCols.Contains(colName, StringComparer.OrdinalIgnoreCase))
                selectCols.Add($"[{colName}]");
        }

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(", ", selectCols));

        // Render FROM clause — use the UPDATE's FROM clause if present (it may contain JOINs),
        // otherwise render the target table via ScriptDom (preserves multi-part names like [ENV].entity).
        if (update.UpdateSpecification.FromClause != null)
        {
            var generator = s_scriptGenerator;
            generator.GenerateScript(update.UpdateSpecification.FromClause, out var fromSql);
            sb.Append(' ').Append(fromSql);
        }
        else
        {
            s_scriptGenerator.GenerateScript(targetNamed, out var targetSql);
            sb.Append(" FROM ").Append(targetSql);
        }

        // Render WHERE clause from ScriptDom
        if (update.UpdateSpecification.WhereClause != null)
        {
            var generator = s_scriptGenerator;
            generator.GenerateScript(update.UpdateSpecification.WhereClause, out var whereSql);
            sb.Append(' ').Append(whereSql);
        }

        // Parse and plan through normal ScriptDom path
        var selectResult = ParseAndPlanSyntheticSelect(sb.ToString(), options);

        var rootNode = DmlExecuteNode.Update(
            entityName,
            selectResult.RootNode,
            compiledClauses,
            rowCap: options.DmlRowCap ?? int.MaxValue);

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = selectResult.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DELETE planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a DELETE statement. Creates a SELECT to find matching record IDs, wraps with DmlExecuteNode.
    /// Builds a synthetic SQL SELECT string from the DELETE's target, FROM/JOIN, and WHERE clauses,
    /// parses it via QueryParser, and plans through the normal ScriptDom SELECT path.
    /// </summary>
    private QueryPlanResult PlanDelete(DeleteStatement delete, QueryPlanOptions options)
    {
        // Extract entity name directly from ScriptDom
        if (delete.DeleteSpecification.Target is not NamedTableReference targetNamed)
            throw new QueryParseException("DELETE target must be a named table.");
        var entityName = GetMultiPartName(targetNamed.SchemaObject);
        var baseName = targetNamed.SchemaObject.BaseIdentifier?.Value ?? entityName;

        // Build a synthetic SELECT SQL string: SELECT [entityid] FROM target [JOINs] [WHERE ...]
        // Use baseName for columns (simple identifiers), ScriptDom for FROM (preserves multi-part names).
        var sb = new StringBuilder();
        sb.Append("SELECT [").Append(baseName).Append("id]");

        // Render FROM clause — use the DELETE's FROM clause if present (it may contain JOINs),
        // otherwise render the target table via ScriptDom (preserves multi-part names like [ENV].entity).
        if (delete.DeleteSpecification.FromClause != null)
        {
            var generator = s_scriptGenerator;
            generator.GenerateScript(delete.DeleteSpecification.FromClause, out var fromSql);
            sb.Append(' ').Append(fromSql);
        }
        else
        {
            s_scriptGenerator.GenerateScript(targetNamed, out var targetSql);
            sb.Append(" FROM ").Append(targetSql);
        }

        // Render WHERE clause from ScriptDom
        if (delete.DeleteSpecification.WhereClause != null)
        {
            var generator = s_scriptGenerator;
            generator.GenerateScript(delete.DeleteSpecification.WhereClause, out var whereSql);
            sb.Append(' ').Append(whereSql);
        }

        // Parse and plan through normal ScriptDom path
        var selectResult = ParseAndPlanSyntheticSelect(sb.ToString(), options);

        var rootNode = DmlExecuteNode.Delete(
            entityName,
            selectResult.RootNode,
            rowCap: options.DmlRowCap ?? int.MaxValue);

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = selectResult.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MERGE planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a MERGE statement. Extracts source query, ON condition, and WHEN clauses,
    /// then creates a MergeNode to execute the merge logic.
    /// </summary>
    private QueryPlanResult PlanMerge(MergeStatement merge, QueryPlanOptions options)
    {
        var spec = merge.MergeSpecification;

        // Target entity
        string targetEntity;
        if (spec.Target is NamedTableReference targetTable)
        {
            targetEntity = targetTable.SchemaObject?.BaseIdentifier?.Value ?? "unknown";
        }
        else
        {
            throw new QueryParseException("MERGE target must be a named table.");
        }

        // Source query: plan the USING clause via ScriptDom path
        IQueryPlanNode sourceNode;
        if (spec.TableReference is NamedTableReference sourceTable)
        {
            // USING sourceTable - render via ScriptDom to preserve multi-part names and escaping
            s_scriptGenerator.GenerateScript(sourceTable, out var sourceTableSql);
            var sql = $"SELECT * FROM {sourceTableSql}";
            var sourceResult = ParseAndPlanSyntheticSelect(sql, options);
            sourceNode = sourceResult.RootNode;
        }
        else if (spec.TableReference is QueryDerivedTable derivedTable
            && derivedTable.QueryExpression is QuerySpecification querySpec)
        {
            // USING (SELECT ...) AS alias — render to SQL, parse, and plan via ScriptDom
            var generator = s_scriptGenerator;
            generator.GenerateScript(querySpec, out var selectSql);
            var sourceResult = ParseAndPlanSyntheticSelect(selectSql, options);
            sourceNode = sourceResult.RootNode;
        }
        else
        {
            throw new QueryParseException(
                $"Unsupported MERGE source type: {spec.TableReference?.GetType().Name ?? "null"}");
        }

        // ON condition: extract match columns
        var matchColumns = ExtractMergeMatchColumns(spec.SearchCondition);

        // WHEN clauses — reject WHEN MATCHED early before parsing details
        MergeWhenNotMatched? whenNotMatched = null;

        if (spec.ActionClauses != null)
        {
            foreach (MergeActionClause clause in spec.ActionClauses)
            {
                if (clause.Condition == MergeCondition.Matched)
                {
                    throw new QueryParseException(
                        "MERGE WHEN MATCHED (UPDATE/DELETE) is not yet supported. " +
                        "Target row lookup from Dataverse is required. " +
                        "Use WHEN NOT MATCHED (INSERT) only, or use separate UPDATE/DELETE statements.");
                }
            }

            foreach (MergeActionClause clause in spec.ActionClauses)
            {
                if (clause.Condition == MergeCondition.NotMatched && clause.Action is InsertMergeAction insertAction)
                {
                    var columns = new List<string>();
                    if (insertAction.Columns != null)
                    {
                        foreach (var col in insertAction.Columns)
                        {
                            var ids = col.MultiPartIdentifier?.Identifiers;
                            if (ids != null && ids.Count > 0)
                            {
                                columns.Add(ids[ids.Count - 1].Value);
                            }
                        }
                    }

                    var values = new List<CompiledScalarExpression>();
                    if (insertAction.Source is ValuesInsertSource valSource
                        && valSource.RowValues?.Count > 0)
                    {
                        foreach (var val in valSource.RowValues[0].ColumnValues)
                        {
                            values.Add(_expressionCompiler.CompileScalar(val));
                        }
                    }

                    whenNotMatched = MergeWhenNotMatched.Insert(columns, values);
                }
            }
        }

        var mergeNode = new MergeNode(sourceNode, targetEntity, matchColumns, null, whenNotMatched);

        return new QueryPlanResult
        {
            RootNode = mergeNode,
            FetchXml = $"-- MERGE INTO {targetEntity}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = targetEntity
        };
    }

    /// <summary>
    /// Extracts match columns from a MERGE ON condition.
    /// Supports simple equality conditions (target.col = source.col).
    /// </summary>
    private static IReadOnlyList<MergeMatchColumn> ExtractMergeMatchColumns(BooleanExpression? searchCondition)
    {
        var matchColumns = new List<MergeMatchColumn>();

        if (searchCondition is BooleanComparisonExpression comp
            && comp.ComparisonType == BooleanComparisonType.Equals)
        {
            var left = GetColumnNameFromExpression(comp.FirstExpression);
            var right = GetColumnNameFromExpression(comp.SecondExpression);
            if (left != null && right != null)
            {
                matchColumns.Add(new MergeMatchColumn(right, left));
            }
        }
        else if (searchCondition is BooleanBinaryExpression binBool
            && binBool.BinaryExpressionType == BooleanBinaryExpressionType.And)
        {
            // Multiple AND conditions
            var leftMatches = ExtractMergeMatchColumns(binBool.FirstExpression);
            var rightMatches = ExtractMergeMatchColumns(binBool.SecondExpression);
            matchColumns.AddRange(leftMatches);
            matchColumns.AddRange(rightMatches);
        }

        return matchColumns;
    }

    private static string? GetColumnNameFromExpression(ScalarExpression expr)
    {
        if (expr is ColumnReferenceExpression colRef)
        {
            var ids = colRef.MultiPartIdentifier?.Identifiers;
            if (ids != null && ids.Count > 0)
            {
                return ids[ids.Count - 1].Value;
            }
        }
        return null;
    }
}
