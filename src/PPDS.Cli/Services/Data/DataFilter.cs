using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// The <c>--filter</c> of <c>data delete</c> and <c>data update</c>: a SQL condition admitted only when every part of it
/// is one of a closed list of shapes that FetchXML expresses exactly, translated to FetchXML by this class alone (not by
/// the query engine's generator, which leaves out what it cannot express and evaluates it later; these commands have no
/// later). Anything else is refused before anything is read (issue 1705).
/// </summary>
/// <remarks>
/// Admitted, joined by <c>AND</c> and <c>OR</c> with parentheses: a column compared with a literal (<c>=</c>, <c>&lt;&gt;</c>,
/// <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>); <c>[NOT] IN</c> with literals; <c>[NOT] LIKE</c> with a
/// text pattern; <c>IS [NOT] NULL</c>; <c>[NOT] BETWEEN</c> with literals. The column comes first and is a plain column
/// name. A literal is a whole or decimal number (optionally with a minus sign directly on it) or quoted text. The
/// negated forms (<c>&lt;&gt;</c>, <c>NOT IN</c>, <c>NOT LIKE</c>, <c>NOT BETWEEN</c>) are each sent together with
/// <c>not-null</c> on the same column, because FetchXML's <c>ne</c>, <c>not-in</c> and <c>not-like</c> also match rows
/// whose column is NULL and SQL's never do.
/// </remarks>
public static class DataFilter
{
    /// <summary>The page size used to read the matching records (Dataverse's largest page).</summary>
    public const int PageSize = 5000;

    private static readonly Regex LogicalName = new(@"\A[a-z_][a-z0-9_]*\z", RegexOptions.CultureInvariant);

    private static readonly Regex ZonedDateTime = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:Z|[+-][0-9]{2}:[0-9]{2})\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex CanonicalGuid = new(
        @"\A[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z",
        RegexOptions.CultureInvariant);

    private const string Instead =
        " A filter may only compare a column with a literal (=, <>, <, <=, >, >=), or use IN, LIKE, IS NULL or " +
        "BETWEEN with literals, joined by AND and OR. To act on records chosen another way, select their ids with " +
        "'query sql' and pass them with --file.";

    /// <summary>
    /// Admits <paramref name="filter"/> and translates it to the FetchXML <c>filter</c> element that selects exactly the
    /// same records, or throws before anything is read.
    /// </summary>
    /// <param name="entity">The table's logical name.</param>
    /// <param name="filter">The SQL condition, without <c>WHERE</c>.</param>
    /// <returns>The admitted filter.</returns>
    /// <exception cref="PpdsException">
    /// <see cref="ErrorCodes.Validation.FilterNotSupported"/> for a part the list does not admit;
    /// <see cref="ErrorCodes.Validation.RequiredField"/> for an empty filter;
    /// <see cref="ErrorCodes.Validation.InvalidValue"/> for an entity name that is not a logical name;
    /// <see cref="ErrorCodes.Query.ParseError"/> for text that is not a SQL condition;
    /// <see cref="ErrorCodes.Operation.Internal"/> when the translation does not account for every condition.
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
                "The filter is empty. Write the condition the records must meet, for example --filter \"statecode = 1\". " +
                "To delete every record of a table, use 'data truncate'.");
        }

        var condition = Parse(filter);
        Admit(condition);
        var element = Emit(condition);
        if (element.Name.LocalName == "condition")
            element = new XElement("filter", new XAttribute("type", "and"), element);
        var count = VerifyEveryConditionEmitted(condition, element);
        return new AdmittedDataFilter(entityName, $"{entityName}id", element, count);
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
                $"The filter is not a SQL condition{where}. Write a condition such as \"name = N'Contoso' AND statecode = 0\".");
        }

        return condition;
    }

    // ------------------------------------------------------------------------------------------------------------
    // The allow-list: every node of the condition tree must be one of these shapes; anything else is refused.

    private static void Admit(BooleanExpression condition)
    {
        switch (condition)
        {
            case BooleanBinaryExpression binary
                when binary.BinaryExpressionType is BooleanBinaryExpressionType.And or BooleanBinaryExpressionType.Or:
                Admit(binary.FirstExpression);
                Admit(binary.SecondExpression);
                return;

            case BooleanParenthesisExpression parenthesis:
                Admit(parenthesis.Expression);
                return;

            case BooleanComparisonExpression comparison:
                AdmitComparison(comparison);
                return;

            case InPredicate inPredicate:
                AdmitIn(inPredicate);
                return;

            case LikePredicate like:
                AdmitLike(like);
                return;

            case BooleanIsNullExpression isNull:
                AdmitColumn(isNull.Expression);
                return;

            case BooleanTernaryExpression between
                when between.TernaryExpressionType is BooleanTernaryExpressionType.Between
                    or BooleanTernaryExpressionType.NotBetween:
                AdmitColumn(between.FirstExpression);
                AdmitLiteral(between.SecondExpression, range: true);
                AdmitLiteral(between.ThirdExpression, range: true);
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
        var range = comparison.ComparisonType switch
        {
            BooleanComparisonType.Equals => false,
            BooleanComparisonType.NotEqualToBrackets => false,
            BooleanComparisonType.NotEqualToExclamation => false,
            BooleanComparisonType.LessThan => true,
            BooleanComparisonType.LessThanOrEqualTo => true,
            BooleanComparisonType.GreaterThan => true,
            BooleanComparisonType.GreaterThanOrEqualTo => true,
            _ => throw Refuse(comparison, "the comparison operator", "Use =, <>, !=, <, <=, > or >=.")
        };

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
        AdmitLiteral(comparison.SecondExpression, range);
    }

    private static void AdmitIn(InPredicate inPredicate)
    {
        if (inPredicate.Subquery is not null)
            throw Refuse(inPredicate, "a subquery", "List the values themselves: IN (1, 2, 3).");

        AdmitColumn(inPredicate.Expression);
        if (inPredicate.Values.Count == 0)
            throw Refuse(inPredicate, "an empty IN list", "List at least one value.");

        foreach (var value in inPredicate.Values)
        {
            if (value is NullLiteral)
            {
                throw Refuse(value, "NULL in an IN list",
                    "In SQL it never matches. Leave NULL out of the list, and add OR <column> IS NULL if you mean the " +
                    "records with no value.");
            }

            AdmitLiteral(value, range: false);
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

        AdmitText(pattern, range: false);
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

    private static void AdmitLiteral(ScalarExpression expression, bool range)
    {
        switch (expression)
        {
            case IntegerLiteral or NumericLiteral when ((Literal)expression).Collation is null:
                return;

            case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral or NumericLiteral }:
                return;

            case StringLiteral text when text.Collation is null:
                AdmitText(text, range);
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
    /// Checks a text literal against the write-path rules for literals: text with characters outside 7-bit ASCII needs
    /// N; no empty or white-space-edged text; a date and time only as <c>yyyy-MM-ddTHH:mm:ss</c> with a zone; a GUID only
    /// as 8-4-4-4-12 hexadecimal digits with dashes; and no range comparison with text other than such a date and time.
    /// </summary>
    private static void AdmitText(StringLiteral text, bool range)
    {
        var value = text.Value;

        if (!text.IsNational && value.Any(c => c > '\u007F'))
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

        var hasZonedForm = ZonedDateTime.IsMatch(value);
        var isZonedDateTime = hasZonedForm
            && DateTimeOffset.TryParseExact(value, ["yyyy-MM-ddTHH:mm:ssK"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
        if (!isZonedDateTime && (hasZonedForm || LooksLikeDateTime(value)))
        {
            throw Refuse(text, "a date and time without a time zone, or not in the form yyyy-MM-ddTHH:mm:ss with Z or an offset",
                "Dataverse would read it in your time zone. Write it as yyyy-MM-ddTHH:mm:ss followed by Z (UTC) or an " +
                "offset, for example '2024-01-15T09:30:00Z' or '2024-01-15T10:30:00+01:00'.");
        }

        if (Guid.TryParse(value, out _) && !CanonicalGuid.IsMatch(value))
        {
            throw Refuse(text, "a GUID not written as 8-4-4-4-12 digits with dashes",
                "Write it as 8-4-4-4-12 hexadecimal digits with dashes and without braces, for example " +
                "'00000000-0000-0000-0000-000000000000'.");
        }

        if (range && !isZonedDateTime)
        {
            throw Refuse(text, "a range comparison (<, <=, >, >=, BETWEEN) with text",
                "Compare a number without quotes, or a date and time written as yyyy-MM-ddTHH:mm:ssZ.");
        }
    }

    private static bool LooksLikeDateTime(string value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>The Unicode White_Space property (as the SQL write path's rule for text).</summary>
    private static bool IsWhiteSpace(char c) => c is >= '\u0009' and <= '\u000D' or '\u0020' or '\u0085' or '\u00A0'
        or '\u1680' or >= '\u2000' and <= '\u200A' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000';

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

    private static PpdsException Refuse(TSqlFragment part, string kind, string instead)
    {
        // The position and the kind of the part, never its text: the filter may hold values that are not to be echoed.
        var message = $"The filter cannot be used: {kind} at line {part.StartLine}, column {part.StartColumn} is not " +
            $"supported here. Nothing was read or changed.{(instead.Length > 0 ? " " + instead : "")}{Instead}";
        return new PpdsException(ErrorCodes.Validation.FilterNotSupported, message);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Translation of the admitted tree. Each admitted leaf becomes one FetchXML condition, and each negated leaf is
    // paired with not-null on the same column, inside an "and" filter of its own.

    private static XElement Emit(BooleanExpression condition)
    {
        switch (condition)
        {
            case BooleanBinaryExpression binary:
                return new XElement("filter",
                    new XAttribute("type", binary.BinaryExpressionType == BooleanBinaryExpressionType.Or ? "or" : "and"),
                    Emit(binary.FirstExpression),
                    Emit(binary.SecondExpression));

            case BooleanParenthesisExpression parenthesis:
                return Emit(parenthesis.Expression);

            case BooleanComparisonExpression comparison:
            {
                var column = ColumnName(comparison.FirstExpression);
                var value = LiteralText(comparison.SecondExpression);
                return comparison.ComparisonType switch
                {
                    BooleanComparisonType.Equals => Condition(column, "eq", value),
                    BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation =>
                        ExcludingNull(column, Condition(column, "ne", value)),
                    BooleanComparisonType.LessThan => Condition(column, "lt", value),
                    BooleanComparisonType.LessThanOrEqualTo => Condition(column, "le", value),
                    BooleanComparisonType.GreaterThan => Condition(column, "gt", value),
                    BooleanComparisonType.GreaterThanOrEqualTo => Condition(column, "ge", value),
                    _ => throw new InvalidOperationException("A comparison operator that was not admitted.")
                };
            }

            case InPredicate inPredicate:
            {
                var column = ColumnName(inPredicate.Expression);
                var element = new XElement("condition",
                    new XAttribute("attribute", column),
                    new XAttribute("operator", inPredicate.NotDefined ? "not-in" : "in"),
                    inPredicate.Values.Select(v => new XElement("value", LiteralText(v))));
                return inPredicate.NotDefined ? ExcludingNull(column, element) : element;
            }

            case LikePredicate like:
            {
                // The whole pattern goes as like / not-like, which keep SQL's % and _; begins-with and ends-with are not
                // used, because whether Dataverse reads a wildcard inside their value is not established.
                var column = ColumnName(like.FirstExpression);
                var element = Condition(column, like.NotDefined ? "not-like" : "like", ((StringLiteral)like.SecondExpression).Value);
                return like.NotDefined ? ExcludingNull(column, element) : element;
            }

            case BooleanIsNullExpression isNull:
                return new XElement("condition",
                    new XAttribute("attribute", ColumnName(isNull.Expression)),
                    new XAttribute("operator", isNull.IsNot ? "not-null" : "null"));

            case BooleanTernaryExpression between:
            {
                var column = ColumnName(between.FirstExpression);
                var negated = between.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween;
                var element = new XElement("condition",
                    new XAttribute("attribute", column),
                    new XAttribute("operator", negated ? "not-between" : "between"),
                    new XElement("value", LiteralText(between.SecondExpression)),
                    new XElement("value", LiteralText(between.ThirdExpression)));
                return negated ? ExcludingNull(column, element) : element;
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

    private static string ColumnName(ScalarExpression expression)
        => ((ColumnReferenceExpression)expression).MultiPartIdentifier.Identifiers[0].Value.ToLowerInvariant();

    private static string LiteralText(ScalarExpression expression) => expression switch
    {
        StringLiteral text => text.Value,
        IntegerLiteral or NumericLiteral => ((Literal)expression).Value,
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: Literal literal } => "-" + literal.Value,
        _ => throw new InvalidOperationException($"A value that was not admitted: {expression.GetType().Name}.")
    };

    // ------------------------------------------------------------------------------------------------------------
    // The second, independent guard: count the conditions in the parsed filter (any kind, by a generic walk of the
    // tree) and the conditions in the FetchXML, and stop on any difference.

    private static readonly HashSet<string> NegatedOperators = new(StringComparer.Ordinal)
    {
        "ne", "not-in", "not-like", "not-between"
    };

    /// <summary>
    /// Verifies that the FetchXML <paramref name="filter"/> holds one condition for every condition of
    /// <paramref name="condition"/>, and a <c>not-null</c> partner for every negated one, so a dropped condition stops the
    /// command before the read.
    /// </summary>
    /// <param name="condition">The parsed filter.</param>
    /// <param name="filter">The FetchXML filter element translated from it.</param>
    /// <returns>The number of conditions in the filter.</returns>
    /// <exception cref="PpdsException"><see cref="ErrorCodes.Operation.Internal"/> on any difference.</exception>
    internal static int VerifyEveryConditionEmitted(BooleanExpression condition, XElement filter)
    {
        var counter = new ConditionCounter();
        condition.Accept(counter);

        var emitted = filter.DescendantsAndSelf("condition").ToList();
        var negated = emitted.Count(c => NegatedOperators.Contains((string?)c.Attribute("operator") ?? ""));
        var partners = emitted.Count(IsNotNullPartner);
        var translated = emitted.Count - partners;

        if (translated != counter.Count || partners != negated)
        {
            throw new PpdsException(ErrorCodes.Operation.Internal,
                $"Internal error: the filter has {counter.Count} condition(s) but the FetchXML built from it has " +
                $"{translated}, with {partners} of {negated} negated condition(s) excluding NULL. Nothing was read or " +
                "changed. Please report this.");
        }

        return counter.Count;
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
        public int Count { get; private set; }

        public override void Visit(BooleanExpression node)
        {
            if (node is not (BooleanBinaryExpression or BooleanParenthesisExpression))
                Count++;
            base.Visit(node);
        }
    }
}

/// <summary>A filter admitted by <see cref="DataFilter.Translate"/>.</summary>
/// <param name="EntityName">The table's logical name.</param>
/// <param name="PrimaryIdAttribute">The id column read for each record.</param>
/// <param name="Filter">The FetchXML filter element that selects exactly the records the SQL condition selects.</param>
/// <param name="ConditionCount">The number of conditions in the filter.</param>
public sealed record AdmittedDataFilter(string EntityName, string PrimaryIdAttribute, XElement Filter, int ConditionCount)
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
