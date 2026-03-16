namespace PPDS.Dataverse.Query;

/// <summary>
/// Per-query execution options that affect how the query is sent to Dataverse.
/// These options map to <c>OrganizationRequest.Parameters</c> keys that modify
/// server-side behavior (e.g., bypassing plugins or flows).
/// </summary>
public sealed record QueryExecutionOptions
{
    /// <summary>
    /// When true, adds <c>BypassCustomPluginExecution = true</c> to the request,
    /// skipping synchronous plugin execution for the query.
    /// Set by the <c>-- ppds:BYPASS_PLUGINS</c> query hint.
    /// </summary>
    public bool BypassPlugins { get; init; }

    /// <summary>
    /// When true, adds <c>SuppressCallbackRegistrationExpanderJob = true</c> to the request,
    /// preventing Power Automate flows from triggering for the query.
    /// Set by the <c>-- ppds:BYPASS_FLOWS</c> query hint.
    /// </summary>
    public bool BypassFlows { get; init; }
}
