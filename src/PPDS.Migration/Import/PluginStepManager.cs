using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Generated;
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
            IEnumerable<int> objectTypeCodes,
            CancellationToken cancellationToken = default)
        {
            var otcList = objectTypeCodes.ToList();
            if (otcList.Count == 0)
            {
                return Array.Empty<Guid>();
            }

            _logger?.LogInformation("Querying active plugin steps for {Count} entities", otcList.Count);

            await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var activeStepIds = new List<Guid>();

            var fetchXml = BuildPluginStepQuery(otcList);

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

                var update = new SdkMessageProcessingStep
                {
                    Id = stepId,
                    StateCode = sdkmessageprocessingstep_statecode.Disabled,
                    StatusCode = sdkmessageprocessingstep_statuscode.Disabled
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
        /// <remarks>
        /// D5: per-step failures are collected and surfaced via an aggregated
        /// <see cref="PluginStepReenableException"/>. Previously per-step failures were logged
        /// at Warning only; if the token expired mid-re-enable, plugins stayed quietly disabled
        /// post-"successful" import. Callers in <c>TieredImporter.EnablePluginsAfterImportAsync</c>
        /// catch this and push structured warnings into <see cref="Progress.IWarningCollector"/>.
        /// </remarks>
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

            var failures = new List<(Guid StepId, Exception Error)>();

            foreach (var stepId in stepList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var update = new SdkMessageProcessingStep
                {
                    Id = stepId,
                    StateCode = sdkmessageprocessingstep_statecode.Enabled,
                    StatusCode = sdkmessageprocessingstep_statuscode.Enabled
                };

                try
                {
                    await client.UpdateAsync(update).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to re-enable plugin step {StepId}", stepId);
                    failures.Add((stepId, ex));
                }
            }

            if (failures.Count > 0)
            {
                throw new PluginStepReenableException(failures);
            }
        }

        private static string BuildPluginStepQuery(List<int> objectTypeCodes)
        {
            // Build filter condition for multiple entities using Object Type Codes
            var entityConditions = string.Join("\n",
                objectTypeCodes.Select(otc => $"<condition attribute='{SdkMessageFilter.Fields.PrimaryObjectTypeCode}' operator='eq' value='{otc}' />"));

            return $@"<fetch>
                <entity name='{SdkMessageProcessingStep.EntityLogicalName}'>
                    <attribute name='{SdkMessageProcessingStep.Fields.SdkMessageProcessingStepId}' />
                    <attribute name='{SdkMessageProcessingStep.Fields.Name}' />
                    <filter type='and'>
                        <condition attribute='{SdkMessageProcessingStep.Fields.StateCode}' operator='eq' value='{(int)sdkmessageprocessingstep_statecode.Enabled}' />
                        <condition attribute='{SdkMessageProcessingStep.Fields.IsHidden}' operator='eq' value='0' />
                        <condition attribute='{SdkMessageProcessingStep.Fields.CustomizationLevel}' operator='eq' value='1' />
                    </filter>
                    <link-entity name='{SdkMessageFilter.EntityLogicalName}' from='{SdkMessageFilter.Fields.SdkMessageFilterId}' to='{SdkMessageProcessingStep.Fields.SdkMessageFilterId}' link-type='inner'>
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
        /// <param name="objectTypeCodes">The Object Type Codes of entities to find plugin steps for.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<IReadOnlyList<Guid>> GetActivePluginStepsAsync(
            IEnumerable<int> objectTypeCodes,
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
        /// <remarks>
        /// Throws <see cref="PluginStepReenableException"/> if one or more steps fail to re-enable,
        /// so callers can surface the failures rather than silently leaving plugins disabled.
        /// </remarks>
        Task EnablePluginStepsAsync(
            IEnumerable<Guid> stepIds,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Aggregated exception thrown when one or more plugin steps fail to re-enable.
    /// Preserves per-step failure details so callers can report exactly which steps remain disabled.
    /// </summary>
    public class PluginStepReenableException : Exception
    {
        /// <summary>
        /// Gets the per-step failures. Each tuple contains the step ID and the underlying exception.
        /// </summary>
        public IReadOnlyList<(Guid StepId, Exception Error)> Failures { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginStepReenableException"/> class.
        /// </summary>
        /// <param name="failures">The per-step failures.</param>
        public PluginStepReenableException(IReadOnlyList<(Guid StepId, Exception Error)> failures)
            : base(BuildMessage(failures), failures?.Count > 0 ? failures[0].Error : null)
        {
            Failures = failures ?? Array.Empty<(Guid, Exception)>();
        }

        private static string BuildMessage(IReadOnlyList<(Guid StepId, Exception Error)>? failures)
        {
            var count = failures?.Count ?? 0;
            return count == 1
                ? $"Failed to re-enable 1 plugin step (stepId={failures![0].StepId}): {failures[0].Error.Message}"
                : $"Failed to re-enable {count} plugin step(s) after import. They remain disabled.";
        }
    }
}
