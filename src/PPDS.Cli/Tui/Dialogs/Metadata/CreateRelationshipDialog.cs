using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Metadata.Authoring;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs.Metadata;

/// <summary>
/// Dialog for creating a new relationship between Dataverse tables.
/// </summary>
internal sealed class CreateRelationshipDialog : TuiDialog
{
    private readonly TextField _solutionField;
    private readonly TextField _referencedEntityField;
    private readonly TextField _referencingEntityField;
    private readonly TextField _schemaNameField;
    private readonly RadioGroup _typeGroup;
    private readonly Label _lookupSchemaLabel;
    private readonly TextField _lookupSchemaNameField;
    private readonly Label _lookupDisplayLabel;
    private readonly TextField _lookupDisplayNameField;
    private bool _confirmed;

    /// <summary>
    /// Gets the type of relationship selected (0 = OneToMany, 1 = ManyToMany).
    /// </summary>
    public int SelectedRelationshipType => _typeGroup.SelectedItem;

    /// <summary>
    /// Gets the OneToMany result, or null if cancelled or wrong type.
    /// </summary>
    public CreateOneToManyRequest? OneToManyResult => _confirmed && _typeGroup.SelectedItem == 0 ? BuildOneToMany() : null;

    /// <summary>
    /// Gets the ManyToMany result, or null if cancelled or wrong type.
    /// </summary>
    public CreateManyToManyRequest? ManyToManyResult => _confirmed && _typeGroup.SelectedItem == 1 ? BuildManyToMany() : null;

    public CreateRelationshipDialog(string currentEntity, InteractiveSession? session = null)
        : base("Create Relationship", session)
    {
        Width = 65;
        Height = 20;

        const int fieldX = 22;
        int y = 1;

        Add(new Label("Solution:") { X = 1, Y = y });
        _solutionField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_solutionField);

        y += 2;
        Add(new Label("Type:") { X = 1, Y = y });
        _typeGroup = new RadioGroup(new NStack.ustring[] { "One-to-Many (1:N)", "Many-to-Many (N:N)" })
        {
            X = fieldX,
            Y = y,
            SelectedItem = 0
        };
        Add(_typeGroup);

        y += 3;
        Add(new Label("Referenced Entity:") { X = 1, Y = y });
        _referencedEntityField = new TextField(currentEntity) { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_referencedEntityField);

        y += 2;
        Add(new Label("Referencing Entity:") { X = 1, Y = y });
        _referencingEntityField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_referencingEntityField);

        y += 2;
        Add(new Label("Schema Name:") { X = 1, Y = y });
        _schemaNameField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_schemaNameField);

        y += 2;
        _lookupSchemaLabel = new Label("Lookup Schema:") { X = 1, Y = y };
        _lookupSchemaNameField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_lookupSchemaLabel, _lookupSchemaNameField);

        y += 2;
        _lookupDisplayLabel = new Label("Lookup Display:") { X = 1, Y = y };
        _lookupDisplayNameField = new TextField("") { X = fieldX, Y = y, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_lookupDisplayLabel, _lookupDisplayNameField);

        _typeGroup.SelectedItemChanged += _ => UpdateLookupVisibility();
        UpdateLookupVisibility();

        var okButton = new Button("_OK") { X = Pos.Center() - 8, Y = Pos.AnchorEnd(1) };
        okButton.Clicked += () =>
        {
            if (string.IsNullOrWhiteSpace(_solutionField.Text?.ToString())
                || string.IsNullOrWhiteSpace(_schemaNameField.Text?.ToString())
                || string.IsNullOrWhiteSpace(_referencingEntityField.Text?.ToString()))
            {
                MessageBox.ErrorQuery("Validation", "Solution, Schema Name, and Referencing Entity are required.", "OK");
                return;
            }
            _confirmed = true;
            Application.RequestStop();
        };

        var cancelButton = new Button("_Cancel") { X = Pos.Center() + 3, Y = Pos.AnchorEnd(1) };
        cancelButton.Clicked += () => Application.RequestStop();

        Add(okButton, cancelButton);
    }

    private void UpdateLookupVisibility()
    {
        bool isOneToMany = _typeGroup.SelectedItem == 0;
        _lookupSchemaLabel.Visible = isOneToMany;
        _lookupSchemaNameField.Visible = isOneToMany;
        _lookupDisplayLabel.Visible = isOneToMany;
        _lookupDisplayNameField.Visible = isOneToMany;
    }

    private CreateOneToManyRequest BuildOneToMany()
    {
        return new CreateOneToManyRequest
        {
            SolutionUniqueName = _solutionField.Text?.ToString()?.Trim() ?? "",
            ReferencedEntity = _referencedEntityField.Text?.ToString()?.Trim() ?? "",
            ReferencingEntity = _referencingEntityField.Text?.ToString()?.Trim() ?? "",
            SchemaName = _schemaNameField.Text?.ToString()?.Trim() ?? "",
            LookupSchemaName = _lookupSchemaNameField.Text?.ToString()?.Trim() ?? "",
            LookupDisplayName = _lookupDisplayNameField.Text?.ToString()?.Trim() ?? ""
        };
    }

    private CreateManyToManyRequest BuildManyToMany()
    {
        return new CreateManyToManyRequest
        {
            SolutionUniqueName = _solutionField.Text?.ToString()?.Trim() ?? "",
            Entity1LogicalName = _referencedEntityField.Text?.ToString()?.Trim() ?? "",
            Entity2LogicalName = _referencingEntityField.Text?.ToString()?.Trim() ?? "",
            SchemaName = _schemaNameField.Text?.ToString()?.Trim() ?? ""
        };
    }
}
