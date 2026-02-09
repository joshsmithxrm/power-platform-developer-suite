using System.Runtime.InteropServices;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
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
/// - CertificateStore: Form with App ID, Thumbprint, Tenant, URL (Windows only)
/// - UsernamePassword: Form with Username, Password (ROPC, deprecated)
/// </remarks>
internal sealed class ProfileCreationDialog : TuiDialog, ITuiStateCapture<ProfileCreationDialogState>
{
    private readonly IReadOnlyList<string> _authMethodNames;
    private readonly AuthMethod[] _authMethods;

    private readonly IProfileService _profileService;
    private readonly IEnvironmentService _environmentService;
    private readonly ITuiErrorService? _errorService;
    private readonly InteractiveSession? _session;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;

    private readonly TextField _nameField;
    private readonly RadioGroup _authMethodRadio;
    private readonly TextField _environmentUrlField;

    // SPN fields (Client Secret / Certificate File / Certificate Store)
    private readonly FrameView _spnFrame;
    private readonly TextField _appIdField;
    private readonly TextField _tenantIdField;
    private readonly Label _secretLabel;
    private readonly TextField _clientSecretField;
    private readonly Label _certPathLabel;
    private readonly TextField _certPathField;
    private readonly Label _certPwdLabel;
    private readonly TextField _certPasswordField;
    private readonly Label _thumbprintLabel;
    private readonly TextField _thumbprintField;

    // Username/Password fields
    private readonly FrameView _credFrame;
    private readonly TextField _usernameField;
    private readonly TextField _passwordField;

    private readonly Label _statusLabel;
    private readonly Button _authenticateButton;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private ProfileSummary? _createdProfile;
    private bool _isAuthenticating;

    /// <summary>
    /// Gets the created profile, or null if cancelled.
    /// </summary>
    public ProfileSummary? CreatedProfile => _createdProfile;

    /// <summary>
    /// Gets the environment URL selected after authentication.
    /// This may differ from the profile's stored environment if selected post-auth.
    /// </summary>
    public string? SelectedEnvironmentUrl { get; private set; }

    /// <summary>
    /// Gets the environment display name selected after authentication.
    /// </summary>
    public string? SelectedEnvironmentName { get; private set; }

    /// <summary>
    /// Creates a new profile creation dialog.
    /// </summary>
    /// <param name="profileService">The profile service for creating profiles.</param>
    /// <param name="environmentService">The environment service for discovery.</param>
    /// <param name="deviceCodeCallback">Optional callback for device code display (null uses built-in dialog).</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public ProfileCreationDialog(
        IProfileService profileService,
        IEnvironmentService environmentService,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        InteractiveSession? session = null) : base("Create Profile", session)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _environmentService = environmentService ?? throw new ArgumentNullException(nameof(environmentService));
        _errorService = session?.GetErrorService();
        _session = session;
        _deviceCodeCallback = deviceCodeCallback;

        // Build platform-aware auth method list
        var methods = new List<(string Label, AuthMethod Method)>
        {
            ("Device Code (Interactive)", AuthMethod.DeviceCode),
            ("Browser (Interactive)", AuthMethod.InteractiveBrowser),
            ("Client Secret (Service Principal)", AuthMethod.ClientSecret),
            ("Certificate File (Service Principal)", AuthMethod.CertificateFile),
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            methods.Add(("Certificate Store (Service Principal)", AuthMethod.CertificateStore));
        }

        methods.Add(("Username & Password", AuthMethod.UsernamePassword));

        _authMethods = methods.Select(m => m.Method).ToArray();
        _authMethodNames = methods.Select(m => m.Label).ToArray();
        var radioLabels = methods.Select(m => (NStack.ustring)m.Label).ToArray();

        Width = 70;
        Height = 29;

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
            Text = string.Empty,
            ColorScheme = TuiColorPalette.TextInput
        };

        // Auth method selection (RadioGroup for reliable rendering)
        var methodLabel = new Label("Auth Method:")
        {
            X = 1,
            Y = 3
        };

        _authMethodRadio = new RadioGroup(radioLabels)
        {
            X = 16,
            Y = 3
        };
        _authMethodRadio.SelectedItem = InteractiveBrowserCredentialProvider.IsAvailable() ? 1 : 0;
        _authMethodRadio.SelectedItemChanged += OnAuthMethodChanged;

        // Enter on RadioGroup selects the item (Terminal.Gui only binds Space by default)
        _authMethodRadio.KeyPress += (args) =>
        {
            if (args.KeyEvent.Key == Key.Enter)
            {
                // Simulate Space key to trigger selection
                _authMethodRadio.ProcessKey(new KeyEvent(Key.Space, new KeyModifiers()));
                args.Handled = true;
            }
        };

        // Environment URL (common for all methods) — positioned relative to radio group
        var urlLabel = new Label("Environment URL:")
        {
            X = 1,
            Y = Pos.Bottom(_authMethodRadio) + 1
        };
        _environmentUrlField = new TextField
        {
            X = 17,
            Y = Pos.Bottom(_authMethodRadio) + 1,
            Width = Dim.Fill() - 3,
            Text = string.Empty,
            ColorScheme = TuiColorPalette.TextInput
        };

        // SPN frame (for ClientSecret, CertificateFile, CertificateStore)
        _spnFrame = new FrameView("Service Principal Settings")
        {
            X = 1,
            Y = Pos.Bottom(_environmentUrlField) + 1,
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
            Text = string.Empty,
            ColorScheme = TuiColorPalette.TextInput
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
            Text = string.Empty,
            ColorScheme = TuiColorPalette.TextInput
        };

        // Client Secret field (Y=4, shared slot)
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
            Text = string.Empty,
            ColorScheme = TuiColorPalette.TextInput
        };

        // Certificate File fields (Y=4 + Y=6, shared slot)
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
            Visible = false,
            ColorScheme = TuiColorPalette.TextInput
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
            Visible = false,
            ColorScheme = TuiColorPalette.TextInput
        };

        // Certificate Store thumbprint field (Y=4, shared slot)
        _thumbprintLabel = new Label("Thumbprint:")
        {
            X = 0,
            Y = 4,
            Visible = false
        };
        _thumbprintField = new TextField
        {
            X = 15,
            Y = 4,
            Width = Dim.Fill() - 2,
            Text = string.Empty,
            Visible = false,
            ColorScheme = TuiColorPalette.TextInput
        };

        _spnFrame.Add(appIdLabel, _appIdField, tenantLabel, _tenantIdField,
            _secretLabel, _clientSecretField, _certPathLabel, _certPathField,
            _certPwdLabel, _certPasswordField, _thumbprintLabel, _thumbprintField);

        // Credentials frame (for Username/Password)
        _credFrame = new FrameView("Credentials")
        {
            X = 1,
            Y = Pos.Bottom(_environmentUrlField) + 1,
            Width = Dim.Fill() - 2,
            Height = 6,
            Visible = false
        };

        var usernameLabel = new Label("Username:")
        {
            X = 0,
            Y = 0
        };
        _usernameField = new TextField
        {
            X = 15,
            Y = 0,
            Width = Dim.Fill() - 2,
            Text = string.Empty,
            ColorScheme = TuiColorPalette.TextInput
        };

        var passwordLabel = new Label("Password:")
        {
            X = 0,
            Y = 2
        };
        _passwordField = new TextField
        {
            X = 15,
            Y = 2,
            Width = Dim.Fill() - 2,
            Secret = true,
            Text = string.Empty,
            ColorScheme = TuiColorPalette.TextInput
        };

        _credFrame.Add(usernameLabel, _usernameField, passwordLabel, _passwordField);

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

        Add(nameLabel, _nameField, methodLabel, _authMethodRadio,
            urlLabel, _environmentUrlField,
            _spnFrame, _credFrame, _statusLabel, _authenticateButton, cancelButton);

        // Update UI based on initial selection
        OnAuthMethodChanged(new SelectedItemChangedArgs(_authMethodRadio.SelectedItem, -1));

        // Defer focus to name field until after layout is complete
        Ready += () => _nameField.SetFocus();
    }

    /// <inheritdoc />
    protected override void OnEscapePressed()
    {
        // Don't close if authentication is in progress
        if (!_isAuthenticating)
        {
            base.OnEscapePressed();
        }
    }

    private AuthMethod GetSelectedMethod() => _authMethods[_authMethodRadio.SelectedItem];

    private void OnAuthMethodChanged(SelectedItemChangedArgs args)
    {
        var method = GetSelectedMethod();
        var isSpn = method is AuthMethod.ClientSecret or AuthMethod.CertificateFile or AuthMethod.CertificateStore;
        var isUserPass = method == AuthMethod.UsernamePassword;

        _spnFrame.Visible = isSpn;
        _credFrame.Visible = isUserPass;

        // Toggle SPN field visibility by method
        _secretLabel.Visible = method == AuthMethod.ClientSecret;
        _clientSecretField.Visible = method == AuthMethod.ClientSecret;

        _certPathLabel.Visible = method == AuthMethod.CertificateFile;
        _certPathField.Visible = method == AuthMethod.CertificateFile;
        _certPwdLabel.Visible = method == AuthMethod.CertificateFile;
        _certPasswordField.Visible = method == AuthMethod.CertificateFile;

        _thumbprintLabel.Visible = method == AuthMethod.CertificateStore;
        _thumbprintField.Visible = method == AuthMethod.CertificateStore;

        // Update status text
        _statusLabel.ColorScheme = TuiColorPalette.Default;
        _statusLabel.Text = method switch
        {
            AuthMethod.DeviceCode => "A code will be shown to enter at microsoft.com/devicelogin",
            AuthMethod.InteractiveBrowser => "Your default browser will open for sign-in",
            AuthMethod.ClientSecret => "Requires App ID, Tenant ID, Client Secret, and Environment URL",
            AuthMethod.CertificateFile => "Requires App ID, Tenant ID, Certificate, and Environment URL",
            AuthMethod.CertificateStore => "Requires App ID, Tenant ID, Thumbprint, and Environment URL",
            AuthMethod.UsernamePassword => "Username and password \u2014 may require disabling MFA",
            _ => "Select an authentication method"
        };
    }

    private void OnAuthenticateClicked()
    {
        if (_isAuthenticating)
        {
            MessageBox.ErrorQuery("In Progress", "Authentication is already in progress.", "OK");
            return;
        }

        // Validate inputs
        var method = GetSelectedMethod();
        var isSpn = method is AuthMethod.ClientSecret or AuthMethod.CertificateFile or AuthMethod.CertificateStore;

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
            if (method == AuthMethod.ClientSecret && string.IsNullOrWhiteSpace(_clientSecretField.Text?.ToString()))
            {
                _statusLabel.Text = "Error: Client Secret is required";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
                return;
            }
            if (method == AuthMethod.CertificateFile && string.IsNullOrWhiteSpace(_certPathField.Text?.ToString()))
            {
                _statusLabel.Text = "Error: Certificate path is required";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
                return;
            }
            if (method == AuthMethod.CertificateStore && string.IsNullOrWhiteSpace(_thumbprintField.Text?.ToString()))
            {
                _statusLabel.Text = "Error: Certificate thumbprint is required";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
                return;
            }
        }
        else if (method == AuthMethod.UsernamePassword)
        {
            if (string.IsNullOrWhiteSpace(_usernameField.Text?.ToString()))
            {
                _statusLabel.Text = "Error: Username is required";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
                return;
            }
            if (string.IsNullOrWhiteSpace(_passwordField.Text?.ToString()))
            {
                _statusLabel.Text = "Error: Password is required";
                _statusLabel.ColorScheme = TuiColorPalette.Error;
                return;
            }
        }

        _isAuthenticating = true;
        _authenticateButton.Enabled = false;
        _statusLabel.ColorScheme = TuiColorPalette.Default;

        // Build request
        var request = BuildCreateRequest();

        // Always provide device code callback (needed for Browser's device code fallback too)
        var deviceCallback = _deviceCodeCallback ?? (info =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                // Auto-copy code to clipboard for convenience
                var copied = ClipboardHelper.CopyToClipboard(info.UserCode) ? " (copied!)" : "";

                // MessageBox is safe from MainLoop.Invoke - doesn't start nested event loop
                MessageBox.Query(
                    "Authentication Required",
                    $"Visit: {info.VerificationUrl}\n\n" +
                    $"Enter code: {info.UserCode}{copied}\n\n" +
                    "Complete authentication in browser, then press OK.",
                    "OK");
            });
        });

        // Pre-auth dialog for Browser auth (matches PpdsApplication.cs pattern)
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeAuth = null;
        if (method == AuthMethod.InteractiveBrowser)
        {
            beforeAuth = (dcCallback) =>
            {
                // Terminal.Gui not initialized — default to opening browser directly
                if (Application.MainLoop == null)
                    return PreAuthDialogResult.OpenBrowser;

                var result = PreAuthDialogResult.Cancel;
                using var waitHandle = new ManualResetEventSlim(false);
                Application.MainLoop.Invoke(() =>
                {
                    try
                    {
                        Application.Refresh();
                        var dialog = new PreAuthenticationDialog(dcCallback);
                        Application.Run(dialog);
                        result = dialog.Result;
                    }
                    finally
                    {
                        waitHandle.Set();
                    }
                });
                waitHandle.Wait();
                return result;
            };
        }

        _statusLabel.Text = "Authenticating...";
        Application.Refresh();

        var authTask = CreateProfileAndHandleResultAsync(request, deviceCallback, beforeAuth);
        if (_errorService != null)
            _errorService.FireAndForget(authTask, "CreateProfile");
        else
            _ = authTask.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task CreateProfileAndHandleResultAsync(
        ProfileCreateRequest request,
        Action<DeviceCodeInfo>? deviceCodeCallback,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth)
    {
        try
        {
            var profile = await Task.Run(() => _profileService.CreateProfileAsync(request, deviceCodeCallback, beforeInteractiveAuth, _cts.Token));
            Application.MainLoop?.Invoke(() =>
            {
                _createdProfile = profile;

                // Immediately show environment selector after successful auth (no success message)
                var envDialog = new EnvironmentSelectorDialog(_environmentService, _deviceCodeCallback, _session);
                Application.Run(envDialog);

                // Store selected environment
                if (envDialog.SelectedEnvironment != null)
                {
                    SelectedEnvironmentUrl = envDialog.SelectedEnvironment.Url;
                    SelectedEnvironmentName = envDialog.SelectedEnvironment.DisplayName;
                }
                else if (envDialog.UseManualUrl && !string.IsNullOrWhiteSpace(envDialog.ManualUrl))
                {
                    SelectedEnvironmentUrl = envDialog.ManualUrl;
                    SelectedEnvironmentName = envDialog.ManualUrl;
                }

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
        var method = GetSelectedMethod();

        return new ProfileCreateRequest
        {
            Name = string.IsNullOrWhiteSpace(_nameField.Text?.ToString()) ? null : _nameField.Text.ToString()?.Trim(),
            Environment = string.IsNullOrWhiteSpace(_environmentUrlField.Text?.ToString()) ? null : _environmentUrlField.Text.ToString()?.Trim(),
            AuthMethod = method,
            UseDeviceCode = method == AuthMethod.DeviceCode,
            // SPN fields
            ApplicationId = _appIdField.Text?.ToString()?.Trim(),
            TenantId = _tenantIdField.Text?.ToString()?.Trim(),
            ClientSecret = method == AuthMethod.ClientSecret ? _clientSecretField.Text?.ToString() : null,
            CertificatePath = method == AuthMethod.CertificateFile ? _certPathField.Text?.ToString()?.Trim() : null,
            CertificatePassword = method == AuthMethod.CertificateFile ? _certPasswordField.Text?.ToString() : null,
            CertificateThumbprint = method == AuthMethod.CertificateStore ? _thumbprintField.Text?.ToString()?.Trim() : null,
            // Username/Password fields
            Username = method == AuthMethod.UsernamePassword ? _usernameField.Text?.ToString()?.Trim() : null,
            Password = method == AuthMethod.UsernamePassword ? _passwordField.Text?.ToString() : null,
        };
    }

    /// <inheritdoc />
    public ProfileCreationDialogState CaptureState() => new(
        Title: Title?.ToString() ?? string.Empty,
        ProfileName: _nameField.Text?.ToString() ?? string.Empty,
        SelectedAuthMethod: _authMethodNames[_authMethodRadio.SelectedItem],
        AvailableAuthMethods: _authMethodNames,
        IsCreating: _isAuthenticating,
        ValidationError: null,
        CanCreate: _authenticateButton.Enabled);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
