using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Metadata.Authoring;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs.Metadata;

/// <summary>
/// Dialog for creating a new global choice (option set).
/// </summary>
internal sealed class CreateChoiceDialog : TuiDialog
{
    private readonly TextField _solutionField;
    private readonly TextField _schemaNameField;
    private readonly TextField _displayNameField;
    private readonly TextField _descriptionField;
    private readonly TextField _optionsField;
    private bool _confirmed;

    /// <summary>
    /// Gets the result request, or null if the dialog was cancelled.
    /// </summary>
    public CreateGlobalChoiceRequest? Result => _confirmed ? BuildResult() : null;

    public CreateChoiceDialog(InteractiveSession? session = null)
        : base("Create Global Choice", session)
    {
        Width = 65;
        Height = 16;

        const int fieldX = 20;
        int y = 1;

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
        Add(new Label("Description:") { X = 1, Y = y });
        _descriptionField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_descriptionField);

        y += 2;
        Add(new Label("Options:") { X = 1, Y = y });
        _optionsField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(new Label("(comma-separated)") { X = fieldX, Y = y + 1 });
        Add(_optionsField);

        var okButton = new Button("_OK") { X = Pos.Center() - 8, Y = Pos.AnchorEnd(1) };
        okButton.Clicked += () =>
        {
            if (string.IsNullOrWhiteSpace(_solutionField.Text?.ToString())
                || string.IsNullOrWhiteSpace(_schemaNameField.Text?.ToString())
                || string.IsNullOrWhiteSpace(_displayNameField.Text?.ToString()))
            {
                MessageBox.ErrorQuery("Validation", "Solution, Schema Name, and Display Name are required.", "OK");
                return;
            }
            _confirmed = true;
            Application.RequestStop();
        };

        var cancelButton = new Button("_Cancel") { X = Pos.Center() + 3, Y = Pos.AnchorEnd(1) };
        cancelButton.Clicked += () => Application.RequestStop();

        Add(okButton, cancelButton);
    }

    private CreateGlobalChoiceRequest BuildResult()
    {
        var optionsText = _optionsField.Text?.ToString()?.Trim() ?? "";
        var options = optionsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((label, index) => new OptionDefinition { Label = label, Value = (index + 1) * 100_000 })
            .ToArray();

        return new CreateGlobalChoiceRequest
        {
            SolutionUniqueName = _solutionField.Text?.ToString()?.Trim() ?? "",
            SchemaName = _schemaNameField.Text?.ToString()?.Trim() ?? "",
            DisplayName = _displayNameField.Text?.ToString()?.Trim() ?? "",
            Description = _descriptionField.Text?.ToString()?.Trim() ?? "",
            Options = options
        };
    }
}
