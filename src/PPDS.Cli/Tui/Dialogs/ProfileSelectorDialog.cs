using PPDS.Cli.Services.Profile;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for selecting from available authentication profiles.
/// </summary>
internal sealed class ProfileSelectorDialog : Dialog
{
    private readonly IProfileService _profileService;
    private readonly ListView _listView;
    private readonly Label _detailLabel;

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
    /// Creates a new profile selector dialog.
    /// </summary>
    public ProfileSelectorDialog(IProfileService profileService) : base("Select Profile")
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));

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

        // Detail label
        _detailLabel = new Label
        {
            X = 1,
            Y = Pos.Bottom(listFrame),
            Width = Dim.Fill() - 2,
            Height = 2,
            Text = string.Empty
        };

        // Buttons
        var selectButton = new Button("_Select")
        {
            X = Pos.Center() - 20,
            Y = Pos.AnchorEnd(1)
        };
        selectButton.Clicked += OnSelectClicked;

        var createButton = new Button("Create _New")
        {
            X = Pos.Center() - 5,
            Y = Pos.AnchorEnd(1)
        };
        createButton.Clicked += OnCreateClicked;

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 12,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () => { Application.RequestStop(); };

        Add(listFrame, _detailLabel, selectButton, createButton, cancelButton);

        // Load profiles asynchronously (fire-and-forget with error handling)
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling via ContinueWith
        _ = LoadProfilesAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    _detailLabel.Text = $"Error: {t.Exception.InnerException?.Message ?? t.Exception.Message}";
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
}
