namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// A single completion item for SQL IntelliSense.
/// </summary>
/// <param name="Label">Display label shown in the completion list.</param>
/// <param name="InsertText">Text inserted when the completion is accepted.</param>
/// <param name="Kind">The classification of this completion item.</param>
/// <param name="Description">Optional description shown in a detail pane.</param>
/// <param name="Detail">Optional short detail text (e.g., attribute type).</param>
/// <param name="SortOrder">Sort priority (lower values appear first).</param>
public record SqlCompletion(
    string Label,
    string InsertText,
    SqlCompletionKind Kind,
    string? Description = null,
    string? Detail = null,
    int SortOrder = 0);
