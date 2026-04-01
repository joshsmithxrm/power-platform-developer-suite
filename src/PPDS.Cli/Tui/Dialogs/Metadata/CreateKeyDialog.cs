using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Metadata.Authoring;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs.Metadata;

/// <summary>
/// Dialog for creating an alternate key on a Dataverse table.
/// </summary>
internal sealed class CreateKeyDialog : TuiDialog
{
    private readonly TextField _solutionField;
    private readonly TextField _entityField;
    private readonly TextField _schemaNameField;
    private readonly TextField _displayNameField;
    private readonly TextField _attributesField;
    private bool _confirmed;

    /// <summary>
    /// Gets the result request, or null if the dialog was cancelled.
    /// </summary>
    public CreateKeyRequest? Result => _confirmed ? BuildResult() : null;

    public CreateKeyDialog(string entityLogicalName, InteractiveSession? session = null)
        : base("Create Alternate Key", session)
    {
        Width = 65;
        Height = 16;

        const int fieldX = 20;
        int y = 1;

        Add(new Label("Entity:") { X = 1, Y = y });
        _entityField = new TextField(entityLogicalName) { X = fieldX, Y = y, Width = Dim.Fill(2), Enabled = false, ColorScheme = TuiColorPalette.TextInput };
        Add(_entityField);

        y += 2;
        Add(new Label("Solution:") { X = 1, Y = y });
        _solutionField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_solutionField);

        y += 2;
        Add(new Label("Schema Name:") { X = 1, Y = y });
        _schemaNameField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_schemaNameField);

        y += 2;
        Add(new Label("Display Name:") { X = 1, Y = y });
        _displayNameField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_displayNameField);

        y += 2;
        Add(new Label("Attributes:") { X = 1, Y = y });
        _attributesField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(new Label("(comma-separated)") { X = fieldX, Y = y + 1 });
        Add(_attributesField);

        var okButton = new Button("_OK") { X = Pos.Center() - 8, Y = Pos.AnchorEnd(1) };
        okButton.Clicked += () =>
        {
            if (string.IsNullOrWhiteSpace(_solutionField.Text?.ToString())
                || string.IsNullOrWhiteSpace(_schemaNameField.Text?.ToString())
                || string.IsNullOrWhiteSpace(_attributesField.Text?.ToString()))
            {
                MessageBox.ErrorQuery("Validation", "Solution, Schema Name, and Attributes are required.", "OK");
                return;
            }
            _confirmed = true;
            Application.RequestStop();
        };

        var cancelButton = new Button("_Cancel") { X = Pos.Center() + 3, Y = Pos.AnchorEnd(1) };
        cancelButton.Clicked += () => Application.RequestStop();

        Add(okButton, cancelButton);
    }

    private CreateKeyRequest BuildResult()
    {
        var attributesText = _attributesField.Text?.ToString()?.Trim() ?? "";
        var attributes = attributesText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new CreateKeyRequest
        {
            SolutionUniqueName = _solutionField.Text?.ToString()?.Trim() ?? "",
            EntityLogicalName = _entityField.Text?.ToString()?.Trim() ?? "",
            SchemaName = _schemaNameField.Text?.ToString()?.Trim() ?? "",
            DisplayName = _displayNameField.Text?.ToString()?.Trim() ?? "",
            KeyAttributes = attributes
        };
    }
}
