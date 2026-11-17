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

    // The column types each use was verified with in the live differential run (specs/query.md, "DML allowlist").
    // Anything else is refused: a shape that was not proven against Dataverse does not run.
    private static readonly HashSet<string> WriteTypes = new(StringComparer.OrdinalIgnoreCase) { "String", "Integer", "Decimal", "Boolean" };
    private static readonly HashSet<string> ValueReadTypes = new(StringComparer.OrdinalIgnoreCase) { "String", "Integer", "Decimal" };
    private static readonly HashSet<string> FilterTypes = new(StringComparer.OrdinalIgnoreCase) { "String", "Integer", "Decimal", "Boolean", "Picklist" };
    private static readonly HashSet<string> IsNullTypes = new(StringComparer.OrdinalIgnoreCase) { "String", "Integer", "Decimal", "DateTime" };
    private static readonly HashSet<string> OrderedFilterTypes = new(StringComparer.OrdinalIgnoreCase) { "Integer", "Decimal", "DateTime" };
    private static readonly HashSet<string> InTypes = new(StringComparer.OrdinalIgnoreCase) { "String" };
    private static readonly HashSet<string> LikeTypes = new(StringComparer.OrdinalIgnoreCase) { "String" };
    private static readonly HashSet<string> OrderByTypes = new(StringComparer.OrdinalIgnoreCase) { "Integer" };

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
                        Written(columns, table, set.Column, forCreate: false);
                        ReadValue(columns, table, set.NewValue);
                    }
                    if (spec.WhereClause?.SearchCondition != null)
                        ReadFilter(columns, table, spec.WhereClause.SearchCondition);
                    break;
                }
                case DeleteStatement delete:
                {
                    var spec = delete.DeleteSpecification;
                    var table = TableName(DmlStatementSupport.ResolveTarget(spec.Target, spec.FromClause));
                    var columns = await ColumnsAsync(metadata, table, ct);
                    if (spec.WhereClause?.SearchCondition != null)
                        ReadFilter(columns, table, spec.WhereClause.SearchCondition);
                    break;
                }
                case InsertStatement insert:
                {
                    var spec = insert.InsertSpecification;
                    var table = TableName((NamedTableReference)spec.Target);
                    var columns = await ColumnsAsync(metadata, table, ct);
                    foreach (var column in spec.Columns)
                        Written(columns, table, column, forCreate: true);

                    if (spec.InsertSource is ValuesInsertSource values)
                    {
                        foreach (var value in values.RowValues.SelectMany(row => row.ColumnValues))
                            ReadValue(columns, table, value);
                    }
                    else if (spec.InsertSource is SelectInsertSource { Select: QuerySpecification select }
                             && select.FromClause?.TableReferences.FirstOrDefault() is NamedTableReference source)
                    {
                        var sourceTable = TableName(source);
                        var sourceColumns = await ColumnsAsync(metadata, sourceTable, ct);
                        foreach (var item in select.SelectElements)
                            ReadValue(sourceColumns, sourceTable, item);
                        if (select.WhereClause?.SearchCondition != null)
                            ReadFilter(sourceColumns, sourceTable, select.WhereClause.SearchCondition);
                        if (select.OrderByClause != null)
                        {
                            foreach (var column in ScalarColumnCollector.Collect(select.OrderByClause))
                                Proven(Column(sourceColumns, sourceTable, column), sourceTable, OrderByTypes, "ORDER BY");
                        }
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
        Proven(attribute, table, WriteTypes, forCreate ? "INSERT" : "UPDATE ... SET");
        return attribute;
    }

    /// <summary>Refuses a column whose type was not verified live for this use.</summary>
    private static void Proven(AttributeMetadataDto attribute, string table, HashSet<string> types, string use)
    {
        if (!types.Contains(attribute.AttributeType ?? ""))
        {
            throw Refuse($"{table}.{attribute.LogicalName} is a {Describe(attribute.AttributeType)} column, which is not supported in {use} " +
                         $"(supported there: {string.Join(", ", types.Select(Describe))} columns).");
        }
    }

    private static string Describe(string? type) => type switch
    {
        "String" => "text",
        "Memo" => "multiline text",
        "Integer" => "whole number",
        "BigInt" => "big whole number",
        "Decimal" => "decimal",
        "Double" => "floating point",
        "Money" => "currency",
        "Boolean" => "yes/no",
        "Picklist" => "choice",
        "DateTime" => "date/time",
        "Lookup" or "Customer" or "Owner" => "lookup",
        "Uniqueidentifier" => "unique identifier",
        "State" => "status",
        "Status" => "status reason",
        null or "" => "unknown",
        _ => type.ToLowerInvariant()
    };

    /// <summary>A SET value, VALUES row value or INSERT ... SELECT item.</summary>
    private static void ReadValue(Dictionary<string, AttributeMetadataDto> columns, string table, TSqlFragment fragment)
    {
        foreach (var column in ScalarColumnCollector.Collect(fragment))
            Proven(Column(columns, table, column), table, ValueReadTypes, "a value");
        var comparisons = new ValueComparisonCheck(columns);
        fragment.Accept(comparisons);
        if (comparisons.Problem != null)
            throw Refuse(comparisons.Problem);
        Read(columns, table, fragment);
    }

    /// <summary>A WHERE condition: each column's type must have been verified with the operator used on it.</summary>
    private static void ReadFilter(Dictionary<string, AttributeMetadataDto> columns, string table, BooleanExpression condition)
    {
        var filter = new FilterTypeCheck(columns, table);
        condition.Accept(filter);
        Read(columns, table, condition);
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
    /// A comparison the engine evaluates itself (IIF/CASE conditions, simple CASE, NULLIF) must be between numbers:
    /// Dataverse compares text case- and accent-insensitively and ignores trailing spaces (observed live), the evaluator
    /// compares ordinally ignoring case only, so a text comparison there could disagree with the same SQL in WHERE.
    /// </summary>
    private sealed class ValueComparisonCheck(Dictionary<string, AttributeMetadataDto> columns) : TSqlFragmentVisitor
    {
        public string? Problem { get; private set; }

        public override void Visit(BooleanComparisonExpression node) => Check(node.FirstExpression, node.SecondExpression);

        public override void Visit(NullIfExpression node) => Check(node.FirstExpression, node.SecondExpression);

        public override void Visit(SimpleCaseExpression node)
            => Check([node.InputExpression, .. node.WhenClauses.Select(w => w.WhenExpression)]);

        public override void ExplicitVisit(ScalarSubquery node)
        {
        }

        private void Check(params ScalarExpression[] operands)
        {
            if (Problem == null && !operands.All(IsNumber))
            {
                Problem = "Comparing text inside a value (IIF, CASE or NULLIF) is not supported: Dataverse compares text without regard " +
                          "to accents or trailing spaces and the query engine would not. Compare numbers there, or filter with WHERE.";
            }
        }

        private bool IsNumber(ScalarExpression expression) => expression switch
        {
            IntegerLiteral or NumericLiteral or RealLiteral => true,
            UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary => IsNumber(unary.Expression),
            ParenthesisExpression paren => IsNumber(paren.Expression),
            BinaryExpression binary => IsNumber(binary.FirstExpression) && IsNumber(binary.SecondExpression),
            ColumnReferenceExpression column => columns.TryGetValue(column.MultiPartIdentifier.Identifiers.Last().Value, out var a)
                                                && NumericTypes.Contains(a.AttributeType ?? ""),
            _ => false
        };
    }

    private sealed class FilterTypeCheck(Dictionary<string, AttributeMetadataDto> columns, string table) : TSqlFragmentVisitor
    {
        public override void Visit(BooleanComparisonExpression node)
        {
            var equality = node.ComparisonType is BooleanComparisonType.Equals or BooleanComparisonType.NotEqualToBrackets
                or BooleanComparisonType.NotEqualToExclamation;
            Check(node.FirstExpression, equality ? FilterTypes : OrderedFilterTypes, equality ? "a WHERE comparison" : "a WHERE comparison with < > <= >=");
        }

        public override void Visit(BooleanTernaryExpression node) => Check(node.FirstExpression, OrderedFilterTypes, "BETWEEN");

        public override void Visit(InPredicate node) => Check(node.Expression, InTypes, "IN");

        public override void Visit(LikePredicate node) => Check(node.FirstExpression, LikeTypes, "LIKE");

        public override void Visit(BooleanIsNullExpression node) => Check(node.Expression, IsNullTypes, "IS NULL");

        private void Check(ScalarExpression expression, HashSet<string> types, string use)
        {
            if (expression is ColumnReferenceExpression column)
                Proven(Column(columns, table, column), table, types, use);
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
