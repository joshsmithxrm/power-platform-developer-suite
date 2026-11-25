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
/// <item>Written values (UPDATE SET, INSERT VALUES, INSERT ... SELECT list), the interim rule: a string, whole-number or
/// decimal literal, NULL, a minus sign on a whole-number or decimal literal, <c>CAST('&lt;guid&gt;' AS uniqueidentifier)</c>
/// with a GUID spelled as <see cref="IsCanonicalGuidText"/> allows, or (not in VALUES) a column of the statement's table.
/// Everything else (arithmetic, parentheses, functions, CASE, IIF, COALESCE, NULLIF, other conversions, variables,
/// subqueries) is refused until the typed SQL value engine proves it; see <see cref="AdmittedValueNodeKinds"/>.</item>
/// <item>INSERT: an explicit column list without duplicates; up to 1,000 VALUES rows of the same length whose values are
/// written values without columns; or a SELECT from the same table whose list has the same
/// length, where every item is a column or an aliased expression with a unique name that is not a column the SELECT reads
/// (values are mapped by position). <c>TOP n</c> / <c>TOP (n)</c> up to 5,000 only with ORDER BY on columns, and ORDER BY only
/// with TOP.</item>
/// <item>No OPTION (...) clause.</item>
/// </list>
/// </remarks>
internal static class DmlStatementSupport
{
    private const string NothingExecuted = " Nothing was executed.";

    private static readonly Sql160ScriptGenerator Generator = new();

    /// <summary>The refusal message for the first unsupported part of <paramref name="statement"/>, or null.</summary>
    public static string? FindUnsupported(TSqlStatement statement) => statement switch
    {
        // First, and without recursion: every later check walks the condition tree recursively.
        DeleteStatement or UpdateStatement or InsertStatement when FindConditionTooLarge(statement) is { } tooLarge => tooLarge,
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

    /// <summary>
    /// The refusal of <paramref name="statement"/>, with its code, or null when it is admitted: first a written value
    /// Dataverse would store differently (<see cref="QueryErrorCode.DmlValueStoredDifferently"/>), then anything not on
    /// the allowlist (<see cref="QueryErrorCode.DmlUnsupportedConstruct"/>), the order of the spec (sql-values,
    /// "Order of refusals before the first statement").
    /// </summary>
    public static DmlRefusal? FindRefusal(TSqlStatement statement)
    {
        if (statement is InsertStatement or UpdateStatement && FindStoredDifferently(statement) is { } stored)
            return new DmlRefusal(QueryErrorCode.DmlValueStoredDifferently, stored);
        return FindUnsupported(statement) is { } message
            ? new DmlRefusal(QueryErrorCode.DmlUnsupportedConstruct, message)
            : null;
    }

    /// <summary>Throws a <see cref="DmlSafetyRefusalException"/> when <paramref name="statement"/> is not on the allowlist.</summary>
    public static void ThrowIfUnsupported(TSqlStatement statement)
    {
        if (FindRefusal(statement) is { } refusal)
            throw new DmlSafetyRefusalException(refusal.Code, refusal.Message);
    }

    /// <summary>
    /// Throws a <see cref="DmlSafetyRefusalException"/> when any INSERT, UPDATE, DELETE, MERGE, EXECUTE AS or REVERT
    /// inside <paramref name="fragment"/> (a script, block, IF, WHILE or TRY) is not on the allowlist.
    /// </summary>
    public static void ThrowIfAnyUnsupported(TSqlFragment fragment)
    {
        var collector = new DataStatementCollector();
        fragment.Accept(collector);
        foreach (var statement in collector.Statements)
            ThrowIfUnsupported(statement);
    }

    // ── the whole-script rule ───────────────────────────────────────────

    /// <summary>
    /// The whole-script rule (maintainer decision M, "One whole-script rule"): a script that contains a write (INSERT,
    /// UPDATE, DELETE or MERGE, anywhere in it) runs only when every one of its top-level statements is an INSERT,
    /// UPDATE or DELETE on the allowlist. Anything else in such a script is refused, before its first statement runs
    /// and before anything is read: IF and WHILE (their conditions are evaluated by the engine), DECLARE and SET, any
    /// SELECT, SELECT ... INTO, TRY ... CATCH, PRINT, THROW, RETURN, BEGIN ... END, cursors, EXECUTE, temporary tables
    /// and table variables, and every statement kind not on this closed list. One refusal names the first statement
    /// that is not admitted and where it is. A script with no write is not affected.
    /// </summary>
    /// <param name="fragment">The parsed request: a <see cref="TSqlScript"/>, or a single statement.</param>
    public static DmlRefusal? FindScriptRefusal(TSqlFragment fragment)
    {
        var statements = TopLevelStatements(fragment);
        // The size of every write's condition tree is checked before anything walks the script recursively (the write
        // finder below included), so a tree deep enough to exhaust the stack is refused, for every statement of the script.
        for (var i = 0; i < statements.Count; i++)
        {
            if (FindConditionTooLarge(statements[i]) is { } tooLarge)
            {
                var message = statements.Count > 1
                    ? $"Statement {i + 1} (line {statements[i].StartLine}, column {statements[i].StartColumn}): {tooLarge}"
                    : tooLarge;
                return new DmlRefusal(QueryErrorCode.DmlUnsupportedConstruct, message);
            }
        }
        if (!ContainsWrite(fragment))
            return null;

        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            var where = statements.Count > 1
                ? $"Statement {i + 1} (line {statement.StartLine}, column {statement.StartColumn})"
                : $"The statement (line {statement.StartLine}, column {statement.StartColumn})";
            if (!AdmittedScriptStatementKinds.Contains(statement.GetType()))
            {
                // MERGE and impersonation have a refusal of their own, which says more.
                if (statement is MergeStatement or ExecuteAsStatement or RevertStatement && FindRefusal(statement) is { } own)
                    return statements.Count > 1 ? own with { Message = $"{where}: {own.Message}" } : own;
                return new DmlRefusal(QueryErrorCode.DmlUnsupportedConstruct,
                    $"This script changes data, so every statement in it must be an INSERT, UPDATE or DELETE that PPDS runs " +
                    $"as written. {where} is {StatementKind(statement)}, which is not." + NothingExecuted +
                    " No row was read and nothing was written. Run the INSERT, UPDATE and DELETE statements on their own, in a " +
                    "request without any other statement.");
            }

            if (FindRefusal(statement) is { } refusal)
                return statements.Count > 1 ? refusal with { Message = $"{where}: {refusal.Message}" } : refusal;
        }

        return null;
    }

    /// <summary>Throws a <see cref="DmlSafetyRefusalException"/> when <see cref="FindScriptRefusal"/> refuses the script.</summary>
    public static void ThrowIfScriptNotAdmitted(TSqlFragment fragment)
    {
        if (FindScriptRefusal(fragment) is { } refusal)
            throw new DmlSafetyRefusalException(refusal.Code, refusal.Message);
    }

    /// <summary>
    /// The statement kinds a script that writes may contain, matched by exact type. Any other kind, including any the
    /// parser adds later, refuses the script.
    /// </summary>
    public static readonly IReadOnlySet<Type> AdmittedScriptStatementKinds = new HashSet<Type>
    {
        typeof(InsertStatement), typeof(UpdateStatement), typeof(DeleteStatement)
    };

    private static IReadOnlyList<TSqlStatement> TopLevelStatements(TSqlFragment fragment) => fragment switch
    {
        TSqlScript script => script.Batches.SelectMany(b => b.Statements).ToList(),
        TSqlBatch batch => batch.Statements.ToList(),
        TSqlStatement statement => [statement],
        _ => []
    };

    private static bool ContainsWrite(TSqlFragment fragment)
    {
        var finder = new WriteFinder();
        fragment.Accept(finder);
        return finder.Found;
    }

    private sealed class WriteFinder : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(InsertStatement node) => Found = true;

        public override void Visit(UpdateStatement node) => Found = true;

        public override void Visit(DeleteStatement node) => Found = true;

        public override void Visit(MergeStatement node) => Found = true;
    }

    /// <summary>The kind of a statement, as the whole-script refusal names it. It repeats no literal from the statement.</summary>
    private static string StatementKind(TSqlStatement statement) => statement switch
    {
        IfStatement => "an IF statement (its condition would be evaluated by PPDS, not by SQL Server or Dataverse)",
        WhileStatement => "a WHILE loop (its condition would be evaluated by PPDS)",
        DeclareVariableStatement => "DECLARE (a variable)",
        DeclareTableVariableStatement => "DECLARE of a table variable",
        SetVariableStatement => "SET of a variable",
        SelectStatement { Into: not null } => "SELECT ... INTO",
        SelectStatement => "a SELECT",
        TryCatchStatement => "TRY ... CATCH",
        PrintStatement => "PRINT",
        ThrowStatement => "THROW",
        RaiseErrorStatement => "RAISERROR",
        ReturnStatement => "RETURN",
        BeginEndBlockStatement => "a BEGIN ... END block",
        ExecuteStatement => "EXECUTE",
        DeclareCursorStatement or OpenCursorStatement or FetchCursorStatement or CloseCursorStatement
            or DeallocateCursorStatement => "a cursor statement",
        CreateTableStatement => "CREATE TABLE",
        DropTableStatement => "DROP TABLE",
        MergeStatement => "MERGE",
        ExecuteAsStatement or RevertStatement => "EXECUTE AS or REVERT",
        _ => $"a statement of kind {statement.GetType().Name.Replace("Statement", string.Empty, StringComparison.Ordinal)}"
    };

    // ── values Dataverse stores differently ─────────────────────────────

    /// <summary>
    /// Whether text made only of white space, or with white space at either end, is written. False until rows B25 and
    /// B26 of the admission table (sql-values, core requirement 4) come into force; then set it to true.
    /// </summary>
    internal const bool WhiteSpaceEdgedTextIsWritten = PPDS.Dataverse.Query.DmlValueAdmission.WhiteSpaceEdgedTextIsWritten;

    /// <summary>
    /// A written text literal (UPDATE SET, INSERT VALUES, INSERT ... SELECT list) that Dataverse stores, or may store,
    /// as something else: the empty string, which Dataverse stores as NULL; and, until <see cref="WhiteSpaceEdgedTextIsWritten"/>,
    /// text that is only white space or has white space at either end. Null when there is none.
    /// </summary>
    private static string? FindStoredDifferently(TSqlStatement statement)
    {
        foreach (var (value, context) in WrittenValues(statement))
        {
            if (value is not StringLiteral text)
                continue;
            if (text.Value.Length == 0)
            {
                return $"Empty text at {At(text)} is not written in {context}: Dataverse stores empty text as NULL, so the " +
                       "column would not hold what the statement says." + NothingExecuted + " Write NULL instead.";
            }
            if (!WhiteSpaceEdgedTextIsWritten && IsWhiteSpaceEdged(text.Value))
            {
                return $"The text at {At(text)} is only white space, or begins or ends with a white-space character or an invisible one " +
                       $"(U+180E, U+200B, U+FEFF), and is not written in {context} yet: Dataverse has not been shown to store such text unchanged." +
                       NothingExecuted + " Remove the white space at either end (or the invisible character), or write NULL.";
            }
        }
        return null;
    }

    private static IEnumerable<(ScalarExpression Value, string Context)> WrittenValues(TSqlStatement statement)
    {
        switch (statement)
        {
            case UpdateStatement update:
                foreach (var clause in update.UpdateSpecification.SetClauses.OfType<AssignmentSetClause>())
                {
                    if (clause.NewValue != null)
                        yield return (clause.NewValue, "UPDATE ... SET");
                }
                break;
            case InsertStatement { InsertSpecification.InsertSource: ValuesInsertSource values }:
                foreach (var row in values.RowValues)
                {
                    foreach (var value in row.ColumnValues)
                        yield return (value, "INSERT ... VALUES");
                }
                break;
            case InsertStatement { InsertSpecification.InsertSource: SelectInsertSource { Select: QuerySpecification select } }:
                foreach (var element in select.SelectElements.OfType<SelectScalarExpression>())
                    yield return (element.Expression, "INSERT ... SELECT");
                break;
        }
    }

    /// <summary>
    /// True when <paramref name="text"/> begins or ends with a character of the Unicode White_Space property (so also when
    /// it is only white space): U+0009 to U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000 to U+200A, U+2028, U+2029,
    /// U+202F, U+205F, U+3000 (sql-values, core requirement 4).
    /// </summary>
    public static bool IsWhiteSpaceEdged(string text) => PPDS.Dataverse.Query.DmlValueAdmission.IsWhiteSpaceEdged(text);

    private sealed class DataStatementCollector : TSqlFragmentVisitor
    {
        public List<TSqlStatement> Statements { get; } = new();

        public override void Visit(InsertStatement node) => Statements.Add(node);

        public override void Visit(UpdateStatement node) => Statements.Add(node);

        public override void Visit(DeleteStatement node) => Statements.Add(node);

        public override void Visit(MergeStatement node) => Statements.Add(node);

        public override void Visit(ExecuteAsStatement node) => Statements.Add(node);

        public override void Visit(RevertStatement node) => Statements.Add(node);
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
                           "assignments are." + NothingExecuted + $" Write the new value instead: SET {column} = <value>, where the value is a literal or a column.";
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

    /// <summary>A VALUES item: a written value without columns (see <see cref="IsAdmittedValue"/>).</summary>
    private static string? CheckInsertValue(ScalarExpression value) => CheckValue(value, null, "INSERT ... VALUES");

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
                return Unsupported($"The TOP at {At(top)}, more than {MaxInsertSelectTop} rows,", context, $" TOP can be at most {MaxInsertSelectTop} here; insert the rows in several statements.");
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
                return Unsupported($"The SELECT item at {At(element)}", context);

            problem = CheckValue(scalar.Expression, names, context);
            if (problem != null)
                return problem;

            var outputName = scalar.ColumnName?.Value
                ?? (scalar.Expression as ColumnReferenceExpression)?.MultiPartIdentifier.Identifiers.Last().Value;
            if (outputName == null)
            {
                return $"The SELECT item at {At(scalar.Expression)} needs an alias (AS name): INSERT ... SELECT maps values by " +
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
                    return Unsupported($"ORDER BY of the expression at {At(order.Expression)}", context);
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

    /// <summary>
    /// The most levels a write's condition tree may have (AND, OR, NOT and parentheses, the condition itself counted). The
    /// checks of the condition recurse once per level, and the stack was exhausted at about 6,500 levels (a chain of ORs),
    /// so the bound is far below that; a long chain of OR on one column is written as IN instead.
    /// </summary>
    public const int MaxConditionDepth = 200;

    /// <summary>The most conditions a write's WHERE may have (each comparison, IN, LIKE, BETWEEN or IS NULL counts once).</summary>
    public const int MaxConditions = 1000;

    /// <summary>
    /// The refusal of a write whose WHERE is deeper than <see cref="MaxConditionDepth"/> or has more conditions than
    /// <see cref="MaxConditions"/>, or null. It walks the tree with a stack of its own, so it cannot exhaust the thread's.
    /// </summary>
    internal static string? FindConditionTooLarge(TSqlStatement statement)
    {
        var (verb, where) = statement switch
        {
            UpdateStatement update => ("UPDATE", update.UpdateSpecification.WhereClause),
            DeleteStatement delete => ("DELETE", delete.DeleteSpecification.WhereClause),
            InsertStatement { InsertSpecification.InsertSource: SelectInsertSource { Select: QuerySpecification select } } => ("INSERT ... SELECT", select.WhereClause),
            _ => ("", null)
        };
        if (where?.SearchCondition is not { } root)
            return null;

        var pending = new Stack<(BooleanExpression Condition, int Depth)>();
        pending.Push((root, 1));
        var conditions = 0;
        while (pending.Count > 0)
        {
            var (condition, depth) = pending.Pop();
            if (depth > MaxConditionDepth)
            {
                return Unsupported($"A WHERE clause nested more than {MaxConditionDepth} levels deep (AND, OR, NOT and parentheses)", $"the WHERE clause of {verb}",
                    " Compare one column with many values with IN (...), or split the statement into several.");
            }
            switch (condition)
            {
                case BooleanBinaryExpression binary:
                    pending.Push((binary.SecondExpression, depth + 1));
                    pending.Push((binary.FirstExpression, depth + 1));
                    break;
                case BooleanParenthesisExpression paren:
                    pending.Push((paren.Expression, depth + 1));
                    break;
                case BooleanNotExpression not:
                    pending.Push((not.Expression, depth + 1));
                    break;
                default:
                    if (++conditions > MaxConditions)
                    {
                        return Unsupported($"A WHERE clause with more than {MaxConditions} conditions", $"the WHERE clause of {verb}",
                            " Compare one column with many values with IN (...), or split the statement into several.");
                    }
                    break;
            }
        }
        return null;
    }

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
            return Unsupported($"The number at {At(real)} (a floating-point literal)", context, RealLiteralHowTo);
        var number = Operands(condition).OfType<ScalarExpression>().FirstOrDefault(o => !NumberFits(o));
        if (number != null)
            return Unsupported($"The number at {At(number)} ({NumberTooLarge})", context, NumberTooLargeHowTo);
        var text = Operands(condition).OfType<StringLiteral>().FirstOrDefault(s => !s.IsNational && !IsSevenBitAscii(s.Value));
        if (text != null)
            return Unsupported($"The text at {At(text)} (text without the N prefix that holds characters outside 7-bit ASCII)", context, WhereTextHowTo(text));
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
                return Unsupported($"The condition at {At(condition)} (NOT)", context, " Write the opposite condition instead (for example <> instead of NOT =).");
            case BooleanComparisonExpression comparison when IsComparison(comparison.ComparisonType):
                if (comparison.FirstExpression is not ColumnReferenceExpression compared)
                    return Unsupported($"The condition at {At(condition)} ({ConditionPart(condition)})", context, " Put the column on the left and a constant on the right.");
                if (comparison.SecondExpression is NullLiteral)
                    return Unsupported($"The condition at {At(condition)} (comparing with NULL)", context, " Use IS NULL or IS NOT NULL.");
                if (!IsConstant(comparison.SecondExpression))
                    return Unsupported($"The condition at {At(condition)} ({ConditionPart(condition)})", context, " Compare the column with a string or number literal.");
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
                return Unsupported($"The condition at {At(condition)} ({ConditionPart(condition)})", context,
                    " Supported conditions: column = constant (and <>, <, >, <=, >=), IN (constants), LIKE 'pattern', " +
                    "BETWEEN, IS [NOT] NULL, combined with AND, OR and parentheses.");
        }
    }

    /// <summary>A floating-point literal directly in this condition (not inside nested AND/OR, which recurse).</summary>
    private static ScalarExpression? RealLiteralIn(BooleanExpression condition)
    {
        static bool Real(ScalarExpression? e) => e is RealLiteral or UnaryExpression { Expression: RealLiteral };
        return Operands(condition).FirstOrDefault(Real);
    }

    /// <summary>The operands of this condition (not inside nested AND/OR, which recurse): comparison, IN, BETWEEN and LIKE.</summary>
    private static IEnumerable<ScalarExpression?> Operands(BooleanExpression condition) => condition switch
    {
        BooleanComparisonExpression c => [c.FirstExpression, c.SecondExpression],
        InPredicate i => i.Values,
        BooleanTernaryExpression b => [b.SecondExpression, b.ThirdExpression],
        LikePredicate l => [l.SecondExpression],
        _ => []
    };

    /// <summary>
    /// The refusal's advice for text without N in a condition: SQL Server converts such text to the database's code page
    /// before comparing, so characters the code page lacks become question marks and other rows match.
    /// </summary>
    private static string WhereTextHowTo(StringLiteral text) =>
        " Write the same text with the N prefix (N'...'). Without N, SQL Server " +
        "converts the text to the database's code page before comparing, so characters the code page does not have become " +
        "question marks and other rows match; the engine cannot tell which code page that is, so text without N is compared " +
        "only when it is 7-bit ASCII.";

    private const string NumberTooLarge = "a number with more digits than PPDS can hold";

    private const string NumberTooLargeHowTo =
        " PPDS holds at most 28 significant digits (up to 79,228,162,514,264,337,593,543,950,335). Write a number within that range.";

    /// <summary>
    /// False for a whole-number or decimal literal (or a minus sign on one) that System.Decimal cannot hold. SQL Server
    /// reads such a literal as numeric(38); the engine cannot, so it is refused before anything runs instead of failing
    /// with a raw overflow. Any other expression is true (it is checked elsewhere).
    /// </summary>
    public static bool NumberFits(ScalarExpression? expression) => expression switch
    {
        IntegerLiteral or NumericLiteral => decimal.TryParse(((Literal)expression).Value, System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out _),
        UnaryExpression { Expression: IntegerLiteral or NumericLiteral } unary => NumberFits(unary.Expression),
        _ => true
    };

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

    // ── values: the interim rule ────────────────────────────────────────

    /// <summary>
    /// The expression node kinds a written value (UPDATE SET, INSERT VALUES, INSERT ... SELECT list) may be, matched by
    /// exact type: a string, whole-number or decimal literal, NULL, a column, a minus sign on a whole-number or decimal
    /// literal (<see cref="UnaryExpression"/>), and <c>CAST('&lt;guid&gt;' AS uniqueidentifier)</c> (<see cref="CastCall"/>).
    /// Every other node kind, including any the parser adds later, is refused.
    /// </summary>
    /// <remarks>
    /// The interim rule for DML on <c>main</c> (maintainer decisions of 2026-11-19 and 2026-11-24): until the typed SQL
    /// value engine (#1764) proves a construct, a written value is a literal, a column reference or NULL. The query engine
    /// computes other expressions with .NET rules, which the differential harness showed differ from SQL Server.
    /// </remarks>
    public static readonly IReadOnlySet<Type> AdmittedValueNodeKinds = new HashSet<Type>
    {
        typeof(StringLiteral), typeof(IntegerLiteral), typeof(NumericLiteral), typeof(NullLiteral),
        typeof(ColumnReferenceExpression), typeof(UnaryExpression), typeof(CastCall)
    };

    /// <summary>
    /// The GUID spellings a written <c>CAST('...' AS uniqueidentifier)</c> may use: 32 hexadecimal digits in groups of
    /// 8-4-4-4-12 separated by dashes, either case, optionally inside one pair of braces, and nothing else (no spaces).
    /// SQL Server and the engine read these the same; other spellings differ (#1875).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex CanonicalGuid = new(
        @"\A(?:[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}|\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="text"/> is a GUID in a spelling a written value may use (see <see cref="CanonicalGuid"/>).</summary>
    public static bool IsCanonicalGuidText(string text) => CanonicalGuid.IsMatch(text);

    /// <summary>
    /// True when <paramref name="expression"/> is a written value the interim rule admits (before the check that a column
    /// belongs to the statement's table). The node's exact type must be in <see cref="AdmittedValueNodeKinds"/>.
    /// </summary>
    public static bool IsAdmittedValue(ScalarExpression expression)
    {
        if (!AdmittedValueNodeKinds.Contains(expression.GetType()))
            return false;
        return expression switch
        {
            StringLiteral text => text.IsNational || IsSevenBitAscii(text.Value),
            IntegerLiteral or NumericLiteral => NumberFits(expression),
            NullLiteral => true,
            ColumnReferenceExpression column => column.ColumnType == ColumnType.Regular,
            UnaryExpression unary => IsNegativeNumber(unary) && NumberFits(unary),
            CastCall cast => IsGuidCast(cast),
            _ => false
        };
    }

    /// <summary>
    /// True when every character of <paramref name="text"/> is 7-bit ASCII. A text literal without the N prefix that holds
    /// any other character is not written (maintainer decision, 2026-11-24, "Refuse it"): SQL Server converts such a
    /// literal to the database's code page and stores the characters the code page lacks as question marks, and the engine
    /// cannot tell which code page (collation) that is, so only the characters every code page shares are admitted.
    /// </summary>
    public static bool IsSevenBitAscii(string text) => PPDS.Dataverse.Query.DmlValueAdmission.IsSevenBitAscii(text);

    /// <summary>
    /// The literal <paramref name="expression"/> is, for the admission functions (<see cref="PPDS.Dataverse.Query.DmlValueAdmission"/>),
    /// or null when it is not a literal of an admitted form: a text, whole-number or decimal literal, a minus sign written
    /// directly on a number literal, NULL, or <c>CAST('&lt;guid&gt;' AS uniqueidentifier)</c> (its text is checked there).
    /// </summary>
    public static PPDS.Dataverse.Query.DmlLiteral? ToDmlLiteral(ScalarExpression expression) => expression switch
    {
        StringLiteral text when expression.GetType() == typeof(StringLiteral)
            => new(text.IsNational ? PPDS.Dataverse.Query.DmlLiteralKind.NationalText : PPDS.Dataverse.Query.DmlLiteralKind.Text, text.Value),
        IntegerLiteral whole when expression.GetType() == typeof(IntegerLiteral) => new(PPDS.Dataverse.Query.DmlLiteralKind.WholeNumber, whole.Value),
        // ScriptDom parses a whole number beyond Int32 (2147483648) as a NumericLiteral, as SQL Server types it numeric(10, 0):
        // by its text it is a whole number.
        NumericLiteral number when expression.GetType() == typeof(NumericLiteral)
            => new(number.Value.Contains('.') ? PPDS.Dataverse.Query.DmlLiteralKind.DecimalNumber : PPDS.Dataverse.Query.DmlLiteralKind.WholeNumber, number.Value),
        NullLiteral when expression.GetType() == typeof(NullLiteral) => new(PPDS.Dataverse.Query.DmlLiteralKind.Null, ""),
        UnaryExpression unary when expression.GetType() == typeof(UnaryExpression) && IsNegativeNumber(unary)
            => unary.Expression is IntegerLiteral i
                ? new(PPDS.Dataverse.Query.DmlLiteralKind.WholeNumber, i.Value, Negative: true)
                : new(((NumericLiteral)unary.Expression).Value.Contains('.') ? PPDS.Dataverse.Query.DmlLiteralKind.DecimalNumber : PPDS.Dataverse.Query.DmlLiteralKind.WholeNumber,
                    ((NumericLiteral)unary.Expression).Value, Negative: true),
        CastCall cast when expression.GetType() == typeof(CastCall)
                           && cast.DataType is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.UniqueIdentifier, Parameters.Count: 0 }
                           && cast.Parameter?.GetType() == typeof(StringLiteral)
            => new(PPDS.Dataverse.Query.DmlLiteralKind.GuidCast, ((StringLiteral)cast.Parameter).Value),
        _ => null
    };

    /// <summary>A minus sign written directly on a whole-number or decimal literal, such as <c>-5</c> or <c>-2.5</c>.</summary>
    private static bool IsNegativeNumber(UnaryExpression unary)
        => unary.UnaryExpressionType == UnaryExpressionType.Negative
           && unary.Expression?.GetType() is { } operand
           && (operand == typeof(IntegerLiteral) || operand == typeof(NumericLiteral));

    /// <summary><c>CAST('&lt;guid&gt;' AS uniqueidentifier)</c> with a GUID in a spelling of <see cref="CanonicalGuid"/>.</summary>
    private static bool IsGuidCast(CastCall cast)
        => cast.DataType?.GetType() == typeof(SqlDataTypeReference)
           && cast.DataType is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.UniqueIdentifier, Parameters.Count: 0 }
           && cast.Parameter?.GetType() == typeof(StringLiteral)
           && IsCanonicalGuidText(((StringLiteral)cast.Parameter).Value);

    private const string InterimRule =
        " Until the typed SQL value engine proves each kind of expression, INSERT and UPDATE write only literals (text, " +
        "numbers, NULL, CAST('<guid>' AS uniqueidentifier)) and columns.";

    /// <summary>
    /// A written value (SET, VALUES, SELECT list): see <see cref="IsAdmittedValue"/>. <paramref name="columns"/> is null
    /// where columns are not allowed (INSERT VALUES).
    /// </summary>
    private static string? CheckValue(ScalarExpression expression, HashSet<string>? columns, string context)
    {
        if (IsAdmittedValue(expression))
        {
            return expression switch
            {
                ColumnReferenceExpression column when columns == null =>
                    Unsupported($"the column {Sql(column)}", context, " VALUES takes constants; use INSERT ... SELECT to copy columns."),
                ColumnReferenceExpression column => CheckColumn(column, columns!, context),
                _ => null
            };
        }

        var part = expression is VariableReference or GlobalVariableExpression
            ? $"The variable {Sql(expression)} at {At(expression)}"
            : $"The value at {At(expression)} ({Construct(expression)})";
        var howTo = HowTo(expression);
        if (columns == null && expression is not (VariableReference or GlobalVariableExpression or RealLiteral or StringLiteral) && NumberFits(expression))
            howTo = " VALUES takes literal values only (text, numbers, NULL, or CAST('<guid>' AS uniqueidentifier)); write the value you want stored as a literal." +
                    (expression is FunctionCall { FunctionName.Value: var f } && f.Equals("NEWID", StringComparison.OrdinalIgnoreCase) ? howTo : string.Empty);
        return Unsupported(part, context, howTo);
    }

    /// <summary>The kind of construct a refused written value is, named in the refusal.</summary>
    private static string Construct(ScalarExpression expression) => expression switch
    {
        StringLiteral => "text without the N prefix that holds characters outside 7-bit ASCII",
        IntegerLiteral or NumericLiteral or UnaryExpression { Expression: IntegerLiteral or NumericLiteral } when !NumberFits(expression) => NumberTooLarge,
        RealLiteral => "a floating-point literal",
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } => "a minus sign on something other than a number literal",
        UnaryExpression => "a unary operator",
        BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } add
            when add.FirstExpression is StringLiteral || add.SecondExpression is StringLiteral => "+ with text",
        BinaryExpression => "arithmetic",
        ParenthesisExpression => "parentheses",
        VariableReference or GlobalVariableExpression => "a variable",
        ScalarSubquery => "a subquery",
        CoalesceExpression => "COALESCE",
        NullIfExpression => "NULLIF",
        IIfCall => "IIF",
        SearchedCaseExpression or SimpleCaseExpression => "CASE",
        CastCall => "a CAST other than CAST('<guid>' AS uniqueidentifier)",
        ConvertCall => "CONVERT",
        TryCastCall => "TRY_CAST",
        TryConvertCall => "TRY_CONVERT",
        ParseCall or TryParseCall => "PARSE",
        LeftFunctionCall => "the function LEFT",
        RightFunctionCall => "the function RIGHT",
        FunctionCall function => $"the function {function.FunctionName.Value.ToUpperInvariant()}",
        Literal => "a literal of this kind",
        _ => "an expression"
    };

    private static string HowTo(ScalarExpression expression) => expression switch
    {
        StringLiteral =>
            " Write the same text with the N prefix (N'...'). Without N, SQL Server " +
            "stores the characters that the database's code page does not have as question marks; the engine cannot tell " +
            "which code page that is, so text without N is written only when it is 7-bit ASCII.",
        RealLiteral or UnaryExpression { Expression: RealLiteral } => RealLiteralHowTo,
        IntegerLiteral or NumericLiteral or UnaryExpression { Expression: IntegerLiteral or NumericLiteral } when !NumberFits(expression) => NumberTooLargeHowTo,
        CastCall { DataType: SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.UniqueIdentifier }, Parameter: StringLiteral } =>
            " Write the GUID as 8-4-4-4-12 hexadecimal digits with dashes, with or without braces and without spaces, " +
            "for example CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier).",
        ParenthesisExpression or UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } =>
            " Write the value as a literal (a negative number as -5, without parentheses) or a column." + InterimRule,
        VariableReference or GlobalVariableExpression => " Use a literal value instead.",
        FunctionCall function when function.FunctionName.Value.Equals("NEWID", StringComparison.OrdinalIgnoreCase) =>
            " Dataverse generates row ids; leave the id column out.",
        FunctionCall function when function.FunctionName.Value.ToUpperInvariant() is "GETDATE" or "GETUTCDATE" or "SYSDATETIME"
            or "SYSUTCDATETIME" or "SYSDATETIMEOFFSET" or "CURRENT_TIMESTAMP" =>
            " Write the time as a literal with Z, for example '2024-01-15T09:30:00Z'.",
        _ => " Compute the value first (for example with a SELECT) and write the result as a literal." + InterimRule
    };

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
    public static bool IsLogicalName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z");

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

        /// <summary>
        /// A text literal with a lower-case n prefix (<c>n'...'</c>): ScriptDom reads it as N'...', SQL Server does not read it
        /// as text at all (a syntax error, or the column n).
        /// </summary>
        public override void Visit(StringLiteral node)
        {
            if (node.IsNational && node.ScriptTokenStream is { } tokens && node.FirstTokenIndex >= 0 && node.FirstTokenIndex < tokens.Count
                && tokens[node.FirstTokenIndex].Text.StartsWith('n'))
            {
                _found ??= Unsupported($"The text at {At(node)} (a lower-case n prefix, which SQL Server does not read as text)",
                    "INSERT, UPDATE or DELETE", " Write the prefix in upper case: N'...'.");
            }
        }

        public override void Visit(IdentityFunctionCall node) => _found ??= Unsupported("IDENTITY()", "INSERT, UPDATE or DELETE");
    }

    private static string Unsupported(string part, string context, string? howTo = null)
        => $"{part} is not supported in {context}." + NothingExecuted + (howTo ?? string.Empty);

    /// <summary>
    /// The text of a name (a column, a table, a variable), as written. Anything else is described by its position: a refusal
    /// message never repeats a literal from the script (specs/sql-values.md, core requirement 1).
    /// </summary>
    private static string Sql(TSqlFragment fragment)
    {
        switch (fragment)
        {
            case ColumnReferenceExpression or VariableReference or GlobalVariableExpression or SchemaObjectName or Identifier:
                Generator.GenerateScript(fragment, out var sql);
                return sql.Trim();
            case NamedTableReference table:
                Generator.GenerateScript(table.SchemaObject, out var name);
                return table.Alias is null ? name.Trim() : $"{name.Trim()} {table.Alias.Value}";
            default:
                return $"the {Kind(fragment)} at {At(fragment)}";
        }
    }

    /// <summary>A name as written, or anything else by its kind and position (for messages that must not repeat a literal).</summary>
    internal static string Describe(TSqlFragment fragment) => Sql(fragment);

    /// <summary>
    /// What a refused condition contains, named by kind (and a function by its name), never by its text: the first
    /// function, variable, subquery, EXISTS, LIKE set or column-to-column comparison in it.
    /// </summary>
    private static string ConditionPart(BooleanExpression condition)
    {
        var finder = new ConditionPartFinder();
        condition.Accept(finder);
        return finder.Part ?? condition switch
        {
            BooleanComparisonExpression { FirstExpression: ColumnReferenceExpression, SecondExpression: ColumnReferenceExpression } => "a comparison of two columns",
            BooleanComparisonExpression { FirstExpression: not ColumnReferenceExpression } => "a value on the left of the comparison",
            LikePredicate => "a LIKE pattern with [ ] or a pattern that is not a text literal",
            _ => "a condition of a kind not supported"
        };
    }

    private sealed class ConditionPartFinder : TSqlFragmentVisitor
    {
        public string? Part { get; private set; }

        public override void Visit(FunctionCall node) => Part ??= $"the function {node.FunctionName.Value.ToUpperInvariant()}";

        public override void Visit(LeftFunctionCall node) => Part ??= "the function LEFT";

        public override void Visit(RightFunctionCall node) => Part ??= "the function RIGHT";

        public override void Visit(VariableReference node) => Part ??= $"the variable {node.Name}";

        public override void Visit(GlobalVariableExpression node) => Part ??= $"the variable {node.Name}";

        public override void Visit(ScalarSubquery node) => Part ??= "a subquery";

        public override void Visit(ExistsPredicate node) => Part ??= "EXISTS (a subquery)";

        public override void Visit(InPredicate node)
        {
            if (node.Subquery != null)
                Part ??= "IN with a subquery (SELECT)";
        }

        public override void Visit(BinaryExpression node) => Part ??= "arithmetic";

        public override void Visit(CastCall node) => Part ??= "CAST";

        public override void Visit(ConvertCall node) => Part ??= "CONVERT";

        public override void Visit(SearchedCaseExpression node) => Part ??= "CASE";

        public override void Visit(SimpleCaseExpression node) => Part ??= "CASE";
    }

    /// <summary>Where a part of the script is: its line and column.</summary>
    internal static string At(TSqlFragment fragment)
        => fragment.StartLine > 0 ? $"line {fragment.StartLine}, column {fragment.StartColumn}" : "this statement";

    private static string Kind(TSqlFragment fragment) => fragment switch
    {
        StringLiteral => "text",
        Literal => "literal",
        BooleanExpression => "condition",
        SelectElement => "SELECT item",
        TableReference => "table reference",
        _ => "expression"
    };

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

/// <summary>A DML refusal: the code of its class (<see cref="QueryErrorCode"/>) and the message for the user.</summary>
internal sealed record DmlRefusal(string Code, string Message);
