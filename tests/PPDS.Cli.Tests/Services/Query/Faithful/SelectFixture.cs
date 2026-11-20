using Microsoft.Xrm.Sdk;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// The rows the faithful SELECT tests run against: ten accounts keyed by <c>accountnumber</c> k1..k10 and five contacts
/// keyed by <c>lastname</c>. The data has NULLs in every nullable column, case and accent variants, a trailing space,
/// a LIKE wildcard character in a value, negative and zero numbers, and choice labels whose alphabetical order differs from
/// their value order. A two-option column (donotemail) is never NULL: the audit observed that an unset one reads back
/// as 0 ("two-option column with no value written reads back as: 0").
/// <code>
/// key  name      employees description customertype revenue followupby (UTC)    startdate   donotemail
/// k1   Alpha     5         NULL        1 Supplier   100     2024-01-15 00:30   2024-01-15  true
/// k2   alpha     10        d2          2 Customer   NULL    2024-01-15 12:00   NULL        false
/// k3   Beta      NULL      d3          3 Partner    250.5   NULL               2024-02-29  false
/// k4   Café      0         NULL        NULL         0       2024-02-29 00:00   NULL        false
/// k5   cafe      -3        x           1 Supplier   1000    2023-12-31 23:59   2023-12-31  true
/// k6   NULL      7         d6          2 Customer   NULL    NULL               NULL        false
/// k7   "trail "  5         d7          NULL         50      2024-06-01 00:00   NULL        true
/// k8   a_c       100       NULL        3 Partner    75      2024-01-16 06:00   2024-01-16  false
/// k9   Zeta      NULL      NULL        1 Supplier   10      NULL               NULL        false
/// k10  abc       12        D2          2 Customer   500     2024-03-01 00:00   2024-03-01  true
/// </code>
/// Contacts (lastname, parentcustomerid): Smith→k1, Jones→k1, Brown→k2, Green→k5, Lee→NULL; only Smith has a
/// description ("x", the same value as account k5's description).
/// k3's parent account is k1 (parentaccountid); every other account has none. createdon is 2023-01-&lt;key&gt; 08:00 UTC.
/// </summary>
internal static class SelectFixture
{
    public static Guid AccountId(int key) => FakeTable.DeterministicId("account", key);

    /// <summary>The name of account k<paramref name="key"/>, as a lookup to it carries it (audit V11, V12).</summary>
    public static string AccountName(int key) => key switch { 1 => "Alpha", 2 => "alpha", 5 => "cafe", _ => throw new ArgumentOutOfRangeException(nameof(key)) };

    public static FakeDataverse Create() => new FakeDataverse().Add(Accounts()).Add(Contacts());

    public static FakeTable Accounts()
    {
        var table = new FakeTable("account")
            .Column("createdon", FakeColumnType.DateTime)
            .Column("accountnumber", FakeColumnType.String)
            .Column("name", FakeColumnType.String)
            .Column("numberofemployees", FakeColumnType.Integer)
            .Column("description", FakeColumnType.String)
            .Choice("customertypecode", (1, "Supplier"), (2, "Customer"), (3, "Partner"))
            .Column("revenue", FakeColumnType.Money)
            .Column("followupby", FakeColumnType.DateTime)
            .Column("ppds_startdate", FakeColumnType.DateOnly)
            .Column("donotemail", FakeColumnType.Boolean)
            .Column("parentaccountid", FakeColumnType.Lookup);

        void Add(int key, string? name, int? employees, string? description, int? type, decimal? revenue, DateTime? followUp,
            DateTime? start, bool? doNotEmail, int? parent = null)
            => table.Row(
                ("accountid", AccountId(key)),
                ("createdon", Utc(2023, 1, key, 8, 0)),
                ("accountnumber", "k" + key),
                ("name", name),
                ("numberofemployees", employees),
                ("description", description),
                ("customertypecode", type is null ? null : new OptionSetValue(type.Value)),
                ("revenue", revenue is null ? null : new Money(revenue.Value)),
                ("followupby", followUp),
                ("ppds_startdate", start),
                ("donotemail", doNotEmail),
                ("parentaccountid", parent is null ? null : new EntityReference("account", AccountId(parent.Value)) { Name = AccountName(parent.Value) }));

        Add(1, "Alpha", 5, null, 1, 100m, Utc(2024, 1, 15, 0, 30), Date(2024, 1, 15), true);
        Add(2, "alpha", 10, "d2", 2, null, Utc(2024, 1, 15, 12, 0), null, false);
        Add(3, "Beta", null, "d3", 3, 250.5m, null, Date(2024, 2, 29), false, parent: 1);
        Add(4, "Café", 0, null, null, 0m, Utc(2024, 2, 29, 0, 0), null, false);
        Add(5, "cafe", -3, "x", 1, 1000m, Utc(2023, 12, 31, 23, 59), Date(2023, 12, 31), true);
        Add(6, null, 7, "d6", 2, null, null, null, false);
        Add(7, "trail ", 5, "d7", null, 50m, Utc(2024, 6, 1, 0, 0), null, true);
        Add(8, "a_c", 100, null, 3, 75m, Utc(2024, 1, 16, 6, 0), Date(2024, 1, 16), false);
        Add(9, "Zeta", null, null, 1, 10m, null, null, false);
        Add(10, "abc", 12, "D2", 2, 500m, Utc(2024, 3, 1, 0, 0), Date(2024, 3, 1), true);
        return table;
    }

    public static FakeTable Contacts()
    {
        var table = new FakeTable("contact")
            .Column("createdon", FakeColumnType.DateTime)
            .Column("lastname", FakeColumnType.String)
            .Column("description", FakeColumnType.String)
            .Column("parentcustomerid", FakeColumnType.Lookup);
        foreach (var (name, parent) in new (string, int?)[] { ("Smith", 1), ("Jones", 1), ("Brown", 2), ("Green", 5), ("Lee", null) })
        {
            table.Row(("createdon", Utc(2023, 2, table.Rows.Count + 1, 8, 0)), ("lastname", name), ("description", name == "Smith" ? "x" : null),
                ("parentcustomerid", parent is null ? null : new EntityReference("account", AccountId(parent.Value)) { Name = AccountName(parent.Value) }));
        }
        return table;
    }

    private static DateTime Utc(int y, int m, int d, int h, int min) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private static DateTime Date(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
}
