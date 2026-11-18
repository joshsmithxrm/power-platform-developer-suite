using System.Globalization;
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
/// then compares the table's actual contents with the result computed independently here (T-SQL semantics, with the
/// organisation's case- and accent-insensitive collation). One statement of every refused category is run too, to
/// confirm nothing changed. The table and solution are deleted at the end, waiting out other suites' customisation locks.
/// Only for https://orgcabef92d.crm.dynamics.com with a solution named ppds_DmlLive*.
/// Set PPDS_DML_LIVE_CLEANUP_ONLY=1 to only remove this suite's leftover tables and solution.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class DmlLiveDifferentialTests : LiveTestBase
{
    private const string ExpectedUrl = "https://orgcabef92d.crm.dynamics.com";
    private static readonly Regex OwnTable = new("^ppds_dmllive[0-9a-f]{8}$");
    private readonly ITestOutputHelper _output;
    private readonly List<string> _failures = new();

    public DmlLiveDifferentialTests(ITestOutputHelper output) => _output = output;

    private static string Solution => Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!.Trim();

    private string _table = "";
    private int[] _choices = [];
    private Guid _me;
    private Microsoft.PowerPlatform.Dataverse.Client.ServiceClient _client = null!;
    private SqlQueryService _service = null!;

    /// <summary>The seed rows as stored (read back from Dataverse after seeding): the truth every case starts from.</summary>
    private List<Row> _seed = new();

    /// <summary>The current id of each seed row (a row a case deleted is recreated with a new id).</summary>
    private Guid[] _ids = [];

    private string Pk => _table + "id";

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

        if (Environment.GetEnvironmentVariable("PPDS_DML_LIVE_CLEANUP_ONLY") == "1")
        {
            await CleanupAsync();
            return;
        }

        _me = ((WhoAmIResponse)await _client.ExecuteAsync(new WhoAmIRequest())).UserId;
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });
        var bulk = new BulkOperationExecutor(pool, LiveTestHelpers.CreateThrottleTracker(),
            Options.Create(new DataverseOptions { BulkOperations = new BulkOperationOptions { BatchSize = 10, MaxParallelBatches = 1 } }),
            NullLogger<BulkOperationExecutor>.Instance);
        _service = new SqlQueryService(new QueryExecutor(pool), new Guard(), bulkOperationExecutor: bulk,
            metadataProvider: new CachedMetadataProvider(new DataverseMetadataQueryService(pool)));
        _service.ConfigureEnvironment(ExpectedUrl, null);
        _service.EnvironmentProtectionLevel = ProtectionLevel.Development;

        // Leftovers of an earlier run of this suite (its own tables only) go first.
        await RemoveOwnTablesAsync();
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

        Assert.True(_failures.Count == 0, "Differences between observed and expected:\n" + string.Join("\n", _failures));
    }

    private Task Locked(Func<Task> operation, string what) => CustomizationLockRetry.RunAsync(operation, what, _output.WriteLine);

    private Task<T> Locked<T>(Func<Task<T>> operation, string what) => CustomizationLockRetry.RunAsync(operation, what, _output.WriteLine);

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
        var wantKeys = want.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var actualKeys = actual.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var same = wantKeys.SequenceEqual(actualKeys);
        if (observe || !same)
        {
            var changed = actual.Where(a => before.Any(b => b.Id == a.Id && b.Key != a.Key)).Select(Label);
            var removed = before.Where(b => actual.All(a => a.Id != b.Id)).Select(Label);
            var added = actual.Where(a => before.All(b => b.Id != a.Id)).Select(Label);
            _output.WriteLine($"OBSERVED {sql} -> changed [{string.Join(", ", changed)}] removed [{string.Join(", ", removed)}] added [{string.Join(", ", added)}]");
        }

        if (!same)
            Fail(sql, $"expected\n    {string.Join("\n    ", wantKeys.Except(actualKeys))}\n  actual\n    {string.Join("\n    ", actualKeys.Except(wantKeys))}");
        else
            _output.WriteLine($"PASS {sql}");
    }

    /// <summary>The statement must fail (a refusal, or a clean error such as divide by zero) and change nothing.</summary>
    private async Task RefusedAsync(string sql, int? rowCap = null)
    {
        await ResetAsync();
        var before = (await ReadAsync()).Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        try
        {
            await _service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = rowCap } });
            Fail(sql, "was expected to be refused but ran");
        }
        catch (PpdsException ex)
        {
            _output.WriteLine($"REFUSED ({ex.ErrorCode}) {sql}: {ex.UserMessage}");
        }
        catch (Exception ex)
        {
            Fail(sql, $"failed without a PpdsException: {ex.GetType().Name}: {ex.Message}");
        }

        var after = (await ReadAsync()).Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
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
        if (await FindSolutionAsync() is not null)
            return;
        var publisherQuery = new QueryExpression("publisher") { ColumnSet = new ColumnSet("publisherid", "customizationprefix") };
        publisherQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, "PPDSDemoPublisher");
        var publisher = (await _client.RetrieveMultipleAsync(publisherQuery)).Entities.Single();
        Assert.Equal("ppds", publisher.GetAttributeValue<string>("customizationprefix"));
        _output.WriteLine($"Creating solution {Solution}.");
        await Locked(() => _client.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = Solution, ["friendlyname"] = Solution + " (disposable PPDS DML live test, safe to delete)",
            ["version"] = "1.0.0.0", ["publisherid"] = publisher.ToEntityReference()
        }), "create solution");
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
        await Locked(() => _client.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                SchemaName = _table, DisplayName = new Label("PPDS DML live test", 1033), DisplayCollectionName = new Label("PPDS DML live tests", 1033),
                Description = new Label("Temporary table for the PPDS DML live test. Safe to delete.", 1033), OwnershipType = OwnershipTypes.UserOwned
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                SchemaName = N, MaxLength = 100, FormatName = StringFormatName.Text, DisplayName = new Label("Name", 1033),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
            },
            SolutionUniqueName = Solution
        }), "create table");

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
        return _ids.Select(id => stored.Single(r => r.Id == id)).ToList();
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

    private async Task<List<Row>> ReadAsync()
    {
        var q = new QueryExpression(_table) { ColumnSet = new ColumnSet(N, Num, Amount, MoneyCol, FloatCol, When, Day, Flag, Choice, Note, Memo, Parent, "statecode", "statuscode") };
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
                [Flag] = e.Contains(Flag) ? e.GetAttributeValue<bool>(Flag) : null,
                [Choice] = e.GetAttributeValue<OptionSetValue>(Choice)?.Value,
                [Note] = e.GetAttributeValue<string>(Note),
                [Memo] = e.GetAttributeValue<string>(Memo),
                [Parent] = parentIndex is -1 ? -1 : parentIndex,
                [State] = e.GetAttributeValue<OptionSetValue>("statecode")?.Value,
                [Status] = e.GetAttributeValue<OptionSetValue>("statuscode")?.Value,
            });
        }).ToList();
    }

    // ── cleanup ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes every table of this suite (named ppds_dmllive + 8 hex and a component of the ppds_DmlLive solution),
    /// waiting out customisation locks held by other suites.
    /// </summary>
    private async Task<List<string>> RemoveOwnTablesAsync()
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
            if (!OwnTable.IsMatch(table.LogicalName))
            {
                leftovers.Add($"table {table.LogicalName} (in {Solution} but not named like this suite's tables, so not touched)");
                continue;
            }

            _output.WriteLine($"Deleting table {table.LogicalName}.");
            try
            {
                await Locked(() => _client.ExecuteAsync(new DeleteEntityRequest { LogicalName = table.LogicalName }), $"delete table {table.LogicalName}");
            }
            catch (Exception ex)
            {
                leftovers.Add($"table {table.LogicalName} ({ex.Message})");
            }
        }
        return leftovers;
    }

    private async Task CleanupAsync()
    {
        var leftovers = await RemoveOwnTablesAsync();

        var remaining = ((RetrieveAllEntitiesResponse)await _client.ExecuteAsync(new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true }))
            .EntityMetadata.Where(e => OwnTable.IsMatch(e.LogicalName)).Select(e => e.LogicalName).ToList();
        if (_table.Length > 0)
            _output.WriteLine($"CLEANUP table {_table} gone: {!remaining.Contains(_table)}");
        foreach (var name in remaining)
        {
            _output.WriteLine($"CLEANUP table {name} still present");
            if (!leftovers.Any(l => l.Contains(name, StringComparison.Ordinal)))
                leftovers.Add($"table {name}");
        }

        var solution = await FindSolutionAsync();
        if (solution != null)
        {
            var components = new QueryExpression("solutioncomponent") { ColumnSet = new ColumnSet("objectid") };
            components.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solution.Id);
            var count = (await _client.RetrieveMultipleAsync(components)).Entities.Count;
            if (count == 0)
            {
                try
                {
                    await Locked(() => _client.DeleteAsync("solution", solution.Id), $"delete solution {Solution}");
                }
                catch (Exception ex)
                {
                    leftovers.Add($"solution {Solution} ({ex.Message})");
                }
            }
            else
            {
                leftovers.Add($"solution {Solution} (still has {count} components, so kept)");
            }
        }

        var solutionGone = await FindSolutionAsync() is null;
        _output.WriteLine($"CLEANUP solution {Solution} gone: {solutionGone}");
        if (!solutionGone && !leftovers.Any(l => l.StartsWith("solution", StringComparison.Ordinal)))
            leftovers.Add($"solution {Solution}");

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
