using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Progress reporter that writes JSON lines to a TextWriter.
    /// Used for CLI and VS Code extension integration.
    /// </summary>
    public class JsonProgressReporter : IProgressReporter
    {
        private readonly TextWriter _writer;
        private readonly JsonSerializerOptions _jsonOptions;
        private int _lastReportedProgress;
        private string? _lastEntity;

        /// <inheritdoc />
        public string OperationName { get; set; } = "Operation";

        /// <summary>
        /// Gets or sets the minimum interval between progress reports (in records).
        /// Default is 100 to avoid flooding output.
        /// </summary>
        public int ReportInterval { get; set; } = 100;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonProgressReporter"/> class.
        /// </summary>
        /// <param name="writer">The text writer to output JSON lines to.</param>
        public JsonProgressReporter(TextWriter writer)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            };
        }

        /// <inheritdoc />
        public void Report(ProgressEventArgs args)
        {
            // Throttle reporting to avoid excessive output
            if (!ShouldReport(args))
            {
                return;
            }

            var output = new
            {
                phase = args.Phase.ToString().ToLowerInvariant(),
                entity = args.Entity,
                field = args.Field,
                relationship = args.Relationship,
                tier = args.TierNumber,
                current = args.Current,
                total = args.Total,
                rps = args.RecordsPerSecond.HasValue ? Math.Round(args.RecordsPerSecond.Value, 1) : (double?)null,
                message = args.Message,
                timestamp = args.Timestamp.ToString("O")
            };

            WriteLine(output);
            _lastReportedProgress = args.Current;
            _lastEntity = args.Entity;
        }

        /// <inheritdoc />
        public void Complete(MigrationResult result)
        {
            var output = new
            {
                phase = "complete",
                operation = OperationName,
                duration = result.Duration.ToString(),
                recordsProcessed = result.RecordsProcessed,
                successCount = result.SuccessCount,
                failureCount = result.FailureCount,
                rps = Math.Round(result.RecordsPerSecond, 1),
                success = result.Success,
                timestamp = DateTime.UtcNow.ToString("O")
            };

            WriteLine(output);
        }

        /// <inheritdoc />
        public void Error(Exception exception, string? context = null)
        {
            // Redact any potential connection strings in exception messages
            var safeMessage = PPDS.Dataverse.Security.ConnectionStringRedactor.RedactExceptionMessage(exception.Message);

            var output = new
            {
                phase = "error",
                message = safeMessage,
                context,
                timestamp = DateTime.UtcNow.ToString("O")
            };

            WriteLine(output);
        }

        /// <inheritdoc />
        public void Reset()
        {
            _lastReportedProgress = 0;
            _lastEntity = null;
        }

        private bool ShouldReport(ProgressEventArgs args)
        {
            // Always report phase changes, completion, and new entities
            if (args.Phase == MigrationPhase.Analyzing ||
                args.Phase == MigrationPhase.Complete ||
                args.Entity != _lastEntity)
            {
                return true;
            }

            // Always report completion of an entity
            if (args.Current == args.Total)
            {
                return true;
            }

            // Throttle intermediate progress
            return args.Current - _lastReportedProgress >= ReportInterval;
        }

        private void WriteLine(object data)
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            _writer.WriteLine(json);
            _writer.Flush();
        }
    }
}
