using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Processes file column data after record import.
    /// Uploads binary file data to Dataverse via chunked transfer (4MB blocks).
    /// This runs as Phase 4.5, after M2M relationships and before state transitions are finalized.
    /// </summary>
    public class FileColumnProcessor : IImportPhaseProcessor
    {
        private readonly FileColumnTransferHelper _transferHelper;
        private readonly ILogger<FileColumnProcessor>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileColumnProcessor"/> class.
        /// </summary>
        /// <param name="transferHelper">The file column transfer helper for chunked uploads.</param>
        public FileColumnProcessor(FileColumnTransferHelper transferHelper)
        {
            _transferHelper = transferHelper ?? throw new ArgumentNullException(nameof(transferHelper));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FileColumnProcessor"/> class.
        /// </summary>
        /// <param name="transferHelper">The file column transfer helper for chunked uploads.</param>
        /// <param name="logger">The logger.</param>
        public FileColumnProcessor(FileColumnTransferHelper transferHelper, ILogger<FileColumnProcessor>? logger)
            : this(transferHelper)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public string PhaseName => "File Column Upload";

        /// <inheritdoc />
        public async Task<PhaseResult> ProcessAsync(
            ImportContext context,
            CancellationToken cancellationToken)
        {
            if (context.Data.FileData.Count == 0)
            {
                _logger?.LogDebug("No file column data to process");
                return PhaseResult.Skipped();
            }

            var stopwatch = Stopwatch.StartNew();
            var successCount = 0;
            var failureCount = 0;
            var errors = new List<MigrationError>();

            context.Progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Importing,
                Message = "Uploading file column data..."
            });

            foreach (var (entityName, fileDataList) in context.Data.FileData)
            {
                foreach (var fileData in fileDataList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Map source record ID to target record ID
                    if (!context.IdMappings.TryGetNewId(entityName, fileData.RecordId, out var targetRecordId))
                    {
                        _logger?.LogWarning(
                            "No ID mapping found for file column upload: {Entity}/{RecordId}/{Field}",
                            entityName, fileData.RecordId, fileData.FieldName);

                        if (context.Options.ContinueOnError)
                        {
                            failureCount++;
                            errors.Add(new MigrationError
                            {
                                Phase = MigrationPhase.Importing,
                                EntityLogicalName = entityName,
                                RecordId = fileData.RecordId,
                                Message = $"No ID mapping for file column '{fileData.FieldName}' upload — record may not have been imported"
                            });
                            context.Options.ErrorCallback?.Invoke(errors[^1]);
                            continue;
                        }

                        throw new InvalidOperationException(
                            $"No ID mapping found for {entityName}/{fileData.RecordId}. Cannot upload file column '{fileData.FieldName}'.");
                    }

                    try
                    {
                        await _transferHelper.UploadAsync(
                            entityName,
                            targetRecordId,
                            fileData.FieldName,
                            fileData.Data,
                            fileData.FileName,
                            fileData.MimeType,
                            cancellationToken).ConfigureAwait(false);

                        successCount++;

                        _logger?.LogDebug(
                            "Uploaded file column {Field} for {Entity}/{RecordId} ({Size} bytes)",
                            fileData.FieldName, entityName, targetRecordId, fileData.Data.Length);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogWarning(ex,
                            "Failed to upload file column {Field} for {Entity}/{RecordId}",
                            fileData.FieldName, entityName, targetRecordId);

                        failureCount++;
                        var error = new MigrationError
                        {
                            Phase = MigrationPhase.Importing,
                            EntityLogicalName = entityName,
                            RecordId = fileData.RecordId,
                            Message = $"File column '{fileData.FieldName}' upload failed: {ex.Message}"
                        };
                        errors.Add(error);
                        context.Options.ErrorCallback?.Invoke(error);

                        if (!context.Options.ContinueOnError)
                        {
                            throw;
                        }
                    }
                }
            }

            stopwatch.Stop();
            var totalProcessed = successCount + failureCount;

            _logger?.LogInformation(
                "File column upload complete: {Success} succeeded, {Failed} failed in {Duration}",
                successCount, failureCount, stopwatch.Elapsed);

            context.Progress?.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Importing,
                Message = $"File column upload: {successCount} succeeded, {failureCount} failed"
            });

            return new PhaseResult
            {
                Success = failureCount == 0,
                RecordsProcessed = totalProcessed,
                SuccessCount = successCount,
                FailureCount = failureCount,
                Duration = stopwatch.Elapsed,
                Errors = errors
            };
        }
    }
}
