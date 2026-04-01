using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Metadata.Authoring;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs.Metadata;

/// <summary>
/// Dialog for creating a new column (attribute) on a Dataverse table.
/// </summary>
internal sealed class CreateColumnDialog : TuiDialog
{
    private readonly TextField _solutionField;
    private readonly TextField _entityField;
    private readonly TextField _schemaNameField;
    private readonly TextField _displayNameField;
    private readonly TextField _descriptionField;
    private readonly RadioGroup _typeGroup;

    // Type-specific fields
    private readonly Label _maxLengthLabel;
    private readonly TextField _maxLengthField;

    private bool _confirmed;

    private static readonly SchemaColumnType[] ColumnTypeValues = Enum.GetValues<SchemaColumnType>();
    private static readonly string[] ColumnTypeNames = ColumnTypeValues.Select(v => v.ToString()).ToArray();

    /// <summary>
    /// Gets the result request, or null if the dialog was cancelled.
    /// </summary>
    public CreateColumnRequest? Result => _confirmed ? BuildResult() : null;

    public CreateColumnDialog(string entityLogicalName, InteractiveSession? session = null)
        : base("Create Column", session)
    {
        Width = 65;
        Height = 22;

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
        Add(new Label("Description:") { X = 1, Y = y });
        _descriptionField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_descriptionField);

        y += 2;
        Add(new Label("Type:") { X = 1, Y = y });
        var typeNames = ColumnTypeNames.Select(n => (NStack.ustring)n).ToArray();
        _typeGroup = new RadioGroup(typeNames) { X = fieldX, Y = y, SelectedItem = 0 };
        Add(_typeGroup);

        // Type-specific: MaxLength (for String/Memo)
        int typeSpecificY = y + ColumnTypeNames.Length + 1;
        _maxLengthLabel = new Label("Max Length:") { X = 1, Y = typeSpecificY, Visible = true };
        _maxLengthField = new TextField("100") { X = fieldX, Y = typeSpecificY, Width = 10, ColorScheme = TuiColorPalette.TextInput, Visible = true };
        Add(_maxLengthLabel, _maxLengthField);

        _typeGroup.SelectedItemChanged += _ => UpdateTypeSpecificFields();
        UpdateTypeSpecificFields();

        // Expand height to fit all type options
        Height = typeSpecificY + 4;

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

    private void UpdateTypeSpecificFields()
    {
        var selectedType = ColumnTypeValues[_typeGroup.SelectedItem];
        bool isText = selectedType is SchemaColumnType.String or SchemaColumnType.Memo;
        _maxLengthLabel.Visible = isText;
        _maxLengthField.Visible = isText;
    }

    private CreateColumnRequest BuildResult()
    {
        var selectedType = ColumnTypeValues[_typeGroup.SelectedItem];
        var request = new CreateColumnRequest
        {
            SolutionUniqueName = _solutionField.Text?.ToString()?.Trim() ?? "",
            EntityLogicalName = _entityField.Text?.ToString()?.Trim() ?? "",
            SchemaName = _schemaNameField.Text?.ToString()?.Trim() ?? "",
            DisplayName = _displayNameField.Text?.ToString()?.Trim() ?? "",
            Description = _descriptionField.Text?.ToString()?.Trim() ?? "",
            ColumnType = selectedType
        };

        if (selectedType is SchemaColumnType.String or SchemaColumnType.Memo)
        {
            var maxLenText = _maxLengthField.Text?.ToString()?.Trim();
            if (int.TryParse(maxLenText, out var maxLen) && maxLen > 0)
            {
                request.MaxLength = maxLen;
            }
        }

        return request;
    }
}
