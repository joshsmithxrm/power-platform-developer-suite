using System.Globalization;
using System.ServiceModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Evaluates a FetchXML request over <see cref="FakeTable"/> rows the way Dataverse does, in a closed world: only shapes and
/// value forms with an observation behind them in the November 2026 live SELECT audit (docs/qa/select-fidelity-audit-2026-11.md
/// on the test/select-fidelity-live branch) are evaluated. Everything else is refused with "not observed: ..." before any row
/// is read (<see cref="NotSupportedException"/>), so a test that depends on it fails instead of passing on a guess. "OBSERVED"
/// cites the audit's raw-FetchXML lines (replayed by <c>AuditObservedReplayTests</c>); a case id (W15, D05, ...) is a statement
/// run through PPDS whose Dataverse behaviour the audit recorded. A behaviour is either cited here or refused.
/// <list type="bullet">
/// <item>R1 (OBSERVED): <c>ne</c>, <c>not-in</c>, <c>not-like</c>, <c>not-begin-with</c>, <c>not-end-with</c> are true for a NULL
/// column.</item>
/// <item>R2: <c>not-between</c> is false for NULL (OBSERVED); so is every other operator except <c>null</c> (W-cases through
/// PPDS that the audit marks correct, for example W14 <c>eq</c>; not observed operator by operator in raw FetchXML).</item>
/// <item>R3 (OBSERVED): <c>eq ""</c> matches nothing; a stored empty string reads back as NULL ("stored string values"); W25:
/// <c>ne ""</c> matches every row.</item>
/// <item>R4 (OBSERVED): strings compare case- and accent-insensitive, trailing spaces ignored, leading spaces significant. Order
/// is a fixed key (no ICU/NLS data) that reproduces "order by name asc"; a comparison first decided at a pair that line does not
/// order (space/<c>%</c>, <c>_</c>/digit, <c>]</c>, apostrophe) is refused. In an equality, plain ASCII punctuation matches only
/// itself (X01 <c>@n</c>); any other character is refused.</item>
/// <item>R5: <c>%</c>, <c>_</c>, <c>[...]</c> wildcards in <c>like</c> and <c>begins-with</c> (OBSERVED); <c>[^...]</c> and ranges
/// (W63, W79); trailing spaces count (W69). <c>ends-with</c> is refused (only <c>not-end-with</c> is observed).</item>
/// <item>R6 (OBSERVED): NULL first ascending, last descending; a choice orders by label; a second key (O01, O02, T03). An
/// order inside a link-entity and ORDER BY a lookup or id are refused. Rows that tie on every key keep table order: not
/// observed, and listed as an open decision (refusing ties would refuse valid statements).</item>
/// <item>R7: a date-time without an offset is read in the caller's time zone (OBSERVED, UTC−6); a date-only column compares
/// dates (OBSERVED). Text of digits only or a variable name is a Dataverse fault (W104 <c>'20240115'</c>, X05 <c>@d</c>). Any
/// other form, and an ISO form that is not a calendar date, is refused.</item>
/// <item>R8 (cause of #1624): a NULL attribute is absent from the record.</item>
/// <item>R9 (PRJ04, J01-J03 through PPDS): an aliased attribute comes back under its alias; a link-entity attribute as
/// <c>alias.attribute</c>. A repeated unaliased attribute comes back once (PRJ12); any other repeated key is refused.</item>
/// <item>R10 (T01, T03, T04, PG01-PG04 through PPDS): <c>top</c>, <c>count</c> and <c>page</c> as the engine writes them
/// (canonical, at most 5,000; 0 returns no row, T04); anything else, and <c>top</c> with paging, is refused.</item>
/// <item>R11 (J01-J03, J05-J07 through PPDS): inner and outer link-entities joined on unique-identifier or lookup columns;
/// <c>entityname</c> on a base condition names a link alias; NULL never joins. A filter inside a link-entity, other keys, other
/// link types, an inner link under an outer one and <c>entityname</c> anywhere else are refused.</item>
/// <item>R12 (O03, J21, S06-S08): an attribute the table does not have is a fault; names are case-sensitive (O03).</item>
/// <item>R13: a decimal point on a whole-number column is a Dataverse fault (W15). An empty value on a whole-number column is
/// unknown for <c>in</c>, <c>not-in</c> and <c>between</c> (W51, W52, W136, W137). Values in any other form are refused.</item>
/// <item>R14 (D05, PG08): <c>distinct</c> compares strings by the R4 key and numbers by value, and returns no record id; which
/// case variant is kept is not observed.</item>
/// <item>R15: formatted values: choice label (O07), whole number digits below 1,000 (PRJ01), <c>Yes</c>/<c>No</c> (O09), date-time
/// in the caller's zone (OBSERVED), lookup name (V11, V12; a fixture lookup without a name is refused). Money, decimal, float,
/// date-only and whole numbers of 1,000 or more carry <see cref="NotModelledFormattedValue"/>.</item>
/// </list>
/// <item>R16 (from the per-case replay, <c>AuditCaseReplayTests</c>): rows that nothing else orders come back in SQL Server
/// uniqueidentifier order of the primary key, which also breaks ties (the unordered outputs, for example W54, D01, PRJ01);
/// <c>distinct</c> keeps the first-created of equal rows and sorts by the returned columns (D05, D06, PG08); whole numbers
/// format with group separators (PG01, PG03 <c>'4,640'</c>); text a whole number cannot be read from is the FormatException
/// fault (W15, E06, X02); <c>eq ""</c> on a whole number matches nothing (W26); <c>ends-with</c> (W57), a braced GUID (W114),
/// <c>'true'</c> on a two-option (W118), a memo line break (W125, W126), <c>top</c>/<c>count</c> 0 (T04), a join on whole
/// numbers (J12) and an outer link with empty from/to (J08) are as observed; an unknown table (C01, E07), an invalid alias
/// (PRJ18) and groupby outside an aggregate (E14, A27) are the observed faults; the virtual <c>&lt;column&gt;name</c> of a
/// choice or two-option comes back empty (V04, V09), matches the label in <c>eq</c> (V08) and orders by label (V09); system
/// <c>statecode</c>/<c>statuscode</c> are in the dataset (V05). Aggregates: see <see cref="ValidateAggregate"/>.</item>
/// </list>
/// Every other operator, <c>valueof</c>, empty filters and every unlisted element, attribute or value are refused.
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

    private sealed record Condition(string Alias, string Attribute, FakeColumnType Type, string Operator, object[] Values, FakeTable? Table = null);

    private sealed record Order(string Alias, string Attribute, FakeColumnType Type, bool Descending);

    private sealed record Spec(Source Root, Dictionary<string, Source> Sources, bool Distinct, int? Top, int? Count, int? Page, bool ReturnTotal)
    {
        /// <summary>The aggregate attributes in document order (<c>aggregate="true"</c> only).</summary>
        public List<AggregateAttribute>? Aggregates { get; init; }
    }

    private sealed record AggregateAttribute(Source Source, string Name, string Alias, FakeColumnType Type, string? Function, bool Distinct);

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

        if (spec.Aggregates != null)
            return EvaluateAggregate(spec, rows);
        var orders = AllOrders(spec.Root).ToList();
        if (!spec.Distinct)
            return Page(spec, Sort(rows, orders, spec.Sources).Select(r => Project(r, spec)).ToList());

        // R14 distinct (audit D05, D06, PG08): of rows that are the same, the first created is kept (D06 returns 'A', not
        // 'a'); with no ORDER BY the result is sorted by the returned columns in order, NULL first (D05, D06).
        var created = spec.Root.Table.Rows.Select((r, i) => (Row: (object)r, Index: i)).ToDictionary(x => x.Row, x => x.Index, ReferenceEqualityComparer.Instance);
        var firstCreated = rows.OrderBy(r => created[r.Rows[BaseAlias]!]).ToList();
        var kept = Sort(firstCreated, orders, spec.Sources, tieBreakById: false)
            .Select(r => Project(r, spec)).DistinctBy(Signature).ToList();
        if (orders.Count == 0)
        {
            var columns = spec.Root.AllAttributes ? spec.Root.Table.Columns.Keys.ToList() : spec.Root.Attributes.Select(a => a.Alias ?? a.Name).ToList();
            IOrderedEnumerable<Entity>? sorted = null;
            foreach (var column in columns)
            {
                object? Key(Entity e) => e.Attributes.TryGetValue(column, out var v) && Normalize(v) is { } n ? n is string str ? StringKey(str, ordered: true) : n : null;
                var comparer = Comparer<object?>.Create((a, b) => CompareForOrder(a, b, FakeColumnType.Decimal));
                sorted = sorted is null ? kept.OrderBy(Key, comparer) : sorted.ThenBy(Key, comparer);
            }
            if (sorted != null)
                kept = Unwrap(() => sorted.ToList());
        }
        return Page(spec, kept);
    }

    // ───────────────────────────── validation (before any row) ─────────────────────────────

    private Spec Validate(XDocument doc)
    {
        var fetch = doc.Root ?? throw Unsupported("an empty document");
        Expect(fetch, "fetch");
        AllowAttributes(fetch, "mapping", "distinct", "no-lock", "top", "count", "page", "paging-cookie", "returntotalrecordcount", "aggregate");
        if (fetch.Attribute("mapping") is { } mapping && mapping.Value != "logical")
            throw Unsupported($"mapping=\"{mapping.Value}\"");
        var isAggregate = fetch.Attribute("aggregate") is { } aggregate && Bool(aggregate);
        _ = fetch.Attribute("no-lock") is { } noLock && Bool(noLock);
        NoText(fetch);

        var children = fetch.Elements().ToList();
        if (children.Count != 1)
            throw Unsupported("a fetch without exactly one child element");
        var entity = children[0];
        Expect(entity, "entity");
        AllowAttributes(entity, "name");

        var sources = new Dictionary<string, Source>(StringComparer.Ordinal);
        var root = new Source { Alias = BaseAlias, Table = Table((string?)entity.Attribute("name")), Element = entity };
        sources[BaseAlias] = root;
        RegisterLinks(root, sources);
        var aggregates = isAggregate ? ValidateAggregate(fetch, root, sources) : null;
        _aggregateMode = isAggregate;
        ValidateContent(root, sources);
        CheckResultKeys(sources);

        // TOP 0 returns no row (audit T04).
        var top = PositiveInt(fetch, "top", min: 0);
        var count = PositiveInt(fetch, "count", min: 0);
        var page = PositiveInt(fetch, "page", max: int.MaxValue);
        if (top != null && (count != null || page != null))
            throw Unsupported("top together with page or count");

        var distinct = fetch.Attribute("distinct") is { } d && Bool(d);
        if (isAggregate && (distinct || top != null || count != null || page is > 1 || root.Links.Any(l => l.Orders.Count > 0) || root.Orders.Count > 0))
            throw Unsupported("an aggregate fetch with distinct, top, count, paging or orders");
        return new Spec(root, sources, distinct, top, count, page,
            fetch.Attribute("returntotalrecordcount") is { } t && Bool(t)) { Aggregates = aggregates };
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
                throw Unsupported($"a duplicate or empty link-entity alias '{alias}'");
            if (from.Length == 0 && to.Length == 0)
            {
                // Audit J08: an outer link with empty from and to returns every parent row with the link columns NULL.
                if (type != "outer")
                    throw Unsupported("an inner link-entity with empty from and to");
            }
            else
            {
                var fromType = RequireColumn(table, from);
                var toType = RequireColumn(parent.Table, to);
                var guids = fromType is FakeColumnType.Uniqueidentifier or FakeColumnType.Lookup && toType is FakeColumnType.Uniqueidentifier or FakeColumnType.Lookup;
                var wholes = fromType == FakeColumnType.Integer && toType == FakeColumnType.Integer; // audit J12
                if (!guids && !wholes)
                    throw Unsupported($"a link-entity joined on {fromType} and {toType} columns ({from}, {to})");
            }

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
                case "attribute" when _aggregateMode:
                    break; // checked by ValidateAggregate
                case "attribute":
                    AllowAttributes(child, "name", "alias", "groupby");
                    NoChildren(child);
                    if (child.Attribute("groupby") is not null)
                        throw Fault($"groupby cannot be specified if aggreate is not true. NodeXml: {child.ToString(SaveOptions.DisableFormatting)}"); // audit E14, A27
                    var name = (string?)child.Attribute("name") ?? throw Unsupported("an <attribute> without a name");
                    ColumnOrVirtual(source.Table, name);
                    var alias = (string?)child.Attribute("alias");
                    if (alias is { Length: 0 })
                        throw Unsupported("an empty attribute alias");
                    if (alias is not null && !Regex.IsMatch(alias, "^[A-Za-z_][A-Za-z0-9_]*$"))
                        throw Fault($"Invalid character specified for alias: {alias}. Only characters within the ranges [A-Z], [a-z] or [0-9] or _ are allowed.  The first character may only be in the ranges [A-Z], [a-z] or _."); // audit PRJ18 (observed up to "[A-Z], [")
                    source.Attributes.Add((name, alias));
                    break;
                case "all-attributes":
                    AllowAttributes(child);
                    NoChildren(child);
                    source.AllAttributes = true;
                    break;
                case "filter":
                    if (source.Alias != BaseAlias)
                        throw Unsupported("a <filter> inside a link-entity");
                    // (Kept for when a link filter is observed: it would see only that link's columns.)
                    var scope = source.Alias == BaseAlias
                        ? sources
                        : new Dictionary<string, Source>(StringComparer.Ordinal) { [source.Alias] = source };
                    source.Filters.Add(ReadFilter(child, source.Alias, scope));
                    break;
                case "order":
                    if (source.Alias != BaseAlias)
                        throw Unsupported("an <order> inside a link-entity");
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

    /// <summary>
    /// The keys the record will carry must be unique. A repeated unaliased attribute is returned once (audit PRJ12); an
    /// alias used twice, or equal to another attribute's key, is not observed.
    /// </summary>
    private static void CheckResultKeys(Dictionary<string, Source> sources)
    {
        var keys = new Dictionary<string, (string Alias, string Name)>(StringComparer.Ordinal);
        foreach (var source in sources.Values)
        {
            foreach (var (name, alias) in source.Attributes)
            {
                var key = alias ?? (source.Alias == BaseAlias ? name : $"{source.Alias}.{name}");
                if (keys.TryGetValue(key, out var seen))
                {
                    if (alias != null || seen != (source.Alias, name))
                        throw Unsupported($"two attributes returned under the same key '{key}'");
                    continue;
                }
                keys[key] = (source.Alias, name);
            }
        }
    }

    private bool _aggregateMode;

    /// <summary>
    /// Aggregate FetchXML, as the audit observed it through the engine. Faults, checked over every attribute in document order
    /// before anything else: an attribute without a name (A17), one that is neither groupby nor aggregate (A15, A21, WF04),
    /// one without an alias (A08, A09, A23), MIN or MAX of text (A06) or of a choice (A25). Modelled: count, countcolumn
    /// (also distinct, by the R4 collation: A22, A24), sum, avg, min and max of whole numbers, decimals, money, floats and
    /// date-times (A02-A05, A07, A20, PG07), groupby on base and link columns (J16, S12). Anything else is refused.
    /// </summary>
    private List<AggregateAttribute> ValidateAggregate(XElement fetch, Source root, Dictionary<string, Source> sources)
    {
        var bySource = sources.Values.ToDictionary(s => s.Element, s => s);
        var found = new List<(XElement Element, Source Source)>();
        foreach (var attribute in fetch.Descendants("attribute"))
        {
            if (attribute.Parent is null || !bySource.TryGetValue(attribute.Parent, out var source))
                throw Unsupported("an <attribute> outside an entity or link-entity");
            var node = attribute.ToString(SaveOptions.DisableFormatting);
            if (attribute.Attribute("name") is null)
                throw Fault($"Attribute: name not found in XNode: {node}");
            if (attribute.Attribute("aggregate") is null && attribute.Attribute("groupby") is null)
                throw Fault($"An attribute can not be requested when an aggregate operation has been specified and its neither groupby nor aggregate.  NodeXml : {node}");
            if (attribute.Attribute("alias") is null)
                throw Fault($"An alias must be specified for every attribute in an aggregate query. NodeXml: {node}");
            found.Add((attribute, source));
        }

        var result = new List<AggregateAttribute>();
        foreach (var (attribute, source) in found)
        {
            AllowAttributes(attribute, "name", "alias", "aggregate", "groupby", "distinct");
            NoChildren(attribute);
            var name = (string)attribute.Attribute("name")!;
            var alias = (string)attribute.Attribute("alias")!;
            var type = RequireColumn(source.Table, name);
            var function = (string?)attribute.Attribute("aggregate");
            var groupby = attribute.Attribute("groupby") is { } g && Bool(g);
            var distinct = attribute.Attribute("distinct") is { } d && Bool(d);
            if (groupby == (function != null))
                throw Unsupported("an attribute that is both groupby and aggregate");
            if (distinct && function != "countcolumn")
                throw Unsupported($"distinct on aggregate {function}");
            if (groupby && type is not (FakeColumnType.Integer or FakeColumnType.String or FakeColumnType.Choice or FakeColumnType.Boolean or FakeColumnType.Uniqueidentifier or FakeColumnType.Lookup))
                throw Unsupported($"groupby on a {type} column");
            if (function is "min" or "max" && type is FakeColumnType.String or FakeColumnType.Choice)
                throw Fault($"Aggregate MAX or MIN is not supported for attribute of type {(type == FakeColumnType.String ? "nvarchar" : "picklist")}.");
            switch (function)
            {
                case null or "count" or "countcolumn":
                    break;
                case "sum" or "avg" when type is FakeColumnType.Integer or FakeColumnType.Decimal or FakeColumnType.Money or FakeColumnType.Double:
                case "min" or "max" when type is FakeColumnType.Integer or FakeColumnType.Decimal or FakeColumnType.Money or FakeColumnType.Double or FakeColumnType.DateTime:
                    break;
                default:
                    throw Unsupported($"aggregate {function} on a {type} column");
            }
            if (!Regex.IsMatch(alias, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw Unsupported($"the aggregate alias '{alias}'");
            result.Add(new AggregateAttribute(source, name, alias, type, function, distinct));
        }
        if (result.Select(a => a.Alias).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw Unsupported("two aggregate attributes with the same alias");
        return result;
    }

    private EntityCollection EvaluateAggregate(Spec spec, List<JoinedRow> rows)
    {
        var groupBy = spec.Aggregates!.Where(a => a.Function is null).ToList();
        object? Raw(JoinedRow r, AggregateAttribute a) => r.Rows.GetValueOrDefault(a.Source.Alias)?.GetValueOrDefault(a.Name);
        object? GroupKey(object? raw) => Normalize(raw) is string s ? StringKey(s) : Normalize(raw);

        var groups = rows.GroupBy(r => string.Join("|", groupBy.Select(a => Convert.ToString(GroupKey(Raw(r, a)), CultureInfo.InvariantCulture) ?? "\u0000")))
            .Select(g => g.ToList()).ToList();
        if (groupBy.Count == 0 && groups.Count == 0)
            groups.Add([]); // no groupby: one row, also over no rows (A10)

        // Groups come back ordered by their groupby values like ORDER BY (choice by label, NULL first): S12, J16.
        IOrderedEnumerable<List<JoinedRow>>? ordered = null;
        foreach (var a in groupBy)
        {
            object? Key(List<JoinedRow> g)
            {
                var value = Normalize(Raw(g[0], a));
                if (a.Type == FakeColumnType.Choice && value is decimal d)
                    return a.Source.Table.Label(a.Name, (int)d) is { } label ? StringKey(label, ordered: true) : null;
                return value is string s ? StringKey(s, ordered: true) : value;
            }
            var type = a.Type == FakeColumnType.Choice ? FakeColumnType.String : a.Type;
            var comparer = Comparer<object?>.Create((x, y) => CompareForOrder(x, y, type));
            ordered = ordered is null ? groups.OrderBy(Key, comparer) : ordered.ThenBy(Key, comparer);
        }
        if (ordered != null)
            groups = Unwrap(() => ordered.ToList());

        var entities = new List<Entity>();
        foreach (var group in groups)
        {
            var entity = new Entity(spec.Root.Table.LogicalName);
            foreach (var a in spec.Aggregates!)
            {
                var value = a.Function is null ? Raw(group[0], a) : Aggregate(a, group.Select(r => Raw(r, a)).ToList(), group.Count);
                if (Normalize(value) is null)
                    continue;
                entity[a.Alias] = new AliasedValue(a.Source.Table.LogicalName, a.Name, value);
                var formatted = a.Function switch
                {
                    null => Formatted(a.Source.Table, a.Name, value!),
                    "count" or "countcolumn" => ((int)value!).ToString("#,##0", CultureInfo.InvariantCulture), // S12 '8'
                    _ => NotModelledFormattedValue
                };
                if (formatted != null)
                    entity.FormattedValues[a.Alias] = formatted;
            }
            entities.Add(entity);
        }
        return Page(spec, entities);
    }

    private static object? Aggregate(AggregateAttribute a, List<object?> raw, int rowCount)
    {
        var values = raw.Where(v => Normalize(v) is not null).ToList();
        switch (a.Function)
        {
            case "count":
                return rowCount;
            case "countcolumn":
                return a.Distinct
                    ? values.Select(v => Normalize(v) is string s ? StringKey(s) : Normalize(v)).Distinct().Count() // A22, A24
                    : values.Count;
        }
        if (values.Count == 0)
        {
            // Over no rows Dataverse returns 0 for sum and max of a whole number (A10); nothing else is observed.
            return a.Function is "sum" or "max" && a.Type == FakeColumnType.Integer
                ? 0
                : throw Unsupported($"aggregate {a.Function} of a {a.Type} column over no rows");
        }
        if (a.Type == FakeColumnType.DateTime)
        {
            var dates = values.Cast<DateTime>().ToList();
            return a.Function == "min" ? dates.Min() : dates.Max();
        }
        if (a.Type == FakeColumnType.Double)
        {
            var d = values.Select(v => Convert.ToDouble(Normalize(v), CultureInfo.InvariantCulture)).ToList();
            return a.Function switch { "sum" => d.Sum(), "avg" => d.Average(), "min" => d.Min(), _ => d.Max() };
        }
        var n = values.Select(v => (decimal)Normalize(v)!).ToList();
        var result = a.Function switch
        {
            "sum" => n.Sum(),
            // The average of a whole number is truncated to a whole number (A02: 166 / 30 = 5).
            "avg" => a.Type == FakeColumnType.Integer ? decimal.Truncate(n.Sum() / n.Count) : n.Sum() / n.Count,
            "min" => n.Min(),
            _ => n.Max()
        };
        return a.Type switch
        {
            FakeColumnType.Integer => (object)(int)result,
            FakeColumnType.Money => new Money(result),
            _ => result
        };
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
        if (children.Count == 0)
            throw Unsupported("an empty <filter>");
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

        if (c.Attribute("entityname") is { } en && (defaultAlias != BaseAlias || en.Value == BaseAlias))
            throw Unsupported("entityname on a condition inside a link-entity, or an empty entityname");
        var source = ResolveSource((string?)c.Attribute("entityname") ?? defaultAlias, sources);
        var attribute = (string?)c.Attribute("attribute") ?? throw Unsupported("a <condition> without an attribute");
        var op = (string?)c.Attribute("operator") ?? throw Unsupported("a <condition> without an operator");
        var type = ColumnOrVirtual(source.Table, attribute);
        var raw = c.Attribute("value") is { } v ? new[] { v.Value } : c.Elements("value").Select(x => x.Value).ToArray();
        if (type == FakeColumnType.VirtualName)
        {
            if (op != "eq" || raw.Length != 1 || source.Table.TypeOf(attribute[..^4]) != FakeColumnType.Choice)
                throw Unsupported($"operator {op} on the virtual column {attribute} (only eq on a choice name is observed, V08)");
            return new Condition(source.Alias, attribute[..^4], type, op, [StringKey(raw[0])], source.Table);
        }
        if (type == FakeColumnType.MultiChoice && op is not ("null" or "not-null"))
            throw Unsupported($"operator {op} on a multi-select column");

        (int Min, int Max) expected = op switch
        {
            "null" or "not-null" => (0, 0),
            "eq" or "ne" or "lt" or "gt" or "le" or "ge" => (1, 1),
            "in" or "not-in" => (1, int.MaxValue),
            "between" or "not-between" => (2, 2),
            _ when LikeOperators.Contains(op) => (1, 1),
            _ => throw Unsupported($"operator {op}")
        };
        if (raw.Length < expected.Min || raw.Length > expected.Max)
            throw Unsupported($"operator {op} with {raw.Length} values");

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

        return new Condition(source.Alias, attribute, type, op, raw.Select(x => Parse(x, type, attribute, op)).ToArray());
    }

    private static Order ReadOrder(XElement order, string alias, IReadOnlyDictionary<string, Source> sources)
    {
        AllowAttributes(order, "attribute", "descending", "entityname");
        NoChildren(order);
        if (order.Attribute("entityname") is { } en && (alias != BaseAlias || en.Value == BaseAlias))
            throw Unsupported("entityname on an order inside a link-entity, or an empty entityname");
        var source = ResolveSource((string?)order.Attribute("entityname") ?? alias, sources);
        var attribute = (string?)order.Attribute("attribute") ?? throw Unsupported("an <order> without an attribute");
        var type = ColumnOrVirtual(source.Table, attribute);
        if (type == FakeColumnType.VirtualName)
        {
            // Audit V09: ORDER BY a choice's name orders by its label, NULL first, like ORDER BY the choice (OBSERVED).
            if (source.Table.TypeOf(attribute[..^4]) != FakeColumnType.Choice)
                throw Unsupported($"ORDER BY the virtual column {attribute}");
            attribute = attribute[..^4];
            type = FakeColumnType.Choice;
        }
        if (type is FakeColumnType.Lookup or FakeColumnType.Uniqueidentifier or FakeColumnType.MultiChoice)
            throw Unsupported($"ORDER BY a {type} column");
        return new Order(source.Alias, attribute, type, order.Attribute("descending") is { } d && Bool(d));
    }

    private static Source ResolveSource(string alias, IReadOnlyDictionary<string, Source> sources)
        => sources.TryGetValue(alias, out var source)
            ? source
            : throw Unsupported($"entityname=\"{alias}\" that is not the alias of an enclosing link-entity");

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

    private static int? PositiveInt(XElement element, string name, int max = MaxPageSize, int min = 1)
    {
        if (element.Attribute(name) is not { } a)
            return null;
        if (!int.TryParse(a.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min
            || a.Value != n.ToString(CultureInfo.InvariantCulture))
            throw Unsupported($"{name}=\"{a.Value}\"");
        if (n > max)
            throw Unsupported($"{name} above {max}");
        return n;
    }

    private FakeTable Table(string? name)
    {
        if (name is null || !_tables.TryGetValue(name, out var table))
            throw Fault($"The entity with a name = '{name}' with namemapping = 'Logical' was not found in the MetadataCache."); // audit C01, E07
        return table;
    }

    /// <summary>
    /// A column, or the virtual <c>&lt;column&gt;name</c> of a choice or two-option column: Dataverse accepts it in an
    /// attribute and returns nothing for it (audit V04, V09, J19), and matches its label in an <c>eq</c> (V08).
    /// </summary>
    private static FakeColumnType ColumnOrVirtual(FakeTable table, string attribute)
        => !table.HasColumn(attribute) && attribute.EndsWith("name", StringComparison.Ordinal)
           && table.HasColumn(attribute[..^4]) && table.TypeOf(attribute[..^4]) is FakeColumnType.Choice or FakeColumnType.Boolean
            ? FakeColumnType.VirtualName
            : RequireColumn(table, attribute);

    /// <summary>R12.</summary>
    private static FakeColumnType RequireColumn(FakeTable table, string attribute)
        => table.HasColumn(attribute)
            ? table.TypeOf(attribute)
            // The live text (audit E01, O03): "'P' entity doesn't contain attribute with Name = 'x' and NameMapping = 'Logical' (...)".
            : throw Fault($"'{table.LogicalName}' entity doesn't contain attribute with Name = '{attribute}' and NameMapping = 'Logical' (look up attribute by name is case-sensitive).");

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

    /// <summary>The empty value Dataverse treats as unknown on a whole-number column (audit W51, W52, W136, W137).</summary>
    private sealed class EmptyValue
    {
        public static readonly EmptyValue Instance = new();
    }

    private static readonly Regex WholeForm = new(@"^-?(0|[1-9]\d*)$", RegexOptions.CultureInvariant);
    private static readonly Regex DecimalForm = new(@"^-?(0|[1-9]\d*)(\.\d+)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// R13: only value forms with an observation behind them are read. A decimal point on a whole-number column is the
    /// Dataverse fault of audit W15; an empty value on a whole-number column is unknown for <c>in</c>, <c>not-in</c> and
    /// <c>between</c> (W51, W52, W136, W137). Every other form (a sign, leading zeros, padding, an exponent, a comma,
    /// overflow, a braced GUID, <c>true</c>/<c>false</c>) is refused.
    /// </summary>
    private object Parse(string value, FakeColumnType type, string attribute, string op)
    {
        var ordered = RangeOperators.Contains(op);
        FaultException<OrganizationServiceFault> FormatFault() => Fault(
            $"An exception System.FormatException was thrown while trying to convert input value '{value}' to attribute '{attribute}'. Expected type of attribute value: System.Int32.");

        switch (type)
        {
            case FakeColumnType.String:
                if (value.Length == 0 && op is not ("eq" or "ne"))
                    throw Unsupported($"an empty value with operator {op} on a string column");
                return StringKey(value, ordered: ordered);

            case FakeColumnType.Integer or FakeColumnType.BigInt or FakeColumnType.Choice:
                if (value.Length == 0)
                    return op is "in" or "not-in" or "between" or "eq" ? EmptyValue.Instance : throw Unsupported($"an empty value with operator {op} on a whole-number column"); // W26 eq
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) && !Regex.IsMatch(value.Trim(), @"^[+-]?\d+$"))
                    throw FormatFault(); // audit W15 '5.0', E06 'abc', X02/X03/X12 '@i'
                if (!WholeForm.IsMatch(value))
                    throw Unsupported($"the whole-number value '{value}'");
                var parsed = decimal.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                var (min, max) = type == FakeColumnType.BigInt ? ((decimal)long.MinValue, (decimal)long.MaxValue) : (int.MinValue, int.MaxValue);
                return parsed >= min && parsed <= max ? parsed : throw Unsupported($"the out-of-range value '{value}'");

            case FakeColumnType.Decimal or FakeColumnType.Money:
                return DecimalForm.IsMatch(value)
                    ? decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
                    : throw Unsupported($"the {type} value '{value}'");

            case FakeColumnType.Double:
                return DecimalForm.IsMatch(value)
                    ? double.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
                    : throw Unsupported($"the float value '{value}'");

            case FakeColumnType.Boolean:
                return value switch { "1" or "true" => true, "0" => false, _ => throw Unsupported($"the two-option value '{value}'") }; // "true": W118

            case FakeColumnType.DateTime:
                return ParseDateTime(value);

            case FakeColumnType.DateOnly:
                if (Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$")
                    && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return date;
                throw Unsupported($"the date-only value '{value}'");

            case FakeColumnType.Uniqueidentifier or FakeColumnType.Lookup:
                return Guid.TryParseExact(value, "D", out var g) || Guid.TryParseExact(value, "B", out g) // "B": W114
                    ? g : throw Unsupported($"the unique-identifier value '{value}'");

            default:
                throw Unsupported($"a condition on a {type} column");
        }
    }

    /// <summary>
    /// R7: an explicit offset or Z is honoured; otherwise the value is in the caller's time zone. Text of digits only or a
    /// variable name is the Dataverse fault of audit W104 (<c>'20240115'</c>) and X05 (<c>@d</c>); any other form, or an
    /// ISO form that is not a calendar date, is refused.
    /// </summary>
    private DateTime ParseDateTime(string value)
    {
        if (Regex.IsMatch(value, @"^(\d+|@\w+)$"))
            throw Fault($"The date-time format for {value} is invalid, or value is outside the supported range.");
        var match = DateForm.Match(value);
        if (!match.Success)
            throw Unsupported($"the date-time value '{value}'");
        var normalized = value.Replace(' ', 'T');
        if (match.Groups["offset"].Success)
        {
            return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset)
                ? withOffset.UtcDateTime
                : throw Unsupported($"the date-time value '{value}'");
        }
        return DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local - _callerUtcOffset, DateTimeKind.Utc)
            : throw Unsupported($"the date-time value '{value}'");
    }

    /// <summary>
    /// R4: the machine-independent comparison key of a string. Accents are removed (Unicode canonical decomposition),
    /// letters folded to A-Z, and the characters mapped so that ordinal order is space &lt; % &lt; [ &lt; ] &lt; _ &lt; digits &lt;
    /// letters. Any other character is refused.
    /// </summary>
    private static string StringKey(string s, bool trimEnd = true, bool ordered = false)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in (trimEnd ? s.TrimEnd(' ') : s).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(KeyChar(ch, ordered));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Other printable ASCII punctuation has no case or accent variant, so it compares equal only to itself; its position
    /// in the order is not observed, so ordering and range comparisons refuse it.
    /// </summary>
    private static char KeyChar(char ch, bool ordered) => ch switch
    {
        ' ' => '\u0001',
        '%' => '\u0002',
        '[' => '\u0003',
        ']' => '\u0004',
        '_' => '\u0005',
        >= '0' and <= '9' => ch,
        >= 'A' and <= 'Z' => ch,
        >= 'a' and <= 'z' => (char)(ch - 'a' + 'A'),
        '\'' when ordered => '\u0006',
        > ' ' and <= '~' when !ordered => ch,
        '\n' when !ordered => ch, // a memo line break (audit W125, W126)
        _ => throw Unsupported($"{(ordered ? "ordering by" : "comparing")} the character '{ch}' (U+{(int)ch:X4}); its collation is not observed")
    };

    /// <summary>
    /// R4 order of two keys. Where the first difference is a pair of characters whose order the audit's "order by name
    /// asc" line does not show (space against <c>%</c>, <c>_</c> against a digit, anything against <c>]</c> or an
    /// apostrophe), the comparison is refused.
    /// </summary>
    private static int CompareOrderKeys(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] == b[i])
                continue;
            var (x, y) = a[i] < b[i] ? (a[i], b[i]) : (b[i], a[i]);
            if (x is '\u0004' or '\u0006' || y is '\u0004' or '\u0006' || (x, y) == ('\u0001', '\u0002') || (x == '\u0005' && y is >= '0' and <= '9'))
                throw Unsupported("the order of two strings that first differ at a character pair the audit did not order");
            return a[i] < b[i] ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

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

    private static int Compare(object actual, object value, FakeColumnType type, bool ordered = false) => type switch
    {
        FakeColumnType.String => ordered
            ? CompareOrderKeys(StringKey((string)actual, ordered: true), (string)value)
            : string.CompareOrdinal(StringKey((string)actual), (string)value),
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
                var key = link.To!.Length == 0 ? null : Normalize(row.Rows[parent.Alias]?.GetValueOrDefault(link.To!));
                var matches = key is null || link.From!.Length == 0 ? [] : candidates.Where(c => Equals(Normalize(c.GetValueOrDefault(link.From!)), key)).ToList();
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
        if (c.Type == FakeColumnType.VirtualName)
        {
            var option = row.Rows.GetValueOrDefault(c.Alias)?.GetValueOrDefault(c.Attribute) as OptionSetValue;
            return option is not null && c.Table!.Label(c.Attribute, option.Value) is { } label && StringKey(label) == (string)c.Values[0];
        }
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

        var ordered = RangeOperators.Contains(c.Operator);
        int Cmp(object value) => Compare(actual, value, c.Type, ordered);
        return c.Operator switch
        {
            "eq" => c.Values[0] is not EmptyValue && Cmp(c.Values[0]) == 0, // an empty value matches nothing (W26)
            "ne" => Cmp(c.Values[0]) != 0,
            "lt" => Cmp(c.Values[0]) < 0,
            "gt" => Cmp(c.Values[0]) > 0,
            "le" => Cmp(c.Values[0]) <= 0,
            "ge" => Cmp(c.Values[0]) >= 0,
            // An empty value is unknown: it matches nothing, and a not-in that holds one is false (audit W51, W52, W137).
            "in" => c.Values.Any(x => x is not EmptyValue && Cmp(x) == 0),
            "not-in" => c.Values.All(x => x is not EmptyValue && Cmp(x) != 0),
            "between" => c.Values.All(x => x is not EmptyValue) && Cmp(c.Values[0]) >= 0 && Cmp(c.Values[1]) <= 0, // W136
            "not-between" => !(Cmp(c.Values[0]) >= 0 && Cmp(c.Values[1]) <= 0),
            _ => throw Unsupported($"operator {c.Operator}")
        };
    }

    // ───────────────────────────── order (R6) ─────────────────────────────

    /// <summary>The entity's own orders first, then each link-entity's, depth first (not observed).</summary>
    private static IEnumerable<Order> AllOrders(Source source)
        => source.Orders.Concat(source.Links.SelectMany(AllOrders));

    private static List<T> Unwrap<T>(Func<List<T>> sort)
    {
        try
        {
            return sort();
        }
        catch (InvalidOperationException ex) when (ex.InnerException is NotSupportedException refused)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(refused).Throw();
            throw;
        }
    }

    private static List<JoinedRow> Sort(List<JoinedRow> rows, List<Order> orders, IReadOnlyDictionary<string, Source> sources, bool tieBreakById = true)
    {
        // Rows come back in SQL Server uniqueidentifier order of the primary key when nothing else decides (the audit's
        // unordered outputs, for example W54, D01 and PRJ01, follow it); that order also breaks ties.
        var primary = sources[BaseAlias].Table.PrimaryIdAttribute;
        System.Data.SqlTypes.SqlGuid Id(JoinedRow r) => new((Guid)r.Rows[BaseAlias]![primary]!);
        if (orders.Count == 0)
            return tieBreakById ? rows.OrderBy(Id).ToList() : rows;
        IOrderedEnumerable<JoinedRow>? sorted = null;
        foreach (var order in orders)
        {
            var table = sources[order.Alias].Table;
            object? Key(JoinedRow r)
            {
                var value = Normalize(r.Rows.GetValueOrDefault(order.Alias)?.GetValueOrDefault(order.Attribute));
                // R6: a choice column orders by its label.
                if (order.Type == FakeColumnType.Choice && value is decimal d)
                    return table.Label(order.Attribute, (int)d) is { } label ? StringKey(label, ordered: true) : throw Unsupported($"a choice value {d} without a label");
                return value is string s ? StringKey(s, ordered: true) : value;
            }
            var type = order.Type == FakeColumnType.Choice ? FakeColumnType.String : order.Type;
            var comparer = Comparer<object?>.Create((a, b) => CompareForOrder(a, b, type));
            sorted = sorted is null
                ? order.Descending ? rows.OrderByDescending(Key, comparer) : rows.OrderBy(Key, comparer)
                : order.Descending ? sorted.ThenByDescending(Key, comparer) : sorted.ThenBy(Key, comparer);
        }
        try
        {
            return tieBreakById ? sorted!.ThenBy(Id).ToList() : sorted!.ToList();
        }
        catch (InvalidOperationException ex) when (ex.InnerException is NotSupportedException refused)
        {
            // The sort wraps a comparer's exception; surface the refusal itself.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(refused).Throw();
            throw;
        }
    }

    /// <summary>R6: NULL sorts lowest, so first ascending and last descending. Strings arrive as R4 keys.</summary>
    private static int CompareForOrder(object? a, object? b, FakeColumnType type)
    {
        if (a is null || b is null)
            return a is null ? (b is null ? 0 : -1) : 1;
        if (a is string sa && b is string sb)
            return CompareOrderKeys(sa, sb);
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
                    continue; // a repeated unaliased attribute (audit PRJ12); other duplicates are refused before any row

                if (!source.Table.HasColumn(name))
                    continue; // a virtual <column>name comes back empty (V04, V09)
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
        // Audit PRJ01 '11', D01 '-100', PG01/PG03 '4,640'.
        FakeColumnType.Integer or FakeColumnType.BigInt
            => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("#,##0", CultureInfo.InvariantCulture),
        FakeColumnType.DateTime => (DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc) + _callerUtcOffset)
            .ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture),
        _ => NotModelledFormattedValue
    };

    private static string Signature(Entity e)
        => string.Join("|", e.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => $"{a.Key}={Normalize(a.Value) switch
            {
                string s => StringKey(s),
                decimal d => (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
                OptionSetValueCollection m => string.Join("|", m.Select(o => o.Value).Order()),
                double f => f.ToString("R", CultureInfo.InvariantCulture),
                var v => Convert.ToString(v, CultureInfo.InvariantCulture)
            }}"));

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
            more = size > 0 && total > number * size;
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
        => new($"Fake Dataverse refuses this FetchXML: not observed: {what}. Observe it live and add the rule before relying on it.");

    private static FaultException<OrganizationServiceFault> Fault(string message)
        => new(new OrganizationServiceFault { Message = message }, new FaultReason(message));
}
