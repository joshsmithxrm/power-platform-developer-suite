using System.Net.Http;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Interactive;
using PPDS.Cli.Services.Query;
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

    private readonly TextView _queryInput;
    private readonly QueryResultsTableView _resultsTable;
    private readonly Label _statusLabel;
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

        Title = "SQL Query";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        // Query input area
        var queryFrame = new FrameView("Query (Ctrl+Enter to execute)")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 6
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
            Visible = false
        };

        _filterField = new TextField
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
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

        // Status bar
        _statusLabel = new Label("Ready. Press Ctrl+Enter to execute query.")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.White, Color.Blue)
            }
        };

        Add(queryFrame, _filterFrame, _resultsTable, _statusLabel);

        // Load profile and environment info (fire-and-forget with error handling)
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling via ContinueWith
        _ = LoadProfileInfoAsync().ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    _statusLabel.Text = $"Error loading profile: {t.Exception.InnerException?.Message ?? t.Exception.Message}";
                });
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013

        // Set up keyboard shortcuts
        SetupKeyboardShortcuts();
    }

    private async Task LoadProfileInfoAsync()
    {
        using var store = new ProfileStore();
        var collection = await store.LoadAsync(CancellationToken.None);
        var profile = collection.ActiveProfile;

        // Update UI on main thread
        Application.MainLoop?.Invoke(() =>
        {
            if (profile?.Environment != null)
            {
                _environmentUrl = profile.Environment.Url;
                _resultsTable.SetEnvironmentUrl(_environmentUrl);
                Title = $"SQL Query - {profile.Environment.DisplayName}";
            }
            else
            {
                _statusLabel.Text = "No environment selected. Select a profile with an environment first.";
            }
        });
    }

    private void SetupKeyboardShortcuts()
    {
        _queryInput.KeyPress += (e) =>
        {
            // Ctrl+Enter to execute
            if (e.KeyEvent.Key == (Key.CtrlMask | Key.Enter))
            {
                _ = ExecuteQueryAsync();
                e.Handled = true;
            }
        };

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

                case Key.CtrlMask | Key.E:
                    _ = ExportResultsAsync();
                    e.Handled = true;
                    break;
            }
        };
    }

    private async Task ExecuteQueryAsync()
    {
        var sql = _queryInput.Text.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sql))
        {
            _statusLabel.Text = "Error: Query cannot be empty.";
            return;
        }

        if (_environmentUrl == null)
        {
            _statusLabel.Text = "Error: No environment selected.";
            return;
        }

        _statusLabel.Text = "Executing query...";
        Application.Refresh();

        try
        {
            var service = await _session.GetSqlQueryServiceAsync(_environmentUrl, CancellationToken.None);

            var request = new SqlQueryRequest
            {
                Sql = sql,
                PageNumber = 1,
                PagingCookie = null
            };

            var result = await service.ExecuteAsync(request, CancellationToken.None);

            // Update UI on main thread
            Application.MainLoop?.Invoke(() =>
            {
                _resultsTable.LoadResults(result.Result);
                _lastSql = sql;
                _lastPagingCookie = result.Result.PagingCookie;
                _lastPageNumber = result.Result.PageNumber;

                var moreText = result.Result.MoreRecords ? " (more available)" : "";
                _statusLabel.Text = $"Returned {result.Result.Count} rows in {result.Result.ExecutionTimeMs}ms{moreText}";
            });
        }
        catch (InvalidOperationException ex)
        {
            Application.MainLoop?.Invoke(() => _statusLabel.Text = $"Error: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            Application.MainLoop?.Invoke(() => _statusLabel.Text = $"Network error: {ex.Message}");
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

                _statusLabel.Text = $"Loaded page {_lastPageNumber}";
            });
        }
        catch (InvalidOperationException ex)
        {
            Application.MainLoop?.Invoke(() => _statusLabel.Text = $"Error loading more: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            Application.MainLoop?.Invoke(() => _statusLabel.Text = $"Network error: {ex.Message}");
        }
    }

    private void ShowFilter()
    {
        _filterFrame.Visible = true;
        _filterField.Text = string.Empty;
        _filterField.SetFocus();
        _statusLabel.Text = "Type to filter results. Press Esc to close filter.";
    }

    private void HideFilter()
    {
        _filterFrame.Visible = false;
        _filterField.Text = string.Empty;
        _statusLabel.Text = "Filter cleared.";
    }

    private void OnFilterChanged(NStack.ustring obj)
    {
        // Filter is handled by the DataTable's DefaultView in QueryResultsTableView
        // For now, filtering is basic - could enhance to filter the underlying DataTable
        var filterText = _filterField.Text?.ToString() ?? string.Empty;
        _statusLabel.Text = string.IsNullOrEmpty(filterText)
            ? "Filter cleared."
            : $"Filtering by: {filterText}";
    }

    private Task ExportResultsAsync()
    {
        MessageBox.Query("Export", "Export functionality will be implemented in a future update.\n\nPlanned features:\n- CSV export\n- TSV export\n- Clipboard copy", "OK");
        return Task.CompletedTask;
    }
}
