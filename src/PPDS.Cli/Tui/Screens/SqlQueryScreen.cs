using System.Net.Http;
using PPDS.Auth.Credentials;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Views;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// SQL Query screen for executing queries against Dataverse and viewing results.
/// </summary>
internal sealed class SqlQueryScreen : Window
{
    private readonly string? _profileName;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly InteractiveSession _session;
    private readonly ITuiErrorService _errorService;
    private readonly IHotkeyRegistry _hotkeyRegistry;
    private readonly List<IDisposable> _hotkeyRegistrations = new();

    private readonly TextView _queryInput;
    private readonly QueryResultsTableView _resultsTable;
    private readonly TuiStatusBar _statusBar;
    private readonly TuiSpinner _statusSpinner;
    private readonly TextField _filterField;
    private readonly FrameView _filterFrame;

    private string? _environmentUrl;
    private string? _lastSql;
    private string? _lastPagingCookie;
    private int _lastPageNumber = 1;

    public SqlQueryScreen(string? profileName, Action<DeviceCodeInfo>? deviceCodeCallback, InteractiveSession session)
    {
        _profileName = profileName;
        _deviceCodeCallback = deviceCodeCallback;
        _session = session;
        _errorService = session.GetErrorService();
        _hotkeyRegistry = session.GetHotkeyRegistry();

        // Mark this screen as active for screen-scope hotkeys
        _hotkeyRegistry.SetActiveScreen(this);

        Title = "SQL Query";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        // Apply dark theme
        ColorScheme = TuiColorPalette.Default;

        // Query input area
        var queryFrame = new FrameView("Query (Ctrl+Enter to execute)")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 6,
            ColorScheme = TuiColorPalette.Default
        };

        _queryInput = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "SELECT TOP 100 accountid, name, createdon FROM account"
        };
        queryFrame.Add(_queryInput);

        // Filter field (hidden by default)
        _filterFrame = new FrameView("Filter (/)")
        {
            X = 0,
            Y = Pos.Bottom(queryFrame),
            Width = Dim.Fill(),
            Height = 3,
            Visible = false,
            ColorScheme = TuiColorPalette.Default
        };

        _filterField = new TextField
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = TuiColorPalette.TextInput
        };
        _filterField.TextChanged += OnFilterChanged;
        _filterFrame.Add(_filterField);

        // Results table
        _resultsTable = new QueryResultsTableView
        {
            X = 0,
            Y = Pos.Bottom(queryFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2
        };
        _resultsTable.LoadMoreRequested += OnLoadMoreRequested;

        // Status spinner (for animated feedback during operations)
        _statusSpinner = new TuiSpinner
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Visible = false
        };

        // Interactive status bar with profile/environment info
        _statusBar = new TuiStatusBar(_session);
        _statusBar.ProfileClicked += OnStatusBarProfileClicked;
        _statusBar.EnvironmentClicked += OnStatusBarEnvironmentClicked;
        _statusBar.SetStatusMessage("Ready. Press Ctrl+Enter to execute query.");

        Add(queryFrame, _filterFrame, _resultsTable, _statusSpinner, _statusBar);

        // Subscribe to environment changes from the session
        _session.EnvironmentChanged += OnEnvironmentChanged;

        // Initialize from session's current environment (may already be set from InitializeAsync)
        if (_session.CurrentEnvironmentUrl != null)
        {
            _environmentUrl = _session.CurrentEnvironmentUrl;
            _resultsTable.SetEnvironmentUrl(_environmentUrl);
            Title = $"SQL Query - {_session.CurrentEnvironmentDisplayName ?? _environmentUrl}";
        }
        else
        {
            _statusBar.SetStatusMessage("Connecting to environment...");
        }

        // Set up keyboard shortcuts
        SetupKeyboardShortcuts();
    }

    private void OnStatusBarProfileClicked()
    {
        var service = _session.GetProfileService();
        var dialog = new ProfileSelectorDialog(service, _session);
        Application.Run(dialog);

        if (dialog.SelectedProfile != null)
        {
            // Profile switch handled by session - status bar updates automatically
        }
        else if (dialog.CreateNewSelected)
        {
            // Show profile creation dialog
            var profileService = _session.GetProfileService();
            var envService = _session.GetEnvironmentService();
            var creationDialog = new ProfileCreationDialog(profileService, envService, _deviceCodeCallback);
            Application.Run(creationDialog);
        }
    }

    private void OnStatusBarEnvironmentClicked()
    {
        var service = _session.GetEnvironmentService();
        var dialog = new EnvironmentSelectorDialog(service, _deviceCodeCallback, _session);
        Application.Run(dialog);

        if (dialog.SelectedEnvironment != null || dialog.UseManualUrl)
        {
            var url = dialog.UseManualUrl ? dialog.ManualUrl : dialog.SelectedEnvironment?.Url;
            var name = dialog.UseManualUrl ? dialog.ManualUrl : dialog.SelectedEnvironment?.DisplayName;

            if (url != null)
            {
                // Environment switch handled by SetEnvironmentAsync - triggers OnEnvironmentChanged
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
                _ = _session.SetEnvironmentAsync(url, name).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _errorService.ReportError("Failed to set environment", t.Exception, "SetEnvironment");
                    }
                }, TaskScheduler.Default);
#pragma warning restore PPDS013
            }
        }
    }

    private void OnEnvironmentChanged(string? url, string? displayName)
    {
        Application.MainLoop?.Invoke(() =>
        {
            if (url != null)
            {
                _environmentUrl = url;
                _resultsTable.SetEnvironmentUrl(_environmentUrl);
                Title = $"SQL Query - {displayName ?? url}";
                _statusBar.SetStatusMessage("Ready. Press Ctrl+Enter to execute query.");

                // Clear stale results from previous environment
                _resultsTable.Clear();
                _lastSql = null;
                _lastPagingCookie = null;
                _lastPageNumber = 1;
            }
            else
            {
                _environmentUrl = null;
                Title = "SQL Query";
                _statusBar.SetStatusMessage("No environment selected. Select an environment first.");
            }
        });
    }

    private void SetupKeyboardShortcuts()
    {
        // Register screen-scope hotkeys via registry
        // These only work on this screen when no dialog is open
        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.CtrlMask | Key.E,
            HotkeyScope.Screen,
            "Export results",
            ShowExportDialog,
            owner: this));

        _hotkeyRegistrations.Add(_hotkeyRegistry.Register(
            Key.CtrlMask | Key.H,
            HotkeyScope.Screen,
            "Query history",
            ShowHistoryDialog,
            owner: this));

        // Component-specific: Ctrl+Enter in query input
        _queryInput.KeyPress += (e) =>
        {
            if (e.KeyEvent.Key == (Key.CtrlMask | Key.Enter))
            {
                _ = ExecuteQueryAsync();
                e.Handled = true;
            }
        };

        // Context-dependent shortcuts that need local state checks
        KeyPress += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.Esc:
                    if (_filterFrame.Visible)
                    {
                        HideFilter();
                    }
                    else
                    {
                        RequestStop();
                    }
                    e.Handled = true;
                    break;

                case Key k when k == (Key)'/':
                    if (!_queryInput.HasFocus)
                    {
                        ShowFilter();
                        e.Handled = true;
                    }
                    break;
            }
        };
    }

    private async Task ExecuteQueryAsync()
    {
        var sql = _queryInput.Text.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sql))
        {
            _statusBar.SetStatusMessage("Error: Query cannot be empty.");
            return;
        }

        if (_environmentUrl == null)
        {
            _statusBar.SetStatusMessage("Error: No environment selected.");
            return;
        }

        TuiDebugLog.Log($"Starting query execution for: {_environmentUrl}");
        TuiDebugLog.Log($"Session.CurrentEnvironmentUrl: {_session.CurrentEnvironmentUrl}");

        try
        {
            // Status: Connecting (with animated spinner)
            UpdateStatus("Connecting to Dataverse...", showSpinner: true);
            TuiDebugLog.Log($"Getting SQL query service for URL: {_environmentUrl}");

            var service = await _session.GetSqlQueryServiceAsync(_environmentUrl, CancellationToken.None);
            TuiDebugLog.Log("Got service, executing query...");

            // Status: Executing (with animated spinner)
            UpdateStatus("Executing query...", showSpinner: true);

            var request = new SqlQueryRequest
            {
                Sql = sql,
                // Don't set PageNumber for initial query - Dataverse rejects TOP with paging
                PageNumber = null,
                PagingCookie = null
            };

            var result = await service.ExecuteAsync(request, CancellationToken.None);
            TuiDebugLog.Log($"Query complete: {result.Result.Count} rows in {result.Result.ExecutionTimeMs}ms");

            // Update UI on main thread
            Application.MainLoop?.Invoke(() =>
            {
                _resultsTable.LoadResults(result.Result);
                _lastSql = sql;
                _lastPagingCookie = result.Result.PagingCookie;
                _lastPageNumber = result.Result.PageNumber;

                var moreText = result.Result.MoreRecords ? " (more available)" : "";
                UpdateStatus($"Returned {result.Result.Count} rows in {result.Result.ExecutionTimeMs}ms{moreText}", showSpinner: false);
            });

            // Save to history (fire-and-forget)
#pragma warning disable PPDS013 // Fire-and-forget - history save shouldn't block UI
            _ = SaveToHistoryAsync(sql, result.Result.Count, result.Result.ExecutionTimeMs);
#pragma warning restore PPDS013
        }
        catch (Exception ex)
        {
            _errorService.ReportError("Query execution failed", ex, "ExecuteQuery");
            UpdateStatus($"Error: {ex.Message}", showSpinner: false);
        }
    }

    private void UpdateStatus(string message, bool showSpinner = false)
    {
        TuiDebugLog.Log($"Status: {message}");
        Application.MainLoop?.Invoke(() =>
        {
            if (showSpinner)
            {
                _statusBar.Visible = false;
                _statusSpinner.Start(message);
            }
            else
            {
                _statusSpinner.Stop();
                _statusBar.SetStatusMessage(message);
                _statusBar.Visible = true;
            }
        });
    }

    private async Task SaveToHistoryAsync(string sql, int rowCount, long executionTimeMs)
    {
        if (_environmentUrl == null) return;

        try
        {
            var historyService = await _session.GetQueryHistoryServiceAsync(_environmentUrl, CancellationToken.None);
            await historyService.AddQueryAsync(_environmentUrl, sql, rowCount, executionTimeMs);
        }
        catch (Exception ex)
        {
            // History save failure is non-critical - log but don't interrupt workflow
            // The status bar shows profile/env info so we don't want to pollute it with warnings
            System.Diagnostics.Debug.WriteLine($"[TUI] Failed to save query to history: {ex.Message}");
            TuiDebugLog.Log($"History save failed: {ex.Message}");
        }
    }

    private async Task OnLoadMoreRequested()
    {
        if (_lastSql == null || _environmentUrl == null)
            return;

        try
        {
            var service = await _session.GetSqlQueryServiceAsync(_environmentUrl, CancellationToken.None);

            var request = new SqlQueryRequest
            {
                Sql = _lastSql,
                PageNumber = _lastPageNumber + 1,
                PagingCookie = _lastPagingCookie
            };

            var result = await service.ExecuteAsync(request, CancellationToken.None);

            // Update UI on main thread
            Application.MainLoop?.Invoke(() =>
            {
                _resultsTable.AddPage(result.Result);
                _lastPagingCookie = result.Result.PagingCookie;
                _lastPageNumber = result.Result.PageNumber;

                _statusBar.SetStatusMessage($"Loaded page {_lastPageNumber}");
            });
        }
        catch (InvalidOperationException ex)
        {
            _errorService.ReportError("Failed to load more results", ex, "LoadMoreResults");
            Application.MainLoop?.Invoke(() => _statusBar.SetStatusMessage($"Error loading more: {ex.Message}"));
        }
        catch (HttpRequestException ex)
        {
            _errorService.ReportError("Network error loading results", ex, "LoadMoreResults");
            Application.MainLoop?.Invoke(() => _statusBar.SetStatusMessage($"Network error: {ex.Message}"));
        }
    }

    private void ShowFilter()
    {
        _filterFrame.Visible = true;
        _filterField.Text = string.Empty;
        _filterField.SetFocus();
        _statusBar.SetStatusMessage("Type to filter results. Press Esc to close filter.");
    }

    private void HideFilter()
    {
        _filterFrame.Visible = false;
        _filterField.Text = string.Empty;
        _resultsTable.ApplyFilter(null);
        _statusBar.SetStatusMessage("Filter cleared.");
    }

    private void OnFilterChanged(NStack.ustring obj)
    {
        var filterText = _filterField.Text?.ToString() ?? string.Empty;
        _resultsTable.ApplyFilter(filterText);
    }

    private void ShowExportDialog()
    {
        var dataTable = _resultsTable.GetDataTable();
        if (dataTable == null || dataTable.Rows.Count == 0)
        {
            MessageBox.ErrorQuery("Export", "No data to export. Execute a query first.", "OK");
            return;
        }

        // Create export service directly (doesn't need environment connection)
        var exportService = new ExportService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ExportService>.Instance);
        var dialog = new ExportDialog(exportService, dataTable);

        Application.Run(dialog);

        if (dialog.ExportCompleted)
        {
            _statusBar.SetStatusMessage("Export completed");
        }
    }

    private void ShowHistoryDialog()
    {
        if (_environmentUrl == null)
        {
            MessageBox.ErrorQuery("History", "No environment selected. Query history is per-environment.", "OK");
            return;
        }

#pragma warning disable PPDS013 // Fire-and-forget with proper error handling
        _ = ShowHistoryDialogAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _errorService.ReportError("Failed to load query history", t.Exception, "LoadHistory");
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
    }

    private async Task ShowHistoryDialogAsync()
    {
        if (_environmentUrl == null) return;

        var historyService = await _session.GetQueryHistoryServiceAsync(_environmentUrl, CancellationToken.None);
        var dialog = new QueryHistoryDialog(historyService, _environmentUrl);

        Application.MainLoop?.Invoke(() =>
        {
            Application.Run(dialog);

            if (dialog.SelectedEntry != null)
            {
                _queryInput.Text = dialog.SelectedEntry.Sql;
                _statusBar.SetStatusMessage("Query loaded from history. Press Ctrl+Enter to execute.");
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Unregister screen hotkeys
            foreach (var registration in _hotkeyRegistrations)
            {
                registration.Dispose();
            }
            _hotkeyRegistrations.Clear();

            // Clear active screen (MainWindow will set itself when it regains focus)
            _hotkeyRegistry.SetActiveScreen(null);

            // Unsubscribe from session events
            _session.EnvironmentChanged -= OnEnvironmentChanged;
        }

        base.Dispose(disposing);
    }
}
