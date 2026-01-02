using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.IntegrationTests.Mocks;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;
using PPDS.Dataverse.Resilience;

namespace PPDS.Dataverse.IntegrationTests.BulkOperations;

/// <summary>
/// Base class for BulkOperationExecutor tests using FakeXrmEasy.
/// Provides pre-configured BulkOperationExecutor with mocked dependencies.
/// </summary>
public abstract class BulkOperationExecutorTestsBase : FakeXrmEasyTestsBase
{
    /// <summary>
    /// The BulkOperationExecutor under test.
    /// </summary>
    protected IBulkOperationExecutor Executor { get; }

    /// <summary>
    /// The fake connection pool for controlling connection behavior.
    /// </summary>
    protected FakeConnectionPool ConnectionPool { get; }

    /// <summary>
    /// The fake throttle tracker for controlling throttle behavior.
    /// </summary>
    protected FakeThrottleTracker ThrottleTracker { get; }

    /// <summary>
    /// The Dataverse options for configuring bulk operation behavior.
    /// </summary>
    protected DataverseOptions Options { get; }

    /// <summary>
    /// Initializes a new instance with mocked BulkOperationExecutor dependencies.
    /// </summary>
    protected BulkOperationExecutorTestsBase()
    {
        ConnectionPool = new FakeConnectionPool(Service);
        ThrottleTracker = new FakeThrottleTracker();
        Options = new DataverseOptions
        {
            BulkOperations = new BulkOperationOptions
            {
                BatchSize = 100,
                MaxParallelBatches = 1 // Sequential for deterministic testing
            },
            Pool = new ConnectionPoolOptions
            {
                MaxConnectionRetries = 2
            }
        };

        var optionsWrapper = Microsoft.Extensions.Options.Options.Create(Options);
        var logger = NullLogger<BulkOperationExecutor>.Instance;

        Executor = new BulkOperationExecutor(
            ConnectionPool,
            ThrottleTracker,
            optionsWrapper,
            logger);
    }

    /// <summary>
    /// Creates a collection of test entities with the specified count.
    /// </summary>
    protected static List<Entity> CreateTestEntities(string entityName, int count)
    {
        var entities = new List<Entity>();
        for (int i = 0; i < count; i++)
        {
            entities.Add(new Entity(entityName)
            {
                ["name"] = $"Test Entity {i}"
            });
        }
        return entities;
    }

    /// <summary>
    /// Creates a collection of test entities with IDs for update/upsert operations.
    /// </summary>
    protected static List<Entity> CreateTestEntitiesWithIds(string entityName, IEnumerable<Guid> ids)
    {
        return ids.Select((id, i) => new Entity(entityName, id)
        {
            ["name"] = $"Updated Entity {i}"
        }).ToList();
    }

    /// <summary>
    /// Creates a progress tracker for testing progress reporting.
    /// </summary>
    protected static TestProgressReporter CreateProgressReporter()
    {
        return new TestProgressReporter();
    }

    /// <summary>
    /// Helper class for capturing progress reports during tests.
    /// </summary>
    protected class TestProgressReporter : IProgress<ProgressSnapshot>
    {
        private readonly List<ProgressSnapshot> _reports = new();

        public IReadOnlyList<ProgressSnapshot> Reports => _reports;

        public ProgressSnapshot? LastReport => _reports.Count > 0 ? _reports[^1] : null;

        public void Report(ProgressSnapshot value)
        {
            _reports.Add(value);
        }
    }
}
