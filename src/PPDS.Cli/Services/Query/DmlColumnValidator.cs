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
                        Written(columns, table, set.Column, forCreate: false);
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
                    foreach (var column in spec.Columns)
                        Written(columns, table, column, forCreate: true);

                    if (spec.InsertSource is ValuesInsertSource values)
                    {
                        foreach (var value in values.RowValues.SelectMany(r => r.ColumnValues))
                            Read(columns, table, value);
                    }
                    else if (spec.InsertSource is SelectInsertSource { Select: QuerySpecification select }
                             && select.FromClause?.TableReferences.FirstOrDefault() is NamedTableReference source)
                    {
                        var sourceTable = TableName(source);
                        var sourceColumns = await ColumnsAsync(metadata, sourceTable, ct);
                        foreach (var element in select.SelectElements)
                            Read(sourceColumns, sourceTable, element);
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

    private static void Written(Dictionary<string, AttributeMetadataDto> columns, string table, ColumnReferenceExpression column, bool forCreate)
    {
        var attribute = Column(columns, table, column);
        if (forCreate ? !attribute.IsValidForCreate : !attribute.IsValidForUpdate)
            throw Refuse($"The column {table}.{attribute.LogicalName} cannot be set by {(forCreate ? "INSERT" : "UPDATE")}.");
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

    private static PpdsException Refuse(string message) => new(ErrorCodes.Query.DmlBlocked, message + " Nothing was executed.");
}
