using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Query;
using PPDS.Query.Planning;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Checks every column a DML statement reads or writes against the table's metadata before anything executes: it must
/// be a real, readable attribute of that table (not a display column such as <c>owneridname</c> that the query engine
/// computes, and not a column of another table), its type must have been verified live for the way it is used (specs/
/// query.md "DML allowlist"), each literal must have the form that type needs (a GUID for an id, a whole number for a
/// choice, a date with Z or an offset for a date/time), and a written value must be of the column's kind. Anything else is
/// refused with <c>Query.DmlBlocked</c>, naming the column and what to write instead.
/// </summary>
internal static class DmlColumnValidator
{
    private static readonly HashSet<string> NumericTypes = Set("Integer", "BigInt", "Decimal", "Double", "Money");
    private static readonly HashSet<string> WholeNumberTypes = Set("Integer", "BigInt");
    private static readonly HashSet<string> TextTypes = Set("String", "Memo");
    private static readonly HashSet<string> IdTypes = Set("Uniqueidentifier", "Lookup", "Owner");
    private static readonly HashSet<string> ChoiceTypes = Set("Picklist", "State", "Status");

    // The column types each use was verified with in the live differential run (specs/query.md, "DML allowlist").
    // Anything else is refused: a shape that was not proven against Dataverse does not run.
    private static readonly HashSet<string> WriteTypes = Set("String", "Memo", "Integer", "Decimal", "Money", "Double", "Boolean", "Picklist", "DateTime", "Lookup", "State", "Status");
    private static readonly HashSet<string> ValueReadTypes = Set("String", "Memo", "Integer", "Decimal", "Money", "Double");
    private static readonly HashSet<string> EqualityTypes = Set("String", "Memo", "Integer", "Decimal", "Money", "Double", "Boolean", "Picklist", "State", "Status", "DateTime", "Uniqueidentifier", "Lookup", "Owner");
    private static readonly HashSet<string> OrderedTypes = Set("Integer", "Decimal", "Money", "Double", "DateTime");
    private static readonly HashSet<string> InTypes = Set("String", "Integer", "Decimal", "Money", "Picklist", "State", "Status", "Uniqueidentifier", "Lookup");
    private static readonly HashSet<string> LikeTypes = Set("String", "Memo");
    private static readonly HashSet<string> IsNullTypes = Set("String", "Memo", "Integer", "Decimal", "Money", "Double", "Picklist", "DateTime", "Lookup");
    private static readonly HashSet<string> OrderByTypes = Set("Integer", "String", "DateTime");


    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

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
                    var assignments = spec.SetClauses.OfType<AssignmentSetClause>().ToList();
                    foreach (var set in assignments)
                    {
                        var target = Written(columns, table, set.Column, forCreate: false);
                        ReadValue(columns, table, set.NewValue);
                        WrittenValue(target, table, set.NewValue, columns);
                    }
                    StatusTogether(assignments.Select(a => (Column(columns, table, a.Column), a.NewValue)).ToList(), table);
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
                    var targets = spec.Columns.Select(column => Written(columns, table, column, forCreate: true)).ToList();
                    foreach (var target in targets.Where(t => ChoiceTypes.Contains(t.AttributeType) && t.AttributeType != "Picklist"))
                        throw Refuse($"INSERT cannot set {table}.{target.LogicalName}: a new row always starts active. Insert it, then UPDATE statecode and statuscode together.");

                    if (spec.InsertSource is ValuesInsertSource values)
                    {
                        foreach (var row in values.RowValues)
                        {
                            for (var i = 0; i < row.ColumnValues.Count && i < targets.Count; i++)
                            {
                                ReadValue(columns, table, row.ColumnValues[i]);
                                WrittenValue(targets[i], table, row.ColumnValues[i], columns);
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
                            ReadValue(sourceColumns, sourceTable, select.SelectElements[i]);
                            if (select.SelectElements[i] is SelectScalarExpression item && i < targets.Count)
                                WrittenValue(targets[i], table, item.Expression, sourceColumns);
                        }
                        if (select.WhereClause?.SearchCondition != null)
                            ReadFilter(sourceColumns, sourceTable, select.WhereClause.SearchCondition);
                        if (select.OrderByClause != null)
                        {
                            foreach (var column in ScalarColumnCollector.Collect(select.OrderByClause))
                            {
                                var ordered = Column(sourceColumns, sourceTable, column);
                                Proven(ordered, sourceTable, OrderByTypes, "ORDER BY");
                                if (string.Equals(ordered.AttributeType, "DateTime", StringComparison.OrdinalIgnoreCase) && !IsVerifiedDateBehavior(ordered))
                                    throw Refuse(UnverifiedDate($"{sourceTable}.{ordered.LogicalName}", ordered) + " It cannot be used in ORDER BY.");
                            }
                        }
                    }
                    else
                    {
                        // The allowlist admits no other source; anything else is refused here too rather than written unchecked.
                        throw Refuse("INSERT is supported only with VALUES or a SELECT from a single table.");
                    }
                    break;
                }
            }
        }
    }

    /// <summary>Every INSERT, UPDATE and DELETE anywhere in the request, whatever block, IF, WHILE or TRY contains it.</summary>
    private static IReadOnlyList<TSqlStatement> DataStatements(TSqlStatement statement)
    {
        var collector = new DataStatementCollector();
        statement.Accept(collector);
        return collector.Statements;
    }

    private sealed class DataStatementCollector : TSqlFragmentVisitor
    {
        public List<TSqlStatement> Statements { get; } = new();

        public override void Visit(InsertStatement node) => Statements.Add(node);

        public override void Visit(UpdateStatement node) => Statements.Add(node);

        public override void Visit(DeleteStatement node) => Statements.Add(node);
    }

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
            throw Refuse($"The columns of '{table}' could not be read ({ex.Message}), so the statement cannot be checked.", ErrorCodes.Query.DmlMetadataUnavailable);
        }

        if (attributes.Count == 0)
            throw Refuse($"The table '{table}' was not found, or has no columns.", ErrorCodes.Query.DmlMetadataUnavailable);
        return attributes.GroupBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    // ── written columns ────────────────────────────────────────────────

    private static AttributeMetadataDto Written(Dictionary<string, AttributeMetadataDto> columns, string table, ColumnReferenceExpression column, bool forCreate)
    {
        var attribute = Column(columns, table, column);
        var verb = forCreate ? "INSERT" : "UPDATE";
        if (attribute.IsPrimaryId || string.Equals(attribute.AttributeType, "Uniqueidentifier", StringComparison.OrdinalIgnoreCase))
            throw Refuse($"{verb} cannot set the id column {table}.{attribute.LogicalName}: Dataverse generates row ids.");
        if (string.Equals(attribute.AttributeType, "Owner", StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse($"{verb} cannot set the owner ({table}.{attribute.LogicalName}): assigning a row also moves its sharing and cascades " +
                         "to related rows, which SQL does not show. Assign the rows in Dataverse instead.");
        }
        if (forCreate ? !attribute.IsValidForCreate : !attribute.IsValidForUpdate)
            throw Refuse($"The column {table}.{attribute.LogicalName} cannot be set by {verb}.");
        Proven(attribute, table, WriteTypes, forCreate ? "INSERT" : "UPDATE ... SET");
        if (string.Equals(attribute.AttributeType, "Lookup", StringComparison.OrdinalIgnoreCase) && attribute.Targets is not { Count: 1 })
            throw Refuse($"{table}.{attribute.LogicalName} can point at more than one table, so a row id alone does not say which. It cannot be set by {verb}.");
        if (string.Equals(attribute.AttributeType, "DateTime", StringComparison.OrdinalIgnoreCase) && !IsVerifiedDateBehavior(attribute))
            throw Refuse(UnverifiedDate($"{table}.{attribute.LogicalName}", attribute) + $" It cannot be set by {verb}.");
        return attribute;
    }

    /// <summary>
    /// A written value. A literal (or NULL, or the GUID cast) is decided by the admission function of the column's kind
    /// (<see cref="DmlValueAdmission.AdmitLiteral"/>), the one path for a written literal: it admits only an exact form whose
    /// value is sent without loss. A column of the statement's table must be of a kind the column takes; its values pass the
    /// same per-value rule in the statement's pass, before its first write (<see cref="DmlValueAdmission.CheckSentValue"/>).
    /// </summary>
    private static void WrittenValue(AttributeMetadataDto target, string table, ScalarExpression value, Dictionary<string, AttributeMetadataDto> readColumns)
    {
        var column = $"{table}.{target.LogicalName}";
        if (DmlStatementSupport.ToDmlLiteral(value) is { } literal)
        {
            var admission = DmlValueAdmission.AdmitLiteral(literal, target, DmlValueUse.Write);
            if (!admission.IsAdmitted)
                throw Refuse($"The value at {DmlStatementSupport.At(value)} cannot be written to {column}: {admission.Reason}", admission.Code!);
            return;
        }

        var type = target.AttributeType;
        if (TextTypes.Contains(type))
        {
            if (!IsText(value, readColumns))
            {
                throw Refuse($"{column} is a text column; the value {Sql(value)} is not text. Write text in quotes (for example '5') " +
                             "or a text column.");
            }
            return;
        }
        if (NumericTypes.Contains(type))
        {
            if (!IsNumeric(value, readColumns))
                throw Refuse($"{column} is a number column; the value {Sql(value)} is not a number. Write a number without quotes (for example 5) or a number column.");
            var category = Category(value, readColumns);
            // SQL Server rounds currency converted to a whole number; the engine would truncate it.
            if (WholeNumberTypes.Contains(type) && category == Money)
                throw Refuse(CurrencyToWholeNumber(value, $"writing it to the whole-number column {column}"));
            // A decimal or floating-point value written to a whole number loses its fraction (as a decimal literal would):
            // refused here, as the literal is, rather than value by value in the statement's pass (review round 2).
            if (WholeNumberTypes.Contains(type) && category is Exact or Float)
            {
                throw Refuse($"{column} holds whole numbers; the value {Sql(value)} is a {(category == Float ? "floating-point" : "decimal")} " +
                             "column, whose fraction would be lost. Write it to a decimal column, or write a whole number.");
            }
            // SQL Server keeps 17 significant digits of a floating-point value converted to a decimal or currency; the
            // engine keeps 15, so the stored digits could differ.
            if (type is "Decimal" or "Money" && category == Float)
                throw Refuse(FloatToDecimal(value, $"the {Describe(type)} column {column}"));
            return;
        }

        // Yes/no, choice, lookup and date columns take a literal only (the forms their admission functions admit).
        throw Refuse($"{column} takes a literal value only, not {Sql(value)}. " + type switch
        {
            "Boolean" => "Write 1, 0 or NULL.",
            "Picklist" or "State" or "Status" => "Write the choice's whole-number value (for example 1).",
            "Lookup" => "Write the row id as a quoted GUID with dashes and without spaces, or NULL.",
            "DateTime" => $"Write the value as a quoted literal ({DateHint(target)}), or NULL.",
            _ => "This column cannot be set by SQL."
        });
    }

    /// <summary>Dataverse checks statecode against statuscode, so they are written together (verified live) or not at all.</summary>
    private static void StatusTogether(List<(AttributeMetadataDto Column, ScalarExpression Value)> written, string table)
    {
        var state = written.FirstOrDefault(a => a.Column.AttributeType == "State");
        var status = written.FirstOrDefault(a => a.Column.AttributeType == "Status");
        if ((state.Column is null) != (status.Column is null))
        {
            var set = state.Column is null ? "statuscode" : "statecode";
            var missing = state.Column is null ? "statecode" : "statuscode";
            throw Refuse($"UPDATE {table} sets {set} without {missing}. Set both together, for example SET statecode = 1, statuscode = 2.");
        }
        if (state.Column is null || status.Column is null)
            return;

        // Each status reason belongs to one status; a mismatched pair would be rejected row by row, or not at all.
        // (WrittenValue already refused values that are not choices of the columns, so both parse.)
        if (Unwrap(state.Value) is IntegerLiteral s && Unwrap(status.Value) is IntegerLiteral r
            && int.TryParse(r.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var reason)
            && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var stateValue)
            && status.Column.Options?.FirstOrDefault(o => o.Value == reason) is { State: { } owner }
            && owner != stateValue)
        {
            throw Refuse($"statuscode {r.Value} belongs to statecode {owner}, not {s.Value}.");
        }
    }

    // ── read columns ───────────────────────────────────────────────────

    /// <summary>Refuses a column whose type was not verified live for this use.</summary>
    private static void Proven(AttributeMetadataDto attribute, string table, HashSet<string> types, string use)
    {
        if (!types.Contains(attribute.AttributeType ?? ""))
        {
            throw Refuse($"{table}.{attribute.LogicalName} is a {Describe(attribute.AttributeType)} column, which is not supported in {use} " +
                         $"(supported there: {string.Join(", ", types.Select(Describe).Distinct())} columns).");
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
        var conversions = new ConversionCheck(columns);
        fragment.Accept(conversions);
        if (conversions.Problem != null)
            throw Refuse(conversions.Problem);
        var textArguments = new TextArgumentCheck(columns);
        fragment.Accept(textArguments);
        if (textArguments.Problem != null)
            throw Refuse(textArguments.Problem);
        var typeRules = new TypeRuleCheck(columns);
        fragment.Accept(typeRules);
        if (typeRules.Problem != null)
            throw Refuse(typeRules.Problem);
        Read(columns, table, fragment);
    }

    /// <summary>A WHERE condition: each column's type must have been verified with the operator used on it, and each literal must suit the type.</summary>
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

    // ── literal forms ──────────────────────────────────────────────────

    /// <summary>
    /// The two date column kinds the live run verified: user-local date and time, and date only. A user-local column
    /// shown as a date only (it stores a time in UTC), a time-zone-independent column, and a column whose behaviour the
    /// metadata does not state are not verified.
    /// </summary>
    private static bool IsVerifiedDateBehavior(AttributeMetadataDto attribute)
        => IsDateOnly(attribute)
           || (string.Equals(attribute.DateTimeBehavior, "UserLocal", StringComparison.OrdinalIgnoreCase)
               && string.Equals(attribute.Format, "DateAndTime", StringComparison.OrdinalIgnoreCase));

    private static bool IsDateOnly(AttributeMetadataDto attribute)
        => string.Equals(attribute.DateTimeBehavior, "DateOnly", StringComparison.OrdinalIgnoreCase);

    private static string UnverifiedDate(string column, AttributeMetadataDto attribute)
        => $"{column} is a date/time column of a kind that has not been verified ({attribute.DateTimeBehavior ?? "behaviour unknown"}, " +
           $"{attribute.Format ?? "format unknown"}); only user-local date-and-time and date-only columns are supported.";

    private static string DateHint(AttributeMetadataDto attribute)
        => IsDateOnly(attribute) ? "a date such as '2024-01-15'" : "with Z (UTC) or an offset, for example '2024-01-15T09:30:00Z'";

    /// <summary>
    /// The constant a WHERE condition compares a column with: decided by the admission function of the column's kind
    /// (<see cref="DmlValueAdmission.AdmitLiteral"/> with <see cref="DmlValueUse.Compare"/>), the one path for a compared
    /// literal. An admitted literal is sent in the FetchXML as written; the grammar leaves it no room for interpretation.
    /// </summary>
    private static void CheckFilterLiteral(AttributeMetadataDto attribute, string column, ScalarExpression value)
    {
        if (DmlStatementSupport.ToDmlLiteral(value) is not { } literal)
            throw Refuse($"{column} is compared with {Sql(value)}; compare it with a literal.");
        var admission = DmlValueAdmission.AdmitLiteral(literal, attribute, DmlValueUse.Compare);
        if (!admission.IsAdmitted)
            throw Refuse($"The value at {DmlStatementSupport.At(value)} cannot be compared with {column}: {admission.Reason}", admission.Code!);
    }

    private static bool IsLiteral<T>(ScalarExpression value) where T : Literal => Unwrap(value) switch
    {
        T => true,
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: T } => true,
        _ => false
    };

    private static ScalarExpression Unwrap(ScalarExpression value) => value is ParenthesisExpression paren ? Unwrap(paren.Expression) : value;

    /// <summary>
    /// A written row id: a GUID string literal, or CAST of one to uniqueidentifier, in a spelling SQL Server and the engine
    /// read the same (<see cref="DmlStatementSupport.IsCanonicalGuidText"/>: dashes, optional braces, no spaces).
    /// </summary>
    private static bool IsGuidValue(ScalarExpression value) => Unwrap(value) switch
    {
        StringLiteral s => DmlStatementSupport.IsCanonicalGuidText(s.Value),
        CastCall { DataType: SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.UniqueIdentifier }, Parameter: StringLiteral s } => DmlStatementSupport.IsCanonicalGuidText(s.Value),
        _ => false
    };

    /// <summary>An expression the engine evaluates to a number: number literals, number columns and arithmetic, functions and conversions of them.</summary>
    private static bool IsNumeric(ScalarExpression expression, Dictionary<string, AttributeMetadataDto> columns) => expression switch
    {
        IntegerLiteral or NumericLiteral or RealLiteral or NullLiteral => true,
        UnaryExpression unary => IsNumeric(unary.Expression, columns),
        ParenthesisExpression paren => IsNumeric(paren.Expression, columns),
        BinaryExpression binary => IsNumeric(binary.FirstExpression, columns) && IsNumeric(binary.SecondExpression, columns),
        ColumnReferenceExpression column => columns.TryGetValue(column.MultiPartIdentifier.Identifiers.Last().Value, out var a) && NumericTypes.Contains(a.AttributeType ?? ""),
        CastCall cast => IsNumberType(cast.DataType) && IsNumeric(cast.Parameter, columns),
        ConvertCall convert => IsNumberType(convert.DataType) && IsNumeric(convert.Parameter, columns),
        TryCastCall tryCast => IsNumberType(tryCast.DataType) && IsNumeric(tryCast.Parameter, columns),
        TryConvertCall tryConvert => IsNumberType(tryConvert.DataType) && IsNumeric(tryConvert.Parameter, columns),
        FunctionCall { FunctionName.Value: var name } when name.Equals("LEN", StringComparison.OrdinalIgnoreCase) => true,
        FunctionCall { FunctionName.Value: var name } function when name.Equals("ABS", StringComparison.OrdinalIgnoreCase) || name.Equals("ROUND", StringComparison.OrdinalIgnoreCase)
            => function.Parameters.Count > 0 && IsNumeric(function.Parameters[0], columns),
        FunctionCall { FunctionName.Value: var name } function when name.Equals("ISNULL", StringComparison.OrdinalIgnoreCase)
            => function.Parameters.All(p => IsNumeric(p, columns)),
        CoalesceExpression coalesce => coalesce.Expressions.All(e => IsNumeric(e, columns)),
        NullIfExpression nullIf => IsNumeric(nullIf.FirstExpression, columns),
        IIfCall iif => IsNumeric(iif.ThenExpression, columns) && IsNumeric(iif.ElseExpression, columns),
        SearchedCaseExpression searched => searched.WhenClauses.All(w => IsNumeric(w.ThenExpression, columns)) && (searched.ElseExpression is null || IsNumeric(searched.ElseExpression, columns)),
        SimpleCaseExpression simple => simple.WhenClauses.All(w => IsNumeric(w.ThenExpression, columns)) && (simple.ElseExpression is null || IsNumeric(simple.ElseExpression, columns)),
        _ => false
    };

    /// <summary>An expression the engine evaluates to text: text literals and columns, and text functions of them.</summary>
    private static bool IsText(ScalarExpression expression, Dictionary<string, AttributeMetadataDto> columns) => expression switch
    {
        StringLiteral or NullLiteral => true,
        ParenthesisExpression paren => IsText(paren.Expression, columns),
        ColumnReferenceExpression column => TypeOf(column, columns) is { } type && TextTypes.Contains(type),
        // Their arguments are checked by TextArgumentCheck.
        FunctionCall { FunctionName.Value: var name } when TextFunctions.Contains(name) || name.Equals("CONCAT", StringComparison.OrdinalIgnoreCase) => true,
        FunctionCall { FunctionName.Value: var name } function when name.Equals("ISNULL", StringComparison.OrdinalIgnoreCase)
            => function.Parameters.All(p => IsText(p, columns)),
        CoalesceExpression coalesce => coalesce.Expressions.All(e => IsText(e, columns)),
        NullIfExpression nullIf => IsText(nullIf.FirstExpression, columns),
        IIfCall iif => IsText(iif.ThenExpression, columns) && IsText(iif.ElseExpression, columns),
        SearchedCaseExpression searched => searched.WhenClauses.All(w => IsText(w.ThenExpression, columns))
                                           && (searched.ElseExpression is null || IsText(searched.ElseExpression, columns)),
        SimpleCaseExpression simple => simple.WhenClauses.All(w => IsText(w.ThenExpression, columns))
                                       && (simple.ElseExpression is null || IsText(simple.ElseExpression, columns)),
        CastCall { DataType: SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.NVarChar } } => true,
        ConvertCall { DataType: SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.NVarChar } } => true,
        _ => false
    };

    /// <summary>An expression the engine evaluates to a whole number (its text is the same in SQL and in the engine).</summary>
    private static bool IsWholeNumber(ScalarExpression expression, Dictionary<string, AttributeMetadataDto> columns) => expression switch
    {
        IntegerLiteral => true,
        UnaryExpression unary => IsWholeNumber(unary.Expression, columns),
        ParenthesisExpression paren => IsWholeNumber(paren.Expression, columns),
        BinaryExpression binary => IsWholeNumber(binary.FirstExpression, columns) && IsWholeNumber(binary.SecondExpression, columns),
        ColumnReferenceExpression column => TypeOf(column, columns) is { } type && WholeNumberTypes.Contains(type),
        FunctionCall { FunctionName.Value: var name } when name.Equals("LEN", StringComparison.OrdinalIgnoreCase) => true,
        FunctionCall { FunctionName.Value: var name } function when name.Equals("ABS", StringComparison.OrdinalIgnoreCase)
            => function.Parameters.Count == 1 && IsWholeNumber(function.Parameters[0], columns),
        CastCall { DataType: SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Int } } cast => IsNumeric(cast.Parameter, columns),
        ConvertCall { DataType: SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Int } } convert => IsNumeric(convert.Parameter, columns),
        _ => false
    };

    // ── static types (T-SQL data type precedence: float > decimal > money > bigint > int) ──────

    internal const string Whole = "int";
    internal const string Big = "bigint";
    internal const string Money = "money";
    internal const string Exact = "decimal";
    internal const string Float = "float";
    internal const string Text = "text";
    internal const string Null = "null";
    internal const string GuidValue = "uniqueidentifier";

    private static int Precedence(string? category) => category switch
    {
        Float => 5,
        Exact => 4,
        Money => 3,
        Big => 2,
        Whole => 1,
        _ => 0
    };

    /// <summary>The higher of two categories by T-SQL precedence (NULL takes the other's type).</summary>
    private static string? Higher(string? a, string? b)
    {
        if (a is null or Null)
            return b;
        if (b is null or Null)
            return a;
        if (a == b)
            return a;
        return Precedence(a) == 0 || Precedence(b) == 0 ? null : Precedence(a) >= Precedence(b) ? a : b;
    }

    /// <summary>
    /// The T-SQL type category of a value: int, bigint, money, decimal, float, text, null or uniqueidentifier (null when
    /// it is none of these). A literal takes its T-SQL type (2 is int, 2.5 is decimal, 'x' is text).
    /// </summary>
    internal static string? Category(ScalarExpression expression, Dictionary<string, AttributeMetadataDto> columns)
    {
        switch (expression)
        {
            case IntegerLiteral:
                return Whole;
            case NumericLiteral:
                return Exact;
            case RealLiteral:
                return Float;
            case StringLiteral:
                return Text;
            case NullLiteral:
                return Null;
            case ColumnReferenceExpression column:
                return TypeOf(column, columns) switch
                {
                    "Integer" => Whole,
                    "BigInt" => Big,
                    "Decimal" => Exact,
                    "Money" => Money,
                    "Double" => Float,
                    "String" or "Memo" => Text,
                    _ => null
                };
            case UnaryExpression unary:
                return Category(unary.Expression, columns);
            case ParenthesisExpression paren:
                return Category(paren.Expression, columns);
            case BinaryExpression binary:
                return Higher(Category(binary.FirstExpression, columns), Category(binary.SecondExpression, columns));
            case CastCall cast:
                return TargetCategory(cast.DataType);
            case ConvertCall convert:
                return TargetCategory(convert.DataType);
            case TryCastCall tryCast:
                return TargetCategory(tryCast.DataType);
            case TryConvertCall tryConvert:
                return TargetCategory(tryConvert.DataType);
            case FunctionCall function:
                var name = function.FunctionName.Value.ToUpperInvariant();
                return name switch
                {
                    "LEN" => Whole,
                    "ABS" or "ROUND" when function.Parameters.Count > 0 => Category(function.Parameters[0], columns),
                    // ISNULL takes the type of its first argument (of the second when the first is NULL).
                    "ISNULL" when function.Parameters.Count == 2 => Category(function.Parameters[0], columns) is Null
                        ? Category(function.Parameters[1], columns)
                        : Category(function.Parameters[0], columns),
                    _ when TextFunctions.Contains(name) || name == "CONCAT" => Text,
                    _ => null
                };
            case CoalesceExpression coalesce:
                return coalesce.Expressions.Select(e => Category(e, columns)).Aggregate((string?)Null, Higher);
            case NullIfExpression nullIf:
                return Category(nullIf.FirstExpression, columns);
            case IIfCall iif:
                return Higher(Category(iif.ThenExpression, columns), Category(iif.ElseExpression, columns));
            case SearchedCaseExpression searched:
                return searched.WhenClauses.Select(w => Category(w.ThenExpression, columns))
                    .Append(searched.ElseExpression is null ? Null : Category(searched.ElseExpression, columns))
                    .Aggregate((string?)Null, Higher);
            case SimpleCaseExpression simple:
                return simple.WhenClauses.Select(w => Category(w.ThenExpression, columns))
                    .Append(simple.ElseExpression is null ? Null : Category(simple.ElseExpression, columns))
                    .Aggregate((string?)Null, Higher);
            default:
                return null;
        }
    }

    private static string? TargetCategory(DataTypeReference type) => type is SqlDataTypeReference sql
        ? sql.SqlDataTypeOption switch
        {
            SqlDataTypeOption.Int => Whole,
            SqlDataTypeOption.BigInt => Big,
            SqlDataTypeOption.Decimal or SqlDataTypeOption.Numeric => Exact,
            SqlDataTypeOption.NVarChar => Text,
            SqlDataTypeOption.UniqueIdentifier => GuidValue,
            _ => null
        }
        : null;

    private static string DescribeCategory(string? category) => category switch
    {
        Whole => "whole-number",
        Exact => "decimal",
        Money => "currency",
        Float => "floating-point",
        Text => "text",
        _ => category ?? "unknown"
    };

    private static string CurrencyToWholeNumber(ScalarExpression currency, string where)
        => $"Converting the currency value {Sql(currency)} to a whole number ({where}) is not supported: SQL Server rounds currency " +
           "converted to a whole number, and the query engine would truncate it. Write the whole number as a literal, or write " +
           "the currency value to a currency or decimal column.";

    /// <summary>
    /// Type rules the engine could not honour as T-SQL does, refused before anything runs: currency converted to a whole
    /// number (SQL Server rounds), floating point converted to decimal or currency (digits kept), % on floating point (SQL
    /// Server refuses it) or currency (not proven), ISNULL whose second value would be narrowed to the first's type, and
    /// arithmetic or COALESCE / IIF / CASE mixing decimal, currency and floating point (only a whole number mixes with another
    /// number type: those are the combinations the live differential run proved).
    /// </summary>
    private sealed class TypeRuleCheck(Dictionary<string, AttributeMetadataDto> columns) : TSqlFragmentVisitor
    {
        public string? Problem { get; private set; }

        public override void Visit(CastCall node) => Conversion(node, node.DataType, node.Parameter);

        public override void Visit(ConvertCall node) => Conversion(node, node.DataType, node.Parameter);

        public override void Visit(TryCastCall node) => Conversion(node, node.DataType, node.Parameter);

        public override void Visit(TryConvertCall node) => Conversion(node, node.DataType, node.Parameter);

        private void Conversion(TSqlFragment node, DataTypeReference type, ScalarExpression parameter)
        {
            var from = Category(parameter, columns);
            var to = TargetCategory(type);
            if (to == Whole && from == Money)
                Problem ??= CurrencyToWholeNumber(parameter, Sql(node));
            else if (to == Exact && from == Float)
                Problem ??= FloatToDecimal(parameter, "a decimal in " + Sql(node));
        }

        public override void Visit(BinaryExpression node)
        {
            var left = Category(node.FirstExpression, columns);
            var right = Category(node.SecondExpression, columns);
            if (node.BinaryExpressionType == BinaryExpressionType.Modulo && (left is Float or Money || right is Float or Money))
            {
                Problem ??= $"{Sql(node)}: % on a {(left is Float or Money ? DescribeCategory(left) : DescribeCategory(right))} value is not supported " +
                            "(SQL Server refuses % on floating point, and % on currency was not proven). Use % on whole numbers or decimals.";
                return;
            }
            Mixed(node, "arithmetic", left, right);
        }

        public override void Visit(FunctionCall node)
        {
            if (!node.FunctionName.Value.Equals("ISNULL", StringComparison.OrdinalIgnoreCase) || node.Parameters.Count != 2)
                return;
            var first = Category(node.Parameters[0], columns);
            var second = Category(node.Parameters[1], columns);
            if (first is Null)
            {
                Problem ??= $"{Sql(node)}: ISNULL with NULL as its first value is not supported. Write the second value instead.";
                return;
            }
            // ISNULL gives the type of its first value; a second value of a lower type (a whole number) is converted exactly.
            if (second is Null || second == first || (second == Whole && first is Exact or Money or Float))
                return;
            var rewrite = first == Whole && second is Exact or Float
                ? $" Write this instead: ISNULL({Sql(node.Parameters[0])}, CAST({Sql(node.Parameters[1])} AS int))."
                : " Convert the second value to the type of the first with CAST first.";
            Problem ??= $"{Sql(node)}: ISNULL gives the type of its first value ({DescribeCategory(first)}), so SQL Server converts the " +
                        $"{DescribeCategory(second)} value {Sql(node.Parameters[1])} to it; that conversion is not supported here." + rewrite;
        }

        /// <summary>NULLIF(a, b) gives a, or NULL where a = b: b of a's type, or a whole number, was proven.</summary>
        public override void Visit(NullIfExpression node)
        {
            var first = Category(node.FirstExpression, columns);
            var second = Category(node.SecondExpression, columns);
            if (first is Null || second is Null)
            {
                Problem ??= $"{Sql(node)}: NULLIF with NULL as a value is not supported (NULLIF(x, NULL) is always x). Write the value instead.";
                return;
            }
            if (second != first && !(second == Whole && first is Exact or Money or Float))
            {
                Problem ??= $"{Sql(node)}: NULLIF comparing a {DescribeCategory(first)} and a {DescribeCategory(second)} value is not supported. " +
                            "Compare values of the same type, or with a whole number.";
            }
        }

        public override void Visit(CoalesceExpression node) => Branches(node, "COALESCE", node.Expressions);

        public override void Visit(IIfCall node) => Branches(node, "IIF", [node.ThenExpression, node.ElseExpression]);

        public override void Visit(SearchedCaseExpression node)
            => Branches(node, "CASE", [.. node.WhenClauses.Select(w => w.ThenExpression), .. node.ElseExpression is null ? Array.Empty<ScalarExpression>() : [node.ElseExpression]]);

        public override void Visit(SimpleCaseExpression node)
            => Branches(node, "CASE", [.. node.WhenClauses.Select(w => w.ThenExpression), .. node.ElseExpression is null ? Array.Empty<ScalarExpression>() : [node.ElseExpression]]);

        private void Branches(TSqlFragment node, string name, IEnumerable<ScalarExpression> values)
        {
            var categories = values.Select(v => Category(v, columns)).Where(c => c is not Null).Distinct().ToList();
            for (var i = 0; i < categories.Count; i++)
                for (var j = i + 1; j < categories.Count; j++)
                    Mixed(node, name, categories[i], categories[j]);
        }

        /// <summary>Decimal, currency and floating point combine only with themselves or with whole numbers.</summary>
        private void Mixed(TSqlFragment node, string what, string? a, string? b)
        {
            if (a is null || b is null || a == b || a is Null || b is Null || a == Whole || b == Whole)
                return;
            if (Precedence(a) == 0 || Precedence(b) == 0)
                return;
            Problem ??= $"{Sql(node)}: {what} combining a {DescribeCategory(a)} and a {DescribeCategory(b)} value is not supported " +
                        "(only whole numbers combine with the other number types here). " +
                        (a is Float || b is Float
                            ? "Keep floating-point values apart from decimal and currency values."
                            : "Convert one of them with CAST(... AS decimal(p, s)) first.");
        }

        public override void ExplicitVisit(ScalarSubquery node)
        {
        }
    }

    private static string FloatToDecimal(ScalarExpression value, string where)
        => $"Converting the floating-point value {Sql(value)} to {where} is not supported: SQL Server keeps 17 significant digits " +
           "of a floating-point value converted to a decimal or currency and the query engine keeps 15, so the stored digits could " +
           "differ. Write it to a floating-point or whole-number column, or write the value as a decimal literal.";

    private static string? TypeOf(ColumnReferenceExpression column, Dictionary<string, AttributeMetadataDto> columns)
        => columns.TryGetValue(column.MultiPartIdentifier.Identifiers.Last().Value, out var a) ? a.AttributeType : null;

    private static readonly HashSet<string> TextFunctions = Set("UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "SUBSTRING");

    private static bool IsNumberType(DataTypeReference type) => type is SqlDataTypeReference
    {
        SqlDataTypeOption: SqlDataTypeOption.Int or SqlDataTypeOption.BigInt or SqlDataTypeOption.Decimal or SqlDataTypeOption.Numeric
    };

    /// <summary>A name (a column) as written; anything else by its kind and position, never its text (no literal is repeated).</summary>
    private static string Sql(TSqlFragment fragment) => DmlStatementSupport.Describe(fragment);

    // ── visitors ───────────────────────────────────────────────────────

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
                    Problem ??= $"Arithmetic ({node.BinaryExpressionType}) on a {Describe(type)} value is not supported. Use CONCAT(...) to join text; " +
                                "arithmetic needs number columns or numbers.";
                }
            }
        }

        public override void ExplicitVisit(ScalarSubquery node)
        {
        }
    }

    /// <summary>
    /// Text functions take text; CONCAT and CAST/CONVERT to nvarchar take text or a whole number. The text SQL makes of a
    /// decimal, currency or floating-point value depends on its type and scale, which the engine does not reproduce, and an
    /// implicit conversion of a number given to a text function was not verified.
    /// </summary>
    private sealed class TextArgumentCheck(Dictionary<string, AttributeMetadataDto> columns) : TSqlFragmentVisitor
    {
        public string? Problem { get; private set; }

        public override void Visit(FunctionCall node)
        {
            var name = node.FunctionName.Value;
            if (name.Equals("CONCAT", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var argument in node.Parameters)
                    TextOrWhole(node, argument);
            }
            else if ((TextFunctions.Contains(name) || name.Equals("LEN", StringComparison.OrdinalIgnoreCase)) && node.Parameters.Count > 0
                     && !IsText(node.Parameters[0], columns))
            {
                Problem ??= $"{Sql(node)}: {name.ToUpperInvariant()} takes text, and {Sql(node.Parameters[0])} is not text. " +
                            "Convert a whole number with CAST(... AS nvarchar(n)) first.";
            }
        }

        public override void Visit(CastCall node)
        {
            if (node.DataType is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.NVarChar })
                TextOrWhole(node, node.Parameter);
        }

        public override void Visit(ConvertCall node)
        {
            if (node.DataType is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.NVarChar })
                TextOrWhole(node, node.Parameter);
        }

        private void TextOrWhole(TSqlFragment node, ScalarExpression argument)
        {
            if (!IsText(argument, columns) && !IsWholeNumber(argument, columns))
            {
                Problem ??= $"{Sql(node)}: {Sql(argument)} would be turned into text, which is supported only for text and whole " +
                            "numbers (the text SQL makes of a decimal, currency or floating-point value is not reproduced). Convert it " +
                            "to a whole number first: " + (Category(argument, columns) == Money
                                ? $"write this instead: CAST(CAST(CAST({Sql(argument)} AS decimal(19, 4)) AS int) AS nvarchar(20))."
                                : $"write this instead: CAST(CAST({Sql(argument)} AS int) AS nvarchar(20)).");
            }
        }
    }

    /// <summary>
    /// Conversions to a number must convert a number: the engine parses text more loosely than SQL ('1,000' and '$5' would
    /// become numbers, where SQL raises an error or TRY_CAST returns NULL). A conversion to uniqueidentifier takes a GUID literal.
    /// </summary>
    private sealed class ConversionCheck(Dictionary<string, AttributeMetadataDto> columns) : TSqlFragmentVisitor
    {
        public string? Problem { get; private set; }

        public override void Visit(CastCall node) => Check(node, node.DataType, node.Parameter);

        public override void Visit(ConvertCall node) => Check(node, node.DataType, node.Parameter);

        public override void Visit(TryCastCall node) => Check(node, node.DataType, node.Parameter);

        public override void Visit(TryConvertCall node) => Check(node, node.DataType, node.Parameter);

        private void Check(TSqlFragment node, DataTypeReference type, ScalarExpression parameter)
        {
            if (Problem != null)
                return;
            if (IsNumberType(type) && !IsNumeric(parameter, columns))
                Problem = $"{Sql(node)} converts a value that is not a number. Converting text to a number is not supported (SQL Server and the query engine parse text differently).";
            else if (type is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.UniqueIdentifier } && !IsGuidValue((ScalarExpression)node))
                Problem = $"{Sql(node)}: CAST to uniqueidentifier takes a quoted GUID literal.";
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
            if (Problem == null && operands.Any(o => o is NullLiteral))
            {
                Problem = "Comparing with NULL inside a value (IIF, CASE or NULLIF) is not supported: the comparison is never true " +
                          "(NULLIF(x, NULL) is always x). Use IS NULL in IIF or CASE, or leave the NULLIF out.";
                return;
            }
            if (Problem == null && !operands.All(o => IsNumeric(o, columns)))
            {
                Problem = "Comparing text inside a value (IIF, CASE or NULLIF) is not supported: Dataverse compares text without regard " +
                          "to accents or trailing spaces and the query engine would not. Compare numbers there, or filter with WHERE.";
            }
        }
    }

    private sealed class FilterTypeCheck(Dictionary<string, AttributeMetadataDto> columns, string table) : TSqlFragmentVisitor
    {
        public override void Visit(BooleanComparisonExpression node)
        {
            var equality = node.ComparisonType is BooleanComparisonType.Equals or BooleanComparisonType.NotEqualToBrackets
                or BooleanComparisonType.NotEqualToExclamation;
            Check(node.FirstExpression, equality ? EqualityTypes : OrderedTypes, equality ? "a WHERE comparison" : "a WHERE comparison with < > <= >=", node.SecondExpression);
        }

        public override void Visit(BooleanTernaryExpression node)
            => Check(node.FirstExpression, OrderedTypes, "BETWEEN", node.SecondExpression, node.ThirdExpression);

        public override void Visit(InPredicate node) => Check(node.Expression, InTypes, "IN", [.. node.Values]);

        public override void Visit(LikePredicate node) => Check(node.FirstExpression, LikeTypes, "LIKE", node.SecondExpression);

        public override void Visit(BooleanIsNullExpression node) => Check(node.Expression, IsNullTypes, "IS NULL");

        private void Check(ScalarExpression expression, HashSet<string> types, string use, params ScalarExpression[] literals)
        {
            if (expression is not ColumnReferenceExpression column)
                return;
            var attribute = Column(columns, table, column);
            Proven(attribute, table, types, use);
            // Only the table's own id was proven among unique-identifier columns (stageid, processid, ... were not).
            if (string.Equals(attribute.AttributeType, "Uniqueidentifier", StringComparison.OrdinalIgnoreCase) && !attribute.IsPrimaryId)
            {
                // The id column named from metadata: activity tables (task, email, ...) use activityid, not <table>id.
                var primaryId = columns.Values.FirstOrDefault(a => a.IsPrimaryId)?.LogicalName;
                throw Refuse($"{table}.{attribute.LogicalName} is a unique-identifier column that is not the table's id, which is not " +
                             "supported in WHERE. " + (primaryId is null
                                 ? "Filter on the table's id column or on a lookup instead."
                                 : $"Filter on the id column {primaryId} or on a lookup instead."));
            }
            // An unverified date kind is refused for every use, including IS NULL (which has no literal to check).
            if (string.Equals(attribute.AttributeType, "DateTime", StringComparison.OrdinalIgnoreCase) && !IsVerifiedDateBehavior(attribute))
                throw Refuse(UnverifiedDate($"{table}.{attribute.LogicalName}", attribute));
            foreach (var literal in literals)
                CheckFilterLiteral(attribute, $"{table}.{attribute.LogicalName}", literal);
        }
    }

    /// <summary>
    /// A refusal before anything runs. Every check here is of the unsupported-construct class (no admission row is in
    /// force yet, so a value outside a column's limits is also not admitted) unless it says otherwise; a table whose
    /// columns cannot be read is <see cref="ErrorCodes.Query.DmlMetadataUnavailable"/>.
    /// </summary>
    private static PpdsException Refuse(string message, string code = ErrorCodes.Query.DmlUnsupportedConstruct)
        => new(code, message + " Nothing was executed.");
}
