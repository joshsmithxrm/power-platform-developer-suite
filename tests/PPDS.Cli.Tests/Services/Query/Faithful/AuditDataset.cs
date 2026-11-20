using Microsoft.Xrm.Sdk;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// The November 2026 live SELECT audit's dataset (SelectFidelityLiveTests.cs on the test/select-fidelity-live branch:
/// <c>CreateSchemaAsync</c>, <c>ParentSeeds</c> and <c>SeedAsync</c>, ported unchanged), as the audit read it back: a parent
/// table of 36 rows, a child table of 24 rows with a lookup to the parent, and a bulk table of 5,050 rows. Table and id
/// names are <see cref="P"/>, <see cref="C"/>, <see cref="B"/> and their <c>id</c> columns; the audit's report writes them
/// as P, C, B, Pid, Cid and Bid.
/// <para>
/// Read-back facts the audit OBSERVED are applied: an unset two-option reads back 0, a stored empty string reads back NULL
/// (the fake treats "" as NULL), and the choice values are Red=374110000, Blue=374110001, Green=374110002. A lookup carries
/// its parent's name (V11, V12). Ids the report shows are the live ids (<see cref="RecordedIds"/>) and the replay compares them; the other ids are generated and the replay masks them.
/// <c>createdon</c> is present because every table has it; its values are invented, not the live ones. Replayed cases do
/// read it: the engine sends queries ordered on <c>createdon</c> for aggregate cases (the shapes "order ascending DateTime
/// (createdon)" and "order descending DateTime (createdon)" in <see cref="ObservedShapes"/>). No recorded result shows a
/// <c>createdon</c> value; LiveObservationsOwed.md lists the live values owed.
/// </para>
/// </summary>
internal static class AuditDataset
{
    public const string P = "ppds_sqlp";
    public const string C = "ppds_sqlc";
    public const string B = "ppds_sqlb";
    public const int Red = 374110000, Blue = 374110001, Green = 374110002;

    internal sealed record PSeed(int Key, string? Name, string? Memo, int? Num, int? Num2, decimal? Dec, decimal? Money, double? Flt,
        bool? Flag, DateTime? Dt, DateTime? DOnly, int? Color, int[]? Multi, string? ItemType, string? ItemTypeName);

    private static DateTime U(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);
    private static DateTime D(int y, int mo, int d) => new(y, mo, d, 0, 0, 0, DateTimeKind.Unspecified);

    internal static List<PSeed> ParentSeeds()
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


    private static readonly Lazy<FakeDataverse> Shared = new(Build);

    /// <summary>The dataset, built once; the fake holds no per-query state the replay depends on.</summary>
    public static FakeDataverse Dataverse => Shared.Value;

    /// <summary>
    /// The live run's record ids, read from the rows the audit's report shows in full: 35 of the 36 parent ids (key 8's
    /// appears 6 times, and one stray row in a joined case pairs it with key 11's id, so the majority is taken), 12 of the
    /// 24 child ids (child key 3 from J13: the only child named 'x' under parent 'Beta' with key &lt;= 5), and 11 of the 5,050
    /// bulk ids (PG01 to PG06). Key 27's id is shown only cut short (W35: <c>84c24e9c-a9af-4bba-83b4-bf2…</c>); its recorded
    /// prefix is kept and the rest is zeros, which fixes its place in id order (the last group is compared first) but not
    /// its value, so it is not in <see cref="RecordedIds"/>. Two more ids are shown only cut short and are not adopted: bulk key
    /// 4 (PG02, PG05: <c>eafb57ca-f124-4fec-9…</c>) and child key 5 (J13: <c>216…</c>). Their prefixes stop before the last
    /// group, which SQL Server compares first, so they fix nothing about the order, and no row the record shows in full holds
    /// them. Using them lets <c>WHERE Pid = '...'</c> (W113, W114) and the
    /// order Dataverse returns rows in when nothing else decides (by id) replay. Ids the report never shows are generated.
    /// </summary>
    public static Guid Id(string table, int key)
        => (table == P ? LiveParentIds : table == C ? LiveChildIds : table == B ? LiveBulkIds : null)?.GetValueOrDefault(key) is { } live && live != Guid.Empty
            ? live
            : table == P && key == 27
            ? Key27FromItsRecordedPrefix
            : FakeTable.DeterministicId(table, key);

    /// <summary>The ids taken from the report (live ids); every other id here is generated and means nothing live.</summary>
    public static IReadOnlySet<Guid> RecordedIds => RecordedIdSet.Value;

    private static readonly Lazy<HashSet<Guid>> RecordedIdSet =
        new(() => [.. LiveParentIds.Values.Concat(LiveChildIds.Values).Concat(LiveBulkIds.Values).Where(g => g != Guid.Empty)]);

    /// <summary>W35 shows key 27's id cut after <c>84c24e9c-a9af-4bba-83b4-bf2</c>; the rest is filled with zeros.</summary>
    private static readonly Guid Key27FromItsRecordedPrefix = Guid.Parse("84c24e9c-a9af-4bba-83b4-bf2000000000");

    private static readonly Dictionary<int, Guid> LiveBulkIds = new()
    {
        [1] = Guid.Parse("7adf238c-7a36-481b-b75a-0a9dc3c7c6ad"),
        [2] = Guid.Parse("aa753305-5ebe-466c-b99c-e334c1175588"),
        [3] = Guid.Parse("f65ba8be-cec9-49c3-b1d7-c772011316d4"),
        [12] = Guid.Parse("f98a58ba-6411-4428-81e5-005b501b0588"),
        [443] = Guid.Parse("f2f691cf-8e38-4de3-897b-0019fc6b2da2"),
        [596] = Guid.Parse("eb4c6572-81b1-462c-9ae1-004e1b055457"),
        [1565] = Guid.Parse("caddba35-e87b-491d-98d3-002bdf364616"),
        [4640] = Guid.Parse("b5ac6c28-ca7c-43f7-b80f-0022e32755eb"),
        [5048] = Guid.Parse("488ac01b-a454-4459-bc83-59fe1ce6d1b5"),
        [5049] = Guid.Parse("237e1a05-f8aa-4548-aa24-06ef04d6e14c"),
        [5050] = Guid.Parse("76a76eb3-8c5d-4a5c-b6a0-4fd34419f541"),
    };

    private static readonly Dictionary<int, Guid> LiveParentIds = new()
    {
        [1] = Guid.Parse("a3a6e420-0107-49f7-86cf-52cfb3670557"),
        [2] = Guid.Parse("ad6a1d7e-4347-43da-aa01-b99c95af9d19"),
        [3] = Guid.Parse("a21b1361-2d6a-4f74-ad3e-7c0901cf773d"),
        [4] = Guid.Parse("9bd7c97d-3a90-4c34-8efe-79071bdfaa1d"),
        [5] = Guid.Parse("b4614f71-b3bd-468b-b5b0-7acc0fd7265f"),
        [6] = Guid.Parse("91fd4404-abd5-457e-964d-d1b26488f8b6"),
        [7] = Guid.Parse("d41a87ef-8dec-49e5-8695-9ef1e154f140"),
        [8] = Guid.Parse("6c22afdc-c20f-4014-80f2-c8649cd65117"),
        [9] = Guid.Parse("54145faa-32dc-4e42-9366-40de4534d28f"),
        [10] = Guid.Parse("112c5d64-8325-413e-b5f2-c0f2e61e8168"),
        [11] = Guid.Parse("2187e72a-b7fd-4c90-9dc3-00747904f1d6"),
        [12] = Guid.Parse("36d04de7-3ac5-4a2f-86de-8374a4b42438"),
        [13] = Guid.Parse("4fae23f5-a7e4-40bf-bdf8-46b6802755bc"),
        [14] = Guid.Parse("60bb146f-a4ec-4766-bc3c-c5dac8cd42ae"),
        [15] = Guid.Parse("b87ccc65-348e-4a1a-a649-9e47169a975d"),
        [16] = Guid.Parse("d39cc76a-4707-485e-8e48-96194aeb3447"),
        [17] = Guid.Parse("cef08609-fe31-425d-bde2-0467d85acf2b"),
        [18] = Guid.Parse("a1ddf183-abf8-4a72-b895-052d247e1f11"),
        [19] = Guid.Parse("a7e6a49f-8152-48ae-aadb-0ac3b5832031"),
        [20] = Guid.Parse("9cb51da5-d478-4364-9afc-65231206d1a7"),
        [21] = Guid.Parse("4830683c-08f9-4f66-b300-990fafa15687"),
        [22] = Guid.Parse("c31db6fd-5cb6-44f8-82fd-736c4a76c41c"),
        [23] = Guid.Parse("5478f900-a37b-4c4d-81a7-54de6977c732"),
        [24] = Guid.Parse("c7d8744a-8b34-4694-b238-297466f4cbfe"),
        [25] = Guid.Parse("388e9daf-ce32-4c86-a134-8fd6eac85277"),
        [26] = Guid.Parse("e21592ce-add9-4c0f-888c-4481f52295a3"),
        [28] = Guid.Parse("4271d2df-a1a6-4b9e-9999-79b6912feb53"),
        [29] = Guid.Parse("4f5ab2a7-cf55-4309-b92b-16d741d6353d"),
        [30] = Guid.Parse("49475e20-09c6-4c7c-8848-4c06b76ad445"),
        [31] = Guid.Parse("d2905ff0-efae-499d-9deb-e62d7e2fa8d8"),
        [32] = Guid.Parse("3a36e40c-1ceb-4f32-b11d-d6ca14b47fb4"),
        [33] = Guid.Parse("06b3cfb1-3beb-4958-a305-6f3233ebbb62"),
        [34] = Guid.Parse("68b291c8-3825-4490-84d3-f15894cf72e9"),
        [35] = Guid.Parse("0840f835-4ea0-4912-a2f9-9369a4cc548c"),
        [36] = Guid.Parse("cf40f189-5ee8-4314-b131-6963f287a78e")
    };

    private static readonly Dictionary<int, Guid> LiveChildIds = new()
    {
        [1] = Guid.Parse("d668c4f1-a410-4701-a1bc-20fdf9a8cf05"),
        [2] = Guid.Parse("03c5c3a1-5369-4c96-89fe-001cce22b902"),
        [3] = Guid.Parse("5f0d4bd4-affa-4251-a357-ad58cf9c0c9e"),
        [4] = Guid.Parse("255db75e-f762-4f12-bb95-f6b6986a8203"),
        [7] = Guid.Parse("b19030d7-ea44-4278-bb13-770d8ea9406c"),
        [8] = Guid.Parse("6f9dca41-ea05-4a64-a997-f06197fde3ac"),
        [9] = Guid.Parse("eb16a58a-9434-48fc-b403-1bc57c2e2233"),
        [14] = Guid.Parse("15acda3f-c208-45e6-a79c-3a2ef4d357a9"),
        [15] = Guid.Parse("af14d8e0-856a-436d-bcf6-8de4c184a784"),
        [16] = Guid.Parse("2ef8c003-1496-48e4-a36f-bbf0df77f7fe"),
        [21] = Guid.Parse("fae391e7-6fba-465b-9d8c-27e45f5af1dc"),
        [23] = Guid.Parse("a9b2d78d-0f35-42a1-988b-596cd20dabe2")
    };


    private static FakeDataverse Build()
    {
        var created = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        int[] colorValues = [Red, Blue, Green];
        var parent = new FakeTable(P)
            .Column("createdon", FakeColumnType.DateTime)
            .Column("ppds_key", FakeColumnType.Integer).Column("ppds_name", FakeColumnType.String).Column("ppds_memo", FakeColumnType.String)
            .Column("ppds_num", FakeColumnType.Integer).Column("ppds_num2", FakeColumnType.Integer).Column("ppds_dec", FakeColumnType.Decimal)
            .Column("ppds_money", FakeColumnType.Money).Column("ppds_flt", FakeColumnType.Double).Column("ppds_flag", FakeColumnType.Boolean)
            .Column("ppds_dt", FakeColumnType.DateTime).Column("ppds_donly", FakeColumnType.DateOnly)
            .Choice("ppds_colorcode", (Red, "Red"), (Blue, "Blue"), (Green, "Green"))
            .Choice("ppds_multi", (Red, "Red"), (Blue, "Blue"), (Green, "Green"))
            .Column("ppds_itemtype", FakeColumnType.String).Column("ppds_itemtypename", FakeColumnType.String)
            // System columns: every row Active (audit V05: statecodename and statuscodename 'Active').
            .Choice("statecode", (0, "Active"), (1, "Inactive")).Choice("statuscode", (1, "Active"), (2, "Inactive"));
        parent.Column("ppds_multi", FakeColumnType.MultiChoice);
        var names = new Dictionary<int, string?>();
        foreach (var s in ParentSeeds())
        {
            names[s.Key] = s.Name;
            parent.Row(("ppds_sqlpid", Id(P, s.Key)), ("createdon", created.AddSeconds(s.Key)), ("ppds_key", s.Key), ("ppds_name", s.Name),
                ("ppds_memo", s.Memo), ("ppds_num", s.Num), ("ppds_num2", s.Num2), ("ppds_dec", s.Dec),
                ("ppds_money", s.Money is { } m ? new Money(m) : null), ("ppds_flt", s.Flt), ("ppds_flag", s.Flag ?? false),
                ("ppds_dt", s.Dt), ("ppds_donly", s.DOnly),
                ("ppds_colorcode", s.Color is { } c ? new OptionSetValue(colorValues[c]) : null),
                ("ppds_multi", s.Multi is { } mu ? new OptionSetValueCollection(mu.Select(i => new OptionSetValue(colorValues[i])).ToList()) : null),
                ("ppds_itemtype", s.ItemType), ("ppds_itemtypename", s.ItemTypeName),
                ("statecode", new OptionSetValue(0)), ("statuscode", new OptionSetValue(1)));
        }

        string?[] childNames = ["Alpha", "beta", "Café", "x", null, "dup", "Zeta", "alpha"];
        var child = new FakeTable(C)
            .Column("createdon", FakeColumnType.DateTime)
            .Column("ppds_key", FakeColumnType.Integer).Column("ppds_name", FakeColumnType.String)
            .Column("ppds_qty", FakeColumnType.Integer).Column("ppds_num", FakeColumnType.Integer)
            .Column("ppds_parentid", FakeColumnType.Lookup);
        for (var k = 1; k <= 24; k++)
        {
            var parentKey = (k % 9) + 1;
            child.Row(("ppds_sqlcid", Id(C, k)), ("createdon", created.AddMinutes(1).AddSeconds(k)), ("ppds_key", k), ("ppds_name", childNames[k % 8]),
                ("ppds_parentid", k % 6 != 0 ? new EntityReference(P, Id(P, parentKey)) { Name = names[parentKey] } : null),
                ("ppds_qty", k % 4 != 0 ? k * 3 - 20 : null),
                ("ppds_num", k % 5 != 0 ? k % 7 : null));
        }

        var bulk = new FakeTable(B).Column("createdon", FakeColumnType.DateTime)
            .Column("ppds_name", FakeColumnType.String).Column("ppds_n", FakeColumnType.Integer);
        for (var k = 1; k <= 5050; k++)
            bulk.Row(("ppds_sqlbid", Id(B, k)), ("createdon", created.AddMinutes(2).AddSeconds(k)), ("ppds_name", $"b{k:D5}"), ("ppds_n", k));

        return new FakeDataverse().Add(parent).Add(child).Add(bulk);
    }
}
