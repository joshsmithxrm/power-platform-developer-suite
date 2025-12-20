using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Client;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// A client wrapper that returns the connection to the pool on dispose.
    /// </summary>
    internal sealed class PooledClient : IPooledClient
    {
        private readonly IDataverseClient _client;
        private readonly Action<PooledClient> _returnToPool;
        private readonly Guid _originalCallerId;
        private readonly Guid? _originalCallerAADObjectId;
        private readonly int _originalMaxRetryCount;
        private readonly TimeSpan _originalRetryPauseTime;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="PooledClient"/> class.
        /// </summary>
        /// <param name="client">The underlying client.</param>
        /// <param name="connectionName">The name of the connection configuration.</param>
        /// <param name="returnToPool">Action to call when returning to pool.</param>
        internal PooledClient(IDataverseClient client, string connectionName, Action<PooledClient> returnToPool)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _returnToPool = returnToPool ?? throw new ArgumentNullException(nameof(returnToPool));
            ConnectionName = connectionName ?? throw new ArgumentNullException(nameof(connectionName));
            ConnectionId = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
            LastUsedAt = DateTime.UtcNow;

            // Store original values for reset
            _originalCallerId = _client.CallerId;
            _originalCallerAADObjectId = _client.CallerAADObjectId;
            _originalMaxRetryCount = _client.MaxRetryCount;
            _originalRetryPauseTime = _client.RetryPauseTime;
        }

        /// <inheritdoc />
        public Guid ConnectionId { get; }

        /// <inheritdoc />
        public string ConnectionName { get; }

        /// <inheritdoc />
        public DateTime CreatedAt { get; }

        /// <inheritdoc />
        public DateTime LastUsedAt { get; internal set; }

        /// <inheritdoc />
        public bool IsReady => _client.IsReady;

        /// <inheritdoc />
        public int RecommendedDegreesOfParallelism => _client.RecommendedDegreesOfParallelism;

        /// <inheritdoc />
        public Guid? ConnectedOrgId => _client.ConnectedOrgId;

        /// <inheritdoc />
        public string ConnectedOrgFriendlyName => _client.ConnectedOrgFriendlyName;

        /// <inheritdoc />
        public string ConnectedOrgUniqueName => _client.ConnectedOrgUniqueName;

        /// <inheritdoc />
        public Version? ConnectedOrgVersion => _client.ConnectedOrgVersion;

        /// <inheritdoc />
        public string? LastError => _client.LastError;

        /// <inheritdoc />
        public Exception? LastException => _client.LastException;

        /// <inheritdoc />
        public Guid CallerId
        {
            get => _client.CallerId;
            set => _client.CallerId = value;
        }

        /// <inheritdoc />
        public Guid? CallerAADObjectId
        {
            get => _client.CallerAADObjectId;
            set => _client.CallerAADObjectId = value;
        }

        /// <inheritdoc />
        public int MaxRetryCount
        {
            get => _client.MaxRetryCount;
            set => _client.MaxRetryCount = value;
        }

        /// <inheritdoc />
        public TimeSpan RetryPauseTime
        {
            get => _client.RetryPauseTime;
            set => _client.RetryPauseTime = value;
        }

        /// <inheritdoc />
        public IDataverseClient Clone() => _client.Clone();

        /// <summary>
        /// Updates the last used timestamp.
        /// </summary>
        internal void UpdateLastUsed()
        {
            LastUsedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Resets the client to its original state.
        /// </summary>
        internal void Reset()
        {
            _client.CallerId = _originalCallerId;
            _client.CallerAADObjectId = _originalCallerAADObjectId;
            _client.MaxRetryCount = _originalMaxRetryCount;
            _client.RetryPauseTime = _originalRetryPauseTime;
        }

        /// <summary>
        /// Applies options to the client.
        /// </summary>
        internal void ApplyOptions(DataverseClientOptions options)
        {
            if (options.CallerId.HasValue)
            {
                _client.CallerId = options.CallerId.Value;
            }

            if (options.CallerAADObjectId.HasValue)
            {
                _client.CallerAADObjectId = options.CallerAADObjectId;
            }

            if (options.MaxRetryCount.HasValue)
            {
                _client.MaxRetryCount = options.MaxRetryCount.Value;
            }

            if (options.RetryPauseTime.HasValue)
            {
                _client.RetryPauseTime = options.RetryPauseTime.Value;
            }
        }

        /// <summary>
        /// Forces disposal of the underlying client without returning to pool.
        /// </summary>
        internal void ForceDispose()
        {
            if (_client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        #region IOrganizationService Implementation

        /// <inheritdoc />
        public Guid Create(Entity entity) => _client.Create(entity);

        /// <inheritdoc />
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
            => _client.Retrieve(entityName, id, columnSet);

        /// <inheritdoc />
        public void Update(Entity entity) => _client.Update(entity);

        /// <inheritdoc />
        public void Delete(string entityName, Guid id) => _client.Delete(entityName, id);

        /// <inheritdoc />
        public OrganizationResponse Execute(OrganizationRequest request) => _client.Execute(request);

        /// <inheritdoc />
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => _client.Associate(entityName, entityId, relationship, relatedEntities);

        /// <inheritdoc />
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => _client.Disassociate(entityName, entityId, relationship, relatedEntities);

        /// <inheritdoc />
        public EntityCollection RetrieveMultiple(QueryBase query) => _client.RetrieveMultiple(query);

        #endregion

        #region IOrganizationServiceAsync Implementation

        /// <inheritdoc />
        public Task<Guid> CreateAsync(Entity entity) => _client.CreateAsync(entity);

        /// <inheritdoc />
        public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet)
            => _client.RetrieveAsync(entityName, id, columnSet);

        /// <inheritdoc />
        public Task UpdateAsync(Entity entity) => _client.UpdateAsync(entity);

        /// <inheritdoc />
        public Task DeleteAsync(string entityName, Guid id) => _client.DeleteAsync(entityName, id);

        /// <inheritdoc />
        public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request) => _client.ExecuteAsync(request);

        /// <inheritdoc />
        public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => _client.AssociateAsync(entityName, entityId, relationship, relatedEntities);

        /// <inheritdoc />
        public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => _client.DisassociateAsync(entityName, entityId, relationship, relatedEntities);

        /// <inheritdoc />
        public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query) => _client.RetrieveMultipleAsync(query);

        #endregion

        #region IOrganizationServiceAsync2 Implementation

        /// <inheritdoc />
        public Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken)
            => _client.CreateAsync(entity, cancellationToken);

        /// <inheritdoc />
        public Task<Entity> CreateAndReturnAsync(Entity entity, CancellationToken cancellationToken)
            => _client.CreateAndReturnAsync(entity, cancellationToken);

        /// <inheritdoc />
        public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet, CancellationToken cancellationToken)
            => _client.RetrieveAsync(entityName, id, columnSet, cancellationToken);

        /// <inheritdoc />
        public Task UpdateAsync(Entity entity, CancellationToken cancellationToken)
            => _client.UpdateAsync(entity, cancellationToken);

        /// <inheritdoc />
        public Task DeleteAsync(string entityName, Guid id, CancellationToken cancellationToken)
            => _client.DeleteAsync(entityName, id, cancellationToken);

        /// <inheritdoc />
        public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken)
            => _client.ExecuteAsync(request, cancellationToken);

        /// <inheritdoc />
        public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken)
            => _client.AssociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken);

        /// <inheritdoc />
        public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken)
            => _client.DisassociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken);

        /// <inheritdoc />
        public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query, CancellationToken cancellationToken)
            => _client.RetrieveMultipleAsync(query, cancellationToken);

        #endregion

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _returnToPool(this);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
