using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs.Metadata;

/// <summary>
/// Generic property editor dialog for updating a single property value.
/// </summary>
internal sealed class EditPropertyDialog : TuiDialog
{
    private readonly TextField _valueField;
    private bool _confirmed;

    /// <summary>
    /// Gets the updated value, or null if cancelled.
    /// </summary>
    public string? UpdatedValue => _confirmed ? _valueField.Text?.ToString()?.Trim() : null;

    public EditPropertyDialog(string fieldName, string currentValue, InteractiveSession? session = null)
        : base("Edit Property", session)
    {
        Width = 60;
        Height = 8;

        Add(new Label($"{fieldName}:") { X = 1, Y = 1 });
        _valueField = new TextField(currentValue) { X = 1, Y = 2, Width = Dim.Fill(2), ColorScheme = TuiColorPalette.TextInput };
        Add(_valueField);

        var okButton = new Button("_OK") { X = Pos.Center() - 8, Y = Pos.AnchorEnd(1) };
        okButton.Clicked += () =>
        {
            _confirmed = true;
            Application.RequestStop();
        };

        var cancelButton = new Button("_Cancel") { X = Pos.Center() + 3, Y = Pos.AnchorEnd(1) };
        cancelButton.Clicked += () => Application.RequestStop();

        Add(okButton, cancelButton);
    }
}
