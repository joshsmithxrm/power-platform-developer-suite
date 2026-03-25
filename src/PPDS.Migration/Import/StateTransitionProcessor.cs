using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Processes Phase 3 of the import pipeline: applying state transitions
    /// collected during Phase 1 entity import.
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
        /// <param name="connectionPool">The connection pool for Dataverse operations.</param>
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
            var transitions = context.StateTransitions;
            if (transitions.Count == 0)
            {
                _logger?.LogInformation("No state transitions to process");
                return PhaseResult.Skipped();
            }

            var stopwatch = Stopwatch.StartNew();
            var successCount = 0;
            var failureCount = 0;
            var errors = new List<MigrationError>();
            var entityNames = transitions.GetEntityNames().ToList();
            var totalTransitions = transitions.Count;
            var processed = 0;

            context.Progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.ProcessingStateTransitions,
                Message = $"Processing {totalTransitions} state transitions across {entityNames.Count} entities..."
            });

            foreach (var entityName in entityNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entityTransitions = transitions.GetTransitions(entityName);

                foreach (var transition in entityTransitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    processed++;

                    try
                    {
                        // Resolve mapped record ID
                        var targetId = transition.RecordId;
                        if (context.IdMappings.TryGetNewId(entityName, transition.RecordId, out var mappedId))
                        {
                            targetId = mappedId;
                        }

                        await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                        // Check if record is already closed (AC-16)
                        if (await IsRecordClosedAsync(client, entityName, targetId).ConfigureAwait(false))
                        {
                            _logger?.LogDebug("Skipping already-closed {Entity}/{Id}", entityName, targetId);
                            successCount++; // Count as success - already in desired state
                            continue;
                        }

                        if (transition.SdkMessage == null)
                        {
                            // Default path: SetStateRequest (AC-06)
                            var request = new OrganizationRequest("SetState")
                            {
                                ["EntityMoniker"] = new EntityReference(entityName, targetId),
                                ["State"] = new OptionSetValue(transition.StateCode),
                                ["Status"] = new OptionSetValue(transition.StatusCode)
                            };
                            await client.ExecuteAsync(request).ConfigureAwait(false);
                        }
                        else
                        {
                            // Specialized SDK message (AC-07 through AC-15)
                            var request = new OrganizationRequest(transition.SdkMessage);
                            if (transition.MessageData != null)
                            {
                                foreach (var kvp in transition.MessageData)
                                {
                                    request[kvp.Key] = kvp.Value;
                                }
                            }
                            await client.ExecuteAsync(request).ConfigureAwait(false);
                        }

                        successCount++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failureCount++;
                        _logger?.LogWarning(ex, "State transition failed for {Entity}/{Id}", entityName, transition.RecordId);
                        errors.Add(new MigrationError
                        {
                            Phase = MigrationPhase.ProcessingStateTransitions,
                            EntityLogicalName = entityName,
                            RecordId = transition.RecordId,
                            Message = ex.Message
                        });
                    }

                    if (processed % 50 == 0)
                    {
                        context.Progress?.Report(new ProgressEventArgs
                        {
                            Phase = MigrationPhase.ProcessingStateTransitions,
                            Current = processed,
                            Total = totalTransitions,
                            Message = $"State transitions: {processed}/{totalTransitions}"
                        });
                    }
                }
            }

            stopwatch.Stop();
            _logger?.LogInformation("State transitions complete: {Success} succeeded, {Failed} failed in {Duration}",
                successCount, failureCount, stopwatch.Elapsed);

            return new PhaseResult
            {
                Success = failureCount == 0,
                RecordsProcessed = successCount + failureCount,
                SuccessCount = successCount,
                FailureCount = failureCount,
                Duration = stopwatch.Elapsed,
                Errors = errors
            };
        }

        /// <summary>
        /// Checks if a record is already in a closed/non-active state.
        /// </summary>
        /// <param name="client">The Dataverse client.</param>
        /// <param name="entityName">The entity logical name.</param>
        /// <param name="recordId">The record ID to check.</param>
        /// <returns>True if the record is already closed (statecode != 0), false otherwise.</returns>
        private static async Task<bool> IsRecordClosedAsync(
            IPooledClient client, string entityName, Guid recordId)
        {
            try
            {
                var request = new RetrieveRequest
                {
                    Target = new EntityReference(entityName, recordId),
                    ColumnSet = new ColumnSet("statecode")
                };
                var response = (RetrieveResponse)await client.ExecuteAsync(request).ConfigureAwait(false);
                var stateCode = response.Entity.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? 0;
                return stateCode != 0;
            }
            catch
            {
                return false; // If we cannot check, proceed with the transition
            }
        }
    }
}
