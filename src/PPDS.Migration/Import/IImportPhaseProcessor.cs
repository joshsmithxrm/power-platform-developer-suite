using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Processes a phase of the import pipeline.
    /// Each phase operates on the shared <see cref="ImportContext"/> and returns a <see cref="PhaseResult"/>.
    /// </summary>
    public interface IImportPhaseProcessor
    {
        /// <summary>
        /// Gets the name of this phase for logging and progress reporting.
        /// </summary>
        string PhaseName { get; }

        /// <summary>
        /// Executes this phase of the import.
        /// </summary>
        /// <param name="context">The shared import context containing data, options, and state.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result of this phase.</returns>
        Task<PhaseResult> ProcessAsync(
            ImportContext context,
            CancellationToken cancellationToken);
    }
}
