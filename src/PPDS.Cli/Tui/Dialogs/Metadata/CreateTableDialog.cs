using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Metadata.Authoring;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs.Metadata;

/// <summary>
/// Dialog for creating a new Dataverse table (entity).
/// </summary>
internal sealed class CreateTableDialog : TuiDialog
{
    private readonly TextField _solutionField;
    private readonly TextField _schemaNameField;
    private readonly TextField _displayNameField;
    private readonly TextField _pluralDisplayNameField;
    private readonly TextField _descriptionField;
    private readonly RadioGroup _ownershipGroup;
    private bool _confirmed;

    /// <summary>
    /// Gets the result request, or null if the dialog was cancelled.
    /// </summary>
    public CreateTableRequest? Result => _confirmed ? BuildResult() : null;

    public CreateTableDialog(InteractiveSession? session = null)
        : base("Create Table", session)
    {
        Width = 65;
        Height = 18;

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
        Add(new Label("Plural Name:") { X = 1, Y = y });
        _pluralDisplayNameField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_pluralDisplayNameField);

        y += 2;
        Add(new Label("Ownership:") { X = 1, Y = y });
        _ownershipGroup = new RadioGroup(new NStack.ustring[] { "User Owned", "Organization Owned" })
        {
            X = fieldX,
            Y = y,
            SelectedItem = 0
        };
        Add(_ownershipGroup);

        y += 3;
        Add(new Label("Description:") { X = 1, Y = y });
        _descriptionField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_descriptionField);

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

    private CreateTableRequest BuildResult()
    {
        return new CreateTableRequest
        {
            SolutionUniqueName = _solutionField.Text?.ToString()?.Trim() ?? "",
            SchemaName = _schemaNameField.Text?.ToString()?.Trim() ?? "",
            DisplayName = _displayNameField.Text?.ToString()?.Trim() ?? "",
            PluralDisplayName = _pluralDisplayNameField.Text?.ToString()?.Trim() ?? "",
            Description = _descriptionField.Text?.ToString()?.Trim() ?? "",
            OwnershipType = _ownershipGroup.SelectedItem == 0 ? "UserOwned" : "OrganizationOwned"
        };
    }
}
