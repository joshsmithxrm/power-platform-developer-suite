using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for creating a new authentication profile with support for all interactive auth methods.
/// </summary>
/// <remarks>
/// Supported auth methods:
/// - DeviceCode: Show URL + code dialog, poll for completion
/// - InteractiveBrowser: Opens system browser for authentication
/// - ClientSecret: Form with App ID, Secret, Tenant, URL
/// - CertificateFile: Form with App ID, Cert Path, Password, Tenant, URL
/// </remarks>
internal sealed class ProfileCreationDialog : Dialog
{
    private readonly IProfileService _profileService;
    private readonly IEnvironmentService _environmentService;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;

    private readonly TextField _nameField;
    private readonly ComboBox _authMethodCombo;
    private readonly TextField _environmentUrlField;
    private readonly Button _discoverButton;

    // SPN fields (Client Secret / Certificate)
    private readonly FrameView _spnFrame;
    private readonly TextField _appIdField;
    private readonly TextField _tenantIdField;
    private readonly Label _secretLabel;
    private readonly TextField _clientSecretField;
    private readonly Label _certPathLabel;
    private readonly TextField _certPathField;
    private readonly Label _certPwdLabel;
    private readonly TextField _certPasswordField;

    private readonly Label _statusLabel;
    private readonly Button _authenticateButton;

    private ProfileSummary? _createdProfile;
    private bool _isAuthenticating;

    /// <summary>
    /// Gets the created profile, or null if cancelled.
    /// </summary>
    public ProfileSummary? CreatedProfile => _createdProfile;

    /// <summary>
    /// Creates a new profile creation dialog.
    /// </summary>
    /// <param name="profileService">The profile service for creating profiles.</param>
    /// <param name="environmentService">The environment service for discovery.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display (null uses built-in dialog).</param>
    public ProfileCreationDialog(
        IProfileService profileService,
        IEnvironmentService environmentService,
        Action<DeviceCodeInfo>? deviceCodeCallback = null) : base("Create Profile")
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _environmentService = environmentService ?? throw new ArgumentNullException(nameof(environmentService));
        _deviceCodeCallback = deviceCodeCallback;

        Width = 70;
        Height = 24;
        ColorScheme = TuiColorPalette.Default;

        // Profile name
        var nameLabel = new Label("Profile Name:")
        {
            X = 1,
            Y = 1
        };
        _nameField = new TextField
        {
            X = 16,
            Y = 1,
            Width = Dim.Fill() - 3,
            Text = string.Empty
        };

        // Auth method dropdown
        var methodLabel = new Label("Auth Method:")
        {
            X = 1,
            Y = 3
        };

        _authMethodCombo = new ComboBox
        {
            X = 16,
            Y = 3,
            Width = Dim.Fill() - 3,
            Height = 5
        };
        _authMethodCombo.SetSource(new[]
        {
            "Device Code (Interactive)",
            "Browser (Interactive)",
            "Client Secret (Service Principal)",
            "Certificate File (Service Principal)"
        });
        _authMethodCombo.SelectedItem = InteractiveBrowserCredentialProvider.IsAvailable() ? 1 : 0;
        _authMethodCombo.SelectedItemChanged += OnAuthMethodChanged;

        // Environment URL (common for all methods)
        var urlLabel = new Label("Environment URL:")
        {
            X = 1,
            Y = 5
        };
        _environmentUrlField = new TextField
        {
            X = 17,
            Y = 5,
            Width = Dim.Fill() - 24,
            Text = string.Empty
        };
        _discoverButton = new Button("Discover...")
        {
            X = Pos.Right(_environmentUrlField) + 1,
            Y = 5
        };
        _discoverButton.Clicked += OnDiscoverClicked;

        // SPN frame (for ClientSecret and CertificateFile)
        _spnFrame = new FrameView("Service Principal Settings")
        {
            X = 1,
            Y = 7,
            Width = Dim.Fill() - 2,
            Height = 8,
            Visible = false
        };

        var appIdLabel = new Label("App ID:")
        {
            X = 0,
            Y = 0
        };
        _appIdField = new TextField
        {
            X = 15,
            Y = 0,
            Width = Dim.Fill() - 2,
            Text = string.Empty
        };

        var tenantLabel = new Label("Tenant ID:")
        {
            X = 0,
            Y = 2
        };
        _tenantIdField = new TextField
        {
            X = 15,
            Y = 2,
            Width = Dim.Fill() - 2,
            Text = string.Empty
        };

        _secretLabel = new Label("Client Secret:")
        {
            X = 0,
            Y = 4
        };
        _clientSecretField = new TextField
        {
            X = 15,
            Y = 4,
            Width = Dim.Fill() - 2,
            Secret = true,
            Text = string.Empty
        };

        _certPathLabel = new Label("Cert Path:")
        {
            X = 0,
            Y = 4,
            Visible = false
        };
        _certPathField = new TextField
        {
            X = 15,
            Y = 4,
            Width = Dim.Fill() - 2,
            Text = string.Empty,
            Visible = false
        };

        _certPwdLabel = new Label("Cert Password:")
        {
            X = 0,
            Y = 6,
            Visible = false
        };
        _certPasswordField = new TextField
        {
            X = 15,
            Y = 6,
            Width = Dim.Fill() - 2,
            Secret = true,
            Text = string.Empty,
            Visible = false
        };

        _spnFrame.Add(appIdLabel, _appIdField, tenantLabel, _tenantIdField,
            _secretLabel, _clientSecretField, _certPathLabel, _certPathField,
            _certPwdLabel, _certPasswordField);

        // Status label
        _statusLabel = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill() - 2,
            Height = 1,
            Text = "Enter profile details and click Start Authentication"
        };

        // Buttons
        _authenticateButton = new Button("Start _Authentication")
        {
            X = Pos.Center() - 18,
            Y = Pos.AnchorEnd(1)
        };
        _authenticateButton.Clicked += OnAuthenticateClicked;

        var cancelButton = new Button("_Cancel")
        {
            X = Pos.Center() + 8,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () =>
        {
            if (!_isAuthenticating)
            {
                Application.RequestStop();
            }
        };

        Add(nameLabel, _nameField, methodLabel, _authMethodCombo,
            urlLabel, _environmentUrlField, _discoverButton,
            _spnFrame, _statusLabel, _authenticateButton, cancelButton);

        // Update UI based on initial selection
        OnAuthMethodChanged(new ListViewItemEventArgs(_authMethodCombo.SelectedItem, null));
    }

    private void OnAuthMethodChanged(ListViewItemEventArgs args)
    {
        var selectedIndex = _authMethodCombo.SelectedItem;
        var isSpn = selectedIndex >= 2; // ClientSecret or CertificateFile
        var isCert = selectedIndex == 3;

        _spnFrame.Visible = isSpn;

        // Toggle visibility of secret vs certificate fields (fix label overlap)
        _secretLabel.Visible = isSpn && !isCert;
        _clientSecretField.Visible = isSpn && !isCert;

        _certPathLabel.Visible = isSpn && isCert;
        _certPathField.Visible = isSpn && isCert;

        _certPwdLabel.Visible = isSpn && isCert;
        _certPasswordField.Visible = isSpn && isCert;

        // Discover button only for interactive methods
        _discoverButton.Enabled = selectedIndex < 2;

        // Update status text
        _statusLabel.Text = selectedIndex switch
        {
            0 => "Device code authentication - a code will be shown to enter at microsoft.com/devicelogin",
            1 => "Browser authentication - your default browser will open for sign-in",
            2 => "Service principal - requires App ID, Tenant ID, Client Secret, and Environment URL",
            3 => "Certificate auth - requires App ID, Tenant ID, Certificate, and Environment URL",
            _ => "Select an authentication method"
        };
    }

    private void OnDiscoverClicked()
    {
        if (_isAuthenticating) return;

        // Show environment selector dialog using provided callback or built-in dialog
        var envDeviceCallback = _deviceCodeCallback ?? ShowDeviceCodeDialog;
        var dialog = new EnvironmentSelectorDialog(_environmentService, envDeviceCallback);
        Application.Run(dialog);

        if (dialog.SelectedEnvironment != null)
        {
            _environmentUrlField.Text = dialog.SelectedEnvironment.Url;
        }
        else if (dialog.UseManualUrl && !string.IsNullOrWhiteSpace(dialog.ManualUrl))
        {
            _environmentUrlField.Text = dialog.ManualUrl;
        }
    }

    private void ShowDeviceCodeDialog(DeviceCodeInfo info)
    {
        // Use the built-in device code dialog
        Application.MainLoop?.Invoke(() =>
        {
            var dialog = new DeviceCodeAuthDialog(info);
            Application.Run(dialog);
        });
    }

    private void OnAuthenticateClicked()
    {
        if (_isAuthenticating)
        {
            MessageBox.ErrorQuery("In Progress", "Authentication is already in progress.", "OK");
            return;
        }

        // Validate inputs
        var selectedIndex = _authMethodCombo.SelectedItem;
        var isSpn = selectedIndex >= 2;
        var isCert = selectedIndex == 3;

        if (isSpn)
        {
            // Validate SPN fields
            if (string.IsNullOrWhiteSpace(_appIdField.Text?.ToString()))
            {
                _statusLabel.Text = "Error: Application ID is required";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
                return;
            }
            if (string.IsNullOrWhiteSpace(_tenantIdField.Text?.ToString()))
            {
                _statusLabel.Text = "Error: Tenant ID is required";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
                return;
            }
            if (string.IsNullOrWhiteSpace(_environmentUrlField.Text?.ToString()))
            {
                _statusLabel.Text = "Error: Environment URL is required for service principals";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
                return;
            }
            if (!isCert && string.IsNullOrWhiteSpace(_clientSecretField.Text?.ToString()))
            {
                _statusLabel.Text = "Error: Client Secret is required";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
                return;
            }
            if (isCert && string.IsNullOrWhiteSpace(_certPathField.Text?.ToString()))
            {
                _statusLabel.Text = "Error: Certificate path is required";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
                return;
            }
        }

        _isAuthenticating = true;
        _authenticateButton.Enabled = false;
        _statusLabel.ColorScheme = TuiColorPalette.Default;

        // Build request
        var request = BuildCreateRequest();

        // Use provided callback if available; otherwise fall back to built-in dialog
        Action<DeviceCodeInfo>? deviceCallback = null;
        if (selectedIndex == 0) // Device Code
        {
            deviceCallback = _deviceCodeCallback ?? (info =>
            {
                Application.MainLoop?.Invoke(() =>
                {
                    var dialog = new DeviceCodeAuthDialog(info);
                    Application.Run(dialog);
                });
            });
        }

        _statusLabel.Text = "Authenticating...";
        Application.Refresh();

#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
        _ = CreateProfileAndHandleResultAsync(request, deviceCallback);
#pragma warning restore PPDS013
    }

    private async Task CreateProfileAndHandleResultAsync(ProfileCreateRequest request, Action<DeviceCodeInfo>? deviceCodeCallback)
    {
        try
        {
            var profile = await _profileService.CreateProfileAsync(request, deviceCodeCallback);
            Application.MainLoop?.Invoke(() =>
            {
                _createdProfile = profile;
                _statusLabel.Text = $"Profile created: {_createdProfile.DisplayIdentifier}";
                _statusLabel.ColorScheme = TuiColorPalette.Success;
                MessageBox.Query("Success", $"Profile '{_createdProfile.DisplayIdentifier}' created successfully!", "OK");
                Application.RequestStop();
            });
        }
        catch (OperationCanceledException)
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = "Authentication was cancelled";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
            });
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                var message = ex is PpdsException ppdsEx ? ppdsEx.UserMessage : ex.Message ?? "Unknown error";
                _statusLabel.Text = $"Error: {message}";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
            });
        }
        finally
        {
            // Reset UI state if the dialog is not closing on success
            if (_createdProfile is null)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    _isAuthenticating = false;
                    _authenticateButton.Enabled = true;
                });
            }
        }
    }

    private ProfileCreateRequest BuildCreateRequest()
    {
        var selectedIndex = _authMethodCombo.SelectedItem;
        var isCert = selectedIndex == 3;

        return new ProfileCreateRequest
        {
            Name = string.IsNullOrWhiteSpace(_nameField.Text?.ToString()) ? null : _nameField.Text.ToString()?.Trim(),
            Environment = string.IsNullOrWhiteSpace(_environmentUrlField.Text?.ToString()) ? null : _environmentUrlField.Text.ToString()?.Trim(),
            UseDeviceCode = selectedIndex == 0,
            // SPN fields
            ApplicationId = _appIdField.Text?.ToString()?.Trim(),
            TenantId = _tenantIdField.Text?.ToString()?.Trim(),
            ClientSecret = isCert ? null : _clientSecretField.Text?.ToString(),
            CertificatePath = isCert ? _certPathField.Text?.ToString()?.Trim() : null,
            CertificatePassword = isCert ? _certPasswordField.Text?.ToString() : null
        };
    }
}
