using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;

namespace PPDS.Migration.Cli.Infrastructure;

/// <summary>
/// A minimal connection pool implementation for device code flow authentication.
/// Wraps a single ServiceClient created with a token provider function.
/// </summary>
public sealed class DeviceCodeConnectionPool : IDataverseConnectionPool
{
    private readonly ServiceClient _serviceClient;
    private int _activeConnections;
    private bool _disposed;

    /// <summary>
    /// Creates a new device code connection pool.
    /// </summary>
    /// <param name="url">The Dataverse environment URL.</param>
    /// <param name="tokenProvider">The token provider function.</param>
    public DeviceCodeConnectionPool(Uri url, Func<string, Task<string>> tokenProvider)
    {
        _serviceClient = new ServiceClient(url, tokenProvider, useUniqueInstance: true);

        if (!_serviceClient.IsReady)
        {
            var error = _serviceClient.LastError ?? "Unknown error";
            throw new InvalidOperationException($"Failed to establish connection 'Interactive'. Error: {error}");
        }
    }

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public PoolStatistics Statistics => new()
    {
        TotalConnections = 1,
        ActiveConnections = _activeConnections,
        IdleConnections = _activeConnections > 0 ? 0 : 1
    };

    /// <inheritdoc />
    public async Task<IPooledClient> GetClientAsync(
        DataverseClientOptions? options = null,
        string? excludeConnectionName = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await Task.CompletedTask; // For async signature
        return CreatePooledClient(options);
    }

    /// <inheritdoc />
    public IPooledClient GetClient(DataverseClientOptions? options = null)
    {
        ThrowIfDisposed();
        return CreatePooledClient(options);
    }

    private IPooledClient CreatePooledClient(DataverseClientOptions? options)
    {
        // Clone the service client for thread safety
        var clonedClient = _serviceClient.Clone();

        // Apply caller ID if specified
        if (options?.CallerId is { } callerId && callerId != Guid.Empty)
        {
            clonedClient.CallerId = callerId;
        }

        Interlocked.Increment(ref _activeConnections);

        return new DeviceCodePooledClient(clonedClient, () =>
        {
            Interlocked.Decrement(ref _activeConnections);
        });
    }

    /// <inheritdoc />
    public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken = default)
    {
        await using var client = await GetClientAsync(cancellationToken: cancellationToken);
        return await client.ExecuteAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public void RecordAuthFailure() { /* Not tracked for simple pool */ }

    /// <inheritdoc />
    public void RecordConnectionFailure() { /* Not tracked for simple pool */ }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DeviceCodeConnectionPool));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serviceClient.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Task.Run(() => _serviceClient.Dispose());
    }
}

/// <summary>
/// A simple pooled client wrapper for device code flow.
/// Delegates all operations to the underlying ServiceClient.
/// </summary>
internal sealed class DeviceCodePooledClient : IPooledClient
{
    private readonly ServiceClient _client;
    private readonly Action _onDispose;
    private bool _disposed;

    public DeviceCodePooledClient(ServiceClient client, Action onDispose)
    {
        _client = client;
        _onDispose = onDispose;
        ConnectionId = Guid.NewGuid();
        CreatedAt = DateTime.UtcNow;
        LastUsedAt = DateTime.UtcNow;
    }

    // IPooledClient properties
    public Guid ConnectionId { get; }
    public string ConnectionName => "Interactive";
    public DateTime CreatedAt { get; }
    public DateTime LastUsedAt { get; private set; }
    public bool IsInvalid { get; private set; }
    public string? InvalidReason { get; private set; }

    public void MarkInvalid(string reason)
    {
        IsInvalid = true;
        InvalidReason = reason;
    }

    // IDataverseClient properties - delegate to ServiceClient
    public bool IsReady => _client.IsReady;
    public int RecommendedDegreesOfParallelism => _client.RecommendedDegreesOfParallelism;
    public Guid? ConnectedOrgId => _client.ConnectedOrgId;
    public string ConnectedOrgFriendlyName => _client.ConnectedOrgFriendlyName;
    public string ConnectedOrgUniqueName => _client.ConnectedOrgUniqueName;
    public Version? ConnectedOrgVersion => _client.ConnectedOrgVersion;
    public string? LastError => _client.LastError;
    public Exception? LastException => _client.LastException;
    public Guid CallerId { get => _client.CallerId; set => _client.CallerId = value; }
    public Guid? CallerAADObjectId { get => _client.CallerAADObjectId; set => _client.CallerAADObjectId = value; }
    public int MaxRetryCount { get => _client.MaxRetryCount; set => _client.MaxRetryCount = value; }
    public TimeSpan RetryPauseTime { get => _client.RetryPauseTime; set => _client.RetryPauseTime = value; }

    public IDataverseClient Clone()
    {
        var cloned = _client.Clone();
        return new DeviceCodePooledClient(cloned, () => { });
    }

    // IOrganizationServiceAsync2 methods (with CancellationToken)
    public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.ExecuteAsync(request, cancellationToken);
    }

    public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.AssociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken);
    }

    public Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.CreateAsync(entity, cancellationToken);
    }

    public Task<Entity> CreateAndReturnAsync(Entity entity, CancellationToken cancellationToken)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.CreateAndReturnAsync(entity, cancellationToken);
    }

    public Task DeleteAsync(string entityName, Guid id, CancellationToken cancellationToken)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.DeleteAsync(entityName, id, cancellationToken);
    }

    public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.DisassociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken);
    }

    public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet, CancellationToken cancellationToken)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.RetrieveAsync(entityName, id, columnSet, cancellationToken);
    }

    public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query, CancellationToken cancellationToken)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.RetrieveMultipleAsync(query, cancellationToken);
    }

    public Task UpdateAsync(Entity entity, CancellationToken cancellationToken)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.UpdateAsync(entity, cancellationToken);
    }

    // IOrganizationServiceAsync methods (without CancellationToken)
    public Task<Guid> CreateAsync(Entity entity)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.CreateAsync(entity);
    }

    public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.RetrieveAsync(entityName, id, columnSet);
    }

    public Task UpdateAsync(Entity entity)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.UpdateAsync(entity);
    }

    public Task DeleteAsync(string entityName, Guid id)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.DeleteAsync(entityName, id);
    }

    public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.ExecuteAsync(request);
    }

    public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.AssociateAsync(entityName, entityId, relationship, relatedEntities);
    }

    public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.DisassociateAsync(entityName, entityId, relationship, relatedEntities);
    }

    public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.RetrieveMultipleAsync(query);
    }

    // IOrganizationService sync methods - delegate to ServiceClient
    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
    {
        LastUsedAt = DateTime.UtcNow;
        _client.Associate(entityName, entityId, relationship, relatedEntities);
    }

    public Guid Create(Entity entity)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.Create(entity);
    }

    public void Delete(string entityName, Guid id)
    {
        LastUsedAt = DateTime.UtcNow;
        _client.Delete(entityName, id);
    }

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
    {
        LastUsedAt = DateTime.UtcNow;
        _client.Disassociate(entityName, entityId, relationship, relatedEntities);
    }

    public OrganizationResponse Execute(OrganizationRequest request)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.Execute(request);
    }

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.Retrieve(entityName, id, columnSet);
    }

    public EntityCollection RetrieveMultiple(QueryBase query)
    {
        LastUsedAt = DateTime.UtcNow;
        return _client.RetrieveMultiple(query);
    }

    public void Update(Entity entity)
    {
        LastUsedAt = DateTime.UtcNow;
        _client.Update(entity);
    }

    // Dispose
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _onDispose();
        _client.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
