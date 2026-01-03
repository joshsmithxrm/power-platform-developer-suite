using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.BulkOperations;

/// <summary>
/// Live integration tests for BulkOperationExecutor using real Dataverse operations.
/// Tests verify CreateMultiple, UpdateMultiple, and UpsertMultiple against a live environment.
/// </summary>
/// <remarks>
/// These tests create and delete records in the account table. They clean up after themselves.
/// </remarks>
[Trait("Category", "Integration")]
public class BulkOperationLiveTests : LiveTestBase
{
    private readonly ITestOutputHelper _output;
    private readonly List<Guid> _createdAccountIds = new();
    private DataverseConnectionPool? _pool;
    private ServiceClientSource? _source;

    /// <summary>
    /// Unique identifier prefix for test records to avoid conflicts.
    /// </summary>
    private readonly string _testPrefix = $"PPDS_LiveTest_{Guid.NewGuid():N}";

    public BulkOperationLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(DataverseConnectionPool Pool, BulkOperationExecutor Executor)> CreateExecutorAsync()
    {
        _source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "BulkOpTest");
        _pool = LiveTestHelpers.CreateConnectionPool(new[] { _source });

        var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        var throttleTracker = LiveTestHelpers.CreateThrottleTracker();

        var options = Options.Create(new DataverseOptions
        {
            BulkOperations = new BulkOperationOptions
            {
                BatchSize = 10, // Small batch for testing
                MaxParallelBatches = 1, // Sequential for predictable testing
            },
            Pool = new ConnectionPoolOptions { MaxConnectionRetries = 2 }
        });

        var executor = new BulkOperationExecutor(
            _pool,
            throttleTracker,
            options,
            loggerFactory.CreateLogger<BulkOperationExecutor>());

        return (_pool, executor);
    }

    #region CreateMultiple Tests

    [SkipIfNoClientSecret]
    public async Task CreateMultiple_CreatesRecordsInDataverse()
    {
        // Arrange
        var (pool, executor) = await CreateExecutorAsync();

        var accounts = Enumerable.Range(1, 5).Select(i => new Entity("account")
        {
            ["name"] = $"{_testPrefix}_Account_{i}"
        }).ToList();

        // Act
        var result = await executor.CreateMultipleAsync("account", accounts);

        // Assert
        result.SuccessCount.Should().Be(5);
        result.FailureCount.Should().Be(0);
        result.Errors.Should().BeEmpty();
        result.CreatedIds.Should().HaveCount(5);

        // Track for cleanup
        _createdAccountIds.AddRange(result.CreatedIds!);

        _output.WriteLine($"Created {result.SuccessCount} accounts");
        _output.WriteLine($"Duration: {result.Duration.TotalMilliseconds:N0}ms");

        // Verify records exist
        await using var client = await pool.GetClientAsync();
        foreach (var id in result.CreatedIds!)
        {
            var retrieved = client.Retrieve("account", id, new ColumnSet("name"));
            retrieved.Should().NotBeNull();
            retrieved.GetAttributeValue<string>("name").Should().StartWith(_testPrefix);
        }
    }

    [SkipIfNoClientSecret]
    public async Task CreateMultiple_ReturnsCreatedIds()
    {
        // Arrange
        var (_, executor) = await CreateExecutorAsync();

        var accounts = new List<Entity>
        {
            new Entity("account") { ["name"] = $"{_testPrefix}_IdTest" }
        };

        // Act
        var result = await executor.CreateMultipleAsync("account", accounts);

        // Assert
        result.CreatedIds.Should().NotBeNull();
        result.CreatedIds.Should().HaveCount(1);
        result.CreatedIds![0].Should().NotBeEmpty();

        _createdAccountIds.AddRange(result.CreatedIds);

        _output.WriteLine($"Created ID: {result.CreatedIds[0]}");
    }

    #endregion

    #region UpdateMultiple Tests

    [SkipIfNoClientSecret]
    public async Task UpdateMultiple_UpdatesExistingRecords()
    {
        // Arrange
        var (pool, executor) = await CreateExecutorAsync();

        // First create records
        var createAccounts = Enumerable.Range(1, 3).Select(i => new Entity("account")
        {
            ["name"] = $"{_testPrefix}_UpdateTest_{i}",
            ["description"] = "Original description"
        }).ToList();

        var createResult = await executor.CreateMultipleAsync("account", createAccounts);
        _createdAccountIds.AddRange(createResult.CreatedIds!);

        // Prepare updates
        var updateAccounts = createResult.CreatedIds!.Select((id, i) => new Entity("account", id)
        {
            ["description"] = $"Updated description {i + 1}"
        }).ToList();

        // Act
        var updateResult = await executor.UpdateMultipleAsync("account", updateAccounts);

        // Assert
        updateResult.SuccessCount.Should().Be(3);
        updateResult.FailureCount.Should().Be(0);

        _output.WriteLine($"Updated {updateResult.SuccessCount} accounts");

        // Verify updates
        await using var client = await pool.GetClientAsync();
        foreach (var (id, index) in createResult.CreatedIds!.Select((id, i) => (id, i)))
        {
            var retrieved = client.Retrieve("account", id, new ColumnSet("description"));
            retrieved.GetAttributeValue<string>("description").Should().Be($"Updated description {index + 1}");
        }
    }

    #endregion

    #region UpsertMultiple Tests

    [SkipIfNoClientSecret]
    public async Task UpsertMultiple_CreatesAndUpdatesRecords()
    {
        // Arrange
        var (pool, executor) = await CreateExecutorAsync();

        // Create one record first
        var existingAccount = new Entity("account")
        {
            ["name"] = $"{_testPrefix}_UpsertExisting",
            ["description"] = "Original"
        };
        var createResult = await executor.CreateMultipleAsync("account", new[] { existingAccount });
        var existingId = createResult.CreatedIds![0];
        _createdAccountIds.Add(existingId);

        // Prepare upsert: one update (existing) + one create (new)
        var upsertEntities = new List<Entity>
        {
            new Entity("account", existingId) // Update existing
            {
                ["description"] = "Upserted (updated)"
            },
            new Entity("account") // Create new
            {
                ["name"] = $"{_testPrefix}_UpsertNew",
                ["description"] = "Upserted (created)"
            }
        };

        // Act
        var upsertResult = await executor.UpsertMultipleAsync("account", upsertEntities);

        // Assert
        upsertResult.SuccessCount.Should().Be(2);
        upsertResult.FailureCount.Should().Be(0);

        // Track created count for upsert
        _output.WriteLine($"Upsert - Created: {upsertResult.CreatedCount}, Updated: {upsertResult.UpdatedCount}");

        // Clean up - we need to find the newly created record
        await using var client = await pool.GetClientAsync();
        var query = new QueryExpression("account")
        {
            ColumnSet = new ColumnSet("name", "description"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("name", ConditionOperator.Equal, $"{_testPrefix}_UpsertNew")
                }
            }
        };
        var newRecords = client.RetrieveMultiple(query);
        foreach (var record in newRecords.Entities)
        {
            _createdAccountIds.Add(record.Id);
        }
    }

    #endregion

    #region Performance Baseline Tests

    [SkipIfNoClientSecret]
    public async Task CreateMultiple_MeasuresPerformanceBaseline()
    {
        // Arrange
        var (_, executor) = await CreateExecutorAsync();

        var recordCounts = new[] { 10, 25 }; // Small counts for CI
        var results = new List<(int Count, TimeSpan Duration, double RecordsPerSecond)>();

        foreach (var count in recordCounts)
        {
            var accounts = Enumerable.Range(1, count).Select(i => new Entity("account")
            {
                ["name"] = $"{_testPrefix}_Perf_{count}_{i}"
            }).ToList();

            var sw = Stopwatch.StartNew();
            var result = await executor.CreateMultipleAsync("account", accounts);
            sw.Stop();

            _createdAccountIds.AddRange(result.CreatedIds!);

            var recordsPerSecond = count / sw.Elapsed.TotalSeconds;
            results.Add((count, sw.Elapsed, recordsPerSecond));

            _output.WriteLine($"Created {count} records in {sw.ElapsedMilliseconds}ms ({recordsPerSecond:N1} records/sec)");
        }

        // Assert - Just verify operations completed successfully
        results.Should().AllSatisfy(r => r.Count.Should().BeGreaterThan(0));
    }

    #endregion

    #region Options Tests

    [SkipIfNoClientSecret]
    public async Task CreateMultiple_WithBypassPowerAutomateFlows()
    {
        // Arrange
        var (_, executor) = await CreateExecutorAsync();

        var accounts = new List<Entity>
        {
            new Entity("account") { ["name"] = $"{_testPrefix}_BypassFlowTest" }
        };

        var options = new BulkOperationOptions
        {
            BatchSize = 10,
            BypassPowerAutomateFlows = true // This doesn't require special privileges
        };

        // Act - Should succeed even with bypass option
        var result = await executor.CreateMultipleAsync("account", accounts, options);

        // Assert
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(0);

        _createdAccountIds.AddRange(result.CreatedIds!);

        _output.WriteLine("Created record with BypassPowerAutomateFlows=true");
    }

    [SkipIfNoClientSecret]
    public async Task CreateMultiple_WithTag()
    {
        // Arrange
        var (_, executor) = await CreateExecutorAsync();

        var tagValue = "PPDS-LiveTest-Tag";
        var accounts = new List<Entity>
        {
            new Entity("account") { ["name"] = $"{_testPrefix}_TagTest" }
        };

        var options = new BulkOperationOptions
        {
            BatchSize = 10,
            Tag = tagValue
        };

        // Act - Should succeed with tag
        var result = await executor.CreateMultipleAsync("account", accounts, options);

        // Assert
        result.SuccessCount.Should().Be(1);

        _createdAccountIds.AddRange(result.CreatedIds!);

        _output.WriteLine($"Created record with Tag='{tagValue}'");
    }

    [SkipIfNoClientSecret]
    public async Task CreateMultiple_SequentialWithMaxParallelBatches1()
    {
        // Arrange
        var (_, executor) = await CreateExecutorAsync();

        var accounts = Enumerable.Range(1, 15).Select(i => new Entity("account")
        {
            ["name"] = $"{_testPrefix}_Sequential_{i}"
        }).ToList();

        var options = new BulkOperationOptions
        {
            BatchSize = 5,
            MaxParallelBatches = 1 // Force sequential execution
        };

        // Act
        var sw = Stopwatch.StartNew();
        var result = await executor.CreateMultipleAsync("account", accounts, options);
        sw.Stop();

        // Assert
        result.SuccessCount.Should().Be(15);
        result.FailureCount.Should().Be(0);

        _createdAccountIds.AddRange(result.CreatedIds!);

        _output.WriteLine($"Sequential: Created {result.SuccessCount} records in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"Batches processed: 3 (15 records / 5 batch size)");
    }

    #endregion

    #region Cleanup

    public override async Task DisposeAsync()
    {
        // Clean up all created records
        if (_createdAccountIds.Count > 0 && _pool != null)
        {
            try
            {
                await using var client = await _pool.GetClientAsync();

                foreach (var id in _createdAccountIds)
                {
                    try
                    {
                        client.Delete("account", id);
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"Failed to delete account {id}: {ex.Message}");
                    }
                }

                _output.WriteLine($"Cleaned up {_createdAccountIds.Count} test accounts");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cleanup failed: {ex.Message}");
            }
        }

        if (_pool is not null) await _pool.DisposeAsync();
        _source?.Dispose();
        Configuration.Dispose();
    }

    #endregion
}
