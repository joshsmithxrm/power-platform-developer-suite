namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Repairs control bindings (<c>datafieldname</c>) that equal a column's logical name ignoring case but differ in case (#1527).
/// <see cref="DryRun"/> plans the repair without acknowledgement, XML update, solution association or publication, and
/// cannot be combined with <see cref="SolutionUniqueName"/> or <see cref="Publish"/>. A supplied
/// <see cref="ConfirmEnvironment"/> must match the resolved environment, on a dry run too.
/// </summary>
public sealed record RepairBindingsRequest(
    string EntityLogicalName,
    string FormName,
    bool DryRun = false,
    string? SolutionUniqueName = null,
    bool Publish = false,
    string? ConfirmEnvironment = null);

/// <summary>A control whose <c>datafieldname</c> was submitted (or, on a dry run, would be) rewritten to the column's logical name.</summary>
/// <param name="ControlId">The control <c>id</c>, unchanged; null when the control has none.</param>
/// <param name="OldBinding">The stored <c>datafieldname</c> before the repair.</param>
/// <param name="NewBinding">The column's logical name from metadata.</param>
/// <param name="Location">"Tab / Section" labels, "Header" or "Footer".</param>
/// <param name="SectionId">The containing section's <c>id</c>; null outside sections (form or tab header/footer).</param>
/// <param name="ControlIdCaseDiffers">True when the control <c>id</c> also spells the logical name in another case; ids are never rewritten.</param>
public sealed record RepairedBinding(string? ControlId, string OldBinding, string NewBinding, string Location, string? SectionId,
    bool ControlIdCaseDiffers = false);

/// <summary>An entry the repair reports but does not change. A rewritten control never appears here.</summary>
/// <param name="ControlId">The control <c>id</c> (for an event, its <c>control</c>; for hidden data, its <c>id</c>); null when absent.</param>
/// <param name="Binding">The stored value as read.</param>
/// <param name="Reason">One of <see cref="BindingRepairReasons"/>.</param>
/// <param name="Location">"Tab / Section" labels, "Header" or "Footer".</param>
/// <param name="SectionId">The containing section's <c>id</c>; null outside sections.</param>
/// <param name="CanonicalName">The column's logical name when one matches; null for <see cref="BindingRepairReasons.NoMatchingColumn"/>.</param>
public sealed record UnchangedBinding(string? ControlId, string Binding, string Reason, string Location, string? SectionId, string? CanonicalName);

/// <summary>Reasons a bound control is reported but left unchanged by binding repair.</summary>
public static class BindingRepairReasons
{
    /// <summary>The <c>datafieldname</c> matches no column of the form's table, even ignoring case.</summary>
    public const string NoMatchingColumn = "no-matching-column";

    /// <summary>The binding is already exact but the control <c>id</c> spells the column's logical name in a different case. Control ids are never rewritten.</summary>
    public const string ControlIdCaseDiffers = "control-id-case-differs";

    /// <summary>A case-variant binding on a control with a <c>relationship</c> or <c>isunbound="true"</c>; its binding may not name a column of this table.</summary>
    public const string RelatedOrUnboundControl = "related-or-unbound-control";

    /// <summary>A case-variant binding whose stored text uses character or entity references; only plain-text values are rewritten.</summary>
    public const string BindingNotPlainText = "binding-not-plain-text";

    /// <summary>A custom-control <c>datafieldname</c> parameter in <c>controlDescriptions</c> spells a column name in a different case.</summary>
    public const string CustomControlParameterCaseDiffers = "custom-control-parameter-case-differs";

    /// <summary>A form event's <c>attribute</c> spells a column name in a different case.</summary>
    public const string EventAttributeCaseDiffers = "event-attribute-case-differs";

    /// <summary>A <c>hiddencontrols/data</c> entry's <c>datafieldname</c> spells a column name in a different case.</summary>
    public const string HiddenDataCaseDiffers = "hidden-data-case-differs";

    /// <summary>True for reasons that describe a case-variant column name still on the form after the repair.</summary>
    public static bool IsRemainingCaseVariant(string reason) => reason != NoMatchingColumn;
}

/// <summary>Outcome of binding repair.</summary>
/// <param name="FormId">The resolved form ID.</param>
/// <param name="FormName">The resolved form name.</param>
/// <param name="DryRun">True when nothing was written or requested.</param>
/// <param name="Repaired">Controls whose binding was (or would be) rewritten, in document order.</param>
/// <param name="Unchanged">Controls reported but not changed, in document order.</param>
/// <param name="FormXmlChanged">True when the form XML update was submitted; always false on a dry run.</param>
/// <param name="ReadBackVerified">True when, after the update, the unpublished form was read back and no control binding
/// differed from its column's logical name only by case. False on a dry run and when nothing was submitted.</param>
/// <param name="SolutionAssociationRequested">True when the explicit solution association request completed.</param>
/// <param name="PublicationRequested">True when the explicit publication request completed.</param>
public sealed record RepairBindingsResult(
    Guid FormId,
    string FormName,
    bool DryRun,
    IReadOnlyList<RepairedBinding> Repaired,
    IReadOnlyList<UnchangedBinding> Unchanged,
    bool FormXmlChanged,
    bool ReadBackVerified,
    bool SolutionAssociationRequested,
    bool PublicationRequested);
