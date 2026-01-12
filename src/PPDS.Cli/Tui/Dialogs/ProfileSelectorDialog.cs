using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Views;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for selecting from available authentication profiles.
/// </summary>
internal sealed class ProfileSelectorDialog : Dialog
{
    private readonly IProfileService _profileService;
    private readonly InteractiveSession? _session;
    private readonly ListView _listView;
    private readonly Label _detailLabel;
    private readonly TuiSpinner _spinner;
    private readonly Label _hintLabel;

    private IReadOnlyList<ProfileSummary> _profiles = Array.Empty<ProfileSummary>();
    private bool _createNewSelected;
    private ProfileSummary? _selectedProfile;

    /// <summary>
    /// Gets whether the user selected "Create New Profile".
    /// </summary>
    public bool CreateNewSelected => _createNewSelected;

    /// <summary>
    /// Gets the selected profile, or null if cancelled or create new was selected.
    /// </summary>
    public ProfileSummary? SelectedProfile => _selectedProfile;

    /// <summary>
    /// Gets whether a profile was deleted during this dialog session.
    /// </summary>
    public bool ProfileWasDeleted { get; private set; }

    /// <summary>
    /// Creates a new profile selector dialog.
    /// </summary>
    /// <param name="profileService">The profile service for profile operations.</param>
    /// <param name="session">Optional session for showing profile details dialog.</param>
    public ProfileSelectorDialog(IProfileService profileService, InteractiveSession? session = null) : base("Select Profile")
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _session = session;

        Width = 60;
        Height = 18;
        ColorScheme = TuiColorPalette.Default;

        // Profile list
        var listFrame = new FrameView("Profiles")
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = 10
        };

        _listView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
            AllowsMultipleSelection = false
        };
        _listView.SelectedItemChanged += OnSelectedItemChanged;
        _listView.OpenSelectedItem += OnItemActivated;
        listFrame.Add(_listView);

        // Spinner for loading animation
        _spinner = new TuiSpinner
        {
            X = 1,
            Y = Pos.Bottom(listFrame),
            Width = Dim.Fill() - 2,
            Height = 1
        };

        // Detail label (hidden while loading)
        _detailLabel = new Label
        {
            X = 1,
            Y = Pos.Bottom(listFrame),
            Width = Dim.Fill() - 2,
            Height = 1,
            Text = string.Empty,
            Visible = false
        };

        // Hint label for keyboard shortcuts
        _hintLabel = new Label
        {
            X = 1,
            Y = Pos.Bottom(_detailLabel),
            Width = Dim.Fill() - 2,
            Height = 1,
            Text = "F2 rename | Del delete | Ctrl+D details",
            ColorScheme = TuiColorPalette.StatusBar_Default
        };

        // Buttons
        var selectButton = new Button("_Select")
        {
            X = Pos.Center() - 24,
            Y = Pos.AnchorEnd(1)
        };
        selectButton.Clicked += OnSelectClicked;

        var createButton = new Button("Create _New")
        {
            X = Pos.Center() - 9,
            Y = Pos.AnchorEnd(1)
        };
        createButton.Clicked += OnCreateClicked;

        var deleteButton = new Button("_Delete")
        {
            X = Pos.Center() + 7,
            Y = Pos.AnchorEnd(1)
        };
        deleteButton.Clicked += OnDeleteClicked;

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 20,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () => { Application.RequestStop(); };

        Add(listFrame, _spinner, _detailLabel, _hintLabel, selectButton, createButton, deleteButton, cancelButton);

        // Start spinner while loading
        _spinner.Start("Loading profiles...");

        // Handle keyboard shortcuts
        KeyPress += OnKeyPress;

        // Handle Delete key in list view
        _listView.KeyPress += (e) =>
        {
            if (e.KeyEvent.Key == Key.DeleteChar)
            {
                OnDeleteClicked();
                e.Handled = true;
            }
        };

        // Load profiles asynchronously (fire-and-forget with error handling)
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling via ContinueWith
        _ = LoadProfilesAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    _spinner.Stop();
                    _detailLabel.Text = $"Error: {t.Exception.InnerException?.Message ?? t.Exception.Message}";
                    _detailLabel.Visible = true;
                });
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }

    private async Task LoadProfilesAsync()
    {
        _profiles = await _profileService.GetProfilesAsync();
        UpdateListView();
    }

    private void UpdateListView()
    {
        Application.MainLoop?.Invoke(() =>
        {
            // Stop spinner and show detail label
            _spinner.Stop();
            _detailLabel.Visible = true;

            var items = new List<string>();

            foreach (var profile in _profiles)
            {
                var marker = profile.IsActive ? "*" : " ";
                var envHint = profile.EnvironmentName != null ? $" [{profile.EnvironmentName}]" : "";
                items.Add($"{marker} {profile.DisplayIdentifier} ({profile.Identity}){envHint}");
            }

            _listView.SetSource(items);

            // Select active profile by default
            var activeIndex = _profiles.ToList().FindIndex(p => p.IsActive);
            if (activeIndex >= 0)
            {
                _listView.SelectedItem = activeIndex;
            }

            UpdateDetail();
        });
    }

    private void OnSelectedItemChanged(ListViewItemEventArgs args)
    {
        UpdateDetail();
    }

    private void UpdateDetail()
    {
        if (_listView.SelectedItem < 0 || _listView.SelectedItem >= _profiles.Count)
        {
            _detailLabel.Text = string.Empty;
            return;
        }

        var profile = _profiles[_listView.SelectedItem];
        var method = profile.AuthMethod.ToString();
        var env = profile.EnvironmentUrl ?? "None";
        _detailLabel.Text = $"Method: {method} | Cloud: {profile.Cloud} | Environment: {env}";
    }

    private void OnItemActivated(ListViewItemEventArgs args)
    {
        OnSelectClicked();
    }

    private void OnSelectClicked()
    {
        if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _profiles.Count)
        {
            _selectedProfile = _profiles[_listView.SelectedItem];
            _createNewSelected = false;
            Application.RequestStop();
        }
    }

    private void OnCreateClicked()
    {
        _createNewSelected = true;
        _selectedProfile = null;
        Application.RequestStop();
    }

    private void OnKeyPress(KeyEventEventArgs e)
    {
        if (e.KeyEvent.Key == Key.F2)
        {
            ShowRenameDialog();
            e.Handled = true;
        }
        else if (e.KeyEvent.Key == (Key.CtrlMask | Key.D))
        {
            ShowProfileDetailsDialog();
            e.Handled = true;
        }
    }

    private void ShowProfileDetailsDialog()
    {
        if (_session == null)
        {
            // Session not available - show simple message
            MessageBox.Query("Details", "Profile details require session context.", "OK");
            return;
        }

        // Show the profile details dialog for the active profile.
        // Design note: ProfileDetailsDialog shows the active profile because it needs
        // full AuthProfile data (token expiration, authority, etc.) which is only readily
        // available for the active profile. Showing details for a non-active profile would
        // require additional profile loading logic and potentially re-authentication.
        var dialog = new ProfileDetailsDialog(_session);
        Application.Run(dialog);
    }

    private void ShowRenameDialog()
    {
        if (_listView.SelectedItem < 0 || _listView.SelectedItem >= _profiles.Count)
        {
            return;
        }

        var profile = _profiles[_listView.SelectedItem];
        var currentName = profile.Name ?? string.Empty;

        // Create rename dialog
        using var dialog = new Dialog("Rename Profile")
        {
            Width = 50,
            Height = 8,
            ColorScheme = TuiColorPalette.Default
        };

        var label = new Label("New name:")
        {
            X = 1,
            Y = 1
        };

        var textField = new TextField(currentName)
        {
            X = 12,
            Y = 1,
            Width = Dim.Fill() - 2,
            ColorScheme = TuiColorPalette.TextInput
        };

        var okButton = new Button("_OK")
        {
            X = Pos.Center() - 10,
            Y = Pos.AnchorEnd(1)
        };

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 2,
            Y = Pos.AnchorEnd(1)
        };

        var newName = string.Empty;

        okButton.Clicked += () =>
        {
            var nameFromTextField = textField.Text?.ToString()?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(nameFromTextField))
            {
                MessageBox.ErrorQuery("Validation Error", "Profile name cannot be empty.", "OK");
                return;
            }

            newName = nameFromTextField;
            Application.RequestStop();
        };

        cancelButton.Clicked += () =>
        {
            Application.RequestStop();
        };

        // Allow Enter to submit
        textField.KeyPress += (args) =>
        {
            if (args.KeyEvent.Key == Key.Enter)
            {
                okButton.OnClicked();
                args.Handled = true;
            }
        };

        dialog.Add(label, textField, okButton, cancelButton);
        textField.SetFocus();

        Application.Run(dialog);

        if (!string.IsNullOrEmpty(newName))
        {
            PerformRename(profile, newName);
        }
    }

    private void PerformRename(ProfileSummary profile, string newName)
    {
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling via ContinueWith
        _ = PerformRenameAsync(profile, newName).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                var ex = t.Exception.InnerException;
                var message = ex is PpdsException ppdsEx ? ppdsEx.UserMessage : ex?.Message ?? "Unknown error";
                Application.MainLoop?.Invoke(() =>
                {
                    MessageBox.ErrorQuery("Rename Failed", message, "OK");
                });
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }

    private async Task PerformRenameAsync(ProfileSummary profile, string newName)
    {
        await _profileService.UpdateProfileAsync(profile.DisplayIdentifier, newName);
        await LoadProfilesAsync();

        Application.MainLoop?.Invoke(() =>
        {
            // Re-select the renamed profile to maintain context for the user
            var renamedProfileIndex = _profiles.ToList().FindIndex(p => p.Name == newName);
            if (renamedProfileIndex >= 0)
            {
                _listView.SelectedItem = renamedProfileIndex;
            }

            _detailLabel.Text = $"Renamed to '{newName}'";
        });
    }

    private void OnDeleteClicked()
    {
        if (_listView.SelectedItem < 0 || _listView.SelectedItem >= _profiles.Count)
            return;

        var profile = _profiles[_listView.SelectedItem];

        // Show confirmation dialog with extra warning if deleting active profile
        var activeWarning = profile.IsActive
            ? "This is your active profile. You will be signed out.\n\n"
            : "";
        var result = MessageBox.Query("Confirm Delete",
            $"Delete profile \"{profile.DisplayIdentifier}\"?\n\n" +
            activeWarning +
            "This will remove the profile and its stored credentials.\n" +
            "This action cannot be undone.",
            "Delete", "Cancel");

        if (result == 0) // "Delete" selected
        {
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
            _ = DeleteProfileAsync(profile).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Application.MainLoop?.Invoke(() =>
                    {
                        _detailLabel.Text = $"Error: {t.Exception?.InnerException?.Message ?? "Delete failed"}";
                    });
                }
            }, TaskScheduler.Default);
#pragma warning restore PPDS013
        }
    }

    private async Task DeleteProfileAsync(ProfileSummary profile)
    {
        var deleted = await _profileService.DeleteProfileAsync(profile.DisplayIdentifier);

        if (deleted)
        {
            ProfileWasDeleted = true;
            await LoadProfilesAsync();
        }
    }
}
