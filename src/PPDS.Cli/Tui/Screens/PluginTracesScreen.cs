using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Services;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// TUI screen for browsing and managing plugin trace logs.
/// </summary>
internal sealed class PluginTracesScreen : TuiScreenBase
{
    private readonly TableView _table;
    private readonly Label _statusLabel;

    private List<PluginTraceInfo> _traces = [];
    private PluginTraceFilter? _currentFilter;
    private CancellationTokenSource? _loadCts;
    private Dialog? _activeDialog;
    private bool _isShowingDialog;

    public override string Title => "Plugin Traces";

    public PluginTracesScreen(InteractiveSession session, string? environmentUrl = null)
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

        if (EnvironmentUrl != null)
        {
            ErrorService.FireAndForget(LoadTracesAsync(), "PluginTraces.InitialLoad");
        }
        else
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
        }
    }

    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.CtrlMask | Key.R, "Refresh", () =>
            ErrorService.FireAndForget(LoadTracesAsync(), "PluginTraces.Refresh"));

        RegisterHotkey(registry, Key.CtrlMask | Key.F, "Filter", ShowFilterDialog);

        RegisterHotkey(registry, Key.CtrlMask | Key.T, "Timeline", () =>
            ErrorService.FireAndForget(ShowTimelineAsync(), "PluginTraces.Timeline"));

        RegisterHotkey(registry, Key.CtrlMask | Key.D, "Delete", ShowDeleteDialog);

        RegisterHotkey(registry, Key.CtrlMask | Key.L, "Trace Level", () =>
            ErrorService.FireAndForget(ShowTraceLevelAsync(), "PluginTraces.TraceLevel"));
    }

    private async Task LoadTracesAsync()
    {
        if (string.IsNullOrEmpty(EnvironmentUrl))
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = "No environment selected. Use the status bar to connect.";
            });
            return;
        }

        // Cancel any in-flight load to prevent races
        _loadCts?.Cancel();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ScreenCancellation);
        var ct = _loadCts.Token;

        try
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = "Loading plugin traces...";
            });

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ct);
            var service = provider.GetRequiredService<IPluginTraceService>();

            var traces = await service.ListAsync(filter: _currentFilter, cancellationToken: ct);

            ct.ThrowIfCancellationRequested();

            // Check trace level when no traces are found to give actionable guidance
            PluginTraceLogSetting? traceSetting = null;
            if (traces.Count == 0 && _currentFilter == null)
            {
                var settings = await service.GetSettingsAsync(ct);
                traceSetting = settings.Setting;
            }

            Application.MainLoop?.Invoke(() =>
            {
                _traces = traces;
                PopulateTable();

                if (traceSetting == PluginTraceLogSetting.Off && _traces.Count == 0)
                {
                    _statusLabel.Text = "Plugin trace level is Off \u2014 no new traces are being recorded. Use Ctrl+L to change.";
                }
                else
                {
                    UpdateStatusLabel();
                }
            });
        }
        catch (OperationCanceledException) { /* screen closing or superseded load */ }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load plugin traces", ex, "PluginTraces.Load");
                _statusLabel.Text = "Error loading plugin traces";
            });
        }
    }

    private void PopulateTable()
    {
        var dt = new System.Data.DataTable();
        dt.Columns.Add("Time", typeof(string));
        dt.Columns.Add("Duration (ms)", typeof(string));
        dt.Columns.Add("Plugin", typeof(string));
        dt.Columns.Add("Entity", typeof(string));
        dt.Columns.Add("Message", typeof(string));
        dt.Columns.Add("Depth", typeof(string));
        dt.Columns.Add("Mode", typeof(string));
        dt.Columns.Add("Status", typeof(string));

        foreach (var trace in _traces)
        {
            dt.Rows.Add(
                trace.CreatedOn.ToString("G"),
                trace.DurationMs?.ToString() ?? "\u2014",
                trace.TypeName,
                trace.PrimaryEntity ?? "\u2014",
                trace.MessageName ?? "\u2014",
                trace.Depth.ToString(),
                trace.Mode == PluginTraceMode.Synchronous ? "Sync" : "Async",
                trace.HasException ? "Error" : "OK");
        }

        _table.Table = dt;
    }

    private void UpdateStatusLabel()
    {
        if (_traces.Count == 0)
        {
            _statusLabel.Text = _currentFilter != null
                ? "No traces match the current filter."
                : "No plugin traces found.";
            return;
        }

        var errors = _traces.Count(t => t.HasException);
        var statusParts = new List<string> { $"{_traces.Count} trace{(_traces.Count != 1 ? "s" : "")}" };
        if (errors > 0) statusParts.Add($"{errors} with errors");
        if (_currentFilter != null) statusParts.Add("filtered");
        _statusLabel.Text = string.Join(" \u2014 ", statusParts);
    }

    private void OnCellActivated(TableView.CellActivatedEventArgs args)
    {
        if (_isShowingDialog) return;
        if (args.Row < 0 || args.Row >= _traces.Count) return;
        var trace = _traces[args.Row];
        ErrorService.FireAndForget(ShowDetailDialogAsync(trace), "PluginTraces.ShowDetail");
    }

    private async Task ShowDetailDialogAsync(PluginTraceInfo trace)
    {
        if (string.IsNullOrEmpty(EnvironmentUrl))
        {
            Application.MainLoop?.Invoke(() => { _statusLabel.Text = "No environment connected"; });
            return;
        }

        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IPluginTraceService>();

            var detail = await service.GetAsync(trace.Id, ScreenCancellation);

            if (detail == null)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    ErrorService.ReportError("Trace not found. It may have been deleted.");
                });
                return;
            }

            Application.MainLoop?.Invoke(() =>
            {
                if (_isShowingDialog) return;
                _isShowingDialog = true;

                _activeDialog?.Dispose();
                _activeDialog = new PluginTraceDetailDialog(detail, Session);
                try
                {
                    Application.Run(_activeDialog);
                }
                finally
                {
                    _activeDialog.Dispose();
                    _activeDialog = null;
                    _isShowingDialog = false;
                }
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load trace details", ex, "PluginTraces.Detail");
            });
        }
    }

    private void ShowFilterDialog()
    {
        if (_isShowingDialog) return;
        _isShowingDialog = true;

        using var dialog = new PluginTraceFilterDialog(_currentFilter, Session);
        Application.Run(dialog);

        _isShowingDialog = false;

        // dialog.Filter is null if cancelled, also null if all fields empty (clears filter)
        if (dialog.Filter != _currentFilter)
        {
            _currentFilter = dialog.Filter;
            ErrorService.FireAndForget(LoadTracesAsync(), "PluginTraces.FilterApply");
        }
    }

    private async Task ShowTimelineAsync()
    {
        if (string.IsNullOrEmpty(EnvironmentUrl))
        {
            Application.MainLoop?.Invoke(() => { _statusLabel.Text = "No environment connected"; });
            return;
        }

        var selectedRow = _table.SelectedRow;
        if (selectedRow < 0 || selectedRow >= _traces.Count)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("No trace selected. Select a trace to view its timeline.");
            });
            return;
        }

        var trace = _traces[selectedRow];
        if (trace.CorrelationId == null || trace.CorrelationId == Guid.Empty)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Selected trace has no correlation ID.");
            });
            return;
        }

        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IPluginTraceService>();

            var timeline = await service.BuildTimelineAsync(trace.CorrelationId.Value, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                if (_isShowingDialog) return;
                _isShowingDialog = true;

                _activeDialog?.Dispose();
                _activeDialog = new TraceTimelineDialog(timeline, Session);
                try
                {
                    Application.Run(_activeDialog);
                }
                finally
                {
                    _activeDialog.Dispose();
                    _activeDialog = null;
                    _isShowingDialog = false;
                }
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to build timeline", ex, "PluginTraces.Timeline");
            });
        }
    }

    private void ShowDeleteDialog()
    {
        if (_isShowingDialog) return;
        _isShowingDialog = true;

        using var dialog = new TraceDeleteDialog(_traces.Count, Session);
        Application.Run(dialog);

        _isShowingDialog = false;

        var result = dialog.Result;
        if (result != null)
        {
            ErrorService.FireAndForget(ExecuteDeleteAsync(result), "PluginTraces.Delete");
        }
    }

    private async Task ExecuteDeleteAsync(TraceDeleteResult deleteResult)
    {
        if (string.IsNullOrEmpty(EnvironmentUrl))
        {
            Application.MainLoop?.Invoke(() => { _statusLabel.Text = "No environment connected"; });
            return;
        }

        try
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = "Deleting traces...";
            });

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IPluginTraceService>();

            int deletedCount;

            if (deleteResult.Mode == TraceDeleteMode.ByIds)
            {
                var ids = _traces.Select(t => t.Id).ToList();
                deletedCount = await service.DeleteByIdsAsync(ids, cancellationToken: ScreenCancellation);
            }
            else
            {
                var olderThan = TimeSpan.FromDays(deleteResult.DayCount!.Value);
                deletedCount = await service.DeleteOlderThanAsync(olderThan, cancellationToken: ScreenCancellation);
            }

            // Reload after deletion
            await LoadTracesAsync();

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Deleted {deletedCount} trace{(deletedCount != 1 ? "s" : "")}. {_traces.Count} remaining.";
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to delete traces", ex, "PluginTraces.Delete");
                _statusLabel.Text = "Error deleting traces";
            });
        }
    }

    private async Task ShowTraceLevelAsync()
    {
        if (string.IsNullOrEmpty(EnvironmentUrl))
        {
            Application.MainLoop?.Invoke(() => { _statusLabel.Text = "No environment connected"; });
            return;
        }

        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IPluginTraceService>();

            var settings = await service.GetSettingsAsync(ScreenCancellation);

            PluginTraceLogSetting? selectedLevel = null;

            Application.MainLoop?.Invoke(() =>
            {
                if (_isShowingDialog) return;
                _isShowingDialog = true;

                using var dialog = new TraceLevelDialog(settings.Setting, Session);
                Application.Run(dialog);

                selectedLevel = dialog.SelectedLevel;
                _isShowingDialog = false;
            });

            if (selectedLevel.HasValue && selectedLevel.Value != settings.Setting)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    _statusLabel.Text = $"Setting trace level to {selectedLevel.Value}...";
                });

                await service.SetSettingsAsync(selectedLevel.Value, ScreenCancellation);

                Application.MainLoop?.Invoke(() =>
                {
                    _statusLabel.Text = $"Trace level set to {selectedLevel.Value}.";
                });
            }
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to change trace level", ex, "PluginTraces.TraceLevel");
            });
        }
    }

    protected override void OnDispose()
    {
        _table.CellActivated -= OnCellActivated;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _activeDialog?.Dispose();
    }
}
