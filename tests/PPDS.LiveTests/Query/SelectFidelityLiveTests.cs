using System.Globalization;
using System.ServiceModel;
using System.Text;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.LiveTests.Infrastructure;
using PPDS.LiveTests.Query.SelectFidelity;
using PPDS.Query.Parsing;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace PPDS.LiveTests.Query;

/// <summary>
/// Live differential audit of SQL SELECT fidelity: every statement is run through the real <see cref="SqlQueryService"/>
/// (the path <c>ppds query sql</c> uses) against throwaway tables with a fully known data set, and compared with the
/// result T-SQL semantics require, computed independently in the test (<see cref="TSql"/>). Run against a DEVELOPMENT
/// environment only:
/// <code>
/// dotnet test tests/PPDS.LiveTests/PPDS.LiveTests.csproj -f net8.0 --filter "FullyQualifiedName~SelectFidelityLiveTests" --logger "console;verbosity=detailed"
/// </code>
/// Settings (process scope): DATAVERSE_URL, PPDS_TEST_PROFILE (an InteractiveBrowser profile already signed in; the run
/// never prompts) or the PPDS_TEST_APP_ID/CLIENT_SECRET/TENANT_ID trio, PPDS_TEST_SOLUTION (an existing unmanaged
/// solution), PPDS_TEST_PREFIX (its publisher prefix). Optional PPDS_SELECT_AUDIT_OUT: a file path that receives the
/// per-case markdown table.
/// <para>
/// The test creates three uniquely named tables (parent, child with a lookup to the parent, and a 5,050-row bulk table
/// that crosses one FetchXML page), seeds them, reads the stored values back through the SDK (the in-memory truth),
/// records the Dataverse behaviours the engine relies on as OBSERVED lines (raw FetchXML, not PPDS), runs every case,
/// and then deletes the rows and tables and confirms they are gone. Nothing pre-existing is written; the only standard
/// table read is <c>systemuser</c> (the caller's own row).
/// </para>
/// Each case records correct / WRONG RESULT / clean error / crash; failures never stop the run. The test fails at the
/// end when any case returned a silently wrong result or crashed, listing them.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class SelectFidelityLiveTests : LiveTestBase
{
    private const int English = 1033;

    private readonly ITestOutputHelper _output;
    private DataverseConnectionPool? _pool;
    private IConnectionSource? _source;
    private SqlQueryService _service = null!;

    // Names (set in CreateSchemaAsync).
    private string P = "", C = "", B = "", PId = "", CId = "";
    private int RedV, BlueV, GreenV;

    // In-memory truth (read back through the SDK after seeding).
    private List<PRow> PRows = [];
    private List<CRow> CRows = [];
    private int BulkCount;
    private Guid MyUserId;
    private string? MyFullName, MyDomainName;

    private readonly List<string> _createdEntities = [];
    private readonly List<Outcome> _outcomes = [];
    private readonly List<string> _observed = [];

    public SelectFidelityLiveTests(ITestOutputHelper output) => _output = output;

    private static string Prefix => Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")!.Trim().ToLowerInvariant();
    private static string Solution => Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!.Trim();

    [MetadataLiveFact]
    public async Task Select_ReturnsExactlyWhatTheStatementSays()
    {
        await ConnectAsync();
        var bodyFailed = false;
        try
        {
            await CreateSchemaAsync();
            await SeedAsync();
            await ReadBackAsync();
            await ObserveDataverseAsync();

            var progress = Environment.GetEnvironmentVariable("PPDS_SELECT_AUDIT_OUT") is { Length: > 0 } outPath ? outPath + ".progress" : null;
            if (progress is not null) File.WriteAllText(progress, $"tables {P} {C} {B}{Environment.NewLine}");
            foreach (var c in BuildCases())
            {
                if (progress is not null) File.AppendAllText(progress, $"running {c.Id}{Environment.NewLine}");
                var outcome = await RunCaseAsync(c);
                if (progress is not null) File.AppendAllText(progress, $"  {outcome.Verdict} {outcome.Detail ?? outcome.ActualText}{Environment.NewLine}");
                _outcomes.Add(outcome);
                _output.WriteLine($"[{outcome.Verdict}] {c.Id} {Flat(c.Sql)}");
                if (outcome.Verdict != Verdict.Correct)
                {
                    _output.WriteLine($"    expected: {outcome.ExpectedText}");
                    _output.WriteLine($"    actual:   {outcome.ActualText}");
                    if (outcome.Detail is not null) _output.WriteLine($"    detail:   {outcome.Detail}");
                    if (outcome.FetchXml is not null) _output.WriteLine($"    fetchxml: {Flat(outcome.FetchXml)}");
                }
                if (outcome.DmlFetchXml is not null) _output.WriteLine($"    DELETE dry-run fetchxml: {Flat(outcome.DmlFetchXml)}");
            }

            WriteReport();
        }
        catch
        {
            bodyFailed = true;
            throw;
        }
        finally
        {
            var leftovers = await CleanupAsync();
            if (leftovers.Count > 0)
            {
                var message = "Cleanup failed; left in the environment (delete manually): " + string.Join("; ", leftovers);
                _output.WriteLine(message);
                if (!bodyFailed) throw new XunitException(message);
            }
        }

        var bad = _outcomes.Where(o => o.Verdict is Verdict.WrongResult or Verdict.Crash or Verdict.HarnessError).ToList();
        if (bad.Count > 0)
            throw new XunitException($"{bad.Count} of {_outcomes.Count} SELECT cases returned a wrong result, crashed or could not be evaluated: " +
                                     string.Join(", ", bad.Select(o => $"{o.Case.Id} ({o.Verdict})")));
    }

    // ═════════════════════════════ connection ═════════════════════════════

    private async Task ConnectAsync()
    {
        if (Prefix != "ppds")
            throw new XunitException("The SELECT audit's case catalogue names columns ppds_*; run it with PPDS_TEST_PREFIX=ppds.");
        _source = Configuration.HasClientSecretCredentials
            ? await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "SelectFidelityLiveTests", maxPoolSize: 4)
            : await LiveTestProfileConnectionSource.CreateAsync(Configuration, "SelectFidelityLiveTests", maxPoolSize: 4);
        var connected = _source.GetSeedClient().ConnectedOrgUriActual?.GetLeftPart(UriPartial.Authority);
        _output.WriteLine($"Connected organisation: {connected} ({_source.GetSeedClient().ConnectedOrgFriendlyName}); solution '{Solution}', prefix '{Prefix}'.");
        _pool = LiveTestHelpers.CreateConnectionPool(new[] { _source });
        var executor = new QueryExecutor(_pool);
        _service = new SqlQueryService(executor, new InactiveGuard(), poolCapacity: _pool.GetTotalRecommendedParallelism());
    }

    private async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request)
    {
        await using var client = await _pool!.GetClientAsync();
        return await client.ExecuteAsync(request);
    }

    private async Task<EntityCollection> RetrieveAllAsync(QueryExpression query)
    {
        var all = new EntityCollection();
        query.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 };
        while (true)
        {
            await using var client = await _pool!.GetClientAsync();
            var page = await client.RetrieveMultipleAsync(query);
            all.Entities.AddRange(page.Entities);
            if (!page.MoreRecords) return all;
            query.PageInfo.PageNumber++;
            query.PageInfo.PagingCookie = page.PagingCookie;
        }
    }

    // ═════════════════════════════ schema ═════════════════════════════

    private async Task CreateSchemaAsync()
    {
        var sfx = Guid.NewGuid().ToString("N")[..6];
        P = $"{Prefix}_sqlp{sfx}";
        C = $"{Prefix}_sqlc{sfx}";
        B = $"{Prefix}_sqlb{sfx}";
        PId = P + "id";
        CId = C + "id";

        await CreateTableAsync(P, "PPDS SELECT audit parent");
        var a = Prefix;
        await AddAttributeAsync(P, new IntegerAttributeMetadata { SchemaName = $"{a}_key", MinValue = int.MinValue, MaxValue = int.MaxValue });
        await AddAttributeAsync(P, new MemoAttributeMetadata { SchemaName = $"{a}_memo", MaxLength = 2000, Format = StringFormat.TextArea });
        await AddAttributeAsync(P, new IntegerAttributeMetadata { SchemaName = $"{a}_num", MinValue = int.MinValue, MaxValue = int.MaxValue });
        await AddAttributeAsync(P, new IntegerAttributeMetadata { SchemaName = $"{a}_num2", MinValue = int.MinValue, MaxValue = int.MaxValue });
        await AddAttributeAsync(P, new DecimalAttributeMetadata { SchemaName = $"{a}_dec", Precision = 2, MinValue = -1000000000m, MaxValue = 1000000000m });
        await AddAttributeAsync(P, new MoneyAttributeMetadata { SchemaName = $"{a}_money", Precision = 2, PrecisionSource = 0, MinValue = -1000000000d, MaxValue = 1000000000d });
        await AddAttributeAsync(P, new DoubleAttributeMetadata { SchemaName = $"{a}_flt", Precision = 2, MinValue = -1000000000d, MaxValue = 1000000000d });
        await AddAttributeAsync(P, new BooleanAttributeMetadata
        {
            SchemaName = $"{a}_flag", DefaultValue = false,
            OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(new Label("Yes", English), 1), new OptionMetadata(new Label("No", English), 0))
        });
        await AddAttributeAsync(P, new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { SchemaName = $"{a}_dt", DateTimeBehavior = DateTimeBehavior.UserLocal });
        await AddAttributeAsync(P, new DateTimeAttributeMetadata(DateTimeFormat.DateOnly) { SchemaName = $"{a}_donly", DateTimeBehavior = DateTimeBehavior.DateOnly });
        var colors = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
        colors.Options.Add(new OptionMetadata(new Label("Red", English), null));
        colors.Options.Add(new OptionMetadata(new Label("Blue", English), null));
        colors.Options.Add(new OptionMetadata(new Label("Green", English), null));
        await AddAttributeAsync(P, new PicklistAttributeMetadata { SchemaName = $"{a}_colorcode", OptionSet = colors });
        var multi = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
        multi.Options.Add(new OptionMetadata(new Label("Red", English), null));
        multi.Options.Add(new OptionMetadata(new Label("Blue", English), null));
        multi.Options.Add(new OptionMetadata(new Label("Green", English), null));
        await AddAttributeAsync(P, new MultiSelectPicklistAttributeMetadata { SchemaName = $"{a}_multi", OptionSet = multi });
        await AddAttributeAsync(P, new StringAttributeMetadata { SchemaName = $"{a}_itemtype", MaxLength = 50, FormatName = StringFormatName.Text });
        await AddAttributeAsync(P, new StringAttributeMetadata { SchemaName = $"{a}_itemtypename", MaxLength = 50, FormatName = StringFormatName.Text });

        await CreateTableAsync(C, "PPDS SELECT audit child");
        await AddAttributeAsync(C, new IntegerAttributeMetadata { SchemaName = $"{a}_key", MinValue = int.MinValue, MaxValue = int.MaxValue });
        await AddAttributeAsync(C, new IntegerAttributeMetadata { SchemaName = $"{a}_qty", MinValue = int.MinValue, MaxValue = int.MaxValue });
        await AddAttributeAsync(C, new IntegerAttributeMetadata { SchemaName = $"{a}_num", MinValue = int.MinValue, MaxValue = int.MaxValue });
        _output.WriteLine($"Creating lookup {a}_parentid on {C} -> {P}.");
        await ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = $"{P}_{C}"[..Math.Min(90, $"{P}_{C}".Length)],
                ReferencedEntity = P,
                ReferencingEntity = C
            },
            Lookup = new LookupAttributeMetadata
            {
                SchemaName = $"{a}_parentid", DisplayName = new Label("Parent", English),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
            },
            SolutionUniqueName = Solution
        });

        await CreateTableAsync(B, "PPDS SELECT audit bulk");
        await AddAttributeAsync(B, new IntegerAttributeMetadata { SchemaName = $"{a}_n", MinValue = int.MinValue, MaxValue = int.MaxValue });

        await ExecuteAsync(new PublishXmlRequest
        {
            ParameterXml = $"<importexportxml><entities><entity>{P}</entity><entity>{C}</entity><entity>{B}</entity></entities></importexportxml>"
        });

        var color = (PicklistAttributeMetadata)((RetrieveAttributeResponse)await ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = P, LogicalName = $"{a}_colorcode", RetrieveAsIfPublished = true })).AttributeMetadata;
        int ValueOf(string label) => color.OptionSet.Options.Single(o => o.Label.LocalizedLabels.Single().Label == label).Value!.Value;
        RedV = ValueOf("Red");
        BlueV = ValueOf("Blue");
        GreenV = ValueOf("Green");
        _output.WriteLine($"Choice values: Red={RedV} Blue={BlueV} Green={GreenV} (label order Blue<Green<Red differs from value order).");
    }

    private async Task CreateTableAsync(string schemaName, string display)
    {
        _output.WriteLine($"Creating table '{schemaName}' in solution '{Solution}'.");
        _createdEntities.Add(schemaName);
        // Another run may be creating a table in the same organisation; Dataverse serialises table creation.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await CreateEntityAsync(schemaName, display);
                return;
            }
            catch (FaultException<OrganizationServiceFault> f) when (attempt < 20 && f.Message.Contains("table creation operation is already in progress", StringComparison.OrdinalIgnoreCase))
            {
                _output.WriteLine($"Table creation busy (attempt {attempt}); retrying in 15 s.");
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
        }
    }

    private async Task CreateEntityAsync(string schemaName, string display)
    {
        await ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                SchemaName = schemaName, DisplayName = new Label(display, English),
                DisplayCollectionName = new Label(display + "s", English),
                Description = new Label("Temporary PPDS SELECT fidelity live-test table. Safe to delete.", English),
                OwnershipType = OwnershipTypes.UserOwned, IsActivity = false
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                SchemaName = $"{Prefix}_name", MaxLength = 100, FormatName = StringFormatName.Text,
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                DisplayName = new Label("Name", English)
            },
            SolutionUniqueName = Solution
        });
    }

    private Task AddAttributeAsync(string entity, AttributeMetadata attribute)
    {
        attribute.DisplayName ??= new Label(attribute.SchemaName, English);
        attribute.RequiredLevel ??= new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
        return ExecuteAsync(new CreateAttributeRequest { EntityName = entity, Attribute = attribute, SolutionUniqueName = Solution });
    }

    // ═════════════════════════════ data ═════════════════════════════

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

    private async Task SeedAsync()
    {
        var a = Prefix;
        int[] colorValues = [RedV, BlueV, GreenV];
        var parents = new List<Entity>();
        var parentIds = new Dictionary<int, Guid>();
        foreach (var s in ParentSeeds())
        {
            var id = Guid.NewGuid();
            parentIds[s.Key] = id;
            var e = new Entity(P, id) { [$"{a}_key"] = s.Key };
            if (s.Name is not null) e[$"{a}_name"] = s.Name;
            if (s.Memo is not null) e[$"{a}_memo"] = s.Memo;
            if (s.Num is not null) e[$"{a}_num"] = s.Num;
            if (s.Num2 is not null) e[$"{a}_num2"] = s.Num2;
            if (s.Dec is not null) e[$"{a}_dec"] = s.Dec;
            if (s.Money is not null) e[$"{a}_money"] = new Money(s.Money.Value);
            if (s.Flt is not null) e[$"{a}_flt"] = s.Flt;
            if (s.Flag is not null) e[$"{a}_flag"] = s.Flag;
            if (s.Dt is not null) e[$"{a}_dt"] = s.Dt;
            if (s.DOnly is not null) e[$"{a}_donly"] = s.DOnly;
            if (s.Color is not null) e[$"{a}_colorcode"] = new OptionSetValue(colorValues[s.Color.Value]);
            if (s.Multi is not null) e[$"{a}_multi"] = new OptionSetValueCollection(s.Multi.Select(i => new OptionSetValue(colorValues[i])).ToList());
            if (s.ItemType is not null) e[$"{a}_itemtype"] = s.ItemType;
            if (s.ItemTypeName is not null) e[$"{a}_itemtypename"] = s.ItemTypeName;
            parents.Add(e);
        }
        await CreateManyAsync(P, parents);

        string?[] childNames = ["Alpha", "beta", "Café", "x", null, "dup", "Zeta", "alpha"];
        var children = new List<Entity>();
        for (var k = 1; k <= 24; k++)
        {
            var e = new Entity(C, Guid.NewGuid()) { [$"{a}_key"] = k };
            var name = childNames[k % 8];
            if (name is not null) e[$"{a}_name"] = name;
            if (k % 6 != 0) e[$"{a}_parentid"] = new EntityReference(P, parentIds[(k % 9) + 1]);
            if (k % 4 != 0) e[$"{a}_qty"] = k * 3 - 20;
            if (k % 5 != 0) e[$"{a}_num"] = k % 7;
            children.Add(e);
        }
        await CreateManyAsync(C, children);

        var bulk = Enumerable.Range(1, 5050).Select(k => new Entity(B, Guid.NewGuid()) { [$"{a}_name"] = $"b{k:D5}", [$"{a}_n"] = k }).ToList();
        await CreateManyAsync(B, bulk);
    }

    private async Task CreateManyAsync(string entity, List<Entity> rows)
    {
        _output.WriteLine($"Creating {rows.Count} rows in {entity}.");
        foreach (var batch in rows.Chunk(1000))
            await ExecuteAsync(new CreateMultipleRequest { Targets = new EntityCollection(batch.ToList()) { EntityName = entity } });
    }

    internal sealed class PRow
    {
        public Guid Id;
        public int Key;
        public string? Name, Memo, ItemType, ItemTypeName, ColorLabel, OwnerName;
        public int? Num, Num2, Color;
        public decimal? Dec, Money;
        public double? Flt;
        public bool? Flag;
        public DateTime? Dt, DOnly;
        public int[]? Multi;
        public Guid OwnerId;
        public int StateCode, StatusCode;
    }

    internal sealed class CRow
    {
        public Guid Id;
        public int Key;
        public string? Name;
        public int? Qty, Num;
        public Guid? ParentId;
        public Guid OwningUser;
    }

    private async Task ReadBackAsync()
    {
        var a = Prefix;
        string[] pCols = [$"{a}_key", $"{a}_name", $"{a}_memo", $"{a}_num", $"{a}_num2", $"{a}_dec", $"{a}_money", $"{a}_flt", $"{a}_flag",
            $"{a}_dt", $"{a}_donly", $"{a}_colorcode", $"{a}_multi", $"{a}_itemtype", $"{a}_itemtypename", "ownerid", "statecode", "statuscode"];
        var parents = await RetrieveAllAsync(new QueryExpression(P) { ColumnSet = new ColumnSet(pCols) });
        PRows = parents.Entities.Select(e => new PRow
        {
            Id = e.Id,
            Key = e.GetAttributeValue<int>($"{a}_key"),
            Name = e.GetAttributeValue<string>($"{a}_name"),
            Memo = e.GetAttributeValue<string>($"{a}_memo"),
            Num = e.GetAttributeValue<int?>($"{a}_num"),
            Num2 = e.GetAttributeValue<int?>($"{a}_num2"),
            Dec = e.GetAttributeValue<decimal?>($"{a}_dec"),
            Money = e.GetAttributeValue<Money>($"{a}_money")?.Value,
            Flt = e.GetAttributeValue<double?>($"{a}_flt"),
            Flag = e.Contains($"{a}_flag") ? e.GetAttributeValue<bool>($"{a}_flag") : null,
            Dt = e.GetAttributeValue<DateTime?>($"{a}_dt"),
            DOnly = e.GetAttributeValue<DateTime?>($"{a}_donly"),
            Color = e.GetAttributeValue<OptionSetValue>($"{a}_colorcode")?.Value,
            ColorLabel = e.FormattedValues.TryGetValue($"{a}_colorcode", out var cl) ? cl : null,
            Multi = e.GetAttributeValue<OptionSetValueCollection>($"{a}_multi")?.Select(o => o.Value).ToArray(),
            ItemType = e.GetAttributeValue<string>($"{a}_itemtype"),
            ItemTypeName = e.GetAttributeValue<string>($"{a}_itemtypename"),
            OwnerId = e.GetAttributeValue<EntityReference>("ownerid").Id,
            OwnerName = e.GetAttributeValue<EntityReference>("ownerid").Name ?? (e.FormattedValues.TryGetValue("ownerid", out var on) ? on : null),
            StateCode = e.GetAttributeValue<OptionSetValue>("statecode").Value,
            StatusCode = e.GetAttributeValue<OptionSetValue>("statuscode").Value
        }).OrderBy(r => r.Key).ToList();

        var children = await RetrieveAllAsync(new QueryExpression(C) { ColumnSet = new ColumnSet($"{a}_key", $"{a}_name", $"{a}_qty", $"{a}_num", $"{a}_parentid", "owninguser") });
        CRows = children.Entities.Select(e => new CRow
        {
            Id = e.Id,
            Key = e.GetAttributeValue<int>($"{a}_key"),
            Name = e.GetAttributeValue<string>($"{a}_name"),
            Qty = e.GetAttributeValue<int?>($"{a}_qty"),
            Num = e.GetAttributeValue<int?>($"{a}_num"),
            ParentId = e.GetAttributeValue<EntityReference>($"{a}_parentid")?.Id,
            OwningUser = e.GetAttributeValue<EntityReference>("owninguser").Id
        }).OrderBy(r => r.Key).ToList();

        BulkCount = (await RetrieveAllAsync(new QueryExpression(B) { ColumnSet = new ColumnSet($"{a}_n") })).Entities.Count;

        MyUserId = ((WhoAmIResponse)await ExecuteAsync(new WhoAmIRequest())).UserId;
        await using (var client = await _pool!.GetClientAsync())
        {
            var me = await client.RetrieveAsync("systemuser", MyUserId, new ColumnSet("fullname", "domainname"));
            MyFullName = me.GetAttributeValue<string>("fullname");
            MyDomainName = me.GetAttributeValue<string>("domainname");
        }

        _output.WriteLine($"Read back {PRows.Count} parent, {CRows.Count} child, {BulkCount} bulk rows.");
        Observe($"stored string values (SDK read-back): {string.Join(", ", PRows.Where(r => r.Key is 4 or 7 or 8 or 30).Select(r => $"key {r.Key} name={Show(r.Name)} memo={Show(r.Memo)}"))}");
        Observe($"two-option column with no value written reads back as: {Show(PRows.Single(r => r.Key == 7).Flag)} (keys with null flag: {string.Join(",", PRows.Where(r => r.Flag is null).Select(r => r.Key))})");
        Observe($"money -0.01 reads back as {Show(PRows.Single(r => r.Key == 8).Money)}, decimal 99.99 as {Show(PRows.Single(r => r.Key == 5).Dec)}, float 1000000 as {Show(PRows.Single(r => r.Key == 5).Flt)}");
    }

    // ═════════════════════════════ OBSERVED (raw FetchXML, not PPDS) ═════════════════════════════

    private void Observe(string line)
    {
        _observed.Add(line);
        _output.WriteLine("OBSERVED " + line);
    }

    private static string Show(object? v) => ResultComparer.Canon(v, false);

    private async Task<List<PRow>> RawFilterAsync(string conditionXml, string? orderXml = null)
    {
        var fetch = $"<fetch><entity name=\"{P}\"><attribute name=\"{Prefix}_key\" />{(conditionXml.Length > 0 ? $"<filter>{conditionXml}</filter>" : "")}{orderXml}</entity></fetch>";
        await using var client = await _pool!.GetClientAsync();
        var result = await client.RetrieveMultipleAsync(new FetchExpression(fetch));
        var byKey = PRows.ToDictionary(r => r.Key);
        return result.Entities.Select(e => byKey[e.GetAttributeValue<int>($"{Prefix}_key")]).ToList();
    }

    private async Task ObserveDataverseAsync()
    {
        var a = Prefix;
        async Task Keys(string what, string condition, Func<PRow, bool?>? tsql = null)
        {
            try
            {
                var rows = await RawFilterAsync(condition);
                var keys = rows.Select(r => r.Key).OrderBy(k => k).ToList();
                var note = "";
                if (tsql is not null)
                {
                    var expected = PRows.Where(r => tsql(r) == true).Select(r => r.Key).OrderBy(k => k).ToList();
                    var extra = keys.Except(expected).ToList();
                    var missing = expected.Except(keys).ToList();
                    note = extra.Count == 0 && missing.Count == 0
                        ? " (same as T-SQL)"
                        : $" (T-SQL differs: extra {string.Join(",", extra)}{(missing.Count > 0 ? $"; missing {string.Join(",", missing)}" : "")})";
                }
                Observe($"FetchXML {what}: {keys.Count} rows, keys {string.Join(",", keys)}{note}");
            }
            catch (Exception ex)
            {
                Observe($"FetchXML {what}: fault {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
        }

        // NULL handling of negative operators.
        await Keys("ne num 5", $"<condition attribute=\"{a}_num\" operator=\"ne\" value=\"5\" />", r => TSql.Ne(r.Num, 5));
        await Keys("not-in num (5,10)", $"<condition attribute=\"{a}_num\" operator=\"not-in\"><value>5</value><value>10</value></condition>", r => TSql.Not(TSql.In(r.Num, 5, 10)));
        await Keys("not-like name 'A%'", $"<condition attribute=\"{a}_name\" operator=\"not-like\" value=\"A%\" />", r => TSql.Not(TSql.Like(r.Name, "A%")));
        await Keys("not-between num 0..10", $"<condition attribute=\"{a}_num\" operator=\"not-between\"><value>0</value><value>10</value></condition>", r => TSql.Not(TSql.Between(r.Num, 0, 10)));
        await Keys("not-begin-with name 'A'", $"<condition attribute=\"{a}_name\" operator=\"not-begin-with\" value=\"A\" />", r => TSql.Not(TSql.Like(r.Name, "A%")));
        await Keys("not-end-with name 'a'", $"<condition attribute=\"{a}_name\" operator=\"not-end-with\" value=\"a\" />", r => TSql.Not(TSql.Like(r.Name, "%a")));
        await Keys("ne dt 2024-01-15T00:30:00Z", $"<condition attribute=\"{a}_dt\" operator=\"ne\" value=\"2024-01-15T00:30:00Z\" />", r => TSql.Ne(r.Dt, U(2024, 1, 15, 0, 30)));
        await Keys("eq name \"\" (what WHERE name = NULL is sent as)", $"<condition attribute=\"{a}_name\" operator=\"eq\" value=\"\" />", _ => null);

        // LIKE / begins-with handling of [], _ and %.
        await Keys("like name '[ab]%'", $"<condition attribute=\"{a}_name\" operator=\"like\" value=\"[ab]%\" />", r => TSql.Like(r.Name, "[ab]%"));
        await Keys("begins-with name '[ab]' (what LIKE '[ab]%' is sent as)", $"<condition attribute=\"{a}_name\" operator=\"begins-with\" value=\"[ab]\" />", r => TSql.Like(r.Name, "[ab]%"));
        await Keys("begins-with name 'a_' ", $"<condition attribute=\"{a}_name\" operator=\"begins-with\" value=\"a_\" />", r => TSql.Like(r.Name, "a[_]%"));
        await Keys("begins-with name '50%' ", $"<condition attribute=\"{a}_name\" operator=\"begins-with\" value=\"50%\" />", r => TSql.Like(r.Name, "50[%]%"));
        await Keys("like name 'a[_]c'", $"<condition attribute=\"{a}_name\" operator=\"like\" value=\"a[_]c\" />", r => TSql.Like(r.Name, "a[_]c"));

        // Case, accent, trailing/leading space sensitivity.
        await Keys("eq name 'alpha'", $"<condition attribute=\"{a}_name\" operator=\"eq\" value=\"alpha\" />");
        var cafe = await RawFilterAsync($"<condition attribute=\"{a}_name\" operator=\"eq\" value=\"cafe\" />");
        Observe($"FetchXML eq name 'cafe': keys {string.Join(",", cafe.Select(r => r.Key).OrderBy(k => k))} ({(cafe.Any(r => r.Name == "Café") ? "accent-insensitive" : "accent-sensitive")})");
        TSql.AccentInsensitive = cafe.Any(r => r.Name == "Café");
        await Keys("eq name 'trail' (stored 'trail  ')", $"<condition attribute=\"{a}_name\" operator=\"eq\" value=\"trail\" />");
        await Keys("eq name 'lead' (stored '  lead')", $"<condition attribute=\"{a}_name\" operator=\"eq\" value=\"lead\" />");

        // Ordering.
        async Task Order(string what, string order, Func<PRow, object?> show)
        {
            try
            {
                var rows = await RawFilterAsync("", order);
                Observe($"FetchXML order {what}: {string.Join(" ", rows.Select(r => Show(show(r))))}");
            }
            catch (Exception ex) { Observe($"FetchXML order {what}: fault {FirstLine(ex.Message)}"); }
        }
        await Order("by name asc", $"<order attribute=\"{a}_name\" /><order attribute=\"{a}_key\" />", r => r.Name);
        await Order("by num asc (NULL position)", $"<order attribute=\"{a}_num\" /><order attribute=\"{a}_key\" />", r => r.Num);
        await Order("by num desc (NULL position)", $"<order attribute=\"{a}_num\" descending=\"true\" /><order attribute=\"{a}_key\" />", r => r.Num);
        await Order("by choice asc (label or value?)", $"<order attribute=\"{a}_colorcode\" /><order attribute=\"{a}_key\" />", r => r.ColorLabel is null ? null : $"{r.ColorLabel}={r.Color}");

        // Date/datetime literals without an offset.
        var dtRow = PRows.Single(r => r.Key == 1);
        await using (var client = await _pool!.GetClientAsync())
        {
            var one = await client.RetrieveAsync(P, dtRow.Id, new ColumnSet($"{a}_dt"));
            Observe($"formatted value of ppds_dt stored as {Show(dtRow.Dt)} (UTC): '{(one.FormattedValues.TryGetValue($"{a}_dt", out var f) ? f : "(none)")}' (the caller's time-zone setting decides this)");
        }
        await Keys("eq dt '2024-01-15T00:30:00' (no offset)", $"<condition attribute=\"{a}_dt\" operator=\"eq\" value=\"2024-01-15T00:30:00\" />", r => TSql.Eq(r.Dt, U(2024, 1, 15, 0, 30)));
        await Keys("eq dt '2024-01-15T00:30:00Z'", $"<condition attribute=\"{a}_dt\" operator=\"eq\" value=\"2024-01-15T00:30:00Z\" />", r => TSql.Eq(r.Dt, U(2024, 1, 15, 0, 30)));
        await Keys("ge dt '2024-01-15' and lt dt '2024-01-16' (no offset)",
            $"<condition attribute=\"{a}_dt\" operator=\"ge\" value=\"2024-01-15\" /><condition attribute=\"{a}_dt\" operator=\"lt\" value=\"2024-01-16\" />",
            r => TSql.And(TSql.Ge(r.Dt, U(2024, 1, 15)), TSql.Lt(r.Dt, U(2024, 1, 16))));
        await Keys("eq donly '2024-01-15'", $"<condition attribute=\"{a}_donly\" operator=\"eq\" value=\"2024-01-15\" />", r => TSql.Eq(r.DOnly, D(2024, 1, 15)));
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();
    private static string Flat(string s) => s.Replace("\r", "").Replace("\n", " ").Replace("  ", " ");

    // ═════════════════════════════ running a case ═════════════════════════════

    internal sealed record Case
    {
        public required string Id { get; init; }
        public required string Area { get; init; }
        public required string Sql { get; init; }
        public Func<Expected>? Expect { get; init; }
        public int? Top { get; init; }

        /// <summary>A custom runner (paging) that produces its own actual table.</summary>
        public Func<Task<ActualTable>>? Run { get; init; }

        /// <summary>Also plan the same WHERE as a DELETE dry-run and record its FetchXML (side-effect free).</summary>
        public string? DmlWhere { get; init; }

        /// <summary>
        /// Planning this statement recurses without bound and the stack overflow ends the whole process (it cannot be
        /// caught), so it is not run in-process; the reason names the separate-process reproduction.
        /// </summary>
        public string? CrashesProcess { get; init; }
    }

    internal sealed class Outcome
    {
        public required Case Case { get; init; }
        public Verdict Verdict { get; set; }
        public string ExpectedText { get; set; } = "";
        public string ActualText { get; set; } = "";
        public string? Detail { get; set; }
        public string? FetchXml { get; set; }
        public string? DmlFetchXml { get; set; }
        public List<string> AutoExpanded { get; set; } = [];
    }

    private async Task<Outcome> RunCaseAsync(Case c)
    {
        var outcome = new Outcome { Case = c };
        Expected expected;
        try
        {
            expected = c.Expect!();
        }
        catch (Exception ex)
        {
            outcome.Verdict = Verdict.HarnessError;
            outcome.Detail = "reference model failed: " + ex.Message;
            return outcome;
        }
        outcome.ExpectedText = expected.Error is not null ? "error: " + expected.Error
            : expected.MultipleResultSets is not null ? expected.MultipleResultSets
            : $"[{string.Join(", ", expected.Columns)}] " + ResultComparer.Describe(expected.Rows);

        if (c.CrashesProcess is not null)
        {
            outcome.Verdict = Verdict.Crash;
            outcome.ActualText = "not run in-process: " + c.CrashesProcess;
            return outcome;
        }

        ActualTable? actual = null;
        Exception? error = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            if (c.Run is not null)
            {
                actual = await c.Run();
            }
            else
            {
                var result = await _service.ExecuteAsync(new SqlQueryRequest { Sql = c.Sql, TopOverride = c.Top, DmlSafety = new DmlSafetyOptions() }, cts.Token);
                outcome.FetchXml = result.TranspiledFetchXml;
                actual = ActualTable.From(result.Result);
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (c.DmlWhere is not null)
            outcome.DmlFetchXml = await DeleteDryRunFetchXmlAsync(c.DmlWhere);

        if (error is not null)
        {
            outcome.ActualText = $"{error.GetType().Name}: {FirstLine(Innermost(error))}";
            if (expected.Error is not null)
            {
                outcome.Verdict = IsCrash(error) ? Verdict.Crash : Verdict.Correct;
                outcome.Detail = "T-SQL also raises an error";
            }
            else
            {
                outcome.Verdict = IsCrash(error) ? Verdict.Crash : Verdict.CleanError;
            }
            return outcome;
        }

        outcome.ActualText = $"[{string.Join(", ", actual!.Columns)}] " + ResultComparer.Describe(actual.Rows);
        if (expected.Error is not null)
        {
            outcome.Verdict = Verdict.WrongResult;
            outcome.Detail = "T-SQL raises an error; the engine returned rows";
            return outcome;
        }
        if (expected.MultipleResultSets is not null)
        {
            outcome.Verdict = Verdict.WrongResult;
            outcome.Detail = "T-SQL returns " + expected.MultipleResultSets + "; the engine returned one table (the last statement's rows) without saying so";
            return outcome;
        }
        var diff = ResultComparer.Compare(expected, actual, [PId, CId, B + "id"]);
        outcome.AutoExpanded = actual.AutoExpanded;
        if (actual.ExtraKeys.Count > 0)
            outcome.Detail = $"unrequested primary-key column ignored: {string.Join(", ", actual.ExtraKeys)}";
        outcome.Verdict = diff is null ? Verdict.Correct : Verdict.WrongResult;
        if (diff is not null) outcome.Detail = outcome.Detail is null ? diff : diff + "; " + outcome.Detail;
        if (actual.AutoExpanded.Count > 0)
            outcome.Detail = (outcome.Detail is null ? "" : outcome.Detail + "; ") + $"auto-added *name columns ignored: {string.Join(", ", actual.AutoExpanded)}";
        return outcome;
    }

    private async Task<string?> DeleteDryRunFetchXmlAsync(string where)
    {
        try
        {
            var result = await _service.ExecuteAsync(new SqlQueryRequest
            {
                Sql = $"DELETE FROM {P} WHERE {where}",
                DmlSafety = new DmlSafetyOptions { IsDryRun = true }
            });
            return (result.TranspiledFetchXml ?? "(none)") + (result.DryRunPlan is { } plan ? " | plan: " + Flat(PlanFormatter.Format(plan)) : "");
        }
        catch (Exception ex)
        {
            return $"dry-run failed: {ex.GetType().Name}: {FirstLine(ex.Message)}";
        }
    }

    private static string Innermost(Exception ex)
    {
        // Show the outermost PPDS message, plus the innermost cause when it differs.
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return ReferenceEquals(inner, ex) || inner.Message == ex.Message ? ex.Message : $"{ex.Message} <- {inner.GetType().Name}: {inner.Message}";
    }

    /// <summary>
    /// A crash is an unhandled programming error surfacing to the user (null reference, invalid cast, key not found,
    /// index out of range, format/overflow) rather than a deliberate error with a message.
    /// </summary>
    private static bool IsCrash(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is NullReferenceException or InvalidCastException or KeyNotFoundException or IndexOutOfRangeException
                or ArgumentOutOfRangeException or FormatException or OverflowException or System.Xml.XmlException)
                return true;
        }
        return false;
    }

    // ═════════════════════════════ report ═════════════════════════════

    private void WriteReport()
    {
        var counts = _outcomes.GroupBy(o => o.Verdict).ToDictionary(g => g.Key, g => g.Count());
        var summary = $"cases {_outcomes.Count}: correct {counts.GetValueOrDefault(Verdict.Correct)}, WRONG RESULT {counts.GetValueOrDefault(Verdict.WrongResult)}, " +
                      $"clean error {counts.GetValueOrDefault(Verdict.CleanError)}, crash {counts.GetValueOrDefault(Verdict.Crash)}, harness error {counts.GetValueOrDefault(Verdict.HarnessError)}";
        _output.WriteLine("SUMMARY " + summary);

        var path = Environment.GetEnvironmentVariable("PPDS_SELECT_AUDIT_OUT");
        if (string.IsNullOrWhiteSpace(path)) return;

        static string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
        static string Name(Verdict v) => v switch
        {
            Verdict.Correct => "correct",
            Verdict.WrongResult => "**WRONG RESULT**",
            Verdict.CleanError => "clean error",
            Verdict.Crash => "**crash**",
            _ => "harness error"
        };
        var sb = new StringBuilder();
        sb.AppendLine($"Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC; {summary}.");
        sb.AppendLine($"Tables: parent `{P}`, child `{C}`, bulk `{B}` ({PRows.Count} / {CRows.Count} / {BulkCount} rows).");
        sb.AppendLine();
        sb.AppendLine("| # | Area | SQL | Expected (T-SQL) | Actual (PPDS) | Verdict | Detail |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var o in _outcomes)
        {
            var sql = o.Case.Sql.Replace(P, "P").Replace(C, "C").Replace(B, "B");
            var detail = o.Detail;
            if (o.DmlFetchXml is not null) detail = (detail is null ? "" : detail + "; ") + "DELETE dry-run: " + o.DmlFetchXml.Replace(P, "P");
            sb.AppendLine($"| {o.Case.Id} | {o.Case.Area} | `{Cell(Flat(sql))}`{(o.Case.Top is { } t ? $" (top {t})" : "")} | {Cell(Shorten(o.ExpectedText.Replace(P, "P")))} | {Cell(Shorten(o.ActualText.Replace(P, "P").Replace(C, "C")))} | {Name(o.Verdict)} | {Cell(Shorten(detail?.Replace(P, "P").Replace(C, "C"), 400))} |");
        }
        sb.AppendLine();
        sb.AppendLine("OBSERVED:");
        foreach (var line in _observed) sb.AppendLine("- " + line.Replace(P, "P").Replace(C, "C"));
        sb.AppendLine();
        sb.AppendLine("FetchXML of non-correct cases:");
        foreach (var o in _outcomes.Where(o => o.Verdict != Verdict.Correct && o.FetchXml is not null))
            sb.AppendLine($"- {o.Case.Id}: `{Cell(Flat(o.FetchXml!).Replace(P, "P").Replace(C, "C"))}`");
        File.WriteAllText(path!, sb.ToString());
        _output.WriteLine($"Report written to {path}.");
    }

    private static string Shorten(string? s, int max = 220) => s is null ? "" : s.Length <= max ? s : s[..max] + "…";

    // ═════════════════════════════ cleanup ═════════════════════════════

    private async Task<List<string>> CleanupAsync()
    {
        var leftovers = new List<string>();
        if (_pool is null) return leftovers;

        // Rows first (children before parents); the 5,050 bulk rows go with their table.
        foreach (var (entity, ids) in new[] { (C, CRows.Select(r => r.Id).ToList()), (P, PRows.Select(r => r.Id).ToList()) })
        {
            if (entity.Length == 0) continue;
            var failed = 0;
            foreach (var id in ids)
            {
                try { await ExecuteAsync(new DeleteRequest { Target = new EntityReference(entity, id) }); }
                catch { failed++; }
            }
            _output.WriteLine($"Deleted {ids.Count - failed} of {ids.Count} rows from {entity}.");
        }

        foreach (var entity in Enumerable.Reverse(_createdEntities).OrderBy(e => e == P ? 1 : 0).ToList())
        {
            try
            {
                await ExecuteAsync(new DeleteEntityRequest { LogicalName = entity.ToLowerInvariant() });
                _output.WriteLine($"Deleted table {entity}.");
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                _output.WriteLine($"Table {entity} was not there (never created).");
            }
            catch (Exception ex)
            {
                leftovers.Add($"table {entity} ({FirstLine(ex.Message)})");
                continue;
            }
            try
            {
                await ExecuteAsync(new RetrieveEntityRequest { LogicalName = entity.ToLowerInvariant(), EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true });
                leftovers.Add($"table {entity} (still retrievable after delete)");
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                _output.WriteLine($"Confirmed table {entity} is gone.");
            }
        }
        return leftovers;
    }

    private static bool IsNotFound(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is FaultException<OrganizationServiceFault> f && (f.Detail.ErrorCode == unchecked((int)0x80040217) || f.Message.Contains("Could not find", StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }

    public override async Task DisposeAsync()
    {
        if (_pool is not null) await _pool.DisposeAsync();
        _source?.Dispose();
        Configuration.Dispose();
        await base.DisposeAsync();
    }

    private sealed class InactiveGuard : IShakedownGuard
    {
        public void EnsureCanMutate(string operationDescription)
        {
            // The audit only reads (DML is planned as a dry-run, never executed).
            throw new PpdsException(ErrorCodes.Safety.ShakedownActive, $"The SELECT audit never mutates through the SQL engine: '{operationDescription}' refused.");
        }
    }
}
