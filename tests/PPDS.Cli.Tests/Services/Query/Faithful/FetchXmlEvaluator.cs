using System.Globalization;
using System.ServiceModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Evaluates a FetchXML request over <see cref="FakeTable"/> rows the way Dataverse does. Each rule says where it was
/// observed; "audit" means the OBSERVED lines of the November 2026 live SELECT audit (docs/qa/select-fidelity-audit-2026-11.md,
/// raw FetchXML sent through the SDK, not through PPDS). A rule marked "not observed" follows the FetchXML documentation
/// and has no live observation behind it yet.
/// <list type="bullet">
/// <item>R1 (audit): <c>ne</c>, <c>not-in</c>, <c>not-like</c>, <c>not-begin-with</c> and <c>not-end-with</c> are true when the
/// column is NULL.</item>
/// <item>R2 (audit): <c>not-between</c> is false when the column is NULL; so is every other operator except <c>null</c>.</item>
/// <item>R3 (audit): <c>eq ""</c> matches nothing: Dataverse stores no empty string, so the column is NULL.</item>
/// <item>R4 (audit): string comparison is case- and accent-insensitive and ignores trailing spaces; leading spaces count.</item>
/// <item>R5 (audit): <c>like</c> and <c>begins-with</c> treat <c>%</c>, <c>_</c> and <c>[...]</c> as wildcards, also in a
/// <c>begins-with</c> value. <c>ends-with</c> is taken to behave like <c>begins-with</c> (not observed).</item>
/// <item>R6 (audit): ORDER BY puts NULL first ascending and last descending; a choice column orders by its label.</item>
/// <item>R7 (audit): a date-time value without an offset is read in the caller's time zone; a date-only column compares
/// dates.</item>
/// <item>R8 (audit, cause of #1624): a NULL attribute is absent from the returned record.</item>
/// <item>R9 (not observed): an attribute with an alias comes back under the alias; a link-entity attribute without one comes
/// back as <c>linkalias.attribute</c>.</item>
/// <item>R10 (not observed): <c>top</c> and <c>count</c> limit the rows; a page holds <c>count</c> rows, 5,000 by default;
/// <c>top</c> cannot be combined with paging and cannot exceed 5,000.</item>
/// <item>R11 (not observed): link-entity inner and outer joins; a filter inside a link-entity restricts the joined rows (for an
/// outer join, the ON clause), a filter condition with <c>entityname</c> applies to the joined row.</item>
/// <item>R12 (not observed): an attribute that the table does not have is a fault.</item>
/// </list>
/// Anything else (aggregates, other operators, <c>valueof</c>, other link types, ...) throws <see cref="NotSupportedException"/>,
/// so no test can pass on a guess.
/// </summary>
internal sealed class FetchXmlEvaluator
{
    private const int MaxPageSize = 5000;

    private static readonly HashSet<string> FetchAttributes = new(StringComparer.Ordinal)
    {
        "version", "mapping", "output-format", "no-lock", "distinct", "top", "count", "page", "paging-cookie",
        "returntotalrecordcount"
    };

    /// <summary>R1: the negated operators Dataverse evaluates as true for a NULL column.</summary>
    private static readonly HashSet<string> TrueForNull = new(StringComparer.Ordinal)
    {
        "ne", "not-in", "not-like", "not-begin-with", "not-end-with"
    };

    private static readonly CompareInfo Collation = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions CollationOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    private readonly IReadOnlyDictionary<string, FakeTable> _tables;
    private readonly TimeSpan _callerUtcOffset;

    public FetchXmlEvaluator(IReadOnlyDictionary<string, FakeTable> tables, TimeSpan callerUtcOffset)
    {
        _tables = tables;
        _callerUtcOffset = callerUtcOffset;
    }

    /// <summary>A base row and, per link-entity alias, the joined row (null for an unmatched outer join).</summary>
    private sealed class JoinedRow
    {
        public required Dictionary<string, IReadOnlyDictionary<string, object?>?> Rows { get; init; }

        public JoinedRow With(string alias, IReadOnlyDictionary<string, object?>? row)
            => new() { Rows = new Dictionary<string, IReadOnlyDictionary<string, object?>?>(Rows, StringComparer.OrdinalIgnoreCase) { [alias] = row } };
    }

    private sealed record Source(string Alias, FakeTable Table, XElement Element);

    private sealed record Order(string Alias, string Attribute, bool Descending);

    private const string BaseAlias = "";

    public EntityCollection Evaluate(string fetchXml)
    {
        var fetch = XDocument.Parse(fetchXml).Root ?? throw Unsupported("an empty document");
        if (fetch.Name.LocalName != "fetch")
            throw Unsupported($"root element <{fetch.Name.LocalName}>");
        foreach (var attribute in fetch.Attributes())
        {
            if (attribute.Name.LocalName == "aggregate" && attribute.Value == "true")
                throw Unsupported("aggregate FetchXML");
            if (attribute.Name.LocalName != "aggregate" && !FetchAttributes.Contains(attribute.Name.LocalName))
                throw Unsupported($"fetch attribute {attribute.Name.LocalName}");
        }

        var entity = fetch.Elements().SingleOrDefault() ?? throw Unsupported("a fetch without exactly one <entity>");
        if (entity.Name.LocalName != "entity")
            throw Unsupported($"fetch child <{entity.Name.LocalName}>");

        var baseTable = Table((string?)entity.Attribute("name"));
        var sources = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase) { [BaseAlias] = new(BaseAlias, baseTable, entity) };
        var orders = new List<Order>();
        CheckChildren(entity, isLink: false);

        // Joins (R11), then the entity filter, then ORDER BY (R6).
        var rows = baseTable.Rows
            .Select(r => new JoinedRow { Rows = new(StringComparer.OrdinalIgnoreCase) { [BaseAlias] = r } })
            .ToList();
        rows = Join(rows, entity, BaseAlias, baseTable, sources, orders);
        foreach (var filter in entity.Elements("filter"))
            rows = rows.Where(r => Matches(filter, r, BaseAlias, sources)).ToList();

        orders.InsertRange(0, entity.Elements("order").Select(o => ReadOrder(o, BaseAlias, sources)));
        rows = Sort(rows, orders, sources);

        var entities = rows.Select(r => Project(r, sources)).ToList();
        if ((string?)fetch.Attribute("distinct") == "true")
            entities = entities.DistinctBy(Signature).ToList();

        return Page(fetch, entities);
    }

    // ───────────────────────────── shape ─────────────────────────────

    private static void CheckChildren(XElement element, bool isLink)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "attribute":
                    foreach (var a in child.Attributes())
                    {
                        if (a.Name.LocalName is not ("name" or "alias"))
                            throw Unsupported($"attribute option {a.Name.LocalName}");
                    }
                    break;
                case "all-attributes":
                case "filter":
                case "link-entity":
                    break;
                case "order":
                    if (child.Attribute("alias") != null)
                        throw Unsupported("order by alias");
                    break;
                default:
                    throw Unsupported($"<{child.Name.LocalName}> inside <{(isLink ? "link-entity" : "entity")}>");
            }
        }
    }

    private FakeTable Table(string? name)
    {
        if (name is null || !_tables.TryGetValue(name, out var table))
            throw Fault($"The entity with a name = '{name}' with namemapping = 'Logical' was not found in the MetadataCache.");
        return table;
    }

    // ───────────────────────────── joins (R11) ─────────────────────────────

    private List<JoinedRow> Join(List<JoinedRow> rows, XElement parent, string parentAlias, FakeTable parentTable,
        Dictionary<string, Source> sources, List<Order> orders)
    {
        foreach (var link in parent.Elements("link-entity"))
        {
            var table = Table((string?)link.Attribute("name"));
            var alias = (string?)link.Attribute("alias")
                ?? throw Unsupported("a link-entity without an alias (the name Dataverse generates is not modelled)");
            var from = (string?)link.Attribute("from") ?? throw Unsupported("a link-entity without from");
            var to = (string?)link.Attribute("to") ?? throw Unsupported("a link-entity without to");
            var type = (string?)link.Attribute("link-type") ?? "inner";
            if (type is not ("inner" or "outer"))
                throw Unsupported($"link-type {type}");
            foreach (var a in link.Attributes())
            {
                if (a.Name.LocalName is not ("name" or "alias" or "from" or "to" or "link-type" or "visible" or "intersect"))
                    throw Unsupported($"link-entity attribute {a.Name.LocalName}");
            }
            if (sources.ContainsKey(alias))
                throw Fault($"Duplicate link-entity alias {alias}.");
            RequireColumn(table, from);
            RequireColumn(parentTable, to);
            CheckChildren(link, isLink: true);

            sources[alias] = new Source(alias, table, link);
            var linkSources = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase) { [alias] = sources[alias] };
            var candidates = table.Rows
                .Where(r => link.Elements("filter").All(f => Matches(f, new JoinedRow { Rows = new(StringComparer.OrdinalIgnoreCase) { [alias] = r } }, alias, linkSources)))
                .ToList();

            var joined = new List<JoinedRow>();
            foreach (var row in rows)
            {
                var key = Normalize(row.Rows[parentAlias]?.GetValueOrDefault(to));
                var matches = key is null ? [] : candidates.Where(c => Equals(Normalize(c.GetValueOrDefault(from)), key)).ToList();
                if (matches.Count == 0)
                {
                    if (type == "outer")
                        joined.Add(row.With(alias, null));
                    continue;
                }
                joined.AddRange(matches.Select(m => row.With(alias, m)));
            }

            orders.AddRange(link.Elements("order").Select(o => ReadOrder(o, alias, sources)));
            rows = Join(joined, link, alias, table, sources, orders);
        }
        return rows;
    }

    // ───────────────────────────── filter (R1-R5, R7) ─────────────────────────────

    private bool Matches(XElement filter, JoinedRow row, string alias, IReadOnlyDictionary<string, Source> sources)
    {
        var type = (string?)filter.Attribute("type") ?? "and";
        if (type is not ("and" or "or"))
            throw Unsupported($"filter type {type}");
        var results = filter.Elements().Select(e => e.Name.LocalName switch
        {
            "filter" => Matches(e, row, alias, sources),
            "condition" => Condition(e, row, alias, sources),
            var other => throw Unsupported($"<{other}> inside <filter>")
        }).ToList();
        return type == "or" ? results.Count == 0 || results.Any(r => r) : results.All(r => r);
    }

    private bool Condition(XElement c, JoinedRow row, string defaultAlias, IReadOnlyDictionary<string, Source> sources)
    {
        foreach (var a in c.Attributes())
        {
            if (a.Name.LocalName is not ("attribute" or "operator" or "value" or "entityname"))
                throw Unsupported($"condition attribute {a.Name.LocalName}");
        }

        var alias = (string?)c.Attribute("entityname") ?? defaultAlias;
        if (!sources.TryGetValue(alias, out var source))
        {
            // entityname may name the base entity itself.
            source = sources.Values.FirstOrDefault(s => s.Alias == BaseAlias && string.Equals(s.Table.LogicalName, alias, StringComparison.OrdinalIgnoreCase))
                ?? throw Fault($"Link entity with name or alias {alias} is not found.");
            alias = source.Alias;
        }
        var attribute = (string?)c.Attribute("attribute") ?? throw Fault("A condition has no attribute.");
        var op = (string?)c.Attribute("operator") ?? throw Fault("A condition has no operator.");
        var type = RequireColumn(source.Table, attribute);
        var actual = Normalize(row.Rows.GetValueOrDefault(alias)?.GetValueOrDefault(attribute));
        var values = c.Attribute("value") is { } v ? new[] { v.Value } : c.Elements("value").Select(x => x.Value).ToArray();

        switch (op)
        {
            case "null": return actual is null;
            case "not-null": return actual is not null;
        }

        if (!IsKnownOperator(op))
            throw Unsupported($"operator {op}");

        if (type is FakeColumnType.Uniqueidentifier or FakeColumnType.Lookup && op is not ("eq" or "ne" or "in" or "not-in"))
            throw Unsupported($"operator {op} on a {type} column");

        // R1 / R2 / R3.
        if (actual is null)
            return TrueForNull.Contains(op);

        int Cmp(string value) => Compare(actual, Parse(value, type), type);
        string One() => values.Length == 1 ? values[0] : throw Fault($"Operator {op} expects one value, got {values.Length}.");

        return op switch
        {
            "eq" => Cmp(One()) == 0,
            "ne" => Cmp(One()) != 0,
            "lt" => Cmp(One()) < 0,
            "gt" => Cmp(One()) > 0,
            "le" => Cmp(One()) <= 0,
            "ge" => Cmp(One()) >= 0,
            "in" => values.Length > 0 ? values.Any(x => Cmp(x) == 0) : throw Fault("Operator in expects values."),
            "not-in" => values.Length > 0 ? values.All(x => Cmp(x) != 0) : throw Fault("Operator not-in expects values."),
            "between" => Between(values, Cmp),
            "not-between" => !Between(values, Cmp),
            "like" => Like(actual, One(), type),
            "not-like" => !Like(actual, One(), type),
            "begins-with" => Like(actual, One() + "%", type),
            "not-begin-with" => !Like(actual, One() + "%", type),
            "ends-with" => Like(actual, "%" + One(), type),
            "not-end-with" => !Like(actual, "%" + One(), type),
            _ => throw Unsupported($"operator {op}")
        };
    }

    private static bool IsKnownOperator(string op) => op is "eq" or "ne" or "lt" or "gt" or "le" or "ge" or "in" or "not-in"
        or "between" or "not-between" or "like" or "not-like" or "begins-with" or "not-begin-with" or "ends-with" or "not-end-with";

    private static bool Between(string[] values, Func<string, int> cmp)
        => values.Length == 2 ? cmp(values[0]) >= 0 && cmp(values[1]) <= 0 : throw Fault("Operator between expects two values.");

    /// <summary>R5: <c>%</c>, <c>_</c>, <c>[...]</c> and <c>[^...]</c>, matched with the R4 collation.</summary>
    private static bool Like(object actual, string pattern, FakeColumnType type)
    {
        if (type != FakeColumnType.String)
            throw Unsupported($"like on a {type} column");
        var regex = new StringBuilder("^");
        var folded = Fold(pattern);
        for (var i = 0; i < folded.Length; i++)
        {
            var ch = folded[i];
            switch (ch)
            {
                case '%': regex.Append(".*"); break;
                case '_': regex.Append('.'); break;
                case '[':
                    var close = folded.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        regex.Append(Regex.Escape("["));
                        break;
                    }
                    var body = folded.Substring(i + 1, close - i - 1);
                    var negate = body.StartsWith('^');
                    if (negate) body = body[1..];
                    regex.Append('[').Append(negate ? "^" : "");
                    for (var j = 0; j < body.Length; j++)
                    {
                        if (body[j] == '-' && j > 0 && j < body.Length - 1) regex.Append('-');
                        else regex.Append(Regex.Escape(body[j].ToString()).Replace("]", "\\]").Replace("-", "\\-"));
                    }
                    regex.Append(']');
                    i = close;
                    break;
                default: regex.Append(Regex.Escape(ch.ToString())); break;
            }
        }
        regex.Append('$');
        return Regex.IsMatch(Fold((string)actual), regex.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    /// <summary>R4 for LIKE: upper case without diacritics.</summary>
    private static string Fold(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    // ───────────────────────────── values ─────────────────────────────

    private static FakeColumnType RequireColumn(FakeTable table, string attribute)
        => table.HasColumn(attribute)
            ? table.TypeOf(attribute)
            : throw Fault($"'{table.LogicalName}' entity doesn't contain attribute with Name = '{attribute}'.");

    /// <summary>SDK value to a comparable value: Money, OptionSetValue and EntityReference to their inner value.</summary>
    private static object? Normalize(object? value) => value switch
    {
        null => null,
        Money m => m.Value,
        OptionSetValue o => (decimal)o.Value,
        EntityReference r => r.Id,
        AliasedValue a => Normalize(a.Value),
        int or long or short or decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        DateTime d => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d,
        string s => s.Length == 0 ? null : s,
        _ => value
    };

    private object Parse(string value, FakeColumnType type)
    {
        try
        {
            return type switch
            {
                FakeColumnType.String => value,
                FakeColumnType.Integer or FakeColumnType.BigInt or FakeColumnType.Decimal or FakeColumnType.Money or FakeColumnType.Choice
                    => decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
                FakeColumnType.Double => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
                FakeColumnType.Boolean => value switch
                {
                    "1" or "true" => true,
                    "0" or "false" => false,
                    _ => throw new FormatException()
                },
                FakeColumnType.DateTime => ParseDateTime(value),
                FakeColumnType.DateOnly => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None).Date,
                FakeColumnType.Uniqueidentifier or FakeColumnType.Lookup => Guid.Parse(value),
                _ => throw Unsupported($"a condition on a {type} column")
            };
        }
        catch (FormatException)
        {
            throw Fault($"Invalid value '{value}' for a {type} column.");
        }
    }

    /// <summary>R7: an explicit offset or Z is honoured; otherwise the value is in the caller's time zone.</summary>
    private DateTime ParseDateTime(string value)
    {
        var hasOffset = Regex.IsMatch(value.Trim(), @"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase);
        if (hasOffset)
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).UtcDateTime;
        var local = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return DateTime.SpecifyKind(local - _callerUtcOffset, DateTimeKind.Utc);
    }

    private static int Compare(object actual, object value, FakeColumnType type) => type switch
    {
        // R4: case- and accent-insensitive, trailing spaces ignored, leading spaces significant.
        FakeColumnType.String => Collation.Compare(((string)actual).TrimEnd(' '), ((string)value).TrimEnd(' '), CollationOptions),
        FakeColumnType.DateOnly => ((DateTime)actual).Date.CompareTo((DateTime)value),
        FakeColumnType.DateTime => DateTime.SpecifyKind((DateTime)actual, DateTimeKind.Utc).CompareTo((DateTime)value),
        FakeColumnType.Double => Convert.ToDouble(actual, CultureInfo.InvariantCulture).CompareTo((double)value),
        FakeColumnType.Boolean => ((bool)actual).CompareTo((bool)value),
        FakeColumnType.Uniqueidentifier or FakeColumnType.Lookup => ((Guid)actual).Equals((Guid)value) ? 0 : 1,
        _ => Convert.ToDecimal(actual, CultureInfo.InvariantCulture).CompareTo((decimal)value)
    };

    // ───────────────────────────── order (R6) ─────────────────────────────

    private static Order ReadOrder(XElement order, string alias, IReadOnlyDictionary<string, Source> sources)
    {
        var entityName = (string?)order.Attribute("entityname");
        if (entityName != null)
            alias = entityName;
        var attribute = (string?)order.Attribute("attribute") ?? throw Fault("An order has no attribute.");
        if (!sources.TryGetValue(alias, out var source))
            throw Fault($"Link entity with name or alias {alias} is not found.");
        RequireColumn(source.Table, attribute);
        return new Order(alias, attribute, (string?)order.Attribute("descending") == "true");
    }

    private static List<JoinedRow> Sort(List<JoinedRow> rows, List<Order> orders, IReadOnlyDictionary<string, Source> sources)
    {
        if (orders.Count == 0)
            return rows;
        IOrderedEnumerable<JoinedRow>? sorted = null;
        foreach (var order in orders)
        {
            var table = sources[order.Alias].Table;
            var type = table.TypeOf(order.Attribute);
            object? Key(JoinedRow r)
            {
                var raw = r.Rows.GetValueOrDefault(order.Alias)?.GetValueOrDefault(order.Attribute);
                var value = Normalize(raw);
                // R6: a choice column orders by its label.
                if (type == FakeColumnType.Choice && value is decimal d && table.Label(order.Attribute, (int)d) is { } label)
                    return label;
                return value;
            }
            var comparer = Comparer<object?>.Create((a, b) => CompareForOrder(a, b, type));
            sorted = sorted is null
                ? order.Descending ? rows.OrderByDescending(Key, comparer) : rows.OrderBy(Key, comparer)
                : order.Descending ? sorted.ThenByDescending(Key, comparer) : sorted.ThenBy(Key, comparer);
        }
        return sorted!.ToList();
    }

    /// <summary>R6: NULL sorts lowest, so first ascending and last descending.</summary>
    private static int CompareForOrder(object? a, object? b, FakeColumnType type)
    {
        if (a is null || b is null)
            return a is null ? (b is null ? 0 : -1) : 1;
        if (a is string sa && b is string sb)
            return Collation.Compare(sa.TrimEnd(' '), sb.TrimEnd(' '), CollationOptions);
        return Compare(a, b, type);
    }

    // ───────────────────────────── projection (R8, R9) ─────────────────────────────

    private static Entity Project(JoinedRow row, IReadOnlyDictionary<string, Source> sources)
    {
        var baseSource = sources[BaseAlias];
        var baseRow = row.Rows[BaseAlias]!;
        var entity = new Entity(baseSource.Table.LogicalName, (Guid)baseRow[baseSource.Table.PrimaryIdAttribute]!);

        foreach (var source in sources.Values)
        {
            var data = row.Rows.GetValueOrDefault(source.Alias);
            var isBase = source.Alias == BaseAlias;
            var requested = source.Element.Element("all-attributes") != null
                ? source.Table.Columns.Keys.Select(name => (Name: name, Alias: (string?)null))
                : source.Element.Elements("attribute").Select(a => (Name: (string)a.Attribute("name")!, Alias: (string?)a.Attribute("alias")));

            foreach (var (name, alias) in requested)
            {
                RequireColumn(source.Table, name);
                var key = alias ?? (isBase ? name : $"{source.Alias}.{name}");
                if (entity.Attributes.ContainsKey(key))
                {
                    if (alias != null)
                        throw Fault($"Duplicate alias {alias}.");
                    continue;
                }

                var value = data?.GetValueOrDefault(name);
                if (value is null || value is string { Length: 0 })
                    continue; // R8
                entity[key] = isBase && alias is null ? value : new AliasedValue(source.Table.LogicalName, name, value);

                var formatted = value switch
                {
                    OptionSetValue o => source.Table.Label(name, o.Value),
                    EntityReference r => r.Name,
                    _ => null
                };
                if (formatted != null)
                    entity.FormattedValues[key] = formatted;
            }
        }
        return entity;
    }

    private static string Signature(Entity e)
        => string.Join("|", e.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key}={Normalize(a.Value)}"));

    // ───────────────────────────── paging (R10) ─────────────────────────────

    private static EntityCollection Page(XElement fetch, List<Entity> entities)
    {
        var top = (string?)fetch.Attribute("top");
        var count = (string?)fetch.Attribute("count");
        var page = (string?)fetch.Attribute("page");
        var total = entities.Count;

        List<Entity> slice;
        bool more;
        if (top != null)
        {
            if (count != null || page != null)
                throw Fault("The top attribute can't be specified with paging attribute page or count.");
            var n = int.Parse(top, CultureInfo.InvariantCulture);
            if (n > MaxPageSize)
                throw Fault($"The top attribute must be at most {MaxPageSize}.");
            slice = entities.Take(n).ToList();
            more = false;
        }
        else
        {
            var size = count != null ? int.Parse(count, CultureInfo.InvariantCulture) : MaxPageSize;
            var number = page != null ? int.Parse(page, CultureInfo.InvariantCulture) : 1;
            slice = entities.Skip((number - 1) * size).Take(size).ToList();
            more = total > number * size;
        }

        var collection = new EntityCollection(slice)
        {
            MoreRecords = more,
            PagingCookie = more ? $"<cookie page=\"{(page != null ? int.Parse(page, CultureInfo.InvariantCulture) : 1)}\" />" : null
        };
        if ((string?)fetch.Attribute("returntotalrecordcount") == "true")
            collection.TotalRecordCount = total;
        return collection;
    }

    // ───────────────────────────── errors ─────────────────────────────

    private static NotSupportedException Unsupported(string what)
        => new($"Fake Dataverse does not model {what}; add the rule with its live observation before relying on it.");

    private static FaultException<OrganizationServiceFault> Fault(string message)
        => new(new OrganizationServiceFault { Message = message }, new FaultReason(message));
}
