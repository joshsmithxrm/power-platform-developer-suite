namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Error codes for form service operations.
/// </summary>
public static class FormErrorCodes
{
    /// <summary>The specified entity logical name was not found in Dataverse.</summary>
    public const string EntityNotFound = "Forms.EntityNotFound";

    /// <summary>No form matching the given name and entity was found.</summary>
    public const string FormNotFound = "Forms.FormNotFound";

    /// <summary>No tab matching the given label was found in the form.</summary>
    public const string TabNotFound = "Forms.TabNotFound";

    /// <summary>No section matching the given label was found in the form.</summary>
    public const string SectionNotFound = "Forms.SectionNotFound";

    /// <summary>The specified column does not exist on the entity.</summary>
    public const string ColumnNotFound = "Forms.ColumnNotFound";

    /// <summary>A reorder would silently copy a field that is only placed in another section.</summary>
    public const string FieldInAnotherSection = "Forms.FieldInAnotherSection";

    /// <summary>The order is incomplete, repeated, or has an invalid anchor.</summary>
    public const string InvalidFieldOrder = "Forms.InvalidFieldOrder";

    /// <summary>A selected section contains multiple controls for a field.</summary>
    public const string AmbiguousField = "Forms.AmbiguousField";

    /// <summary>The requested edit cannot safely preserve the authored layout.</summary>
    public const string UnsupportedLayout = "Forms.UnsupportedLayout";

    /// <summary>The source section has no selected field to move.</summary>
    public const string FieldNotInSection = "Forms.FieldNotInSection";

    /// <summary>A move would replace or duplicate an existing target control.</summary>
    public const string FieldAlreadyInTarget = "Forms.FieldAlreadyInTarget";

    /// <summary>The column's attribute type is not supported for form controls (no classid mapping).</summary>
    public const string UnsupportedColumnType = "Forms.UnsupportedColumnType";

    /// <summary>The specified savedqueries record (view) was not found.</summary>
    public const string ViewNotFound = "Forms.ViewNotFound";

    /// <summary>The max-rows value is outside the allowed range of 2–250.</summary>
    public const string InvalidMaxRows = "Forms.InvalidMaxRows";

    /// <summary>The formxml failed XSD validation or contains non-brace-format GUIDs.</summary>
    public const string InvalidFormXml = "Forms.InvalidFormXml";

    /// <summary>The formxml contains duplicate id or labelid attribute values.</summary>
    public const string DuplicateGuid = "Forms.DuplicateGuid";

    /// <summary>The form's iscustomizable flag is false; mutations are silently ignored by Dataverse.</summary>
    public const string FormNotCustomizable = "Forms.FormNotCustomizable";

    /// <summary>A binding repair was submitted but reading the form back did not confirm it.</summary>
    public const string RepairNotVerified = "Forms.RepairNotVerified";

    /// <summary>A binding repair could not be planned (nothing was written) and the failure has no more specific code.</summary>
    public const string RepairFailed = "Forms.RepairFailed";

    /// <summary>A binding repair's form XML update did not complete; whether it was applied is unknown.</summary>
    public const string RepairWriteOutcomeUnknown = "Forms.RepairWriteOutcomeUnknown";

    /// <summary>The organisation service answered a binding repair's form XML update with a fault; that request did not change the form.</summary>
    public const string RepairWriteRejected = "Forms.RepairWriteRejected";

    /// <summary>An explicitly requested solution association failed after the form XML step.</summary>
    public const string SolutionAssociationFailed = "Forms.SolutionAssociationFailed";

    /// <summary>An explicitly requested publication failed after the form XML step.</summary>
    public const string PublicationFailed = "Forms.PublicationFailed";

    /// <summary>A tab with the same generated name already exists in the form.</summary>
    public const string DuplicateTabName = "Forms.DuplicateTabName";

    /// <summary>A section with the same generated name already exists in the form.</summary>
    public const string DuplicateSectionName = "Forms.DuplicateSectionName";

    /// <summary>The sub-grid's generated name (<c>subgrid_&lt;label&gt;</c>) is already used in the form: by another sub-grid, or as the <c>id</c> of any other control (field controls included).</summary>
    public const string DuplicateSubgridName = "Forms.DuplicateSubgridName";
}
