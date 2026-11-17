using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Query.Planning;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Checks every column a DML statement reads or writes against the table's metadata before anything executes: it must
/// be a real, readable attribute of that table (not a display column such as <c>owneridname</c> that the query engine
/// computes, and not a column of another table), written columns must be valid for create or update, and arithmetic
/// must be between numeric columns or numbers. Anything else is refused with <c>Query.DmlBlocked</c>, naming the column.
/// </summary>
internal static class DmlColumnValidator
{
    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Integer", "BigInt", "Decimal", "Double", "Money"
    };

    public static async Task ValidateAsync(TSqlStatement planned, ICachedMetadataProvider metadata, CancellationToken ct)
    {
        foreach (var statement in DataStatements(planned))
        {
            switch (statement)
            {
                case UpdateStatement update:
                {
                    var spec = update.UpdateSpecification;
                    var table = TableName(DmlStatementSupport.ResolveTarget(spec.Target, spec.FromClause));
                    var columns = await ColumnsAsync(metadata, table, ct);
                    foreach (var set in spec.SetClauses.OfType<AssignmentSetClause>())
                    {
                        WrittenValue(Written(columns, table, set.Column, forCreate: false), table, set.NewValue);
                        Read(columns, table, set.NewValue);
                    }
                    if (spec.WhereClause?.SearchCondition != null)
                        Read(columns, table, spec.WhereClause.SearchCondition);
                    break;
                }
                case DeleteStatement delete:
                {
                    var spec = delete.DeleteSpecification;
                    var table = TableName(DmlStatementSupport.ResolveTarget(spec.Target, spec.FromClause));
                    var columns = await ColumnsAsync(metadata, table, ct);
                    if (spec.WhereClause?.SearchCondition != null)
                        Read(columns, table, spec.WhereClause.SearchCondition);
                    break;
                }
                case InsertStatement insert:
                {
                    var spec = insert.InsertSpecification;
                    var table = TableName((NamedTableReference)spec.Target);
                    var columns = await ColumnsAsync(metadata, table, ct);
                    var targets = spec.Columns.Select(column => Written(columns, table, column, forCreate: true)).ToList();

                    if (spec.InsertSource is ValuesInsertSource values)
                    {
                        foreach (var row in values.RowValues)
                        {
                            for (var i = 0; i < row.ColumnValues.Count && i < targets.Count; i++)
                            {
                                WrittenValue(targets[i], table, row.ColumnValues[i]);
                                Read(columns, table, row.ColumnValues[i]);
                            }
                        }
                    }
                    else if (spec.InsertSource is SelectInsertSource { Select: QuerySpecification select }
                             && select.FromClause?.TableReferences.FirstOrDefault() is NamedTableReference source)
                    {
                        var sourceTable = TableName(source);
                        var sourceColumns = await ColumnsAsync(metadata, sourceTable, ct);
                        for (var i = 0; i < select.SelectElements.Count; i++)
                        {
                            if (select.SelectElements[i] is SelectScalarExpression item && i < targets.Count)
                                WrittenValue(targets[i], table, item.Expression);
                            Read(sourceColumns, sourceTable, select.SelectElements[i]);
                        }
                        if (select.WhereClause?.SearchCondition != null)
                            Read(sourceColumns, sourceTable, select.WhereClause.SearchCondition);
                        if (select.OrderByClause != null)
                            Read(sourceColumns, sourceTable, select.OrderByClause);
                    }
                    break;
                }
            }
        }
    }

    private static IEnumerable<TSqlStatement> DataStatements(TSqlStatement statement) => statement switch
    {
        InsertStatement or UpdateStatement or DeleteStatement => [statement],
        BeginEndBlockStatement block => block.StatementList.Statements.SelectMany(DataStatements),
        IfStatement @if => DataStatements(@if.ThenStatement).Concat(@if.ElseStatement is { } e ? DataStatements(e) : []),
        WhileStatement @while => DataStatements(@while.Statement),
        TryCatchStatement tryCatch => tryCatch.TryStatements.Statements.Concat(tryCatch.CatchStatements.Statements).SelectMany(DataStatements),
        _ => []
    };

    private static string TableName(NamedTableReference table) => table.SchemaObject.BaseIdentifier.Value.ToLowerInvariant();

    private static async Task<Dictionary<string, AttributeMetadataDto>> ColumnsAsync(
        ICachedMetadataProvider metadata, string table, CancellationToken ct)
    {
        IReadOnlyList<AttributeMetadataDto> attributes;
        try
        {
            attributes = await metadata.GetAttributesAsync(table, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Refuse($"The columns of '{table}' could not be read ({ex.Message}), so the statement cannot be checked.");
        }

        if (attributes.Count == 0)
            throw Refuse($"The table '{table}' was not found, or has no columns.");
        return attributes.GroupBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static AttributeMetadataDto Written(Dictionary<string, AttributeMetadataDto> columns, string table, ColumnReferenceExpression column, bool forCreate)
    {
        var attribute = Column(columns, table, column);
        if (forCreate ? !attribute.IsValidForCreate : !attribute.IsValidForUpdate)
            throw Refuse($"The column {table}.{attribute.LogicalName} cannot be set by {(forCreate ? "INSERT" : "UPDATE")}.");
        return attribute;
    }

    /// <summary>
    /// Writing a date/time value is not proven (time zone handling was not verified live), so a date/time column can only
    /// be set to NULL.
    /// </summary>
    private static void WrittenValue(AttributeMetadataDto target, string table, ScalarExpression value)
    {
        if (string.Equals(target.AttributeType, "DateTime", StringComparison.OrdinalIgnoreCase) && value is not NullLiteral)
            throw Refuse($"Setting the date/time column {table}.{target.LogicalName} to a value is not supported (only NULL).");
    }

    private static void Read(Dictionary<string, AttributeMetadataDto> columns, string table, TSqlFragment fragment)
    {
        foreach (var column in ScalarColumnCollector.Collect(fragment))
        {
            var attribute = Column(columns, table, column);
            if (!attribute.IsValidForRead)
                throw Refuse($"The column {table}.{attribute.LogicalName} cannot be read.");
        }

        var arithmetic = new ArithmeticCheck(columns);
        fragment.Accept(arithmetic);
        if (arithmetic.Problem != null)
            throw Refuse(arithmetic.Problem);

        var dates = new DateLiteralCheck(columns);
        fragment.Accept(dates);
        if (dates.Problem != null)
            throw Refuse(dates.Problem);
    }

    private static AttributeMetadataDto Column(Dictionary<string, AttributeMetadataDto> columns, string table, ColumnReferenceExpression column)
    {
        var name = column.MultiPartIdentifier.Identifiers.Last().Value;
        if (!columns.TryGetValue(name, out var attribute))
            throw Refuse($"The table {table} has no column '{name}'.");
        if (attribute.AttributeOf != null || string.Equals(attribute.AttributeType, "Virtual", StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse($"{table}.{name} is a display column computed from {attribute.AttributeOf ?? "another column"}; INSERT, " +
                         "UPDATE and DELETE cannot read it. Use the underlying column.");
        }
        return attribute;
    }

    /// <summary>Arithmetic only between numbers (SQL would convert text to a number; the evaluator would join text).</summary>
    private sealed class ArithmeticCheck(Dictionary<string, AttributeMetadataDto> columns) : TSqlFragmentVisitor
    {
        public string? Problem { get; private set; }

        public override void Visit(BinaryExpression node)
        {
            foreach (var operand in new[] { node.FirstExpression, node.SecondExpression })
            {
                var type = operand switch
                {
                    ColumnReferenceExpression c when columns.TryGetValue(c.MultiPartIdentifier.Identifiers.Last().Value, out var a) => a.AttributeType,
                    StringLiteral => "String",
                    _ => null
                };
                if (type != null && !NumericTypes.Contains(type))
                {
                    Problem ??= $"Arithmetic ({node.BinaryExpressionType}) on a {type} value is not supported. Use CONCAT(...) to join text; " +
                                "arithmetic needs number columns or numbers.";
                }
            }
        }

        public override void ExplicitVisit(ScalarSubquery node)
        {
        }
    }

    /// <summary>
    /// Dataverse reads a date/time literal without an offset in the calling user's time zone (observed live), while SQL has
    /// no time zone, so a comparison with one is refused unless the literal ends with Z or +hh:mm / -hh:mm.
    /// </summary>
    private sealed class DateLiteralCheck(Dictionary<string, AttributeMetadataDto> columns) : TSqlFragmentVisitor
    {
        private static readonly System.Text.RegularExpressions.Regex Offset = new(@"(Z|[+-]\d{2}:\d{2})$");

        public string? Problem { get; private set; }

        public override void Visit(BooleanComparisonExpression node) => Check(node.FirstExpression, node.SecondExpression);

        public override void Visit(BooleanTernaryExpression node)
        {
            Check(node.FirstExpression, node.SecondExpression);
            Check(node.FirstExpression, node.ThirdExpression);
        }

        public override void Visit(InPredicate node)
        {
            foreach (var value in node.Values)
                Check(node.Expression, value);
        }

        private void Check(ScalarExpression left, ScalarExpression right)
        {
            if (left is ColumnReferenceExpression column
                && columns.TryGetValue(column.MultiPartIdentifier.Identifiers.Last().Value, out var attribute)
                && string.Equals(attribute.AttributeType, "DateTime", StringComparison.OrdinalIgnoreCase)
                && right is StringLiteral literal && !Offset.IsMatch(literal.Value.Trim()))
            {
                Problem ??= $"The date/time literal '{literal.Value}' has no time zone; Dataverse would read it in your time zone. " +
                            "Add Z (UTC) or an offset such as +01:00.";
            }
        }
    }

    private static PpdsException Refuse(string message) => new(ErrorCodes.Query.DmlBlocked, message + " Nothing was executed.");
}
