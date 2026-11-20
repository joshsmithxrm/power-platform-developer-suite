using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// The audit as the oracle, by machine. Each OBSERVED line of the November 2026 live SELECT audit
/// (docs/qa/select-fidelity-audit-2026-11.md on the test/select-fidelity-live branch, "OBSERVED Dataverse behaviours",
/// run 5) is copied verbatim below. The test parses the expected rows out of that text and runs the same raw FetchXML the
/// audit sent (<c>RawFilterAsync</c> in SelectFidelityLiveTests.cs) against the audit's own parent table, rebuilt from its
/// seed (<c>ParentSeeds</c>, ported unchanged). A refusal by the fake fails the case: the audit observed it.
/// <para>
/// 29 OBSERVED lines: 27 are replayed here (21 row sets, 4 orders, the stored-string read-back and the formatted date-time).
/// The other 2 describe the SDK read-back of the seed itself (an unset two-option reads back as 0; money, decimal and float
/// read back unchanged); the dataset below applies them.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class AuditObservedReplayTests
{

    // ───────────── The audit's parent table (SelectFidelityLiveTests.ParentSeeds, unchanged) ─────────────

    private sealed record PSeed(int Key, string? Name, string? Memo, int? Num, int? Num2, decimal? Dec, decimal? Money, double? Flt,
        bool? Flag, DateTime? Dt, DateTime? DOnly, int? Color, int[]? Multi, string? ItemType, string? ItemTypeName);

    private static DateTime U(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);
    private static DateTime D(int y, int mo, int d) => new(y, mo, d, 0, 0, 0, DateTimeKind.Unspecified);

    private static List<PSeed> ParentSeeds()
    {
        var list = new List<PSeed>
        {
            new(1, "Alpha", "first memo", 10, 10, 1.25m, 100m, 3.25, true, U(2024, 1, 15, 0, 30), D(2024, 1, 15), 0, [0], "A", "ItemA"),
            new(2, "alpha", null, 5, 7, 1.25m, 0m, 0, false, U(2024, 1, 15, 23, 30), D(2024, 1, 16), 1, [0, 1], "B", "ItemB"),
            new(3, "ALPHA", "Mixed CASE memo", -1, null, -2.5m, -10m, -0.5, true, U(2024, 6, 30, 12), D(2024, 6, 30), 2, null, null, "ItemC"),
            new(4, "Beta", "", 0, 0, 0m, null, null, false, null, null, null, null, "A", null),
            new(5, "Café", "café memo", 5, 5, 99.99m, 1234.56m, 1000000, true, U(2000, 1, 1), D(2000, 1, 1), 0, [2], "C", "ItemC"),
            new(6, "cafe", null, null, 3, null, 50m, 2.5, false, U(2099, 12, 31, 12), D(2099, 12, 31), 1, null, "B", "ItemB"),
            new(7, "  lead", "line1\nline2", 100, null, 10.5m, null, -3.25, null, U(1900, 1, 1, 12), D(1900, 1, 1), null, null, null, null),
            new(8, "trail  ", null, -100, -100, -0.01m, -0.01m, 0.01, true, U(2024, 1, 14, 23, 59), D(2024, 1, 14), 2, [0, 1, 2], "A", "ItemA"),
            new(9, "50% off", "percent", 50, 49, 50m, 50m, 50, false, U(2024, 2, 29, 8), D(2024, 2, 29), 0, null, "A", null),
            new(10, "under_score", null, 7, 8, null, 7m, 7, true, U(2024, 3, 10, 9, 30), D(2024, 3, 10), 1, [1], "B", "ItemB"),
            new(11, "underXscore", null, 8, 7, 8m, 8m, 8, false, U(2024, 3, 10, 10, 30), D(2024, 3, 10), 2, null, "C", "ItemC"),
        };
        string?[] names =
        [
            "[bracket]", "a[b]c", "abc", "abd", "ab", "b", null, null, "O'Brien", "Zeta", "Ångström", "naïve", "c%d", "e_f",
            "100", "20", "x", "X", "Alpha ", "dup", "dup", "zz", "Ab", "a_c", "a%c"
        ];
        string?[] itemTypes = ["A", "B", "C", "a"];
        for (var k = 12; k <= 36; k++)
        {
            var name = names[k - 12];
            int? num = k % 5 == 0 ? null : (k * 7 % 23) - 8;
            if (name == "dup") num = 5;
            list.Add(new PSeed(
                k, name,
                k % 4 == 0 ? null : (k % 7 == 0 ? $"MEMO {k}" : $"memo {k}"),
                num,
                k % 6 == 0 ? null : (k * 5 % 19) - 6,
                k % 7 == 0 ? null : Math.Round(((k * 13 % 41) - 20) / 4m, 2),
                k % 8 == 0 ? null : (k * 17 % 50) - 25 + 0.5m,
                k % 9 == 0 ? null : ((k * 11 % 30) - 15) / 4.0,
                k % 3 == 0 ? null : k % 2 == 0,
                k % 10 == 0 ? null : U(2023, 1 + k % 12, 1 + k % 27, k % 24, k * 7 % 60),
                k % 11 == 0 ? null : D(2022, 1 + k % 12, 1 + k % 28),
                k % 4 == 1 ? null : k % 3,
                k % 5 == 2 ? null : k % 3 == 0 ? [0] : k % 3 == 1 ? [1, 2] : [0, 2],
                k % 6 == 5 ? null : itemTypes[k % 4],
                k % 7 == 3 ? null : $"Item{k % 3}"));
        }
        return list;
    }

    /// <summary>
    /// The parent table as the audit read it back. Its choice labels are the ones the OBSERVED order line shows
    /// (Red=374110000, Blue=374110001, Green=374110002); an unset two-option reads back as 0 (false), as observed.
    /// Columns no replayed line uses (num2, decimal, money, float, multi-select, item types) are left out.
    /// </summary>
    private static FakeDataverse AuditDataverse()
    {
        var table = new FakeTable("ppds_p")
            .Column("ppds_key", FakeColumnType.Integer).Column("ppds_name", FakeColumnType.String).Column("ppds_memo", FakeColumnType.String)
            .Column("ppds_num", FakeColumnType.Integer).Column("ppds_flag", FakeColumnType.Boolean)
            .Column("ppds_dt", FakeColumnType.DateTime).Column("ppds_donly", FakeColumnType.DateOnly)
            .Choice("ppds_colorcode", (374110000, "Red"), (374110001, "Blue"), (374110002, "Green"));
        foreach (var s in ParentSeeds())
        {
            table.Row(("ppds_key", s.Key), ("ppds_name", s.Name), ("ppds_memo", s.Memo), ("ppds_num", s.Num), ("ppds_flag", s.Flag ?? false),
                ("ppds_dt", s.Dt), ("ppds_donly", s.DOnly),
                ("ppds_colorcode", s.Color is { } c ? new OptionSetValue(374110000 + c) : null));
        }
        return new FakeDataverse().Add(table);
    }

    /// <summary>The FetchXML of the audit's <c>RawFilterAsync</c>, with the audit's table and prefix.</summary>
    private static EntityCollection Raw(string condition, string order = "", string attributes = "")
        => AuditDataverse().RetrieveMultiple(
            $"<fetch><entity name=\"ppds_p\"><attribute name=\"ppds_key\" />{attributes}{(condition.Length > 0 ? $"<filter>{condition}</filter>" : "")}{order}</entity></fetch>");

    private static int Key(Entity e) => e.GetAttributeValue<int>("ppds_key");

    // ───────────── Replays ─────────────

    [Theory]
    [MemberData(nameof(KeyCases))]
    public void ObservedRows(string observed, string condition)
    {
        var expected = Regex.Match(observed, @"keys ?([\d,]*)").Groups[1].Value;
        var actual = string.Join(",", Raw(condition).Entities.Select(Key).Order());
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(OrderCases))]
    public void ObservedOrder(string observed, string order, string attribute)
    {
        var tokens = Regex.Matches(observed[(observed.IndexOf("): ", StringComparison.Ordinal) is var i and >= 0 ? i + 3 : observed.IndexOf(": ", StringComparison.Ordinal) + 2)..],
                @"NULL|'(?:[^']|'(?! |$))*'|-?\d+")
            .Select(m => m.Value).ToList();
        var result = Raw("", order, $"<attribute name=\"{attribute}\" />");

        string Show(Entity e) => e.Attributes.TryGetValue(attribute, out var v) ? v switch
        {
            string s => $"'{s}'",
            OptionSetValue o => $"'{e.FormattedValues[attribute]}={o.Value}'",
            int n => n.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(v?.GetType().Name)
        } : "NULL";

        Assert.Equal(36, tokens.Count);
        Assert.Equal(tokens, result.Entities.Select(Show));
    }

    [Fact]
    public void ObservedStoredStrings()
    {
        var result = Raw("", "", "<attribute name=\"ppds_name\" /><attribute name=\"ppds_memo\" />").Entities.ToDictionary(Key);
        foreach (Match m in Regex.Matches(StoredStrings, @"key (\d+) name=(NULL|'.*?') memo=(NULL|'.*?')(?=, key|$)", RegexOptions.Singleline))
        {
            var row = result[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)];
            string Show(string column) => row.Attributes.TryGetValue(column, out var v) ? $"'{v}'" : "NULL";
            Assert.Equal(m.Groups[2].Value, Show("ppds_name"));
            Assert.Equal(m.Groups[3].Value, Show("ppds_memo"));
        }
        Assert.Equal(4, Regex.Matches(StoredStrings, "key ").Count);
    }

    [Fact]
    public void ObservedFormattedDateTime()
    {
        var expected = Regex.Match(FormattedDt, @"'([^']*)'").Groups[1].Value;
        var k1 = Raw("<condition attribute=\"ppds_key\" operator=\"eq\" value=\"1\" />", "", "<attribute name=\"ppds_dt\" />").Entities.Single();
        Assert.Equal(expected, k1.FormattedValues["ppds_dt"]);
    }

    // ───────────── The audit's OBSERVED lines, verbatim ─────────────

    public static IEnumerable<object[]> KeyCases() =>
    [
        [@"FetchXML ne num 5: 32 rows, keys 1,3,4,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,33,34,35,36 (T-SQL differs: extra 6,15,20,25,30,35)", @"<condition attribute=""ppds_num"" operator=""ne"" value=""5"" />"],
        [@"FetchXML not-in num (5,10): 30 rows, keys 3,4,6,7,8,9,10,11,12,13,14,15,16,17,18,20,21,22,23,24,25,26,27,28,29,30,33,34,35,36 (T-SQL differs: extra 6,15,20,25,30,35)", @"<condition attribute=""ppds_num"" operator=""not-in""><value>5</value><value>10</value></condition>"],
        [@"FetchXML not-like name 'A%': 24 rows, keys 4,5,6,7,8,9,10,11,12,17,18,19,20,21,23,24,25,26,27,28,29,31,32,33 (T-SQL differs: extra 18,19)", @"<condition attribute=""ppds_name"" operator=""not-like"" value=""A%"" />"],
        [@"FetchXML not-between num 0..10: 15 rows, keys 3,7,8,9,13,14,16,17,23,24,26,27,29,33,36 (same as T-SQL)", @"<condition attribute=""ppds_num"" operator=""not-between""><value>0</value><value>10</value></condition>"],
        [@"FetchXML not-begin-with name 'A': 24 rows, keys 4,5,6,7,8,9,10,11,12,17,18,19,20,21,23,24,25,26,27,28,29,31,32,33 (T-SQL differs: extra 18,19)", @"<condition attribute=""ppds_name"" operator=""not-begin-with"" value=""A"" />"],
        [@"FetchXML not-end-with name 'a': 31 rows, keys 5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36 (T-SQL differs: extra 18,19)", @"<condition attribute=""ppds_name"" operator=""not-end-with"" value=""a"" />"],
        [@"FetchXML ne dt 2024-01-15T00:30:00Z: 35 rows, keys 2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36 (T-SQL differs: extra 4,20,30)", @"<condition attribute=""ppds_dt"" operator=""ne"" value=""2024-01-15T00:30:00Z"" />"],
        [@"FetchXML eq name """" (what WHERE name = NULL is sent as): 0 rows, keys  (same as T-SQL)", @"<condition attribute=""ppds_name"" operator=""eq"" value="""" />"],
        [@"FetchXML like name '[ab]%': 14 rows, keys 1,2,3,4,13,14,15,16,17,22,30,34,35,36 (same as T-SQL)", @"<condition attribute=""ppds_name"" operator=""like"" value=""[ab]%"" />"],
        [@"FetchXML begins-with name '[ab]' (what LIKE '[ab]%' is sent as): 14 rows, keys 1,2,3,4,13,14,15,16,17,22,30,34,35,36 (same as T-SQL)", @"<condition attribute=""ppds_name"" operator=""begins-with"" value=""[ab]"" />"],
        [@"FetchXML begins-with name 'a_' : 12 rows, keys 1,2,3,13,14,15,16,22,30,34,35,36 (T-SQL differs: extra 1,2,3,13,14,15,16,22,30,34,36)", @"<condition attribute=""ppds_name"" operator=""begins-with"" value=""a_"" />"],
        [@"FetchXML begins-with name '50%' : 1 rows, keys 9 (same as T-SQL)", @"<condition attribute=""ppds_name"" operator=""begins-with"" value=""50%"" />"],
        [@"FetchXML like name 'a[_]c': 1 rows, keys 35 (same as T-SQL)", @"<condition attribute=""ppds_name"" operator=""like"" value=""a[_]c"" />"],
        [@"FetchXML eq name 'alpha': 4 rows, keys 1,2,3,30", @"<condition attribute=""ppds_name"" operator=""eq"" value=""alpha"" />"],
        [@"FetchXML eq name 'cafe': keys 5,6 (accent-insensitive)", @"<condition attribute=""ppds_name"" operator=""eq"" value=""cafe"" />"],
        [@"FetchXML eq name 'trail' (stored 'trail  '): 1 rows, keys 8", @"<condition attribute=""ppds_name"" operator=""eq"" value=""trail"" />"],
        [@"FetchXML eq name 'lead' (stored '  lead'): 0 rows, keys", @"<condition attribute=""ppds_name"" operator=""eq"" value=""lead"" />"],
        [@"FetchXML eq dt '2024-01-15T00:30:00' (no offset): 0 rows, keys  (T-SQL differs: extra ; missing 1)", @"<condition attribute=""ppds_dt"" operator=""eq"" value=""2024-01-15T00:30:00"" />"],
        [@"FetchXML eq dt '2024-01-15T00:30:00Z': 1 rows, keys 1 (same as T-SQL)", @"<condition attribute=""ppds_dt"" operator=""eq"" value=""2024-01-15T00:30:00Z"" />"],
        [@"FetchXML ge dt '2024-01-15' and lt dt '2024-01-16' (no offset): 1 rows, keys 2 (T-SQL differs: extra ; missing 1)", @"<condition attribute=""ppds_dt"" operator=""ge"" value=""2024-01-15"" /><condition attribute=""ppds_dt"" operator=""lt"" value=""2024-01-16"" />"],
        [@"FetchXML eq donly '2024-01-15': 1 rows, keys 1 (same as T-SQL)", @"<condition attribute=""ppds_donly"" operator=""eq"" value=""2024-01-15"" />"],
    ];

    public static IEnumerable<object[]> OrderCases() =>
    [
        [@"FetchXML order by name asc: NULL NULL '  lead' '[bracket]' '100' '20' '50% off' 'a%c' 'a[b]c' 'a_c' 'ab' 'Ab' 'abc' 'abd' 'Alpha' 'alpha' 'ALPHA' 'Alpha ' 'Ångström' 'b' 'Beta' 'c%d' 'Café' 'cafe' 'dup' 'dup' 'e_f' 'naïve' 'O'Brien' 'trail  ' 'under_score' 'underXscore' 'x' 'X' 'Zeta' 'zz'", @"<order attribute=""ppds_name"" /><order attribute=""ppds_key"" />", "ppds_name"],
        [@"FetchXML order by num asc (NULL position): NULL NULL NULL NULL NULL NULL -100 -8 -7 -4 -3 -2 -1 -1 0 0 1 3 4 5 5 5 5 7 7 8 8 10 10 11 12 13 14 14 50 100", @"<order attribute=""ppds_num"" /><order attribute=""ppds_key"" />", "ppds_num"],
        [@"FetchXML order by num desc (NULL position): 100 50 14 14 13 12 11 10 10 8 8 7 7 5 5 5 5 4 3 1 0 0 -1 -1 -2 -3 -4 -7 -8 -100 NULL NULL NULL NULL NULL NULL", @"<order attribute=""ppds_num"" descending=""true"" /><order attribute=""ppds_key"" />", "ppds_num"],
        [@"FetchXML order by choice asc (label or value?): NULL NULL NULL NULL NULL NULL NULL NULL 'Blue=374110001' 'Blue=374110001' 'Blue=374110001' 'Blue=374110001' 'Blue=374110001' 'Blue=374110001' 'Blue=374110001' 'Blue=374110001' 'Blue=374110001' 'Green=374110002' 'Green=374110002' 'Green=374110002' 'Green=374110002' 'Green=374110002' 'Green=374110002' 'Green=374110002' 'Green=374110002' 'Green=374110002' 'Red=374110000' 'Red=374110000' 'Red=374110000' 'Red=374110000' 'Red=374110000' 'Red=374110000' 'Red=374110000' 'Red=374110000' 'Red=374110000' 'Red=374110000'", @"<order attribute=""ppds_colorcode"" /><order attribute=""ppds_key"" />", "ppds_colorcode"],
    ];

    private const string StoredStrings = @"stored string values (SDK read-back): key 4 name='Beta' memo=NULL, key 7 name='  lead' memo='line1
line2', key 8 name='trail  ' memo=NULL, key 30 name='Alpha ' memo='memo 30'";

    private const string FormattedDt = @"formatted value of ppds_dt stored as 2024-01-15T00:30:00 (UTC): '1/14/2024 6:30 PM' (the caller's time-zone setting decides this)";
}
