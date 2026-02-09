using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using PPDS.Dataverse.Pooling;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog showing detailed environment and organization information.
/// Equivalent to 'ppds env who' command.
/// </summary>
internal sealed class EnvironmentDetailsDialog : TuiDialog, ITuiStateCapture<EnvironmentDetailsDialogState>
{
    private readonly InteractiveSession _session;
    private readonly ITuiErrorService _errorService;
    private readonly string _environmentUrl;
    private readonly string? _environmentDisplayName;
    private readonly ITuiThemeService _themeService;
    private readonly CancellationTokenSource _cancellationSource = new();
    private bool _disposed;

    private readonly Label _envNameLabel;
    private readonly Label _urlLabel;
    private readonly Label _uniqueNameLabel;
    private readonly Label _versionLabel;
    private readonly Label _orgIdLabel;
    private readonly Label _userIdLabel;
    private readonly Label _businessUnitIdLabel;
    private readonly Label _connectedAsLabel;
    private readonly Label _statusLabel;
    private readonly Button _refreshButton;

    /// <summary>
    /// Creates a new environment details dialog.
    /// </summary>
    /// <param name="session">The interactive session for connection pool access.</param>
    /// <param name="environmentUrl">The environment URL to query.</param>
    /// <param name="environmentDisplayName">The environment display name (optional).</param>
    public EnvironmentDetailsDialog(
        InteractiveSession session,
        string environmentUrl,
        string? environmentDisplayName = null) : base("Environment Details", session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _errorService = session.GetErrorService();
        _environmentUrl = environmentUrl ?? throw new ArgumentNullException(nameof(environmentUrl));
        _environmentDisplayName = environmentDisplayName;
        _themeService = session.GetThemeService();

        Width = Dim.Percent(80);
        Height = 20;

        // Environment name header with type-specific coloring
        var envType = _themeService.DetectEnvironmentType(_environmentUrl);
        var envLabel = _themeService.GetEnvironmentLabel(envType);
        var headerText = !string.IsNullOrEmpty(envLabel)
            ? $"Environment: {_environmentDisplayName ?? "(loading...)"} [{envLabel}]"
            : $"Environment: {_environmentDisplayName ?? "(loading...)"}";

        _envNameLabel = new Label(headerText)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            ColorScheme = _themeService.GetStatusBarScheme(envType)
        };

        // Separator using LineView for proper line drawing
        var separator = new LineView
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill() - 2,
            ColorScheme = TuiColorPalette.Default
        };

        // Detail labels with consistent formatting
        const int labelWidth = 18;
        int row = 4;

        _urlLabel = CreateDetailRow("URL:", ref row, labelWidth);
        _uniqueNameLabel = CreateDetailRow("Unique Name:", ref row, labelWidth);
        // Type and Region removed - type already shown in header badge
        _versionLabel = CreateDetailRow("Version:", ref row, labelWidth);

        // Blank line before IDs
        row++;

        _orgIdLabel = CreateDetailRow("Organization ID:", ref row, labelWidth);
        _userIdLabel = CreateDetailRow("User ID:", ref row, labelWidth);
        _businessUnitIdLabel = CreateDetailRow("Business Unit ID:", ref row, labelWidth);

        // Blank line before connected user
        row++;

        _connectedAsLabel = CreateDetailRow("Connected As:", ref row, labelWidth);

        // Status label for loading/error messages
        _statusLabel = new Label("Loading environment details...")
        {
            X = 1,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill() - 2,
            Height = 1
        };

        // Buttons
        _refreshButton = new Button("_Refresh")
        {
            X = Pos.Center() - 12,
            Y = Pos.AnchorEnd(1)
        };
        _refreshButton.Clicked += OnRefreshClicked;

        var closeButton = new Button("_Close")
        {
            X = Pos.Center() + 3,
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => { Application.RequestStop(); };

        Add(_envNameLabel, separator,
            _urlLabel, _uniqueNameLabel, _versionLabel,
            _orgIdLabel, _userIdLabel, _businessUnitIdLabel, _connectedAsLabel,
            _statusLabel, _refreshButton, closeButton);

        // Load details asynchronously
        LoadDetailsAsync(_cancellationSource.Token);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _cancellationSource.Cancel();
            _cancellationSource.Dispose();
        }
        base.Dispose(disposing); // Calls TuiDialog.Dispose which clears active dialog
    }

    private Label CreateDetailRow(string labelText, ref int row, int labelWidth)
    {
        var label = new Label(labelText)
        {
            X = 1,
            Y = row,
            Width = labelWidth,
            ColorScheme = TuiColorPalette.TableHeader
        };

        var valueLabel = new Label("(loading...)")
        {
            X = labelWidth + 1,
            Y = row,
            Width = Dim.Fill() - labelWidth - 2,
            ColorScheme = TuiColorPalette.Default
        };

        Add(label);
        row++;

        return valueLabel;
    }

    private void LoadDetailsAsync(CancellationToken cancellationToken)
    {
        _refreshButton.Enabled = false;
        _statusLabel.Text = "Loading environment details...";

        _errorService.FireAndForget(LoadDetailsInternalAsync(cancellationToken), "LoadEnvironmentDetails");
    }

    private async Task LoadDetailsInternalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var provider = await _session.GetServiceProviderAsync(_environmentUrl, cancellationToken);
        var pool = provider.GetRequiredService<IDataverseConnectionPool>();

        cancellationToken.ThrowIfCancellationRequested();

        await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // Execute WhoAmI to get user info
        var whoAmIResponse = (WhoAmIResponse)await client.ExecuteAsync(new WhoAmIRequest(), cancellationToken);

        // Get org info from client properties
        var orgName = client.ConnectedOrgFriendlyName;
        var orgUniqueName = client.ConnectedOrgUniqueName;
        var orgId = client.ConnectedOrgId;
        var version = client.ConnectedOrgVersion?.ToString();

        // Get profile info for connected user identity
        var store = _session.GetProfileStore();
        var collection = await store.LoadAsync(cancellationToken);
        var profile = collection.ActiveProfile;
        var connectedAs = profile?.Username ?? "(unknown)";

        // Get environment type from profile or detect from URL
        var envInfo = profile?.Environment;
        var envType = envInfo?.Type;
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Application.MainLoop?.Invoke(() =>
        {
            // Check _disposed before accessing token (CTS may be disposed)
            if (_disposed)
            {
                return;
            }

            // Update header with actual name
            var detectedEnvType = _themeService.DetectEnvironmentType(_environmentUrl);
            var envLabel = _themeService.GetEnvironmentLabel(detectedEnvType);
            var displayType = !string.IsNullOrEmpty(envType) ? envType : envLabel;

            _envNameLabel.Text = !string.IsNullOrEmpty(displayType)
                ? $"Environment: {orgName ?? _environmentDisplayName ?? "(unknown)"} [{displayType}]"
                : $"Environment: {orgName ?? _environmentDisplayName ?? "(unknown)"}";

            // Update detail values
            _urlLabel.Text = _environmentUrl;
            _uniqueNameLabel.Text = orgUniqueName ?? "(not available)";
            // Type and region removed - type is shown in header badge
            _versionLabel.Text = version ?? "(not available)";

            _orgIdLabel.Text = orgId != Guid.Empty ? orgId.ToString() : "(not available)";
            _userIdLabel.Text = whoAmIResponse.UserId.ToString();
            _businessUnitIdLabel.Text = whoAmIResponse.BusinessUnitId.ToString();

            _connectedAsLabel.Text = connectedAs;

            _statusLabel.Text = "Details loaded successfully";
            _statusLabel.ColorScheme = TuiColorPalette.Success;
        });
    }

    private void OnRefreshClicked()
    {
        LoadDetailsAsync(_cancellationSource.Token);
    }

    /// <inheritdoc />
    public EnvironmentDetailsDialogState CaptureState()
    {
        var envType = _themeService.DetectEnvironmentType(_environmentUrl);

        return new EnvironmentDetailsDialogState(
            Title: Title?.ToString() ?? string.Empty,
            DisplayName: _environmentDisplayName ?? "(unknown)",
            Url: _environmentUrl,
            EnvironmentType: envType,
            OrganizationId: _orgIdLabel.Text?.ToString(),
            Version: _versionLabel.Text?.ToString());
    }
}
