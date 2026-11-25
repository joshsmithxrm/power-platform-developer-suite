using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
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
        /// <remarks>
        /// Each table is resolved in the target environment by its logical name (entity metadata), and
        /// the steps are selected by the target's object type code. The object type code recorded in a
        /// schema file is the source environment's: codes of custom tables differ between environments,
        /// so it is never used (#1921). If any table cannot be resolved, nothing is selected and an
        /// <see cref="ImportRefusedException"/> is thrown. The step query reads every page.
        /// </remarks>
        public async Task<IReadOnlyList<PluginStepInfo>> GetActivePluginStepsAsync(
            IEnumerable<string> entityLogicalNames,
            CancellationToken cancellationToken = default)
        {
            var names = entityLogicalNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0)
            {
                return Array.Empty<PluginStepInfo>();
            }

            _logger?.LogInformation("Querying active plugin steps for {Count} entities", names.Count);

            await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var targetCodes = new List<int>();
            foreach (var name in names)
            {
                targetCodes.Add(await ResolveTargetObjectTypeCodeAsync(client, name, cancellationToken).ConfigureAwait(false));
            }

            var steps = new List<PluginStepInfo>();
            var seen = new HashSet<Guid>();
            var page = 1;
            string? pagingCookie = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fetchXml = BuildPluginStepQuery(targetCodes.Distinct().ToList(), page, pagingCookie);
                var response = await client.RetrieveMultipleAsync(new FetchExpression(fetchXml), cancellationToken)
                    .ConfigureAwait(false);

                foreach (var entity in response.Entities)
                {
                    if (seen.Add(entity.Id))
                    {
                        steps.Add(new PluginStepInfo(entity.Id, entity.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Name)));
                    }
                }

                if (!response.MoreRecords)
                {
                    break;
                }

                page++;
                pagingCookie = response.PagingCookie;
            }

            _logger?.LogInformation("Found {Count} active plugin steps", steps.Count);

            return steps;
        }

        private static async Task<int> ResolveTargetObjectTypeCodeAsync(
            IPooledClient client,
            string logicalName,
            CancellationToken cancellationToken)
        {
            int? code;
            try
            {
                var response = (RetrieveEntityResponse)await client.ExecuteAsync(
                    new RetrieveEntityRequest
                    {
                        LogicalName = logicalName,
                        EntityFilters = EntityFilters.Entity,
                        RetrieveAsIfPublished = false
                    },
                    cancellationToken).ConfigureAwait(false);

                var metadata = response.EntityMetadata;
                code = metadata != null && string.Equals(metadata.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase)
                    ? metadata.ObjectTypeCode
                    : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw NotResolved(logicalName, ex);
            }

            return code ?? throw NotResolved(logicalName, null);
        }

        private static ImportRefusedException NotResolved(string logicalName, Exception? inner) =>
            new(ImportRefusalReasons.PluginTableNotResolved,
                $"Table '{logicalName}' is marked to have its plug-in steps disabled during the import, but it could not be " +
                "found in the target environment's metadata. No plug-in step was disabled and nothing was imported. " +
                "Create the table in the target, or remove disableplugins from its entry in the schema.",
                inner);

        /// <inheritdoc />
        /// <remarks>
        /// Each step id is added to <paramref name="disabledStepIds"/> before its update is sent, so a
        /// caller that re-enables that collection in a <c>finally</c> block restores every step that
        /// may have been disabled, also when the run is cancelled or an update fails part-way. A step
        /// that cannot be disabled stops the run with an <see cref="ImportRefusedException"/>: the
        /// import must not run with plug-ins it was asked to suppress.
        /// </remarks>
        public async Task DisablePluginStepsAsync(
            IEnumerable<Guid> stepIds,
            ICollection<Guid> disabledStepIds,
            CancellationToken cancellationToken = default)
        {
            if (disabledStepIds == null) throw new ArgumentNullException(nameof(disabledStepIds));

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

                disabledStepIds.Add(stepId);
                try
                {
                    await client.UpdateAsync(update, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "Failed to disable plugin step {StepId}", stepId);
                    throw new ImportRefusedException(
                        ImportRefusalReasons.PluginStepNotDisabled,
                        "A plug-in step that the schema asks to disable during the import could not be disabled, so the " +
                        "import was not run. The steps already disabled are re-enabled. Check that you can change plug-in " +
                        "step registrations in the target, or remove disableplugins from the schema.",
                        ex);
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
                    await client.UpdateAsync(update, cancellationToken).ConfigureAwait(false);
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

        /// <summary>Rows per page of the step query.</summary>
        internal const int PageSize = 5000;

        private static string BuildPluginStepQuery(List<int> objectTypeCodes, int page, string? pagingCookie)
        {
            // Build filter condition for multiple entities using the TARGET's object type codes
            var entityConditions = string.Join("\n",
                objectTypeCodes.Select(otc => $"<condition attribute='{SdkMessageFilter.Fields.PrimaryObjectTypeCode}' operator='eq' value='{otc}' />"));

            var cookie = pagingCookie != null
                ? $" paging-cookie='{System.Security.SecurityElement.Escape(pagingCookie)}'"
                : string.Empty;

            return $@"<fetch count='{PageSize}' page='{page}'{cookie}>
                <entity name='{SdkMessageProcessingStep.EntityLogicalName}'>
                    <attribute name='{SdkMessageProcessingStep.Fields.SdkMessageProcessingStepId}' />
                    <attribute name='{SdkMessageProcessingStep.Fields.Name}' />
                    <order attribute='{SdkMessageProcessingStep.Fields.SdkMessageProcessingStepId}' />
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
        /// Gets the active custom plug-in steps of the specified tables in the target environment.
        /// </summary>
        /// <param name="entityLogicalNames">The logical names of the tables. Each is resolved in the target
        /// environment's metadata; if one cannot be resolved, an <see cref="ImportRefusedException"/> is thrown
        /// and no step is returned.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<IReadOnlyList<PluginStepInfo>> GetActivePluginStepsAsync(
            IEnumerable<string> entityLogicalNames,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Disables the specified plug-in steps.
        /// </summary>
        /// <param name="stepIds">The steps to disable.</param>
        /// <param name="disabledStepIds">Receives each step id before its update is sent, so the caller can
        /// re-enable every step that may have been disabled, whatever happens next.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="ImportRefusedException">A step could not be disabled.</exception>
        Task DisablePluginStepsAsync(
            IEnumerable<Guid> stepIds,
            ICollection<Guid> disabledStepIds,
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
    /// An active plug-in step found for an import.
    /// </summary>
    /// <param name="Id">The step id.</param>
    /// <param name="Name">The step name, as registered.</param>
    public sealed record PluginStepInfo(Guid Id, string? Name);

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
