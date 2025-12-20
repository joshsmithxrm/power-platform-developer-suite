using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace PPDS.Dataverse.Client
{
    /// <summary>
    /// Implementation of <see cref="IDataverseClient"/> that wraps a <see cref="ServiceClient"/>.
    /// Provides a consistent abstraction over the Dataverse SDK.
    /// </summary>
    public class DataverseClient : IDataverseClient, IDisposable
    {
        private readonly ServiceClient _serviceClient;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataverseClient"/> class.
        /// </summary>
        /// <param name="serviceClient">The underlying ServiceClient to wrap.</param>
        /// <exception cref="ArgumentNullException">Thrown when serviceClient is null.</exception>
        public DataverseClient(ServiceClient serviceClient)
        {
            _serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DataverseClient"/> class using a connection string.
        /// </summary>
        /// <param name="connectionString">The Dataverse connection string.</param>
        /// <exception cref="ArgumentException">Thrown when connectionString is null or empty.</exception>
        public DataverseClient(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
            }

            _serviceClient = new ServiceClient(connectionString);
        }

        /// <inheritdoc />
        public bool IsReady => _serviceClient.IsReady;

        /// <inheritdoc />
        public int RecommendedDegreesOfParallelism => _serviceClient.RecommendedDegreesOfParallelism;

        /// <inheritdoc />
        public Guid? ConnectedOrgId => _serviceClient.ConnectedOrgId;

        /// <inheritdoc />
        public string ConnectedOrgFriendlyName => _serviceClient.ConnectedOrgFriendlyName;

        /// <inheritdoc />
        public string ConnectedOrgUniqueName => _serviceClient.ConnectedOrgUniqueName;

        /// <inheritdoc />
        public Version? ConnectedOrgVersion => _serviceClient.ConnectedOrgVersion;

        /// <inheritdoc />
        public string? LastError => _serviceClient.LastError;

        /// <inheritdoc />
        public Exception? LastException => _serviceClient.LastException;

        /// <inheritdoc />
        public Guid CallerId
        {
            get => _serviceClient.CallerId;
            set => _serviceClient.CallerId = value;
        }

        /// <inheritdoc />
        public Guid? CallerAADObjectId
        {
            get => _serviceClient.CallerAADObjectId;
            set => _serviceClient.CallerAADObjectId = value;
        }

        /// <inheritdoc />
        public int MaxRetryCount
        {
            get => _serviceClient.MaxRetryCount;
            set => _serviceClient.MaxRetryCount = value;
        }

        /// <inheritdoc />
        public TimeSpan RetryPauseTime
        {
            get => _serviceClient.RetryPauseTime;
            set => _serviceClient.RetryPauseTime = value;
        }

        /// <inheritdoc />
        public IDataverseClient Clone()
        {
            return new DataverseClient(_serviceClient.Clone());
        }

        #region IOrganizationService Implementation

        /// <inheritdoc />
        public Guid Create(Entity entity)
        {
            return _serviceClient.Create(entity);
        }

        /// <inheritdoc />
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            return _serviceClient.Retrieve(entityName, id, columnSet);
        }

        /// <inheritdoc />
        public void Update(Entity entity)
        {
            _serviceClient.Update(entity);
        }

        /// <inheritdoc />
        public void Delete(string entityName, Guid id)
        {
            _serviceClient.Delete(entityName, id);
        }

        /// <inheritdoc />
        public OrganizationResponse Execute(OrganizationRequest request)
        {
            return _serviceClient.Execute(request);
        }

        /// <inheritdoc />
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            _serviceClient.Associate(entityName, entityId, relationship, relatedEntities);
        }

        /// <inheritdoc />
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            _serviceClient.Disassociate(entityName, entityId, relationship, relatedEntities);
        }

        /// <inheritdoc />
        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            return _serviceClient.RetrieveMultiple(query);
        }

        #endregion

        #region IOrganizationServiceAsync Implementation

        /// <inheritdoc />
        public Task<Guid> CreateAsync(Entity entity)
        {
            return _serviceClient.CreateAsync(entity);
        }

        /// <inheritdoc />
        public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet)
        {
            return _serviceClient.RetrieveAsync(entityName, id, columnSet);
        }

        /// <inheritdoc />
        public Task UpdateAsync(Entity entity)
        {
            return _serviceClient.UpdateAsync(entity);
        }

        /// <inheritdoc />
        public Task DeleteAsync(string entityName, Guid id)
        {
            return _serviceClient.DeleteAsync(entityName, id);
        }

        /// <inheritdoc />
        public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request)
        {
            return _serviceClient.ExecuteAsync(request);
        }

        /// <inheritdoc />
        public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            return _serviceClient.AssociateAsync(entityName, entityId, relationship, relatedEntities);
        }

        /// <inheritdoc />
        public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            return _serviceClient.DisassociateAsync(entityName, entityId, relationship, relatedEntities);
        }

        /// <inheritdoc />
        public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query)
        {
            return _serviceClient.RetrieveMultipleAsync(query);
        }

        #endregion

        #region IOrganizationServiceAsync2 Implementation

        /// <inheritdoc />
        public Task<Guid> CreateAsync(Entity entity, CancellationToken cancellationToken)
        {
            return _serviceClient.CreateAsync(entity, cancellationToken);
        }

        /// <inheritdoc />
        public Task<Entity> CreateAndReturnAsync(Entity entity, CancellationToken cancellationToken)
        {
            return _serviceClient.CreateAndReturnAsync(entity, cancellationToken);
        }

        /// <inheritdoc />
        public Task<Entity> RetrieveAsync(string entityName, Guid id, ColumnSet columnSet, CancellationToken cancellationToken)
        {
            return _serviceClient.RetrieveAsync(entityName, id, columnSet, cancellationToken);
        }

        /// <inheritdoc />
        public Task UpdateAsync(Entity entity, CancellationToken cancellationToken)
        {
            return _serviceClient.UpdateAsync(entity, cancellationToken);
        }

        /// <inheritdoc />
        public Task DeleteAsync(string entityName, Guid id, CancellationToken cancellationToken)
        {
            return _serviceClient.DeleteAsync(entityName, id, cancellationToken);
        }

        /// <inheritdoc />
        public Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken)
        {
            return _serviceClient.ExecuteAsync(request, cancellationToken);
        }

        /// <inheritdoc />
        public Task AssociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken)
        {
            return _serviceClient.AssociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken);
        }

        /// <inheritdoc />
        public Task DisassociateAsync(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities, CancellationToken cancellationToken)
        {
            return _serviceClient.DisassociateAsync(entityName, entityId, relationship, relatedEntities, cancellationToken);
        }

        /// <inheritdoc />
        public Task<EntityCollection> RetrieveMultipleAsync(QueryBase query, CancellationToken cancellationToken)
        {
            return _serviceClient.RetrieveMultipleAsync(query, cancellationToken);
        }

        #endregion

        /// <summary>
        /// Disposes of the client and releases resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes of managed resources.
        /// </summary>
        /// <param name="disposing">Whether to dispose managed resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _serviceClient.Dispose();
            }

            _disposed = true;
        }
    }
}
