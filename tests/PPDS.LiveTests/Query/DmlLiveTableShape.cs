using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;

namespace PPDS.LiveTests.Query;

/// <summary>
/// The columns of the live suite's throwaway table, as the DML column validator reads them from metadata: the fake the
/// offline admission matrix asks the product about. The live run compares it with the real table's metadata
/// (<see cref="Differences"/>), so the matrix cannot pass against a table that differs from the one the live suite proves.
/// </summary>
internal static class DmlLiveTableShape
{
    /// <summary>The table name the offline catalogue and the matrix use (the live run uses a random suffix).</summary>
    public const string Table = "ppds_dmllive00000000";

    /// <summary>The choice values the offline catalogue uses (the live run reads the real ones).</summary>
    public static readonly int[] Choices = [374110000, 374110001, 374110002];

    /// <summary>The primary id column of a table of this suite.</summary>
    public static string PrimaryId(string table) => table + "id";

    private static AttributeMetadataDto A(string name, string type, bool create = true, bool update = true, bool read = true,
        string? behavior = null, string? format = null, bool primary = false, string? attributeOf = null, List<string>? targets = null,
        List<OptionValueDto>? options = null, int? precision = null, int? precisionSource = null) => new()
    {
        Precision = precision, PrecisionSource = precisionSource,
        LogicalName = name, DisplayName = name, SchemaName = name, AttributeType = type, IsValidForCreate = create,
        IsValidForUpdate = update, IsValidForRead = read, DateTimeBehavior = behavior, Format = format, IsPrimaryId = primary,
        AttributeOf = attributeOf, Targets = targets, Options = options
    };

    /// <summary>The columns a DML statement on the table can name, with what the validator decides from.</summary>
    public static IReadOnlyList<AttributeMetadataDto> Columns(string table, IReadOnlyList<int> choices) =>
    [
        A(PrimaryId(table), "Uniqueidentifier", create: true, update: false, primary: true),
        A("ppds_name", "String", format: "Text"),
        A("ppds_note", "String", format: "Text"),
        A("ppds_memo", "Memo"),
        A("ppds_num", "Integer"),
        A("ppds_amount", "Decimal"),
        // As the live suite creates it: 2 places, from the column itself (precision source 0).
        A("ppds_money", "Money", precision: 2, precisionSource: 0),
        A("ppds_float", "Double"),
        A("ppds_when", "DateTime", behavior: "UserLocal", format: "DateAndTime"),
        A("ppds_day", "DateTime", behavior: "DateOnly", format: "DateOnly"),
        A("ppds_tzi", "DateTime", behavior: "TimeZoneIndependent", format: "DateAndTime"),
        A("ppds_legacy", "DateTime", behavior: "UserLocal", format: "DateOnly"),
        A("ppds_flag", "Boolean", options: [new OptionValueDto { Value = 0, Label = "No" }, new OptionValueDto { Value = 1, Label = "Yes" }]),
        A("ppds_choice", "Picklist", options: choices.Select(c => new OptionValueDto { Value = c, Label = c.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList()),
        A("ppds_parent", "Lookup", targets: [table]),
        A("statecode", "State", create: false, options: [new OptionValueDto { Value = 0, Label = "Active" }, new OptionValueDto { Value = 1, Label = "Inactive" }]),
        A("statuscode", "Status", options: [new OptionValueDto { Value = 1, Label = "Active", State = 0 }, new OptionValueDto { Value = 2, Label = "Inactive", State = 1 }]),
        A("ownerid", "Owner", targets: ["systemuser", "team"]),
        A("owneridname", "String", create: false, update: false, format: "Text", attributeOf: "ownerid"),
        A("createdon", "DateTime", create: false, update: false, behavior: "UserLocal", format: "DateAndTime"),
        A("createdby", "Lookup", create: false, update: false, targets: ["systemuser"]),
        A("versionnumber", "BigInt", create: false, update: false),
    ];

    /// <summary>Where the real metadata differs from the shape, for every field the DML validator reads.</summary>
    public static List<string> Differences(string table, IReadOnlyList<int> choices, IReadOnlyList<AttributeMetadataDto> actual)
    {
        var differences = new List<string>();
        var byName = actual.GroupBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var expected in Columns(table, choices))
        {
            if (!byName.TryGetValue(expected.LogicalName, out var real))
            {
                differences.Add($"{expected.LogicalName}: missing");
                continue;
            }

            void Same<T>(string field, T want, T got)
            {
                if (!EqualityComparer<T>.Default.Equals(want, got))
                    differences.Add($"{expected.LogicalName}.{field}: shape {want}, metadata {got}");
            }

            Same("AttributeType", expected.AttributeType, real.AttributeType);
            Same("IsValidForCreate", expected.IsValidForCreate, real.IsValidForCreate);
            Same("IsValidForUpdate", expected.IsValidForUpdate, real.IsValidForUpdate);
            Same("IsValidForRead", expected.IsValidForRead, real.IsValidForRead);
            Same("IsPrimaryId", expected.IsPrimaryId, real.IsPrimaryId);
            Same("AttributeOf", expected.AttributeOf, real.AttributeOf);
            Same("DateTimeBehavior", expected.DateTimeBehavior, real.DateTimeBehavior);
            Same("Format", expected.Format, real.Format);
            if (expected.AttributeType == "Money")
            {
                Same("Precision", expected.Precision, real.Precision);
                Same("PrecisionSource", expected.PrecisionSource, real.PrecisionSource);
            }
            Same("Targets", string.Join(",", expected.Targets ?? []), string.Join(",", real.Targets ?? []));
            Same("Options", string.Join(",", (expected.Options ?? []).Select(o => $"{o.Value}/{o.State}")),
                string.Join(",", (real.Options ?? []).Select(o => $"{o.Value}/{o.State}")));
        }
        return differences;
    }

    /// <summary>A metadata provider that knows only this table (offline).</summary>
    public sealed class Provider(string table, IReadOnlyList<int> choices) : ICachedMetadataProvider
    {
        public Task<IReadOnlyList<AttributeMetadataDto>> GetAttributesAsync(string entityLogicalName, CancellationToken ct = default)
            => Task.FromResult(string.Equals(entityLogicalName, table, StringComparison.OrdinalIgnoreCase)
                ? Columns(table, choices)
                : (IReadOnlyList<AttributeMetadataDto>)Array.Empty<AttributeMetadataDto>());

        /// <summary>The table list: this table, a standard table (the live run reads the real kind).</summary>
        public Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EntitySummary>>([new EntitySummary
            {
                LogicalName = table, DisplayName = table, SchemaName = table, TableType = "Standard", IsActivity = false
            }]);

        public Task<EntityRelationshipsDto> GetRelationshipsAsync(string entityLogicalName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task PreloadAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public void InvalidateAll()
        {
        }

        public void InvalidateEntity(string entityLogicalName)
        {
        }

        public void InvalidateEntityList()
        {
        }

        public void InvalidateGlobalOptionSets()
        {
        }
    }
}
