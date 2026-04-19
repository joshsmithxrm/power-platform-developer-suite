using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Dataverse.Tests.Query;

[Trait("Category", "PlanUnit")]
public class QueryExecutorGetCountTests
{
    /// <summary>
    /// Builds a <see cref="RetrieveTotalRecordCountResponse"/> with the given entity counts.
    /// Uses the SDK <see cref="EntityRecordCountCollection"/> type so the strongly-typed
    /// property accessor works correctly.
    /// </summary>
    private static RetrieveTotalRecordCountResponse BuildCountResponse(
        params (string entity, long count)[] entries)
    {
        var collection = new EntityRecordCountCollection();
        foreach (var (entity, count) in entries)
        {
            collection.Add(entity, count);
        }

        var response = new RetrieveTotalRecordCountResponse();
        response.Results["EntityRecordCountCollection"] = collection;
        return response;
    }

    /// <summary>
    /// Creates a mock pool that returns a mock client. The client's ExecuteAsync
    /// is set up to return the given response for any OrganizationRequest.
    /// </summary>
    private static (Mock<IDataverseConnectionPool> pool, Mock<IPooledClient> client) CreateMockPool(
        OrganizationResponse response)
    {
        var mockClient = new Mock<IPooledClient>();
        mockClient
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var mockPool = new Mock<IDataverseConnectionPool>();
        mockPool
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        return (mockPool, mockClient);
    }

    [Fact]
    public async Task GetTotalRecordCountAsync_ReturnsCount_WhenEntityFound()
    {
        // Arrange
        var response = BuildCountResponse(("account", 42000L));
        var (mockPool, mockClient) = CreateMockPool(response);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var count = await executor.GetTotalRecordCountAsync("account");

        // Assert
        Assert.Equal(42000L, count);
        mockClient.Verify(
            c => c.ExecuteAsync(
                It.Is<RetrieveTotalRecordCountRequest>(r =>
                    r.EntityNames != null && r.EntityNames.Length == 1 && r.EntityNames[0] == "account"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetTotalRecordCountAsync_ReturnsNull_WhenEntityNotInCollection()
    {
        // Arrange: response has a different entity
        var response = BuildCountResponse(("contact", 100L));
        var (mockPool, _) = CreateMockPool(response);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var count = await executor.GetTotalRecordCountAsync("account");

        // Assert
        Assert.Null(count);
    }

    [Fact]
    public async Task GetTotalRecordCountAsync_DisposesPooledClient()
    {
        // Arrange
        var response = BuildCountResponse(("account", 1L));
        var (mockPool, mockClient) = CreateMockPool(response);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        await executor.GetTotalRecordCountAsync("account");

        // Assert: client was disposed (returned to pool)
        mockClient.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task GetTotalRecordCountAsync_PassesCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var response = BuildCountResponse(("lead", 500L));
        var (mockPool, mockClient) = CreateMockPool(response);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var count = await executor.GetTotalRecordCountAsync("lead", token);

        // Assert
        Assert.Equal(500L, count);
        mockPool.Verify(
            p => p.GetClientAsync(null, null, token),
            Times.Once);
        mockClient.Verify(
            c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), token),
            Times.Once);
    }

    [Fact]
    public async Task GetTotalRecordCountAsync_ThrowsForNullEntityName()
    {
        // Arrange
        var mockPool = new Mock<IDataverseConnectionPool>();
        var executor = new QueryExecutor(mockPool.Object);

        // Act & Assert: ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        // (ArgumentNullException derives from ArgumentException)
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => executor.GetTotalRecordCountAsync(null!));
    }

    [Fact]
    public async Task GetTotalRecordCountAsync_ThrowsForEmptyEntityName()
    {
        // Arrange
        var mockPool = new Mock<IDataverseConnectionPool>();
        var executor = new QueryExecutor(mockPool.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => executor.GetTotalRecordCountAsync(""));
    }

    [Fact]
    public async Task GetTotalRecordCountAsync_ThrowsForWhitespaceEntityName()
    {
        // Arrange
        var mockPool = new Mock<IDataverseConnectionPool>();
        var executor = new QueryExecutor(mockPool.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => executor.GetTotalRecordCountAsync("   "));
    }
}

[Trait("Category", "PlanUnit")]
public class QueryExecutorGetMinMaxCreatedOnTests
{
    /// <summary>
    /// Creates a mock pool whose client returns the given EntityCollection
    /// from RetrieveMultipleAsync (used by ExecuteFetchXmlAsync).
    /// The implementation now makes two sorted top-1 calls, so the mock
    /// returns the same collection for both.
    /// </summary>
    private static (Mock<IDataverseConnectionPool> pool, Mock<IPooledClient> client) CreateMockPoolForFetchXml(
        EntityCollection entityCollection)
    {
        var mockClient = new Mock<IPooledClient>();
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entityCollection);

        var mockPool = new Mock<IDataverseConnectionPool>();
        mockPool
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        return (mockPool, mockClient);
    }

    /// <summary>
    /// Builds an EntityCollection that simulates a sorted top-1 response
    /// containing a single record with a createdon attribute.
    /// </summary>
    private static EntityCollection BuildSortedEntityResponse(
        string entityLogicalName,
        DateTime? createdon)
    {
        if (!createdon.HasValue)
        {
            var empty = new EntityCollection();
            empty.EntityName = entityLogicalName;
            return empty;
        }

        var entity = new Entity(entityLogicalName);
        entity["createdon"] = createdon.Value;

        var collection = new EntityCollection(new[] { entity });
        collection.EntityName = entityLogicalName;
        return collection;
    }

    [Fact]
    public async Task GetMinMaxCreatedOnAsync_ReturnsCorrectDates_WhenRecordsExist()
    {
        // Arrange: mock returns a record with createdon for both sorted queries
        var minDate = new DateTime(2020, 1, 15, 8, 30, 0, DateTimeKind.Utc);
        var maxDate = new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var capturedQueries = new System.Collections.Generic.List<string>();
        var mockClient = new Mock<IPooledClient>();
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Returns<QueryBase, CancellationToken>((q, _) =>
            {
                var fetch = (FetchExpression)q;
                capturedQueries.Add(fetch.Query);
                // Ascending sort = min query, descending = max query
                if (fetch.Query.Contains("descending=\"false\"") || fetch.Query.Contains("descending='false'"))
                    return Task.FromResult(BuildSortedEntityResponse("account", minDate));
                return Task.FromResult(BuildSortedEntityResponse("account", maxDate));
            });

        var mockPool = new Mock<IDataverseConnectionPool>();
        mockPool
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var (min, max) = await executor.GetMinMaxCreatedOnAsync("account");

        // Assert
        Assert.Equal(minDate, min);
        Assert.Equal(maxDate, max);
        Assert.Equal(2, capturedQueries.Count); // one ascending (min) + one descending (max) query
    }

    [Fact]
    public async Task GetMinMaxCreatedOnAsync_ReturnsNulls_WhenNoRecords()
    {
        // Arrange: empty entity collection (entity has no records)
        var entityCollection = new EntityCollection();
        entityCollection.EntityName = "account";
        var (mockPool, _) = CreateMockPoolForFetchXml(entityCollection);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var (min, max) = await executor.GetMinMaxCreatedOnAsync("account");

        // Assert
        Assert.Null(min);
        Assert.Null(max);
    }

    [Fact]
    public async Task GetMinMaxCreatedOnAsync_ThrowsForNullEntityName()
    {
        // Arrange
        var mockPool = new Mock<IDataverseConnectionPool>();
        var executor = new QueryExecutor(mockPool.Object);

        // Act & Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => executor.GetMinMaxCreatedOnAsync(null!));
    }

    [Fact]
    public async Task GetMinMaxCreatedOnAsync_ThrowsForEmptyEntityName()
    {
        // Arrange
        var mockPool = new Mock<IDataverseConnectionPool>();
        var executor = new QueryExecutor(mockPool.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => executor.GetMinMaxCreatedOnAsync(""));
    }

    [Fact]
    public async Task GetMinMaxCreatedOnAsync_ThrowsForWhitespaceEntityName()
    {
        // Arrange
        var mockPool = new Mock<IDataverseConnectionPool>();
        var executor = new QueryExecutor(mockPool.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => executor.GetMinMaxCreatedOnAsync("   "));
    }

    [Fact]
    public async Task GetMinMaxCreatedOnAsync_ExecutesSortedTopOneQueries()
    {
        // Arrange
        var date = new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var entityCollection = BuildSortedEntityResponse("contact", date);

        var capturedQueries = new System.Collections.Generic.List<string>();
        var mockClient = new Mock<IPooledClient>();
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) =>
            {
                if (q is FetchExpression fe) capturedQueries.Add(fe.Query);
            })
            .ReturnsAsync(entityCollection);

        var mockPool = new Mock<IDataverseConnectionPool>();
        mockPool
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        var executor = new QueryExecutor(mockPool.Object);

        // Act
        await executor.GetMinMaxCreatedOnAsync("contact");

        // Assert: two sorted top-1 queries, no aggregate
        Assert.Equal(2, capturedQueries.Count);

        // Both queries should use top="1", sorted by createdon, no aggregate
        foreach (var query in capturedQueries)
        {
            Assert.Contains("top=\"1\"", query);
            Assert.Contains("name=\"contact\"", query);
            Assert.Contains("name=\"createdon\"", query);
            Assert.DoesNotContain("aggregate", query);
        }

        // One ascending, one descending
        Assert.Contains(capturedQueries, q => q.Contains("descending=\"false\""));
        Assert.Contains(capturedQueries, q => q.Contains("descending=\"true\""));
    }

    [Fact]
    public async Task GetMinMaxCreatedOnAsync_DisposesPooledClients()
    {
        // Arrange
        var entityCollection = BuildSortedEntityResponse(
            "account",
            new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (mockPool, mockClient) = CreateMockPoolForFetchXml(entityCollection);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        await executor.GetMinMaxCreatedOnAsync("account");

        // Assert: client was disposed twice (once per sorted query via ExecuteFetchXmlAsync)
        mockClient.Verify(c => c.DisposeAsync(), Times.Exactly(2));
    }
}

// ═══════════════════════════════════════════════════════════════
//  AC-06 / AC-07: Bypass hints set OrganizationRequest parameters
// ═══════════════════════════════════════════════════════════════

[Trait("Category", "PlanUnit")]
public class QueryExecutorBypassHintTests
{
    private const string ValidFetchXml =
        "<fetch><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>";

    /// <summary>
    /// Builds a mock pool whose client captures ExecuteAsync request parameters
    /// and returns a valid (empty) RetrieveMultipleResponse.
    /// </summary>
    private static (Mock<IDataverseConnectionPool> pool, Mock<IPooledClient> client)
        CreateMockPoolCapturingExecuteAsync(
            out List<OrganizationRequest> capturedRequests)
    {
        var requests = new List<OrganizationRequest>();
        capturedRequests = requests;

        var entityCollection = new EntityCollection();
        entityCollection.EntityName = "account";

        var response = new RetrieveMultipleResponse();
        response.Results["EntityCollection"] = entityCollection;

        var mockClient = new Mock<IPooledClient>();
        mockClient
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) => requests.Add(req))
            .ReturnsAsync(response);

        // Also mock RetrieveMultipleAsync for the non-options path (should not be called here)
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entityCollection);

        var mockPool = new Mock<IDataverseConnectionPool>();
        mockPool
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        return (mockPool, mockClient);
    }

    // ── AC-06: BYPASS_PLUGINS sets BypassCustomPluginExecution header ─────

    [Fact]
    public async Task ExecuteFetchXmlAsync_BypassPlugins_SetsParameterOnRequest()
    {
        // Arrange
        var (mockPool, _) = CreateMockPoolCapturingExecuteAsync(out var capturedRequests);
        var executor = new QueryExecutor(mockPool.Object);
        var options = new QueryExecutionOptions { BypassPlugins = true };

        // Act
        await executor.ExecuteFetchXmlAsync(
            ValidFetchXml,
            pageNumber: null,
            pagingCookie: null,
            includeCount: false,
            executionOptions: options);

        // Assert: ExecuteAsync was called with the bypass parameter set
        Assert.Single(capturedRequests);
        var req = capturedRequests[0];
        Assert.True(req.Parameters.ContainsKey("BypassCustomPluginExecution"),
            "BypassCustomPluginExecution key must be present in the request parameters");
        Assert.Equal(true, req.Parameters["BypassCustomPluginExecution"]);
    }

    [Fact]
    public async Task ExecuteFetchXmlAsync_BypassPlugins_DoesNotSetFlowsParameter()
    {
        // Arrange: only BYPASS_PLUGINS set — flows parameter must NOT be added
        var (mockPool, _) = CreateMockPoolCapturingExecuteAsync(out var capturedRequests);
        var executor = new QueryExecutor(mockPool.Object);
        var options = new QueryExecutionOptions { BypassPlugins = true, BypassFlows = false };

        // Act
        await executor.ExecuteFetchXmlAsync(
            ValidFetchXml, null, null, false, options);

        // Assert
        Assert.Single(capturedRequests);
        Assert.False(capturedRequests[0].Parameters.ContainsKey("SuppressCallbackRegistrationExpanderJob"),
            "SuppressCallbackRegistrationExpanderJob must not be set when BypassFlows is false");
    }

    // ── AC-07: BYPASS_FLOWS sets SuppressCallbackRegistrationExpanderJob ─

    [Fact]
    public async Task ExecuteFetchXmlAsync_BypassFlows_SetsParameterOnRequest()
    {
        // Arrange
        var (mockPool, _) = CreateMockPoolCapturingExecuteAsync(out var capturedRequests);
        var executor = new QueryExecutor(mockPool.Object);
        var options = new QueryExecutionOptions { BypassFlows = true };

        // Act
        await executor.ExecuteFetchXmlAsync(
            ValidFetchXml, null, null, false, options);

        // Assert
        Assert.Single(capturedRequests);
        var req = capturedRequests[0];
        Assert.True(req.Parameters.ContainsKey("SuppressCallbackRegistrationExpanderJob"),
            "SuppressCallbackRegistrationExpanderJob key must be present in the request parameters");
        Assert.Equal(true, req.Parameters["SuppressCallbackRegistrationExpanderJob"]);
    }

    [Fact]
    public async Task ExecuteFetchXmlAsync_BypassFlows_DoesNotSetPluginsParameter()
    {
        // Arrange: only BYPASS_FLOWS set — plugins parameter must NOT be added
        var (mockPool, _) = CreateMockPoolCapturingExecuteAsync(out var capturedRequests);
        var executor = new QueryExecutor(mockPool.Object);
        var options = new QueryExecutionOptions { BypassPlugins = false, BypassFlows = true };

        // Act
        await executor.ExecuteFetchXmlAsync(
            ValidFetchXml, null, null, false, options);

        // Assert
        Assert.Single(capturedRequests);
        Assert.False(capturedRequests[0].Parameters.ContainsKey("BypassCustomPluginExecution"),
            "BypassCustomPluginExecution must not be set when BypassPlugins is false");
    }

    [Fact]
    public async Task ExecuteFetchXmlAsync_BothBypassFlags_SetsBothParameters()
    {
        // Arrange: both BYPASS_PLUGINS and BYPASS_FLOWS active simultaneously
        var (mockPool, _) = CreateMockPoolCapturingExecuteAsync(out var capturedRequests);
        var executor = new QueryExecutor(mockPool.Object);
        var options = new QueryExecutionOptions { BypassPlugins = true, BypassFlows = true };

        // Act
        await executor.ExecuteFetchXmlAsync(
            ValidFetchXml, null, null, false, options);

        // Assert: both parameters are set
        Assert.Single(capturedRequests);
        var req = capturedRequests[0];
        Assert.Equal(true, req.Parameters["BypassCustomPluginExecution"]);
        Assert.Equal(true, req.Parameters["SuppressCallbackRegistrationExpanderJob"]);
    }

    [Fact]
    public async Task ExecuteFetchXmlAsync_NullOptions_UsesStandardPath()
    {
        // Arrange: when executionOptions is null, falls through to standard RetrieveMultiple path
        var entityCollection = new EntityCollection();
        entityCollection.EntityName = "account";

        var mockClient = new Mock<IPooledClient>();
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entityCollection);

        var mockPool = new Mock<IDataverseConnectionPool>();
        mockPool
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        var executor = new QueryExecutor(mockPool.Object);

        // Act: null options — standard path
        await executor.ExecuteFetchXmlAsync(
            ValidFetchXml, null, null, false, executionOptions: null);

        // Assert: standard RetrieveMultipleAsync was used (no ExecuteAsync for bypass)
        mockClient.Verify(
            c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mockClient.Verify(
            c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

// ═══════════════════════════════════════════════════════════════
//  C2: ExecuteFetchXmlAllPagesAsync respects maxRecords cap
// ═══════════════════════════════════════════════════════════════

[Trait("Category", "PlanUnit")]
public class QueryExecutorAllPagesTests
{
    /// <summary>
    /// Builds an EntityCollection containing <paramref name="count"/> synthetic records
    /// with the given MoreRecords/PagingCookie state.
    /// </summary>
    private static EntityCollection BuildPage(string entity, int count, bool moreRecords, string? pagingCookie)
    {
        var entities = new List<Entity>(count);
        for (var i = 0; i < count; i++)
        {
            var e = new Entity(entity, Guid.NewGuid());
            e["name"] = $"row-{i}";
            entities.Add(e);
        }
        var coll = new EntityCollection(entities)
        {
            EntityName = entity,
            MoreRecords = moreRecords,
            PagingCookie = pagingCookie ?? string.Empty
        };
        return coll;
    }

    /// <summary>
    /// Sets up a mock pool whose client returns the supplied sequence of
    /// EntityCollections on successive RetrieveMultipleAsync calls.
    /// </summary>
    private static (Mock<IDataverseConnectionPool> pool, Mock<IPooledClient> client, Action<int> getCallCount)
        CreateMockPoolWithPages(params EntityCollection[] pages)
    {
        var callCount = 0;
        var mockClient = new Mock<IPooledClient>();
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Returns<QueryBase, CancellationToken>((_, _) =>
            {
                var page = pages[Math.Min(callCount, pages.Length - 1)];
                callCount++;
                return Task.FromResult(page);
            });

        var mockPool = new Mock<IDataverseConnectionPool>();
        mockPool
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        Action<int> verifier = expected => Assert.Equal(expected, callCount);
        return (mockPool, mockClient, verifier);
    }

    private const string ValidFetchXml =
        "<fetch><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>";

    [Fact]
    public async Task ExecuteFetchXmlAllPagesAsync_TrimsResults_WhenFirstPageExceedsMaxRecords()
    {
        // Arrange: server returns 5000 rows in one page, but caller asked for 100.
        // Prior bug: whole 5000-row page was returned to caller; new behavior trims to 100.
        var page1 = BuildPage("account", count: 5000, moreRecords: true, pagingCookie: "cookie-1");
        var (mockPool, _, verifyCalls) = CreateMockPoolWithPages(page1);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var result = await executor.ExecuteFetchXmlAllPagesAsync(ValidFetchXml, maxRecords: 100);

        // Assert
        Assert.Equal(100, result.Records.Count);
        Assert.Equal(100, result.Count);
        verifyCalls(1); // No second page fetched — we already had enough
    }

    [Fact]
    public async Task ExecuteFetchXmlAllPagesAsync_DoesNotFetchNextPage_WhenCapReached()
    {
        // Arrange: first page has 5000, MoreRecords=true — but maxRecords=5000 reached, so stop.
        var page1 = BuildPage("account", count: 5000, moreRecords: true, pagingCookie: "cookie-1");
        var page2 = BuildPage("account", count: 5000, moreRecords: false, pagingCookie: null);
        var (mockPool, _, verifyCalls) = CreateMockPoolWithPages(page1, page2);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var result = await executor.ExecuteFetchXmlAllPagesAsync(ValidFetchXml, maxRecords: 5000);

        // Assert
        Assert.Equal(5000, result.Records.Count);
        verifyCalls(1); // Second page must NOT have been fetched
    }

    [Fact]
    public async Task ExecuteFetchXmlAllPagesAsync_ContinuesPaging_WhenUnderCap()
    {
        // Arrange: first page returns 5000 with MoreRecords=true, second page returns 500 terminal.
        // Caller asks for 5500 rows → both pages fetched, final count == 5500 (not trimmed).
        var page1 = BuildPage("account", count: 5000, moreRecords: true, pagingCookie: "cookie-1");
        var page2 = BuildPage("account", count: 500, moreRecords: false, pagingCookie: null);
        var (mockPool, _, verifyCalls) = CreateMockPoolWithPages(page1, page2);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var result = await executor.ExecuteFetchXmlAllPagesAsync(ValidFetchXml, maxRecords: 5500);

        // Assert
        Assert.Equal(5500, result.Records.Count);
        verifyCalls(2);
    }

    [Fact]
    public async Task ExecuteFetchXmlAllPagesAsync_TrimsResults_WhenSecondPageOvershoots()
    {
        // Arrange: caller asks for 5500, first page returns 5000, second returns 5000.
        // Combined total 10000 is trimmed to 5500.
        var page1 = BuildPage("account", count: 5000, moreRecords: true, pagingCookie: "cookie-1");
        var page2 = BuildPage("account", count: 5000, moreRecords: true, pagingCookie: "cookie-2");
        var (mockPool, _, verifyCalls) = CreateMockPoolWithPages(page1, page2);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var result = await executor.ExecuteFetchXmlAllPagesAsync(ValidFetchXml, maxRecords: 5500);

        // Assert
        Assert.Equal(5500, result.Records.Count);
        // Two pages fetched (first did not satisfy cap on its own), but no third page fetched
        verifyCalls(2);
    }

    [Fact]
    public async Task ExecuteFetchXmlAllPagesAsync_TerminatesOnMoreRecordsFalse_WhenUnderCap()
    {
        // Arrange: single terminal page with only 50 rows; maxRecords far above.
        var page1 = BuildPage("account", count: 50, moreRecords: false, pagingCookie: null);
        var (mockPool, _, verifyCalls) = CreateMockPoolWithPages(page1);
        var executor = new QueryExecutor(mockPool.Object);

        // Act
        var result = await executor.ExecuteFetchXmlAllPagesAsync(ValidFetchXml, maxRecords: 10_000);

        // Assert
        Assert.Equal(50, result.Records.Count);
        verifyCalls(1);
    }
}

// ═══════════════════════════════════════════════════════════════
//  C4: GetMinMaxCreatedOnAsync validates entity logical name
// ═══════════════════════════════════════════════════════════════

[Trait("Category", "PlanUnit")]
public class QueryExecutorEntityNameValidationTests
{
    private static (Mock<IDataverseConnectionPool> pool, Mock<IPooledClient> client) CreateSafeMockPool()
    {
        var empty = new EntityCollection { EntityName = "account" };
        var mockClient = new Mock<IPooledClient>();
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(empty);
        var mockPool = new Mock<IDataverseConnectionPool>();
        mockPool
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);
        return (mockPool, mockClient);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("contact")]
    [InlineData("new_myentity")]
    [InlineData("prefix_entity_01")]
    public async Task GetMinMaxCreatedOnAsync_AcceptsValidLogicalNames(string entityLogicalName)
    {
        // Arrange
        var (mockPool, _) = CreateSafeMockPool();
        var executor = new QueryExecutor(mockPool.Object);

        // Act & Assert: does not throw for well-formed lowercase logical names
        var (min, max) = await executor.GetMinMaxCreatedOnAsync(entityLogicalName);
        Assert.Null(min);
        Assert.Null(max);
    }

    [Theory]
    [InlineData("Account")]               // uppercase letter
    [InlineData("1account")]              // starts with digit
    [InlineData("account'; drop table")] // injection attempt with quote + spaces
    [InlineData("acc<evil/>")]            // angle brackets / XML-ish injection
    [InlineData("acc&amp;")]              // ampersand
    [InlineData("acc\"quoted\"")]         // embedded quotes
    [InlineData("acc-dash")]              // dash not allowed
    [InlineData("acc.dot")]               // dot not allowed
    public async Task GetMinMaxCreatedOnAsync_RejectsInvalidOrUnsafeLogicalNames(string entityLogicalName)
    {
        // Arrange
        var (mockPool, mockClient) = CreateSafeMockPool();
        var executor = new QueryExecutor(mockPool.Object);

        // Act & Assert: regex backstop throws before any query is issued
        await Assert.ThrowsAsync<ArgumentException>(
            () => executor.GetMinMaxCreatedOnAsync(entityLogicalName));

        mockClient.Verify(
            c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
