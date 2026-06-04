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
}
