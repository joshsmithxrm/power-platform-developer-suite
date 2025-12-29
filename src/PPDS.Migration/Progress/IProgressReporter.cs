using System;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Interface for reporting migration progress.
    /// </summary>
    public interface IProgressReporter
    {
        /// <summary>
        /// Gets or sets the operation name for completion messages (e.g., "Export", "Import", "Copy").
        /// </summary>
        string OperationName { get; set; }

        /// <summary>
        /// Reports a progress update.
        /// </summary>
        /// <param name="args">The progress event data.</param>
        void Report(ProgressEventArgs args);

        /// <summary>
        /// Reports operation completion.
        /// </summary>
        /// <param name="result">The migration result.</param>
        void Complete(MigrationResult result);

        /// <summary>
        /// Reports an error.
        /// </summary>
        /// <param name="exception">The exception that occurred.</param>
        /// <param name="context">Optional context about what was happening.</param>
        void Error(Exception exception, string? context = null);

        /// <summary>
        /// Resets the progress reporter for a new operation phase.
        /// Restarts the internal stopwatch and clears any cached state.
        /// Use this between phases (e.g., between export and import in a copy operation).
        /// </summary>
        void Reset();
    }
}
