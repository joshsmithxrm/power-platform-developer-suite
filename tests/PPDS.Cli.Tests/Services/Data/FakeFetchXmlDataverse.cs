using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// A recording fake of Dataverse for the data commands' filter path: it records every client taken from the pool and
/// every FetchXML sent, and answers FetchXML from in-memory rows with Dataverse's own semantics, not SQL's:
/// <c>ne</c>, <c>not-in</c>, <c>not-like</c>, <c>not-begin-with</c> and <c>not-end-with</c> match rows whose column is
/// NULL (observed live, specs/query.md on the SQL write-safety work), <c>not-between</c> does not, a date and time value
/// without a zone is read in the calling user's time zone (here UTC-05:00), text compares ignoring case and trailing
/// spaces, and results come in pages (the <c>count</c> attribute, 5,000 by default, with <c>MoreRecords</c> and a paging
/// cookie). No Dataverse is involved.
/// </summary>
internal sealed class FakeFetchXmlDataverse
{
    /// <summary>The offset the fake applies to a date and time value sent without a zone (the "user's" time zone).</summary>
    public static readonly TimeSpan UserOffset = TimeSpan.FromHours(-5);

    private readonly string _entity;
    private readonly List<(string Label, Guid Id, Dictionary<string, object?> Values)> _rows = new();

    public FakeFetchXmlDataverse(string entity) => _entity = entity;

    /// <summary>Every FetchXML text the code under test sent, in order.</summary>
    public List<string> FetchXmls { get; } = new();

    /// <summary>How many clients were taken from the pool.</summary>
    public int ClientsTaken { get; private set; }

    /// <summary>Whether anything was read: a client taken or a query sent.</summary>
    public bool AnythingRead => ClientsTaken > 0 || FetchXmls.Count > 0;

    public Guid Add(string label, params (string Column, object? Value)[] values)
    {
        var id = Guid.NewGuid();
        _rows.Add((label, id, values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase)));
        return id;
    }

    public Guid IdOf(string label) => _rows.Single(r => r.Label == label).Id;

    public string LabelOf(Guid id) => _rows.Single(r => r.Id == id).Label;

    public IDataverseConnectionPool CreatePool()
    {
        var client = new Mock<IPooledClient>();
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase q, CancellationToken _) => Answer(q));
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
            .ReturnsAsync((QueryBase q) => Answer(q));

        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                ClientsTaken++;
                return client.Object;
            });
        return pool.Object;
    }

    /// <summary>The conditions of the last FetchXML sent, as "attribute operator value[,value]" strings.</summary>
    public IReadOnlyList<string> ConditionsSent(int index = -1)
    {
        if (FetchXmls.Count == 0) return Array.Empty<string>();
        var xml = XDocument.Parse(FetchXmls[index < 0 ? FetchXmls.Count - 1 : index]);
        return xml.Descendants("condition").Select(c =>
        {
            var values = c.Attribute("value") is { } v
                ? new[] { v.Value }
                : c.Elements("value").Select(e => e.Value).ToArray();
            return $"{c.Attribute("attribute")?.Value} {c.Attribute("operator")?.Value} {string.Join(",", values.Select(x => $"'{x}'"))}".TrimEnd();
        }).ToList();
    }

    private EntityCollection Answer(QueryBase query)
    {
        if (query is not FetchExpression fetch)
            throw new InvalidOperationException("The fake answers FetchXML only.");

        FetchXmls.Add(fetch.Query);
        var doc = XDocument.Parse(fetch.Query);
        var root = doc.Root!;
        var entity = root.Element("entity")!;
        if (entity.Attribute("name")!.Value != _entity)
            throw new InvalidOperationException("Unexpected entity.");

        var filters = entity.Elements("filter").ToList();
        var matching = _rows.Where(r => filters.All(f => Matches(f, r.Values))).ToList();

        if (root.Attribute("top") is { } top)
        {
            matching = matching.Take(int.Parse(top.Value, CultureInfo.InvariantCulture)).ToList();
            return ToCollection(matching, moreRecords: false, page: 1);
        }

        var count = root.Attribute("count") is { } c ? int.Parse(c.Value, CultureInfo.InvariantCulture) : 5000;
        var page = root.Attribute("page") is { } p ? int.Parse(p.Value, CultureInfo.InvariantCulture) : 1;
        var slice = matching.Skip((page - 1) * count).Take(count).ToList();
        return ToCollection(slice, moreRecords: matching.Count > page * count, page);
    }

    private EntityCollection ToCollection(
        List<(string Label, Guid Id, Dictionary<string, object?> Values)> rows, bool moreRecords, int page)
    {
        var collection = new EntityCollection { EntityName = _entity, MoreRecords = moreRecords };
        if (moreRecords) collection.PagingCookie = $"<cookie page=\"{page}\" />";
        foreach (var row in rows)
        {
            var e = new Entity(_entity, row.Id);
            e[$"{_entity}id"] = row.Id;
            collection.Entities.Add(e);
        }
        return collection;
    }

    private static bool Matches(XElement filter, Dictionary<string, object?> row)
    {
        var or = filter.Attribute("type")?.Value == "or";
        var results = filter.Elements().Select(child => child.Name.LocalName switch
        {
            "condition" => ConditionMatches(child, row),
            "filter" => Matches(child, row),
            _ => throw new InvalidOperationException($"Unexpected element {child.Name}.")
        }).ToList();
        if (results.Count == 0) return true;
        return or ? results.Any(x => x) : results.All(x => x);
    }

    private static bool ConditionMatches(XElement condition, Dictionary<string, object?> row)
    {
        var attribute = condition.Attribute("attribute")!.Value;
        var op = condition.Attribute("operator")!.Value;
        row.TryGetValue(attribute, out var stored);
        var values = condition.Attribute("value") is { } v
            ? new List<string> { v.Value }
            : condition.Elements("value").Select(e => e.Value).ToList();

        switch (op)
        {
            case "null": return stored is null;
            case "not-null": return stored is not null;
            case "eq": return stored is not null && Compare(stored, values[0]) == 0;
            case "ne": return stored is null || Compare(stored, values[0]) != 0;
            case "lt": return stored is not null && Compare(stored, values[0]) < 0;
            case "le": return stored is not null && Compare(stored, values[0]) <= 0;
            case "gt": return stored is not null && Compare(stored, values[0]) > 0;
            case "ge": return stored is not null && Compare(stored, values[0]) >= 0;
            case "in": return stored is not null && values.Any(x => Compare(stored, x) == 0);
            case "not-in": return stored is null || values.All(x => Compare(stored, x) != 0);
            case "between": return stored is not null && Compare(stored, values[0]) >= 0 && Compare(stored, values[1]) <= 0;
            case "not-between": return stored is not null && !(Compare(stored, values[0]) >= 0 && Compare(stored, values[1]) <= 0);
            case "like": return stored is not null && SqlLike((string)stored, values[0]);
            case "not-like": return stored is null || !SqlLike((string)stored, values[0]);
            case "begins-with": return stored is not null && SqlLike((string)stored, values[0] + "%");
            case "not-begin-with": return stored is null || !SqlLike((string)stored, values[0] + "%");
            case "ends-with": return stored is not null && SqlLike((string)stored, "%" + values[0]);
            case "not-end-with": return stored is null || !SqlLike((string)stored, "%" + values[0]);
            default: throw new InvalidOperationException($"The fake does not know operator {op}.");
        }
    }

    private static int Compare(object stored, string value) => stored switch
    {
        string s => string.Compare(s.TrimEnd(' '), value.TrimEnd(' '), StringComparison.OrdinalIgnoreCase),
        int i => ((decimal)i).CompareTo(decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)),
        decimal d => d.CompareTo(decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)),
        DateTime dt => dt.CompareTo(ReadDate(value)),
        Guid g => g.CompareTo(Guid.Parse(value)),
        _ => throw new InvalidOperationException($"The fake does not compare {stored.GetType().Name}.")
    };

    private static DateTime ReadDate(string value)
    {
        var hasZone = Regex.IsMatch(value, @"(Z|[+-][0-9]{2}:[0-9]{2})\z");
        if (hasZone)
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).UtcDateTime;
        var local = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        return DateTime.SpecifyKind(local - UserOffset, DateTimeKind.Utc);
    }

    /// <summary>SQL LIKE with % and _ (no [ ] and no ESCAPE), ignoring case as Dataverse's collation does.</summary>
    private static bool SqlLike(string text, string pattern)
    {
        var regex = "^" + string.Concat(pattern.Select(ch => ch switch
        {
            '%' => ".*",
            '_' => ".",
            _ => Regex.Escape(ch.ToString())
        })) + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }
}
