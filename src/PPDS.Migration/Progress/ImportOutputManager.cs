using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Migration.Import;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Manages streaming output for import operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Produces three output files:
    /// <list type="bullet">
    ///   <item><c>{basePath}.errors.jsonl</c> - JSON Lines format, one error per line (streamed immediately)</item>
    ///   <item><c>{basePath}.progress.log</c> - Human-readable progress log (streamed immediately)</item>
    ///   <item><c>{basePath}.summary.json</c> - Final summary report (written on completion)</item>
    /// </list>
    /// </para>
    /// <para>
    /// Thread Safety: All logging methods are thread-safe and can be called concurrently.
    /// Uses lock-based synchronization for file writes.
    /// </para>
    /// <para>
    /// Errors and progress are flushed immediately to disk, ensuring no data loss on cancellation.
    /// </para>
    /// </remarks>
    public sealed class ImportOutputManager : IAsyncDisposable, IDisposable
    {
        private readonly string _basePath;
        private readonly StreamWriter _errorWriter;
        private readonly StreamWriter _progressWriter;
        private readonly object _errorLock = new();
        private readonly object _progressLock = new();
        private readonly DateTime _startTime;
        private int _errorCount;
        private int _disposed;

        private static readonly JsonSerializerOptions JsonLineOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private static readonly JsonSerializerOptions SummaryJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Converters = { new TimeSpanConverter() }
        };

        /// <summary>
        /// Creates a new output manager for the specified base path.
        /// </summary>
        /// <param name="basePath">
        /// Base path for output files. Files will be created as:
        /// {basePath}.errors.jsonl, {basePath}.progress.log, {basePath}.summary.json
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when basePath is null.</exception>
        public ImportOutputManager(string basePath)
        {
            _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
            _startTime = DateTime.UtcNow;

            // Create directory if needed
            var directory = Path.GetDirectoryName(basePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Open streams with immediate flush (AutoFlush = true)
            _errorWriter = new StreamWriter($"{basePath}.errors.jsonl", append: false)
            {
                AutoFlush = true
            };

            _progressWriter = new StreamWriter($"{basePath}.progress.log", append: false)
            {
                AutoFlush = true
            };

            // Write header to progress log
            _progressWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] Import started");
        }

        /// <summary>
        /// Gets the path to the errors file (.errors.jsonl).
        /// </summary>
        public string ErrorsPath => $"{_basePath}.errors.jsonl";

        /// <summary>
        /// Gets the path to the progress log file (.progress.log).
        /// </summary>
        public string ProgressPath => $"{_basePath}.progress.log";

        /// <summary>
        /// Gets the path to the summary file (.summary.json).
        /// </summary>
        public string SummaryPath => $"{_basePath}.summary.json";

        /// <summary>
        /// Gets the number of errors logged so far.
        /// </summary>
        public int ErrorCount => _errorCount;

        /// <summary>
        /// Logs an error immediately to the errors file.
        /// Thread-safe.
        /// </summary>
        /// <param name="error">The error to log.</param>
        public void LogError(MigrationError error)
        {
            if (error == null) return;
            ThrowIfDisposed();

            var line = new ErrorLine
            {
                Entity = error.EntityLogicalName,
                RecordId = error.RecordId,
                RecordIndex = error.RecordIndex,
                ErrorCode = error.ErrorCode,
                Message = error.Message,
                Pattern = ErrorReportWriter.ClassifyError(error.Message),
                Timestamp = error.Timestamp,
                Diagnostics = error.Diagnostics
            };

            // Remove empty pattern
            if (string.IsNullOrEmpty(line.Pattern))
            {
                line.Pattern = null;
            }

            var json = JsonSerializer.Serialize(line, JsonLineOptions);

            lock (_errorLock)
            {
                _errorWriter.WriteLine(json);
                Interlocked.Increment(ref _errorCount);
            }
        }

        /// <summary>
        /// Logs a progress message immediately to the progress log.
        /// Thread-safe.
        /// </summary>
        /// <param name="message">The progress message.</param>
        public void LogProgress(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            ThrowIfDisposed();

            lock (_progressLock)
            {
                _progressWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            }
        }

        /// <summary>
        /// Logs entity progress with rate information.
        /// Thread-safe.
        /// </summary>
        /// <param name="entityLogicalName">Entity logical name.</param>
        /// <param name="processed">Records processed.</param>
        /// <param name="total">Total records.</param>
        /// <param name="recordsPerSecond">Current throughput.</param>
        public void LogEntityProgress(string entityLogicalName, int processed, int total, double recordsPerSecond)
        {
            ThrowIfDisposed();

            var percent = total > 0 ? (processed * 100 / total) : 0;
            var message = $"[{entityLogicalName}] {processed:N0}/{total:N0} ({percent}%) @ {recordsPerSecond:F0} rec/s";

            lock (_progressLock)
            {
                _progressWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            }
        }

        /// <summary>
        /// Logs an entity-level error summary.
        /// Thread-safe.
        /// </summary>
        /// <param name="entityLogicalName">Entity logical name.</param>
        /// <param name="failedRecords">Number of failed records.</param>
        /// <param name="errorSummary">Brief error description.</param>
        public void LogEntityError(string entityLogicalName, int failedRecords, string errorSummary)
        {
            ThrowIfDisposed();

            var message = $"[{entityLogicalName}] ERROR: {errorSummary} ({failedRecords} records)";

            lock (_progressLock)
            {
                _progressWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            }
        }

        /// <summary>
        /// Logs the start of a tier.
        /// Thread-safe.
        /// </summary>
        /// <param name="tierNumber">Tier number.</param>
        /// <param name="entityNames">Entities in this tier.</param>
        public void LogTierStart(int tierNumber, string[] entityNames)
        {
            ThrowIfDisposed();

            var entities = string.Join(", ", entityNames);
            var message = $"Processing tier {tierNumber}: {entities}";

            lock (_progressLock)
            {
                _progressWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            }
        }

        /// <summary>
        /// Writes the final summary to the summary file.
        /// Should be called on import completion (success, failure, or cancellation).
        /// </summary>
        /// <param name="result">The import result.</param>
        /// <param name="sourceFile">Source data file path.</param>
        /// <param name="targetEnvironment">Target environment URL.</param>
        /// <param name="executionContext">Optional execution context.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task WriteSummaryAsync(
            ImportResult result,
            string? sourceFile,
            string? targetEnvironment,
            ImportExecutionContext? executionContext = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            var summary = new ImportSummary
            {
                GeneratedAt = DateTime.UtcNow,
                SourceFile = sourceFile,
                TargetEnvironment = targetEnvironment,
                ExecutionContext = executionContext,
                Success = result.Success,
                Duration = result.Duration,
                TiersProcessed = result.TiersProcessed,
                RecordsImported = result.RecordsImported,
                RecordsUpdated = result.RecordsUpdated,
                RecordsFailed = _errorCount,
                RecordsPerSecond = result.RecordsPerSecond,
                ErrorPatterns = DetectErrorPatterns(result)
            };

            var json = JsonSerializer.Serialize(summary, SummaryJsonOptions);
            await File.WriteAllTextAsync($"{_basePath}.summary.json", json, cancellationToken);

            // Log completion to progress log
            var status = result.Success ? "completed successfully" : "completed with errors";
            LogProgress($"Import {status}. Duration: {result.Duration:hh\\:mm\\:ss}, Imported: {result.RecordsImported:N0}, Failed: {_errorCount:N0}");
        }

        /// <summary>
        /// Detects error patterns from the import result.
        /// </summary>
        private static System.Collections.Generic.Dictionary<string, int> DetectErrorPatterns(ImportResult result)
        {
            var patterns = new System.Collections.Generic.Dictionary<string, int>();

            foreach (var error in result.Errors)
            {
                var pattern = ErrorReportWriter.ClassifyError(error.Message);
                if (!string.IsNullOrEmpty(pattern))
                {
                    patterns.TryGetValue(pattern, out var count);
                    patterns[pattern] = count + 1;
                }
            }

            return patterns;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed != 0)
                throw new ObjectDisposedException(nameof(ImportOutputManager));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _errorWriter.Dispose();
                _progressWriter.Dispose();
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await _errorWriter.DisposeAsync();
                await _progressWriter.DisposeAsync();
            }
        }

        /// <summary>
        /// Error line for JSON Lines format.
        /// </summary>
        private sealed class ErrorLine
        {
            [JsonPropertyName("entity")]
            public string? Entity { get; set; }

            [JsonPropertyName("recordId")]
            public Guid? RecordId { get; set; }

            [JsonPropertyName("recordIndex")]
            public int? RecordIndex { get; set; }

            [JsonPropertyName("errorCode")]
            public int? ErrorCode { get; set; }

            [JsonPropertyName("message")]
            public string Message { get; set; } = string.Empty;

            [JsonPropertyName("pattern")]
            public string? Pattern { get; set; }

            [JsonPropertyName("timestamp")]
            public DateTime Timestamp { get; set; }

            [JsonPropertyName("diagnostics")]
            public System.Collections.Generic.IReadOnlyList<Dataverse.BulkOperations.BatchFailureDiagnostic>? Diagnostics { get; set; }
        }

        /// <summary>
        /// Summary report structure.
        /// </summary>
        private sealed class ImportSummary
        {
            [JsonPropertyName("generatedAt")]
            public DateTime GeneratedAt { get; set; }

            [JsonPropertyName("sourceFile")]
            public string? SourceFile { get; set; }

            [JsonPropertyName("targetEnvironment")]
            public string? TargetEnvironment { get; set; }

            [JsonPropertyName("executionContext")]
            public ImportExecutionContext? ExecutionContext { get; set; }

            [JsonPropertyName("success")]
            public bool Success { get; set; }

            [JsonPropertyName("duration")]
            public TimeSpan Duration { get; set; }

            [JsonPropertyName("tiersProcessed")]
            public int TiersProcessed { get; set; }

            [JsonPropertyName("recordsImported")]
            public int RecordsImported { get; set; }

            [JsonPropertyName("recordsUpdated")]
            public int RecordsUpdated { get; set; }

            [JsonPropertyName("recordsFailed")]
            public int RecordsFailed { get; set; }

            [JsonPropertyName("recordsPerSecond")]
            public double RecordsPerSecond { get; set; }

            [JsonPropertyName("errorPatterns")]
            public System.Collections.Generic.Dictionary<string, int>? ErrorPatterns { get; set; }
        }

        /// <summary>
        /// Custom TimeSpan converter for JSON serialization.
        /// Serializes as ISO 8601 duration string.
        /// </summary>
        private sealed class TimeSpanConverter : JsonConverter<TimeSpan>
        {
            public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var value = reader.GetString();
                return value != null ? TimeSpan.Parse(value) : TimeSpan.Zero;
            }

            public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString(@"hh\:mm\:ss\.fff"));
            }
        }
    }
}
