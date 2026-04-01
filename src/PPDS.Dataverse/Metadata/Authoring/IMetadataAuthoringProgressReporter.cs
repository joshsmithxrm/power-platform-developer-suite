namespace PPDS.Dataverse.Metadata.Authoring;

/// <summary>
/// Lightweight progress reporter for metadata authoring operations.
/// Defined in the Dataverse layer to avoid dependency on PPDS.Cli.
/// </summary>
public interface IMetadataAuthoringProgressReporter
{
    /// <summary>
    /// Reports a phase change during the authoring operation.
    /// </summary>
    /// <param name="phase">The current phase name (e.g., "Validating", "Creating table").</param>
    /// <param name="detail">Optional detail about the phase.</param>
    void ReportPhase(string phase, string? detail = null);

    /// <summary>
    /// Reports an informational message during the authoring operation.
    /// </summary>
    /// <param name="message">The informational message.</param>
    void ReportInfo(string message);
}
