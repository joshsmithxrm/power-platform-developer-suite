namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the FetchXmlPreviewDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="FetchXml">The FetchXML content displayed.</param>
/// <param name="HasContent">Whether any content is displayed.</param>
public sealed record FetchXmlPreviewDialogState(
    string Title,
    string? FetchXml,
    bool HasContent);
