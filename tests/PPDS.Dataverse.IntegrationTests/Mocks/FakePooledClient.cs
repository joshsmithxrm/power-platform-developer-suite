using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.IntegrationTests.Mocks;

/// <summary>
/// Fake IPooledClient implementation that wraps a FakeXrmEasy IOrganizationService.
/// Used for testing BulkOperationExecutor with mocked Dataverse operations.
/// </summary>
public class FakePooledClient : IPooledClient
{
    private readonly IOrganizationService _service;
    private readonly string _connectionName;
    private readonly Action? _onDispose;
    private bool _isDisposed;

    public FakePooledClient(
        IOrganizationService service,
        string connectionName = "fake-connection",
        Action? onDispose = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _connectionName = connectionName;
        _onDispose = onDispose;
        ConnectionId = Guid.NewGuid();
        CreatedAt = DateTime.UtcNow;
        LastUsedAt = DateTime.UtcNow;
    }

    // IPooledClient implementation
    public Guid ConnectionId { get; }
    public string ConnectionName => _connectionName;
    public string DisplayName => $"{_connectionName}@FakeOrg";
    public DateTime CreatedAt { get; }
    public DateTime LastUsedAt { get; private set; }
    public bool IsInvalid { get; private set; }
    public string? InvalidReason { get; private set; }

    public void MarkInvalid(string reason)
    {
        IsInvalid = true;
        InvalidReason = reason;
    }

    // IDataverseClient implementation
    public bool IsReady => !_isDisposed;
    public int RecommendedDegreesOfParallelism => 4;
    public Guid? ConnectedOrgId => Guid.NewGuid();
    public string ConnectedOrgFriendlyName => "FakeOrg";
    public string ConnectedOrgUniqueName => "fakeorg";
    public Version? ConnectedOrgVersion => new Version(9, 2, 0, 0);
    public string? LastError => null;
    public Exception? LastException => null;
    public Guid CallerId { get; set; }
    public Guid? CallerAADObjectId { get; set; }
    public int MaxRetryCount { get; set; } = 3;
    public TimeSpan RetryPauseTime { get; set; } = TimeSpan.FromSeconds(2);

    public IDataverseClient Clone() => new FakePooledClient(_service, _connectionName);

    // IOrganizationService implementation - delegate to FakeXrmEasy
    public Guid Create(Entity entity)
    {
        LastUsedAt = DateTime.UtcNow;
        return _service.Create(entity);
    }

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
    {
        LastUsedAt = DateTime.UtcNow;
        return _service.Retrieve(entityName, id, columnSet);
    }

    public void Update(Entity entity)
    {
        LastUsedAt = DateTime.UtcNow;
        _service.Update(entity);
    }

    public void Delete(string entityName, Guid id)
    {
        LastUsedAt = DateTime.UtcNow;
        _service.Delete(entityName, id);
    }

    public OrganizationResponse Execute(OrganizationRequest request)
    {
        LastUsedAt = DateTime.UtcNow;
        return _service.Execute(request);
    }

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
    {
        LastUsedAt = DateTime.UtcNow;
        _service.Associate(entityName, entityId, relationship, relatedEntities);
    }

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
    {
        LastUsedAt = DateTime.UtcNow;
        _service.Disassociate(entityName, entityId, relationship, relatedEntities);
    }

    public EntityCollection RetrieveMultiple(QueryBase query)
    {
        LastUsedAt = DateTime.UtcNow;
        return _service.RetrieveMultiple(query);
    }

    // IOrganizationServiceAsync2 implementation - wrap sync operations
    public Task<Guid> CreateAsync(Entity entity) => Task.FromResult(Create(entity));
    public Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken) => Task.FromResult(Create(entity));
    public Task<Entity> CreateAndReturnAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var id = Create(entity);
        entity.Id = id;
        return Task.FromResult(entity);
    }
    public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet) => Task.FromResult(Retrieve(entityName, id, columnSet));
    public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet, CancellationToken cancellationToken) => Task.FromResult(Retrieve(entityName, id, columnSet));
    public Task UpdateAsync(Entity entity) { Update(entity); return Task.CompletedTask; }
    public Task UpdateAsync(Entity entity, CancellationToken cancellationToken) { Update(entity); return Task.CompletedTask; }
    public Task DeleteAsync(string entityName, Guid id) { Delete(entityName, id); return Task.CompletedTask; }
    public Task DeleteAsync(string entityName, Guid id, CancellationToken cancellationToken) { Delete(entityName, id); return Task.CompletedTask; }
    public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request) => Task.FromResult(Execute(request));
    public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken) => Task.FromResult(Execute(request));
    public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { Associate(entityName, entityId, relationship, relatedEntities); return Task.CompletedTask; }
    public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken) { Associate(entityName, entityId, relationship, relatedEntities); return Task.CompletedTask; }
    public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { Disassociate(entityName, entityId, relationship, relatedEntities); return Task.CompletedTask; }
    public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken) { Disassociate(entityName, entityId, relationship, relatedEntities); return Task.CompletedTask; }
    public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query) => Task.FromResult(RetrieveMultiple(query));
    public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query, CancellationToken cancellationToken) => Task.FromResult(RetrieveMultiple(query));

    // IDisposable/IAsyncDisposable
    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _onDispose?.Invoke();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
