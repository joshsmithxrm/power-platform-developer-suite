using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Processes Phase 3 of import: applies state/status transitions to records
    /// that were imported in their default (active) state but need to be set to
    /// a non-default state (e.g., closed, won, lost, cancelled).
    /// </summary>
    public class StateTransitionProcessor : IImportPhaseProcessor
    {
        private readonly IDataverseConnectionPool _connectionPool;
        private readonly ILogger<StateTransitionProcessor>? _logger;

        /// <inheritdoc />
        public string PhaseName => "State Transitions";

        /// <summary>
        /// Initializes a new instance of the <see cref="StateTransitionProcessor"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="logger">Optional logger.</param>
        public StateTransitionProcessor(
            IDataverseConnectionPool connectionPool,
            ILogger<StateTransitionProcessor>? logger = null)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<PhaseResult> ProcessAsync(
            ImportContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);

            var transitions = context.StateTransitions;
            var entityNames = transitions.GetEntityNames();
            var allTransitions = new List<StateTransitionData>();

            foreach (var entityName in entityNames)
            {
                allTransitions.AddRange(transitions.GetTransitions(entityName));
            }

            if (allTransitions.Count == 0)
            {
                _logger?.LogDebug("No state transitions to process -- skipping phase");
                return PhaseResult.Skipped();
            }

            var sw = Stopwatch.StartNew();
            var successCount = 0;
            var failureCount = 0;
            var errors = new List<MigrationError>();

            foreach (var transition in allTransitions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Check if the record is already in the target state (AC-16)
                    if (await IsRecordAlreadyInStateAsync(transition, cancellationToken).ConfigureAwait(false))
                    {
                        _logger?.LogDebug(
                            "Record {EntityName}/{RecordId} already in statecode={StateCode}, skipping transition",
                            transition.EntityName, transition.RecordId, transition.StateCode);
                        successCount++;
                        continue;
                    }

                    // Apply the transition
                    if (!string.IsNullOrEmpty(transition.SdkMessage))
                    {
                        await ExecuteSdkMessageAsync(transition, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ExecuteSetStateAsync(transition, cancellationToken).ConfigureAwait(false);
                    }

                    successCount++;
                }
                catch (Exception ex)
                {
                    failureCount++;
                    errors.Add(new MigrationError
                    {
                        EntityLogicalName = transition.EntityName,
                        RecordId = transition.RecordId,
                        Message = ex.Message,
                        Phase = MigrationPhase.ProcessingStateTransitions
                    });

                    _logger?.LogWarning(ex,
                        "Failed to apply state transition for {EntityName}/{RecordId}",
                        transition.EntityName, transition.RecordId);

                    if (!context.Options.ContinueOnError)
                    {
                        break;
                    }
                }
            }

            sw.Stop();

            return new PhaseResult
            {
                Success = failureCount == 0,
                RecordsProcessed = successCount + failureCount,
                SuccessCount = successCount,
                FailureCount = failureCount,
                Duration = sw.Elapsed,
                Errors = errors
            };
        }

        /// <summary>
        /// Checks if a record is already in the target state to avoid double-closing.
        /// </summary>
        private async Task<bool> IsRecordAlreadyInStateAsync(
            StateTransitionData transition,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var client = await _connectionPool.GetClientAsync(
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var retrieved = await client.RetrieveAsync(
                    transition.EntityName,
                    transition.RecordId,
                    new ColumnSet("statecode"),
                    cancellationToken).ConfigureAwait(false);

                var currentState = retrieved.GetAttributeValue<OptionSetValue>("statecode");
                return currentState?.Value == transition.StateCode;
            }
            catch (FaultException)
            {
                // If we can't retrieve the record, proceed with the transition
                return false;
            }
        }

        /// <summary>
        /// Executes a SetStateRequest for the given transition (AC-06).
        /// </summary>
        private async Task ExecuteSetStateAsync(
            StateTransitionData transition,
            CancellationToken cancellationToken)
        {
            var request = new OrganizationRequest("SetState")
            {
                ["EntityMoniker"] = new EntityReference(transition.EntityName, transition.RecordId),
                ["State"] = new OptionSetValue(transition.StateCode),
                ["Status"] = new OptionSetValue(transition.StatusCode)
            };

            await _connectionPool.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

            _logger?.LogDebug(
                "Applied SetState for {EntityName}/{RecordId}: state={StateCode}, status={StatusCode}",
                transition.EntityName, transition.RecordId,
                transition.StateCode, transition.StatusCode);
        }

        /// <summary>
        /// Executes an SDK message for the given transition (e.g., WinOpportunity, LoseOpportunity).
        /// </summary>
        private async Task ExecuteSdkMessageAsync(
            StateTransitionData transition,
            CancellationToken cancellationToken)
        {
            var request = new OrganizationRequest(transition.SdkMessage!);

            if (transition.MessageData != null)
            {
                foreach (var kvp in transition.MessageData)
                {
                    request[kvp.Key] = kvp.Value;
                }
            }

            await _connectionPool.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

            _logger?.LogDebug(
                "Applied SDK message '{SdkMessage}' for {EntityName}/{RecordId}",
                transition.SdkMessage, transition.EntityName, transition.RecordId);
        }
    }
}
