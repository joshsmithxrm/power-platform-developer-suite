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
    private Dictionary<string, QueryColumnType> _columnTypes = new();
    private QueryResult? _lastResult;
    private string? _environmentUrl;
    private bool _isLoadingMore;
    private bool _guidColumnsHidden;

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
            Height = 1
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
    /// Loads query results into the table, replacing any existing data.
    /// </summary>
    public void LoadResults(QueryResult result)
    {
        _lastResult = result;
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
        _tableView.Table = _dataTable;
        _lastResult = null;
        MoreRecordsAvailable = false;
        PagingCookie = null;
        CurrentPageNumber = 1;
        _statusLabel.Text = "No data";
    }

    /// <summary>
    /// Applies a filter to the results. Pass null or empty string to clear filter.
    /// </summary>
    /// <param name="filterText">Text to search for in any string column.</param>
    public void ApplyFilter(string? filterText)
    {
        if (_dataTable.Rows.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(filterText))
        {
            _dataTable.DefaultView.RowFilter = string.Empty;
            UpdateStatus();
            return;
        }

        // Build filter expression: search all string columns
        var conditions = new List<string>();
        var escaped = EscapeFilterValue(filterText);

        foreach (DataColumn column in _dataTable.Columns)
        {
            // Only filter string columns
            if (column.DataType == typeof(string))
            {
                conditions.Add($"[{column.ColumnName}] LIKE '%{escaped}%'");
            }
        }

        if (conditions.Count > 0)
        {
            _dataTable.DefaultView.RowFilter = string.Join(" OR ", conditions);
            var filteredCount = _dataTable.DefaultView.Count;
            var totalCount = _dataTable.Rows.Count;
            _statusLabel.Text = $"Showing {filteredCount} of {totalCount} rows (filtered by: {filterText})";
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
            _statusLabel.Text = $"Error loading: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            _statusLabel.Text = $"Network error: {ex.Message}";
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
            _statusLabel.Text = "No cell selected";
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
                _statusLabel.Text = $"Copied: {displayValue}";
            }
            else
            {
                _statusLabel.Text = $"Copy failed. Value: {value}";
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
                _statusLabel.Text = "URL copied to clipboard";
            }
            else
            {
                _statusLabel.Text = url;
            }
        }
        else
        {
            _statusLabel.Text = "Cannot build URL (no environment or primary key)";
        }
    }

    private void OpenInBrowser()
    {
        var url = GetCurrentRecordUrl();
        if (url != null)
        {
            if (BrowserHelper.OpenUrl(url))
            {
                _statusLabel.Text = "Opened in browser";
            }
            else
            {
                _statusLabel.Text = "Failed to open browser";
            }
        }
        else
        {
            _statusLabel.Text = "Cannot build URL (no environment or primary key)";
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
        var rowCount = _dataTable.Rows.Count;
        var moreText = MoreRecordsAvailable ? " (more available)" : "";
        var guidText = _guidColumnsHidden ? " | GUIDs hidden (Ctrl+H)" : "";
        _statusLabel.Text = $"{rowCount} rows{moreText}{guidText} | Ctrl+C: copy | Ctrl+U: copy URL | Ctrl+O: open";
    }

    /// <summary>
    /// Applies type-aware column sizing based on the column data types.
    /// </summary>
    private void ApplyColumnSizing()
    {
        _tableView.Style.ColumnStyles.Clear();

        foreach (DataColumn column in _dataTable.Columns)
        {
            if (!_columnTypes.TryGetValue(column.ColumnName, out var dataType))
            {
                continue;
            }

            var style = GetColumnStyle(dataType);
            if (style != null)
            {
                // Apply hidden state for GUID columns if toggled
                if (_guidColumnsHidden && dataType == QueryColumnType.Guid)
                {
                    style.Visible = false;
                }

                _tableView.Style.ColumnStyles[column] = style;
            }
        }

        _tableView.SetNeedsDisplay();
    }

    /// <summary>
    /// Gets the appropriate column style for a given data type.
    /// </summary>
    private static TableView.ColumnStyle? GetColumnStyle(QueryColumnType dataType)
    {
        return dataType switch
        {
            // GUID columns: fixed width of 38 (36 chars + 2 for padding)
            QueryColumnType.Guid => new TableView.ColumnStyle { MinWidth = 38, MaxWidth = 38 },

            // DateTime columns: fixed width of 20 (yyyy-MM-dd HH:mm:ss + padding)
            QueryColumnType.DateTime => new TableView.ColumnStyle { MinWidth = 20, MaxWidth = 20 },

            // Boolean columns: fixed width of 5 (Yes/No)
            QueryColumnType.Boolean => new TableView.ColumnStyle { MinWidth = 5, MaxWidth = 5 },

            // Integer columns: constrained width
            QueryColumnType.Integer or QueryColumnType.BigInt =>
                new TableView.ColumnStyle { MinWidth = 8, MaxWidth = 20 },

            // Decimal/currency columns: constrained width
            QueryColumnType.Decimal or QueryColumnType.Double or QueryColumnType.Money =>
                new TableView.ColumnStyle { MinWidth = 10, MaxWidth = 20 },

            // Lookup columns: moderate flexibility
            QueryColumnType.Lookup => new TableView.ColumnStyle { MinWidth = 15, MaxWidth = 50 },

            // OptionSet columns: constrained width
            QueryColumnType.OptionSet or QueryColumnType.MultiSelectOptionSet =>
                new TableView.ColumnStyle { MinWidth = 10, MaxWidth = 30 },

            // String/Memo columns: flexible, minimum width only
            QueryColumnType.String or QueryColumnType.Memo =>
                new TableView.ColumnStyle { MinWidth = 10 },

            // Unknown or other types: no specific styling
            _ => null
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

        var message = _guidColumnsHidden ? "GUID columns hidden" : "GUID columns visible";
        _statusLabel.Text = message;
    }
}
