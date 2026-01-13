using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for displaying detailed profile information.
/// Equivalent to the 'ppds auth who' CLI command.
/// </summary>
internal sealed class ProfileDetailsDialog : TuiDialog, ITuiStateCapture<ProfileDetailsDialogState>
{
    private readonly InteractiveSession _session;
    private readonly ITuiErrorService _errorService;
    private readonly Label _profileNameLabel;
    private readonly Label _identityLabel;
    private readonly Label _authMethodLabel;
    private readonly Label _cacheTypeLabel;
    private readonly Label _cloudLabel;
    private readonly Label _tenantLabel;
    private readonly Label _objectIdLabel;
    private readonly Label _puidLabel;
    private readonly Label _authorityLabel;
    private readonly Label _tokenStatusLabel;
    private readonly Label _createdLabel;
    private readonly Label _lastUsedLabel;
    private readonly Label _environmentLabel;
    private readonly Label _environmentUrlLabel;

    /// <summary>
    /// Creates a new profile details dialog.
    /// </summary>
    /// <param name="session">The interactive session for accessing profile data.</param>
    public ProfileDetailsDialog(InteractiveSession session) : base("Profile Details", session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _errorService = session.GetErrorService();

        Width = 68;
        Height = 23; // Increased for ObjectId, PUID, and Cache Type fields

        const int labelWidth = 14;
        const int valueX = 16;

        // Profile name (header)
        var profileHeaderLabel = new Label("Profile:")
        {
            X = 1,
            Y = 1,
            Width = labelWidth
        };
        _profileNameLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 1,
            Width = Dim.Fill() - 2
        };

        // Separator
        var separator = new Label(new string('─', 64))
        {
            X = 1,
            Y = 2
        };

        // Identity
        var identityHeaderLabel = new Label("Identity:")
        {
            X = 1,
            Y = 3,
            Width = labelWidth
        };
        _identityLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 3,
            Width = Dim.Fill() - 2
        };

        // Auth Method
        var authMethodHeaderLabel = new Label("Auth Method:")
        {
            X = 1,
            Y = 4,
            Width = labelWidth
        };
        _authMethodLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 4,
            Width = Dim.Fill() - 2
        };

        // Cache Type (token storage type)
        var cacheTypeHeaderLabel = new Label("Cache Type:")
        {
            X = 1,
            Y = 5,
            Width = labelWidth
        };
        _cacheTypeLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 5,
            Width = Dim.Fill() - 2
        };

        // Cloud
        var cloudHeaderLabel = new Label("Cloud:")
        {
            X = 1,
            Y = 6,
            Width = labelWidth
        };
        _cloudLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 6,
            Width = Dim.Fill() - 2
        };

        // Tenant
        var tenantHeaderLabel = new Label("Tenant:")
        {
            X = 1,
            Y = 7,
            Width = labelWidth
        };
        _tenantLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 7,
            Width = Dim.Fill() - 2
        };

        // Object ID (Entra ID Object Id)
        var objectIdHeaderLabel = new Label("Object Id:")
        {
            X = 1,
            Y = 8,
            Width = labelWidth
        };
        _objectIdLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 8,
            Width = Dim.Fill() - 2
        };

        // PUID
        var puidHeaderLabel = new Label("PUID:")
        {
            X = 1,
            Y = 9,
            Width = labelWidth
        };
        _puidLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 9,
            Width = Dim.Fill() - 2
        };

        // Authority
        var authorityHeaderLabel = new Label("Authority:")
        {
            X = 1,
            Y = 10,
            Width = labelWidth
        };
        _authorityLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 10,
            Width = Dim.Fill() - 2
        };

        // Token Status
        var tokenHeaderLabel = new Label("Token Status:")
        {
            X = 1,
            Y = 12,
            Width = labelWidth
        };
        _tokenStatusLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 12,
            Width = Dim.Fill() - 2
        };

        // Created
        var createdHeaderLabel = new Label("Created:")
        {
            X = 1,
            Y = 13,
            Width = labelWidth
        };
        _createdLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 13,
            Width = Dim.Fill() - 2
        };

        // Last Used
        var lastUsedHeaderLabel = new Label("Last Used:")
        {
            X = 1,
            Y = 14,
            Width = labelWidth
        };
        _lastUsedLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 14,
            Width = Dim.Fill() - 2
        };

        // Environment
        var envHeaderLabel = new Label("Environment:")
        {
            X = 1,
            Y = 16,
            Width = labelWidth
        };
        _environmentLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 16,
            Width = Dim.Fill() - 2
        };

        // URL
        var urlHeaderLabel = new Label("URL:")
        {
            X = 1,
            Y = 17,
            Width = labelWidth
        };
        _environmentUrlLabel = new Label(string.Empty)
        {
            X = valueX,
            Y = 17,
            Width = Dim.Fill() - 2
        };

        // Buttons
        var refreshButton = new Button("_Refresh")
        {
            X = Pos.Center() - 10,
            Y = Pos.AnchorEnd(1)
        };
        refreshButton.Clicked += OnRefreshClicked;

        var closeButton = new Button("_Close")
        {
            X = Pos.Center() + 3,
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        Add(
            profileHeaderLabel, _profileNameLabel,
            separator,
            identityHeaderLabel, _identityLabel,
            authMethodHeaderLabel, _authMethodLabel,
            cacheTypeHeaderLabel, _cacheTypeLabel,
            cloudHeaderLabel, _cloudLabel,
            tenantHeaderLabel, _tenantLabel,
            objectIdHeaderLabel, _objectIdLabel,
            puidHeaderLabel, _puidLabel,
            authorityHeaderLabel, _authorityLabel,
            tokenHeaderLabel, _tokenStatusLabel,
            createdHeaderLabel, _createdLabel,
            lastUsedHeaderLabel, _lastUsedLabel,
            envHeaderLabel, _environmentLabel,
            urlHeaderLabel, _environmentUrlLabel,
            refreshButton, closeButton
        );

        // Load profile data asynchronously
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling via ContinueWith
        _ = LoadProfileAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                _errorService.ReportError("Failed to load profile details", t.Exception, "ProfileDetails");
                Application.MainLoop?.Invoke(() =>
                {
                    _profileNameLabel.Text = "Error loading profile (see F12 for details)";
                });
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }

    private async Task LoadProfileAsync()
    {
        var store = _session.GetProfileStore();
        var collection = await store.LoadAsync(CancellationToken.None);
        var profile = collection.ActiveProfile;

        // Query MSAL for current token state (if environment is bound)
        CachedTokenInfo? tokenInfo = null;
        if (profile?.Environment != null && !string.IsNullOrEmpty(profile.Environment.Url))
        {
            try
            {
                using var provider = CredentialProviderFactory.Create(profile);
                tokenInfo = await provider.GetCachedTokenInfoAsync(profile.Environment.Url, CancellationToken.None);
            }
            catch
            {
                // Ignore errors - token info will be null
            }
        }

        Application.MainLoop?.Invoke(() =>
        {
            if (profile == null)
            {
                _profileNameLabel.Text = "(No active profile)";
                _identityLabel.Text = "-";
                _authMethodLabel.Text = "-";
                _cacheTypeLabel.Text = "-";
                _cloudLabel.Text = "-";
                _tenantLabel.Text = "-";
                _authorityLabel.Text = "-";
                _tokenStatusLabel.Text = "-";
                _tokenStatusLabel.ColorScheme = TuiColorPalette.Default;
                _createdLabel.Text = "-";
                _lastUsedLabel.Text = "-";
                _environmentLabel.Text = "-";
                _environmentUrlLabel.Text = "-";
                return;
            }

            UpdateDisplay(profile, tokenInfo);
        });
    }

    private void UpdateDisplay(AuthProfile profile, CachedTokenInfo? tokenInfo)
    {
        // Profile name
        _profileNameLabel.Text = profile.DisplayIdentifier;

        // Identity (username or app ID)
        _identityLabel.Text = profile.IdentityDisplay;

        // Auth method
        _authMethodLabel.Text = profile.AuthMethod.ToString();

        // Cache type (token storage)
        _cacheTypeLabel.Text = TokenCacheDetector.GetCacheType().ToString();

        // Cloud
        _cloudLabel.Text = profile.Cloud.ToString();

        // Tenant
        _tenantLabel.Text = profile.TenantId ?? "(not specified)";

        // Object ID (Entra ID Object Id)
        _objectIdLabel.Text = profile.ObjectId ?? "(not available)";

        // PUID
        _puidLabel.Text = profile.Puid ?? "(not available)";

        // Authority
        var authority = profile.Authority ?? CloudEndpoints.GetAuthorityUrl(profile.Cloud, profile.TenantId);
        _authorityLabel.Text = TruncateUrl(authority, 48);

        // Token status with color
        UpdateTokenStatus(profile, tokenInfo);

        // Created
        _createdLabel.Text = profile.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        // Last used
        _lastUsedLabel.Text = profile.LastUsedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "(never)";

        // Environment
        if (profile.HasEnvironment)
        {
            _environmentLabel.Text = profile.Environment!.DisplayName;
            _environmentUrlLabel.Text = TruncateUrl(profile.Environment.Url, 48);
        }
        else
        {
            _environmentLabel.Text = "(no environment selected)";
            _environmentUrlLabel.Text = "-";
        }
    }

    private void UpdateTokenStatus(AuthProfile profile, CachedTokenInfo? tokenInfo)
    {
        if (tokenInfo == null)
        {
            _tokenStatusLabel.Text = profile.HasEnvironment ? "Unknown" : "(no environment bound)";
            _tokenStatusLabel.ColorScheme = TuiColorPalette.Default;
            return;
        }

        if (tokenInfo.IsExpired)
        {
            // Expired
            _tokenStatusLabel.Text = "Expired";
            _tokenStatusLabel.ColorScheme = TuiColorPalette.Error;
        }
        else
        {
            var remaining = tokenInfo.ExpiresOn - DateTimeOffset.UtcNow;
            string timeRemaining;

            if (remaining.TotalDays >= 1)
            {
                timeRemaining = $"{(int)remaining.TotalDays} day(s)";
            }
            else if (remaining.TotalHours >= 1)
            {
                timeRemaining = $"{(int)remaining.TotalHours} hour(s)";
            }
            else if (remaining.TotalMinutes >= 1)
            {
                timeRemaining = $"{(int)remaining.TotalMinutes} minute(s)";
            }
            else
            {
                timeRemaining = "less than 1 minute";
            }

            _tokenStatusLabel.Text = $"Valid (expires in {timeRemaining})";
            _tokenStatusLabel.ColorScheme = TuiColorPalette.Success;
        }
    }

    private static string TruncateUrl(string url, int maxLength)
    {
        if (string.IsNullOrEmpty(url) || url.Length <= maxLength)
        {
            return url;
        }

        return url.Substring(0, maxLength - 3) + "...";
    }

    private void OnRefreshClicked()
    {
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling via ContinueWith
        _ = LoadProfileAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                _errorService.ReportError("Failed to refresh profile", t.Exception, "ProfileRefresh");
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }

    /// <inheritdoc />
    public ProfileDetailsDialogState CaptureState()
    {
        var envType = _session.GetThemeService().DetectEnvironmentType(
            _environmentUrlLabel.Text?.ToString() ?? string.Empty);

        return new ProfileDetailsDialogState(
            Title: Title?.ToString() ?? string.Empty,
            ProfileName: _profileNameLabel.Text?.ToString() ?? "(unknown)",
            AuthMethod: _authMethodLabel.Text?.ToString() ?? "Unknown",
            Identity: _identityLabel.Text?.ToString(),
            EnvironmentName: _environmentLabel.Text?.ToString(),
            EnvironmentUrl: _environmentUrlLabel.Text?.ToString(),
            EnvironmentType: envType,
            IsActive: true,
            CreatedAt: null);
    }
}
