using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// A <see cref="SqlQueryService"/> over fake executors that records every read (FetchXML, page, cookie) and every
/// bulk write (operation, entity, row count), so a test can prove exactly what a DML request changed.
/// </summary>
internal sealed class DmlTestHarness
{
    public required SqlQueryService Service { get; init; }
    public required Mock<IBulkOperationExecutor> Bulk { get; init; }

    /// <summary>Every id passed to a bulk delete, in call order.</summary>
    public List<Guid> DeletedIds { get; } = new();

    /// <summary>The bulk options of the latest write.</summary>
    public BulkOperationOptions? LastOptions { get; set; }

    /// <summary>Every bulk write: ("Delete"|"Update"|"Create", entity, rows).</summary>
    public List<(string Operation, string Entity, int Rows)> Writes { get; } = new();

    /// <summary>Every FetchXML read with the paging arguments the scan passed.</summary>
    public List<(string FetchXml, int? Page, string? Cookie)> Reads { get; } = new();

    public IEnumerable<int> Deleted => Writes.Where(w => w.Operation == "Delete").Select(w => w.Rows);
    public IEnumerable<int> Updated => Writes.Where(w => w.Operation == "Update").Select(w => w.Rows);
    public IEnumerable<int> Created => Writes.Where(w => w.Operation == "Create").Select(w => w.Rows);

    /// <summary>Called before each bulk write returns; lets a test throw or cancel mid-statement.</summary>
    public Action<string, int, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>? OnWrite { get; set; }

    /// <summary>The id of row <paramref name="index"/> of page <paramref name="page"/> (distinct across pages).</summary>
    public static Guid Id(int index, int page = 1) => Guid.Parse($"00000000-0000-0000-{page:D4}-{index + 1:D12}");

    /// <summary>The lookup value of the odd rows (parentaccountid); even rows have none.</summary>
    public static readonly Guid ParentId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static QueryResult Rows(int count, bool moreRecords = false, int pageNumber = 1) => new()
    {
        PageNumber = pageNumber,
        EntityLogicalName = "account",
        Columns = new[] { new QueryColumn { LogicalName = "accountid" }, new QueryColumn { LogicalName = "name" } },
        Records = Enumerable.Range(0, count).Select(i => (IReadOnlyDictionary<string, QueryValue>)new Dictionary<string, QueryValue>
        {
            ["accountid"] = QueryValue.Simple(Id(i, pageNumber)),
            ["contactid"] = QueryValue.Simple(Guid.NewGuid()),
            ["name"] = QueryValue.Simple("n" + i),
            // revenue 500, 1000, 1500, ...; description null on even rows, "d<i>" on odd rows
            ["revenue"] = QueryValue.Simple(500m * (i + 1)),
            ["numberofemployees"] = QueryValue.Simple(10 * (i + 1)),
            ["description"] = i % 2 == 0 ? QueryValue.Null : QueryValue.Simple("d" + i),
            // status 0 (active) on even rows, 1 on odd rows; the other columns alternate or count up the same way
            ["statecode"] = QueryValue.Simple(i % 2),
            ["statuscode"] = QueryValue.Simple(i % 2 == 0 ? 1 : 2),
            ["creditlimit"] = QueryValue.Simple(5m * i),
            ["customertypecode"] = QueryValue.Simple(i % 2 + 1),
            ["parentaccountid"] = i % 2 == 1 ? QueryValue.Simple(ParentId) : QueryValue.Null,
            ["notes"] = QueryValue.Simple("note" + i),
            ["donotemail"] = QueryValue.Simple(i % 2 == 0),
            ["followupby"] = QueryValue.Simple(new DateTime(2024, 1, i + 1, 0, 0, 0, DateTimeKind.Utc)),
            ["birthday"] = QueryValue.Simple(new DateTime(2024, 1, 15 + i, 0, 0, 0, DateTimeKind.Unspecified))
        }).ToArray(),
        Count = count,
        MoreRecords = moreRecords,
        PagingCookie = moreRecords ? "next" : null
    };

    /// <summary>
    /// Contact reads return <paramref name="contactRows"/> rows; everything else returns <paramref name="rows"/>.
    /// Each read reports one page of rows; <paramref name="pages"/> pages are available before MoreRecords is false.
    /// </summary>
    public static DmlTestHarness Create(
        int rows,
        int? contactRows = null,
        int pages = 1,
        ProtectionLevel protection = ProtectionLevel.Development,
        bool duplicateRows = false,
        Action<int, Dictionary<string, QueryValue>>? shapeRow = null)
    {
        var executor = new Mock<IQueryExecutor>(MockBehavior.Loose);
        var bulk = new Mock<IBulkOperationExecutor>(MockBehavior.Loose);
        var harness = new DmlTestHarness
        {
            Bulk = bulk,
            Service = new SqlQueryService(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: bulk.Object,
                metadataProvider: TestMetadata.Provider)
            {
                EnvironmentProtectionLevel = protection
            }
        };

        QueryResult Pick(string fetchXml, int? page, string? cookie)
        {
            harness.Reads.Add((fetchXml, page, cookie));
            var count = fetchXml.Contains("contact") ? contactRows ?? rows : rows;
            var more = (page ?? 1) < pages;
            var source = Rows(count, more, page ?? 1);
            if (shapeRow != null)
            {
                // A test's own source values (for example text with white space at either end), row by row.
                source = new QueryResult
                {
                    PageNumber = source.PageNumber, EntityLogicalName = source.EntityLogicalName, Columns = source.Columns,
                    Records = source.Records.Select((r, i) =>
                    {
                        var shaped = new Dictionary<string, QueryValue>(r, StringComparer.OrdinalIgnoreCase);
                        shapeRow(i, shaped);
                        return (IReadOnlyDictionary<string, QueryValue>)shaped;
                    }).ToArray(),
                    Count = source.Count, MoreRecords = source.MoreRecords, PagingCookie = source.PagingCookie
                };
            }
            var result = OnlyRequestedAttributes(source, fetchXml);
            if (duplicateRows)
            {
                // Each record twice (same id), as a one-to-many source would produce.
                result = new QueryResult
                {
                    PageNumber = result.PageNumber, EntityLogicalName = result.EntityLogicalName, Columns = result.Columns,
                    Records = result.Records.SelectMany(r => new[] { r, r }).ToArray(), Count = result.Count * 2,
                    MoreRecords = result.MoreRecords, PagingCookie = result.PagingCookie
                };
            }
            return result;
        }

        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string f, int? p, string? c, bool _, CancellationToken _) => Pick(f, p, c));
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string f, int? p, string? c, bool _, QueryExecutionOptions? _, CancellationToken _) => Pick(f, p, c));

        BulkOperationResult Record(string operation, string entity, int n, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? progress)
        {
            harness.OnWrite?.Invoke(entity, n, progress);
            harness.Writes.Add((operation, entity, n));
            return new BulkOperationResult { SuccessCount = n };
        }

        bulk.Setup(b => b.DeleteMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string entity, IEnumerable<Guid> ids, BulkOperationOptions? options, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? progress, CancellationToken _) =>
            {
                harness.DeletedIds.AddRange(ids);
                harness.LastOptions = options;
                return Task.FromResult(Record("Delete", entity, ids.Count(), progress));
            });
        bulk.Setup(b => b.UpdateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string entity, IEnumerable<Microsoft.Xrm.Sdk.Entity> e, BulkOperationOptions? options, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? progress, CancellationToken _) =>
            {
                harness.WrittenEntities.AddRange(e);
                harness.LastOptions = options;
                return Task.FromResult(Record("Update", entity, e.Count(), progress));
            });
        bulk.Setup(b => b.CreateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string entity, IEnumerable<Microsoft.Xrm.Sdk.Entity> e, BulkOperationOptions? options, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? progress, CancellationToken _) =>
            {
                harness.WrittenEntities.AddRange(e);
                harness.LastOptions = options;
                return Task.FromResult(Record("Create", entity, e.Count(), progress));
            });
        return harness;
    }

    public void AssertNothingChanged() => Xunit.Assert.Empty(Writes);

    /// <summary>
    /// When true (set per test), the fake evaluates the FetchXML filter and count like Dataverse instead of returning every
    /// row, so a dropped or loosened condition changes the rows a statement touches. Async-local: tests run in parallel.
    /// </summary>
    public static bool Faithful => FaithfulMode.Value;

    /// <summary>Set to true in a test to make <see cref="Faithful"/> true for that test's async flow.</summary>
    public static readonly AsyncLocal<bool> FaithfulMode = new();

    private static int CompareValues(object? a, object? b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
        _ => Convert.ToDecimal(a, System.Globalization.CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, System.Globalization.CultureInfo.InvariantCulture))
    };

    private static QueryResult With(QueryResult result, IReadOnlyDictionary<string, QueryValue>[] records) => new()
    {
        PageNumber = result.PageNumber, EntityLogicalName = result.EntityLogicalName, Columns = result.Columns,
        Records = records, Count = records.Length, MoreRecords = result.MoreRecords, PagingCookie = result.PagingCookie
    };

    /// <summary>Every entity passed to a bulk create or update, in call order.</summary>
    public List<Microsoft.Xrm.Sdk.Entity> WrittenEntities { get; } = new();

    /// <summary>
    /// Like Dataverse, returns only the attributes the FetchXML asks for (all of them for &lt;all-attributes/&gt;), so a
    /// test fails when the engine does not request a column it later reads.
    /// </summary>
    public static QueryResult OnlyRequestedAttributes(QueryResult result, string fetchXml)
    {
        var doc = System.Xml.Linq.XDocument.Parse(fetchXml);
        var entity = doc.Root!.Element("entity")!;
        var records = result.Records.AsEnumerable();
        if (Faithful)
        {
            // Like Dataverse: the filter selects the rows, count/top limits them.
            records = records.Where(r => entity.Elements("filter").All(f => FetchXmlFilter.Matches(f, r)));
            foreach (var order in entity.Elements("order").Reverse())
            {
                var attribute = (string)order.Attribute("attribute")!;
                object? Key(IReadOnlyDictionary<string, QueryValue> r) => r.TryGetValue(attribute, out var v) ? v.Value : null;
                records = (string?)order.Attribute("descending") == "true"
                    ? records.OrderByDescending(Key, Comparer<object?>.Create(CompareValues))
                    : records.OrderBy(Key, Comparer<object?>.Create(CompareValues));
            }
        }

        // Like Dataverse, count/top limit the rows (always, not only in faithful mode), so a row limit that reached a DML
        // source scan would change fewer rows and the test would see it.
        var limit = (string?)doc.Root.Attribute("count") ?? (string?)doc.Root.Attribute("top");
        if (limit != null)
            records = records.Take(int.Parse(limit, System.Globalization.CultureInfo.InvariantCulture));

        if (entity.Element("all-attributes") != null)
            return With(result, records.ToArray());

        // Each attribute comes back under its alias when it has one, under its name otherwise (as Dataverse returns it).
        var requested = entity.Elements("attribute")
            .Select(a => (Name: (string)a.Attribute("name")!, Key: (string?)a.Attribute("alias") ?? (string)a.Attribute("name")!))
            .ToList();
        var returned = records
            .Select(r => (IReadOnlyDictionary<string, QueryValue>)requested
                .ToDictionary(q => q.Key, q => r.TryGetValue(q.Name, out var v) ? v : QueryValue.Null, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return new QueryResult
        {
            PageNumber = result.PageNumber,
            EntityLogicalName = result.EntityLogicalName,
            Columns = requested.Select(r => new QueryColumn { LogicalName = r.Key }).ToArray(),
            Records = returned,
            Count = returned.Length,
            MoreRecords = result.MoreRecords,
            PagingCookie = result.PagingCookie
        };
    }
}
