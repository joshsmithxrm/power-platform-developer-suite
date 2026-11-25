using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Moq;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Evaluates a FetchXML &lt;filter&gt; against an in-memory row the way Dataverse does, as observed in the live
/// differential run (DmlLiveDifferentialTests): string comparison is case-insensitive; a NULL attribute matches only
/// null / not-null and the negated operators ne, not-in, not-like, not-begin-with and not-end-with,
/// which Dataverse also matches when the attribute is NULL (unlike SQL).
/// </summary>
internal static class FetchXmlFilter
{
    public static bool Matches(XElement filter, IReadOnlyDictionary<string, QueryValue> row)
    {
        var or = string.Equals((string?)filter.Attribute("type"), "or", StringComparison.OrdinalIgnoreCase);
        var results = filter.Elements().Select(e => e.Name.LocalName switch
        {
            "filter" => Matches(e, row),
            "condition" => Condition(e, row),
            _ => throw new NotSupportedException($"FetchXML element {e.Name.LocalName} in a filter")
        }).ToList();
        return or ? results.Any(r => r) : results.All(r => r);
    }

    private static bool Condition(XElement c, IReadOnlyDictionary<string, QueryValue> row)
    {
        var attribute = (string)c.Attribute("attribute")!;
        var op = (string)c.Attribute("operator")!;
        var actual = row.TryGetValue(attribute, out var qv) ? qv.Value : null;
        var values = c.Attribute("value") is { } v ? new[] { v.Value } : c.Elements("value").Select(x => x.Value).ToArray();

        switch (op)
        {
            case "null": return actual is null;
            case "not-null": return actual is not null;
        }

        // Observed live (2026-11-17): ne, not-in, not-like (and the begin/end forms) also match NULL rows; not-between does not.
        var negated = op is "ne" or "not-in" or "not-like" or "not-begin-with" or "not-end-with";
        if (actual is null)
            return negated;

        int Cmp(string value) => actual switch
        {
            string s => string.Compare(s, value, StringComparison.OrdinalIgnoreCase),
            Guid g => Guid.TryParse(value, out var other) ? g.CompareTo(other) : 1,
            bool b => b.CompareTo(value == "1"),
            DateTime d => d.CompareTo(DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime),
            _ => Convert.ToDecimal(actual, CultureInfo.InvariantCulture).CompareTo(decimal.Parse(value, CultureInfo.InvariantCulture))
        };
        bool Like(string pattern)
            => Regex.IsMatch(actual.ToString()!, "^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return op switch
        {
            "eq" => Cmp(values[0]) == 0,
            "ne" => Cmp(values[0]) != 0,
            "lt" => Cmp(values[0]) < 0,
            "gt" => Cmp(values[0]) > 0,
            "le" => Cmp(values[0]) <= 0,
            "ge" => Cmp(values[0]) >= 0,
            "in" => values.Any(x => Cmp(x) == 0),
            "not-in" => values.All(x => Cmp(x) != 0),
            "like" => Like(values[0]),
            "not-like" => !Like(values[0]),
            "begins-with" => Like(values[0] + "%"),
            "not-begin-with" => !Like(values[0] + "%"),
            "ends-with" => Like("%" + values[0]),
            "not-end-with" => !Like("%" + values[0]),
            "between" => Cmp(values[0]) >= 0 && Cmp(values[1]) <= 0,
            "not-between" => !(Cmp(values[0]) >= 0 && Cmp(values[1]) <= 0),
            _ => throw new NotSupportedException($"FetchXML operator {op}")
        };
    }
}

/// <summary>Attribute metadata for the fake account and contact tables used by the DML tests.</summary>
internal static class TestMetadata
{
    private static AttributeMetadataDto A(string name, string type, bool create = true, bool update = true, string? attributeOf = null,
        List<OptionValueDto>? options = null, List<string>? targets = null, string? behavior = null, string? format = null, bool? primary = null,
        int? precision = null, int? precisionSource = 0) => new()
    {
        Precision = precision,
        // A currency column's places come from the column itself unless a test says otherwise (P1).
        PrecisionSource = type == "Money" ? precisionSource : null,
        LogicalName = name, DisplayName = name, SchemaName = name, AttributeType = type,
        IsValidForCreate = create, IsValidForUpdate = update, IsValidForRead = true, AttributeOf = attributeOf,
        Options = options, Targets = targets, IsPrimaryId = primary ?? type == "Uniqueidentifier",
        DateTimeBehavior = type == "DateTime" && behavior != "-" ? behavior ?? "UserLocal" : null, Format = type == "DateTime" ? format ?? "DateAndTime" : null
    };

    private static List<OptionValueDto> O(params (int Value, int? State)[] values)
        => values.Select(v => new OptionValueDto { Value = v.Value, Label = "o" + v.Value, State = v.State }).ToList();

    private static readonly Dictionary<string, AttributeMetadataDto[]> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["account"] =
        [
            A("accountid", "Uniqueidentifier", update: false), A("name", "String"), A("description", "String"),
            A("revenue", "Decimal"), A("creditlimit", "Money"), A("numberofemployees", "Integer"), A("notes", "Memo"), A("customertypecode", "Picklist", options: O((1, null), (2, null))),
            A("statecode", "State", options: O((0, null), (1, null))), A("statuscode", "Status", options: O((1, 0), (2, 1))),
            A("parentaccountid", "Lookup", targets: ["account"]), A("ownerid", "Owner"),
            A("owneridname", "String", create: false, update: false, attributeOf: "ownerid"), A("domainname", "String"),
            A("contactid", "Lookup"), A("createdon", "DateTime", create: false, update: false), A("followupby", "DateTime"),
            A("legacydate", "DateTime", behavior: "UserLocal", format: "DateOnly"), A("tzidate", "DateTime", behavior: "TimeZoneIndependent"),
            A("nobehaviordate", "DateTime", behavior: "-"), A("birthday", "DateTime", behavior: "DateOnly", format: "DateOnly"),
            A("stageid", "Uniqueidentifier", primary: false), A("donotemail", "Boolean"), A("aging30", "BigInt"),
            A("address1_latitude", "Double", precision: 5), A("ppds_unreadfloat", "Double"), A("ppds_rate", "Decimal", precision: 2), A("ppds_amount2", "Money", precision: 2),
            A("ppds_pricingamount", "Money", precision: 2, precisionSource: 1), A("ppds_currencyamount", "Money", precision: 2, precisionSource: 2),
            A("ppds_unreadamount", "Money", precision: 2, precisionSource: null)
        ],
        // Tables of the kinds a write is refused for (P2); the columns let a test show that nothing is read.
        ["ppds_elastic"] = [A("ppds_elasticid", "Uniqueidentifier", update: false), A("ppds_name", "String")],
        ["ppds_virtual"] = [A("ppds_virtualid", "Uniqueidentifier", update: false), A("ppds_name", "String")],
        ["ppds_provided"] = [A("ppds_providedid", "Uniqueidentifier", update: false), A("ppds_name", "String")],
        ["ppds_untyped"] = [A("ppds_untypedid", "Uniqueidentifier", update: false), A("ppds_name", "String")],
        ["activitypointer"] = [A("activityid", "Uniqueidentifier", update: false), A("subject", "String")],
        // An activity table: its primary id is activityid, not taskid.
        ["task"] =
        [
            A("activityid", "Uniqueidentifier", update: false), A("subject", "String"), A("stageid", "Uniqueidentifier", primary: false)
        ],
        ["solution"] = [A("solutionid", "Uniqueidentifier", update: false), A("friendlyname", "String")],
        ["contact"] =
        [
            A("contactid", "Uniqueidentifier", update: false), A("lastname", "String"), A("fullname", "String"),
            A("parentcustomerid", "Customer"), A("accountid", "Lookup"), A("name", "String")
        ]
    };

    private static EntitySummary E(string name, string? tableType, bool? isActivity = false, Guid? dataProviderId = null) => new()
    {
        LogicalName = name, DisplayName = name, SchemaName = name, TableType = tableType, IsActivity = isActivity, DataProviderId = dataProviderId
    };

    /// <summary>The table list: the kind of each table (P2). <c>task</c> is an activity table.</summary>
    public static readonly IReadOnlyList<EntitySummary> Entities =
    [
        // As Dataverse reports them (live, 2026-11-25): an activity table has type Standard and the activity flag; the shared
        // activity table, activitypointer, has type Standard and the flag false.
        E("account", "Standard"), E("contact", "Standard"), E("solution", "Standard"), E("task", "Standard", isActivity: true),
        E("activitypointer", "Standard", isActivity: false),
        E("ppds_elastic", "Elastic"), E("ppds_virtual", "Virtual"),
        E("ppds_provided", "Standard", dataProviderId: Guid.Parse("7015a531-cc0d-4537-b5f2-c882a1eb65ad")),
        E("ppds_untyped", null, isActivity: null)
    ];

    public static ICachedMetadataProvider Provider { get; } = Create();

    private static ICachedMetadataProvider Create()
    {
        var mock = new Mock<ICachedMetadataProvider>(MockBehavior.Loose);
        mock.Setup(m => m.GetAttributesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string table, CancellationToken _) =>
                Tables.TryGetValue(table, out var a) ? a : Array.Empty<AttributeMetadataDto>());
        mock.Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Entities);
        return mock.Object;
    }
}
