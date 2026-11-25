using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata.Models;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// The <c>--filter</c> of <c>data delete</c> and <c>data update</c>: a SQL condition admitted only when every part of it
/// is one of a closed list of shapes, and every value in it is admitted for its column's type, translated to FetchXML by
/// this class alone (not by the query engine's generator, which leaves out what it cannot express and evaluates it
/// later; these commands have no later). Anything else is refused before any record is read (issue 1705).
/// </summary>
/// <remarks>
/// <para>
/// Two steps. <see cref="Translate"/> needs no connection: it parses the condition and admits its structure and what can
/// be decided from the text alone, so most refusals come before signing in. <see cref="AdmittedDataFilter.Bind"/> takes the
/// table's column metadata, read before any record: every column must be a column of the table, and every value is
/// admitted for that column's type by <see cref="DataFilterOperandAdmission.Admit"/>.
/// </para>
/// <para>
/// Admitted shapes, joined by <c>AND</c> and <c>OR</c> with parentheses: a column compared with a literal (<c>=</c>,
/// <c>&lt;&gt;</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>); <c>[NOT] IN</c> with literals;
/// <c>[NOT] LIKE</c> with a text pattern; <c>IS [NOT] NULL</c>; <c>[NOT] BETWEEN</c> with literals. The column comes first
/// and is a plain column name. The negated forms are each sent together with <c>not-null</c> on the same column, because
/// FetchXML's <c>ne</c>, <c>not-in</c> and <c>not-like</c> also match rows whose column is NULL and SQL's never do.
/// </para>
/// </remarks>
public static class DataFilter
{
    /// <summary>The page size used to read the matching records (Dataverse's largest page).</summary>
    public const int PageSize = 5000;

    /// <summary>
    /// The most values one <c>IN</c> list may hold. The same bound as the SQL write path's <c>IN</c>; a list this long is
    /// already far past what a person checks by eye before a delete.
    /// </summary>
    public const int MaxInListValues = 1000;

    /// <summary>
    /// The most literal values the whole filter may hold. SQL Server accepts at most 2,100 parameters in one request, and
    /// Dataverse is taken to send each FetchXML value as one; 2,000 stays below that with room to spare.
    /// </summary>
    public const int MaxValuesInFilter = 2000;

    private static readonly Regex LogicalName = new(@"\A[a-z_][a-z0-9_]*\z", RegexOptions.CultureInvariant);

    internal const string Instead =
        " A filter may only compare a column with a literal (=, <>, <, <=, >, >=), or use IN, LIKE, IS NULL or " +
        "BETWEEN with literals, joined by AND and OR. To act on records chosen another way, select their ids with " +
        "'query sql' and pass them with --file.";

    /// <summary>
    /// Admits the structure of <paramref name="filter"/> and the parts of its values that the text alone decides, or
    /// throws before anything is read. No connection is needed.
    /// </summary>
    /// <param name="entity">The table's logical name (lower-cased here).</param>
    /// <param name="filter">The SQL condition, without <c>WHERE</c>.</param>
    /// <returns>The admitted filter, to be bound to the table's columns with <see cref="AdmittedDataFilter.Bind"/>.</returns>
    /// <exception cref="PpdsException">
    /// <see cref="ErrorCodes.Validation.FilterNotSupported"/> for a part the list does not admit;
    /// <see cref="ErrorCodes.Validation.RequiredField"/> for an empty filter;
    /// <see cref="ErrorCodes.Validation.InvalidValue"/> for an entity name that is not a logical name;
    /// <see cref="ErrorCodes.Query.ParseError"/> for text that is not a SQL condition.
    /// </exception>
    public static AdmittedDataFilter Translate(string entity, string? filter)
    {
        var entityName = (entity ?? "").Trim().ToLowerInvariant();
        if (!LogicalName.IsMatch(entityName))
        {
            throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                "The entity name must be a logical name (lower-case letters, digits and underscores).");
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            // An empty filter never means "every record" here: that is what 'data truncate' is for.
            throw new PpdsException(ErrorCodes.Validation.RequiredField,
                "The filter is empty. Write the condition the records must meet, for example --filter \"numberofemployees = 0\". " +
                "To delete every record of a table, use 'data truncate'.");
        }

        var condition = Parse(filter);
        var values = 0;
        Admit(condition, ref values);
        if (values > MaxValuesInFilter)
        {
            throw Refuse(condition, $"a filter with more than {MaxValuesInFilter.ToString(CultureInfo.InvariantCulture)} values",
                "Split the work into several commands, or select the ids with 'query sql' and pass them with --file.");
        }

        return new AdmittedDataFilter(entityName, condition, ConditionCounter.Count(condition));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Parsing

    private static BooleanExpression Parse(string filter)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(filter);
        var condition = parser.ParseBooleanExpression(reader, out var errors);
        if (errors.Count > 0 || condition is null)
        {
            var first = errors.FirstOrDefault();
            var where = first is null ? "" : $" at line {first.Line}, column {first.Column}";
            throw new PpdsException(ErrorCodes.Query.ParseError,
                $"The filter is not a SQL condition{where}. Write a condition such as \"name = N'Contoso' AND numberofemployees > 10\".");
        }

        return condition;
    }

    // ------------------------------------------------------------------------------------------------------------
    // The allow-list over the condition tree: every node must be one of these shapes; anything else is refused.

    private static void Admit(BooleanExpression condition, ref int values)
    {
        switch (condition)
        {
            case BooleanBinaryExpression binary
                when binary.BinaryExpressionType is BooleanBinaryExpressionType.And or BooleanBinaryExpressionType.Or:
                Admit(binary.FirstExpression, ref values);
                Admit(binary.SecondExpression, ref values);
                return;

            case BooleanParenthesisExpression parenthesis:
                Admit(parenthesis.Expression, ref values);
                return;

            case BooleanComparisonExpression comparison:
                AdmitComparison(comparison);
                values++;
                return;

            case InPredicate inPredicate:
                AdmitIn(inPredicate);
                values += inPredicate.Values.Count;
                return;

            case LikePredicate like:
                AdmitLike(like);
                values++;
                return;

            case BooleanIsNullExpression isNull:
                AdmitColumn(isNull.Expression);
                return;

            case BooleanTernaryExpression between
                when between.TernaryExpressionType is BooleanTernaryExpressionType.Between
                    or BooleanTernaryExpressionType.NotBetween:
                AdmitColumn(between.FirstExpression);
                AdmitLiteral(between.SecondExpression);
                AdmitLiteral(between.ThirdExpression);
                values += 2;
                return;

            case BooleanNotExpression:
                throw Refuse(condition, "NOT (...)",
                    "Write the condition without NOT: use <>, NOT IN, NOT LIKE, NOT BETWEEN or IS NOT NULL.");

            case ExistsPredicate:
                throw Refuse(condition, "a subquery (EXISTS)", "Compare a column of the table with a literal.");

            default:
                throw Refuse(condition, "a condition of a kind not supported here", "");
        }
    }

    private static void AdmitComparison(BooleanComparisonExpression comparison)
    {
        if (comparison.ComparisonType is not (BooleanComparisonType.Equals or BooleanComparisonType.NotEqualToBrackets
            or BooleanComparisonType.NotEqualToExclamation or BooleanComparisonType.LessThan
            or BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.GreaterThan
            or BooleanComparisonType.GreaterThanOrEqualTo))
        {
            throw Refuse(comparison, "the comparison operator", "Use =, <>, !=, <, <=, > or >=.");
        }

        if (comparison.FirstExpression is NullLiteral || comparison.SecondExpression is NullLiteral)
        {
            throw Refuse(comparison, "a comparison with NULL",
                "In SQL it matches no record. Use IS NULL or IS NOT NULL.");
        }

        var leftIsColumn = comparison.FirstExpression is ColumnReferenceExpression;
        var rightIsColumn = comparison.SecondExpression is ColumnReferenceExpression;
        if (leftIsColumn && rightIsColumn)
        {
            throw Refuse(comparison, "a column compared with a column",
                "FetchXML compares a column only with a value. Compare the column with a literal.");
        }

        if (!leftIsColumn && IsLiteral(comparison.FirstExpression) && (rightIsColumn || IsLiteral(comparison.SecondExpression)))
        {
            throw Refuse(comparison, rightIsColumn ? "a literal before the column" : "a comparison without a column",
                rightIsColumn ? "Write the column first, for example revenue > 5." : "Compare a column with a literal.");
        }

        AdmitColumn(comparison.FirstExpression);
        AdmitLiteral(comparison.SecondExpression);
    }

    private static void AdmitIn(InPredicate inPredicate)
    {
        if (inPredicate.Subquery is not null)
            throw Refuse(inPredicate, "a subquery", "List the values themselves: IN (1, 2, 3).");

        AdmitColumn(inPredicate.Expression);
        if (inPredicate.Values.Count == 0)
            throw Refuse(inPredicate, "an empty IN list", "List at least one value.");
        if (inPredicate.Values.Count > MaxInListValues)
        {
            throw Refuse(inPredicate, $"an IN list of more than {MaxInListValues.ToString(CultureInfo.InvariantCulture)} values",
                "Split the work into several commands, or select the ids with 'query sql' and pass them with --file.");
        }

        foreach (var value in inPredicate.Values)
        {
            if (value is NullLiteral)
            {
                throw Refuse(value, "NULL in an IN list",
                    "In SQL it never matches. Leave NULL out of the list, and add OR <column> IS NULL if you mean the " +
                    "records with no value.");
            }

            AdmitLiteral(value);
        }
    }

    private static void AdmitLike(LikePredicate like)
    {
        if (like.EscapeExpression is not null || like.OdbcEscape)
            throw Refuse(like, "LIKE with ESCAPE", "Write the pattern without ESCAPE.");

        AdmitColumn(like.FirstExpression);
        if (like.SecondExpression is not StringLiteral pattern || pattern.Collation is not null)
        {
            throw Refuse(like.SecondExpression, "a LIKE pattern that is not quoted text",
                "Write the pattern as quoted text, for example LIKE N'Contoso%'.");
        }

        if (pattern.Value.IndexOfAny(['[', ']']) >= 0)
        {
            throw Refuse(pattern, "a LIKE pattern with [ or ]",
                "Only % and _ are supported as wildcards here; Dataverse has not been shown to read [ ] as SQL does.");
        }

        AdmitText(pattern);
    }

    private static void AdmitColumn(ScalarExpression expression)
    {
        switch (expression)
        {
            case ColumnReferenceExpression { ColumnType: ColumnType.Regular } column
                when column.Collation is null && column.MultiPartIdentifier?.Identifiers.Count == 1:
                if (!LogicalName.IsMatch(column.MultiPartIdentifier.Identifiers[0].Value.ToLowerInvariant()))
                    throw Refuse(expression, "a column name that is not a logical name", "Use the column's logical name.");
                return;

            case ColumnReferenceExpression { ColumnType: ColumnType.Regular } column when column.Collation is not null:
                throw Refuse(expression, "COLLATE", "Remove COLLATE; Dataverse compares with its own collation.");

            case ColumnReferenceExpression { ColumnType: ColumnType.Regular }:
                throw Refuse(expression, "a column with a table or alias prefix", "Write the column name alone.");

            default:
                throw RefuseValue(expression, whereAColumnIsExpected: true);
        }
    }

    private static void AdmitLiteral(ScalarExpression expression)
    {
        switch (expression)
        {
            case IntegerLiteral or NumericLiteral when ((Literal)expression).Collation is null:
                return;

            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral or NumericLiteral }:
                return;

            case StringLiteral text when text.Collation is null:
                AdmitText(text);
                return;

            case StringLiteral:
                throw Refuse(expression, "COLLATE", "Remove COLLATE; Dataverse compares with its own collation.");

            case NullLiteral:
                throw Refuse(expression, "a comparison with NULL", "In SQL it matches no record. Use IS NULL or IS NOT NULL.");

            case ColumnReferenceExpression { ColumnType: ColumnType.Regular }:
                throw Refuse(expression, "a column compared with a column",
                    "FetchXML compares a column only with a value. Compare the column with a literal.");

            default:
                throw RefuseValue(expression, whereAColumnIsExpected: false);
        }
    }

    /// <summary>
    /// What the text of a text literal alone decides: no control character (FetchXML cannot carry most of them, and
    /// none is admitted); characters outside 7-bit ASCII need N; no empty or white-space-edged text. Whether text fits
    /// the column is decided with the column's type in <see cref="DataFilterOperandAdmission"/>.
    /// </summary>
    private static void AdmitText(StringLiteral text)
    {
        var value = text.Value;

        if (value.Any(IsControlOrNotXml))
        {
            throw Refuse(text, "text that holds a control character",
                "Remove control characters (line breaks, tabs and the like) from the value.");
        }

        if (!text.IsNational && value.Any(c => c > (char)0x7F))
        {
            throw Refuse(text, "text without N that holds characters outside 7-bit ASCII",
                "SQL Server would convert it to its code page; Dataverse would not. Write it with N: N'...'.");
        }

        if (value.Length == 0 || IsWhiteSpace(value[0]) || IsWhiteSpace(value[^1]))
        {
            throw Refuse(text, "empty text, or text that begins or ends with white space",
                "Dataverse has not been shown to compare such text as SQL Server does. Remove the white space at either " +
                "end, or use IS NULL for records with no value.");
        }
    }

    /// <summary>A C0 or C1 control character, a lone surrogate half, or a character XML does not allow.</summary>
    private static bool IsControlOrNotXml(char c) => c < (char)0x20 || c is >= (char)0x7F and <= (char)0x9F
        || char.IsSurrogate(c) || c == (char)0xFFFE || c == (char)0xFFFF;

    /// <summary>The Unicode White_Space property (as the SQL write path's rule for text).</summary>
    private static bool IsWhiteSpace(char c) => c is >= (char)0x09 and <= (char)0x0D or (char)0x20 or (char)0x85
        or (char)0xA0 or (char)0x1680 or >= (char)0x2000 and <= (char)0x200A or (char)0x2028 or (char)0x2029
        or (char)0x202F or (char)0x205F or (char)0x3000;

    private static bool IsLiteral(ScalarExpression expression) => expression is Literal
        || expression is UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: Literal };

    private static PpdsException RefuseValue(ScalarExpression expression, bool whereAColumnIsExpected)
    {
        var (kind, instead) = expression switch
        {
            CastCall or ConvertCall or TryCastCall or TryConvertCall or ParseCall or TryParseCall =>
                ("CAST or CONVERT", "Write the value as a literal of the column's kind."),
            FunctionCall or LeftFunctionCall or RightFunctionCall or IIfCall or CoalesceExpression or NullIfExpression =>
                ("a function", "Functions are not sent to Dataverse. Compare the column itself; for part of a text, use LIKE."),
            BinaryExpression => ("arithmetic", "Work out the value and compare the column with it."),
            SearchedCaseExpression or SimpleCaseExpression => ("a CASE expression", "Write one condition per case, joined by OR."),
            ScalarSubquery => ("a subquery", "Compare a column of the table with a literal."),
            VariableReference or GlobalVariableExpression => ("a variable", "Write the value itself."),
            ParenthesisExpression => ("a value in parentheses", "Write the value without parentheses."),
            UnaryExpression => ("a sign or operator on a value", "Write the number itself, for example -5."),
            Literal => ("a literal of a kind not supported here (floating point, money, binary or ODBC)",
                "Write a whole or decimal number, or quoted text."),
            ColumnReferenceExpression => ("a special column", "Use the column's logical name."),
            _ => ("an expression of a kind not supported here", "")
        };
        if (whereAColumnIsExpected && IsLiteral(expression))
            (kind, instead) = ("a comparison without a column", "Compare a column with a literal.");
        return Refuse(expression, kind, instead);
    }

    internal static PpdsException Refuse(TSqlFragment part, string kind, string instead)
        => Refuse(part.StartLine, part.StartColumn, kind, instead);

    internal static PpdsException Refuse(int line, int column, string kind, string instead)
    {
        // The position and the kind of the part, never its text: the filter may hold values that are not to be echoed.
        var message = $"The filter cannot be used: {kind} at line {line}, column {column} is not supported here. " +
            $"Nothing was read or changed.{(instead.Length > 0 ? " " + instead : "")}{Instead}";
        return new PpdsException(ErrorCodes.Validation.FilterNotSupported, message);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Translation of the admitted tree, once the columns are known. Each admitted leaf becomes one FetchXML condition,
    // and each negated leaf is paired with not-null on the same column, inside an "and" filter of its own.

    internal static XElement Emit(BooleanExpression condition, ColumnBinder bind)
    {
        switch (condition)
        {
            case BooleanBinaryExpression binary:
                return new XElement("filter",
                    new XAttribute("type", binary.BinaryExpressionType == BooleanBinaryExpressionType.Or ? "or" : "and"),
                    Emit(binary.FirstExpression, bind),
                    Emit(binary.SecondExpression, bind));

            case BooleanParenthesisExpression parenthesis:
                return Emit(parenthesis.Expression, bind);

            case BooleanComparisonExpression comparison:
            {
                var (op, use) = comparison.ComparisonType switch
                {
                    BooleanComparisonType.Equals => ("eq", DataFilterUse.Equality),
                    BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => ("ne", DataFilterUse.Equality),
                    BooleanComparisonType.LessThan => ("lt", DataFilterUse.Range),
                    BooleanComparisonType.LessThanOrEqualTo => ("le", DataFilterUse.Range),
                    BooleanComparisonType.GreaterThan => ("gt", DataFilterUse.Range),
                    BooleanComparisonType.GreaterThanOrEqualTo => ("ge", DataFilterUse.Range),
                    _ => throw new InvalidOperationException("A comparison operator that was not admitted.")
                };
                var column = bind.Column(comparison.FirstExpression);
                var element = Condition(column.Name, op, bind.Value(column, comparison.SecondExpression, use));
                return op == "ne" ? ExcludingNull(column.Name, element) : element;
            }

            case InPredicate inPredicate:
            {
                var column = bind.Column(inPredicate.Expression);
                var element = new XElement("condition",
                    new XAttribute("attribute", column.Name),
                    new XAttribute("operator", inPredicate.NotDefined ? "not-in" : "in"),
                    inPredicate.Values.Select(v => new XElement("value", bind.Value(column, v, DataFilterUse.In))));
                return inPredicate.NotDefined ? ExcludingNull(column.Name, element) : element;
            }

            case LikePredicate like:
            {
                // The whole pattern goes as like / not-like, which keep SQL's % and _; begins-with and ends-with are not
                // used, because whether Dataverse reads a wildcard inside their value is not established.
                var column = bind.Column(like.FirstExpression);
                var element = Condition(column.Name, like.NotDefined ? "not-like" : "like",
                    bind.Value(column, like.SecondExpression, DataFilterUse.Like));
                return like.NotDefined ? ExcludingNull(column.Name, element) : element;
            }

            case BooleanIsNullExpression isNull:
            {
                var column = bind.Column(isNull.Expression);
                bind.NullTest(column, isNull.Expression);
                return new XElement("condition",
                    new XAttribute("attribute", column.Name),
                    new XAttribute("operator", isNull.IsNot ? "not-null" : "null"));
            }

            case BooleanTernaryExpression between:
            {
                var column = bind.Column(between.FirstExpression);
                var negated = between.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween;
                var element = new XElement("condition",
                    new XAttribute("attribute", column.Name),
                    new XAttribute("operator", negated ? "not-between" : "between"),
                    new XElement("value", bind.Value(column, between.SecondExpression, DataFilterUse.Range)),
                    new XElement("value", bind.Value(column, between.ThirdExpression, DataFilterUse.Range)));
                return negated ? ExcludingNull(column.Name, element) : element;
            }

            default:
                throw new InvalidOperationException($"A condition that was not admitted: {condition.GetType().Name}.");
        }
    }

    private static XElement Condition(string column, string op, string value) => new("condition",
        new XAttribute("attribute", column), new XAttribute("operator", op), new XAttribute("value", value));

    private static XElement ExcludingNull(string column, XElement negated) => new("filter",
        new XAttribute("type", "and"),
        negated,
        new XElement("condition", new XAttribute("attribute", column), new XAttribute("operator", "not-null")));

    // ------------------------------------------------------------------------------------------------------------
    // A second guard on the translation, and only a count. A generic walk of the parsed tree counts its conditions, the
    // FetchXML's conditions are counted, and each not-null partner of a negated condition is checked on its own; a
    // difference stops the command before the read. It catches a condition left out or added (the defect of issue
    // 1705). It does not check an operator, a value, a column or AND against OR; the translation's tests do that.

    private static readonly HashSet<string> NegatedOperators = new(StringComparer.Ordinal)
    {
        "ne", "not-in", "not-like", "not-between"
    };

    /// <summary>
    /// Counts the conditions of <paramref name="condition"/> and of the FetchXML <paramref name="filter"/>, and the
    /// <c>not-null</c> partners of its negated conditions, so a condition left out or added stops the command before the
    /// read. It does not compare operators, values, columns or AND against OR.
    /// </summary>
    /// <param name="condition">The parsed filter.</param>
    /// <param name="filter">The FetchXML filter element translated from it.</param>
    /// <returns>The number of conditions in the filter.</returns>
    /// <exception cref="PpdsException"><see cref="ErrorCodes.Operation.Internal"/> on any difference.</exception>
    internal static int VerifyEveryConditionEmitted(BooleanExpression condition, XElement filter)
    {
        var expected = ConditionCounter.Count(condition);

        var emitted = filter.DescendantsAndSelf("condition").ToList();
        var negated = emitted.Count(c => NegatedOperators.Contains((string?)c.Attribute("operator") ?? ""));
        var partners = emitted.Count(IsNotNullPartner);
        var translated = emitted.Count - partners;

        if (translated != expected || partners != negated)
        {
            throw new PpdsException(ErrorCodes.Operation.Internal,
                $"Internal error: the filter has {expected} condition(s) but the FetchXML built from it has " +
                $"{translated}, with {partners} of {negated} negated condition(s) excluding NULL. Nothing was read or " +
                "changed. Please report this.");
        }

        return expected;
    }

    /// <summary>A not-null condition that is the second child of an "and" filter whose first child is a negated condition on the same column.</summary>
    private static bool IsNotNullPartner(XElement condition)
    {
        if ((string?)condition.Attribute("operator") != "not-null" || condition.Parent is not { } parent)
            return false;
        var children = parent.Elements().ToList();
        return parent.Name.LocalName == "filter"
            && (string?)parent.Attribute("type") == "and"
            && children.Count == 2
            && children[1] == condition
            && children[0].Name.LocalName == "condition"
            && NegatedOperators.Contains((string?)children[0].Attribute("operator") ?? "")
            && (string?)children[0].Attribute("attribute") == (string?)condition.Attribute("attribute");
    }

    /// <summary>Counts every condition node of a boolean tree: every boolean expression that is not AND, OR or parentheses.</summary>
    private sealed class ConditionCounter : TSqlFragmentVisitor
    {
        private int _count;

        public static int Count(BooleanExpression condition)
        {
            var counter = new ConditionCounter();
            condition.Accept(counter);
            return counter._count;
        }

        public override void Visit(BooleanExpression node)
        {
            if (node is not (BooleanBinaryExpression or BooleanParenthesisExpression))
                _count++;
            base.Visit(node);
        }
    }
}

/// <summary>How a condition uses its column and value; the operand admission decides by column type and use.</summary>
internal enum DataFilterUse
{
    /// <summary><c>=</c>, <c>&lt;&gt;</c>, <c>!=</c>.</summary>
    Equality,

    /// <summary><c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>BETWEEN</c>.</summary>
    Range,

    /// <summary><c>IN</c>, <c>NOT IN</c>.</summary>
    In,

    /// <summary><c>LIKE</c>, <c>NOT LIKE</c>.</summary>
    Like,

    /// <summary><c>IS NULL</c>, <c>IS NOT NULL</c> (no value).</summary>
    NullTest
}

/// <summary>The kind of a literal in a filter.</summary>
internal enum DataFilterLiteralKind
{
    /// <summary>A whole or decimal number, with an optional minus sign on it.</summary>
    Number,

    /// <summary>Quoted text, with or without N.</summary>
    Text
}

/// <summary>A literal of the filter, as written, and where.</summary>
/// <param name="Kind">Number or text.</param>
/// <param name="Text">The number as written (with its minus sign), or the text's value.</param>
/// <param name="IsNational">Whether text was written with N.</param>
/// <param name="Line">The literal's line in the filter.</param>
/// <param name="Column">The literal's column in the filter.</param>
internal sealed record DataFilterLiteral(DataFilterLiteralKind Kind, string Text, bool IsNational, int Line, int Column);

/// <summary>A column of the table, as the filter uses it.</summary>
/// <param name="Name">The column's logical name.</param>
/// <param name="Metadata">The column's metadata.</param>
/// <param name="Line">Where the filter names it: line.</param>
/// <param name="Column">Where the filter names it: column.</param>
internal sealed record DataFilterColumn(string Name, AttributeMetadataDto Metadata, int Line, int Column);

/// <summary>Binds the filter's columns and values to the table's metadata while the FetchXML is built.</summary>
internal sealed class ColumnBinder
{
    private readonly Dictionary<string, AttributeMetadataDto> _columns;

    public ColumnBinder(IEnumerable<AttributeMetadataDto> columns)
    {
        _columns = new Dictionary<string, AttributeMetadataDto>(StringComparer.Ordinal);
        foreach (var column in columns)
            _columns[column.LogicalName.ToLowerInvariant()] = column;
    }

    /// <summary>The table's column named by <paramref name="expression"/>, or a refusal naming its position.</summary>
    public DataFilterColumn Column(ScalarExpression expression)
    {
        var reference = (ColumnReferenceExpression)expression;
        var name = reference.MultiPartIdentifier.Identifiers[0].Value.ToLowerInvariant();
        if (!_columns.TryGetValue(name, out var metadata))
        {
            throw DataFilter.Refuse(expression, "a column the table does not have",
                "Check the column's logical name ('metadata attributes' lists a table's columns).");
        }

        return new DataFilterColumn(name, metadata, expression.StartLine, expression.StartColumn);
    }

    /// <summary>Admits <c>IS [NOT] NULL</c> on <paramref name="column"/>, or refuses.</summary>
    public void NullTest(DataFilterColumn column, ScalarExpression expression)
        => DataFilterOperandAdmission.Admit(column, DataFilterUse.NullTest, literal: null);

    /// <summary>The FetchXML value for <paramref name="expression"/> against <paramref name="column"/>, or a refusal.</summary>
    public string Value(DataFilterColumn column, ScalarExpression expression, DataFilterUse use)
    {
        var literal = expression switch
        {
            StringLiteral text => new DataFilterLiteral(DataFilterLiteralKind.Text, text.Value, text.IsNational,
                text.StartLine, text.StartColumn),
            IntegerLiteral or NumericLiteral => new DataFilterLiteral(DataFilterLiteralKind.Number,
                ((Literal)expression).Value, false, expression.StartLine, expression.StartColumn),
            UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: Literal inner } =>
                new DataFilterLiteral(DataFilterLiteralKind.Number, "-" + inner.Value, false,
                    expression.StartLine, expression.StartColumn),
            _ => throw new InvalidOperationException($"A value that was not admitted: {expression.GetType().Name}.")
        };
        return DataFilterOperandAdmission.Admit(column, use, literal)
            ?? throw new InvalidOperationException("The admission returned no value for a literal.");
    }
}

/// <summary>A filter admitted by <see cref="DataFilter.Translate"/>, not yet bound to the table's columns.</summary>
public sealed class AdmittedDataFilter
{
    internal AdmittedDataFilter(string entityName, BooleanExpression condition, int conditionCount)
    {
        EntityName = entityName;
        Condition = condition;
        ConditionCount = conditionCount;
    }

    /// <summary>The table's logical name.</summary>
    public string EntityName { get; }

    /// <summary>The number of conditions in the filter.</summary>
    public int ConditionCount { get; }

    internal BooleanExpression Condition { get; }

    /// <summary>
    /// Binds the filter to the table's columns: every column must be one of <paramref name="columns"/>, and every value
    /// is admitted for its column's type. Call it with the metadata read before any record.
    /// </summary>
    /// <param name="columns">The table's column metadata.</param>
    /// <returns>The filter with its FetchXML.</returns>
    /// <exception cref="PpdsException">
    /// <see cref="ErrorCodes.Validation.FilterNotSupported"/> for a column the table does not have or a value its column
    /// does not admit; <see cref="ErrorCodes.Query.FilterMetadataUnavailable"/> when the metadata names no primary id;
    /// <see cref="ErrorCodes.Operation.Internal"/> when the FetchXML does not account for every condition.
    /// </exception>
    public BoundDataFilter Bind(IReadOnlyList<AttributeMetadataDto> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var primaryId = columns.FirstOrDefault(c => c.IsPrimaryId)?.LogicalName;
        if (string.IsNullOrEmpty(primaryId))
        {
            throw new PpdsException(ErrorCodes.Query.FilterMetadataUnavailable,
                $"The columns of '{EntityName}' were read without a primary id column, so the filter's values cannot be " +
                "checked against their column types. Nothing was read or changed.");
        }

        var element = DataFilter.Emit(Condition, new ColumnBinder(columns));
        if (element.Name.LocalName == "condition")
            element = new XElement("filter", new XAttribute("type", "and"), element);
        var count = DataFilter.VerifyEveryConditionEmitted(Condition, element);
        return new BoundDataFilter(EntityName, primaryId.ToLowerInvariant(), element, count);
    }
}

/// <summary>A filter bound to the table's columns, ready to be read page by page.</summary>
/// <param name="EntityName">The table's logical name.</param>
/// <param name="PrimaryIdAttribute">The table's primary id column (from its metadata).</param>
/// <param name="Filter">The FetchXML filter element that selects the records the SQL condition selects.</param>
/// <param name="ConditionCount">The number of conditions in the filter.</param>
public sealed record BoundDataFilter(string EntityName, string PrimaryIdAttribute, XElement Filter, int ConditionCount)
{
    /// <summary>The FetchXML for one page of the matching records' ids, in id order.</summary>
    /// <param name="page">The page number, from 1.</param>
    /// <param name="pagingCookie">The paging cookie of the previous page, if any.</param>
    /// <returns>The FetchXML text.</returns>
    public string ToFetchXml(int page, string? pagingCookie)
    {
        var fetch = new XElement("fetch",
            new XAttribute("page", page.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("count", DataFilter.PageSize.ToString(CultureInfo.InvariantCulture)),
            new XElement("entity",
                new XAttribute("name", EntityName),
                new XElement("attribute", new XAttribute("name", PrimaryIdAttribute)),
                new XElement("order", new XAttribute("attribute", PrimaryIdAttribute)),
                new XElement(Filter)));
        if (!string.IsNullOrEmpty(pagingCookie))
            fetch.Add(new XAttribute("paging-cookie", pagingCookie));
        return fetch.ToString(SaveOptions.DisableFormatting);
    }
}
