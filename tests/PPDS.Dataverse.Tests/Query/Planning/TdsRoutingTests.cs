using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning;

[Trait("Category", "PlanUnit")]
public class TdsRoutingTests
{
    private readonly QueryPlanner _planner = new();

    // ── Routing to TDS ──────────────────────────────────────────────

    [Fact]
    public void Plan_TdsEnabled_CompatibleQuery_ProducesTdsScanNode()
    {
        var sql = "SELECT name FROM account";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        var tdsNode = Assert.IsType<TdsScanNode>(result.RootNode);
        Assert.Equal("account", tdsNode.EntityLogicalName);
        Assert.Equal(sql, tdsNode.Sql);
    }

    [Fact]
    public void Plan_TdsEnabled_CompatibleQuery_ReturnsOriginalSqlInFetchXml()
    {
        var sql = "SELECT name FROM account WHERE revenue > 1000";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        Assert.Contains(sql, result.FetchXml);
    }

    [Fact]
    public void Plan_TdsEnabled_CompatibleQuery_EntityLogicalNameSet()
    {
        var sql = "SELECT fullname FROM contact";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        Assert.Equal("contact", result.EntityLogicalName);
    }

    [Fact]
    public void Plan_TdsEnabled_CompatibleQuery_EmptyVirtualColumns()
    {
        var sql = "SELECT name FROM account";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        Assert.Empty(result.VirtualColumns);
    }

    [Fact]
    public void Plan_TdsEnabled_WithMaxRows_TdsScanNodeHasMaxRows()
    {
        var sql = "SELECT name FROM account";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor(),
            MaxRows = 100
        };

        var result = _planner.Plan(stmt, options);

        var tdsNode = Assert.IsType<TdsScanNode>(result.RootNode);
        Assert.Equal(100, tdsNode.MaxRows);
    }

    [Fact]
    public void Plan_TdsEnabled_WithTopInSql_TdsScanNodeHasTop()
    {
        var sql = "SELECT TOP 50 name FROM account";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        var tdsNode = Assert.IsType<TdsScanNode>(result.RootNode);
        Assert.Equal(50, tdsNode.MaxRows);
    }

    // ── Fallback to FetchXML ────────────────────────────────────────

    [Fact]
    public void Plan_TdsDisabled_ProducesFetchXmlScanNode()
    {
        var sql = "SELECT name FROM account";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = false,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_TdsEnabled_NoExecutor_FallsBackToFetchXml()
    {
        var sql = "SELECT name FROM account";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = null
        };

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_TdsEnabled_NoOriginalSql_FallsBackToFetchXml()
    {
        var sql = "SELECT name FROM account";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = null,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_TdsEnabled_IncompatibleEntity_FallsBackToFetchXml()
    {
        var sql = "SELECT name FROM activityparty";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_TdsEnabled_VirtualEntity_FallsBackToFetchXml()
    {
        var sql = "SELECT name FROM virtual_entity";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    [Fact]
    public void Plan_TdsEnabled_ElasticTable_FallsBackToFetchXml()
    {
        var sql = "SELECT name FROM msdyn_aborecord";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        Assert.IsType<FetchXmlScanNode>(result.RootNode);
    }

    // ── COUNT(*) always uses CountOptimizedNode ─────────────────────

    [Fact]
    public void Plan_TdsEnabled_BareCountStar_UsesCountOptimizedNodeNotTds()
    {
        var sql = "SELECT COUNT(*) FROM account";
        var stmt = SqlParser.Parse(sql);
        var options = new QueryPlanOptions
        {
            UseTdsEndpoint = true,
            OriginalSql = sql,
            TdsQueryExecutor = new StubTdsQueryExecutor()
        };

        var result = _planner.Plan(stmt, options);

        // COUNT(*) optimization takes priority over TDS routing
        Assert.IsType<CountOptimizedNode>(result.RootNode);
    }

    // ── TdsScanNode properties ──────────────────────────────────────

    [Fact]
    public void TdsScanNode_Description_IncludesEntityName()
    {
        var node = new TdsScanNode(
            "SELECT name FROM account",
            "account",
            new StubTdsQueryExecutor());

        Assert.Contains("account", node.Description);
        Assert.Contains("TdsScan", node.Description);
    }

    [Fact]
    public void TdsScanNode_Description_IncludesTopWhenPresent()
    {
        var node = new TdsScanNode(
            "SELECT TOP 10 name FROM account",
            "account",
            new StubTdsQueryExecutor(),
            maxRows: 10);

        Assert.Contains("top 10", node.Description);
    }

    [Fact]
    public void TdsScanNode_IsLeafNode_NoChildren()
    {
        var node = new TdsScanNode(
            "SELECT name FROM account",
            "account",
            new StubTdsQueryExecutor());

        Assert.Empty(node.Children);
    }

    [Fact]
    public void TdsScanNode_EstimatedRows_UnknownWhenNoMaxRows()
    {
        var node = new TdsScanNode(
            "SELECT name FROM account",
            "account",
            new StubTdsQueryExecutor());

        Assert.Equal(-1, node.EstimatedRows);
    }

    [Fact]
    public void TdsScanNode_EstimatedRows_EqualsMaxRowsWhenSet()
    {
        var node = new TdsScanNode(
            "SELECT name FROM account",
            "account",
            new StubTdsQueryExecutor(),
            maxRows: 500);

        Assert.Equal(500, node.EstimatedRows);
    }

    [Fact]
    public void TdsScanNode_Constructor_ThrowsOnNullSql()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TdsScanNode(null!, "account", new StubTdsQueryExecutor()));
    }

    [Fact]
    public void TdsScanNode_Constructor_ThrowsOnNullEntityName()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TdsScanNode("SELECT 1", null!, new StubTdsQueryExecutor()));
    }

    [Fact]
    public void TdsScanNode_Constructor_ThrowsOnNullExecutor()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TdsScanNode("SELECT 1", "account", null!));
    }

    // ── TdsScanNode execution ───────────────────────────────────────

    [Fact]
    public async Task TdsScanNode_ExecuteAsync_YieldsRowsFromTdsResult()
    {
        var records = new List<IReadOnlyDictionary<string, QueryValue>>
        {
            new Dictionary<string, QueryValue>
            {
                ["name"] = QueryValue.Simple("Contoso")
            },
            new Dictionary<string, QueryValue>
            {
                ["name"] = QueryValue.Simple("Fabrikam")
            }
        };

        var executor = new StubTdsQueryExecutor(records, "account");
        var node = new TdsScanNode("SELECT name FROM account", "account", executor);

        var context = CreateContext();
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal("Contoso", rows[0].Values["name"].Value);
        Assert.Equal("Fabrikam", rows[1].Values["name"].Value);
    }

    [Fact]
    public async Task TdsScanNode_ExecuteAsync_RespectsMaxRows()
    {
        var records = new List<IReadOnlyDictionary<string, QueryValue>>
        {
            new Dictionary<string, QueryValue> { ["name"] = QueryValue.Simple("A") },
            new Dictionary<string, QueryValue> { ["name"] = QueryValue.Simple("B") },
            new Dictionary<string, QueryValue> { ["name"] = QueryValue.Simple("C") }
        };

        var executor = new StubTdsQueryExecutor(records, "account");
        var node = new TdsScanNode("SELECT name FROM account", "account", executor, maxRows: 2);

        var context = CreateContext();
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task TdsScanNode_ExecuteAsync_IncrementsStatistics()
    {
        var records = new List<IReadOnlyDictionary<string, QueryValue>>
        {
            new Dictionary<string, QueryValue> { ["name"] = QueryValue.Simple("X") }
        };

        var executor = new StubTdsQueryExecutor(records, "account");
        var node = new TdsScanNode("SELECT name FROM account", "account", executor);

        var context = CreateContext();
        await foreach (var _ in node.ExecuteAsync(context))
        {
            // consume
        }

        Assert.Equal(1, context.Statistics.PagesFetched);
        Assert.Equal(1, context.Statistics.RowsRead);
    }

    [Fact]
    public async Task TdsScanNode_ExecuteAsync_PassesSqlToExecutor()
    {
        var executor = new StubTdsQueryExecutor();
        var sql = "SELECT name, revenue FROM account WHERE revenue > 1000";
        var node = new TdsScanNode(sql, "account", executor);

        var context = CreateContext();
        await foreach (var _ in node.ExecuteAsync(context))
        {
            // consume
        }

        Assert.Equal(sql, executor.LastExecutedSql);
    }

    [Fact]
    public async Task TdsScanNode_ExecuteAsync_PassesMaxRowsToExecutor()
    {
        var executor = new StubTdsQueryExecutor();
        var node = new TdsScanNode("SELECT name FROM account", "account", executor, maxRows: 42);

        var context = CreateContext();
        await foreach (var _ in node.ExecuteAsync(context))
        {
            // consume
        }

        Assert.Equal(42, executor.LastMaxRows);
    }

    [Fact]
    public async Task TdsScanNode_ExecuteAsync_ReportsCancellation()
    {
        var executor = new StubTdsQueryExecutor();
        var node = new TdsScanNode("SELECT name FROM account", "account", executor);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var context = CreateContext();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(context, cts.Token))
            {
                // Should not reach here
            }
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static QueryPlanContext CreateContext()
    {
        return new QueryPlanContext(
            new StubQueryExecutor(),
            new StubExpressionEvaluator());
    }

    /// <summary>
    /// Stub TDS query executor that returns configurable results.
    /// </summary>
    private sealed class StubTdsQueryExecutor : ITdsQueryExecutor
    {
        private readonly IReadOnlyList<IReadOnlyDictionary<string, QueryValue>> _records;
        private readonly string _entityName;

        public string? LastExecutedSql { get; private set; }
        public int? LastMaxRows { get; private set; }

        public StubTdsQueryExecutor(
            IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>? records = null,
            string? entityName = null)
        {
            _records = records ?? Array.Empty<IReadOnlyDictionary<string, QueryValue>>();
            _entityName = entityName ?? "unknown";
        }

        public Task<QueryResult> ExecuteSqlAsync(
            string sql, int? maxRows = null, CancellationToken cancellationToken = default)
        {
            LastExecutedSql = sql;
            LastMaxRows = maxRows;

            var result = new QueryResult
            {
                EntityLogicalName = _entityName,
                Columns = Array.Empty<QueryColumn>(),
                Records = _records,
                Count = _records.Count
            };

            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Stub IQueryExecutor for creating QueryPlanContext (not used in TDS tests).
    /// </summary>
    private sealed class StubQueryExecutor : IQueryExecutor
    {
        public Task<QueryResult> ExecuteFetchXmlAsync(
            string fetchXml, int? pageNumber = null, string? pagingCookie = null,
            bool includeCount = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(QueryResult.Empty("stub"));
        }

        public Task<QueryResult> ExecuteFetchXmlAllPagesAsync(
            string fetchXml, int maxRecords = 5000,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(QueryResult.Empty("stub"));
        }

        public Task<long?> GetTotalRecordCountAsync(
            string entityLogicalName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<long?>(0L);
        }
    }

    /// <summary>
    /// Stub IExpressionEvaluator for creating QueryPlanContext.
    /// </summary>
    private sealed class StubExpressionEvaluator : IExpressionEvaluator
    {
        public object? Evaluate(
            ISqlExpression expression,
            IReadOnlyDictionary<string, QueryValue> row)
        {
            return null;
        }

        public bool EvaluateCondition(
            ISqlCondition condition,
            IReadOnlyDictionary<string, QueryValue> row)
        {
            return false;
        }
    }
}
