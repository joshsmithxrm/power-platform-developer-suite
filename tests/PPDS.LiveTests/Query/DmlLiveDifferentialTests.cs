using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Query;

/// <summary>
/// Live differential proof of the DML allowlist (specs/query.md "DML allowlist"). Creates one throwaway table in a
/// throwaway solution, seeds a fully known set of rows (with NULLs in every column), and for every allowlisted shape runs
/// the real statement through the real <see cref="SqlQueryService"/> with confirmation, then compares the table's actual
/// contents with the result computed independently here from the known rows (T-SQL semantics). One statement of every
/// refused category is run too, to confirm nothing changed. The table and solution are deleted at the end.
/// Only for https://orgcabef92d.crm.dynamics.com with a solution named ppds_DmlLive*.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DmlLiveDifferentialTests : LiveTestBase
{
    private const string ExpectedUrl = "https://orgcabef92d.crm.dynamics.com";
    private readonly ITestOutputHelper _output;
    private readonly List<string> _failures = new();

    public DmlLiveDifferentialTests(ITestOutputHelper output) => _output = output;

    private static string Solution => Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!.Trim();

    /// <summary>A row of the throwaway table, as the test models it.</summary>
    private sealed record Row(Guid Id, string? Name, int? Num, decimal? Amount, DateTime? When, bool Flag, int? Choice, string? Note)
    {
        public string Key => $"{Name ?? "<null>"}|{Num?.ToString(CultureInfo.InvariantCulture) ?? "<null>"}|{Amount?.ToString("0.00", CultureInfo.InvariantCulture) ?? "<null>"}|" +
                             $"{When?.ToString("O") ?? "<null>"}|{Flag}|{Choice?.ToString(CultureInfo.InvariantCulture) ?? "<null>"}|{Note ?? "<null>"}";
    }

    private string _table = "";
    private int[] _choices = [];
    private Microsoft.PowerPlatform.Dataverse.Client.ServiceClient _client = null!;
    private SqlQueryService _service = null!;
    private List<Row> _seed = new();

    [MetadataLiveFact]
    public async Task EveryAllowlistedShape_ChangesExactlyTheRowsAndValuesItsTextSays()
    {
        Assert.Equal(ExpectedUrl, Configuration.DataverseUrl?.TrimEnd('/'));
        Assert.StartsWith("ppds_DmlLive", Solution, StringComparison.Ordinal);

        using var source = await LiveTestProfileConnectionSource.CreateAsync(Configuration, "DmlLiveDifferentialTests", maxPoolSize: 2);
        var seedClient = source.GetSeedClient();
        var connected = seedClient.ConnectedOrgUriActual?.GetLeftPart(UriPartial.Authority);
        Assert.Equal(ExpectedUrl, connected);
        _output.WriteLine($"Connected to {connected} ({seedClient.ConnectedOrgFriendlyName}).");
        _client = seedClient;

        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });
        var bulk = new BulkOperationExecutor(pool, LiveTestHelpers.CreateThrottleTracker(),
            Options.Create(new DataverseOptions { BulkOperations = new BulkOperationOptions { BatchSize = 10, MaxParallelBatches = 1 } }),
            NullLogger<BulkOperationExecutor>.Instance);
        _service = new SqlQueryService(new QueryExecutor(pool), new Guard(), bulkOperationExecutor: bulk,
            metadataProvider: new CachedMetadataProvider(new DataverseMetadataQueryService(pool)));
        _service.ConfigureEnvironment(ExpectedUrl, null);
        _service.EnvironmentProtectionLevel = ProtectionLevel.Development;

        var createdSolution = await CreateSolutionAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        _table = $"ppds_dmllive{suffix}";
        try
        {
            await CreateTableAsync();
            _seed = await SeedAsync();
            await RunAllCasesAsync();
        }
        finally
        {
            await CleanupAsync(createdSolution);
        }

        Assert.True(_failures.Count == 0, "Differences between observed and expected:\n" + string.Join("\n", _failures));
    }

    // ── cases ───────────────────────────────────────────────────────────────────────────────

    private async Task RunAllCasesAsync()
    {
        var t = _table;
        int C(int i) => _choices[i];

        // WHERE: every operator and its negation, NULLs present. UPDATE ... SET note = 'HIT' marks the matched rows.
        var where = new (string Sql, Func<Row, bool> Match)[]
        {
            ("ppds_num = 2", r => r.Num == 2),
            ("ppds_num <> 2", r => r.Num is not null && r.Num != 2),
            ("ppds_num != 2", r => r.Num is not null && r.Num != 2),
            ("ppds_num < 2", r => r.Num < 2),
            ("ppds_num > 2", r => r.Num > 2),
            ("ppds_num <= 2", r => r.Num <= 2),
            ("ppds_num >= 2", r => r.Num >= 2),
            ("ppds_num = -1", _ => false),
            ("ppds_amount = 10.5", r => r.Amount == 10.5m),
            ("ppds_amount > 20", r => r.Amount > 20),
            ("ppds_name = 'alpha'", r => Eq(r.Name, "alpha")),
            ("ppds_name = 'ALPHA'", r => Eq(r.Name, "alpha")),
            ("ppds_name <> 'alpha'", r => r.Name is not null && !Eq(r.Name, "alpha")),
            ("ppds_name IN ('alpha', 'gamma')", r => Eq(r.Name, "alpha") || Eq(r.Name, "gamma")),
            ("ppds_name NOT IN ('alpha', 'gamma')", r => r.Name is not null && !Eq(r.Name, "alpha") && !Eq(r.Name, "gamma")),
            ("ppds_name LIKE 'al%'", r => Like(r.Name, "al%")),
            ("ppds_name LIKE '%mm%'", r => Like(r.Name, "%mm%")),
            ("ppds_name LIKE '%a'", r => Like(r.Name, "%a")),
            ("ppds_name LIKE 'alp_ha'", r => Like(r.Name, "alp_ha")),
            ("ppds_name NOT LIKE 'al%'", r => r.Name is not null && !Like(r.Name, "al%")),
            ("ppds_num BETWEEN 1 AND 2", r => r.Num >= 1 && r.Num <= 2),
            ("ppds_num NOT BETWEEN 1 AND 2", r => r.Num is not null && !(r.Num >= 1 && r.Num <= 2)),
            ("ppds_num IS NULL", r => r.Num is null),
            ("ppds_num IS NOT NULL", r => r.Num is not null),
            ("ppds_note IS NULL", r => r.Note is null),
            ("ppds_num = 1 OR ppds_num = 2 AND ppds_amount > 100", r => r.Num == 1 || (r.Num == 2 && r.Amount > 100)),
            ("(ppds_num = 1 OR ppds_num = 2) AND ppds_amount IS NULL", r => (r.Num == 1 || r.Num == 2) && r.Amount is null),
            ("ppds_flag = 1", r => r.Flag),
            ("ppds_flag = 0", r => !r.Flag),
            ($"ppds_choice = {C(0)}", r => r.Choice == C(0)),
            ($"ppds_choice <> {C(0)}", r => r.Choice is not null && r.Choice != C(0)),
            ("ppds_when < '2024-01-01T00:00:00-01:00'", r => r.When < new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc)),
            ("ppds_when BETWEEN '2024-01-01T00:00:00Z' AND '2024-06-15T12:00:00Z'", r => r.When >= new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) && r.When <= new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc)),
            ("ppds_when IS NULL", r => r.When is null),
            ("ppds_when >= '2024-01-01T00:00:00Z'", r => r.When >= new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        };
        foreach (var (sql, match) in where)
            await CaseAsync($"UPDATE {t} SET ppds_note = 'HIT' WHERE {sql}", rows => rows.Select(r => match(r) ? r with { Note = "HIT" } : r).ToList(), observe: true);

        // SET values: every allowed function with NULL and non-NULL inputs (all rows).
        const string all = "ppds_name IS NULL OR ppds_name IS NOT NULL";
        var notes = new (string Expr, Func<Row, string?> Value)[]
        {
            ("COALESCE(ppds_note, ppds_name)", r => r.Note ?? r.Name),
            ("ISNULL(ppds_note, 'none')", r => r.Note ?? "none"),
            ("NULLIF(ppds_name, 'alpha')", r => Eq(r.Name, "alpha") ? null : r.Name),
            ("IIF(ppds_num > 2, 'big', 'small')", r => r.Num > 2 ? "big" : "small"),
            ("CASE WHEN ppds_amount > 20 THEN 'hi' ELSE 'lo' END", r => r.Amount > 20 ? "hi" : "lo"),
            ("CASE ppds_name WHEN 'alpha' THEN 'first' ELSE ppds_note END", r => Eq(r.Name, "alpha") ? "first" : r.Note),
            ("CASE WHEN ppds_note IS NULL THEN 'none' ELSE ppds_note END", r => r.Note ?? "none"),
            ("UPPER(ppds_name)", r => r.Name?.ToUpperInvariant()),
            ("LOWER(ppds_name)", r => r.Name?.ToLowerInvariant()),
            ("LTRIM(ppds_note)", r => r.Note?.TrimStart(' ')),
            ("RTRIM(ppds_note)", r => r.Note?.TrimEnd(' ')),
            ("TRIM(ppds_note)", r => r.Note?.Trim(' ')),
            ("CONCAT(ppds_name, '-', ppds_note)", r => (r.Name ?? "") + "-" + (r.Note ?? "")),
            ("SUBSTRING(ppds_name, 2, 3)", r => r.Name is null ? null : r.Name.Length < 2 ? "" : r.Name.Substring(1, Math.Min(3, r.Name.Length - 1))),
            ("REPLACE(ppds_name, 'a', 'o')", r => r.Name?.Replace("a", "o", StringComparison.OrdinalIgnoreCase)),
            ("CAST(ppds_num AS nvarchar(10))", r => r.Num?.ToString(CultureInfo.InvariantCulture)),
            ("CONVERT(nvarchar(10), ppds_num)", r => r.Num?.ToString(CultureInfo.InvariantCulture)),
            ("'lit'", _ => "lit"),
        };
        foreach (var (expr, value) in notes)
            await CaseAsync($"UPDATE {t} SET ppds_note = {expr} WHERE {all}", rows => rows.Select(r => r with { Note = value(r) }).ToList());

        var nums = new (string Expr, Func<Row, int?> Value)[]
        {
            ("LEN(ppds_note)", r => r.Note?.TrimEnd(' ').Length),
            ("ppds_num * 2 + 1", r => r.Num * 2 + 1),
            ("ABS(ppds_num - 3)", r => r.Num is null ? null : Math.Abs(r.Num.Value - 3)),
            ("-ppds_num", r => -r.Num),
            ("ppds_num % 2", r => r.Num % 2),
        };
        foreach (var (expr, value) in nums)
            await CaseAsync($"UPDATE {t} SET ppds_num = {expr} WHERE {all}", rows => rows.Select(r => r with { Num = value(r) }).ToList());
        await CaseAsync($"UPDATE {t} SET ppds_amount = ROUND(ppds_amount, 0) WHERE {all}",
            rows => rows.Select(r => r with { Amount = r.Amount is null ? null : Math.Round(r.Amount.Value, 0, MidpointRounding.AwayFromZero) }).ToList());

        // Alias target.
        await CaseAsync($"UPDATE a SET ppds_note = 'ALIAS' FROM {t} a WHERE a.ppds_num = 1",
            rows => rows.Select(r => r.Num == 1 ? r with { Note = "ALIAS" } : r).ToList());
        await CaseAsync($"DELETE a FROM {t} a WHERE a.ppds_num = 4", rows => rows.Where(r => r.Num != 4).ToList());

        // DELETE with NULLs present.
        await CaseAsync($"DELETE FROM {t} WHERE ppds_num <> 2", rows => rows.Where(r => !(r.Num is not null && r.Num != 2)).ToList(), observe: true);

        // INSERT VALUES and INSERT ... SELECT (TOP n, TOP (n), ORDER BY, aliased and computed columns).
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num, ppds_amount, ppds_flag, ppds_note) VALUES ('ins1', 7, 1.5, 1, NULL), ('ins2', NULL, NULL, 0, 'n')",
            rows => rows.Concat([new Row(Guid.Empty, "ins1", 7, 1.5m, null, true, null, null), new Row(Guid.Empty, "ins2", null, null, null, false, null, "n")]).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num) SELECT TOP 2 ppds_name, ppds_num FROM {t} WHERE ppds_num IS NOT NULL ORDER BY ppds_num",
            rows => rows.Concat(rows.Where(r => r.Num is not null).OrderBy(r => r.Num).Take(2).Select(r => new Row(Guid.Empty, r.Name, r.Num, null, null, false, null, null))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num) SELECT TOP (2) ppds_name, ppds_num FROM {t} WHERE ppds_num IS NOT NULL ORDER BY ppds_num DESC",
            rows => rows.Concat(rows.Where(r => r.Num is not null).OrderByDescending(r => r.Num).Take(2).Select(r => new Row(Guid.Empty, r.Name, r.Num, null, null, false, null, null))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_note) SELECT ppds_name AS n, UPPER(ppds_name) AS u FROM {t} WHERE ppds_num = 1",
            rows => rows.Concat(rows.Where(r => r.Num == 1).Select(r => new Row(Guid.Empty, r.Name, null, null, null, false, null, r.Name?.ToUpperInvariant()))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_note, ppds_name) SELECT ppds_name, COALESCE(ppds_note, 'none') AS c FROM {t} WHERE ppds_num >= 4",
            rows => rows.Concat(rows.Where(r => r.Num >= 4).Select(r => new Row(Guid.Empty, r.Note ?? "none", null, null, null, false, null, r.Name))).ToList());

        // Row cap: at the cap runs, one over is refused and changes nothing.
        var notNull = _seed.Count(r => r.Num is not null);
        await CaseAsync($"UPDATE {t} SET ppds_note = 'CAP' WHERE ppds_num IS NOT NULL",
            rows => rows.Select(r => r.Num is not null ? r with { Note = "CAP" } : r).ToList(), rowCap: notNull);
        await RefusedAsync($"UPDATE {t} SET ppds_note = 'CAP' WHERE ppds_num IS NOT NULL", rowCap: notNull - 1);

        // One statement of every refused category: nothing may change.
        foreach (var sql in new[]
        {
            $"UPDATE {t} SET ppds_note = 'x' WHERE NOT (ppds_num = 2)",
            $"UPDATE {t} SET ppds_note = 'x' WHERE UPPER(ppds_name) = 'ALPHA'",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name = NULL",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name = ppds_note",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name = 'ALPHA' COLLATE Latin1_General_CS_AS",
            $"UPDATE TOP (1) {t} SET ppds_note = 'x' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' FROM {t} JOIN {t} b ON b.ppds_num = {t}.ppds_num WHERE {t}.ppds_num = 1",
            $"UPDATE {t} SET ppds_note = owneridname WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = ppds_name + '-x' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE [ppds_num]] = 1 --] = 1",
            $"DELETE FROM {t} WHERE ppds_num IN (SELECT ppds_num FROM {t} WHERE ppds_num = 1)",
            $"INSERT INTO {t} (ppds_name) SELECT UPPER(ppds_name) FROM {t}",
            $"MERGE {t} AS x USING {t} AS y ON x.ppds_num = y.ppds_num WHEN NOT MATCHED THEN INSERT (ppds_name) VALUES ('m');",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1\nGO\nUPDATE {t} SET ppds_note = 'y' WHERE ppds_num = 2",
            $"DELETE FROM [QA].{t} WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_when < '2024-01-01'",
            $"UPDATE {t} SET ppds_when = '2024-01-01T00:00:00Z' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name = 'a' + 'b'",
            $"UPDATE {t} SET ppds_num = TRY_CAST(ppds_name AS int) WHERE ppds_num = 1",
        })
        {
            await RefusedAsync(sql);
        }
    }

    // ── one case ────────────────────────────────────────────────────────────────────────────

    private async Task CaseAsync(string sql, Func<List<Row>, List<Row>> expected, bool observe = false, int? rowCap = null)
    {
        await ResetAsync();
        var before = await ReadAsync();
        try
        {
            await _service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = rowCap } });
        }
        catch (Exception ex)
        {
            Fail(sql, $"threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var actual = await ReadAsync();
        var want = expected(before);
        if (observe)
        {
            var changed = actual.Where(a => before.Any(b => b.Id == a.Id && b.Key != a.Key)).Select(a => a.Name ?? "<null>");
            var removed = before.Where(b => actual.All(a => a.Id != b.Id)).Select(b => b.Name ?? "<null>");
            _output.WriteLine($"OBSERVED {sql} -> changed [{string.Join(", ", changed)}] removed [{string.Join(", ", removed)}]");
        }

        var wantKeys = want.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var actualKeys = actual.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (!wantKeys.SequenceEqual(actualKeys))
            Fail(sql, $"expected\n    {string.Join("\n    ", wantKeys)}\n  actual\n    {string.Join("\n    ", actualKeys)}");
        else
            _output.WriteLine($"PASS {sql}");
    }

    private async Task RefusedAsync(string sql, int? rowCap = null)
    {
        await ResetAsync();
        var before = (await ReadAsync()).Select(r => r.Key).OrderBy(k => k).ToList();
        try
        {
            await _service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = rowCap } });
            Fail(sql, "was expected to be refused but ran");
        }
        catch (PpdsException ex)
        {
            _output.WriteLine($"REFUSED ({ex.ErrorCode}) {sql}: {ex.UserMessage}");
        }

        var after = (await ReadAsync()).Select(r => r.Key).OrderBy(k => k).ToList();
        if (!before.SequenceEqual(after))
            Fail(sql, "a refused statement changed the table");
    }

    private void Fail(string sql, string what)
    {
        _failures.Add($"{sql}: {what}");
        _output.WriteLine($"FAIL {sql}: {what}");
    }

    private static bool Eq(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Like(string? value, string pattern) => value is not null
        && System.Text.RegularExpressions.Regex.IsMatch(value, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ── table, rows, cleanup ────────────────────────────────────────────────────────────────

    private async Task<bool> CreateSolutionAsync()
    {
        if (await FindSolutionAsync() is not null)
            return false;
        var publisherQuery = new QueryExpression("publisher") { ColumnSet = new ColumnSet("publisherid", "customizationprefix") };
        publisherQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, "PPDSDemoPublisher");
        var publisher = (await _client.RetrieveMultipleAsync(publisherQuery)).Entities.Single();
        Assert.Equal("ppds", publisher.GetAttributeValue<string>("customizationprefix"));
        _output.WriteLine($"Creating solution {Solution}.");
        await _client.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = Solution, ["friendlyname"] = Solution + " (disposable PPDS DML live test, safe to delete)",
            ["version"] = "1.0.0.0", ["publisherid"] = publisher.ToEntityReference()
        });
        return true;
    }

    private async Task<Entity?> FindSolutionAsync()
    {
        var q = new QueryExpression("solution") { ColumnSet = new ColumnSet("solutionid") };
        q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, Solution);
        return (await _client.RetrieveMultipleAsync(q)).Entities.FirstOrDefault();
    }

    private async Task CreateTableAsync()
    {
        _output.WriteLine($"Creating table {_table} in {Solution}.");
        await _client.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                SchemaName = _table, DisplayName = new Label("PPDS DML live test", 1033), DisplayCollectionName = new Label("PPDS DML live tests", 1033),
                Description = new Label("Temporary table for the PPDS DML live test. Safe to delete.", 1033), OwnershipType = OwnershipTypes.UserOwned
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                SchemaName = "ppds_name", MaxLength = 100, FormatName = StringFormatName.Text, DisplayName = new Label("Name", 1033),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
            },
            SolutionUniqueName = Solution
        });

        var none = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
        var choice = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
        foreach (var label in new[] { "One", "Two", "Three" })
            choice.Options.Add(new OptionMetadata(new Label(label, 1033), null));
        AttributeMetadata[] columns =
        [
            new IntegerAttributeMetadata { SchemaName = "ppds_num", MinValue = -100000, MaxValue = 100000, DisplayName = new Label("Num", 1033), RequiredLevel = none },
            new DecimalAttributeMetadata { SchemaName = "ppds_amount", Precision = 2, MinValue = -1000000, MaxValue = 1000000, DisplayName = new Label("Amount", 1033), RequiredLevel = none },
            new DateTimeAttributeMetadata { SchemaName = "ppds_when", Format = DateTimeFormat.DateAndTime, DisplayName = new Label("When", 1033), RequiredLevel = none },
            new BooleanAttributeMetadata { SchemaName = "ppds_flag", DisplayName = new Label("Flag", 1033), RequiredLevel = none,
                OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(new Label("Yes", 1033), 1), new OptionMetadata(new Label("No", 1033), 0)) },
            new PicklistAttributeMetadata { SchemaName = "ppds_choice", DisplayName = new Label("Choice", 1033), RequiredLevel = none, OptionSet = choice },
            new StringAttributeMetadata { SchemaName = "ppds_note", MaxLength = 200, FormatName = StringFormatName.Text, DisplayName = new Label("Note", 1033), RequiredLevel = none },
        ];
        foreach (var column in columns)
            await _client.ExecuteAsync(new CreateAttributeRequest { EntityName = _table, Attribute = column, SolutionUniqueName = Solution });

        var picklist = (PicklistAttributeMetadata)((RetrieveAttributeResponse)await _client.ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = _table, LogicalName = "ppds_choice", RetrieveAsIfPublished = true })).AttributeMetadata;
        _choices = picklist.OptionSet.Options.Select(o => o.Value!.Value).ToArray();
    }

    private Row[] SeedRows() =>
    [
        new(Guid.Empty, "alpha", 1, 10.5m, Utc(2024, 1, 1, 0, 0), true, _choices[0], "a"),
        new(Guid.Empty, "Beta", 2, null, null, false, _choices[1], null),
        new(Guid.Empty, "gamma", null, 30m, Utc(2024, 6, 15, 12, 0), false, null, "  pad  "),
        new(Guid.Empty, "alp_ha", 4, 40.25m, Utc(2023, 12, 31, 23, 30), true, _choices[2], "x"),
        new(Guid.Empty, null, 5, 50m, Utc(2025, 1, 1, 0, 0), false, _choices[0], "d5"),
    ];

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private async Task<List<Row>> SeedAsync()
    {
        var rows = new List<Row>();
        foreach (var row in SeedRows())
        {
            var id = await _client.CreateAsync(ToEntity(row));
            rows.Add(row with { Id = id });
        }
        return rows;
    }

    /// <summary>Restores exactly the seed rows: deletes anything else, rewrites every seed row, recreates deleted ones.</summary>
    private async Task ResetAsync()
    {
        var current = await ReadAsync();
        foreach (var extra in current.Where(c => _seed.All(s => s.Id != c.Id)))
            await _client.DeleteAsync(_table, extra.Id);
        for (var i = 0; i < _seed.Count; i++)
        {
            var row = _seed[i];
            if (current.Any(c => c.Id == row.Id))
            {
                if (current.Single(c => c.Id == row.Id).Key != row.Key)
                    await _client.UpdateAsync(ToEntity(row));
            }
            else
            {
                _seed[i] = row with { Id = await _client.CreateAsync(ToEntity(row with { Id = Guid.Empty })) };
            }
        }
    }

    private Entity ToEntity(Row row)
    {
        var entity = row.Id == Guid.Empty ? new Entity(_table) : new Entity(_table, row.Id);
        entity["ppds_name"] = row.Name;
        entity["ppds_num"] = row.Num;
        entity["ppds_amount"] = row.Amount;
        entity["ppds_when"] = row.When;
        entity["ppds_flag"] = row.Flag;
        entity["ppds_choice"] = row.Choice is { } c ? new OptionSetValue(c) : null;
        entity["ppds_note"] = row.Note;
        return entity;
    }

    private async Task<List<Row>> ReadAsync()
    {
        var q = new QueryExpression(_table) { ColumnSet = new ColumnSet("ppds_name", "ppds_num", "ppds_amount", "ppds_when", "ppds_flag", "ppds_choice", "ppds_note") };
        return (await _client.RetrieveMultipleAsync(q)).Entities.Select(e => new Row(
            e.Id,
            e.GetAttributeValue<string>("ppds_name"),
            e.Contains("ppds_num") ? e.GetAttributeValue<int?>("ppds_num") : null,
            e.Contains("ppds_amount") ? decimal.Round(e.GetAttributeValue<decimal>("ppds_amount"), 2) : null,
            e.Contains("ppds_when") ? e.GetAttributeValue<DateTime>("ppds_when").ToUniversalTime() : null,
            e.GetAttributeValue<bool>("ppds_flag"),
            e.GetAttributeValue<OptionSetValue>("ppds_choice")?.Value,
            e.GetAttributeValue<string>("ppds_note"))).ToList();
    }

    private async Task CleanupAsync(bool createdSolution)
    {
        var leftovers = new List<string>();
        try
        {
            _output.WriteLine($"Deleting table {_table}.");
            await _client.ExecuteAsync(new DeleteEntityRequest { LogicalName = _table });
        }
        catch (Exception ex) when (!ex.Message.Contains("Could not find", StringComparison.OrdinalIgnoreCase))
        {
            leftovers.Add($"table {_table} ({ex.Message})");
        }

        var tableGone = !((RetrieveAllEntitiesResponse)await _client.ExecuteAsync(new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true }))
            .EntityMetadata.Any(e => e.LogicalName == _table);
        _output.WriteLine($"CLEANUP table {_table} gone: {tableGone}");
        if (!tableGone && !leftovers.Any(l => l.StartsWith("table", StringComparison.Ordinal)))
            leftovers.Add($"table {_table}");

        if (createdSolution && tableGone)
        {
            var solution = await FindSolutionAsync();
            if (solution != null)
                await _client.DeleteAsync("solution", solution.Id);
            var solutionGone = await FindSolutionAsync() is null;
            _output.WriteLine($"CLEANUP solution {Solution} gone: {solutionGone}");
            if (!solutionGone)
                leftovers.Add($"solution {Solution}");
        }
        else if (createdSolution)
        {
            leftovers.Add($"solution {Solution} (kept because the table could not be deleted)");
        }

        Assert.True(leftovers.Count == 0, "Cleanup failed; leftovers: " + string.Join("; ", leftovers));
    }

    private sealed class Guard : IShakedownGuard
    {
        public void EnsureCanMutate(string operationDescription)
        {
            if (Environment.GetEnvironmentVariable("PPDS_SHAKEDOWN") == "1")
                throw new PpdsException(ErrorCodes.Safety.ShakedownActive, $"Shakedown active: '{operationDescription}' refused.");
        }
    }
}
