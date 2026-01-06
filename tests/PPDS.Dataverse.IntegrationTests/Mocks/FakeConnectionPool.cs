using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.IntegrationTests.Mocks;

/// <summary>
/// Fake IDataverseConnectionPool implementation for testing.
/// Returns FakePooledClient instances wrapping the provided IOrganizationService.
/// </summary>
public class FakeConnectionPool : IDataverseConnectionPool
{
    private readonly IOrganizationService _service;
    private readonly string _connectionName;
    private readonly int _recommendedParallelism;
    private int _activeConnections;
    private BatchParallelismCoordinator? _batchCoordinator;
    private readonly object _batchCoordinatorLock = new();

    public FakeConnectionPool(
        IOrganizationService service,
        string connectionName = "fake-connection",
        int recommendedParallelism = 4)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _connectionName = connectionName;
        _recommendedParallelism = recommendedParallelism;
    }

    public bool IsEnabled => true;
    public int SourceCount => 1;

    public BatchParallelismCoordinator BatchCoordinator
    {
        get
        {
            if (_batchCoordinator != null) return _batchCoordinator;
            lock (_batchCoordinatorLock)
            {
                return _batchCoordinator ??= new BatchParallelismCoordinator(this);
            }
        }
    }

    public PoolStatistics Statistics => new()
    {
        TotalConnections = 10,
        ActiveConnections = _activeConnections,
        IdleConnections = 10 - _activeConnections,
        ThrottledConnections = 0,
        RequestsServed = 0,
        ThrottleEvents = 0,
        InvalidConnections = 0,
        AuthFailures = 0,
        ConnectionFailures = 0
    };

    public Task<IPooledClient> GetClientAsync(
        DataverseClientOptions? options = null,
        string? excludeConnectionName = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activeConnections);
        var client = new FakePooledClient(
            _service,
            _connectionName,
            onDispose: () => Interlocked.Decrement(ref _activeConnections));
        return Task.FromResult<IPooledClient>(client);
    }

    public IPooledClient GetClient(DataverseClientOptions? options = null)
    {
        Interlocked.Increment(ref _activeConnections);
        return new FakePooledClient(
            _service,
            _connectionName,
            onDispose: () => Interlocked.Decrement(ref _activeConnections));
    }

    public async Task<IPooledClient?> TryGetClientWithCapacityAsync(CancellationToken cancellationToken = default)
    {
        return await GetClientAsync(cancellationToken: cancellationToken);
    }

    public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_service.Execute(request));
    }

    public int GetTotalRecommendedParallelism() => _recommendedParallelism;

    public int GetLiveSourceDop(string sourceName) => _recommendedParallelism;

    public int GetActiveConnectionCount(string sourceName) => _activeConnections;

    public void RecordAuthFailure() { }
    public void RecordConnectionFailure() { }
    public void InvalidateSeed(string connectionName) { }

    public void Dispose()
    {
        _batchCoordinator?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        _batchCoordinator?.Dispose();
        return ValueTask.CompletedTask;
    }
}
