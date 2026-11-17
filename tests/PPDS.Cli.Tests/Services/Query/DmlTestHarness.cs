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

    public static QueryResult Rows(int count, bool moreRecords = false, int pageNumber = 1) => new()
    {
        PageNumber = pageNumber,
        EntityLogicalName = "account",
        Columns = new[] { new QueryColumn { LogicalName = "accountid" }, new QueryColumn { LogicalName = "name" } },
        Records = Enumerable.Range(0, count).Select(i => (IReadOnlyDictionary<string, QueryValue>)new Dictionary<string, QueryValue>
        {
            ["accountid"] = QueryValue.Simple(Guid.NewGuid()),
            ["contactid"] = QueryValue.Simple(Guid.NewGuid()),
            ["name"] = QueryValue.Simple("n" + i),
            // revenue 500, 1000, 1500, ...; description null on even rows, "d<i>" on odd rows
            ["revenue"] = QueryValue.Simple(500m * (i + 1)),
            ["description"] = i % 2 == 0 ? QueryValue.Null : QueryValue.Simple("d" + i)
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
        bool duplicateRows = false)
    {
        var executor = new Mock<IQueryExecutor>(MockBehavior.Loose);
        var bulk = new Mock<IBulkOperationExecutor>(MockBehavior.Loose);
        var harness = new DmlTestHarness
        {
            Bulk = bulk,
            Service = new SqlQueryService(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: bulk.Object)
            {
                EnvironmentProtectionLevel = protection
            }
        };

        QueryResult Pick(string fetchXml, int? page, string? cookie)
        {
            harness.Reads.Add((fetchXml, page, cookie));
            var count = fetchXml.Contains("contact") ? contactRows ?? rows : rows;
            var more = (page ?? 1) < pages;
            var result = OnlyRequestedAttributes(Rows(count, more, page ?? 1), fetchXml);
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

    /// <summary>Every entity passed to a bulk create or update, in call order.</summary>
    public List<Microsoft.Xrm.Sdk.Entity> WrittenEntities { get; } = new();

    /// <summary>
    /// Like Dataverse, returns only the attributes the FetchXML asks for (all of them for &lt;all-attributes/&gt;), so a
    /// test fails when the engine does not request a column it later reads.
    /// </summary>
    public static QueryResult OnlyRequestedAttributes(QueryResult result, string fetchXml)
    {
        if (fetchXml.Contains("<all-attributes", StringComparison.OrdinalIgnoreCase))
            return result;

        var requested = System.Text.RegularExpressions.Regex.Matches(fetchXml, "<attribute name=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new QueryResult
        {
            PageNumber = result.PageNumber,
            EntityLogicalName = result.EntityLogicalName,
            Columns = result.Columns.Where(c => requested.Contains(c.LogicalName)).ToArray(),
            Records = result.Records
                .Select(r => (IReadOnlyDictionary<string, QueryValue>)r.Where(kv => requested.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value))
                .ToArray(),
            Count = result.Count,
            MoreRecords = result.MoreRecords,
            PagingCookie = result.PagingCookie
        };
    }
}
