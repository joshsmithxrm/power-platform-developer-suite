using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Query.Planning;

/// <summary>
/// Clauses that the parser accepts on INSERT, UPDATE, DELETE and MERGE but the engine cannot honour. Each one would
/// otherwise be dropped silently and change which rows (or values) a statement affects, so it is refused instead,
/// before anything executes. Shared by the DML safety guard (so the refusal comes before any confirmation prompt) and
/// the planner (so no caller can execute such a statement).
/// </summary>
internal static class DmlStatementSupport
{
    private const string NothingExecuted = " Nothing was executed.";

    /// <summary>The refusal message for the first unsupported clause of <paramref name="statement"/>, or null.</summary>
    public static string? FindUnsupported(TSqlStatement statement) => statement switch
    {
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

    /// <summary>Throws a <see cref="DmlSafetyRefusalException"/> when <paramref name="statement"/> has an unsupported clause.</summary>
    public static void ThrowIfUnsupported(TSqlStatement statement)
    {
        var message = FindUnsupported(statement);
        if (message != null)
            throw new DmlSafetyRefusalException(QueryErrorCode.DmlBlocked, message);
    }

    private static string? CheckDelete(DeleteStatement delete)
    {
        var spec = delete.DeleteSpecification;
        return CommonChecks(delete, "DELETE", spec.TopRowFilter, spec.OutputClause, spec.OutputIntoClause);
    }

    private static string? CheckUpdate(UpdateStatement update)
    {
        var spec = update.UpdateSpecification;
        var common = CommonChecks(update, "UPDATE", spec.TopRowFilter, spec.OutputClause, spec.OutputIntoClause);
        if (common != null)
            return common;

        foreach (var setClause in spec.SetClauses)
        {
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
                           $"assignments are." + NothingExecuted + $" Write it as SET {column} = {column} ... instead.";
                case AssignmentSetClause:
                    break;
                default:
                    return "UPDATE ... SET supports only 'column = value' assignments." + NothingExecuted;
            }
        }

        return null;
    }

    private static string? CheckInsert(InsertStatement insert)
    {
        var spec = insert.InsertSpecification;
        if (spec.TopRowFilter != null)
        {
            return "INSERT TOP (n) is not supported." + NothingExecuted +
                   " Put TOP in the SELECT instead: INSERT INTO table (columns) SELECT TOP (n) ...";
        }

        var common = CommonChecks(insert, "INSERT", null, spec.OutputClause, spec.OutputIntoClause);
        if (common != null)
            return common;

        var columnCount = spec.Columns?.Count ?? 0;
        switch (spec.InsertSource)
        {
            case null or ValuesInsertSource { IsDefaultValues: true }:
                return "INSERT ... DEFAULT VALUES is not supported." + NothingExecuted + " Name the columns and give their values.";
            case ExecuteInsertSource:
                return "INSERT ... EXEC is not supported." + NothingExecuted;
            case ValuesInsertSource values:
                if (columnCount == 0)
                    return NoColumnList;
                for (var i = 0; i < values.RowValues.Count; i++)
                {
                    var count = values.RowValues[i].ColumnValues.Count;
                    if (count != columnCount)
                    {
                        return $"INSERT names {columnCount} column(s) but row {i + 1} of VALUES has {count} value(s)." +
                               NothingExecuted;
                    }
                }
                return null;
            case SelectInsertSource { Select: QuerySpecification select }:
                if (columnCount == 0)
                    return NoColumnList;
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
                return null;
            case SelectInsertSource:
                return "INSERT ... SELECT with UNION, INTERSECT, EXCEPT or a parenthesised query is not supported." +
                       NothingExecuted + " Insert from each SELECT separately.";
            default:
                return "This INSERT source is not supported." + NothingExecuted;
        }
    }

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
            return $"TOP is not supported on UPDATE or DELETE: it would not limit which rows are changed." + NothingExecuted +
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

        return null;
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
