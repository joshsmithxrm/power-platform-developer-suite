using System.Text.RegularExpressions;
using Microsoft.Crm.Sdk.Messages;
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
using PPDS.Dataverse.Query;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Query;

/// <summary>
/// Live differential proof of the DML allowlist (specs/query.md "DML allowlist"). Creates one throwaway table (every
/// column type the allowlist admits, with NULLs in each) in a throwaway solution, seeds a known set of rows, and for
/// every allowlisted shape runs the real statement through the real <see cref="SqlQueryService"/> with confirmation,
/// then compares the table's actual contents (every column, read through the SDK) with the result computed
/// independently here (T-SQL semantics, with the organisation's case- and accent-insensitive collation), and the counts
/// the product reports with what actually changed. One statement of every refused category is run too, to confirm
/// nothing changed. The table (and the solution, when this run created it) is deleted at the end, waiting out other
/// suites' customisation locks. It runs only against the organisation named in PPDS_DML_LIVE_CONFIRM_URL, which must
/// equal both DATAVERSE_URL and the URL of the organisation the profile connects to, and only with a solution named
/// ppds_DmlLive*. Leftovers of an interrupted run are removed by the separate cleanup-only operation.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class DmlLiveDifferentialTests : LiveTestBase
{
    private static readonly Regex OwnTable = new("^ppds_dmllive[0-9a-f]{8}$");

    /// <summary>The description every table of this suite carries: the mark that it is this suite's to delete.</summary>
    private const string TableMarker = "Temporary table for the PPDS DML live test. Safe to delete.";

    /// <summary>The friendly-name suffix of a solution this suite created: the mark that it is this suite's to delete.</summary>
    private const string SolutionMarker = " (disposable PPDS DML live test, safe to delete)";

    private readonly ITestOutputHelper _output;
    private readonly List<string> _failures = new();

    public DmlLiveDifferentialTests(ITestOutputHelper output) => _output = output;

    private static string Solution => Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!.Trim();

    private string _table = "";
    private bool _tableCreated;
    private bool _solutionCreated;
    private int[] _choices = [];
    private Guid _me;
    private Microsoft.PowerPlatform.Dataverse.Client.ServiceClient _client = null!;
    private SqlQueryService _service = null!;
    private int _passed;
    private int _refused;

    /// <summary>The seed rows as stored (read back from Dataverse after seeding): the truth every case starts from.</summary>
    private List<Row> _seed = new();

    /// <summary>The current id of each seed row (a row a case deleted is recreated with a new id).</summary>
    private Guid[] _ids = [];

    private string Pk => _table + "id";

    [DmlLiveFact]
    public async Task EveryAllowlistedShape_ChangesExactlyTheRowsAndValuesItsTextSays()
    {
        var expectedUrl = await ConnectAsync();

        _me = ((WhoAmIResponse)await _client.ExecuteAsync(new WhoAmIRequest())).UserId;
        using var source = _source!;
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });
        var bulk = new BulkOperationExecutor(pool, LiveTestHelpers.CreateThrottleTracker(),
            Options.Create(new DataverseOptions { BulkOperations = new BulkOperationOptions { BatchSize = 10, MaxParallelBatches = 1 } }),
            NullLogger<BulkOperationExecutor>.Instance);
        _service = new SqlQueryService(new QueryExecutor(pool), new Guard(), bulkOperationExecutor: bulk,
            metadataProvider: new CachedMetadataProvider(new DataverseMetadataQueryService(pool)));
        _service.ConfigureEnvironment(expectedUrl, null);
        _service.EnvironmentProtectionLevel = ProtectionLevel.Development;

        await CreateSolutionAsync();
        _table = $"ppds_dmllive{Guid.NewGuid().ToString("N")[..8]}";
        try
        {
            await CreateTableAsync();
            _seed = await SeedAsync();
            await RunAllCasesAsync();
        }
        finally
        {
            await CleanupAsync();
        }

        _output.WriteLine($"TOTALS pass {_passed}, refused {_refused}, fail {_failures.Count}");
        Assert.True(_failures.Count == 0, "Differences between observed and expected:\n" + string.Join("\n", _failures));
    }

    /// <summary>
    /// Removes what an interrupted run of this suite left behind: tables named like this suite's tables that carry its
    /// description and are older than an hour (so a run in progress is never touched), and the solution when its name
    /// marks it as this suite's and it is empty. Nothing else.
    /// </summary>
    [DmlLiveCleanupFact]
    public async Task CleanupOnly_RemovesOnlyThisSuitesLeftovers()
    {
        await ConnectAsync();
        using var source = _source!;
        var leftovers = await RemoveOwnTablesAsync(TimeSpan.FromHours(1));
        leftovers.AddRange(await RemoveSolutionIfOursAsync());
        Assert.True(leftovers.Count == 0, "Cleanup left: " + string.Join("; ", leftovers));
    }

    private LiveTestProfileConnectionSource? _source;

    private async Task<string> ConnectAsync()
    {
        // The organisation this suite may change is named explicitly for each run; nothing in the repository names it.
        var expectedUrl = ConfirmedUrl(Environment.GetEnvironmentVariable(DmlLiveSkip.ConfirmUrlVariable));
        Assert.Equal(expectedUrl, Configuration.DataverseUrl?.TrimEnd('/'));
        Assert.StartsWith("ppds_DmlLive", Solution, StringComparison.Ordinal);

        _source = await LiveTestProfileConnectionSource.CreateAsync(Configuration, "DmlLiveDifferentialTests", maxPoolSize: 2);
        var seedClient = _source.GetSeedClient();
        var connected = seedClient.ConnectedOrgUriActual?.GetLeftPart(UriPartial.Authority);
        Assert.Equal(expectedUrl, connected);
        _output.WriteLine($"Connected to the organisation named in {DmlLiveSkip.ConfirmUrlVariable} ({seedClient.ConnectedOrgFriendlyName}).");
        _client = seedClient;
        return expectedUrl;
    }

    /// <summary>
    /// The organisation URL the run is confirmed for: required, https, scheme and host only (as the connection reports it).
    /// Anything else stops the suite before it connects.
    /// </summary>
    internal static string ConfirmedUrl(string? value)
    {
        var url = value?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException(
                $"{DmlLiveSkip.ConfirmUrlVariable} is not set. Set it to the URL of the development organisation this suite may change " +
                "(the same value as DATAVERSE_URL); the suite does not run without it.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.GetLeftPart(UriPartial.Authority) != url)
        {
            throw new InvalidOperationException($"{DmlLiveSkip.ConfirmUrlVariable} must be an https organisation URL with no path, for example https://<org>.crm.dynamics.com.");
        }

        return url;
    }

    private Task Locked(Func<Task> operation, string what) => CustomizationLockRetry.RunAsync(operation, what, _output.WriteLine);

    private Task<T> Locked<T>(Func<Task<T>> operation, string what) => CustomizationLockRetry.RunAsync(operation, what, _output.WriteLine);

    // ── one case ────────────────────────────────────────────────────────────────────────────

    private async Task CaseAsync(string sql, Func<List<Row>, List<Row>> expected, bool observe = false, int? rowCap = null)
    {
        await ResetAsync();
        var before = await ReadAsync();
        SqlQueryResult result;
        try
        {
            result = await _service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = rowCap } });
        }
        catch (Exception ex)
        {
            Fail(sql, $"threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var actual = await ReadAsync();
        var want = expected(before);
        var wantKeys = want.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var actualKeys = actual.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var same = wantKeys.SequenceEqual(actualKeys);
        var changed = actual.Where(a => before.Any(b => b.Id == a.Id && b.Key != a.Key)).ToList();
        var removed = before.Where(b => actual.All(a => a.Id != b.Id)).ToList();
        var added = actual.Where(a => before.All(b => b.Id != a.Id)).ToList();
        if (observe || !same)
            _output.WriteLine($"OBSERVED {sql} -> changed [{string.Join(", ", changed.Select(Label))}] removed [{string.Join(", ", removed.Select(Label))}] added [{string.Join(", ", added.Select(Label))}]");

        // C-S1: the counts the product reports are what changed in Dataverse: rows written (their row version moved),
        // removed or added, and no failures.
        var written = Verb(sql) switch
        {
            "DELETE" => removed.Count,
            "INSERT" => added.Count,
            _ => actual.Count(a => before.Any(b => b.Id == a.Id && b.Version != a.Version))
        };
        var reported = result.DmlOutcome;
        var countsMatch = reported is { FailedRows: 0 } && reported.AffectedRows == written;

        if (!same)
            Fail(sql, $"expected\n    {string.Join("\n    ", wantKeys.Except(actualKeys))}\n  actual\n    {string.Join("\n    ", actualKeys.Except(wantKeys))}");
        else if (!countsMatch)
            Fail(sql, $"reported {reported?.AffectedRows.ToString() ?? "no"} affected / {reported?.FailedRows.ToString() ?? "no"} failed, but {written} rows changed in Dataverse");
        else
        {
            _passed++;
            _output.WriteLine($"PASS {sql} (reported {reported!.AffectedRows} affected = {written} changed)");
        }
    }

    private static string Verb(string sql)
        => Regex.Match(Regex.Replace(sql, "--[^\n]*", ""), @"\b(UPDATE|DELETE|INSERT)\b", RegexOptions.IgnoreCase).Value.ToUpperInvariant();

    /// <summary>The statement must fail (a refusal, or a clean error such as divide by zero) and change nothing.</summary>
    private async Task RefusedAsync(string sql, int? rowCap = null)
    {
        await ResetAsync();
        var before = (await ReadAsync()).Select(r => r.Key + "|" + r.Version).OrderBy(k => k, StringComparer.Ordinal).ToList();
        try
        {
            await _service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = rowCap } });
            Fail(sql, "was expected to be refused but ran");
        }
        catch (PpdsException ex)
        {
            _refused++;
            _output.WriteLine($"REFUSED ({ex.ErrorCode}) {sql}: {ex.UserMessage}");
        }
        catch (Exception ex)
        {
            Fail(sql, $"failed without a PpdsException: {ex.GetType().Name}: {ex.Message}");
        }

        // Every column and the row version: a refused statement wrote nothing at all.
        var after = (await ReadAsync()).Select(r => r.Key + "|" + r.Version).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (!before.SequenceEqual(after))
            Fail(sql, "a refused statement changed the table");
    }

    private void Fail(string sql, string what)
    {
        _failures.Add($"{sql}: {what}");
        _output.WriteLine($"FAIL {sql}: {what}");
    }

    private string Label(Row r) => r.Get<string>(N) is { } name ? $"'{name}'" : $"<null#{Array.IndexOf(_ids, r.Id)}>";

    // ── table ───────────────────────────────────────────────────────────────────────────────

    private async Task CreateSolutionAsync()
    {
        if (await FindSolutionAsync() is { } existing)
        {
            // Reused, and deleted at the end only if its name marks it as this suite's (C-S5).
            _output.WriteLine($"Using the existing solution {Solution} ({existing.GetAttributeValue<string>("friendlyname")}).");
            return;
        }
        var publisherQuery = new QueryExpression("publisher") { ColumnSet = new ColumnSet("publisherid", "customizationprefix") };
        publisherQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, "PPDSDemoPublisher");
        var publisher = (await _client.RetrieveMultipleAsync(publisherQuery)).Entities.Single();
        Assert.Equal("ppds", publisher.GetAttributeValue<string>("customizationprefix"));
        _output.WriteLine($"Creating solution {Solution}.");
        await Locked(() => _client.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = Solution, ["friendlyname"] = Solution + SolutionMarker,
            ["version"] = "1.0.0.0", ["publisherid"] = publisher.ToEntityReference()
        }), "create solution");
        _solutionCreated = true;
    }

    private async Task<Entity?> FindSolutionAsync()
    {
        var q = new QueryExpression("solution") { ColumnSet = new ColumnSet("solutionid", "friendlyname") };
        q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, Solution);
        return (await _client.RetrieveMultipleAsync(q)).Entities.FirstOrDefault();
    }

    private async Task CreateTableAsync()
    {
        _output.WriteLine($"Creating table {_table} in {Solution}.");
        await Locked(() => _client.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                SchemaName = _table, DisplayName = new Label("PPDS DML live test", 1033), DisplayCollectionName = new Label("PPDS DML live tests", 1033),
                Description = new Label(TableMarker, 1033), OwnershipType = OwnershipTypes.UserOwned
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                SchemaName = N, MaxLength = 100, FormatName = StringFormatName.Text, DisplayName = new Label("Name", 1033),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
            },
            SolutionUniqueName = Solution
        }), "create table");
        _tableCreated = true;

        var none = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None);
        var choice = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
        foreach (var label in new[] { "One", "Two", "Three" })
            choice.Options.Add(new OptionMetadata(new Label(label, 1033), null));
        Label L(string text) => new(text, 1033);
        AttributeMetadata[] columns =
        [
            new IntegerAttributeMetadata { SchemaName = Num, MinValue = -100000, MaxValue = 100000, DisplayName = L("Num"), RequiredLevel = none },
            new DecimalAttributeMetadata { SchemaName = Amount, Precision = 2, MinValue = -1000000, MaxValue = 1000000, DisplayName = L("Amount"), RequiredLevel = none },
            new MoneyAttributeMetadata { SchemaName = MoneyCol, Precision = 2, PrecisionSource = 0, MinValue = -1000000, MaxValue = 1000000, DisplayName = L("Money"), RequiredLevel = none },
            new DoubleAttributeMetadata { SchemaName = FloatCol, Precision = 2, MinValue = -1000000, MaxValue = 1000000, DisplayName = L("Float"), RequiredLevel = none },
            new DateTimeAttributeMetadata { SchemaName = When, Format = DateTimeFormat.DateAndTime, DateTimeBehavior = DateTimeBehavior.UserLocal, DisplayName = L("When"), RequiredLevel = none },
            new DateTimeAttributeMetadata { SchemaName = Day, Format = DateTimeFormat.DateOnly, DateTimeBehavior = DateTimeBehavior.DateOnly, DisplayName = L("Day"), RequiredLevel = none },
            // Two date kinds that are not verified, present only so the refusal is shown against real metadata.
            new DateTimeAttributeMetadata { SchemaName = Tzi, Format = DateTimeFormat.DateAndTime, DateTimeBehavior = DateTimeBehavior.TimeZoneIndependent, DisplayName = L("Tzi"), RequiredLevel = none },
            new DateTimeAttributeMetadata { SchemaName = LegacyDay, Format = DateTimeFormat.DateOnly, DateTimeBehavior = DateTimeBehavior.UserLocal, DisplayName = L("Legacy day"), RequiredLevel = none },
            new BooleanAttributeMetadata { SchemaName = Flag, DisplayName = L("Flag"), RequiredLevel = none,
                OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(L("Yes"), 1), new OptionMetadata(L("No"), 0)) },
            new PicklistAttributeMetadata { SchemaName = Choice, DisplayName = L("Choice"), RequiredLevel = none, OptionSet = choice },
            new StringAttributeMetadata { SchemaName = Note, MaxLength = 200, FormatName = StringFormatName.Text, DisplayName = L("Note"), RequiredLevel = none },
            new MemoAttributeMetadata { SchemaName = Memo, MaxLength = 2000, Format = StringFormat.TextArea, DisplayName = L("Memo"), RequiredLevel = none },
        ];
        foreach (var column in columns)
            await Locked(() => _client.ExecuteAsync(new CreateAttributeRequest { EntityName = _table, Attribute = column, SolutionUniqueName = Solution }), $"create column {column.SchemaName}");

        await Locked(() => _client.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = _table + "_parent", ReferencedEntity = _table, ReferencingEntity = _table,
                CascadeConfiguration = new CascadeConfiguration
                {
                    Assign = CascadeType.NoCascade, Delete = CascadeType.RemoveLink, Merge = CascadeType.NoCascade,
                    Reparent = CascadeType.NoCascade, Share = CascadeType.NoCascade, Unshare = CascadeType.NoCascade
                }
            },
            Lookup = new LookupAttributeMetadata { SchemaName = Parent, DisplayName = L("Parent"), RequiredLevel = none },
            SolutionUniqueName = Solution
        }), "create lookup");

        var picklist = (PicklistAttributeMetadata)((RetrieveAttributeResponse)await _client.ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = _table, LogicalName = Choice, RetrieveAsIfPublished = true })).AttributeMetadata;
        _choices = picklist.OptionSet.Options.Select(o => o.Value!.Value).ToArray();
    }

    // ── rows ────────────────────────────────────────────────────────────────────────────────

    private async Task<List<Row>> SeedAsync()
    {
        var model = SeedRows();
        _ids = new Guid[model.Length];
        for (var i = 0; i < model.Length; i++)
            _ids[i] = await _client.CreateAsync(ToEntity(model[i].With(Parent, null).With(State, null).With(Status, null), Guid.Empty));
        for (var i = 0; i < model.Length; i++)
            await _client.UpdateAsync(ToEntity(model[i], _ids[i]));

        // The truth is what Dataverse stored (it may normalise on save), read back without PPDS.
        var stored = await ReadAsync();
        var seed = _ids.Select(id => stored.Single(r => r.Id == id)).ToList();
        foreach (var column in new[] { N, Num, Amount, MoneyCol, FloatCol, When, Day, Flag, Choice, Note, Memo, Parent })
        {
            if (seed.All(r => r.V[column] is not null))
                throw new InvalidOperationException($"The seed has no NULL in {column}.");
        }
        return seed;
    }

    /// <summary>Restores exactly the seed rows: deletes anything else, recreates deleted ones, rewrites changed ones.</summary>
    private async Task ResetAsync()
    {
        var current = await ReadAsync();
        foreach (var extra in current.Where(c => !_ids.Contains(c.Id)))
            await _client.DeleteAsync(_table, extra.Id);
        var recreated = false;
        for (var i = 0; i < _ids.Length; i++)
        {
            if (current.All(c => c.Id != _ids[i]))
            {
                _ids[i] = await _client.CreateAsync(ToEntity(_seed[i].With(Parent, null).With(State, null).With(Status, null), Guid.Empty));
                recreated = true;
            }
        }
        if (recreated)
            current = await ReadAsync();

        for (var i = 0; i < _ids.Length; i++)
        {
            var row = current.Single(c => c.Id == _ids[i]);
            if (row.Key != _seed[i].Key)
                await _client.UpdateAsync(ToEntity(_seed[i], _ids[i]));
        }
    }

    private Entity ToEntity(Row row, Guid id)
    {
        var entity = id == Guid.Empty ? new Entity(_table) : new Entity(_table, id);
        entity[N] = row.Get<string>(N);
        entity[Num] = row.Get<int?>(Num);
        entity[Amount] = row.Get<decimal?>(Amount);
        entity[MoneyCol] = row.Get<decimal?>(MoneyCol) is { } m ? new Money(m) : null;
        entity[FloatCol] = row.Get<decimal?>(FloatCol) is { } f ? (double)f : null;
        entity[When] = row.Get<DateTime?>(When);
        entity[Day] = row.Get<DateTime?>(Day);
        entity[Tzi] = row.Get<DateTime?>(Tzi);
        entity[LegacyDay] = row.Get<DateTime?>(LegacyDay);
        entity[Flag] = row.Get<bool?>(Flag);
        entity[Choice] = row.Get<int?>(Choice) is { } c ? new OptionSetValue(c) : null;
        entity[Note] = row.Get<string>(Note);
        entity[Memo] = row.Get<string>(Memo);
        if (row.V.ContainsKey(Parent))
            entity[Parent] = row.Get<int?>(Parent) is { } p ? new EntityReference(_table, _ids[p]) : null;
        if (row.Get<int?>(State) is { } state)
            entity["statecode"] = new OptionSetValue(state);
        if (row.Get<int?>(Status) is { } status)
            entity["statuscode"] = new OptionSetValue(status);
        return entity;
    }

    /// <summary>Every column of the table (including those only refused cases touch, and the owner), and the row version.</summary>
    private async Task<List<Row>> ReadAsync()
    {
        var q = new QueryExpression(_table)
        {
            ColumnSet = new ColumnSet(N, Num, Amount, MoneyCol, FloatCol, When, Day, Tzi, LegacyDay, Flag, Choice, Note, Memo, Parent,
                "statecode", "statuscode", "ownerid", "versionnumber")
        };
        var entities = (await _client.RetrieveMultipleAsync(q)).Entities;
        return entities.Select(e =>
        {
            var parent = e.GetAttributeValue<EntityReference>(Parent)?.Id;
            var parentIndex = parent is { } pid ? Array.IndexOf(_ids, pid) : (int?)null;
            return new Row(e.Id, new Dictionary<string, object?>
            {
                [N] = e.GetAttributeValue<string>(N),
                [Num] = e.GetAttributeValue<int?>(Num),
                [Amount] = e.Contains(Amount) ? decimal.Round(e.GetAttributeValue<decimal>(Amount), 2) : null,
                [MoneyCol] = e.GetAttributeValue<Money>(MoneyCol) is { } m ? decimal.Round(m.Value, 2) : null,
                [FloatCol] = e.Contains(FloatCol) ? decimal.Round((decimal)e.GetAttributeValue<double>(FloatCol), 2) : null,
                [When] = e.Contains(When) ? e.GetAttributeValue<DateTime>(When).ToUniversalTime() : null,
                [Day] = e.Contains(Day) ? DateTime.SpecifyKind(e.GetAttributeValue<DateTime>(Day).Date, DateTimeKind.Unspecified) : null,
                [Tzi] = e.Contains(Tzi) ? DateTime.SpecifyKind(e.GetAttributeValue<DateTime>(Tzi), DateTimeKind.Utc) : null,
                [LegacyDay] = e.Contains(LegacyDay) ? e.GetAttributeValue<DateTime>(LegacyDay).ToUniversalTime() : null,
                [Flag] = e.Contains(Flag) ? e.GetAttributeValue<bool>(Flag) : null,
                [Choice] = e.GetAttributeValue<OptionSetValue>(Choice)?.Value,
                [Note] = e.GetAttributeValue<string>(Note),
                [Memo] = e.GetAttributeValue<string>(Memo),
                [Parent] = parentIndex is -1 ? -1 : parentIndex,
                [State] = e.GetAttributeValue<OptionSetValue>("statecode")?.Value,
                [Status] = e.GetAttributeValue<OptionSetValue>("statuscode")?.Value,
                [Owner] = e.GetAttributeValue<EntityReference>("ownerid")?.Id,
            }, e.GetAttributeValue<long?>("versionnumber"));
        }).ToList();
    }

    // ── cleanup ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes tables of this suite in the solution: named like this suite's tables, carrying its description, and
    /// created at least <paramref name="minimumAge"/> ago (so another run in progress is never touched). Waits out
    /// customisation locks held by other suites.
    /// </summary>
    private async Task<List<string>> RemoveOwnTablesAsync(TimeSpan minimumAge)
    {
        var leftovers = new List<string>();
        var solution = await FindSolutionAsync();
        if (solution is null)
            return leftovers;

        var components = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid", "componenttype") };
        components.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solution.Id);
        components.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 1);
        var tableIds = (await _client.RetrieveMultipleAsync(components)).Entities.Select(c => c.GetAttributeValue<Guid>("objectid")).ToHashSet();
        if (tableIds.Count == 0)
            return leftovers;

        var all = ((RetrieveAllEntitiesResponse)await _client.ExecuteAsync(new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true })).EntityMetadata;
        foreach (var table in all.Where(e => e.MetadataId is { } id && tableIds.Contains(id)))
        {
            var ours = OwnTable.IsMatch(table.LogicalName)
                && table.Description?.UserLocalizedLabel?.Label == TableMarker;
            if (!ours)
            {
                leftovers.Add($"table {table.LogicalName} (in {Solution} but not marked as this suite's, so not touched)");
                continue;
            }
            if (table.CreatedOn is { } created && DateTime.UtcNow - created.ToUniversalTime() < minimumAge)
            {
                _output.WriteLine($"Keeping table {table.LogicalName}: created {created:u}, possibly by a run in progress.");
                continue;
            }

            _output.WriteLine($"Deleting leftover table {table.LogicalName}.");
            leftovers.AddRange(await DeleteTableAsync(table.LogicalName));
        }
        return leftovers;
    }

    private async Task<List<string>> DeleteTableAsync(string logicalName)
    {
        try
        {
            await Locked(() => _client.ExecuteAsync(new DeleteEntityRequest { LogicalName = logicalName }), $"delete table {logicalName}");
            return [];
        }
        catch (Exception ex)
        {
            return [$"table {logicalName} ({ex.Message})"];
        }
    }

    /// <summary>Deletes the solution when this run created it, or its name marks it as this suite's, and it is empty.</summary>
    private async Task<List<string>> RemoveSolutionIfOursAsync()
    {
        var solution = await FindSolutionAsync();
        if (solution is null)
            return [];
        var marked = solution.GetAttributeValue<string>("friendlyname")?.EndsWith(SolutionMarker, StringComparison.Ordinal) == true;
        if (!_solutionCreated && !marked)
        {
            _output.WriteLine($"CLEANUP solution {Solution} kept: this run did not create it and its name does not mark it as this suite's.");
            return [];
        }

        var components = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
        components.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solution.Id);
        var count = (await _client.RetrieveMultipleAsync(components)).Entities.Count;
        if (count > 0)
            return [$"solution {Solution} (still has {count} components, so kept)"];
        try
        {
            await Locked(() => _client.DeleteAsync("solution", solution.Id), $"delete solution {Solution}");
        }
        catch (Exception ex)
        {
            return [$"solution {Solution} ({ex.Message})"];
        }

        var gone = await FindSolutionAsync() is null;
        _output.WriteLine($"CLEANUP solution {Solution} gone: {gone}");
        return gone ? [] : [$"solution {Solution}"];
    }

    /// <summary>Deletes the table this run created, then the solution if it is this suite's and empty.</summary>
    private async Task CleanupAsync()
    {
        var leftovers = new List<string>();
        if (_tableCreated)
        {
            leftovers.AddRange(await DeleteTableAsync(_table));
            var remaining = ((RetrieveAllEntitiesResponse)await _client.ExecuteAsync(new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true }))
                .EntityMetadata.Any(e => e.LogicalName == _table);
            _output.WriteLine($"CLEANUP table {_table} gone: {!remaining}");
            if (remaining && leftovers.Count == 0)
                leftovers.Add($"table {_table}");
        }

        leftovers.AddRange(await RemoveSolutionIfOursAsync());
        if (leftovers.Count > 0)
            _failures.Add("Cleanup failed; leftovers: " + string.Join("; ", leftovers));
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
