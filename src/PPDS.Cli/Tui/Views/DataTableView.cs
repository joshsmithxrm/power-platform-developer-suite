using System.Data;
using System.Text;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.Export;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Generic reusable table view for displaying DataTable content with multi-cell selection,
/// filtering, pagination, and context menus.
/// </summary>
/// <remarks>
/// <para>
/// This is a reusable component for displaying tabular data in Terminal.Gui.
/// Specialized views (QueryResultsTableView, PluginTracesTableView, etc.) can extend
/// or compose this component with domain-specific features.
/// </para>
/// <para>
/// Features:
/// - Multi-cell selection (#205)
/// - Filter/search (#204)
/// - Mouse support (#207)
/// - Context menu (Copy Cell, Copy Row, Copy Selection)
/// - Pagination with "load more" events
/// - Keyboard navigation
/// </para>
/// <para>
/// See issue #234 for design context.
/// </para>
/// </remarks>
internal class DataTableView : FrameView
{
    private readonly TableView _tableView;
    private readonly Label _statusLabel;
    private readonly TextField? _filterField;
    private readonly FrameView? _filterFrame;

    private DataTable _sourceTable;
    private DataView _filteredView;
    private string _currentFilter = string.Empty;
    private bool _isLoadingMore;

    /// <summary>
    /// Raised when the user scrolls to the end and more records are available.
    /// Handler should fetch the next page and call AddRows().
    /// </summary>
    public event Func<Task>? LoadMoreRequested;

    /// <summary>
    /// Raised when a row is activated (Enter key or double-click).
    /// </summary>
    public event Action<int>? RowActivated;

    /// <summary>
    /// Raised when selection changes.
    /// </summary>
    public event Action<int, int>? SelectionChanged;

    /// <summary>
    /// Whether more records are available to load.
    /// </summary>
    public bool MoreRecordsAvailable { get; set; }

    /// <summary>
    /// Gets the underlying TableView for advanced customization.
    /// </summary>
    public TableView TableView => _tableView;

    /// <summary>
    /// Gets or sets the currently selected row index.
    /// </summary>
    public int SelectedRow
    {
        get => _tableView.SelectedRow;
        set => _tableView.SelectedRow = value;
    }

    /// <summary>
    /// Gets or sets the currently selected column index.
    /// </summary>
    public int SelectedColumn
    {
        get => _tableView.SelectedColumn;
        set => _tableView.SelectedColumn = value;
    }

    /// <summary>
    /// Gets the number of rows in the current view.
    /// </summary>
    public int RowCount => _filteredView?.Count ?? 0;

    /// <summary>
    /// Gets the number of columns.
    /// </summary>
    public int ColumnCount => _sourceTable?.Columns.Count ?? 0;

    /// <summary>
    /// Gets whether the filter is currently visible.
    /// </summary>
    public bool IsFilterVisible => _filterFrame?.Visible ?? false;

    /// <summary>
    /// Creates a new DataTableView.
    /// </summary>
    /// <param name="title">Frame title.</param>
    /// <param name="includeFilterBar">Whether to include a filter bar.</param>
    public DataTableView(string title = "Data", bool includeFilterBar = true) : base(title)
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _sourceTable = new DataTable();
        _filteredView = _sourceTable.DefaultView;

        // Optional filter bar
        if (includeFilterBar)
        {
            _filterFrame = new FrameView("Filter (/)")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 3,
                Visible = false
            };

            _filterField = new TextField
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 1,
                ColorScheme = Infrastructure.TuiColorPalette.TextInput
            };
            _filterField.TextChanged += OnFilterTextChanged;
            _filterFrame.Add(_filterField);
            Add(_filterFrame);
        }

        // Table view
        var tableY = includeFilterBar ? Pos.Bottom(_filterFrame!) : 0;
        _tableView = new TableView
        {
            X = 0,
            Y = tableY,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 1,
            FullRowSelect = false,
            MultiSelect = true
        };

        // Configure table style
        _tableView.Style.ShowHorizontalHeaderOverline = true;
        _tableView.Style.ShowVerticalCellLines = true;
        _tableView.Style.AlwaysShowHeaders = true;
        _tableView.Style.ExpandLastColumn = true;

        // Status bar
        _statusLabel = new Label("No data")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1
        };

        Add(_tableView, _statusLabel);
        SetupKeyboardShortcuts();
        SetupMouseHandlers();
    }

    /// <summary>
    /// Loads a DataTable into the view, replacing any existing data.
    /// </summary>
    public virtual void LoadData(DataTable table)
    {
        _sourceTable = table ?? new DataTable();
        _filteredView = _sourceTable.DefaultView;
        _currentFilter = string.Empty;
        _tableView.Table = _sourceTable;
        UpdateStatus();
    }

    /// <summary>
    /// Adds rows from another DataTable (for pagination).
    /// </summary>
    public virtual void AddRows(DataTable additionalRows)
    {
        if (additionalRows == null) return;

        foreach (DataRow row in additionalRows.Rows)
        {
            var newRow = _sourceTable.NewRow();
            for (int i = 0; i < _sourceTable.Columns.Count && i < additionalRows.Columns.Count; i++)
            {
                newRow[i] = row[i];
            }
            _sourceTable.Rows.Add(newRow);
        }

        _tableView.SetNeedsDisplay();
        UpdateStatus();
    }

    /// <summary>
    /// Clears all data from the view.
    /// </summary>
    public virtual void ClearData()
    {
        _sourceTable = new DataTable();
        _filteredView = _sourceTable.DefaultView;
        _currentFilter = string.Empty;
        _tableView.Table = _sourceTable;
        MoreRecordsAvailable = false;
        _statusLabel.Text = "No data";
    }

    /// <summary>
    /// Shows the filter bar.
    /// </summary>
    public void ShowFilter()
    {
        if (_filterFrame == null) return;

        _filterFrame.Visible = true;
        _filterField!.Text = string.Empty;
        _filterField.SetFocus();
        _statusLabel.Text = "Type to filter. Press Esc to close.";
        LayoutSubviews();
    }

    /// <summary>
    /// Hides the filter bar and clears the filter.
    /// </summary>
    public void HideFilter()
    {
        if (_filterFrame == null) return;

        _filterFrame.Visible = false;
        ClearFilter();
        LayoutSubviews();
    }

    /// <summary>
    /// Applies a filter to the data.
    /// </summary>
    public void ApplyFilter(string filterText)
    {
        _currentFilter = filterText?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(_currentFilter))
        {
            ClearFilter();
            return;
        }

        try
        {
            // Build filter expression across all string columns
            var conditions = new List<string>();
            foreach (DataColumn col in _sourceTable.Columns)
            {
                if (col.DataType == typeof(string))
                {
                    var escaped = EscapeFilterValue(_currentFilter);
                    conditions.Add($"[{col.ColumnName}] LIKE '%{escaped}%'");
                }
            }

            if (conditions.Count > 0)
            {
                _filteredView.RowFilter = string.Join(" OR ", conditions);
            }

            UpdateStatus();
        }
        catch (Exception)
        {
            // Invalid filter expression - ignore
            _filteredView.RowFilter = string.Empty;
        }
    }

    /// <summary>
    /// Clears the current filter.
    /// </summary>
    public void ClearFilter()
    {
        _currentFilter = string.Empty;
        _filteredView.RowFilter = string.Empty;
        UpdateStatus();
    }

    /// <summary>
    /// Gets the value at the specified row and column.
    /// </summary>
    public object? GetValue(int row, int column)
    {
        if (row < 0 || row >= _sourceTable.Rows.Count ||
            column < 0 || column >= _sourceTable.Columns.Count)
            return null;

        return _sourceTable.Rows[row][column];
    }

    /// <summary>
    /// Gets the value at the selected cell.
    /// </summary>
    public object? GetSelectedValue()
    {
        return GetValue(_tableView.SelectedRow, _tableView.SelectedColumn);
    }

    /// <summary>
    /// Copies the selected cell value to clipboard.
    /// </summary>
    public bool CopySelectedCell()
    {
        var value = GetSelectedValue()?.ToString() ?? string.Empty;
        var success = ClipboardHelper.CopyToClipboard(value);

        if (success)
        {
            var display = value.Length > 40 ? value[..37] + "..." : value;
            SetStatus($"Copied: {display}");
        }
        else
        {
            SetStatus($"Copy failed. Value: {value}");
        }

        return success;
    }

    /// <summary>
    /// Copies the selected row(s) to clipboard.
    /// </summary>
    public bool CopySelectedRows()
    {
        var sb = new StringBuilder();

        // Get selected rows from multi-selection
        var selectedRows = GetSelectedRowIndices();
        if (selectedRows.Count == 0 && _tableView.SelectedRow >= 0)
        {
            selectedRows = new List<int> { _tableView.SelectedRow };
        }

        if (selectedRows.Count == 0)
        {
            SetStatus("No rows selected");
            return false;
        }

        // Header row
        var headers = new List<string>();
        foreach (DataColumn col in _sourceTable.Columns)
        {
            headers.Add(col.ColumnName);
        }
        sb.AppendLine(string.Join("\t", headers));

        // Data rows
        foreach (var rowIndex in selectedRows.OrderBy(i => i))
        {
            if (rowIndex < 0 || rowIndex >= _sourceTable.Rows.Count) continue;

            var row = _sourceTable.Rows[rowIndex];
            var values = new List<string>();
            foreach (DataColumn col in _sourceTable.Columns)
            {
                var value = row[col]?.ToString() ?? string.Empty;
                value = value.Replace("\t", " ").Replace("\n", " ").Replace("\r", "");
                values.Add(value);
            }
            sb.AppendLine(string.Join("\t", values));
        }

        var text = sb.ToString().TrimEnd();
        var success = ClipboardHelper.CopyToClipboard(text);

        if (success)
        {
            SetStatus($"Copied {selectedRows.Count} row(s)");
        }
        else
        {
            SetStatus("Copy failed");
        }

        return success;
    }

    /// <summary>
    /// Sets the status label text.
    /// </summary>
    public void SetStatus(string message)
    {
        _statusLabel.Text = message;
    }

    /// <summary>
    /// Gets the row indices that are currently selected.
    /// </summary>
    protected virtual List<int> GetSelectedRowIndices()
    {
        var indices = new List<int>();

        // Terminal.Gui MultiSelectedRegions for TableView
        if (_tableView.MultiSelectedRegions != null)
        {
            foreach (var region in _tableView.MultiSelectedRegions)
            {
                for (int r = region.Rect.Y; r < region.Rect.Y + region.Rect.Height; r++)
                {
                    if (!indices.Contains(r))
                        indices.Add(r);
                }
            }
        }

        return indices;
    }

    /// <summary>
    /// Updates the status label with current row count and hints.
    /// </summary>
    protected virtual void UpdateStatus()
    {
        var rowCount = _sourceTable.Rows.Count;
        var filteredCount = _filteredView.Count;
        var moreText = MoreRecordsAvailable ? " (more)" : "";

        if (!string.IsNullOrEmpty(_currentFilter))
        {
            _statusLabel.Text = $"{filteredCount} of {rowCount} rows{moreText} | Ctrl+C: copy | /: filter";
        }
        else
        {
            _statusLabel.Text = $"{rowCount} rows{moreText} | Ctrl+C: copy | /: filter";
        }
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

                case Key.CtrlMask | Key.ShiftMask | Key.C:
                    CopySelectedRows();
                    e.Handled = true;
                    break;

                case Key.Enter:
                    OnRowActivated(_tableView.SelectedRow);
                    e.Handled = true;
                    break;

                case Key.End:
                    if (MoreRecordsAvailable)
                    {
                        _ = LoadMoreAsync();
                    }
                    break;

                case Key.PageDown:
                case Key.CursorDown:
                    if (MoreRecordsAvailable && IsNearEnd())
                    {
                        _ = LoadMoreAsync();
                    }
                    break;
            }
        };

        // Global key handling for filter toggle
        KeyPress += (e) =>
        {
            switch (e.KeyEvent.Key)
            {
                case Key k when k == (Key)'/':
                    if (!_tableView.HasFocus || (_filterFrame != null && !_filterFrame.Visible))
                    {
                        ShowFilter();
                        e.Handled = true;
                    }
                    break;

                case Key.Esc:
                    if (_filterFrame?.Visible == true)
                    {
                        HideFilter();
                        _tableView.SetFocus();
                        e.Handled = true;
                    }
                    break;
            }
        };

        // Filter field key handling
        if (_filterField != null)
        {
            _filterField.KeyPress += (e) =>
            {
                if (e.KeyEvent.Key == Key.Enter)
                {
                    // Apply filter and move focus to table
                    _tableView.SetFocus();
                    e.Handled = true;
                }
            };
        }

        // Selection change notification
        _tableView.SelectedCellChanged += (e) =>
        {
            SelectionChanged?.Invoke(e.NewRow, e.NewCol);
        };
    }

    private void SetupMouseHandlers()
    {
        _tableView.MouseClick += (e) =>
        {
            if (e.MouseEvent.Flags == MouseFlags.Button3Clicked)
            {
                // Right-click context menu
                ShowContextMenu(e.MouseEvent.X, e.MouseEvent.Y);
                e.Handled = true;
            }
            else if (e.MouseEvent.Flags == MouseFlags.Button1DoubleClicked)
            {
                // Double-click to activate
                OnRowActivated(_tableView.SelectedRow);
                e.Handled = true;
            }
        };
    }

    private void ShowContextMenu(int x, int y)
    {
        var menu = new ContextMenu(x, y,
            new MenuBarItem(null, new MenuItem[]
            {
                new MenuItem("Copy Cell", "Ctrl+C", () => CopySelectedCell()),
                new MenuItem("Copy Row(s)", "Ctrl+Shift+C", () => CopySelectedRows()),
                null!, // Separator
                new MenuItem("Clear Filter", "", ClearFilter, canExecute: () => !string.IsNullOrEmpty(_currentFilter))
            }));

        menu.Show();
    }

    private void OnFilterTextChanged(NStack.ustring obj)
    {
        var filterText = _filterField?.Text?.ToString() ?? string.Empty;
        ApplyFilter(filterText);
    }

    private bool IsNearEnd()
    {
        if (_tableView.Table == null) return false;
        var visibleRows = _tableView.Bounds.Height - 2;
        var lastVisibleRow = _tableView.RowOffset + visibleRows;
        return lastVisibleRow >= _tableView.Table.Rows.Count - 5;
    }

    private async Task LoadMoreAsync()
    {
        if (_isLoadingMore || LoadMoreRequested == null)
            return;

        _isLoadingMore = true;
        SetStatus("Loading more...");
        Application.Refresh();

        try
        {
            await LoadMoreRequested.Invoke();
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    private void OnRowActivated(int rowIndex)
    {
        RowActivated?.Invoke(rowIndex);
    }

    private static string EscapeFilterValue(string value)
    {
        // Escape special characters for DataView.RowFilter
        return value
            .Replace("'", "''")
            .Replace("[", "[[]")
            .Replace("%", "[%]")
            .Replace("*", "[*]");
    }
}
