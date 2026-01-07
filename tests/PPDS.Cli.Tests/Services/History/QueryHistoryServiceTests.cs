using Microsoft.Extensions.Logging;
using Moq;
using PPDS.Cli.Services.History;
using Xunit;

namespace PPDS.Cli.Tests.Services.History;

/// <summary>
/// Unit tests for <see cref="QueryHistoryService"/>.
/// </summary>
public class QueryHistoryServiceTests : IDisposable
{
    private readonly Mock<ILogger<QueryHistoryService>> _mockLogger;
    private readonly string _tempBasePath;
    private readonly QueryHistoryService _service;
    private readonly string _testId;

    /// <summary>
    /// Generates a unique test environment URL to isolate test runs.
    /// </summary>
    private string TestEnvironmentUrl => $"https://test-{_testId}.crm.dynamics.com";

    public QueryHistoryServiceTests()
    {
        _testId = Guid.NewGuid().ToString("N")[..8];
        _mockLogger = new Mock<ILogger<QueryHistoryService>>();
        _tempBasePath = Path.Combine(Path.GetTempPath(), $"ppds-test-{_testId}");
        Directory.CreateDirectory(_tempBasePath);

        // Use a custom service that allows us to override the base path
        _service = new QueryHistoryService(_mockLogger.Object);
    }

    public void Dispose()
    {
        // Clean up temp directory
        if (Directory.Exists(_tempBasePath))
        {
            try
            {
                Directory.Delete(_tempBasePath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new QueryHistoryService(null!));
    }

    #endregion

    #region AddQueryAsync Tests

    [Fact]
    public async Task AddQueryAsync_WithValidQuery_ReturnsEntry()
    {
        var result = await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT name FROM account");

        Assert.NotNull(result);
        Assert.Equal("SELECT name FROM account", result.Sql);
        Assert.NotNull(result.Id);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task AddQueryAsync_WithRowCount_IncludesRowCount()
    {
        var result = await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT name FROM account", rowCount: 42);

        Assert.Equal(42, result.RowCount);
    }

    [Fact]
    public async Task AddQueryAsync_WithExecutionTime_IncludesExecutionTime()
    {
        var result = await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT name FROM account", executionTimeMs: 150);

        Assert.Equal(150, result.ExecutionTimeMs);
    }

    [Fact]
    public async Task AddQueryAsync_TrimsWhitespace()
    {
        var result = await _service.AddQueryAsync(TestEnvironmentUrl, "  SELECT name FROM account  ");

        Assert.Equal("SELECT name FROM account", result.Sql);
    }

    [Fact]
    public async Task AddQueryAsync_WithEmptySql_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.AddQueryAsync(TestEnvironmentUrl, ""));
    }

    [Fact]
    public async Task AddQueryAsync_WithWhitespaceSql_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.AddQueryAsync(TestEnvironmentUrl, "   "));
    }

    [Fact]
    public async Task AddQueryAsync_DuplicateQuery_MovesToFront()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT name FROM account");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT id FROM contact");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT name FROM account"); // Duplicate

        var history = await _service.GetHistoryAsync(TestEnvironmentUrl);

        Assert.Equal(2, history.Count);
        Assert.Equal("SELECT name FROM account", history[0].Sql);
    }

    [Fact]
    public async Task AddQueryAsync_DuplicateWithDifferentWhitespace_TreatedAsDuplicate()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT name FROM account");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT   name   FROM   account"); // Same but different whitespace

        var history = await _service.GetHistoryAsync(TestEnvironmentUrl);

        Assert.Single(history);
    }

    [Fact]
    public async Task AddQueryAsync_DuplicateWithDifferentCase_TreatedAsDuplicate()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT name FROM account");
        await _service.AddQueryAsync(TestEnvironmentUrl, "select NAME from ACCOUNT"); // Same but different case

        var history = await _service.GetHistoryAsync(TestEnvironmentUrl);

        Assert.Single(history);
    }

    #endregion

    #region GetHistoryAsync Tests

    [Fact]
    public async Task GetHistoryAsync_WithNoHistory_ReturnsEmptyList()
    {
        var result = await _service.GetHistoryAsync("https://empty.crm.dynamics.com");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsEntriesInReverseOrder()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 1");
        await Task.Delay(10); // Small delay to ensure different timestamps
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 2");
        await Task.Delay(10);
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 3");

        var history = await _service.GetHistoryAsync(TestEnvironmentUrl);

        Assert.Equal(3, history.Count);
        Assert.Equal("SELECT 3", history[0].Sql);
        Assert.Equal("SELECT 2", history[1].Sql);
        Assert.Equal("SELECT 1", history[2].Sql);
    }

    [Fact]
    public async Task GetHistoryAsync_WithCountLimit_ReturnsLimitedEntries()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 1");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 2");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 3");

        var history = await _service.GetHistoryAsync(TestEnvironmentUrl, count: 2);

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public async Task GetHistoryAsync_DifferentEnvironments_SeparateHistory()
    {
        var env1 = $"https://env1-{_testId}.crm.dynamics.com";
        var env2 = $"https://env2-{_testId}.crm.dynamics.com";

        await _service.AddQueryAsync(env1, "SELECT 1");
        await _service.AddQueryAsync(env2, "SELECT 2");

        var history1 = await _service.GetHistoryAsync(env1);
        var history2 = await _service.GetHistoryAsync(env2);

        Assert.Single(history1);
        Assert.Single(history2);
        Assert.Equal("SELECT 1", history1[0].Sql);
        Assert.Equal("SELECT 2", history2[0].Sql);
    }

    #endregion

    #region SearchHistoryAsync Tests

    [Fact]
    public async Task SearchHistoryAsync_WithMatchingPattern_ReturnsMatches()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT name FROM account");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT id FROM contact");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT accountid FROM account");

        var results = await _service.SearchHistoryAsync(TestEnvironmentUrl, "account");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Contains("account", r.Sql, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchHistoryAsync_CaseInsensitive()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT name FROM Account");

        var results = await _service.SearchHistoryAsync(TestEnvironmentUrl, "ACCOUNT");

        Assert.Single(results);
    }

    [Fact]
    public async Task SearchHistoryAsync_WithNoMatches_ReturnsEmpty()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT name FROM account");

        var results = await _service.SearchHistoryAsync(TestEnvironmentUrl, "xyz123");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchHistoryAsync_WithEmptyPattern_ReturnsAll()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 1");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 2");

        var results = await _service.SearchHistoryAsync(TestEnvironmentUrl, "");

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchHistoryAsync_WithNullPattern_ReturnsAll()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 1");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 2");

        var results = await _service.SearchHistoryAsync(TestEnvironmentUrl, null!);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchHistoryAsync_WithCountLimit_ReturnsLimitedResults()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 1 FROM account");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 2 FROM account");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 3 FROM account");

        var results = await _service.SearchHistoryAsync(TestEnvironmentUrl, "account", count: 2);

        Assert.Equal(2, results.Count);
    }

    #endregion

    #region DeleteEntryAsync Tests

    [Fact]
    public async Task DeleteEntryAsync_WithExistingEntry_ReturnsTrue()
    {
        var entry = await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 1");

        var result = await _service.DeleteEntryAsync(TestEnvironmentUrl, entry.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task DeleteEntryAsync_WithExistingEntry_RemovesFromHistory()
    {
        var entry = await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 1");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 2");

        await _service.DeleteEntryAsync(TestEnvironmentUrl, entry.Id);
        var history = await _service.GetHistoryAsync(TestEnvironmentUrl);

        Assert.Single(history);
        Assert.Equal("SELECT 2", history[0].Sql);
    }

    [Fact]
    public async Task DeleteEntryAsync_WithNonexistentEntry_ReturnsFalse()
    {
        var result = await _service.DeleteEntryAsync(TestEnvironmentUrl, "nonexistent-id");

        Assert.False(result);
    }

    #endregion

    #region ClearHistoryAsync Tests

    [Fact]
    public async Task ClearHistoryAsync_ClearsAllEntries()
    {
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 1");
        await _service.AddQueryAsync(TestEnvironmentUrl, "SELECT 2");

        await _service.ClearHistoryAsync(TestEnvironmentUrl);
        var history = await _service.GetHistoryAsync(TestEnvironmentUrl);

        Assert.Empty(history);
    }

    [Fact]
    public async Task ClearHistoryAsync_WithNoHistory_DoesNotThrow()
    {
        // Should not throw
        await _service.ClearHistoryAsync("https://empty.crm.dynamics.com");
    }

    [Fact]
    public async Task ClearHistoryAsync_OnlyAffectsSpecifiedEnvironment()
    {
        var env1 = $"https://env1-{_testId}.crm.dynamics.com";
        var env2 = $"https://env2-{_testId}.crm.dynamics.com";

        await _service.AddQueryAsync(env1, "SELECT 1");
        await _service.AddQueryAsync(env2, "SELECT 2");

        await _service.ClearHistoryAsync(env1);

        var history1 = await _service.GetHistoryAsync(env1);
        var history2 = await _service.GetHistoryAsync(env2);

        Assert.Empty(history1);
        Assert.Single(history2);
    }

    #endregion
}
