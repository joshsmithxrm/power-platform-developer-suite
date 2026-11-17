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
/// <item>Values (UPDATE SET, INSERT VALUES, INSERT ... SELECT list): literals, the target's (or source's) columns,
/// arithmetic, COALESCE, ISNULL, NULLIF, IIF, CASE, CAST, CONVERT, TRY_CAST, TRY_CONVERT and the functions in
/// <see cref="AllowedFunctions"/>. Columns are not allowed in INSERT VALUES. Variables and subqueries are refused.</item>
/// <item>INSERT: an explicit column list without duplicates; VALUES rows of the same length, or a single-table SELECT of
/// the connected environment whose list has the same length, where every item is a column or an aliased expression with
/// a unique name (values are mapped by position). <c>SELECT TOP (n)</c> and ORDER BY on columns are allowed.</item>
/// </list>
/// </remarks>
internal static class DmlStatementSupport
{
    private const string NothingExecuted = " Nothing was executed.";

    /// <summary>Scalar functions proven (by tests) to evaluate correctly in DML values.</summary>
    public static readonly IReadOnlySet<string> AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "LEN", "CONCAT", "SUBSTRING", "REPLACE", "ABS", "ROUND", "ISNULL"
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
                   " Put TOP in the SELECT instead: INSERT INTO table (columns) SELECT TOP (n) ...";
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
                        problem = CheckValue(value, columns: null, "INSERT ... VALUES");
                        if (problem != null)
                            return problem;
                    }
                }
                return null;
            case SelectInsertSource { Select: QuerySpecification select }:
                return CheckInsertSelect(select, columnCount);
            case SelectInsertSource:
                return "INSERT ... SELECT with UNION, INTERSECT, EXCEPT or a parenthesised query is not supported." +
                       NothingExecuted + " Insert from each SELECT separately.";
            default:
                return "This INSERT source is not supported." + NothingExecuted;
        }
    }

    private static string? CheckInsertSelect(QuerySpecification select, int columnCount)
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
        if (select.TopRowFilter is { } top && (top.Percent || top.WithTies || top.Expression is not IntegerLiteral and not ParenthesisExpression { Expression: IntegerLiteral }))
            return Unsupported("TOP other than TOP (number)", context);

        if (select.FromClause is not { TableReferences.Count: 1 } || select.FromClause.TableReferences[0] is not NamedTableReference source)
        {
            return "INSERT ... SELECT is supported only from a single table of the connected environment (no joins, " +
                   "derived tables or functions in FROM)." + NothingExecuted;
        }

        var problem = CheckLocalTable(context, source, reading: true);
        if (problem != null)
            return problem;
        var names = Names(source);

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
        switch (condition)
        {
            case BooleanBinaryExpression binary:
                return CheckCondition(binary.FirstExpression, names, verb) ?? CheckCondition(binary.SecondExpression, names, verb);
            case BooleanParenthesisExpression paren:
                return CheckCondition(paren.Expression, names, verb);
            case BooleanNotExpression:
                return Unsupported($"NOT ({Sql(condition)})", context, " Write the opposite condition instead (for example <> instead of NOT =).");
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

    private static bool IsComparison(BooleanComparisonType type) => type is BooleanComparisonType.Equals
        or BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation
        or BooleanComparisonType.LessThan or BooleanComparisonType.GreaterThan
        or BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.GreaterThanOrEqualTo;

    private static bool IsConstant(ScalarExpression expression) => expression switch
    {
        StringLiteral or IntegerLiteral or NumericLiteral or RealLiteral => true,
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral or NumericLiteral or RealLiteral } => true,
        _ => false
    };

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
            case StringLiteral or IntegerLiteral or NumericLiteral or RealLiteral or NullLiteral:
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
            case CastCall cast:
                return CheckValue(cast.Parameter, columns, context);
            case ConvertCall { Style: not null } convert:
                return Unsupported($"{Sql(convert)} (a CONVERT style)", context, " Use CONVERT without a style, or CAST.");
            case ConvertCall convert:
                return CheckValue(convert.Parameter, columns, context);
            case TryCastCall tryCast:
                return CheckValue(tryCast.Parameter, columns, context);
            case TryConvertCall { Style: not null } tryConvert:
                return Unsupported($"{Sql(tryConvert)} (a CONVERT style)", context, " Use TRY_CONVERT without a style, or TRY_CAST.");
            case TryConvertCall tryConvert:
                return CheckValue(tryConvert.Parameter, columns, context);
            case FunctionCall function when function.CallTarget == null && function.OverClause == null
                && function.WithinGroupClause == null && function.UniqueRowFilter == UniqueRowFilter.NotSpecified
                && AllowedFunctions.Contains(function.FunctionName.Value):
                return All(function.Parameters);
            case FunctionCall function:
                return Unsupported($"the function {function.FunctionName.Value}", context,
                    $" Supported functions: {string.Join(", ", AllowedFunctions.OrderBy(f => f))}, and COALESCE, NULLIF, IIF, CASE, CAST, CONVERT, TRY_CAST, TRY_CONVERT.");
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
                return CheckValuePredicate(binary.FirstExpression, columns, context) ?? CheckValuePredicate(binary.SecondExpression, columns, context);
            case BooleanParenthesisExpression paren:
                return CheckValuePredicate(paren.Expression, columns, context);
            case BooleanNotExpression:
                // NOT over an unknown (NULL) comparison is unknown in SQL; the evaluator would make it true.
                return Unsupported($"NOT ({Sql(predicate)})", context, " Write the condition without NOT.");
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

        // OPTION (MAXDOP n) only changes parallelism; any other query hint could change behaviour and is refused.
        var hint = statement.OptimizerHints.FirstOrDefault(h => h.HintKind != OptimizerHintKind.MaxDop);
        if (hint != null)
            return Unsupported($"OPTION ({Sql(hint)})", verb);

        return null;
    }

    /// <summary>A Dataverse logical name: letters, digits and underscores, not starting with a digit.</summary>
    public static bool IsLogicalName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");

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
