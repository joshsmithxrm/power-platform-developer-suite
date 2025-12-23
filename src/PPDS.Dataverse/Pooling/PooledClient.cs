using System;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Resilience;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// A client wrapper that returns the connection to the pool on dispose.
    /// Automatically detects and records throttle events.
    /// </summary>
    internal sealed class PooledClient : IPooledClient
    {
        private readonly IDataverseClient _client;
        private readonly Action<PooledClient> _returnToPool;
        private readonly Action<string, TimeSpan>? _onThrottle;
        private readonly Guid _originalCallerId;
        private readonly Guid? _originalCallerAADObjectId;
        private readonly int _originalMaxRetryCount;
        private readonly TimeSpan _originalRetryPauseTime;
        private int _returned;

        /// <summary>
        /// Initializes a new instance of the <see cref="PooledClient"/> class.
        /// </summary>
        /// <param name="client">The underlying client.</param>
        /// <param name="connectionName">The name of the connection configuration.</param>
        /// <param name="returnToPool">Action to call when returning to pool.</param>
        /// <param name="onThrottle">Optional callback when throttle is detected (connectionName, retryAfter).</param>
        internal PooledClient(
            IDataverseClient client,
            string connectionName,
            Action<PooledClient> returnToPool,
            Action<string, TimeSpan>? onThrottle = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _returnToPool = returnToPool ?? throw new ArgumentNullException(nameof(returnToPool));
            _onThrottle = onThrottle;
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
        public bool IsInvalid { get; private set; }

        /// <inheritdoc />
        public string? InvalidReason { get; private set; }

        /// <inheritdoc />
        public void MarkInvalid(string reason)
        {
            IsInvalid = true;
            InvalidReason = reason;
        }

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
        /// Resets the client to its original state and marks it available for reuse.
        /// </summary>
        internal void Reset()
        {
            _client.CallerId = _originalCallerId;
            _client.CallerAADObjectId = _originalCallerAADObjectId;
            _client.MaxRetryCount = _originalMaxRetryCount;
            _client.RetryPauseTime = _originalRetryPauseTime;

            // Reset the invalid state
            IsInvalid = false;
            InvalidReason = null;

            // Reset the returned flag so this client can be returned again on next use
            Interlocked.Exchange(ref _returned, 0);
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

        #region Throttle Detection

        private static readonly TimeSpan FallbackRetryAfter = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Checks if an exception is a service protection error and extracts the RetryAfter.
        /// </summary>
        private bool TryHandleThrottle(Exception ex)
        {
            if (ex is not FaultException<OrganizationServiceFault> faultEx)
            {
                return false;
            }

            var fault = faultEx.Detail;
            if (!ServiceProtectionException.IsServiceProtectionError(fault.ErrorCode))
            {
                return false;
            }

            // Extract RetryAfter and notify the pool
            var retryAfter = ExtractRetryAfter(fault);
            _onThrottle?.Invoke(ConnectionName, retryAfter);
            return true;
        }

        /// <summary>
        /// Extracts the Retry-After duration from a fault.
        /// </summary>
        private static TimeSpan ExtractRetryAfter(OrganizationServiceFault fault)
        {
            if (fault.ErrorDetails != null &&
                fault.ErrorDetails.TryGetValue("Retry-After", out var retryAfterObj))
            {
                return retryAfterObj switch
                {
                    TimeSpan ts => ts,
                    int seconds => TimeSpan.FromSeconds(seconds),
                    double seconds => TimeSpan.FromSeconds(seconds),
                    _ => FallbackRetryAfter
                };
            }

            return FallbackRetryAfter;
        }

        /// <summary>
        /// Wraps a synchronous operation with throttle detection.
        /// </summary>
        private T ExecuteWithThrottleDetection<T>(Func<T> operation)
        {
            try
            {
                return operation();
            }
            catch (Exception ex) when (TryHandleThrottle(ex))
            {
                throw; // Re-throw after recording throttle
            }
        }

        /// <summary>
        /// Wraps a synchronous void operation with throttle detection.
        /// </summary>
        private void ExecuteWithThrottleDetection(Action operation)
        {
            try
            {
                operation();
            }
            catch (Exception ex) when (TryHandleThrottle(ex))
            {
                throw; // Re-throw after recording throttle
            }
        }

        /// <summary>
        /// Wraps an async operation with throttle detection.
        /// </summary>
        private async Task<T> ExecuteWithThrottleDetectionAsync<T>(Func<Task<T>> operation)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (TryHandleThrottle(ex))
            {
                throw; // Re-throw after recording throttle
            }
        }

        /// <summary>
        /// Wraps an async void operation with throttle detection.
        /// </summary>
        private async Task ExecuteWithThrottleDetectionAsync(Func<Task> operation)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (TryHandleThrottle(ex))
            {
                throw; // Re-throw after recording throttle
            }
        }

        #endregion

        #region IOrganizationService Implementation

        /// <inheritdoc />
        public Guid Create(Entity entity) =>
            ExecuteWithThrottleDetection(() => _client.Create(entity));

        /// <inheritdoc />
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
            ExecuteWithThrottleDetection(() => _client.Retrieve(entityName, id, columnSet));

        /// <inheritdoc />
        public void Update(Entity entity) =>
            ExecuteWithThrottleDetection(() => _client.Update(entity));

        /// <inheritdoc />
        public void Delete(string entityName, Guid id) =>
            ExecuteWithThrottleDetection(() => _client.Delete(entityName, id));

        /// <inheritdoc />
        public OrganizationResponse Execute(OrganizationRequest request) =>
            ExecuteWithThrottleDetection(() => _client.Execute(request));

        /// <inheritdoc />
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            ExecuteWithThrottleDetection(() => _client.Associate(entityName, entityId, relationship, relatedEntities));

        /// <inheritdoc />
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            ExecuteWithThrottleDetection(() => _client.Disassociate(entityName, entityId, relationship, relatedEntities));

        /// <inheritdoc />
        public EntityCollection RetrieveMultiple(QueryBase query) =>
            ExecuteWithThrottleDetection(() => _client.RetrieveMultiple(query));

        #endregion

        #region IOrganizationServiceAsync Implementation

        /// <inheritdoc />
        public Task<Guid> CreateAsync(Entity entity) =>
            ExecuteWithThrottleDetectionAsync(() => _client.CreateAsync(entity));

        /// <inheritdoc />
        public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet) =>
            ExecuteWithThrottleDetectionAsync(() => _client.RetrieveAsync(entityName, id, columnSet));

        /// <inheritdoc />
        public Task UpdateAsync(Entity entity) =>
            ExecuteWithThrottleDetectionAsync(() => _client.UpdateAsync(entity));

        /// <inheritdoc />
        public Task DeleteAsync(string entityName, Guid id) =>
            ExecuteWithThrottleDetectionAsync(() => _client.DeleteAsync(entityName, id));

        /// <inheritdoc />
        public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request) =>
            ExecuteWithThrottleDetectionAsync(() => _client.ExecuteAsync(request));

        /// <inheritdoc />
        public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            ExecuteWithThrottleDetectionAsync(() => _client.AssociateAsync(entityName, entityId, relationship, relatedEntities));

        /// <inheritdoc />
        public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
            ExecuteWithThrottleDetectionAsync(() => _client.DisassociateAsync(entityName, entityId, relationship, relatedEntities));

        /// <inheritdoc />
        public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query) =>
            ExecuteWithThrottleDetectionAsync(() => _client.RetrieveMultipleAsync(query));

        #endregion

        #region IOrganizationServiceAsync2 Implementation

        /// <inheritdoc />
        public Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken) =>
            ExecuteWithThrottleDetectionAsync(() => _client.CreateAsync(entity, cancellationToken));

        /// <inheritdoc />
        public Task<Entity> CreateAndReturnAsync(Entity entity, CancellationToken cancellationToken) =>
            ExecuteWithThrottleDetectionAsync(() => _client.CreateAndReturnAsync(entity, cancellationToken));

        /// <inheritdoc />
        public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet, CancellationToken cancellationToken) =>
            ExecuteWithThrottleDetectionAsync(() => _client.RetrieveAsync(entityName, id, columnSet, cancellationToken));

        /// <inheritdoc />
        public Task UpdateAsync(Entity entity, CancellationToken cancellationToken) =>
            ExecuteWithThrottleDetectionAsync(() => _client.UpdateAsync(entity, cancellationToken));

        /// <inheritdoc />
        public Task DeleteAsync(string entityName, Guid id, CancellationToken cancellationToken) =>
            ExecuteWithThrottleDetectionAsync(() => _client.DeleteAsync(entityName, id, cancellationToken));

        /// <inheritdoc />
        public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken) =>
            ExecuteWithThrottleDetectionAsync(() => _client.ExecuteAsync(request, cancellationToken));

        /// <inheritdoc />
        public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken) =>
            ExecuteWithThrottleDetectionAsync(() => _client.AssociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken));

        /// <inheritdoc />
        public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken) =>
            ExecuteWithThrottleDetectionAsync(() => _client.DisassociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken));

        /// <inheritdoc />
        public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query, CancellationToken cancellationToken) =>
            ExecuteWithThrottleDetectionAsync(() => _client.RetrieveMultipleAsync(query, cancellationToken));

        #endregion

        /// <inheritdoc />
        public void Dispose()
        {
            // Use Interlocked.Exchange to ensure we only return to pool once per checkout.
            // This prevents double-release of the semaphore if Dispose is called multiple times.
            // The flag is reset in Reset() when the connection is returned to the pool.
            if (Interlocked.Exchange(ref _returned, 1) != 0)
            {
                return;
            }

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
