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
/// <param name="Location">One of the layout values of <see cref="BindingLocations"/>: "&lt;Tab&gt; / &lt;Section&gt;" labels,
/// "&lt;Tab&gt; / Tab header", "&lt;Tab&gt; / Tab footer", "Header" or "Footer".</param>
/// <param name="SectionId">The containing section's <c>id</c>; null outside sections (form or tab header/footer).</param>
/// <param name="ControlIdCaseDiffers">True when the control <c>id</c> also spells the logical name in another case; ids are never rewritten.</param>
public sealed record RepairedBinding(string? ControlId, string OldBinding, string NewBinding, string Location, string? SectionId,
    bool ControlIdCaseDiffers = false)
{
    /// <summary>
    /// How the read-back found this control: <see cref="BindingVerification.ById"/> when its id is present and unique on the
    /// layout, <see cref="BindingVerification.ByPosition"/> when the id is absent or repeated (the control was matched by its
    /// position among the layout-bound controls and its container). Null on a dry run and for read-back-only entries.
    /// </summary>
    public string? VerifiedBy { get; init; }
}

/// <summary>Values of <see cref="RepairedBinding.VerifiedBy"/>.</summary>
public static class BindingVerification
{
    /// <summary>Found by its unique control id in the same container.</summary>
    public const string ById = "id";
    /// <summary>Found by position among the layout-bound controls (id absent or repeated).</summary>
    public const string ByPosition = "position";
}

/// <summary>
/// An entry the repair reports but does not change. A rewritten control's own binding never appears here. A separate reference
/// that belongs to a rewritten control (its custom-control <c>datafieldname</c> parameter, or an event naming it) can appear,
/// with <see cref="OwnerControlRewritten"/> set: that reference was not rewritten, so it now differs in case from the control's binding.
/// </summary>
/// <param name="ControlId">The control <c>id</c> (for an event, its <c>control</c>; for hidden data, its <c>id</c>); null when absent.</param>
/// <param name="Binding">The stored value as read.</param>
/// <param name="Reason">One of <see cref="BindingRepairReasons"/>.</param>
/// <param name="Location">A layout location as on <see cref="RepairedBinding.Location"/>, or "Control descriptions",
/// "Event '&lt;name&gt;'", "Hidden controls" or "Outside form layout" (see <see cref="BindingLocations"/>).</param>
/// <param name="SectionId">The containing section's <c>id</c>; null outside sections.</param>
/// <param name="CanonicalName">The column's logical name when exactly one matches; null for no match or an ambiguous match.</param>
public sealed record UnchangedBinding(string? ControlId, string Binding, string Reason, string Location, string? SectionId, string? CanonicalName)
{
    /// <summary>True when this is a custom-control parameter or event reference of a control whose binding this run rewrote.</summary>
    public bool OwnerControlRewritten { get; init; }
}

/// <summary>Fixed location values used by binding repair. <see cref="TabHeader"/> and <see cref="TabFooter"/> follow a tab label
/// ("&lt;Tab&gt; / Tab header"); section locations are "&lt;Tab&gt; / &lt;Section&gt;" labels.</summary>
public static class BindingLocations
{
    /// <summary>Suffix for a control in a tab header: "&lt;Tab&gt; / Tab header".</summary>
    public const string TabHeader = "Tab header";
    /// <summary>Suffix for a control in a tab footer: "&lt;Tab&gt; / Tab footer".</summary>
    public const string TabFooter = "Tab footer";
    /// <summary>The form header.</summary>
    public const string Header = "Header";
    /// <summary>The form footer.</summary>
    public const string Footer = "Footer";
    /// <summary>A custom-control parameter whose control is not on the form layout.</summary>
    public const string ControlDescriptions = "Control descriptions";
    /// <summary>A <c>hiddencontrols/data</c> entry.</summary>
    public const string HiddenControls = "Hidden controls";
    /// <summary>A <c>cell/control</c> that is not on the form's own layout path (for example inside custom-control parameters).</summary>
    public const string OutsideFormLayout = "Outside form layout";
}

/// <summary>Reasons a bound control is reported but left unchanged by binding repair.</summary>
public static class BindingRepairReasons
{
    /// <summary>The <c>datafieldname</c> matches no column of the form's table, even ignoring case.</summary>
    public const string NoMatchingColumn = "no-matching-column";

    /// <summary>The binding matches more than one column name ignoring case (and none exactly), so the target is ambiguous.</summary>
    public const string AmbiguousColumn = "ambiguous-column";

    /// <summary>The binding is already exact but the control <c>id</c> spells the column's logical name in a different case. Control ids are never rewritten.</summary>
    public const string ControlIdCaseDiffers = "control-id-case-differs";

    /// <summary>A case-variant binding on a control with a <c>relationship</c> or <c>isunbound="true"</c>; its binding may not name a column of this table.</summary>
    public const string RelatedOrUnboundControl = "related-or-unbound-control";

    /// <summary>A case-variant binding whose stored text uses character or entity references; only plain-text values are rewritten.</summary>
    public const string BindingNotPlainText = "binding-not-plain-text";

    /// <summary>A case-variant <c>cell/control</c> binding that is not on the form's own layout path; never rewritten.</summary>
    public const string OutsideFormLayout = "outside-form-layout";

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
/// <param name="Unchanged">Entries reported but not changed.</param>
/// <param name="FormXmlChanged">True when the form XML update was submitted; always false on a dry run.</param>
/// <param name="ReadBackVerified">True when, after the update, the unpublished form was read back and every control this run
/// rewrote was found (by unique id, or by position where the id is absent or repeated; see <see cref="RepairedBinding.VerifiedBy"/>)
/// with the new binding. It says nothing about entries in <see cref="Unchanged"/>. False on a dry run and when nothing was submitted.</param>
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
    bool PublicationRequested)
{
    /// <summary>
    /// Bindings that the read-back form shows as repairable although this run did not rewrite them (for example because
    /// the server changed how a value is spelled). Reported for a follow-up run; not a verification failure. Empty when
    /// nothing was read back.
    /// </summary>
    public IReadOnlyList<RepairedBinding> ReadBackRepairable { get; init; } = [];

    /// <summary>
    /// False when the verified read-back could not be scanned for further repairable bindings, so
    /// <see cref="ReadBackRepairable"/> is empty because nothing is known, not because nothing was found. True otherwise.
    /// </summary>
    public bool ReadBackScanComplete { get; init; } = true;
}
