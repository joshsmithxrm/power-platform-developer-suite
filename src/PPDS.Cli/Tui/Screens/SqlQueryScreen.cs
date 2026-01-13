using System.Net.Http;
using PPDS.Auth.Credentials;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using PPDS.Cli.Tui.Views;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// SQL Query screen for executing queries against Dataverse and viewing results.
/// Implements ITuiScreen for hosting in the TuiShell.
/// </summary>
internal sealed class SqlQueryScreen : ITuiScreen, ITuiStateCapture<SqlQueryScreenState>
{
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly InteractiveSession _session;
    private readonly ITuiErrorService _errorService;
    private readonly List<IDisposable> _hotkeyRegistrations = new();
    private IHotkeyRegistry? _hotkeyRegistry;

    private readonly View _content;
    private readonly FrameView _queryFrame;
    private readonly TextView _queryInput;
    private readonly QueryResultsTableView _resultsTable;
    private readonly TextField _filterField;
    private readonly FrameView _filterFrame;
    private readonly TuiSpinner _statusSpinner;
    private readonly Label _statusLabel;

    private string? _environmentUrl;
    private string? _lastSql;
    private string? _lastPagingCookie;
    private int _lastPageNumber = 1;
    private bool _isExecuting;
    private string _statusText = "Ready";
    private string? _lastErrorMessage;
    private bool _disposed;

    /// <inheritdoc />
    public View Content => _content;

    /// <inheritdoc />
    public string Title => _environmentUrl != null
        ? $"SQL Query - {_session.CurrentEnvironmentDisplayName ?? _environmentUrl}"
        : "SQL Query";

    // Note: Keep underscore on MenuBarItem (_Query) for Alt+Q to open menu.
    // Remove underscores from MenuItems - they create global Alt+letter hotkeys in Terminal.Gui.
    /// <inheritdoc />
    public MenuBarItem[]? ScreenMenuItems => new[]
    {
        new MenuBarItem("_Query", new MenuItem[]
        {
            new("Execute", "Ctrl+Enter", () => _ = ExecuteQueryAsync()),
            new("Export Results", "Ctrl+E", ShowExportDialog),
            new("History", "Ctrl+Shift+H", ShowHistoryDialog),
            new("", "", () => {}, null, null, Key.Null), // Separator
            new("Filter Results", "/", ShowFilter),
        })
    };

    /// <inheritdoc />
    public event Action? CloseRequested;

    public SqlQueryScreen(Action<DeviceCodeInfo>? deviceCodeCallback, InteractiveSession session)
    {
        _deviceCodeCallback = deviceCodeCallback;
        _session = session;
        _errorService = session.GetErrorService();

        // Create the container view for the content
        _content = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _content.ColorScheme = TuiColorPalette.Default;

        // Query input area
        _queryFrame = new FrameView("Query (Ctrl+Enter to execute, F6 to toggle focus)")
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

        // Handle special keys directly on TextView before Terminal.Gui's default handling
        _queryInput.KeyPress += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.CtrlMask | Key.A:
                    // Select all text
                    var text = _queryInput.Text?.ToString() ?? string.Empty;
                    if (text.Length > 0)
                    {
                        _queryInput.SelectionStartColumn = 0;
                        _queryInput.SelectionStartRow = 0;
                        var lines = text.Split('\n');
                        var lastRow = lines.Length - 1;
                        var lastCol = lines[lastRow].TrimEnd('\r').Length;
                        _queryInput.CursorPosition = new Point(lastCol, lastRow);
                        _queryInput.SetNeedsDisplay();
                    }
                    e.Handled = true;
                    break;

                case Key.ShiftMask | Key.Space:
                    // Shift+Space should insert space (Terminal.Gui doesn't handle this by default)
                    _queryInput.ProcessKey(new KeyEvent(Key.Space, new KeyModifiers()));
                    e.Handled = true;
                    break;

                case Key.ShiftMask | Key.DeleteChar:
                    // Shift+Delete should delete (forward delete)
                    _queryInput.ProcessKey(new KeyEvent(Key.DeleteChar, new KeyModifiers()));
                    e.Handled = true;
                    break;

                case Key.ShiftMask | Key.Backspace:
                    // Shift+Backspace should backspace
                    _queryInput.ProcessKey(new KeyEvent(Key.Backspace, new KeyModifiers()));
                    e.Handled = true;
                    break;
            }
        };

        _queryFrame.Add(_queryInput);

        // Filter field (hidden by default)
        _filterFrame = new FrameView("Filter (/)")
        {
            X = 0,
            Y = Pos.Bottom(_queryFrame),
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

        // Results table (leave room for status line at bottom)
        _resultsTable = new QueryResultsTableView
        {
            X = 0,
            Y = Pos.Bottom(_queryFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill() - 1
        };
        _resultsTable.LoadMoreRequested += OnLoadMoreRequested;

        // Status area at bottom - spinner and label share same position
        _statusSpinner = new TuiSpinner
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Visible = false
        };

        _statusLabel = new Label("Ready")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1
        };

        _content.Add(_queryFrame, _filterFrame, _resultsTable, _statusSpinner, _statusLabel);

        // Visual focus indicators - only change title, not colors
        // The table's built-in selection highlighting is sufficient
        _queryFrame.Enter += (_) =>
        {
            _queryFrame.Title = "\u25b6 Query (Ctrl+Enter to execute, F6 to toggle focus)";
        };
        _queryFrame.Leave += (_) =>
        {
            _queryFrame.Title = "Query (Ctrl+Enter to execute, F6 to toggle focus)";
        };
        _resultsTable.Enter += (_) =>
        {
            _resultsTable.Title = "\u25b6 Results";
        };
        _resultsTable.Leave += (_) =>
        {
            _resultsTable.Title = "Results";
        };

        // Subscribe to environment changes from the session
        _session.EnvironmentChanged += OnEnvironmentChanged;

        // Initialize from session's current environment
        if (_session.CurrentEnvironmentUrl != null)
        {
            _environmentUrl = _session.CurrentEnvironmentUrl;
            _resultsTable.SetEnvironmentUrl(_environmentUrl);
        }

        // Set up keyboard handling for context-dependent shortcuts
        SetupKeyboardHandling();
    }

    /// <inheritdoc />
    public void OnActivated(IHotkeyRegistry hotkeyRegistry)
    {
        _hotkeyRegistry = hotkeyRegistry;

        // Register screen-scope hotkeys
        _hotkeyRegistrations.Add(hotkeyRegistry.Register(
            Key.CtrlMask | Key.E,
            HotkeyScope.Screen,
            "Export results",
            ShowExportDialog,
            owner: this));

        _hotkeyRegistrations.Add(hotkeyRegistry.Register(
            Key.CtrlMask | Key.ShiftMask | Key.H,
            HotkeyScope.Screen,
            "Query history",
            ShowHistoryDialog,
            owner: this));

        _hotkeyRegistrations.Add(hotkeyRegistry.Register(
            Key.F6,
            HotkeyScope.Screen,
            "Toggle query/results focus",
            () =>
            {
                if (_queryInput.HasFocus)
                    _resultsTable.SetFocus();
                else
                    _queryInput.SetFocus();
            },
            owner: this));

        _hotkeyRegistrations.Add(hotkeyRegistry.Register(
            Key.CtrlMask | Key.Enter,
            HotkeyScope.Screen,
            "Execute query",
            () => _ = ExecuteQueryAsync(),
            owner: this));
    }

    /// <inheritdoc />
    public void OnDeactivating()
    {
        foreach (var registration in _hotkeyRegistrations)
        {
            registration.Dispose();
        }
        _hotkeyRegistrations.Clear();
        _hotkeyRegistry = null;
    }

    private void SetupKeyboardHandling()
    {
        // Context-dependent shortcuts that need local state checks
        _content.KeyPress += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.Esc:
                    if (_filterFrame.Visible)
                    {
                        HideFilter();
                    }
                    else if (!_queryInput.HasFocus)
                    {
                        // Return to query from results
                        _queryInput.SetFocus();
                    }
                    else
                    {
                        // Request close when already in query
                        CloseRequested?.Invoke();
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

                case Key.CtrlMask | Key.W:
                    // Ctrl+W always closes the screen immediately
                    CloseRequested?.Invoke();
                    e.Handled = true;
                    break;

                case Key k when k == (Key)'q' || k == (Key)'Q':
                    // Q closes when not typing in query input (vim-style)
                    if (!_queryInput.HasFocus)
                    {
                        CloseRequested?.Invoke();
                        e.Handled = true;
                    }
                    break;
            }
        };
    }

    private void OnEnvironmentChanged(string? url, string? displayName)
    {
        Application.MainLoop?.Invoke(() =>
        {
            if (url != null)
            {
                _environmentUrl = url;
                _resultsTable.SetEnvironmentUrl(_environmentUrl);

                // Clear stale results from previous environment
                _resultsTable.ClearData();
                _lastSql = null;
                _lastPagingCookie = null;
                _lastPageNumber = 1;
            }
            else
            {
                _environmentUrl = null;
            }
        });
    }

    private async Task ExecuteQueryAsync()
    {
        var sql = _queryInput.Text.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sql))
        {
            _statusText = "Error: Query cannot be empty.";
            _statusLabel.Text = _statusText;
            return;
        }

        if (_environmentUrl == null)
        {
            _statusText = "Error: No environment selected.";
            _statusLabel.Text = _statusText;
            return;
        }

        TuiDebugLog.Log($"Starting query execution for: {_environmentUrl}");

        _isExecuting = true;
        _lastErrorMessage = null;

        // Show spinner, hide status label
        _statusLabel.Visible = false;
        _statusSpinner.Start("Executing query...");

        try
        {
            TuiDebugLog.Log($"Getting SQL query service for URL: {_environmentUrl}");

            var service = await _session.GetSqlQueryServiceAsync(_environmentUrl, CancellationToken.None);
            TuiDebugLog.Log("Got service, executing query...");

            var request = new SqlQueryRequest
            {
                Sql = sql,
                PageNumber = null,
                PagingCookie = null
            };

            var result = await service.ExecuteAsync(request, CancellationToken.None);
            TuiDebugLog.Log($"Query complete: {result.Result.Count} rows in {result.Result.ExecutionTimeMs}ms");

            Application.MainLoop?.Invoke(() =>
            {
                _resultsTable.LoadResults(result.Result);
                _lastSql = sql;
                _lastPagingCookie = result.Result.PagingCookie;
                _lastPageNumber = result.Result.PageNumber;

                var moreText = result.Result.MoreRecords ? " (more available)" : "";
                _statusText = $"Returned {result.Result.Count} rows in {result.Result.ExecutionTimeMs}ms{moreText}";

                // Stop spinner, show status label
                _statusSpinner.Stop();
                _statusLabel.Text = _statusText;
                _statusLabel.Visible = true;
                _isExecuting = false;
            });

            // Save to history (fire-and-forget)
#pragma warning disable PPDS013 // Fire-and-forget - history save shouldn't block UI
            _ = SaveToHistoryAsync(sql, result.Result.Count, result.Result.ExecutionTimeMs);
#pragma warning restore PPDS013
        }
        catch (Exception ex)
        {
            _errorService.ReportError("Query execution failed", ex, "ExecuteQuery");
            _lastErrorMessage = ex.Message;
            _statusText = $"Error: {ex.Message}";

            // Stop spinner, show error in status label
            _statusSpinner.Stop();
            _statusLabel.Text = _statusText;
            _statusLabel.Visible = true;
            _isExecuting = false;
        }
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

            Application.MainLoop?.Invoke(() =>
            {
                _resultsTable.AddPage(result.Result);
                _lastPagingCookie = result.Result.PagingCookie;
                _lastPageNumber = result.Result.PageNumber;
            });
        }
        catch (InvalidOperationException ex)
        {
            _errorService.ReportError("Failed to load more results", ex, "LoadMoreResults");
        }
        catch (HttpRequestException ex)
        {
            _errorService.ReportError("Network error loading results", ex, "LoadMoreResults");
        }
    }

    private void ShowFilter()
    {
        _filterFrame.Visible = true;
        _resultsTable.Y = Pos.Bottom(_filterFrame);
        _filterField.Text = string.Empty;
        _filterField.SetFocus();
    }

    private void HideFilter()
    {
        _filterFrame.Visible = false;
        _resultsTable.Y = Pos.Bottom(_queryFrame);
        _filterField.Text = string.Empty;
        _resultsTable.ApplyFilter(null);
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

        var columnTypes = _resultsTable.GetColumnTypes();
        var exportService = new ExportService(Microsoft.Extensions.Logging.Abstractions.NullLogger<ExportService>.Instance);
        var dialog = new ExportDialog(exportService, dataTable, columnTypes);

        Application.Run(dialog);
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
            }
        });
    }

    /// <inheritdoc />
    public SqlQueryScreenState CaptureState()
    {
        var dataTable = _resultsTable.GetDataTable();
        var columnHeaders = new List<string>();
        if (dataTable != null)
        {
            foreach (System.Data.DataColumn col in dataTable.Columns)
            {
                columnHeaders.Add(col.ColumnName);
            }
        }

        var totalRows = dataTable?.Rows.Count ?? 0;
        var pageSize = _resultsTable.PageSize;
        var totalPages = pageSize > 0 && totalRows > 0
            ? (int)Math.Ceiling((double)totalRows / pageSize)
            : 0;
        var currentPage = _lastPageNumber;

        return new SqlQueryScreenState(
            QueryText: _queryInput.Text?.ToString() ?? string.Empty,
            IsExecuting: _isExecuting,
            StatusText: _statusText,
            ResultCount: totalRows > 0 ? totalRows : null,
            CurrentPage: totalRows > 0 ? currentPage : null,
            TotalPages: totalPages > 0 ? totalPages : null,
            PageSize: pageSize,
            ColumnHeaders: columnHeaders,
            VisibleRowCount: _resultsTable.VisibleRowCount,
            FilterText: _filterField.Text?.ToString() ?? string.Empty,
            FilterVisible: _filterFrame.Visible,
            CanExport: totalRows > 0,
            ErrorMessage: _lastErrorMessage);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Ensure hotkeys are unregistered
        OnDeactivating();

        // Unsubscribe from session events
        _session.EnvironmentChanged -= OnEnvironmentChanged;

        _content.Dispose();
    }
}
