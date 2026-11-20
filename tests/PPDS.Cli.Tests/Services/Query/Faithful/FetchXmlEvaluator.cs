using System.Globalization;
using System.ServiceModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Evaluates a FetchXML request over <see cref="FakeTable"/> rows the way Dataverse does. "Audit" means the November 2026
/// live SELECT audit (docs/qa/select-fidelity-audit-2026-11.md): "OBSERVED" lines are raw FetchXML sent through the SDK;
/// case ids (W15, D05, ...) are statements run through PPDS whose Dataverse behaviour the audit recorded. A rule marked
/// "not observed" follows the FetchXML documentation and has no live observation behind it.
/// <list type="bullet">
/// <item>R1 (audit OBSERVED): <c>ne</c>, <c>not-in</c>, <c>not-like</c>, <c>not-begin-with</c> and <c>not-end-with</c> are true
/// when the column is NULL.</item>
/// <item>R2: <c>not-between</c> is false when the column is NULL (audit OBSERVED). Every other operator except <c>null</c> is
/// false for NULL too (not observed one by one; consistent with the observed <c>ge</c>/<c>lt</c> date probe).</item>
/// <item>R3 (audit OBSERVED): <c>eq ""</c> matches nothing: Dataverse stores an empty string as NULL.</item>
/// <item>R4 (audit OBSERVED): string comparison is case- and accent-insensitive and ignores trailing spaces; leading spaces
/// count. Strings compare by a fixed key, not by the machine's ICU or NLS data: letters fold to A-Z, and the order is
/// space &lt; <c>%</c> &lt; <c>[</c> &lt; <c>]</c> &lt; <c>_</c> &lt; digits &lt; letters, a shorter prefix first. Observed
/// ("order by name asc"): <c>%</c> &lt; <c>[</c> &lt; <c>_</c> &lt; letters, space &lt; <c>[</c> &lt; digits &lt; letters.
/// Not observed: space against <c>%</c>, <c>]</c> against anything, and <c>%</c> or <c>_</c> against digits. Any other
/// character (an apostrophe, a letter that does not fold to A-Z, kana, ...) is refused.</item>
/// <item>R5: <c>like</c> and <c>begins-with</c> treat <c>%</c>, <c>_</c> and <c>[...]</c> as wildcards, also in a
/// <c>begins-with</c> value (audit OBSERVED); <c>[^...]</c> and ranges as in W63 and W79 (audit, through PPDS); trailing spaces
/// count in LIKE (audit W69 and the OBSERVED <c>not-end-with</c> line). <c>ends-with</c> is taken to behave like
/// <c>begins-with</c> (not observed).</item>
/// <item>R6 (audit OBSERVED): ORDER BY puts NULL first ascending and last descending; a choice column orders by its label;
/// strings order by the R4 key. Ties keep table order (not observed). Orders inside a link-entity apply after the entity's
/// own orders (not observed). ORDER BY a lookup or unique-identifier column is refused (not observed).</item>
/// <item>R7 (audit OBSERVED): a date-time value without an offset is read in the caller's time zone; a date-only column
/// compares dates. Only the forms <c>yyyy-MM-dd</c>, <c>yyyy-MM-dd[T| ]HH:mm[:ss[.f…]]</c> with an optional <c>Z</c> or
/// <c>±HH:mm</c> are read; any other form is refused (not observed).</item>
/// <item>R8 (audit, cause of #1624): a NULL attribute is absent from the returned record.</item>
/// <item>R9 (not observed): an attribute with an alias comes back under the alias; a link-entity attribute without one comes
/// back as <c>linkalias.attribute</c>.</item>
/// <item>R10 (not observed): <c>top</c> and <c>count</c> limit the rows; a page holds <c>count</c> rows, 5,000 by default;
/// <c>top</c> with paging is a fault; <c>top</c> or <c>count</c> above 5,000 is refused.</item>
/// <item>R11 (not observed): link-entity inner and outer joins; a filter inside a link-entity restricts the joined rows (for an
/// outer join, the ON clause); a condition with <c>entityname</c> applies to the joined row; NULL never joins. An inner
/// link nested under an outer link is refused.</item>
/// <item>R12 (audit O03, J21, S06-S08): an attribute the table does not have is a fault.</item>
/// <item>R13 (audit W15): a value that does not convert to the column's type is a fault, for example <c>5.0</c> on a whole
/// number column. Exponents, padding and a decimal point on a whole-number column are faults (W15 observed the decimal
/// point; the others not observed).</item>
/// <item>R14 (audit D05, PG08): <c>distinct</c> compares strings by the R4 key and returns no record id. Which of several
/// case variants is returned is not observed (the first row is kept).</item>
/// <item>R15 formatted values: a choice has its label (audit O07), a whole number its digits (audit PRJ01, values below
/// 1,000 only), a two-option column "Yes"/"No" (audit O09 observed "No"), a date-time <c>M/d/yyyy h:mm tt</c> in the caller's
/// time zone (audit OBSERVED formatted-value line), a lookup its name. Money, decimal, float, date-only and whole numbers of
/// 1,000 or more carry <see cref="NotModelledFormattedValue"/>, so a test that reads them fails.</item>
/// </list>
/// The whole document is validated before any row is read: an unknown element, attribute, operator, boolean spelling or
/// value is refused (<see cref="NotSupportedException"/>) or a fault even when no row would reach it.
/// </summary>
internal sealed class FetchXmlEvaluator
{
    private const int MaxPageSize = 5000;

    /// <summary>The formatted value of a type whose Dataverse formatting is not modelled (R15).</summary>
    public const string NotModelledFormattedValue = "<FakeDataverse: formatted value not modelled>";

    /// <summary>R1: the negated operators Dataverse evaluates as true for a NULL column.</summary>
    private static readonly HashSet<string> TrueForNull = new(StringComparer.Ordinal)
    {
        "ne", "not-in", "not-like", "not-begin-with", "not-end-with"
    };

    private static readonly HashSet<string> LikeOperators = new(StringComparer.Ordinal)
    {
        "like", "not-like", "begins-with", "not-begin-with", "ends-with", "not-end-with"
    };

    private static readonly HashSet<string> RangeOperators = new(StringComparer.Ordinal)
    {
        "lt", "gt", "le", "ge", "between", "not-between"
    };

    private static readonly Regex DateForm = new(
        @"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?)?(?<offset>Z|[+-]\d{2}:\d{2})?$",
        RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<string, FakeTable> _tables;
    private readonly TimeSpan _callerUtcOffset;

    public FetchXmlEvaluator(IReadOnlyDictionary<string, FakeTable> tables, TimeSpan callerUtcOffset)
    {
        _tables = tables;
        _callerUtcOffset = callerUtcOffset;
    }

    // ───────────────────────────── model ─────────────────────────────

    private sealed class Source
    {
        public required string Alias { get; init; }
        public required FakeTable Table { get; init; }
        public required XElement Element { get; init; }
        public string LinkType { get; init; } = "inner";
        public string? From { get; init; }
        public string? To { get; init; }
        public Source? Parent { get; init; }
        public List<(string Name, string? Alias)> Attributes { get; } = new();
        public bool AllAttributes { get; set; }
        public List<Filter> Filters { get; } = new();
        public List<Order> Orders { get; } = new();
        public List<Source> Links { get; } = new();
    }

    private sealed record Filter(bool Or, List<object> Children);

    private sealed record Condition(string Alias, string Attribute, FakeColumnType Type, string Operator, object[] Values);

    private sealed record Order(string Alias, string Attribute, FakeColumnType Type, bool Descending);

    private sealed record Spec(Source Root, Dictionary<string, Source> Sources, bool Distinct, int? Top, int? Count, int? Page, bool ReturnTotal);

    /// <summary>A base row and, per link-entity alias, the joined row (null for an unmatched outer join).</summary>
    private sealed class JoinedRow
    {
        public required Dictionary<string, IReadOnlyDictionary<string, object?>?> Rows { get; init; }

        public JoinedRow With(string alias, IReadOnlyDictionary<string, object?>? row)
            => new() { Rows = new Dictionary<string, IReadOnlyDictionary<string, object?>?>(Rows, StringComparer.OrdinalIgnoreCase) { [alias] = row } };
    }

    private const string BaseAlias = "";

    public EntityCollection Evaluate(string fetchXml)
    {
        var spec = Validate(XDocument.Parse(fetchXml));

        var rows = spec.Root.Table.Rows
            .Select(r => new JoinedRow { Rows = new(StringComparer.OrdinalIgnoreCase) { [BaseAlias] = r } })
            .ToList();
        rows = Join(rows, spec.Root);
        foreach (var filter in spec.Root.Filters)
            rows = rows.Where(r => Matches(filter, r)).ToList();

        rows = Sort(rows, AllOrders(spec.Root).ToList(), spec.Sources);
        var entities = rows.Select(r => Project(r, spec)).ToList();
        if (spec.Distinct)
            entities = entities.DistinctBy(Signature).ToList();
        return Page(spec, entities);
    }

    // ───────────────────────────── validation (before any row) ─────────────────────────────

    private Spec Validate(XDocument doc)
    {
        var fetch = doc.Root ?? throw Unsupported("an empty document");
        Expect(fetch, "fetch");
        AllowAttributes(fetch, "mapping", "distinct", "no-lock", "top", "count", "page", "paging-cookie", "returntotalrecordcount", "aggregate");
        if (fetch.Attribute("mapping") is { } mapping && mapping.Value != "logical")
            throw Unsupported($"mapping=\"{mapping.Value}\"");
        if (fetch.Attribute("aggregate") is { } aggregate && Bool(aggregate))
            throw Unsupported("aggregate FetchXML");
        _ = fetch.Attribute("no-lock") is { } noLock && Bool(noLock);
        NoText(fetch);

        var children = fetch.Elements().ToList();
        if (children.Count != 1)
            throw Unsupported("a fetch without exactly one child element");
        var entity = children[0];
        Expect(entity, "entity");
        AllowAttributes(entity, "name");

        var sources = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        var root = new Source { Alias = BaseAlias, Table = Table((string?)entity.Attribute("name")), Element = entity };
        sources[BaseAlias] = root;
        RegisterLinks(root, sources);
        ValidateContent(root, sources);

        var top = PositiveInt(fetch, "top");
        var count = PositiveInt(fetch, "count");
        var page = PositiveInt(fetch, "page", max: int.MaxValue);
        if (top != null && (count != null || page != null))
            throw Fault("The top attribute can't be specified with paging attribute page or count.");

        return new Spec(root, sources,
            fetch.Attribute("distinct") is { } d && Bool(d),
            top, count, page,
            fetch.Attribute("returntotalrecordcount") is { } t && Bool(t));
    }

    private void RegisterLinks(Source parent, Dictionary<string, Source> sources)
    {
        foreach (var link in parent.Element.Elements("link-entity"))
        {
            AllowAttributes(link, "name", "from", "to", "alias", "link-type");
            var table = Table((string?)link.Attribute("name"));
            var alias = (string?)link.Attribute("alias")
                ?? throw Unsupported("a link-entity without an alias (the name Dataverse generates is not modelled)");
            var from = (string?)link.Attribute("from") ?? throw Unsupported("a link-entity without from");
            var to = (string?)link.Attribute("to") ?? throw Unsupported("a link-entity without to");
            var type = (string?)link.Attribute("link-type") ?? "inner";
            if (type is not ("inner" or "outer"))
                throw Unsupported($"link-type {type}");
            if (type == "inner" && IsUnderOuter(parent))
                throw Unsupported("an inner link-entity nested under an outer one (not observed)");
            if (alias.Length == 0 || sources.ContainsKey(alias))
                throw Fault($"Duplicate or empty link-entity alias '{alias}'.");
            RequireColumn(table, from);
            RequireColumn(parent.Table, to);

            var source = new Source { Alias = alias, Table = table, Element = link, LinkType = type, From = from, To = to, Parent = parent };
            sources[alias] = source;
            parent.Links.Add(source);
            RegisterLinks(source, sources);
        }
    }

    private static bool IsUnderOuter(Source source)
    {
        for (var s = source; s != null; s = s.Parent)
        {
            if (s.LinkType == "outer" && s.Parent != null)
                return true;
        }
        return false;
    }

    private void ValidateContent(Source source, Dictionary<string, Source> sources)
    {
        NoText(source.Element);
        foreach (var child in source.Element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "attribute":
                    AllowAttributes(child, "name", "alias");
                    NoChildren(child);
                    var name = (string?)child.Attribute("name") ?? throw Fault("An attribute has no name.");
                    RequireColumn(source.Table, name);
                    var alias = (string?)child.Attribute("alias");
                    if (alias is { Length: 0 })
                        throw Fault("An attribute alias is empty.");
                    source.Attributes.Add((name, alias));
                    break;
                case "all-attributes":
                    AllowAttributes(child);
                    NoChildren(child);
                    source.AllAttributes = true;
                    break;
                case "filter":
                    // A filter inside a link-entity sees only that link's columns.
                    var scope = source.Alias == BaseAlias
                        ? sources
                        : new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase) { [source.Alias] = source };
                    source.Filters.Add(ReadFilter(child, source.Alias, scope));
                    break;
                case "order":
                    source.Orders.Add(ReadOrder(child, source.Alias, sources));
                    break;
                case "link-entity":
                    break; // registered already; validated through its own Source
                default:
                    throw Unsupported($"<{child.Name.LocalName}> inside <{source.Element.Name.LocalName}>");
            }
        }

        foreach (var link in source.Links)
            ValidateContent(link, sources);
    }

    private Filter ReadFilter(XElement filter, string alias, IReadOnlyDictionary<string, Source> sources)
    {
        AllowAttributes(filter, "type");
        NoText(filter);
        var type = (string?)filter.Attribute("type") ?? "and";
        if (type is not ("and" or "or"))
            throw Unsupported($"filter type {type}");
        var children = filter.Elements().Select(e => e.Name.LocalName switch
        {
            "filter" => (object)ReadFilter(e, alias, sources),
            "condition" => ReadCondition(e, alias, sources),
            var other => throw Unsupported($"<{other}> inside <filter>")
        }).ToList();
        return new Filter(type == "or", children);
    }

    private Condition ReadCondition(XElement c, string defaultAlias, IReadOnlyDictionary<string, Source> sources)
    {
        AllowAttributes(c, "attribute", "operator", "value", "entityname");
        NoText(c);
        foreach (var child in c.Elements())
        {
            if (child.Name.LocalName != "value")
                throw Unsupported($"<{child.Name.LocalName}> inside <condition>");
            AllowAttributes(child);
            if (child.Elements().Any())
                throw Unsupported("child elements of <value>");
        }
        if (c.Attribute("value") != null && c.Elements("value").Any())
            throw Unsupported("a condition with both value= and <value> elements");

        var source = ResolveSource((string?)c.Attribute("entityname") ?? defaultAlias, sources);
        var attribute = (string?)c.Attribute("attribute") ?? throw Fault("A condition has no attribute.");
        var op = (string?)c.Attribute("operator") ?? throw Fault("A condition has no operator.");
        var type = RequireColumn(source.Table, attribute);
        var raw = c.Attribute("value") is { } v ? new[] { v.Value } : c.Elements("value").Select(x => x.Value).ToArray();

        (int Min, int Max) expected = op switch
        {
            "null" or "not-null" => (Min: 0, Max: 0),
            "eq" or "ne" or "lt" or "gt" or "le" or "ge" => (1, 1),
            "in" or "not-in" => (1, int.MaxValue),
            "between" or "not-between" => (2, 2),
            _ when LikeOperators.Contains(op) => (1, 1),
            _ => throw Unsupported($"operator {op}")
        };
        if (raw.Length < expected.Min || raw.Length > expected.Max)
            throw Fault($"Operator {op} on '{attribute}' expects {expected.Min}..{expected.Max} values, got {raw.Length}.");

        if (LikeOperators.Contains(op))
        {
            if (type != FakeColumnType.String)
                throw Unsupported($"{op} on a {type} column");
            var pattern = op switch
            {
                "begins-with" or "not-begin-with" => raw[0] + "%",
                "ends-with" or "not-end-with" => "%" + raw[0],
                _ => raw[0]
            };
            return new Condition(source.Alias, attribute, type, op, [LikeRegex(pattern)]);
        }
        if (RangeOperators.Contains(op) && type is FakeColumnType.Uniqueidentifier or FakeColumnType.Lookup or FakeColumnType.Boolean)
            throw Unsupported($"operator {op} on a {type} column");

        return new Condition(source.Alias, attribute, type, op, raw.Select(x => Parse(x, type, attribute)).ToArray());
    }

    private static Order ReadOrder(XElement order, string alias, IReadOnlyDictionary<string, Source> sources)
    {
        AllowAttributes(order, "attribute", "descending", "entityname");
        NoChildren(order);
        var source = ResolveSource((string?)order.Attribute("entityname") ?? alias, sources);
        var attribute = (string?)order.Attribute("attribute") ?? throw Fault("An order has no attribute.");
        var type = RequireColumn(source.Table, attribute);
        if (type is FakeColumnType.Lookup or FakeColumnType.Uniqueidentifier)
            throw Unsupported($"ORDER BY a {type} column (not observed)");
        return new Order(source.Alias, attribute, type, order.Attribute("descending") is { } d && Bool(d));
    }

    private static Source ResolveSource(string alias, IReadOnlyDictionary<string, Source> sources)
    {
        if (sources.TryGetValue(alias, out var source))
            return source;
        // entityname may name the base entity itself.
        return sources.Values.FirstOrDefault(s => s.Alias == BaseAlias && string.Equals(s.Table.LogicalName, alias, StringComparison.OrdinalIgnoreCase))
            ?? throw Fault($"Link entity with name or alias {alias} is not found.");
    }

    private static void Expect(XElement element, string name)
    {
        if (element.Name.LocalName != name || element.Name.NamespaceName.Length != 0)
            throw Unsupported($"element <{element.Name}> where <{name}> belongs");
    }

    private static void AllowAttributes(XElement element, params string[] allowed)
    {
        foreach (var a in element.Attributes())
        {
            if (a.IsNamespaceDeclaration || !allowed.Contains(a.Name.LocalName) || a.Name.NamespaceName.Length != 0)
                throw Unsupported($"attribute {a.Name} on <{element.Name.LocalName}>");
        }
    }

    private static void NoChildren(XElement element)
    {
        if (element.Elements().Any())
            throw Unsupported($"child elements of <{element.Name.LocalName}>");
        NoText(element);
    }

    private static void NoText(XElement element)
    {
        if (element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            throw Unsupported($"text inside <{element.Name.LocalName}>");
    }

    /// <summary>Only "true" and "false"; other spellings ("1", "True") are not modelled.</summary>
    private static bool Bool(XAttribute attribute) => attribute.Value switch
    {
        "true" => true,
        "false" => false,
        _ => throw Unsupported($"{attribute.Name}=\"{attribute.Value}\" (only true and false are modelled)")
    };

    private static int? PositiveInt(XElement element, string name, int max = MaxPageSize)
    {
        if (element.Attribute(name) is not { } a)
            return null;
        if (!int.TryParse(a.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            throw Fault($"Invalid {name} '{a.Value}'.");
        if (n > max)
            throw Unsupported($"{name} above {max} (not observed)");
        return n;
    }

    private FakeTable Table(string? name)
    {
        if (name is null || !_tables.TryGetValue(name, out var table))
            throw Fault($"The entity with a name = '{name}' with namemapping = 'Logical' was not found in the MetadataCache.");
        return table;
    }

    /// <summary>R12.</summary>
    private static FakeColumnType RequireColumn(FakeTable table, string attribute)
        => table.HasColumn(attribute)
            ? table.TypeOf(attribute)
            : throw Fault($"'{table.LogicalName}' entity doesn't contain attribute with Name = '{attribute}'.");

    // ───────────────────────────── values (R4, R7, R13) ─────────────────────────────

    /// <summary>SDK value to a comparable value: Money, OptionSetValue and EntityReference to their inner value; "" to NULL (R3).</summary>
    private static object? Normalize(object? value) => value switch
    {
        null => null,
        Money m => m.Value,
        OptionSetValue o => (decimal)o.Value,
        EntityReference r => r.Id,
        AliasedValue a => Normalize(a.Value),
        int or long or short or decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        DateTime d => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d,
        string { Length: 0 } => null,
        _ => value
    };

    /// <summary>R13: a value Dataverse cannot convert to the column's type is a fault (W15: "5.0" on a whole number).</summary>
    private object Parse(string value, FakeColumnType type, string attribute)
    {
        const NumberStyles Whole = NumberStyles.AllowLeadingSign;
        const NumberStyles Fraction = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        object? parsed = type switch
        {
            FakeColumnType.String => StringKey(value),
            FakeColumnType.Integer or FakeColumnType.Choice
                => int.TryParse(value, Whole, CultureInfo.InvariantCulture, out var i) ? (decimal)i : null,
            FakeColumnType.BigInt => long.TryParse(value, Whole, CultureInfo.InvariantCulture, out var l) ? (decimal)l : null,
            FakeColumnType.Decimal or FakeColumnType.Money
                => decimal.TryParse(value, Fraction, CultureInfo.InvariantCulture, out var m) ? m : null,
            FakeColumnType.Double
                => double.TryParse(value, Fraction | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var f) ? f : null,
            FakeColumnType.Boolean => value switch { "1" or "true" => true, "0" or "false" => false, _ => null },
            FakeColumnType.DateTime => ParseDateTime(value),
            FakeColumnType.DateOnly => Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$")
                ? DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null
                : throw Unsupported($"date-only value '{value}' that is not yyyy-MM-dd (not observed)"),
            FakeColumnType.Uniqueidentifier or FakeColumnType.Lookup => Guid.TryParseExact(value, "D", out var g) ? g : null,
            _ => throw Unsupported($"a condition on a {type} column")
        };
        return parsed ?? throw Fault(
            $"An exception System.FormatException was thrown while trying to convert input value '{value}' to attribute '{attribute}'. Expected type of attribute value: {type}.");
    }

    /// <summary>R7: an explicit offset or Z is honoured; otherwise the value is in the caller's time zone.</summary>
    private DateTime? ParseDateTime(string value)
    {
        var match = DateForm.Match(value);
        if (!match.Success)
            throw Unsupported($"date-time value '{value}' (only ISO yyyy-MM-dd[THH:mm[:ss]] forms are modelled)");
        var normalized = value.Replace(' ', 'T');
        if (match.Groups["offset"].Success)
            return DateTimeOffset.Parse(normalized, CultureInfo.InvariantCulture).UtcDateTime;
        var local = DateTime.Parse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return DateTime.SpecifyKind(local - _callerUtcOffset, DateTimeKind.Utc);
    }

    /// <summary>
    /// R4: the machine-independent comparison key of a string. Accents are removed (Unicode canonical decomposition),
    /// letters folded to A-Z, and the characters mapped so that ordinal order is space &lt; % &lt; [ &lt; ] &lt; _ &lt; digits &lt;
    /// letters. Any other character is refused.
    /// </summary>
    private static string StringKey(string s, bool trimEnd = true)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in (trimEnd ? s.TrimEnd(' ') : s).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(KeyChar(ch));
        }
        return sb.ToString();
    }

    private static char KeyChar(char ch) => ch switch
    {
        ' ' => '\u0001',
        '%' => '\u0002',
        '[' => '\u0003',
        ']' => '\u0004',
        '_' => '\u0005',
        >= '0' and <= '9' => ch,
        >= 'A' and <= 'Z' => ch,
        >= 'a' and <= 'z' => (char)(ch - 'a' + 'A'),
        _ => throw Unsupported($"comparing the character '{ch}' (U+{(int)ch:X4}); its collation is not observed")
    };

    /// <summary>R5: <c>%</c>, <c>_</c>, <c>[...]</c> and <c>[^...]</c> over R4 keys; trailing spaces count.</summary>
    private static Regex LikeRegex(string pattern)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            switch (ch)
            {
                case '%': regex.Append(".*"); break;
                case '_': regex.Append('.'); break;
                case '[':
                    var close = pattern.IndexOf(']', i + 1);
                    if (close < 0)
                        throw Unsupported($"an unclosed [ in LIKE pattern '{pattern}'");
                    var body = pattern.Substring(i + 1, close - i - 1);
                    var negate = body.StartsWith('^');
                    if (negate) body = body[1..];
                    if (body.Length == 0)
                        throw Unsupported($"an empty [] in LIKE pattern '{pattern}'");
                    regex.Append('[').Append(negate ? "^" : "");
                    for (var j = 0; j < body.Length; j++)
                    {
                        if (body[j] == '-' && j > 0 && j < body.Length - 1)
                            regex.Append('-');
                        else
                            regex.Append(Regex.Escape(StringKey(body[j].ToString(), trimEnd: false)).Replace("-", "\\-"));
                    }
                    regex.Append(']');
                    i = close;
                    break;
                default:
                    regex.Append(Regex.Escape(StringKey(ch.ToString(), trimEnd: false)));
                    break;
            }
        }
        regex.Append('$');
        return new Regex(regex.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private static int Compare(object actual, object value, FakeColumnType type) => type switch
    {
        FakeColumnType.String => string.CompareOrdinal(StringKey((string)actual), (string)value),
        FakeColumnType.DateOnly => ((DateTime)actual).Date.CompareTo((DateTime)value),
        FakeColumnType.DateTime => DateTime.SpecifyKind((DateTime)actual, DateTimeKind.Utc).CompareTo((DateTime)value),
        FakeColumnType.Double => Convert.ToDouble(actual, CultureInfo.InvariantCulture).CompareTo((double)value),
        FakeColumnType.Boolean => ((bool)actual).CompareTo((bool)value),
        FakeColumnType.Uniqueidentifier or FakeColumnType.Lookup => ((Guid)actual).Equals((Guid)value) ? 0 : 1,
        _ => Convert.ToDecimal(actual, CultureInfo.InvariantCulture).CompareTo((decimal)value)
    };

    // ───────────────────────────── joins (R11) ─────────────────────────────

    private static List<JoinedRow> Join(List<JoinedRow> rows, Source parent)
    {
        foreach (var link in parent.Links)
        {
            var candidates = link.Table.Rows
                .Where(r => link.Filters.All(f => Matches(f, new JoinedRow { Rows = new(StringComparer.OrdinalIgnoreCase) { [link.Alias] = r } })))
                .ToList();

            var joined = new List<JoinedRow>();
            foreach (var row in rows)
            {
                var key = Normalize(row.Rows[parent.Alias]?.GetValueOrDefault(link.To!));
                var matches = key is null ? [] : candidates.Where(c => Equals(Normalize(c.GetValueOrDefault(link.From!)), key)).ToList();
                if (matches.Count == 0)
                {
                    if (link.LinkType == "outer")
                        joined.Add(row.With(link.Alias, null));
                    continue;
                }
                joined.AddRange(matches.Select(m => row.With(link.Alias, m)));
            }
            rows = Join(joined, link);
        }
        return rows;
    }

    // ───────────────────────────── filter (R1-R5, R7) ─────────────────────────────

    private static bool Matches(Filter filter, JoinedRow row)
    {
        var results = filter.Children.Select(c => c is Filter f ? Matches(f, row) : Holds((Condition)c, row)).ToList();
        return filter.Or ? results.Count == 0 || results.Any(r => r) : results.All(r => r);
    }

    private static bool Holds(Condition c, JoinedRow row)
    {
        var actual = Normalize(row.Rows.GetValueOrDefault(c.Alias)?.GetValueOrDefault(c.Attribute));
        switch (c.Operator)
        {
            case "null": return actual is null;
            case "not-null": return actual is not null;
        }

        // R1 / R2 / R3.
        if (actual is null)
            return TrueForNull.Contains(c.Operator);

        if (LikeOperators.Contains(c.Operator))
        {
            var like = ((Regex)c.Values[0]).IsMatch(StringKey((string)actual, trimEnd: false));
            return c.Operator is "like" or "begins-with" or "ends-with" ? like : !like;
        }

        int Cmp(object value) => Compare(actual, value, c.Type);
        return c.Operator switch
        {
            "eq" => Cmp(c.Values[0]) == 0,
            "ne" => Cmp(c.Values[0]) != 0,
            "lt" => Cmp(c.Values[0]) < 0,
            "gt" => Cmp(c.Values[0]) > 0,
            "le" => Cmp(c.Values[0]) <= 0,
            "ge" => Cmp(c.Values[0]) >= 0,
            "in" => c.Values.Any(x => Cmp(x) == 0),
            "not-in" => c.Values.All(x => Cmp(x) != 0),
            "between" => Cmp(c.Values[0]) >= 0 && Cmp(c.Values[1]) <= 0,
            "not-between" => !(Cmp(c.Values[0]) >= 0 && Cmp(c.Values[1]) <= 0),
            _ => throw Unsupported($"operator {c.Operator}")
        };
    }

    // ───────────────────────────── order (R6) ─────────────────────────────

    /// <summary>The entity's own orders first, then each link-entity's, depth first (not observed).</summary>
    private static IEnumerable<Order> AllOrders(Source source)
        => source.Orders.Concat(source.Links.SelectMany(AllOrders));

    private static List<JoinedRow> Sort(List<JoinedRow> rows, List<Order> orders, IReadOnlyDictionary<string, Source> sources)
    {
        if (orders.Count == 0)
            return rows;
        IOrderedEnumerable<JoinedRow>? sorted = null;
        foreach (var order in orders)
        {
            var table = sources[order.Alias].Table;
            object? Key(JoinedRow r)
            {
                var value = Normalize(r.Rows.GetValueOrDefault(order.Alias)?.GetValueOrDefault(order.Attribute));
                // R6: a choice column orders by its label.
                if (order.Type == FakeColumnType.Choice && value is decimal d)
                    return table.Label(order.Attribute, (int)d) is { } label ? StringKey(label) : throw Unsupported($"a choice value {d} without a label");
                return value is string s ? StringKey(s) : value;
            }
            var type = order.Type == FakeColumnType.Choice ? FakeColumnType.String : order.Type;
            var comparer = Comparer<object?>.Create((a, b) => CompareForOrder(a, b, type));
            sorted = sorted is null
                ? order.Descending ? rows.OrderByDescending(Key, comparer) : rows.OrderBy(Key, comparer)
                : order.Descending ? sorted.ThenByDescending(Key, comparer) : sorted.ThenBy(Key, comparer);
        }
        return sorted!.ToList();
    }

    /// <summary>R6: NULL sorts lowest, so first ascending and last descending. Strings arrive as R4 keys.</summary>
    private static int CompareForOrder(object? a, object? b, FakeColumnType type)
    {
        if (a is null || b is null)
            return a is null ? (b is null ? 0 : -1) : 1;
        if (a is string sa && b is string sb)
            return string.CompareOrdinal(sa, sb);
        return Compare(a, b, type);
    }

    // ───────────────────────────── projection (R8, R9, R14, R15) ─────────────────────────────

    private Entity Project(JoinedRow row, Spec spec)
    {
        var root = spec.Root;
        var baseRow = row.Rows[BaseAlias]!;
        // R14: a distinct query returns no record id.
        var entity = spec.Distinct
            ? new Entity(root.Table.LogicalName)
            : new Entity(root.Table.LogicalName, (Guid)baseRow[root.Table.PrimaryIdAttribute]!);

        foreach (var source in spec.Sources.Values)
        {
            var data = row.Rows.GetValueOrDefault(source.Alias);
            var isBase = source.Alias == BaseAlias;
            var requested = source.AllAttributes
                ? source.Table.Columns.Keys.Select(name => (Name: name, Alias: (string?)null))
                : source.Attributes;

            foreach (var (name, alias) in requested)
            {
                var key = alias ?? (isBase ? name : $"{source.Alias}.{name}");
                if (entity.Attributes.ContainsKey(key))
                {
                    if (alias != null)
                        throw Fault($"Duplicate alias {alias}.");
                    continue;
                }

                var value = data?.GetValueOrDefault(name);
                if (Normalize(value) is null)
                    continue; // R8, R3
                entity[key] = isBase && alias is null ? value : new AliasedValue(source.Table.LogicalName, name, value);
                if (Formatted(source.Table, name, value!) is { } formatted)
                    entity.FormattedValues[key] = formatted;
            }
        }
        return entity;
    }

    /// <summary>R15.</summary>
    private string? Formatted(FakeTable table, string name, object value) => table.TypeOf(name) switch
    {
        FakeColumnType.String or FakeColumnType.Uniqueidentifier => null,
        FakeColumnType.Choice => table.Label(name, ((OptionSetValue)value).Value),
        FakeColumnType.Lookup => ((EntityReference)value).Name,
        FakeColumnType.Boolean => (bool)value ? "Yes" : "No",
        FakeColumnType.Integer or FakeColumnType.BigInt when Math.Abs(Convert.ToDecimal(value, CultureInfo.InvariantCulture)) < 1000
            => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("0", CultureInfo.InvariantCulture),
        FakeColumnType.DateTime => (DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc) + _callerUtcOffset)
            .ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture),
        _ => NotModelledFormattedValue
    };

    private static string Signature(Entity e)
        => string.Join("|", e.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => $"{a.Key}={(Normalize(a.Value) is string s ? StringKey(s) : Normalize(a.Value))}"));

    // ───────────────────────────── paging (R10) ─────────────────────────────

    private static EntityCollection Page(Spec spec, List<Entity> entities)
    {
        var total = entities.Count;
        List<Entity> slice;
        bool more;
        if (spec.Top is { } top)
        {
            slice = entities.Take(top).ToList();
            more = false;
        }
        else
        {
            var size = spec.Count ?? MaxPageSize;
            var number = spec.Page ?? 1;
            slice = entities.Skip((number - 1) * size).Take(size).ToList();
            more = total > number * size;
        }

        var collection = new EntityCollection(slice)
        {
            MoreRecords = more,
            PagingCookie = more ? $"<cookie page=\"{spec.Page ?? 1}\" />" : null
        };
        if (spec.ReturnTotal)
            collection.TotalRecordCount = total;
        return collection;
    }

    // ───────────────────────────── errors ─────────────────────────────

    private static NotSupportedException Unsupported(string what)
        => new($"Fake Dataverse does not model {what}; add the rule with its live observation before relying on it.");

    private static FaultException<OrganizationServiceFault> Fault(string message)
        => new(new OrganizationServiceFault { Message = message }, new FaultReason(message));
}
