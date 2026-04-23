using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services.Settings;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.WebResources;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// TUI screen for browsing and managing Dataverse web resources.
/// </summary>
internal sealed class WebResourcesScreen : TuiScreenBase
{
    private readonly TableView _table;
    private readonly Label _statusLabel;
    private List<WebResourceInfo> _resources = [];
    private Dialog? _contentDialog;
    private CancellationTokenSource? _loadCts;
    private bool _textOnly = true;
    private Guid? _selectedSolutionId;
    private string? _selectedSolutionName;
    private bool _staleFilterChecked;

    public override string Title => "Web Resources";

    public WebResourcesScreen(InteractiveSession session, string? environmentUrl = null)
        : base(session, environmentUrl)
    {
        _table = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            FullRowSelect = true,
            Style = { ShowHorizontalHeaderOverline = false, ShowHorizontalHeaderUnderline = true }
        };

        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.Bottom(_table),
            Width = Dim.Fill(),
            Height = 1,
            Text = "Loading...",
            ColorScheme = TuiColorPalette.Default
        };

        _table.CellActivated += OnCellActivated;

        Content.Add(_table, _statusLabel);

        // Restore persisted filter state before initial load
        if (EnvironmentUrl != null)
        {
            var savedState = Session.GetTuiStateStore()
                .LoadScreenState<WebResourcesScreenState>("WebResources", EnvironmentUrl);
            if (savedState != null)
            {
                _selectedSolutionId = savedState.SelectedSolutionId;
                _textOnly = savedState.TextOnly;
            }
        }

        if (EnvironmentUrl != null)
        {
            ErrorService.FireAndForget(LoadDataAsync(), "WebResources.InitialLoad");
        }
        else
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
        }
    }

    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.CtrlMask | Key.R, "Refresh", () => ErrorService.FireAndForget(LoadDataAsync(), "WebResources.Refresh"));
        RegisterHotkey(registry, Key.CtrlMask | Key.P, "Publish selected", () => ErrorService.FireAndForget(PublishSelectedAsync(), "WebResources.Publish"));
        RegisterHotkey(registry, Key.CtrlMask | Key.F, "Solution filter", () => ErrorService.FireAndForget(ShowSolutionFilterAsync(), "WebResources.Filter"));
        RegisterHotkey(registry, Key.CtrlMask | Key.ShiftMask | Key.T, "Toggle text-only", ToggleTextOnly);
        RegisterHotkey(registry, Key.CtrlMask | Key.O, "Open in Maker", OpenInMaker);
        RegisterHotkey(registry, Key.CtrlMask | Key.ShiftMask | Key.P, "Publish All", () =>
            ErrorService.FireAndForget(PublishAllAsync(), "WebResources.PublishAll"));
    }

    private async Task LoadDataAsync()
    {
        if (EnvironmentUrl == null)
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
            return;
        }

        // Cancel any previous load
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
            var filterLabel = _textOnly ? "text-only " : "";
            _statusLabel.Text = $"Loading {filterLabel}web resources...";
            Application.Refresh();

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ct);
            var service = provider.GetRequiredService<IWebResourceService>();

            // Stale reference check: verify persisted solution still exists (first load only)
            if (_selectedSolutionId.HasValue && !_staleFilterChecked)
            {
                _staleFilterChecked = true;
                var solutionService = provider.GetRequiredService<ISolutionService>();
                var solutions = await solutionService.ListAsync(cancellationToken: ct);
                var matchedSolution = solutions.Items.FirstOrDefault(s => s.Id == _selectedSolutionId.Value);
                if (matchedSolution == null)
                {
                    _selectedSolutionId = null;
                    _selectedSolutionName = null;
                    ErrorService.FireAndForget(
                        Session.GetTuiStateStore().SaveScreenStateAsync("WebResources", EnvironmentUrl!,
                            new WebResourcesScreenState { SelectedSolutionId = null, TextOnly = _textOnly }),
                        "WebResources.ClearStaleState");
                    Application.MainLoop.Invoke(() =>
                    {
                        _statusLabel.Text = "Previously filtered solution not found \u2014 showing all";
                    });
                }
                else if (_selectedSolutionName == null)
                {
                    // Restore solution name from service on first load (e.g. after app restart)
                    _selectedSolutionName = !string.IsNullOrWhiteSpace(matchedSolution.FriendlyName)
                        ? matchedSolution.FriendlyName
                        : matchedSolution.UniqueName;
                }
            }

            var wrResult = await service.ListAsync(_selectedSolutionId, _textOnly, cancellationToken: ct);
            _resources = wrResult.Items.ToList();

            Application.MainLoop.Invoke(() =>
            {
                var dt = new System.Data.DataTable();
                dt.Columns.Add("Name", typeof(string));
                dt.Columns.Add("Display Name", typeof(string));
                dt.Columns.Add("Type", typeof(string));
                dt.Columns.Add("Managed", typeof(string));
                dt.Columns.Add("Modified By", typeof(string));
                dt.Columns.Add("Modified On", typeof(string));

                foreach (var resource in _resources)
                {
                    dt.Rows.Add(
                        resource.Name,
                        resource.DisplayName ?? "\u2014",
                        resource.TypeName,
                        resource.IsManaged ? "Yes" : "No",
                        resource.ModifiedByName ?? "\u2014",
                        resource.ModifiedOn?.ToString("g") ?? "\u2014");
                }

                _table.Table = dt;

                var managed = _resources.Count(r => r.IsManaged);
                var unmanaged = _resources.Count - managed;
                var textTypes = _resources.Count(r => r.IsTextType);
                var statusParts = new List<string> { $"{_resources.Count} web resource{(_resources.Count != 1 ? "s" : "")}" };
                if (unmanaged > 0) statusParts.Add($"{unmanaged} unmanaged");
                if (managed > 0) statusParts.Add($"{managed} managed");
                if (!_textOnly) statusParts.Add($"{textTypes} text");
                statusParts.Add(_textOnly ? "text-only" : "all types");
                // I4 / L11-c: show active solution filter so user knows a filter is applied.
                if (_selectedSolutionId.HasValue && _selectedSolutionName != null)
                {
                    statusParts.Add($"Filtered: {_selectedSolutionName}");
                }
                _statusLabel.Text = string.Join(" \u2014 ", statusParts);
            });
        }
        catch (OperationCanceledException) { /* load cancelled or screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load web resources", ex, "WebResources.Load");
                _statusLabel.Text = "Error loading web resources";
            });
        }
    }

    private void OnCellActivated(TableView.CellActivatedEventArgs args)
    {
        if (args.Row < 0 || args.Row >= _resources.Count) return;
        var resource = _resources[args.Row];

        if (!resource.IsTextType)
        {
            MessageBox.Query("Binary Resource", "Binary web resources cannot be viewed in TUI.", "OK");
            return;
        }

        ErrorService.FireAndForget(ShowContentDialogAsync(resource), "WebResources.ShowContent");
    }

    private async Task ShowContentDialogAsync(WebResourceInfo resource)
    {
        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IWebResourceService>();

            var content = await service.GetContentAsync(resource.Id, false, ScreenCancellation);

            Application.MainLoop.Invoke(() =>
            {
                // Safely dispose any prior dialog: null the field FIRST so
                // OnDispose() can't re-enter a partially disposed instance if
                // Dispose() throws.
                var prior = _contentDialog;
                _contentDialog = null;
                prior?.Dispose();

                var textView = new TextView
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill(),
                    ReadOnly = true,
                    Text = content?.Content ?? "(No content available)"
                };

                var dialog = new Dialog(
                    $"{resource.Name} ({resource.TypeName})",
                    new Button("Close", is_default: true))
                {
                    Width = Dim.Percent(80),
                    Height = Dim.Percent(80)
                };
                try
                {
                    _contentDialog = dialog;
                    dialog.Add(textView);
                    Application.Run(dialog);
                }
                finally
                {
                    // Null-before-dispose: if Dispose throws, OnDispose() won't
                    // see a stale reference to this instance.
                    var d = _contentDialog;
                    _contentDialog = null;
                    d?.Dispose();
                }
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load web resource content", ex, "WebResources.Content");
            });
        }
    }

    private async Task PublishSelectedAsync()
    {
        if (EnvironmentUrl == null)
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
            return;
        }

        var selectedRow = _table.SelectedRow;
        if (selectedRow < 0 || selectedRow >= _resources.Count) return;
        var resource = _resources[selectedRow];

        var result = MessageBox.Query(
            "Publish Web Resource",
            $"Publish '{resource.Name}'?",
            "Publish", "Cancel");

        if (result != 0) return;

        try
        {
            _statusLabel.Text = $"Publishing {resource.Name}...";
            Application.Refresh();

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IWebResourceService>();

            var count = await service.PublishAsync([resource.Id], ScreenCancellation);

            Application.MainLoop.Invoke(() =>
            {
                _statusLabel.Text = $"Published {count} web resource{(count != 1 ? "s" : "")} successfully";
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to publish web resource", ex, "WebResources.Publish");
                _statusLabel.Text = "Error publishing web resource";
            });
        }
    }

    private async Task PublishAllAsync()
    {
        if (EnvironmentUrl == null)
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
            return;
        }

        var result = MessageBox.Query(
            "Publish All Customizations",
            "Publish all customizations? This publishes everything, not just web resources.",
            "Publish All", "Cancel");

        if (result != 0) return;

        try
        {
            _statusLabel.Text = "Publishing all customizations...";
            Application.Refresh();

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IWebResourceService>();

            await service.PublishAllAsync(ScreenCancellation);

            Application.MainLoop.Invoke(() =>
            {
                _statusLabel.Text = "All customizations published successfully";
            });

            await LoadDataAsync();
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to publish all customizations", ex, "WebResources.PublishAll");
                _statusLabel.Text = "Error publishing customizations";
            });
        }
    }

    private async Task ShowSolutionFilterAsync()
    {
        if (EnvironmentUrl == null)
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
            return;
        }

        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var solutionService = provider.GetRequiredService<ISolutionService>();

            var solutionsResult = await solutionService.ListAsync(cancellationToken: ScreenCancellation);
            var solutions = solutionsResult.Items;

            Application.MainLoop.Invoke(() =>
            {
                // Build list with "All Solutions" option at top
                var items = new List<string> { "(All Solutions)" };
                items.AddRange(solutions.Select(s => $"{s.FriendlyName} ({s.UniqueName})"));

                var listView = new ListView(items)
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill()
                };

                var dialog = new Dialog("Filter by Solution", new Button("Cancel"))
                {
                    Width = Dim.Percent(60),
                    Height = Dim.Percent(60)
                };

                Action<ListViewItemEventArgs> openHandler = (args) =>
                {
                    if (args.Item == 0)
                    {
                        _selectedSolutionId = null;
                        _selectedSolutionName = null;
                    }
                    else
                    {
                        var sol = solutions[args.Item - 1];
                        _selectedSolutionId = sol.Id;
                        _selectedSolutionName = !string.IsNullOrWhiteSpace(sol.FriendlyName)
                            ? sol.FriendlyName
                            : sol.UniqueName;
                    }
                    Application.RequestStop();
                };

                try
                {
                    dialog.Add(listView);

                    listView.OpenSelectedItem += openHandler;

                    Application.Run(dialog);
                }
                finally
                {
                    // R3: explicitly unsubscribe before disposing.
                    listView.OpenSelectedItem -= openHandler;
                    dialog.Dispose();
                }

                // Persist updated filter state
                ErrorService.FireAndForget(
                    Session.GetTuiStateStore().SaveScreenStateAsync("WebResources", EnvironmentUrl!,
                        new WebResourcesScreenState { SelectedSolutionId = _selectedSolutionId, TextOnly = _textOnly }),
                    "WebResources.SaveState");

                // Reload with new filter
                ErrorService.FireAndForget(LoadDataAsync(), "WebResources.FilterReload");
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load solutions for filter", ex, "WebResources.SolutionFilter");
            });
        }
    }

    private void ToggleTextOnly()
    {
        _textOnly = !_textOnly;

        if (EnvironmentUrl != null)
        {
            // Persist updated filter state
            ErrorService.FireAndForget(
                Session.GetTuiStateStore().SaveScreenStateAsync("WebResources", EnvironmentUrl,
                    new WebResourcesScreenState { SelectedSolutionId = _selectedSolutionId, TextOnly = _textOnly }),
                "WebResources.SaveState");
        }

        ErrorService.FireAndForget(LoadDataAsync(), "WebResources.ToggleTextOnly");
    }

    private void OpenInMaker()
    {
        if (EnvironmentUrl == null)
        {
            ErrorService.ReportError("No environment URL available");
            return;
        }
        var dialog = new Dialog("Open in Maker", new Button("OK", is_default: true))
        {
            Width = 60,
            Height = 7
        };
        try
        {
            dialog.Add(new Label { X = 1, Y = 1, Text = "Open this URL in your browser:" });
            dialog.Add(new Label { X = 1, Y = 2, Text = EnvironmentUrl + "/WebResources" });
            Application.Run(dialog);
        }
        finally
        {
            dialog.Dispose();
        }
    }

    protected override void OnDispose()
    {
        _table.CellActivated -= OnCellActivated;
        _contentDialog?.Dispose();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}
