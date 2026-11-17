namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Repairs control bindings (<c>datafieldname</c>) that equal a column's logical name ignoring case but differ in case (#1527).
/// <see cref="DryRun"/> plans the repair without confirmation, XML update, solution association or publication.
/// </summary>
public sealed record RepairBindingsRequest(
    string EntityLogicalName,
    string FormName,
    bool DryRun = false,
    string? SolutionUniqueName = null,
    bool Publish = false);

/// <summary>A control whose <c>datafieldname</c> was (or, on a dry run, would be) rewritten to the column's logical name.</summary>
/// <param name="ControlId">The control <c>id</c>, unchanged; null when the control has none.</param>
/// <param name="OldBinding">The stored <c>datafieldname</c> before the repair.</param>
/// <param name="NewBinding">The column's logical name from metadata.</param>
/// <param name="Location">"Tab / Section" labels, "Header" or "Footer".</param>
/// <param name="SectionId">The containing section's <c>id</c>; null in the header and footer.</param>
public sealed record RepairedBinding(string? ControlId, string OldBinding, string NewBinding, string Location, string? SectionId);

/// <summary>A bound control the repair reports but does not change.</summary>
/// <param name="ControlId">The control <c>id</c>; null when the control has none.</param>
/// <param name="Binding">The stored <c>datafieldname</c> as read (before any repair of the same control).</param>
/// <param name="Reason">One of <see cref="BindingRepairReasons"/>.</param>
/// <param name="Location">"Tab / Section" labels, "Header" or "Footer".</param>
/// <param name="SectionId">The containing section's <c>id</c>; null in the header and footer.</param>
/// <param name="CanonicalName">The column's logical name when one matches; null for <see cref="BindingRepairReasons.NoMatchingColumn"/>.</param>
public sealed record UnchangedBinding(string? ControlId, string Binding, string Reason, string Location, string? SectionId, string? CanonicalName);

/// <summary>Reasons a bound control is reported but left unchanged by binding repair.</summary>
public static class BindingRepairReasons
{
    /// <summary>The <c>datafieldname</c> matches no column of the form's table, even ignoring case.</summary>
    public const string NoMatchingColumn = "no-matching-column";

    /// <summary>The control <c>id</c> spells the column's logical name in a different case. Control ids are never rewritten.</summary>
    public const string ControlIdCaseDiffers = "control-id-case-differs";
}

/// <summary>Outcome of binding repair.</summary>
/// <param name="FormId">The resolved form ID.</param>
/// <param name="FormName">The resolved form name.</param>
/// <param name="DryRun">True when nothing was written or requested.</param>
/// <param name="Repaired">Controls whose binding was (or would be) rewritten, in document order.</param>
/// <param name="Unchanged">Controls reported but not changed, in document order.</param>
/// <param name="FormXmlChanged">True when the form XML update was submitted; always false on a dry run.</param>
/// <param name="SolutionAssociationRequested">True when the explicit solution association request completed.</param>
/// <param name="PublicationRequested">True when the explicit publication request completed.</param>
public sealed record RepairBindingsResult(
    Guid FormId,
    string FormName,
    bool DryRun,
    IReadOnlyList<RepairedBinding> Repaired,
    IReadOnlyList<UnchangedBinding> Unchanged,
    bool FormXmlChanged,
    bool SolutionAssociationRequested,
    bool PublicationRequested);
