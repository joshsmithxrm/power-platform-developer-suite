using System;
using System.Collections.Concurrent;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// Extension methods on <see cref="IPooledClient"/> for common Dataverse SDK operations
    /// that wrap <see cref="OrganizationRequest"/>/<see cref="OrganizationResponse"/> pairs
    /// into strongly-typed async helpers.
    /// </summary>
    public static class PooledClientExtensions
    {
        /// <summary>
        /// Per-environment semaphore for publish operations. Ensures only one publish
        /// (PublishXml or PublishAll) runs at a time per environment to avoid server-side conflicts.
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublishLocks = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Retrieves an unpublished record from Dataverse.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Uses <see cref="RetrieveUnpublishedRequest"/> to fetch the draft (unpublished) version
        /// of a record. This is essential for web resource editing where the user needs to see
        /// the latest saved content, not the last-published version.
        /// </para>
        /// </remarks>
        /// <param name="client">The pooled client to execute the request on.</param>
        /// <param name="entityLogicalName">The logical name of the entity to retrieve.</param>
        /// <param name="id">The unique identifier of the record.</param>
        /// <param name="columnSet">The columns to retrieve.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The unpublished entity record.</returns>
        public static async Task<Entity> RetrieveUnpublishedAsync(
            this IPooledClient client,
            string entityLogicalName,
            Guid id,
            ColumnSet columnSet,
            CancellationToken cancellationToken = default)
        {
            var request = new RetrieveUnpublishedRequest
            {
                Target = new EntityReference(entityLogicalName, id),
                ColumnSet = columnSet
            };

            var response = (RetrieveUnpublishedResponse)await client.ExecuteAsync(request, cancellationToken);
            return response.Entity;
        }

        /// <summary>
        /// Publishes specific customizations using a PublishXml request.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Wraps <see cref="PublishXmlRequest"/> with per-environment concurrency protection.
        /// Only one publish operation (PublishXml or PublishAll) can run at a time per environment
        /// to avoid server-side conflicts and redundant API calls.
        /// </para>
        /// <para>
        /// If a publish is already in progress for the environment, throws
        /// <see cref="InvalidOperationException"/>. Callers in the RPC/Application Service layer
        /// should catch this and wrap it in a <c>PpdsException</c> with an appropriate error code.
        /// </para>
        /// </remarks>
        /// <param name="client">The pooled client to execute the request on.</param>
        /// <param name="parameterXml">
        /// The XML describing which customizations to publish.
        /// For example: <c>&lt;importexportxml&gt;&lt;webresources&gt;&lt;webresource&gt;{id}&lt;/webresource&gt;&lt;/webresources&gt;&lt;/importexportxml&gt;</c>
        /// </param>
        /// <param name="environmentKey">
        /// A stable key identifying the target environment (e.g., environment URL or org unique name).
        /// Used to scope the publish lock so different environments can publish concurrently.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">
        /// A publish operation is already in progress for this environment.
        /// </exception>
        public static async Task PublishXmlAsync(
            this IPooledClient client,
            string parameterXml,
            string environmentKey,
            CancellationToken cancellationToken = default)
        {
            var semaphore = PublishLocks.GetOrAdd(environmentKey, _ => new SemaphoreSlim(1, 1));

            if (!await semaphore.WaitAsync(0, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"A publish operation is already in progress for environment '{environmentKey}'. " +
                    "Wait for it to complete before starting another.");
            }

            try
            {
                var request = new PublishXmlRequest
                {
                    ParameterXml = parameterXml
                };

                await client.ExecuteAsync(request, cancellationToken);
            }
            catch (FaultException<OrganizationServiceFault> ex) when (IsEnvironmentLockFault(ex))
            {
                throw new InvalidOperationException(
                    "Your changes were saved to Dataverse but could not be published because another operation " +
                    $"is currently running in the environment ({ex.Detail?.Message ?? ex.Message}). " +
                    "Wait for the conflicting operation to complete, then re-run the command with --publish " +
                    "or run `ppds publish` to publish manually.",
                    ex);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Publishes all customizations in the environment.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Wraps <see cref="PublishAllXmlRequest"/> with per-environment concurrency protection.
        /// Shares the same per-environment semaphore as <see cref="PublishXmlAsync"/> so that
        /// a PublishAll and a PublishXml cannot run simultaneously for the same environment.
        /// </para>
        /// <para>
        /// If a publish is already in progress for the environment, throws
        /// <see cref="InvalidOperationException"/>. Callers in the RPC/Application Service layer
        /// should catch this and wrap it in a <c>PpdsException</c> with an appropriate error code.
        /// </para>
        /// </remarks>
        /// <param name="client">The pooled client to execute the request on.</param>
        /// <param name="environmentKey">
        /// A stable key identifying the target environment (e.g., environment URL or org unique name).
        /// Used to scope the publish lock so different environments can publish concurrently.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">
        /// A publish operation is already in progress for this environment.
        /// </exception>
        public static async Task PublishAllXmlAsync(
            this IPooledClient client,
            string environmentKey,
            CancellationToken cancellationToken = default)
        {
            var semaphore = PublishLocks.GetOrAdd(environmentKey, _ => new SemaphoreSlim(1, 1));

            if (!await semaphore.WaitAsync(0, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"A publish operation is already in progress for environment '{environmentKey}'. " +
                    "Wait for it to complete before starting another.");
            }

            try
            {
                var request = new PublishAllXmlRequest();
                await client.ExecuteAsync(request, cancellationToken);
            }
            catch (FaultException<OrganizationServiceFault> ex) when (IsEnvironmentLockFault(ex))
            {
                throw new InvalidOperationException(
                    "Publish-all could not start because another operation is currently running in the environment " +
                    $"({ex.Detail?.Message ?? ex.Message}). " +
                    "Wait for the conflicting operation to complete and retry.",
                    ex);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Returns true when a Dataverse fault indicates the environment is locked by another
        /// concurrent operation (e.g. an active import or publish).
        /// Dataverse surfaces this as "Cannot start the requested operation [X] because there is
        /// another [Y] running at this moment" — match on that message pattern since the error
        /// code is not publicly documented.
        /// </summary>
        private static bool IsEnvironmentLockFault(FaultException<OrganizationServiceFault> ex)
        {
            var detail = ex.Detail?.Message;
            var msg = string.IsNullOrEmpty(detail) ? (ex.Message ?? string.Empty) : detail;
            return msg.Contains("Cannot start the requested operation", StringComparison.OrdinalIgnoreCase)
                && msg.Contains("running at this moment", StringComparison.OrdinalIgnoreCase);
        }
    }
}
