using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Query.Planning;

/// <summary>
/// The allowlist of DML shapes the engine is proven (by tests) to honour. INSERT, UPDATE and DELETE run only when every
/// part of the statement is on it; anything else is refused before anything executes, with a message naming the part
/// that is not supported. Shared by the DML safety guard (so the refusal comes before any confirmation prompt) and the
/// planner (so no caller can execute such a statement). MERGE, EXECUTE AS and REVERT are always refused.
/// </summary>
/// <remarks>
/// <para>Supported shapes (everything else is refused):</para>
/// <list type="bullet">
/// <item>Target: a table of the connected environment named directly (<c>account</c>, <c>dbo.account</c>).</item>
/// <item>UPDATE/DELETE <c>FROM</c>: only the target table itself, optionally aliased (<c>UPDATE a ... FROM account a</c>).
/// Joins, derived tables, functions and other environments in FROM are refused.</item>
/// <item>WHERE (UPDATE, DELETE, INSERT ... SELECT): AND, OR and parentheses over <c>column op constant</c>
/// (=, &lt;&gt;, !=, &lt;, &gt;, &lt;=, &gt;=), <c>column [NOT] IN (constants)</c>, <c>column [NOT] LIKE 'pattern'</c>
/// (no <c>[ ]</c> or ESCAPE), <c>column [NOT] BETWEEN constant AND constant</c> and <c>column IS [NOT] NULL</c>. These are
/// sent to Dataverse as FetchXML conditions; a constant is a string or number literal. NOT, functions, variables,
/// subqueries, column-to-column comparisons and comparisons with NULL are refused.</item>
/// <item>Values (UPDATE SET, INSERT ... SELECT list): literals, the target's (or source's) columns, arithmetic,
/// COALESCE, ISNULL, NULLIF, IIF, CASE, CAST/CONVERT to nvarchar(n), int or decimal(p, s), TRY_CAST/TRY_CONVERT to int or
/// decimal(p, s) and the functions in <see cref="AllowedFunctions"/> with their proven argument forms. Variables and
/// subqueries are refused.</item>
/// <item>INSERT: an explicit column list without duplicates; up to 1,000 VALUES rows of the same length whose values are
/// literals, NULL or CAST('&lt;guid&gt;' AS uniqueidentifier); or a SELECT from the same table whose list has the same
/// length, where every item is a column or an aliased expression with a unique name that is not a column the SELECT reads
/// (values are mapped by position). <c>TOP n</c> / <c>TOP (n)</c> up to 5,000 only with ORDER BY on columns, and ORDER BY only
/// with TOP.</item>
/// <item>No OPTION (...) clause.</item>
/// </list>
/// </remarks>
internal static class DmlStatementSupport
{
    private const string NothingExecuted = " Nothing was executed.";

    /// <summary>Scalar functions proven (by tests) to evaluate correctly in DML values.</summary>
    public static readonly IReadOnlySet<string> AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // REPLACE is not listed: the organisation's collation matches text without regard to accents, the evaluator does not.
        "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "LEN", "CONCAT", "SUBSTRING", "ABS", "ROUND", "ISNULL"
    };

    private static readonly Sql160ScriptGenerator Generator = new();

    /// <summary>The refusal message for the first unsupported part of <paramref name="statement"/>, or null.</summary>
    public static string? FindUnsupported(TSqlStatement statement) => statement switch
    {
        DeleteStatement or UpdateStatement or InsertStatement when DecorationCheck.Find(statement) is { } decoration => decoration,
        DeleteStatement delete => CheckDelete(delete),
        UpdateStatement update => CheckUpdate(update),
        InsertStatement insert => CheckInsert(insert),
        MergeStatement =>
            "MERGE is not supported: it would not change any rows." + NothingExecuted +
            " Use INSERT ... SELECT, UPDATE or DELETE instead.",
        ExecuteAsStatement or RevertStatement =>
            "EXECUTE AS and REVERT (impersonation) are not supported, so statements would not run as the user you named." +
            NothingExecuted,
        _ => null
    };

    /// <summary>Throws a <see cref="DmlSafetyRefusalException"/> when <paramref name="statement"/> is not on the allowlist.</summary>
    public static void ThrowIfUnsupported(TSqlStatement statement)
    {
        var message = FindUnsupported(statement);
        if (message != null)
            throw new DmlSafetyRefusalException(QueryErrorCode.DmlBlocked, message);
    }

    /// <summary>
    /// The base table an UPDATE/DELETE writes to, after <see cref="FindUnsupported"/> accepted it: the FROM table when the
    /// target is its alias or name, otherwise the target itself.
    /// </summary>
    public static NamedTableReference ResolveTarget(TableReference target, FromClause? fromClause)
    {
        var named = (NamedTableReference)target;
        return fromClause?.TableReferences.Count == 1 && fromClause.TableReferences[0] is NamedTableReference from
            ? from
            : named;
    }

    // ── statements ─────────────────────────────────────────────────────

    private static string? CheckDelete(DeleteStatement delete)
    {
        var spec = delete.DeleteSpecification;
        var problem = CommonChecks(delete, "DELETE", spec.TopRowFilter, spec.OutputClause, spec.OutputIntoClause);
        if (problem != null)
            return problem;
        return CheckTargetAndFrom("DELETE", spec.Target, spec.FromClause, out var names) ?? CheckWhere("DELETE", spec.WhereClause, names);
    }

    private static string? CheckUpdate(UpdateStatement update)
    {
        var spec = update.UpdateSpecification;
        var problem = CommonChecks(update, "UPDATE", spec.TopRowFilter, spec.OutputClause, spec.OutputIntoClause);
        if (problem != null)
            return problem;
        problem = CheckTargetAndFrom("UPDATE", spec.Target, spec.FromClause, out var names);
        if (problem != null)
            return problem;

        var setColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setClause in spec.SetClauses)
        {
            if (setClause is AssignmentSetClause { Column.MultiPartIdentifier.Identifiers: { Count: > 0 } ids }
                && !setColumns.Add(ids[ids.Count - 1].Value))
            {
                return $"UPDATE ... SET names the column {ids[ids.Count - 1].Value} more than once." + NothingExecuted;
            }

            switch (setClause)
            {
                case AssignmentSetClause { Variable: not null }:
                    return "UPDATE ... SET @variable = ... is not supported: assigning a variable does not change a column." +
                           NothingExecuted;
                case AssignmentSetClause { Column: null }:
                    return "UPDATE ... SET must name a column." + NothingExecuted;
                case AssignmentSetClause assignment when assignment.AssignmentKind != AssignmentKind.Equals:
                    var column = assignment.Column.MultiPartIdentifier.Identifiers.Last().Value;
                    return $"UPDATE ... SET {column} {Operator(assignment.AssignmentKind)} is not supported; only 'column = value' " +
                           "assignments are." + NothingExecuted + $" Write it as SET {column} = {column} ... instead.";
                case AssignmentSetClause assignment:
                    problem = CheckColumn(assignment.Column, names, "the SET column")
                        ?? CheckValue(assignment.NewValue, names, "UPDATE ... SET");
                    if (problem != null)
                        return problem;
                    break;
                default:
                    return "UPDATE ... SET supports only 'column = value' assignments." + NothingExecuted;
            }
        }

        return CheckWhere("UPDATE", spec.WhereClause, names);
    }

    private static string? CheckInsert(InsertStatement insert)
    {
        var spec = insert.InsertSpecification;
        if (spec.TopRowFilter != null)
        {
            return "INSERT TOP (n) is not supported." + NothingExecuted +
                   " Put TOP in the SELECT instead: INSERT INTO table (columns) SELECT TOP (n) ... FROM table ORDER BY column.";
        }

        var problem = CommonChecks(insert, "INSERT", null, spec.OutputClause, spec.OutputIntoClause)
            ?? CheckLocalTable("INSERT", spec.Target);
        if (problem != null)
            return problem;

        var columnCount = spec.Columns?.Count ?? 0;
        switch (spec.InsertSource)
        {
            case null or ValuesInsertSource { IsDefaultValues: true }:
                return "INSERT ... DEFAULT VALUES is not supported." + NothingExecuted + " Name the columns and give their values.";
            case ExecuteInsertSource:
                return "INSERT ... EXEC is not supported." + NothingExecuted;
        }

        if (columnCount == 0)
            return NoColumnList;
        var badColumn = spec.Columns!.FirstOrDefault(c => c.MultiPartIdentifier.Identifiers.Count != 1
            || !IsLogicalName(c.MultiPartIdentifier.Identifiers[0].Value));
        if (badColumn != null)
            return $"The INSERT column {Sql(badColumn)} must be a plain column name of the target table." + NothingExecuted;
        var duplicate = spec.Columns!
            .GroupBy(c => c.MultiPartIdentifier.Identifiers.Last().Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            return $"INSERT names the column {duplicate.Key} more than once." + NothingExecuted;

        switch (spec.InsertSource)
        {
            case ValuesInsertSource { RowValues.Count: > MaxValuesRows } tooMany:
                return $"INSERT ... VALUES gives {tooMany.RowValues.Count} rows; at most {MaxValuesRows} are supported in one " +
                       "statement (as in SQL Server)." + NothingExecuted + $" Split it into several INSERT statements of at most {MaxValuesRows} rows.";
            case ValuesInsertSource values:
                for (var i = 0; i < values.RowValues.Count; i++)
                {
                    var row = values.RowValues[i].ColumnValues;
                    if (row.Count != columnCount)
                    {
                        return $"INSERT names {columnCount} column(s) but row {i + 1} of VALUES has {row.Count} value(s)." +
                               NothingExecuted;
                    }

                    foreach (var value in row)
                    {
                        problem = CheckInsertValue(value);
                        if (problem != null)
                            return problem;
                    }
                }
                return null;
            case SelectInsertSource { Select: QuerySpecification select }:
                return CheckInsertSelect(select, columnCount, (NamedTableReference)spec.Target);
            case SelectInsertSource:
                return "INSERT ... SELECT with UNION, INTERSECT, EXCEPT or a parenthesised query is not supported." +
                       NothingExecuted + " Insert from each SELECT separately.";
            default:
                return "This INSERT source is not supported." + NothingExecuted;
        }
    }

    /// <summary>
    /// A VALUES item: a literal (text, whole or decimal number, NULL), a negative number, or CAST('&lt;guid&gt;' AS
    /// uniqueidentifier). Only these were proven live; an expression would be evaluated by the engine, not SQL.
    /// </summary>
    private static string? CheckInsertValue(ScalarExpression value)
    {
        const string context = "INSERT ... VALUES";
        switch (value)
        {
            case StringLiteral or IntegerLiteral or NumericLiteral or NullLiteral:
            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral or NumericLiteral }:
            case CastCall { DataType: SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.UniqueIdentifier }, Parameter: StringLiteral }:
                return null;
            case RealLiteral or UnaryExpression { Expression: RealLiteral }:
                return Unsupported($"{Sql(value)} (a floating-point literal)", context, RealLiteralHowTo);
            case ColumnReferenceExpression column:
                return Unsupported($"the column {Sql(column)}", context, " VALUES takes constants; use INSERT ... SELECT to copy columns.");
            case VariableReference or GlobalVariableExpression:
                return Unsupported($"the variable {Sql(value)}", context, " Use a literal value instead.");
            case FunctionCall function when function.FunctionName.Value.Equals("NEWID", StringComparison.OrdinalIgnoreCase):
                return Unsupported("the function NEWID", context, " Dataverse generates row ids; leave the id column out.");
            default:
                return Unsupported(Sql(value), context,
                    " VALUES takes literal values only (text, numbers, NULL, or CAST('<guid>' AS uniqueidentifier)); write the value you want stored as a literal.");
        }
    }

    private static string? CheckInsertSelect(QuerySpecification select, int columnCount, NamedTableReference target)
    {
        const string context = "INSERT ... SELECT";
        if (select.SelectElements.Any(e => e is SelectStarExpression))
        {
            return "INSERT ... SELECT * is not supported: the SELECT columns cannot be matched to the INSERT column list." +
                   NothingExecuted + " List the SELECT columns in the same order as the INSERT columns.";
        }

        if (select.SelectElements.Count != columnCount)
        {
            return $"INSERT names {columnCount} column(s) but the SELECT returns {select.SelectElements.Count} columns." +
                   NothingExecuted;
        }

        if (select.UniqueRowFilter == UniqueRowFilter.Distinct)
            return Unsupported("SELECT DISTINCT", context);
        if (select.GroupByClause != null || select.HavingClause != null)
            return Unsupported("GROUP BY / HAVING", context);
        if (select.OffsetClause != null || select.ForClause != null)
            return Unsupported("OFFSET or FOR", context);
        if (select.TopRowFilter is { } top)
        {
            if (top.Percent || top.WithTies || (top.Expression is ParenthesisExpression p ? p.Expression : top.Expression) is not IntegerLiteral topValue)
                return Unsupported("TOP other than TOP (number)", context);
            if (!int.TryParse(topValue.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n > MaxInsertSelectTop)
                return Unsupported($"TOP {topValue.Value}", context, $" TOP can be at most {MaxInsertSelectTop} here; insert the rows in several statements.");
            // Without ORDER BY, SQL does not say which rows TOP takes, so the rows Dataverse returns first could differ.
            if (select.OrderByClause == null)
                return Unsupported("TOP without ORDER BY", context, " Add ORDER BY so the statement says which rows TOP takes.");
        }

        if (select.FromClause is not { TableReferences.Count: 1 } || select.FromClause.TableReferences[0] is not NamedTableReference source)
        {
            return "INSERT ... SELECT is supported only from a single table of the connected environment (no joins, " +
                   "derived tables or functions in FROM)." + NothingExecuted;
        }

        var problem = CheckLocalTable(context, source, reading: true);
        if (problem != null)
            return problem;
        // Only a copy within the same table was proven live.
        if (!string.Equals(source.SchemaObject.BaseIdentifier.Value, target.SchemaObject.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase))
        {
            return $"INSERT ... SELECT from another table ({source.SchemaObject.BaseIdentifier.Value} into {target.SchemaObject.BaseIdentifier.Value}) " +
                   "is not supported; only a copy within the same table is." + NothingExecuted +
                   " Select the rows first, then insert them with INSERT ... VALUES.";
        }
        var names = Names(source);

        // T-SQL reads a column by its name even when another item's alias has that name; the FetchXML result would
        // return both under one key, so such an alias is refused.
        var readColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in select.SelectElements.OfType<SelectScalarExpression>())
        {
            var visitor = new ColumnNames();
            element.Expression.Accept(visitor);
            readColumns.UnionWith(visitor.Names);
        }
        foreach (var element in select.SelectElements.OfType<SelectScalarExpression>())
        {
            var alias = element.ColumnName?.Value;
            var isSameColumn = element.Expression is ColumnReferenceExpression self
                && string.Equals(self.MultiPartIdentifier.Identifiers.Last().Value, alias, StringComparison.OrdinalIgnoreCase);
            if (alias != null && !isSameColumn && readColumns.Contains(alias))
            {
                return Unsupported($"the alias {alias}, which is also the name of a column the SELECT reads", context,
                    " Give it an alias that is not a column name of the table.");
            }
        }

        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in select.SelectElements)
        {
            if (element is not SelectScalarExpression scalar)
                return Unsupported(Sql(element), context);

            problem = CheckValue(scalar.Expression, names, context);
            if (problem != null)
                return problem;

            var outputName = scalar.ColumnName?.Value
                ?? (scalar.Expression as ColumnReferenceExpression)?.MultiPartIdentifier.Identifiers.Last().Value;
            if (outputName == null)
            {
                return $"The SELECT item {Sql(scalar.Expression)} needs an alias (AS name): INSERT ... SELECT maps values by " +
                       "position, and an unnamed expression cannot be mapped reliably." + NothingExecuted;
            }

            if (!IsLogicalName(outputName))
                return $"The SELECT alias {outputName} is not a valid name (letters, digits and underscores only)." + NothingExecuted;

            if (!outputNames.Add(outputName))
            {
                return $"Two SELECT items are named {outputName}: give each a distinct alias (AS name) so the values map " +
                       "to the INSERT columns by position." + NothingExecuted;
            }
        }

        // Without TOP, ORDER BY does not change which rows are copied, and paging over a sort on a column that is not
        // unique was not proven to return every row exactly once.
        if (select.OrderByClause != null && select.TopRowFilter == null)
            return Unsupported("ORDER BY without TOP", context, " Remove the ORDER BY, or add TOP.");

        if (select.OrderByClause != null)
        {
            foreach (var order in select.OrderByClause.OrderByElements)
            {
                if (order.Expression is not ColumnReferenceExpression orderColumn || CheckColumn(orderColumn, names, "ORDER BY") != null)
                    return Unsupported($"ORDER BY {Sql(order.Expression)}", context);
                // T-SQL orders by a same-named SELECT alias; Dataverse would order by the table's column.
                var orderName = orderColumn.MultiPartIdentifier.Identifiers.Last().Value;
                var shadowing = select.SelectElements.OfType<SelectScalarExpression>().FirstOrDefault(e =>
                    string.Equals(e.ColumnName?.Value, orderName, StringComparison.OrdinalIgnoreCase)
                    && !(e.Expression is ColumnReferenceExpression c
                         && string.Equals(c.MultiPartIdentifier.Identifiers.Last().Value, orderName, StringComparison.OrdinalIgnoreCase)));
                if (shadowing != null)
                    return Unsupported($"ORDER BY {orderName}, which is also the alias of another SELECT item", context, " Order by a column that is not used as an alias.");
            }
        }

        return CheckWhere(context, select.WhereClause, names);
    }

    // ── tables ──────────────────────────────────────────────────────────

    private static string? CheckTargetAndFrom(string verb, TableReference target, FromClause? from, out HashSet<string> names)
    {
        names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var problem = CheckLocalTable(verb, target);
        if (problem != null)
            return problem;

        var targetTable = (NamedTableReference)target;
        if (from == null)
        {
            names = Names(targetTable);
            return null;
        }

        if (from.TableReferences.Count != 1 || from.TableReferences[0] is not NamedTableReference fromTable)
        {
            return $"{verb} ... FROM is supported only when FROM names the target table alone (for example " +
                   $"{verb} a ... FROM account a WHERE ...). Joins, derived tables, functions and other environments in " +
                   "FROM are not supported." + NothingExecuted + " Put the condition in the WHERE clause instead.";
        }

        problem = CheckLocalTable(verb, fromTable);
        if (problem != null)
            return problem;

        // The target must be the FROM table: its alias when it has one, otherwise its name.
        var targetName = targetTable.SchemaObject.BaseIdentifier.Value;
        var boundName = fromTable.Alias?.Value ?? fromTable.SchemaObject.BaseIdentifier.Value;
        if (targetTable.SchemaObject.SchemaIdentifier != null && fromTable.Alias != null
            || !string.Equals(targetName, boundName, StringComparison.OrdinalIgnoreCase))
        {
            return $"The {verb} target {Sql(targetTable)} is not the table named in FROM ({Sql(fromTable)})." +
                   NothingExecuted + $" Write {verb} {boundName} ... FROM {Sql(fromTable)}.";
        }

        names = Names(fromTable);
        return null;
    }

    /// <summary>A plain table of the connected environment: one-part or dbo-qualified, no hints, sampling or temporal clause.</summary>
    private static string? CheckLocalTable(string verb, TableReference table, bool reading = false)
    {
        if (table is not NamedTableReference named)
            return $"The {verb} {(reading ? "source" : "target")} must be a table name; {Sql(table)} is not supported." + NothingExecuted;

        var schemaObject = named.SchemaObject;
        if (!IsLogicalName(schemaObject.BaseIdentifier.Value) || (named.Alias != null && !IsLogicalName(named.Alias.Value)))
            return $"{Sql(named)} is not a valid Dataverse table name or alias." + NothingExecuted;
        var schema = schemaObject.SchemaIdentifier?.Value;
        if (schemaObject.ServerIdentifier != null || schemaObject.DatabaseIdentifier != null
            || (schema != null && !schema.Equals("dbo", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{Sql(named)} names another environment or schema: {verb} can {(reading ? "read" : "write")} only tables " +
                   "of the connected environment." + NothingExecuted;
        }

        if (named.TableHints.Count > 0 || named.TableSampleClause != null || named.TemporalClause != null)
            return Unsupported($"table hints or clauses on {schemaObject.BaseIdentifier.Value}", verb);

        if (schemaObject.BaseIdentifier.Value.StartsWith("#", StringComparison.Ordinal))
            return Unsupported($"the temporary table {schemaObject.BaseIdentifier.Value}", verb);

        return null;
    }

    private static HashSet<string> Names(NamedTableReference table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { table.SchemaObject.BaseIdentifier.Value };
        if (table.Alias != null)
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { table.Alias.Value };
        return names;
    }

    private static string? CheckColumn(ColumnReferenceExpression column, HashSet<string>? names, string where)
    {
        var ids = column.MultiPartIdentifier?.Identifiers;
        if (column.ColumnType != ColumnType.Regular || ids == null || ids.Count == 0)
            return Unsupported(Sql(column), where);
        if (ids.Any(id => !IsLogicalName(id.Value)))
            return $"{Sql(column)} in {where} is not a valid Dataverse column name." + NothingExecuted;
        if (ids.Count > 2 || (ids.Count == 2 && (names == null || !names.Contains(ids[0].Value))))
            return $"The column {Sql(column)} in {where} does not belong to the statement's table." + NothingExecuted;
        return null;
    }

    // ── WHERE: conditions Dataverse evaluates (FetchXML) ─────────────────

    private static string? CheckWhere(string verb, WhereClause? where, HashSet<string> names)
    {
        if (where == null)
            return null;
        if (where.Cursor != null)
            return Unsupported("WHERE CURRENT OF (cursors)", verb);
        return CheckCondition(where.SearchCondition, names, verb);
    }

    private static string? CheckCondition(BooleanExpression condition, HashSet<string> names, string verb)
    {
        var context = $"the WHERE clause of {verb}";
        var real = RealLiteralIn(condition);
        if (real != null)
            return Unsupported($"{Sql(real)} (a floating-point literal)", context, RealLiteralHowTo);
        switch (condition)
        {
            case InPredicate { Subquery: null } tooLong when tooLong.Values.Count > MaxInValues:
                return Unsupported($"IN with {tooLong.Values.Count} values", context,
                    $" Split the statement into several, each with at most {MaxInValues} values in IN (...).");
            case BooleanBinaryExpression binary:
                return CheckCondition(binary.FirstExpression, names, verb) ?? CheckCondition(binary.SecondExpression, names, verb);
            case BooleanParenthesisExpression paren:
                return CheckCondition(paren.Expression, names, verb);
            case BooleanNotExpression:
                return Unsupported(Sql(condition), context, " Write the opposite condition instead (for example <> instead of NOT =).");
            case BooleanComparisonExpression comparison when IsComparison(comparison.ComparisonType):
                if (comparison.FirstExpression is not ColumnReferenceExpression compared)
                    return Unsupported(Sql(condition), context, " Put the column on the left and a constant on the right.");
                if (comparison.SecondExpression is NullLiteral)
                    return Unsupported($"{Sql(condition)} (comparing with NULL)", context, " Use IS NULL or IS NOT NULL.");
                if (!IsConstant(comparison.SecondExpression))
                    return Unsupported(Sql(condition), context, " Compare the column with a string or number literal.");
                return CheckColumn(compared, names, context);
            case BooleanIsNullExpression { Expression: ColumnReferenceExpression column }:
                return CheckColumn(column, names, context);
            case InPredicate { Subquery: null, Expression: ColumnReferenceExpression column } inPredicate
                when inPredicate.Values.All(IsConstant):
                return CheckColumn(column, names, context);
            case LikePredicate { EscapeExpression: null, FirstExpression: ColumnReferenceExpression column, SecondExpression: StringLiteral pattern }
                when !pattern.Value.Contains('[') && !pattern.Value.Contains(']'):
                return CheckColumn(column, names, context);
            case BooleanTernaryExpression { FirstExpression: ColumnReferenceExpression column } between
                when IsConstant(between.SecondExpression) && IsConstant(between.ThirdExpression):
                return CheckColumn(column, names, context);
            default:
                return Unsupported(Sql(condition), context,
                    " Supported conditions: column = constant (and <>, <, >, <=, >=), IN (constants), LIKE 'pattern', " +
                    "BETWEEN, IS [NOT] NULL, combined with AND, OR and parentheses.");
        }
    }

    /// <summary>A floating-point literal directly in this condition (not inside nested AND/OR, which recurse).</summary>
    private static ScalarExpression? RealLiteralIn(BooleanExpression condition)
    {
        static bool Real(ScalarExpression? e) => e is RealLiteral or UnaryExpression { Expression: RealLiteral };
        IEnumerable<ScalarExpression?> operands = condition switch
        {
            BooleanComparisonExpression c => [c.FirstExpression, c.SecondExpression],
            InPredicate i => i.Values,
            BooleanTernaryExpression b => [b.SecondExpression, b.ThirdExpression],
            _ => []
        };
        return operands.FirstOrDefault(Real);
    }

    private static bool IsComparison(BooleanComparisonType type) => type is BooleanComparisonType.Equals
        or BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation
        or BooleanComparisonType.LessThan or BooleanComparisonType.GreaterThan
        or BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.GreaterThanOrEqualTo;

    // A floating-point literal (2.5E0) was not proven; a decimal literal (2.5) was.
    private static bool IsConstant(ScalarExpression expression) => expression switch
    {
        StringLiteral or IntegerLiteral or NumericLiteral => true,
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral or NumericLiteral } => true,
        _ => false
    };

    /// <summary>The longest IN list the live differential run proved.</summary>
    public const int MaxInValues = 1000;

    /// <summary>The most rows one INSERT ... VALUES may give (SQL Server's own limit).</summary>
    public const int MaxValuesRows = 1000;

    /// <summary>The largest TOP of an INSERT ... SELECT (a single FetchXML page).</summary>
    public const int MaxInsertSelectTop = 5000;

    private const string RealLiteralHowTo = " Write the number as a decimal, for example 2.5 instead of 2.5E0.";

    // ── values: expressions the engine evaluates against the row ───────

    /// <summary>
    /// A value (SET, VALUES, SELECT list) made only of proven node kinds. <paramref name="columns"/> is null where columns
    /// are not allowed (INSERT VALUES).
    /// </summary>
    private static string? CheckValue(ScalarExpression expression, HashSet<string>? columns, string context)
    {
        string? All(IEnumerable<ScalarExpression?> parts)
            => parts.Where(p => p != null).Select(p => CheckValue(p!, columns, context)).FirstOrDefault(m => m != null);

        switch (expression)
        {
            case RealLiteral:
                return Unsupported($"{Sql(expression)} (a floating-point literal)", context, RealLiteralHowTo);
            case StringLiteral or IntegerLiteral or NumericLiteral or NullLiteral:
                return null;
            case ColumnReferenceExpression column:
                return columns == null
                    ? Unsupported($"the column {Sql(column)}", context, " VALUES takes constants; use INSERT ... SELECT to copy columns.")
                    : CheckColumn(column, columns, context);
            case VariableReference or GlobalVariableExpression:
                return Unsupported($"the variable {Sql(expression)}", context, " Use a literal value instead.");
            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary:
                return CheckValue(unary.Expression, columns, context);
            case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } add
                when add.FirstExpression is StringLiteral || add.SecondExpression is StringLiteral:
                return Unsupported($"{Sql(add)} (+ with text)", context, " Use CONCAT(...) to join text.");
            case BinaryExpression binary when binary.BinaryExpressionType is BinaryExpressionType.Add
                or BinaryExpressionType.Subtract or BinaryExpressionType.Multiply or BinaryExpressionType.Divide
                or BinaryExpressionType.Modulo:
                return All([binary.FirstExpression, binary.SecondExpression]);
            case ParenthesisExpression paren:
                return CheckValue(paren.Expression, columns, context);
            case CoalesceExpression coalesce:
                return All(coalesce.Expressions);
            case NullIfExpression nullIf:
                return All([nullIf.FirstExpression, nullIf.SecondExpression]);
            case IIfCall iif:
                return CheckValuePredicate(iif.Predicate, columns, context) ?? All([iif.ThenExpression, iif.ElseExpression]);
            case SearchedCaseExpression searched:
                foreach (var when in searched.WhenClauses)
                {
                    var problem = CheckValuePredicate(when.WhenExpression, columns, context) ?? CheckValue(when.ThenExpression, columns, context);
                    if (problem != null)
                        return problem;
                }
                return searched.ElseExpression == null ? null : CheckValue(searched.ElseExpression, columns, context);
            case SimpleCaseExpression simple:
                return All(new[] { simple.InputExpression, simple.ElseExpression }
                    .Concat(simple.WhenClauses.SelectMany(w => new[] { w.WhenExpression, w.ThenExpression })));
            // Conversions verified live: to nvarchar(n), int, decimal(p, s), and (CAST of a GUID literal) uniqueidentifier;
            // TRY_CAST/TRY_CONVERT to int or decimal(p, s). Date/time conversions are not (time zones), nor CONVERT styles,
            // nor the forms whose length, precision or scale SQL would supply (nvarchar, nvarchar(max), decimal, numeric).
            case CastCall cast when !IsConversionTarget(cast.DataType, allowGuid: true):
                return Unsupported($"{Sql(cast)} (CAST to {Sql(cast.DataType)})", context, ConversionTargets);
            case CastCall cast:
                return CheckValue(cast.Parameter, columns, context);
            case ConvertCall { Style: not null } convert:
                return Unsupported($"{Sql(convert)} (a CONVERT style)", context, " Remove the style: CONVERT(type, value), to nvarchar(n), int or decimal(p, s).");
            case ConvertCall convert when !IsConversionTarget(convert.DataType, allowGuid: false):
                return Unsupported($"{Sql(convert)} (CONVERT to {Sql(convert.DataType)})", context, ConversionTargets);
            case ConvertCall convert:
                return CheckValue(convert.Parameter, columns, context);
            case TryCastCall tryCast when IsNumberTarget(tryCast.DataType):
                return CheckValue(tryCast.Parameter, columns, context);
            case TryConvertCall { Style: null } tryConvert when IsNumberTarget(tryConvert.DataType):
                return CheckValue(tryConvert.Parameter, columns, context);
            case TryCastCall or TryConvertCall:
                return Unsupported(Sql(expression), context, " TRY_CAST and TRY_CONVERT are supported to int or decimal(p, s) (precision at most 28) only.");
            case LeftFunctionCall or RightFunctionCall:
                return Unsupported(Sql(expression), context, " Use SUBSTRING(text, start, length) instead.");
            case FunctionCall function when function.CallTarget == null && function.OverClause == null
                && function.WithinGroupClause == null && function.UniqueRowFilter == UniqueRowFilter.NotSpecified
                && AllowedFunctions.Contains(function.FunctionName.Value):
                return CheckFunctionArguments(function, context) ?? All(function.Parameters);
            case FunctionCall function when function.FunctionName.Value.ToUpperInvariant() is "GETDATE" or "GETUTCDATE" or "SYSDATETIME"
                or "SYSUTCDATETIME" or "SYSDATETIMEOFFSET" or "CURRENT_TIMESTAMP":
                return Unsupported($"the function {function.FunctionName.Value}", context,
                    " The engine would evaluate it again for every row, where SQL evaluates it once per statement. Write the time as a literal with Z, for example '2024-01-15T09:30:00Z'.");
            case FunctionCall function when function.FunctionName.Value.Equals("NEWID", StringComparison.OrdinalIgnoreCase):
                return Unsupported("the function NEWID", context, " Dataverse generates row ids; leave the id column out.");
            case FunctionCall function when function.FunctionName.Value.Equals("REPLACE", StringComparison.OrdinalIgnoreCase):
                return Unsupported("the function REPLACE", context,
                    " Dataverse matches text without regard to accents and the query engine would not, so REPLACE could change different characters than SQL would.");
            case FunctionCall function:
                return Unsupported($"the function {function.FunctionName.Value}", context,
                    $" Supported functions: {string.Join(", ", AllowedFunctions.OrderBy(f => f))}, and COALESCE, NULLIF, IIF, CASE, CAST, CONVERT, TRY_CAST and TRY_CONVERT.");
            case ScalarSubquery:
                return Unsupported("a subquery", context);
            default:
                return Unsupported(Sql(expression), context);
        }
    }

    /// <summary>A condition inside IIF/CASE, evaluated against the row like the values around it.</summary>
    private static string? CheckValuePredicate(BooleanExpression predicate, HashSet<string>? columns, string context)
    {
        switch (predicate)
        {
            case BooleanBinaryExpression binary:
                // AND/OR over number comparisons with NULLs was verified live (unknown takes the ELSE branch, as in SQL).
                return CheckValuePredicate(binary.FirstExpression, columns, context) ?? CheckValuePredicate(binary.SecondExpression, columns, context);
            case BooleanParenthesisExpression paren:
                return CheckValuePredicate(paren.Expression, columns, context);
            case BooleanNotExpression:
                // NOT over an unknown (NULL) comparison is unknown in SQL; the evaluator would make it true.
                return Unsupported(Sql(predicate), context, " Write the condition without NOT.");
            case BooleanComparisonExpression comparison when IsComparison(comparison.ComparisonType):
                return CheckValue(comparison.FirstExpression, columns, context) ?? CheckValue(comparison.SecondExpression, columns, context);
            case BooleanIsNullExpression isNull:
                return CheckValue(isNull.Expression, columns, context);
            default:
                return Unsupported(Sql(predicate), context);
        }
    }

    // ── shared ─────────────────────────────────────────────────────────

    private const string NoColumnList =
        "INSERT without a column list is not supported." + NothingExecuted +
        " Name the columns: INSERT INTO table (column1, column2) ...";

    private static string? CommonChecks(
        DataModificationStatement statement,
        string verb,
        TopRowFilter? top,
        OutputClause? output,
        OutputIntoClause? outputInto)
    {
        if (top != null)
        {
            return "TOP is not supported on UPDATE or DELETE: it would not limit which rows are changed." + NothingExecuted +
                   " Narrow the WHERE clause instead.";
        }

        if (output != null || outputInto != null)
        {
            return $"The OUTPUT clause is not supported on {verb}." + NothingExecuted +
                   " Remove it, and run a SELECT afterwards to see the changed rows.";
        }

        if (statement.WithCtesAndXmlNamespaces != null)
        {
            return $"WITH (a common table expression) before {verb} is not supported." + NothingExecuted +
                   " Put the condition in the statement's WHERE clause instead.";
        }

        // No query hint was proven against Dataverse for DML (and none limits which rows change), so OPTION is refused.
        if (statement.OptimizerHints.Count > 0)
        {
            return Unsupported($"OPTION ({string.Join(", ", statement.OptimizerHints.Select(Sql))})", verb,
                " Remove the OPTION clause.");
        }

        return null;
    }

    /// <summary>A Dataverse logical name: letters, digits and underscores, not starting with a digit.</summary>
    public static bool IsLogicalName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");

    private sealed class ColumnNames : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids)
                Names.Add(ids[ids.Count - 1].Value);
        }
    }

    /// <summary>
    /// Decorations that the node-kind checks above would not notice, and that the engine does not apply: COLLATE on any
    /// expression, ODBC escapes, LIKE ESCAPE, identity and $ACTION-style calls, ODBC literals.
    /// </summary>
    private sealed class DecorationCheck : TSqlFragmentVisitor
    {
        private string? _found;

        public static string? Find(TSqlFragment fragment)
        {
            var check = new DecorationCheck();
            fragment.Accept(check);
            return check._found;
        }

        public override void Visit(PrimaryExpression node)
        {
            if (node.Collation != null)
                _found ??= Unsupported($"COLLATE {node.Collation.Value}", "INSERT, UPDATE or DELETE", " Remove the COLLATE clause.");
        }

        public override void Visit(LikePredicate node)
        {
            if (node.EscapeExpression != null || node.OdbcEscape)
                _found ??= Unsupported("LIKE ... ESCAPE", "INSERT, UPDATE or DELETE");
        }

        public override void Visit(OdbcLiteral node) => _found ??= Unsupported("an ODBC literal ({d ...})", "INSERT, UPDATE or DELETE");

        public override void Visit(IdentityFunctionCall node) => _found ??= Unsupported("IDENTITY()", "INSERT, UPDATE or DELETE");
    }

    private const string ConversionTargets =
        " Supported: nvarchar(n) with a length from 1 to 4000, int, decimal(p, s) with both precision (at most 28) and scale " +
        "(for example decimal(10, 2)), and CAST('<guid>' AS uniqueidentifier).";

    /// <summary>The largest nvarchar(n) length a conversion may name.</summary>
    public const int MaxNVarCharLength = 4000;

    private static int? IntegerParameter(SqlDataTypeReference type, int index)
        => type.Parameters.Count > index && type.Parameters[index] is IntegerLiteral literal
           && int.TryParse(literal.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// The largest decimal precision a conversion may name: the engine computes decimals exactly up to 28 digits
    /// (System.Decimal), and the live differential run proved decimal(28, 0), decimal(28, 28) and the precisions below.
    /// </summary>
    public const int MaxDecimalPrecision = 28;

    /// <summary>The largest number of places ROUND may name (the live run proved 10, the most a decimal column holds).</summary>
    public const int MaxRoundPlaces = 10;

    /// <summary>int, or decimal(p, s) with both given (SQL's defaults would round to a whole number), p at most 28.</summary>
    private static bool IsNumberTarget(DataTypeReference type) => type switch
    {
        SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Int, Parameters.Count: 0 } => true,
        SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Decimal, Parameters.Count: 2 } d
            => IntegerParameter(d, 0) is >= 1 and <= MaxDecimalPrecision and var p && IntegerParameter(d, 1) is { } s && s <= p,
        _ => false
    };

    /// <summary>nvarchar(n) with an explicit length (SQL's default is 30, and max was not proven).</summary>
    private static bool IsTextTarget(DataTypeReference type)
        => type is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.NVarChar, Parameters.Count: 1 } t
           && IntegerParameter(t, 0) is >= 1 and <= MaxNVarCharLength;

    private static bool IsConversionTarget(DataTypeReference type, bool allowGuid)
        => IsNumberTarget(type) || IsTextTarget(type)
           || (allowGuid && type is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.UniqueIdentifier });

    /// <summary>
    /// The argument forms of the allowed functions that were proven: one argument for the text functions and ABS (no TRIM
    /// options), SUBSTRING with a literal start of 1 or more and a literal length of 0 or more, ROUND with a literal number
    /// of places of 0 or more.
    /// </summary>
    private static string? CheckFunctionArguments(FunctionCall function, string context)
    {
        var name = function.FunctionName.Value.ToUpperInvariant();
        var arguments = function.Parameters;
        static int? Literal(ScalarExpression e) => e is IntegerLiteral i
            && int.TryParse(i.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

        if (function.TrimOptions != null)
        {
            return Unsupported($"{Sql(function)} (TRIM with LEADING, TRAILING, BOTH or FROM)", context,
                " Use LTRIM(text), RTRIM(text) or TRIM(text), which remove spaces.");
        }

        switch (name)
        {
            case "LTRIM" or "RTRIM" or "TRIM" when arguments.Count != 1:
                return Unsupported(Sql(function), context,
                    $" {name} takes one argument here (no LEADING, TRAILING, BOTH or FROM, and no characters to remove), for example {name}(name).");
            case "UPPER" or "LOWER" or "LEN" or "ABS" when arguments.Count != 1:
                return Unsupported(Sql(function), context, $" {name} takes one argument here, for example {name}(name).");
            case "ISNULL" when arguments.Count != 2:
                return Unsupported(Sql(function), context, " ISNULL takes two arguments.");
            case "CONCAT" when arguments.Count < 2:
                return Unsupported(Sql(function), context, " CONCAT takes two or more arguments.");
            case "SUBSTRING" when arguments.Count != 3 || Literal(arguments[1]) is not >= 1 || Literal(arguments[2]) is not >= 0:
                return Unsupported(Sql(function), context,
                    " Give SUBSTRING a start of 1 or more and a length of 0 or more, written as numbers, for example SUBSTRING(name, 1, 10).");
            case "ROUND" when arguments.Count != 2 || Literal(arguments[1]) is not (>= 0 and <= MaxRoundPlaces):
                return Unsupported(Sql(function), context,
                    $" Give ROUND a value and a number of decimal places from 0 to {MaxRoundPlaces}, written as a number, for example ROUND(amount, 1).");
            default:
                return null;
        }
    }

    private static string Unsupported(string part, string context, string? howTo = null)
        => $"{part} is not supported in {context}." + NothingExecuted + (howTo ?? string.Empty);

    private static string Sql(TSqlFragment fragment)
    {
        Generator.GenerateScript(fragment, out var sql);
        return sql.Trim();
    }

    private static string Operator(AssignmentKind kind) => kind switch
    {
        AssignmentKind.AddEquals => "+=",
        AssignmentKind.SubtractEquals => "-=",
        AssignmentKind.MultiplyEquals => "*=",
        AssignmentKind.DivideEquals => "/=",
        AssignmentKind.ModEquals => "%=",
        AssignmentKind.BitwiseAndEquals => "&=",
        AssignmentKind.BitwiseOrEquals => "|=",
        AssignmentKind.BitwiseXorEquals => "^=",
        _ => "(compound assignment)"
    };
}
