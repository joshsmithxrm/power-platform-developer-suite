using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Credentials;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tui.Components;
using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using PPDS.Cli.Tui.Views;
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
    /// <summary>
    /// Number of rows per streaming chunk. Results appear incrementally in batches
    /// of this size, providing visual feedback while loading continues in the background.
    /// </summary>
    private const int StreamingChunkSize = 100;

    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;

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

    /// <summary>
    /// Current editor height in rows. Adjusted by keyboard (Ctrl+Shift+Up/Down)
    /// or mouse drag on the splitter bar.
    /// </summary>
    private int _editorHeight = DefaultEditorHeight;

    private string? _lastSql;
    private string? _lastPagingCookie;
    private int _lastPageNumber = 1;
    private bool _isExecuting;
    private CancellationTokenSource? _queryCts;
    private string _statusText = "Ready";
    private string? _lastErrorMessage;
    private QueryPlanDescription? _lastExecutionPlan;
    private long _lastExecutionTimeMs;
    private bool _useTdsEndpoint;

    /// <inheritdoc />
    public override string Title => EnvironmentUrl != null
        ? $"SQL Query - {EnvironmentDisplayName ?? EnvironmentUrl}"
        : "SQL Query";

    // Note: Keep underscore on MenuBarItem (_Query) for Alt+Q to open menu.
    // Remove underscores from MenuItems - they create global Alt+letter hotkeys in Terminal.Gui.
    /// <inheritdoc />
    public override MenuBarItem[]? ScreenMenuItems => new[]
    {
        new MenuBarItem("_Query", new MenuItem[]
        {
            new("Execute", "F5", () => _ = ExecuteQueryAsync()),
            new("Show FetchXML", "Ctrl+Shift+F", ShowFetchXmlDialog),
            new("Show Execution Plan", "Ctrl+Shift+E", ShowExecutionPlanDialog),
            new("History", "Ctrl+Shift+H", ShowHistoryDialog),
            new("", "", () => {}, null, null, Key.Null), // Separator
            new("Filter Results", "/", ShowFilter),
            new("", "", () => {}, null, null, Key.Null), // Separator
            new(_useTdsEndpoint ? "\u2713 TDS Read Replica" : "  TDS Read Replica", "Ctrl+T", ToggleTdsEndpoint),
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
                    if (_isExecuting && _queryCts is { } cts)
                    {
                        cts.Cancel();
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
        _queryFrame.Enter += (_) =>
        {
            _queryFrame.Title = "\u25b6 Query (F5 to execute, Ctrl+Space for suggestions, Alt+\u2191\u2193 to resize, F6 to toggle focus)";
        };
        _queryFrame.Leave += (_) =>
        {
            _queryFrame.Title = "Query (F5 to execute, Ctrl+Space for suggestions, Alt+\u2191\u2193 to resize, F6 to toggle focus)";
        };
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

        // Set up keyboard handling for context-dependent shortcuts
        SetupKeyboardHandling();
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
        RegisterHotkey(registry, Key.F5, "Execute query", () => _ = ExecuteQueryAsync());
        RegisterHotkey(registry, Key.CtrlMask | Key.ShiftMask | Key.F, "Show FetchXML", ShowFetchXmlDialog);
        RegisterHotkey(registry, Key.CtrlMask | Key.T, "Toggle TDS Endpoint", ToggleTdsEndpoint);
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
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
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

    private async Task ExecuteQueryAsync()
    {
        var sql = _queryInput.Text.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sql))
        {
            _statusText = "Error: Query cannot be empty.";
            _statusLabel.Text = _statusText;
            return;
        }

        if (EnvironmentUrl == null)
        {
            _statusText = "Error: No environment selected.";
            _statusLabel.Text = _statusText;
            return;
        }

        TuiDebugLog.Log($"Starting streaming query execution for: {EnvironmentUrl}");

        // Create/reset query-level cancellation
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = CancellationTokenSource.CreateLinkedTokenSource(ScreenCancellation);
        var queryCt = _queryCts.Token;

        _isExecuting = true;
        _lastErrorMessage = null;

        // Show spinner, hide status label
        _statusLabel.Visible = false;
        _statusSpinner.Start("Executing query... (press Escape to cancel)");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var streamingStarted = false;

        // Tick elapsed time on the spinner every second until streaming starts
        var elapsedTimer = Application.MainLoop?.AddTimeout(TimeSpan.FromSeconds(1), (_) =>
        {
            if (!_isExecuting) return false;
            if (!streamingStarted)
                _statusSpinner.Message = $"Executing query... {stopwatch.Elapsed.TotalSeconds:F0}s (press Escape to cancel)";
            return true;
        });

        try
        {
        try
        {
            TuiDebugLog.Log($"Getting SQL query service for URL: {EnvironmentUrl}");

            var service = await Session.GetSqlQueryServiceAsync(EnvironmentUrl, queryCt);
            TuiDebugLog.Log("Got service, executing streaming query...");

            var request = new SqlQueryRequest
            {
                Sql = sql,
                PageNumber = null,
                PagingCookie = null,
                EnablePrefetch = true,
                UseTdsEndpoint = _useTdsEndpoint
            };

            IReadOnlyList<Dataverse.Query.QueryColumn>? columns = null;
            var totalRows = 0;
            var isFirstChunk = true;

            await foreach (var chunk in service.ExecuteStreamingAsync(request, StreamingChunkSize, queryCt))
            {
                // Capture column metadata from first chunk
                if (isFirstChunk && chunk.Columns != null)
                {
                    columns = chunk.Columns;
                }

                totalRows = chunk.TotalRowsSoFar;

                // Marshal UI updates to the main thread
                var chunkCapture = chunk;
                var columnsCapture = columns;
                var isFirst = isFirstChunk;

                Application.MainLoop?.Invoke(() =>
                {
                    try
                    {
                        if (isFirst && columnsCapture != null)
                        {
                            _resultsTable.InitializeStreamingColumns(
                                columnsCapture,
                                chunkCapture.EntityLogicalName ?? "unknown");
                            NotifyMenuChanged();
                        }

                        if (chunkCapture.Rows.Count > 0 && columnsCapture != null)
                        {
                            _resultsTable.AppendStreamingRows(chunkCapture.Rows, columnsCapture);
                        }

                        // Update spinner with progress
                        streamingStarted = true;
                        if (!chunkCapture.IsComplete)
                        {
                            _statusSpinner.Message = $"Loading... {chunkCapture.TotalRowsSoFar:N0} rows ({stopwatch.Elapsed.TotalSeconds:F1}s)";
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorService.ReportError("Failed to display streaming results", ex, "ExecuteQuery.StreamChunk");
                        TuiDebugLog.Log($"Error in streaming chunk callback: {ex}");
                    }
                });

                isFirstChunk = false;
            }

            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;

            TuiDebugLog.Log($"Streaming query complete: {totalRows} rows in {elapsedMs}ms");

            Application.MainLoop?.Invoke(() =>
            {
                try
                {
                    _lastSql = sql;
                    _lastPageNumber = 1;
                    _lastPagingCookie = null;
                    _lastExecutionTimeMs = elapsedMs;

                    _statusText = $"Returned {totalRows:N0} rows in {elapsedMs}ms";
                }
                catch (Exception ex)
                {
                    ErrorService.ReportError("Failed to finalize query results", ex, "ExecuteQuery.Finalize");
                    _lastErrorMessage = ex.Message;
                    _statusText = $"Error finalizing results: {ex.Message}";
                    TuiDebugLog.Log($"Error in ExecuteQuery finalize callback: {ex}");
                }
                finally
                {
                    _statusSpinner.Stop();
                    _statusLabel.Text = _statusText;
                    _statusLabel.Visible = true;
                    _isExecuting = false;
                }
            });

            // Save to history (fire-and-forget)
            ErrorService.FireAndForget(
                SaveToHistoryAsync(sql, totalRows, stopwatch.ElapsedMilliseconds),
                "SaveHistory");

            // Cache execution plan (fire-and-forget)
            ErrorService.FireAndForget(
                CacheExecutionPlanAsync(sql),
                "CacheExecutionPlan");
        }
        catch (DataverseAuthenticationException authEx) when (authEx.RequiresReauthentication)
        {
            // Stop spinner before showing dialog
            _statusSpinner.Stop();
            _statusLabel.Visible = true;

            TuiDebugLog.Log($"Authentication error: {authEx.Message}");

            // Show re-authentication dialog
            var dialog = new ReAuthenticationDialog(authEx.UserMessage, Session);
            Application.Run(dialog);

            if (dialog.ShouldReauthenticate)
            {
                TuiDebugLog.Log("User chose to re-authenticate");
                try
                {
                    _statusLabel.Text = "Re-authenticating...";
                    await Session.InvalidateAndReauthenticateAsync(ScreenCancellation);

                    // Retry the query - ExecuteQueryAsync reads from _queryInput
                    TuiDebugLog.Log("Re-authentication successful, retrying query");
                    _statusLabel.Visible = false;
                    ErrorService.FireAndForget(ExecuteQueryAsync(), "RetryQuery");
                    return;
                }
                catch (Exception reAuthEx)
                {
                    ErrorService.ReportError("Re-authentication failed", reAuthEx, "ExecuteQuery.ReAuth");
                    _lastErrorMessage = reAuthEx.Message;
                    _statusText = $"Re-authentication failed: {reAuthEx.Message}";
                    _statusLabel.Text = _statusText;
                    _isExecuting = false;
                }
            }
            else
            {
                TuiDebugLog.Log("User cancelled re-authentication");
                ErrorService.ReportError("Session expired", authEx, "ExecuteQuery");
                _lastErrorMessage = authEx.Message;
                _statusText = $"Error: {authEx.Message}";
                _statusLabel.Text = _statusText;
                _isExecuting = false;
            }
        }
        catch (OperationCanceledException) when (_queryCts?.IsCancellationRequested == true && !ScreenCancellation.IsCancellationRequested)
        {
            // Query was cancelled by user (Escape), not by screen closing
            _statusSpinner.Stop();
            _statusLabel.Text = "Query cancelled.";
            _statusLabel.Visible = true;
            _isExecuting = false;
        }
        catch (Exception ex)
        {
            ErrorService.ReportError("Query execution failed", ex, "ExecuteQuery");
            _lastErrorMessage = ex.Message;
            _statusText = $"Error: {ex.Message}";

            // Stop spinner, show error in status label
            _statusSpinner.Stop();
            _statusLabel.Text = _statusText;
            _statusLabel.Visible = true;
            _isExecuting = false;
        }
        }
        finally
        {
            if (elapsedTimer != null)
                Application.MainLoop?.RemoveTimeout(elapsedTimer);
        }
    }

    private async Task SaveToHistoryAsync(string sql, int rowCount, long executionTimeMs)
    {
        if (EnvironmentUrl == null) return;

        try
        {
            var historyService = await Session.GetQueryHistoryServiceAsync(EnvironmentUrl, ScreenCancellation);
            await historyService.AddQueryAsync(EnvironmentUrl, sql, rowCount, executionTimeMs);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TUI] Failed to save query to history: {ex.Message}");
            TuiDebugLog.Log($"History save failed: {ex.Message}");
        }
    }

    private async Task CacheExecutionPlanAsync(string sql)
    {
        if (EnvironmentUrl == null) return;

        try
        {
            var service = await Session.GetSqlQueryServiceAsync(EnvironmentUrl, ScreenCancellation);
            var plan = await service.ExplainAsync(sql, ScreenCancellation);
            _lastExecutionPlan = plan;
        }
        catch (Exception ex)
        {
            TuiDebugLog.Log($"Execution plan cache failed: {ex.Message}");
        }
    }

    private async Task OnLoadMoreRequested()
    {
        if (_lastSql == null || EnvironmentUrl == null)
            return;

        try
        {
            var service = await Session.GetSqlQueryServiceAsync(EnvironmentUrl, ScreenCancellation);

            var request = new SqlQueryRequest
            {
                Sql = _lastSql,
                PageNumber = _lastPageNumber + 1,
                PagingCookie = _lastPagingCookie,
                EnablePrefetch = true
            };

            var result = await service.ExecuteAsync(request, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                try
                {
                    _resultsTable.AddPage(result.Result);
                    _lastPagingCookie = result.Result.PagingCookie;
                    _lastPageNumber = result.Result.PageNumber;
                }
                catch (Exception ex)
                {
                    ErrorService.ReportError("Failed to load additional results", ex, "LoadMore.AddPage");
                    TuiDebugLog.Log($"Error in LoadMore callback: {ex}");
                }
            });
        }
        catch (DataverseAuthenticationException authEx) when (authEx.RequiresReauthentication)
        {
            TuiDebugLog.Log($"Authentication error during load more: {authEx.Message}");

            // Show re-authentication dialog
            var dialog = new ReAuthenticationDialog(authEx.UserMessage, Session);
            Application.Run(dialog);

            if (dialog.ShouldReauthenticate)
            {
                try
                {
                    await Session.InvalidateAndReauthenticateAsync(ScreenCancellation);
                    // Don't auto-retry load more - user can click the button again
                }
                catch (Exception reAuthEx)
                {
                    ErrorService.ReportError("Re-authentication failed", reAuthEx, "LoadMoreResults.ReAuth");
                }
            }
            else
            {
                ErrorService.ReportError("Session expired", authEx, "LoadMoreResults");
            }
        }
        catch (InvalidOperationException ex)
        {
            ErrorService.ReportError("Failed to load more results", ex, "LoadMoreResults");
        }
        catch (HttpRequestException ex)
        {
            ErrorService.ReportError("Network error loading results", ex, "LoadMoreResults");
        }
    }

    private void ToggleTdsEndpoint()
    {
        _useTdsEndpoint = !_useTdsEndpoint;
        _statusLabel.Text = _useTdsEndpoint
            ? "Mode: TDS Read Replica (read-only, slight delay)"
            : "Mode: Dataverse (real-time)";
        NotifyMenuChanged();
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
        var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
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
        if (_lastExecutionPlan != null && sql == _lastSql)
        {
            ShowPlanDialog(_lastExecutionPlan, _lastExecutionTimeMs);
            return;
        }

        // Otherwise, fetch the plan
        ErrorService.FireAndForget(FetchAndShowPlanAsync(sql), "ShowExecutionPlan");
    }

    private async Task FetchAndShowPlanAsync(string sql)
    {
        try
        {
            var service = await Session.GetSqlQueryServiceAsync(EnvironmentUrl!, ScreenCancellation);
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

        var dialog = new Dialog("Execution Plan", 80, 25)
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
            ErrorMessage: _lastErrorMessage,
            EditorHeight: _editorHeight);
    }

    protected override void OnDispose()
    {
        _queryCts?.Cancel();
        _queryCts?.Dispose();
    }
}
