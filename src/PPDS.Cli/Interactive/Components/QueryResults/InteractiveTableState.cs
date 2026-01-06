namespace PPDS.Cli.Interactive.Components.QueryResults;

/// <summary>
/// Manages viewport state for the interactive table.
/// Tracks selected row, visible rows, and column scroll position.
/// </summary>
internal sealed class InteractiveTableState
{
    /// <summary>
    /// The underlying navigation state with all record data.
    /// </summary>
    public RecordNavigationState NavigationState { get; }

    /// <summary>
    /// Index of the first visible row in the viewport.
    /// </summary>
    public int FirstVisibleRow { get; private set; }

    /// <summary>
    /// Index of the first visible column (for horizontal scrolling).
    /// Columns 0 through FixedColumnCount-1 are always visible.
    /// </summary>
    public int FirstScrollableColumn { get; private set; }

    /// <summary>
    /// The currently selected row index (highlighted row).
    /// </summary>
    public int SelectedRowIndex { get; private set; }

    /// <summary>
    /// The currently selected column index (for cell selection).
    /// </summary>
    public int SelectedColumnIndex { get; private set; }

    /// <summary>
    /// Number of rows that can be displayed in the viewport.
    /// </summary>
    public int VisibleRowCount { get; private set; }

    /// <summary>
    /// Number of columns that are always visible on the left (don't scroll horizontally).
    /// Default is 1 (typically the primary name/ID column).
    /// </summary>
    public int FixedColumnCount { get; set; } = 1;

    /// <summary>
    /// Calculated column layouts for the current viewport.
    /// </summary>
    public IReadOnlyList<ColumnLayout> ColumnLayouts { get; private set; } = Array.Empty<ColumnLayout>();

    /// <summary>
    /// Status message to display (e.g., "URL copied").
    /// </summary>
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Creates state from a navigation state.
    /// </summary>
    public InteractiveTableState(RecordNavigationState navigationState)
    {
        NavigationState = navigationState;
        VisibleRowCount = CalculateVisibleRowCount();
    }

    /// <summary>
    /// Total number of loaded records.
    /// </summary>
    public int TotalRows => NavigationState.TotalLoaded;

    /// <summary>
    /// Total number of columns.
    /// </summary>
    public int TotalColumns => NavigationState.Columns.Count;

    /// <summary>
    /// Number of scrollable columns (total minus fixed).
    /// </summary>
    public int ScrollableColumnCount => Math.Max(0, TotalColumns - FixedColumnCount);

    /// <summary>
    /// Whether vertical scrolling up is possible.
    /// </summary>
    public bool CanScrollUp => FirstVisibleRow > 0;

    /// <summary>
    /// Whether vertical scrolling down is possible.
    /// </summary>
    public bool CanScrollDown => FirstVisibleRow + VisibleRowCount < TotalRows ||
                                  NavigationState.MoreRecordsAvailable;

    /// <summary>
    /// Whether horizontal scrolling left is possible.
    /// </summary>
    public bool CanScrollLeft => FirstScrollableColumn > 0;

    /// <summary>
    /// Whether horizontal scrolling right is possible.
    /// </summary>
    public bool CanScrollRight => FirstScrollableColumn < ScrollableColumnCount - 1;

    /// <summary>
    /// Moves the selection up one row.
    /// </summary>
    /// <returns>True if moved, false if at top.</returns>
    public bool MoveUp()
    {
        if (SelectedRowIndex > 0)
        {
            SelectedRowIndex--;
            EnsureSelectedRowVisible();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Moves the selection down one row.
    /// </summary>
    /// <returns>True if moved, false if at bottom of loaded records.</returns>
    public bool MoveDown()
    {
        if (SelectedRowIndex < TotalRows - 1)
        {
            SelectedRowIndex++;
            EnsureSelectedRowVisible();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Moves up by one page.
    /// </summary>
    public void PageUp()
    {
        SelectedRowIndex = Math.Max(0, SelectedRowIndex - VisibleRowCount);
        EnsureSelectedRowVisible();
    }

    /// <summary>
    /// Moves down by one page.
    /// </summary>
    public void PageDown()
    {
        SelectedRowIndex = Math.Min(TotalRows - 1, SelectedRowIndex + VisibleRowCount);
        EnsureSelectedRowVisible();
    }

    /// <summary>
    /// Jumps to the first row.
    /// </summary>
    public void GoToStart()
    {
        SelectedRowIndex = 0;
        FirstVisibleRow = 0;
    }

    /// <summary>
    /// Jumps to the last row.
    /// </summary>
    public void GoToEnd()
    {
        SelectedRowIndex = TotalRows - 1;
        EnsureSelectedRowVisible();
    }

    /// <summary>
    /// Moves cell selection right by one column.
    /// </summary>
    /// <returns>True if moved.</returns>
    public bool MoveRight()
    {
        if (SelectedColumnIndex < TotalColumns - 1)
        {
            SelectedColumnIndex++;
            EnsureSelectedColumnVisible();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Moves cell selection left by one column.
    /// </summary>
    /// <returns>True if moved.</returns>
    public bool MoveLeft()
    {
        if (SelectedColumnIndex > 0)
        {
            SelectedColumnIndex--;
            EnsureSelectedColumnVisible();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Scrolls right by one column (viewport scroll without changing selection).
    /// </summary>
    /// <returns>True if scrolled.</returns>
    public bool ScrollRight()
    {
        if (CanScrollRight)
        {
            FirstScrollableColumn++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Scrolls left by one column (viewport scroll without changing selection).
    /// </summary>
    /// <returns>True if scrolled.</returns>
    public bool ScrollLeft()
    {
        if (CanScrollLeft)
        {
            FirstScrollableColumn--;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Updates the visible row count when terminal is resized.
    /// </summary>
    public void UpdateVisibleRowCount()
    {
        VisibleRowCount = CalculateVisibleRowCount();
        EnsureSelectedRowVisible();
    }

    /// <summary>
    /// Sets the column layouts after calculation.
    /// </summary>
    public void SetColumnLayouts(IReadOnlyList<ColumnLayout> layouts)
    {
        ColumnLayouts = layouts;
    }

    /// <summary>
    /// Ensures the selected row is visible in the viewport.
    /// </summary>
    private void EnsureSelectedRowVisible()
    {
        // If selected is above viewport, scroll up
        if (SelectedRowIndex < FirstVisibleRow)
        {
            FirstVisibleRow = SelectedRowIndex;
        }
        // If selected is below viewport, scroll down
        else if (SelectedRowIndex >= FirstVisibleRow + VisibleRowCount)
        {
            FirstVisibleRow = SelectedRowIndex - VisibleRowCount + 1;
        }
    }

    /// <summary>
    /// Ensures the selected column is visible in the viewport.
    /// </summary>
    private void EnsureSelectedColumnVisible()
    {
        // Fixed columns are always visible
        if (SelectedColumnIndex < FixedColumnCount)
        {
            return;
        }

        // For scrollable columns, adjust FirstScrollableColumn
        var scrollableIndex = SelectedColumnIndex - FixedColumnCount;

        // If selected column is before the visible scrollable range
        if (scrollableIndex < FirstScrollableColumn)
        {
            FirstScrollableColumn = scrollableIndex;
        }
        // If selected column is after the visible scrollable range, we need to check
        // which columns are actually visible based on layout
        else if (ColumnLayouts.Count > 0)
        {
            var layout = ColumnLayouts.FirstOrDefault(l => l.ColumnIndex == SelectedColumnIndex);
            if (layout != null && !layout.IsVisible)
            {
                // Scroll right until the column becomes visible
                FirstScrollableColumn = scrollableIndex;
            }
        }
    }

    /// <summary>
    /// Gets the value of the currently selected cell.
    /// </summary>
    /// <returns>The string value of the selected cell, or null if not available.</returns>
    public string? GetSelectedCellValue()
    {
        if (SelectedRowIndex < 0 || SelectedRowIndex >= TotalRows ||
            SelectedColumnIndex < 0 || SelectedColumnIndex >= TotalColumns)
        {
            return null;
        }

        var record = NavigationState.AllRecords[SelectedRowIndex];
        var column = NavigationState.Columns[SelectedColumnIndex];
        var key = column.Alias ?? column.LogicalName;

        if (record.TryGetValue(key, out var value))
        {
            return ValueFormatter.GetPlainValue(value, column);
        }

        return null;
    }

    /// <summary>
    /// Gets the display name of the currently selected column.
    /// </summary>
    public string GetSelectedColumnName()
    {
        if (SelectedColumnIndex < 0 || SelectedColumnIndex >= TotalColumns)
        {
            return string.Empty;
        }

        var column = NavigationState.Columns[SelectedColumnIndex];
        return column.Alias ?? column.LogicalName;
    }

    /// <summary>
    /// Recalculates visible row count for current terminal height.
    /// Call this when terminal is resized.
    /// </summary>
    public void RecalculateVisibleRowCount()
    {
        VisibleRowCount = CalculateVisibleRowCount();
    }

    /// <summary>
    /// Calculates how many rows fit in the current terminal.
    /// </summary>
    private static int CalculateVisibleRowCount()
    {
        // Reserve space for: header panel (~5 lines with profile), column headers (2 lines),
        // status bar (2 lines), borders
        const int reservedLines = 11;
        return Math.Max(3, Console.WindowHeight - reservedLines);
    }

    /// <summary>
    /// Checks if at the end of loaded records and more are available.
    /// </summary>
    public bool NeedsMoreRecords => SelectedRowIndex >= TotalRows - 1 &&
                                     NavigationState.MoreRecordsAvailable;

    /// <summary>
    /// Synchronizes the selected row index to the navigation state.
    /// </summary>
    public void SyncToNavigationState()
    {
        NavigationState.CurrentIndex = SelectedRowIndex;
    }
}

/// <summary>
/// Layout information for a single column in the viewport.
/// </summary>
internal sealed class ColumnLayout
{
    /// <summary>
    /// Index of this column in the data.
    /// </summary>
    public required int ColumnIndex { get; init; }

    /// <summary>
    /// Display width of this column.
    /// </summary>
    public required int DisplayWidth { get; init; }

    /// <summary>
    /// Starting X position on screen.
    /// </summary>
    public required int ScreenX { get; init; }

    /// <summary>
    /// Whether this column is visible in the current viewport.
    /// </summary>
    public required bool IsVisible { get; init; }

    /// <summary>
    /// Whether this is a fixed (non-scrolling) column.
    /// </summary>
    public required bool IsFixed { get; init; }

    /// <summary>
    /// The column header text.
    /// </summary>
    public required string HeaderText { get; init; }
}
