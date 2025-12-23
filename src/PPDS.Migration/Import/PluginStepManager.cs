using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Manages plugin step disabling/enabling during import.
    /// </summary>
    public class PluginStepManager : IPluginStepManager
    {
        private readonly IDataverseConnectionPool _connectionPool;
        private readonly ILogger<PluginStepManager>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginStepManager"/> class.
        /// </summary>
        public PluginStepManager(IDataverseConnectionPool connectionPool)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginStepManager"/> class.
        /// </summary>
        public PluginStepManager(IDataverseConnectionPool connectionPool, ILogger<PluginStepManager> logger)
            : this(connectionPool)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Guid>> GetActivePluginStepsAsync(
            IEnumerable<string> entityLogicalNames,
            CancellationToken cancellationToken = default)
        {
            var entityList = entityLogicalNames.ToList();
            if (entityList.Count == 0)
            {
                return Array.Empty<Guid>();
            }

            _logger?.LogInformation("Querying active plugin steps for {Count} entities", entityList.Count);

            await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var activeStepIds = new List<Guid>();

            // Query sdkmessageprocessingstep for each entity
            // We need to join through sdkmessagefilter to find steps by entity
            var fetchXml = BuildPluginStepQuery(entityList);

            var response = await client.RetrieveMultipleAsync(new FetchExpression(fetchXml))
                .ConfigureAwait(false);

            foreach (var entity in response.Entities)
            {
                activeStepIds.Add(entity.Id);
            }

            _logger?.LogInformation("Found {Count} active plugin steps", activeStepIds.Count);

            return activeStepIds;
        }

        /// <inheritdoc />
        public async Task DisablePluginStepsAsync(
            IEnumerable<Guid> stepIds,
            CancellationToken cancellationToken = default)
        {
            var stepList = stepIds.ToList();
            if (stepList.Count == 0)
            {
                return;
            }

            _logger?.LogInformation("Disabling {Count} plugin steps", stepList.Count);

            await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var stepId in stepList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var update = new Entity("sdkmessageprocessingstep", stepId)
                {
                    ["statecode"] = new OptionSetValue(1), // Disabled
                    ["statuscode"] = new OptionSetValue(2) // Disabled
                };

                try
                {
                    await client.UpdateAsync(update).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to disable plugin step {StepId}", stepId);
                }
            }
        }

        /// <inheritdoc />
        public async Task EnablePluginStepsAsync(
            IEnumerable<Guid> stepIds,
            CancellationToken cancellationToken = default)
        {
            var stepList = stepIds.ToList();
            if (stepList.Count == 0)
            {
                return;
            }

            _logger?.LogInformation("Re-enabling {Count} plugin steps", stepList.Count);

            await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var stepId in stepList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var update = new Entity("sdkmessageprocessingstep", stepId)
                {
                    ["statecode"] = new OptionSetValue(0), // Enabled
                    ["statuscode"] = new OptionSetValue(1) // Enabled
                };

                try
                {
                    await client.UpdateAsync(update).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to re-enable plugin step {StepId}", stepId);
                }
            }
        }

        private static string BuildPluginStepQuery(List<string> entityLogicalNames)
        {
            // Build filter condition for multiple entities
            var entityConditions = string.Join("\n",
                entityLogicalNames.Select(e => $"<condition attribute='primaryobjecttypecode' operator='eq' value='{e}' />"));

            return $@"<fetch>
                <entity name='sdkmessageprocessingstep'>
                    <attribute name='sdkmessageprocessingstepid' />
                    <attribute name='name' />
                    <filter type='and'>
                        <condition attribute='statecode' operator='eq' value='0' />
                        <condition attribute='ishidden' operator='eq' value='0' />
                        <condition attribute='customizationlevel' operator='eq' value='1' />
                    </filter>
                    <link-entity name='sdkmessagefilter' from='sdkmessagefilterid' to='sdkmessagefilterid' link-type='inner'>
                        <filter type='or'>
                            {entityConditions}
                        </filter>
                    </link-entity>
                </entity>
            </fetch>";
        }
    }

    /// <summary>
    /// Interface for managing plugin steps during import.
    /// </summary>
    public interface IPluginStepManager
    {
        /// <summary>
        /// Gets the IDs of active plugin steps for the specified entities.
        /// </summary>
        Task<IReadOnlyList<Guid>> GetActivePluginStepsAsync(
            IEnumerable<string> entityLogicalNames,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Disables the specified plugin steps.
        /// </summary>
        Task DisablePluginStepsAsync(
            IEnumerable<Guid> stepIds,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Re-enables the specified plugin steps.
        /// </summary>
        Task EnablePluginStepsAsync(
            IEnumerable<Guid> stepIds,
            CancellationToken cancellationToken = default);
    }
}
