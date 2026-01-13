using System.Data;
using System.Net.Http;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.Query;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Wraps Terminal.Gui TableView for displaying query results with pagination and copy support.
/// </summary>
internal sealed class QueryResultsTableView : FrameView
{
    private readonly TableView _tableView;
    private readonly Label _statusLabel;

    private DataTable _dataTable;
    private DataTable? _unfilteredDataTable; // Original data before filtering
    private Dictionary<string, QueryColumnType> _columnTypes = new();
    private QueryResult? _lastResult;
    private string? _environmentUrl;
    private bool _isLoadingMore;
    private bool _guidColumnsHidden;
    private string? _currentFilter;
    private object? _statusRestoreToken; // Token to cancel pending status restore
    private const int StatusRestoreDelayMs = 2500;

    /// <summary>
    /// Raised when the user scrolls to the end and more records are available.
    /// Handler should fetch the next page and call AddPage().
    /// </summary>
    public event Func<Task>? LoadMoreRequested;

    /// <summary>
    /// Whether more records are available to load.
    /// </summary>
    public bool MoreRecordsAvailable { get; private set; }

    /// <summary>
    /// The paging cookie for fetching the next page.
    /// </summary>
    public string? PagingCookie { get; private set; }

    /// <summary>
    /// Current page number (1-based).
    /// </summary>
    public int CurrentPageNumber { get; private set; } = 1;

    /// <summary>
    /// Gets the number of rows per page (for display purposes).
    /// </summary>
    public int PageSize => _tableView.Bounds.Height - 2; // Subtract header rows

    /// <summary>
    /// Gets the number of visible rows in the current view.
    /// </summary>
    public int VisibleRowCount => Math.Min(PageSize, _dataTable.Rows.Count);

    public QueryResultsTableView() : base("Results")
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _dataTable = new DataTable();

        _tableView = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 1,
            FullRowSelect = false,
            MultiSelect = true
        };

        // Configure table style (Terminal.Gui 1.x API)
        _tableView.Style.ShowHorizontalHeaderOverline = true;
        _tableView.Style.ShowVerticalCellLines = true;
        _tableView.Style.AlwaysShowHeaders = true;
        _tableView.Style.ExpandLastColumn = true;

        _statusLabel = new Label("No data")
        {
            X = 0,
            Y = Pos.Bottom(_tableView),
            Width = Dim.Fill(),
            Height = 1,
            TextAlignment = TextAlignment.Centered
        };

        Add(_tableView, _statusLabel);
        SetupKeyboardShortcuts();
    }

    /// <summary>
    /// Sets the environment URL for building record URLs.
    /// </summary>
    public void SetEnvironmentUrl(string? url)
    {
        _environmentUrl = url;
    }

    /// <summary>
    /// Gets the underlying DataTable for export operations.
    /// </summary>
    public DataTable? GetDataTable() => _dataTable.Rows.Count > 0 ? _dataTable : null;

    /// <summary>
    /// Gets the column type metadata for export operations.
    /// </summary>
    public IReadOnlyDictionary<string, QueryColumnType>? GetColumnTypes() =>
        _columnTypes.Count > 0 ? _columnTypes : null;

    /// <summary>
    /// Loads query results into the table, replacing any existing data.
    /// </summary>
    public void LoadResults(QueryResult result)
    {
        _lastResult = result;
        _unfilteredDataTable = null; // Clear filter cache
        _currentFilter = null;

        var (table, columnTypes) = QueryResultConverter.ToDataTableWithTypes(result);
        _dataTable = table;
        _columnTypes = columnTypes;
        _tableView.Table = _dataTable;

        ApplyColumnSizing();

        MoreRecordsAvailable = result.MoreRecords;
        PagingCookie = result.PagingCookie;
        CurrentPageNumber = result.PageNumber;

        UpdateStatus();
    }

    /// <summary>
    /// Adds a page of results to the existing data.
    /// </summary>
    public void AddPage(QueryResult result)
    {
        if (_lastResult == null)
        {
            LoadResults(result);
            return;
        }

        // Append rows to existing DataTable
        foreach (var record in result.Records)
        {
            var row = _dataTable.NewRow();
            foreach (var column in result.Columns)
            {
                if (record.TryGetValue(column.LogicalName, out var value))
                {
                    row[column.LogicalName] = QueryResultConverter.FormatValue(value);
                }
            }
            _dataTable.Rows.Add(row);
        }

        MoreRecordsAvailable = result.MoreRecords;
        PagingCookie = result.PagingCookie;
        CurrentPageNumber = result.PageNumber;

        _tableView.SetNeedsDisplay();
        UpdateStatus();
    }

    /// <summary>
    /// Clears all results from the table.
    /// </summary>
    public void ClearData()
    {
        _dataTable = new DataTable();
        _unfilteredDataTable = null; // Clear filter cache
        _currentFilter = null;
        _tableView.Table = _dataTable;
        _lastResult = null;
        MoreRecordsAvailable = false;
        PagingCookie = null;
        CurrentPageNumber = 1;
        _statusLabel.TextAlignment = TextAlignment.Centered;
        _statusLabel.Text = "No data";
    }

    /// <summary>
    /// Applies a filter to the results. Pass null or empty string to clear filter.
    /// </summary>
    /// <param name="filterText">Text to search for in any string column.</param>
    public void ApplyFilter(string? filterText)
    {
        // Store original data on first filter
        if (_unfilteredDataTable == null && _dataTable.Rows.Count > 0)
        {
            _unfilteredDataTable = _dataTable.Copy();
        }

        var sourceTable = _unfilteredDataTable ?? _dataTable;
        if (sourceTable.Rows.Count == 0)
            return;

        _currentFilter = filterText;

        if (string.IsNullOrWhiteSpace(filterText))
        {
            // Clear filter - restore original data
            if (_unfilteredDataTable != null)
            {
                _dataTable = _unfilteredDataTable.Copy();
                _tableView.Table = _dataTable;
                ApplyColumnSizing();
            }
            UpdateStatus();
            return;
        }

        // Build filter expression: search all string columns
        var conditions = new List<string>();
        var escaped = EscapeFilterValue(filterText);

        foreach (DataColumn column in sourceTable.Columns)
        {
            // Only filter string columns
            if (column.DataType == typeof(string))
            {
                conditions.Add($"[{column.ColumnName}] LIKE '%{escaped}%'");
            }
        }

        if (conditions.Count > 0)
        {
            // Apply filter to source and create new filtered table
            sourceTable.DefaultView.RowFilter = string.Join(" OR ", conditions);
            _dataTable = sourceTable.DefaultView.ToTable();
            sourceTable.DefaultView.RowFilter = string.Empty; // Reset source filter

            _tableView.Table = _dataTable;
            ApplyColumnSizing();
            UpdateStatus();
        }
    }

    /// <summary>
    /// Escapes special characters in filter text for DataView.RowFilter LIKE expressions.
    /// </summary>
    private static string EscapeFilterValue(string value)
    {
        // Escape special characters: ', *, %, [, ]
        return value
            .Replace("'", "''")
            .Replace("[", "[[]")
            .Replace("]", "[]]")
            .Replace("*", "[*]")
            .Replace("%", "[%]");
    }

    private void SetupKeyboardShortcuts()
    {
        _tableView.KeyPress += (e) =>
        {
            // Only handle shortcuts when table has focus
            // Prevents Ctrl+C/X from triggering when query input has focus
            if (!_tableView.HasFocus)
                return;

            switch (e.KeyEvent.Key)
            {
                case Key.CtrlMask | Key.C:
                    CopySelectedCell();
                    e.Handled = true;
                    break;

                case Key.CtrlMask | Key.U:
                    CopyRecordUrl();
                    e.Handled = true;
                    break;

                case Key.CtrlMask | Key.O:
                    OpenInBrowser();
                    e.Handled = true;
                    break;

                case Key.CtrlMask | Key.H:
                    ToggleGuidColumns();
                    e.Handled = true;
                    break;

                case Key.End:
                    // Check if we need to load more when going to end
                    if (MoreRecordsAvailable)
                    {
                        _ = LoadMoreAsync();
                    }
                    break;

                case Key.PageDown:
                case Key.CursorDown:
                    // Check if we're near the end and need more data
                    if (MoreRecordsAvailable && IsNearEnd())
                    {
                        _ = LoadMoreAsync();
                    }
                    break;
            }
        };
    }

    private bool IsNearEnd()
    {
        if (_tableView.Table == null) return false;
        var visibleRows = _tableView.Bounds.Height - 2; // Subtract header rows
        var lastVisibleRow = _tableView.RowOffset + visibleRows;
        return lastVisibleRow >= _tableView.Table.Rows.Count - 5;
    }

    private async Task LoadMoreAsync()
    {
        // Guard against concurrent load requests
        if (_isLoadingMore || LoadMoreRequested == null)
            return;

        _isLoadingMore = true;
        _statusLabel.Text = $"Loading page {CurrentPageNumber + 1}...";
        Application.Refresh();

        try
        {
            await LoadMoreRequested.Invoke();
        }
        catch (InvalidOperationException ex)
        {
            ShowTemporaryStatus($"Error loading: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            ShowTemporaryStatus($"Network error: {ex.Message}");
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    private void CopySelectedCell()
    {
        if (_tableView.Table == null || _tableView.SelectedRow < 0)
        {
            ShowTemporaryStatus("No cell selected");
            return;
        }

        var row = _tableView.SelectedRow;
        var col = _tableView.SelectedColumn;

        if (row >= 0 && row < _tableView.Table.Rows.Count &&
            col >= 0 && col < _tableView.Table.Columns.Count)
        {
            var value = _tableView.Table.Rows[row][col]?.ToString() ?? string.Empty;

            if (ClipboardHelper.CopyToClipboard(value))
            {
                var displayValue = value.Length > 40 ? value[..37] + "..." : value;
                ShowTemporaryStatus($"Copied: {displayValue}");
            }
            else
            {
                ShowTemporaryStatus($"Copy failed. Value: {value}");
            }
        }
    }

    private void CopyRecordUrl()
    {
        var url = GetCurrentRecordUrl();
        if (url != null)
        {
            if (ClipboardHelper.CopyToClipboard(url))
            {
                ShowTemporaryStatus("URL copied to clipboard");
            }
            else
            {
                ShowTemporaryStatus(url);
            }
        }
        else
        {
            ShowTemporaryStatus("Cannot build URL (no environment or primary key)");
        }
    }

    private void OpenInBrowser()
    {
        var url = GetCurrentRecordUrl();
        if (url != null)
        {
            if (BrowserHelper.OpenUrl(url))
            {
                ShowTemporaryStatus("Opened in browser");
            }
            else
            {
                ShowTemporaryStatus("Failed to open browser");
            }
        }
        else
        {
            ShowTemporaryStatus("Cannot build URL (no environment or primary key)");
        }
    }

    private string? GetCurrentRecordUrl()
    {
        if (_environmentUrl == null || _lastResult == null || _tableView.SelectedRow < 0)
            return null;

        // Look for primary key column (entity + "id")
        var primaryKeyColumn = _lastResult.EntityLogicalName + "id";
        var idColumnIndex = -1;

        for (int i = 0; i < _dataTable.Columns.Count; i++)
        {
            if (string.Equals(_dataTable.Columns[i].ColumnName, primaryKeyColumn, StringComparison.OrdinalIgnoreCase))
            {
                idColumnIndex = i;
                break;
            }
        }

        if (idColumnIndex < 0) return null;

        var id = _dataTable.Rows[_tableView.SelectedRow][idColumnIndex]?.ToString();
        if (string.IsNullOrEmpty(id)) return null;

        // Build Dynamics 365 record URL
        var baseUrl = _environmentUrl.TrimEnd('/');
        return $"{baseUrl}/main.aspx?etn={_lastResult.EntityLogicalName}&id={id}&pagetype=entityrecord";
    }

    private void UpdateStatus()
    {
        // Cancel any pending restore since we're updating status now
        _statusRestoreToken = null;

        var sourceCount = _unfilteredDataTable?.Rows.Count ?? _dataTable.Rows.Count;
        var displayCount = _dataTable.Rows.Count;
        var moreText = MoreRecordsAvailable ? " (more available)" : "";
        var guidText = _guidColumnsHidden ? " | GUIDs hidden (Ctrl+H)" : "";
        var filterText = !string.IsNullOrEmpty(_currentFilter)
            ? $" (filtered: {displayCount} of {sourceCount})"
            : "";

        _statusLabel.TextAlignment = TextAlignment.Left;
        _statusLabel.Text = $"{displayCount} rows{filterText}{moreText}{guidText} | Ctrl+C: copy | Ctrl+U: copy URL | Ctrl+O: open";
    }

    /// <summary>
    /// Shows a temporary status message, then auto-restores the default status after a delay.
    /// </summary>
    private void ShowTemporaryStatus(string message)
    {
        // Cancel any pending restore
        var token = new object();
        _statusRestoreToken = token;

        _statusLabel.TextAlignment = TextAlignment.Left;
        _statusLabel.Text = message;

        // Schedule restore after delay
        _ = Task.Delay(StatusRestoreDelayMs).ContinueWith(_ =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                // Only restore if this token is still active (not superseded by another message)
                if (_statusRestoreToken == token)
                {
                    UpdateStatus();
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Applies content-aware column sizing with padding.
    /// Columns are sized based on actual data with breathing room.
    /// </summary>
    private void ApplyColumnSizing()
    {
        _tableView.Style.ColumnStyles.Clear();

        const int padding = 2; // Extra space on each side
        const int minWidth = 6;
        const int maxWidth = 60;

        foreach (DataColumn column in _dataTable.Columns)
        {
            _columnTypes.TryGetValue(column.ColumnName, out var dataType);

            // Calculate optimal width based on content
            var headerWidth = column.ColumnName.Length;
            var maxContentWidth = 0;

            // Sample first 100 rows for performance
            var rowsToSample = Math.Min(_dataTable.Rows.Count, 100);
            for (int i = 0; i < rowsToSample; i++)
            {
                var value = _dataTable.Rows[i][column]?.ToString() ?? string.Empty;
                maxContentWidth = Math.Max(maxContentWidth, value.Length);
            }

            // Use the larger of header or content, plus padding
            var optimalWidth = Math.Max(headerWidth, maxContentWidth) + padding;

            // Apply type-specific constraints
            var (typeMin, typeMax) = GetTypeWidthConstraints(dataType);
            var finalWidth = Math.Clamp(optimalWidth, Math.Max(minWidth, typeMin), Math.Min(maxWidth, typeMax));

            var style = new TableView.ColumnStyle
            {
                MinWidth = finalWidth,
                MaxWidth = finalWidth + 10 // Allow some expansion
            };

            // Apply hidden state for GUID columns if toggled
            if (_guidColumnsHidden && dataType == QueryColumnType.Guid)
            {
                style.Visible = false;
            }

            _tableView.Style.ColumnStyles[column] = style;
        }

        _tableView.SetNeedsDisplay();
    }

    /// <summary>
    /// Gets min/max width constraints for a data type.
    /// </summary>
    private static (int min, int max) GetTypeWidthConstraints(QueryColumnType dataType)
    {
        return dataType switch
        {
            QueryColumnType.Guid => (36, 38),
            QueryColumnType.DateTime => (19, 22),
            QueryColumnType.Boolean => (5, 8),
            QueryColumnType.Integer or QueryColumnType.BigInt => (6, 20),
            QueryColumnType.Decimal or QueryColumnType.Double or QueryColumnType.Money => (8, 20),
            _ => (6, 100)
        };
    }

    /// <summary>
    /// Toggles visibility of GUID columns.
    /// </summary>
    private void ToggleGuidColumns()
    {
        _guidColumnsHidden = !_guidColumnsHidden;
        ApplyColumnSizing();
        UpdateStatus();
    }
}
