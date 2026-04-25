using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tui.Components;
using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using PPDS.Cli.Tui.Views;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Resilience;
using PPDS.Dataverse.Sql.Intellisense;
using SqlSourceTokenizer = PPDS.Query.Intellisense.SqlSourceTokenizer;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// SQL Query screen for executing queries against Dataverse and viewing results.
/// Inherits TuiScreenBase for lifecycle, hotkey, and dispose management.
/// </summary>
internal sealed class SqlQueryScreen : TuiScreenBase, ITuiStateCapture<SqlQueryScreenState>
{
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly SqlQueryPresenter _presenter;

    /// <summary>
    /// Minimum editor height in rows (including frame border).
    /// </summary>
    private const int MinEditorHeight = 3;

    /// <summary>
    /// Default editor height in rows (including frame border).
    /// </summary>
    private const int DefaultEditorHeight = 6;

    private readonly FrameView _queryFrame;
    private readonly SyntaxHighlightedTextView _queryInput;
    private readonly QueryResultsTableView _resultsTable;
    private readonly TextField _filterField;
    private readonly FrameView _filterFrame;
    private readonly SplitterView _splitter;
    private readonly TuiSpinner _statusSpinner;
    private readonly Label _statusLabel;

    // Stored presenter event handlers for R3-compliant unsubscription in OnDispose.
    private readonly Action<IReadOnlyList<QueryColumn>, string> _onStreamingColumnsReady;
    private readonly Action<IReadOnlyList<IReadOnlyDictionary<string, QueryValue>>, IReadOnlyList<QueryColumn>, bool, int> _onStreamingRowsReady;
    private readonly Action<string> _onStatusChanged;
    private readonly Action<string, long, QueryExecutionMode?> _onExecutionComplete;
    private readonly Action<DataverseAuthenticationException> _onAuthenticationRequired;
    private readonly Action<PpdsException> _onDmlConfirmationRequired;
    private readonly Action _onQueryCancelled;
    private readonly Action<string> _onErrorOccurred;
    private readonly Action<QueryResult> _onPageLoaded;

    /// <summary>
    /// Current editor height in rows. Adjusted by keyboard (Ctrl+Shift+Up/Down)
    /// or mouse drag on the splitter bar.
    /// </summary>
    private int _editorHeight = DefaultEditorHeight;

    private string _statusText = "Ready";

    /// <inheritdoc />
    public override string Title
    {
        get
        {
            var mode = _presenter.UseFetchXmlMode ? "FetchXML" : "SQL Query";
            return EnvironmentUrl != null
                ? $"{mode} - {EnvironmentDisplayName ?? EnvironmentUrl}"
                : mode;
        }
    }

    // Note: Keep underscore on MenuBarItem (_Query) for Alt+Q to open menu.
    // Remove underscores from MenuItems - they create global Alt+letter hotkeys in Terminal.Gui.
    /// <inheritdoc />
    public override MenuBarItem[]? ScreenMenuItems => new[]
    {
        new MenuBarItem("_Query", new MenuItem[]
        {
            new("Execute", "F5", StartPresenterExecution),
            new("Show FetchXML", "Ctrl+Shift+F / F9", ShowFetchXmlDialog),
            new("Show Execution Plan", "Ctrl+Shift+E / F7", ShowExecutionPlanDialog),
            new("History", "Ctrl+Shift+H / F8", ShowHistoryDialog),
            new("", "", () => {}, null, null, Key.Null), // Separator
            new("Filter Results", "/", ShowFilter),
            new("", "", () => {}, null, null, Key.Null), // Separator
            new(_presenter.UseFetchXmlMode ? "\u2713 FetchXML Input" : "  FetchXML Input", "F11", ToggleFetchXmlMode),
            new(_presenter.UseTdsEndpoint ? "\u2713 TDS Read Replica" : "  TDS Read Replica", "F10", ToggleTdsEndpoint),
        })
    };

    /// <inheritdoc />
    public override Action? ExportAction => _resultsTable.GetDataTable() != null ? ShowExportDialog : null;

    public SqlQueryScreen(Action<DeviceCodeInfo>? deviceCodeCallback, InteractiveSession session, string? environmentUrl = null, string? environmentDisplayName = null)
        : base(session, environmentUrl)
    {
        if (environmentDisplayName != null)
            EnvironmentDisplayName = environmentDisplayName;
        _deviceCodeCallback = deviceCodeCallback;

        // Create presenter (contains all query orchestration logic, Terminal.Gui-free)
        _presenter = new SqlQueryPresenter(session, EnvironmentUrl ?? string.Empty);

        // Query input area
        _queryFrame = new FrameView("Query (F5 to execute, Ctrl+Space for suggestions, Alt+\u2191\u2193 to resize, F6 to toggle focus)")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = _editorHeight,
            ColorScheme = TuiColorPalette.Default
        };

        _queryInput = new SyntaxHighlightedTextView(new SqlSourceTokenizer(), TuiColorPalette.SqlSyntax)
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
                case Key.Esc:
                    if (_presenter.IsExecuting)
                    {
                        _presenter.CancelQuery();
                        _statusSpinner!.Message = "Cancelling...";
                        e.Handled = true;
                    }
                    break;

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

                case Key.AltMask | Key.Backspace:
                    // Delete word before cursor (Alt variant)
                    DeleteWordBackward();
                    e.Handled = true;
                    break;

                case Key.CtrlMask | Key.Backspace:
                    // Delete word before cursor (Ctrl variant)
                    DeleteWordBackward();
                    e.Handled = true;
                    break;

                case Key.AltMask | Key.DeleteChar:
                    // Delete word after cursor (Alt variant)
                    DeleteWordForward();
                    e.Handled = true;
                    break;

                case Key.CtrlMask | Key.DeleteChar:
                    // Delete word after cursor (Ctrl variant)
                    DeleteWordForward();
                    e.Handled = true;
                    break;

                case Key.AltMask | Key.CursorLeft:
                    // Word navigation backward (Alt variant) - forward to Ctrl+Left
                    _queryInput.ProcessKey(new KeyEvent(Key.CursorLeft | Key.CtrlMask, new KeyModifiers { Ctrl = true }));
                    e.Handled = true;
                    break;

                case Key.AltMask | Key.CursorRight:
                    // Word navigation forward (Alt variant) - forward to Ctrl+Right
                    _queryInput.ProcessKey(new KeyEvent(Key.CursorRight | Key.CtrlMask, new KeyModifiers { Ctrl = true }));
                    e.Handled = true;
                    break;

                case Key.CtrlMask | Key.Z:
                    // Undo - pass through to TextView's built-in handler
                    // Terminal.Gui has this bound but something may be blocking it
                    _queryInput.ProcessKey(new KeyEvent(Key.Z | Key.CtrlMask, new KeyModifiers { Ctrl = true }));
                    e.Handled = true;
                    break;

                case Key.CtrlMask | Key.Y:
                    // Ctrl+Y is emacs yank (paste) in Terminal.Gui — consume to prevent
                    // accidental paste that conflicts with Ctrl+V paste handling.
                    // True redo is not supported by Terminal.Gui's TextView.
                    e.Handled = true;
                    break;
            }
        };

        _queryFrame.Add(_queryInput);

        // Splitter bar between query editor and results
        _splitter = new SplitterView
        {
            X = 0,
            Y = Pos.Bottom(_queryFrame)
        };
        _splitter.Dragged += OnSplitterDragged;

        // Filter field (hidden by default)
        _filterFrame = new FrameView("Filter (/)")
        {
            X = 0,
            Y = Pos.Bottom(_splitter),
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
            Y = Pos.Bottom(_splitter),
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

        Content.Add(_queryFrame, _splitter, _filterFrame, _resultsTable, _statusSpinner, _statusLabel);

        // Visual focus indicators - only change title, not colors
        // The table's built-in selection highlighting is sufficient
        _queryFrame.Enter += (_) => UpdateQueryFrameTitle(focused: true);
        _queryFrame.Leave += (_) => UpdateQueryFrameTitle(focused: false);
        _resultsTable.Enter += (_) =>
        {
            _resultsTable.Title = "\u25b6 Results";
        };
        _resultsTable.Leave += (_) =>
        {
            _resultsTable.Title = "Results";
        };

        // Initialize results table with environment URL (already set by base constructor)
        if (EnvironmentUrl != null)
        {
            _resultsTable.SetEnvironmentUrl(EnvironmentUrl);
        }

        // Eagerly resolve IntelliSense language service so completions work immediately
        if (EnvironmentUrl != null)
        {
            ErrorService.FireAndForget(ResolveLanguageServiceAsync(), "ResolveLanguageService");
        }

        // Show status feedback when IntelliSense is requested before the service is ready
        _queryInput.IntelliSenseUnavailable += () =>
        {
            if (EnvironmentUrl == null)
            {
                _statusLabel.Text = "IntelliSense unavailable — no environment selected";
            }
            else
            {
                _statusLabel.Text = "IntelliSense loading...";
            }
        };

        // Pre-allocate stored handlers for R3-compliant unsubscription in OnDispose.
        // These lambdas close over `this` and reference UI fields (_resultsTable, _statusSpinner,
        // _statusLabel, _queryInput) — they must be assigned after those fields are initialized.
        _onStreamingColumnsReady = (columns, entityLogicalName) =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                try
                {
                    _resultsTable.InitializeStreamingColumns(columns, entityLogicalName);
                    NotifyMenuChanged();
                }
                catch (Exception ex)
                {
                    ErrorService.ReportError("Failed to display streaming columns", ex, "Presenter.StreamingColumnsReady");
                }
            });
        };

        _onStreamingRowsReady = (rows, columns, isComplete, totalRowsSoFar) =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                try
                {
                    _resultsTable.AppendStreamingRows(rows, columns);
                }
                catch (Exception ex)
                {
                    ErrorService.ReportError("Failed to display streaming results", ex, "Presenter.StreamingRowsReady");
                }
            });
        };

        _onStatusChanged = (statusMessage) =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusSpinner.Message = statusMessage;
                _statusLabel.Text = statusMessage;
            });
        };

        _onExecutionComplete = (statusText, elapsedMs, executionMode) =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusText = statusText;
                _statusSpinner.Stop();
                _statusLabel.Text = _statusText;
                _statusLabel.Visible = true;
            });
        };

        _onAuthenticationRequired = (authEx) =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusSpinner.Stop();
                _statusLabel.Visible = true;

                TuiDebugLog.Log($"Authentication error: {authEx.Message}");

                var dialog = new ReAuthenticationDialog(authEx.UserMessage, Session);
                Application.Run(dialog);

                if (dialog.ShouldReauthenticate)
                {
                    TuiDebugLog.Log("User chose to re-authenticate");
                    try
                    {
                        _statusLabel.Text = "Re-authenticating...";
                        ErrorService.FireAndForget(HandleReauthAndRetryAsync(), "ReauthAndRetry");
                    }
                    catch (Exception reAuthEx)
                    {
                        ErrorService.ReportError("Re-authentication failed", reAuthEx, "ExecuteQuery.ReAuth");
                        _statusText = $"Re-authentication failed: {reAuthEx.Message}";
                        _statusLabel.Text = _statusText;
                    }
                }
                else
                {
                    TuiDebugLog.Log("User cancelled re-authentication");
                    ErrorService.ReportError("Session expired", authEx, "ExecuteQuery");
                    _statusText = $"Error: {authEx.Message}";
                    _statusLabel.Text = _statusText;
                }
            });
        };

        _onDmlConfirmationRequired = (dmlEx) =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusSpinner.Stop();
                _statusLabel.Visible = true;

                var result = MessageBox.Query(
                    "Confirm DML Operation",
                    dmlEx.Message + "\n\nDo you want to proceed?",
                    "Execute", "Cancel");

                if (result == 0) // "Execute" button
                {
                    TuiDebugLog.Log("User confirmed DML operation, retrying with IsConfirmed=true");
                    _statusLabel.Visible = false;
                    _presenter.ConfirmDml();
                    var queryText = _queryInput.Text.ToString() ?? string.Empty;
                    var task = _presenter.UseFetchXmlMode
                        ? _presenter.ExecuteFetchXmlAsync(queryText, ScreenCancellation)
                        : _presenter.ExecuteAsync(queryText, ScreenCancellation);
                    ErrorService.FireAndForget(task, "RetryDmlConfirmed");
                }
                else
                {
                    TuiDebugLog.Log("User cancelled DML operation");
                    _statusText = "DML operation cancelled.";
                    _statusLabel.Text = _statusText;
                }
            });
        };

        _onQueryCancelled = () =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusSpinner.Stop();
                _statusLabel.Text = "Query cancelled.";
                _statusLabel.Visible = true;
            });
        };

        _onErrorOccurred = (errorMessage) =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusText = $"Error: {errorMessage}";
                _statusSpinner.Stop();
                _statusLabel.Text = _statusText;
                _statusLabel.Visible = true;
            });
        };

        _onPageLoaded = (queryResult) =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                try
                {
                    _resultsTable.AddPage(queryResult);
                }
                catch (Exception ex)
                {
                    ErrorService.ReportError("Failed to load additional results", ex, "LoadMore.AddPage");
                    TuiDebugLog.Log($"Error in LoadMore callback: {ex}");
                }
            });
        };

        // Subscribe to presenter events — marshal all UI updates to the main thread
        SubscribeToPresenterEvents();

        // Set up keyboard handling for context-dependent shortcuts
        SetupKeyboardHandling();
    }

    /// <summary>
    /// Subscribes to all presenter events using stored handler fields for R3-compliant
    /// unsubscription in <see cref="OnDispose"/>.
    /// </summary>
    private void SubscribeToPresenterEvents()
    {
        _presenter.StreamingColumnsReady += _onStreamingColumnsReady;
        _presenter.StreamingRowsReady += _onStreamingRowsReady;
        _presenter.StatusChanged += _onStatusChanged;
        _presenter.ExecutionComplete += _onExecutionComplete;
        _presenter.AuthenticationRequired += _onAuthenticationRequired;
        _presenter.DmlConfirmationRequired += _onDmlConfirmationRequired;
        _presenter.QueryCancelled += _onQueryCancelled;
        _presenter.ErrorOccurred += _onErrorOccurred;
        _presenter.PageLoaded += _onPageLoaded;
    }

    /// <inheritdoc />
    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.CtrlMask | Key.E, "Export results", ShowExportDialog);
        RegisterHotkey(registry, Key.CtrlMask | Key.ShiftMask | Key.E, "Show execution plan", ShowExecutionPlanDialog);
        RegisterHotkey(registry, Key.CtrlMask | Key.ShiftMask | Key.H, "Query history", ShowHistoryDialog);
        RegisterHotkey(registry, Key.F6, "Toggle query/results focus", () =>
        {
            if (_queryInput.HasFocus)
                _resultsTable.SetFocus();
            else
                _queryInput.SetFocus();
        });
        RegisterHotkey(registry, Key.F5, "Execute query", StartPresenterExecution);
        RegisterHotkey(registry, Key.CtrlMask | Key.ShiftMask | Key.F, "Show FetchXML", ShowFetchXmlDialog);
        // F10 instead of Ctrl+Shift+T: terminals cannot distinguish Ctrl+T from Ctrl+Shift+T
        // (they send the same keycode), so the global Ctrl+T (new tab) always wins. See #580.
        RegisterHotkey(registry, Key.F10, "Toggle TDS Endpoint", ToggleTdsEndpoint);
        RegisterHotkey(registry, Key.F11, "Toggle FetchXML Input", ToggleFetchXmlMode);
        // F-key alternatives for Linux compatibility (Ctrl+Shift combos don't work on Linux terminals)
        RegisterHotkey(registry, Key.F7, "Show execution plan", ShowExecutionPlanDialog);
        RegisterHotkey(registry, Key.F8, "Query history", ShowHistoryDialog);
        RegisterHotkey(registry, Key.F9, "Show FetchXML", ShowFetchXmlDialog);
    }

    private void SetupKeyboardHandling()
    {
        // Context-dependent shortcuts that need local state checks
        Content.KeyPress += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key.Esc:
                    if (_filterFrame.Visible)
                    {
                        HideFilter();
                        e.Handled = true;
                    }
                    else if (!_queryInput.HasFocus)
                    {
                        // Return to query from results
                        _queryInput.SetFocus();
                        e.Handled = true;
                    }
                    // Escape does nothing when already in query editor — use Ctrl+W to close tab
                    break;

                case Key k when k == (Key)'/':
                    if (!_queryInput.HasFocus)
                    {
                        ShowFilter();
                        e.Handled = true;
                    }
                    break;

                case Key k when k == (Key)'q' || k == (Key)'Q':
                    // Q closes when not typing in query input (vim-style)
                    if (!_queryInput.HasFocus)
                    {
                        RequestClose();
                        e.Handled = true;
                    }
                    break;

                case Key.CursorUp | Key.AltMask:
                case Key.CursorUp | Key.CtrlMask | Key.ShiftMask:
                    // Shrink editor (Alt+Up primary, Ctrl+Shift+Up secondary)
                    ResizeEditor(-1);
                    e.Handled = true;
                    break;

                case Key.CursorDown | Key.AltMask:
                case Key.CursorDown | Key.CtrlMask | Key.ShiftMask:
                    // Grow editor (Alt+Down primary, Ctrl+Shift+Down secondary)
                    ResizeEditor(1);
                    e.Handled = true;
                    break;
            }
        };
    }

    /// <summary>
    /// Calculates the maximum allowed editor height (80% of available screen height).
    /// </summary>
    private int GetMaxEditorHeight()
    {
        // Content.Frame.Height may be 0 before layout; fall back to a sensible default
        var available = Content.Frame.Height > 0 ? Content.Frame.Height : 25;
        return Math.Max(MinEditorHeight, (int)(available * 0.8));
    }

    /// <summary>
    /// Resizes the query editor by the specified delta (positive = grow, negative = shrink).
    /// Clamps to <see cref="MinEditorHeight"/> and 80% of screen height.
    /// </summary>
    private void ResizeEditor(int delta)
    {
        var newHeight = Math.Clamp(_editorHeight + delta, MinEditorHeight, GetMaxEditorHeight());
        if (newHeight == _editorHeight) return;

        _editorHeight = newHeight;
        _queryFrame.Height = _editorHeight;
        Content.LayoutSubviews();
        Content.SetNeedsDisplay();
    }

    /// <summary>
    /// Handles mouse drag events from the splitter bar.
    /// </summary>
    private void OnSplitterDragged(int delta)
    {
        ResizeEditor(delta);
    }

    /// <summary>
    /// Resolves the <see cref="ISqlLanguageService"/> eagerly so IntelliSense works
    /// as soon as the screen opens, without waiting for the first query execution.
    /// </summary>
    private async Task ResolveLanguageServiceAsync()
    {
        TuiDebugLog.Log($"ResolveLanguageServiceAsync starting for {EnvironmentUrl}");
        try
        {
            var provider = await GetProviderAsync(ScreenCancellation);
            TuiDebugLog.Log("Service provider obtained, resolving ISqlLanguageService...");
            var langService = provider.GetService<ISqlLanguageService>();
            if (langService != null)
            {
                _queryInput.LanguageService = langService;
                TuiDebugLog.Log("ISqlLanguageService resolved and assigned — IntelliSense is now active");
            }
            else
            {
                TuiDebugLog.Log("ISqlLanguageService resolved to NULL — IntelliSense will not work");
            }
        }
        catch (OperationCanceledException)
        {
            TuiDebugLog.Log("ResolveLanguageServiceAsync cancelled (screen closed)");
        }
        catch (Exception ex)
        {
            TuiDebugLog.Log($"Failed to resolve ISqlLanguageService: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts a query execution via the presenter with UI spinner management.
    /// </summary>
    private void StartPresenterExecution()
    {
        var sql = _queryInput.Text.ToString() ?? string.Empty;

        if (EnvironmentUrl == null)
        {
            _statusText = "Error: No environment selected.";
            _statusLabel.Text = _statusText;
            return;
        }

        // Show spinner, hide status label
        _statusLabel.Visible = false;
        _statusSpinner.Start("Executing query... (press Escape to cancel)");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Tick elapsed time on the spinner every second
        var elapsedTimer = Application.MainLoop?.AddTimeout(TimeSpan.FromSeconds(1), (_) =>
        {
            if (!_presenter.IsExecuting) return false;
            _statusSpinner.Message = $"Executing query... {stopwatch.Elapsed.TotalSeconds:F0}s (press Escape to cancel)";
            return true;
        });

        // Subscribe to completion to clean up the timer
        void OnComplete(string s, long ms, QueryExecutionMode? m) => CleanUpTimer();
        void OnCancel() => CleanUpTimer();
        void OnError(string msg) => CleanUpTimer();
        void OnAuth(DataverseAuthenticationException ex) => CleanUpTimer();
        void OnDml(PpdsException ex) => CleanUpTimer();

        void CleanUpTimer()
        {
            if (elapsedTimer != null)
                Application.MainLoop?.RemoveTimeout(elapsedTimer);

            // Unsubscribe all one-shot timer cleanup handlers
            _presenter.ExecutionComplete -= OnComplete;
            _presenter.QueryCancelled -= OnCancel;
            _presenter.ErrorOccurred -= OnError;
            _presenter.AuthenticationRequired -= OnAuth;
            _presenter.DmlConfirmationRequired -= OnDml;
        }

        _presenter.ExecutionComplete += OnComplete;
        _presenter.QueryCancelled += OnCancel;
        _presenter.ErrorOccurred += OnError;
        _presenter.AuthenticationRequired += OnAuth;
        _presenter.DmlConfirmationRequired += OnDml;

        var task = _presenter.UseFetchXmlMode
            ? _presenter.ExecuteFetchXmlAsync(sql, ScreenCancellation)
            : _presenter.ExecuteAsync(sql, ScreenCancellation);
        ErrorService.FireAndForget(task, "ExecuteQuery");
    }

    /// <summary>
    /// Handles re-authentication and retries the query.
    /// </summary>
    private async Task HandleReauthAndRetryAsync()
    {
        await Session.InvalidateAndReauthenticateAsync(ProfileName, EnvironmentUrl, ScreenCancellation);

        TuiDebugLog.Log("Re-authentication successful, retrying query");

        Application.MainLoop?.Invoke(() =>
        {
            _statusLabel.Visible = false;
            StartPresenterExecution();
        });
    }

    private async Task OnLoadMoreRequested()
    {
        await _presenter.LoadMoreAsync(ScreenCancellation);
    }

    private void ToggleTdsEndpoint()
    {
        _presenter.ToggleTds();
        _statusLabel.SetNeedsDisplay();
        NotifyMenuChanged();
    }

    private void ToggleFetchXmlMode()
    {
        _presenter.ToggleFetchXml();
        UpdateQueryFrameTitle(focused: _queryFrame.HasFocus);
        _statusLabel.SetNeedsDisplay();
        NotifyMenuChanged();
    }

    private void UpdateQueryFrameTitle(bool focused)
    {
        var prefix = focused ? "▶ " : string.Empty;
        var body = _presenter.UseFetchXmlMode
            ? "FetchXML (F5 to execute, F11 to switch to SQL)"
            : "Query (F5 to execute, Ctrl+Space for suggestions, Alt+↑↓ to resize, F6 to toggle focus)";
        _queryFrame.Title = prefix + body;
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
        _resultsTable.Y = Pos.Bottom(_splitter);
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
        var exportService = Session.GetExportService();
        var dialog = new ExportDialog(exportService, dataTable, columnTypes, Session);

        Application.Run(dialog);
    }

    private void ShowHistoryDialog()
    {
        if (EnvironmentUrl == null)
        {
            MessageBox.ErrorQuery("History", "No environment selected. Query history is per-environment.", "OK");
            return;
        }

        // QueryHistoryService is local file-based, no Dataverse connection needed
        var historyService = Session.GetQueryHistoryService();
        var dialog = new QueryHistoryDialog(historyService, EnvironmentUrl, Session);

        Application.Run(dialog);

        if (dialog.SelectedEntry != null)
        {
            _queryInput.Text = dialog.SelectedEntry.Sql;
        }
    }

    private void ShowFetchXmlDialog()
    {
        var sql = _queryInput.Text?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(sql))
        {
            MessageBox.ErrorQuery("Show FetchXML", "Enter a SQL query first.", "OK");
            return;
        }

        if (EnvironmentUrl == null)
        {
            MessageBox.ErrorQuery("Show FetchXML", "No environment selected.", "OK");
            return;
        }

        ErrorService.FireAndForget(ShowFetchXmlDialogAsync(sql), "ShowFetchXml");
    }

    private async Task ShowFetchXmlDialogAsync(string sql)
    {
        // Caller guarantees EnvironmentUrl is non-null before calling this method
        var provider = await GetProviderAsync(ScreenCancellation);
        var sqlQueryService = provider.GetRequiredService<ISqlQueryService>();

        var fetchXml = sqlQueryService.TranspileSql(sql);

        Application.MainLoop?.Invoke(() =>
        {
            var dialog = new FetchXmlPreviewDialog(fetchXml, Session);
            Application.Run(dialog);
        });
    }

    private void ShowExecutionPlanDialog()
    {
        var sql = _queryInput.Text?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(sql))
        {
            MessageBox.ErrorQuery("Execution Plan", "Enter a SQL query first.", "OK");
            return;
        }

        if (EnvironmentUrl == null)
        {
            MessageBox.ErrorQuery("Execution Plan", "No environment selected.", "OK");
            return;
        }

        // If we have a cached plan from the last execution, show it immediately
        if (_presenter.LastExecutionPlan != null && sql == _presenter.LastSql)
        {
            ShowPlanDialog(_presenter.LastExecutionPlan, _presenter.LastExecutionTimeMs);
            return;
        }

        // Otherwise, fetch the plan
        ErrorService.FireAndForget(FetchAndShowPlanAsync(sql), "ShowExecutionPlan");
    }

    private async Task FetchAndShowPlanAsync(string sql)
    {
        try
        {
            var service = await GetSqlServiceAsync(ScreenCancellation);
            var plan = await service.ExplainAsync(sql, ScreenCancellation);

            Application.MainLoop?.Invoke(() => ShowPlanDialog(plan, 0));
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                MessageBox.ErrorQuery("Execution Plan", $"Failed to get plan: {ex.Message}", "OK");
            });
        }
    }

    private void ShowPlanDialog(QueryPlanDescription plan, long executionTimeMs)
    {
        var planText = QueryPlanView.FormatPlanTree(plan, executionTimeMs);

        using var dialog = new Dialog("Execution Plan", 80, 25)
        {
            ColorScheme = TuiColorPalette.Default
        };

        var textView = new TextView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 3,
            ReadOnly = true,
            WordWrap = false,
            Text = planText,
            ColorScheme = TuiColorPalette.ReadOnlyText
        };

        var closeButton = new Button("Close")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        dialog.Add(textView, closeButton);
        closeButton.SetFocus();
        Application.Run(dialog);
    }

    private void DeleteWordBackward()
    {
        var text = _queryInput.Text?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;

        // Get flat cursor position
        var pos = _queryInput.CursorPosition;
        var lines = text.Split('\n');
        var flatPos = 0;
        for (int i = 0; i < pos.Y && i < lines.Length; i++)
        {
            flatPos += lines[i].Length + 1; // +1 for newline
        }
        flatPos += Math.Min(pos.X, lines.Length > pos.Y ? lines[pos.Y].Length : 0);

        if (flatPos == 0) return;

        // Find word boundary going backward
        var start = flatPos - 1;

        // Skip whitespace first
        while (start > 0 && char.IsWhiteSpace(text[start]))
            start--;

        // Then skip word characters
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            start--;

        // Delete from start to flatPos
        var newText = text.Remove(start, flatPos - start);
        _queryInput.Text = newText;

        // Reposition cursor
        var newFlatPos = start;
        var newRow = 0;
        var newCol = 0;
        var remaining = newFlatPos;
        var newLines = newText.Split('\n');
        foreach (var line in newLines)
        {
            if (remaining <= line.Length)
            {
                newCol = remaining;
                break;
            }
            remaining -= line.Length + 1;
            newRow++;
        }
        _queryInput.CursorPosition = new Point(newCol, newRow);
    }

    private void DeleteWordForward()
    {
        var text = _queryInput.Text?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;

        // Get flat cursor position
        var pos = _queryInput.CursorPosition;
        var lines = text.Split('\n');
        var flatPos = 0;
        for (int i = 0; i < pos.Y && i < lines.Length; i++)
        {
            flatPos += lines[i].Length + 1;
        }
        flatPos += Math.Min(pos.X, lines.Length > pos.Y ? lines[pos.Y].Length : 0);

        if (flatPos >= text.Length) return;

        // Find word boundary going forward
        var end = flatPos;

        // Skip word characters first
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;

        // Then skip whitespace
        while (end < text.Length && char.IsWhiteSpace(text[end]))
            end++;

        // Delete from flatPos to end
        var newText = text.Remove(flatPos, end - flatPos);
        _queryInput.Text = newText;

        // Cursor stays at same position (already correct after deletion)
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
        var currentPage = _presenter.LastPageNumber;

        return new SqlQueryScreenState(
            QueryText: _queryInput.Text?.ToString() ?? string.Empty,
            IsExecuting: _presenter.IsExecuting,
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
            ErrorMessage: _presenter.LastErrorMessage,
            EditorHeight: _editorHeight);
    }

    protected override void OnDispose()
    {
        // R3: unsubscribe all presenter events before disposing.
        _presenter.StreamingColumnsReady -= _onStreamingColumnsReady;
        _presenter.StreamingRowsReady -= _onStreamingRowsReady;
        _presenter.StatusChanged -= _onStatusChanged;
        _presenter.ExecutionComplete -= _onExecutionComplete;
        _presenter.AuthenticationRequired -= _onAuthenticationRequired;
        _presenter.DmlConfirmationRequired -= _onDmlConfirmationRequired;
        _presenter.QueryCancelled -= _onQueryCancelled;
        _presenter.ErrorOccurred -= _onErrorOccurred;
        _presenter.PageLoaded -= _onPageLoaded;
        _presenter.Dispose();
    }
}
