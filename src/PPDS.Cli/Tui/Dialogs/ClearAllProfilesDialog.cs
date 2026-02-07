using PPDS.Cli.Services.Profile;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for clearing all profiles with strong confirmation.
/// Equivalent to the 'ppds auth clear' CLI command.
/// </summary>
/// <remarks>
/// This is a destructive operation that:
/// - Deletes all saved profiles from profiles.json
/// - Removes all stored credentials from secure store
/// - Clears the MSAL token cache
///
/// Requires typing the profile count to confirm (prevents accidental clicks).
/// </remarks>
internal sealed class ClearAllProfilesDialog : TuiDialog, ITuiStateCapture<ClearAllProfilesDialogState>
{
    private readonly IProfileService _profileService;
    private readonly ITuiErrorService? _errorService;
    private readonly TextField _confirmationField;
    private readonly Button _clearButton;
    private readonly int _profileCount;

    /// <summary>
    /// Gets whether the clear operation was confirmed and completed successfully.
    /// </summary>
    public bool Cleared { get; private set; }

    /// <summary>
    /// Creates a new clear all profiles dialog.
    /// </summary>
    /// <param name="profileService">The profile service.</param>
    /// <param name="profileCount">The number of profiles to be deleted.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public ClearAllProfilesDialog(IProfileService profileService, int profileCount, InteractiveSession? session = null)
        : base("Clear All Profiles", session)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _errorService = session?.GetErrorService();
        _profileCount = profileCount;

        Width = 65;
        Height = 17;

        // Warning header with icon
        var warningLabel = new Label("WARNING: This will delete ALL profiles")
        {
            X = Pos.Center(),
            Y = 1,
            ColorScheme = TuiColorPalette.Error
        };

        // Separator
        var separator = new Label(new string('─', 61))
        {
            X = 1,
            Y = 2
        };

        // Consequences list
        var consequencesLabel = new Label("This action will:")
        {
            X = 2,
            Y = 4
        };

        var profileCountText = _profileCount == 1 ? "1 saved profile" : $"{_profileCount} saved profiles";
        var bulletPoint1 = new Label($"  * Delete all {profileCountText}")
        {
            X = 2,
            Y = 5
        };

        var bulletPoint2 = new Label("  * Remove all stored credentials")
        {
            X = 2,
            Y = 6
        };

        var bulletPoint3 = new Label("  * Clear the token cache")
        {
            X = 2,
            Y = 7
        };

        // Warning about irreversibility
        var irreversibleLabel = new Label("This action CANNOT be undone.")
        {
            X = 2,
            Y = 9,
            ColorScheme = TuiColorPalette.Error
        };

        // Confirmation prompt
        var confirmPromptLabel = new Label($"Type the number of profiles ({_profileCount}) to confirm:")
        {
            X = 2,
            Y = 11
        };

        _confirmationField = new TextField(string.Empty)
        {
            X = 2,
            Y = 12,
            Width = 10,
            ColorScheme = TuiColorPalette.TextInput
        };

        // Buttons
        _clearButton = new Button("Clear All")
        {
            X = Pos.Center() - 12,
            Y = Pos.AnchorEnd(1),
            Enabled = false,
            ColorScheme = TuiColorPalette.Error
        };
        _clearButton.Clicked += OnClearClicked;

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 3,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () => Application.RequestStop();

        // Enable/disable Clear button based on confirmation input
        _confirmationField.TextChanged += (_) =>
        {
            var text = _confirmationField.Text?.ToString()?.Trim() ?? string.Empty;
            _clearButton.Enabled = text == _profileCount.ToString();
        };

        // Allow Enter to submit if confirmation is valid
        _confirmationField.KeyPress += (args) =>
        {
            if (args.KeyEvent.Key == Key.Enter && _clearButton.Enabled)
            {
                OnClearClicked();
                args.Handled = true;
            }
        };

        Add(
            warningLabel,
            separator,
            consequencesLabel,
            bulletPoint1,
            bulletPoint2,
            bulletPoint3,
            irreversibleLabel,
            confirmPromptLabel,
            _confirmationField,
            _clearButton,
            cancelButton
        );

        // Focus on confirmation field
        _confirmationField.SetFocus();
    }

    private void OnClearClicked()
    {
        // Double-check confirmation
        var text = _confirmationField.Text?.ToString()?.Trim() ?? string.Empty;
        if (text != _profileCount.ToString())
        {
            return;
        }

        // Perform the clear operation
        _errorService?.FireAndForget(PerformClearAsync(), "ClearAllProfiles");
    }

    private async Task PerformClearAsync()
    {
        await _profileService.ClearAllAsync(CancellationToken.None);

        Cleared = true;

        Application.MainLoop?.Invoke(() =>
        {
            Application.RequestStop();
        });
    }

    /// <inheritdoc />
    public ClearAllProfilesDialogState CaptureState() => new(
        Title: Title?.ToString() ?? string.Empty,
        WarningMessage: "WARNING: This will delete ALL profiles",
        ProfileCount: _profileCount,
        ConfirmButtonText: "Clear All",
        CancelButtonText: "Cancel");
}
