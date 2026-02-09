namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// Lightweight progress reporter for query plan execution.
/// Defined in the Dataverse layer so it can be used without depending on PPDS.Cli.
/// The CLI's <c>IProgressReporter</c> can wrap/adapt this interface.
/// </summary>
public interface IQueryProgressReporter
{
    /// <summary>
    /// Reports item-level progress during execution.
    /// </summary>
    /// <param name="currentItem">The current item number (1-based).</param>
    /// <param name="totalItems">The total number of items, or 0 if unknown.</param>
    /// <param name="statusMessage">Optional status message.</param>
    void ReportProgress(int currentItem, int totalItems, string? statusMessage = null);

    /// <summary>
    /// Reports a phase change during execution (e.g., "Fetching pages", "Merging results").
    /// </summary>
    /// <param name="phase">The current phase name.</param>
    /// <param name="detail">Optional detail about the phase.</param>
    void ReportPhase(string phase, string? detail = null);
}
