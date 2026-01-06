using PPDS.Dataverse.Query;
using Spectre.Console;

namespace PPDS.Cli.Interactive.Components.QueryResults;

/// <summary>
/// Handles rendering of the interactive table viewport.
/// Uses direct console output for efficient updates.
/// </summary>
internal sealed class TableViewport
{
    private readonly InteractiveTableState _state;
    private const int MinColumnWidth = 4;
    private const int ColumnPadding = 2; // Space between columns

    // Track positions for efficient partial redraws
    private int _headerHeight;
    private int _tableStartY;

    public TableViewport(InteractiveTableState state)
    {
        _state = state;
    }

    /// <summary>
    /// Performs a full render of the table.
    /// </summary>
    public void Render()
    {
        Console.Clear();
        Console.CursorVisible = false;

        // Calculate column layouts for current terminal width
        CalculateColumnLayouts();

        // Render header panel
        RenderHeader();
        _tableStartY = Console.CursorTop;

        // Render column headers
        RenderColumnHeaders();

        // Render visible rows
        RenderRows();

        // Render status bar
        RenderStatusBar();
    }

    /// <summary>
    /// Updates just the row highlighting (fast path for up/down navigation).
    /// </summary>
    public void UpdateRowHighlight(int previousRow, int newRow)
    {
        // Only redraw if both rows are visible
        var visibleStart = _state.FirstVisibleRow;
        var visibleEnd = visibleStart + _state.VisibleRowCount;

        // Redraw previous row (remove highlight)
        if (previousRow >= visibleStart && previousRow < visibleEnd)
        {
            var screenRow = _tableStartY + 2 + (previousRow - visibleStart); // +2 for header rows
            RenderSingleRow(previousRow, screenRow, isSelected: false);
        }

        // Redraw new row (add highlight)
        if (newRow >= visibleStart && newRow < visibleEnd)
        {
            var screenRow = _tableStartY + 2 + (newRow - visibleStart);
            RenderSingleRow(newRow, screenRow, isSelected: true);
        }

        // Update status bar position indicator
        RenderStatusBar();
    }

    /// <summary>
    /// Renders just the status bar.
    /// </summary>
    public void RenderStatusBar()
    {
        var statusY = _tableStartY + 2 + _state.VisibleRowCount + 1;
        Console.SetCursorPosition(0, statusY);

        var terminalWidth = Console.WindowWidth;

        // Clear the line
        Console.Write(new string(' ', terminalWidth));
        Console.SetCursorPosition(0, statusY);

        // Build status line
        var nav = _state.NavigationState;
        var position = $"Row {_state.SelectedRowIndex + 1}/{nav.DisplayTotal}";

        var scrollIndicator = "";
        if (_state.CanScrollLeft || _state.CanScrollRight)
        {
            var leftArrow = _state.CanScrollLeft ? "<" : " ";
            var rightArrow = _state.CanScrollRight ? ">" : " ";
            scrollIndicator = $" | Col {leftArrow}{_state.FirstScrollableColumn + 1}{rightArrow}";
        }

        // Status message (temporary)
        var message = _state.StatusMessage ?? "";
        if (!string.IsNullOrEmpty(message))
        {
            message = $" | {message}";
        }

        var statusText = $"{position}{scrollIndicator}{message}";

        // Help text on the right
        var helpText = "[↑↓] Row  [←→] Col  [Enter] View  [N]ew  [Esc] Back";

        // Calculate positioning
        var availableWidth = terminalWidth - statusText.Length - 2;
        if (availableWidth > helpText.Length)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(statusText);
            Console.SetCursorPosition(terminalWidth - helpText.Length - 1, statusY);
            Console.Write(helpText);
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(statusText);
            Console.ResetColor();
        }

        // Clear status message after showing
        _state.StatusMessage = null;
    }

    private void RenderHeader()
    {
        var nav = _state.NavigationState;

        // Use Spectre.Console for the styled header panel
        var recordRange = _state.VisibleRowCount < nav.TotalLoaded
            ? $"{_state.FirstVisibleRow + 1}-{Math.Min(_state.FirstVisibleRow + _state.VisibleRowCount, nav.TotalLoaded)}"
            : $"1-{nav.TotalLoaded}";

        var headerContent =
            $"{Styles.MutedText("Entity:")} {Markup.Escape(nav.EntityName)}\n" +
            $"{Styles.MutedText("Records:")} {recordRange} of {nav.DisplayTotal}";

        var header = new Panel(headerContent)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Styles.HeaderBorder,
            Header = new PanelHeader(" Query Results ", Justify.Center),
            Padding = new Padding(1, 0, 1, 0)
        };
        AnsiConsole.Write(header);
        AnsiConsole.WriteLine();

        _headerHeight = Console.CursorTop;
    }

    private void RenderColumnHeaders()
    {
        var layouts = _state.ColumnLayouts;
        if (layouts.Count == 0) return;

        // Top border
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("┌");
        foreach (var layout in layouts.Where(l => l.IsVisible))
        {
            Console.Write(new string('─', layout.DisplayWidth + ColumnPadding));
            Console.Write("┬");
        }
        Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
        Console.WriteLine("┐");

        // Header text
        Console.Write("│");
        foreach (var layout in layouts.Where(l => l.IsVisible))
        {
            Console.ForegroundColor = ConsoleColor.White;
            var text = TruncateOrPad(layout.HeaderText, layout.DisplayWidth + ColumnPadding);
            Console.Write(text);
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write("│");
        }
        Console.WriteLine();

        // Bottom border of header
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("├");
        foreach (var layout in layouts.Where(l => l.IsVisible))
        {
            Console.Write(new string('─', layout.DisplayWidth + ColumnPadding));
            Console.Write("┼");
        }
        Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
        Console.WriteLine("┤");

        Console.ResetColor();
    }

    private void RenderRows()
    {
        var nav = _state.NavigationState;
        var visibleLayouts = _state.ColumnLayouts.Where(l => l.IsVisible).ToList();

        for (var i = 0; i < _state.VisibleRowCount; i++)
        {
            var rowIndex = _state.FirstVisibleRow + i;
            if (rowIndex >= nav.TotalLoaded) break;

            var isSelected = rowIndex == _state.SelectedRowIndex;
            RenderDataRow(rowIndex, isSelected, visibleLayouts);
        }

        // Bottom border
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.Write("└");
        foreach (var layout in visibleLayouts)
        {
            Console.Write(new string('─', layout.DisplayWidth + ColumnPadding));
            Console.Write("┴");
        }
        Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
        Console.WriteLine("┘");
        Console.ResetColor();
    }

    private void RenderDataRow(int rowIndex, bool isSelected, IReadOnlyList<ColumnLayout> visibleLayouts)
    {
        var nav = _state.NavigationState;
        var record = nav.AllRecords[rowIndex];
        var columns = nav.Columns;

        if (isSelected)
        {
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.White;
        }
        else
        {
            Console.ResetColor();
        }

        Console.ForegroundColor = isSelected ? ConsoleColor.White : ConsoleColor.DarkCyan;
        Console.Write("│");

        foreach (var layout in visibleLayouts)
        {
            var column = columns[layout.ColumnIndex];
            var key = column.Alias ?? column.LogicalName;
            record.TryGetValue(key, out var value);

            // Get plain value (no markup for direct console output)
            var displayValue = GetPlainDisplayValue(value, column, layout.DisplayWidth);

            Console.ForegroundColor = isSelected ? ConsoleColor.White : ConsoleColor.Gray;
            Console.Write(TruncateOrPad(displayValue, layout.DisplayWidth + ColumnPadding));
            Console.ForegroundColor = isSelected ? ConsoleColor.White : ConsoleColor.DarkCyan;
            Console.Write("│");
        }

        Console.ResetColor();
        Console.WriteLine();
    }

    private void RenderSingleRow(int rowIndex, int screenY, bool isSelected)
    {
        Console.SetCursorPosition(0, screenY);

        var visibleLayouts = _state.ColumnLayouts.Where(l => l.IsVisible).ToList();
        RenderDataRow(rowIndex, isSelected, visibleLayouts);
    }

    private void CalculateColumnLayouts()
    {
        var columns = _state.NavigationState.Columns;
        var terminalWidth = Console.WindowWidth;
        var layouts = new List<ColumnLayout>();

        // Calculate content width for each column
        var columnWidths = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var headerWidth = (column.Alias ?? column.LogicalName).Length;

            // Sample content to determine width
            var contentWidths = _state.NavigationState.AllRecords
                .Take(50)
                .Select(r =>
                {
                    var key = column.Alias ?? column.LogicalName;
                    r.TryGetValue(key, out var value);
                    return ValueFormatter.GetPlainValue(value, column).Length;
                })
                .ToList();

            var maxContentWidth = contentWidths.Count > 0 ? contentWidths.Max() : 0;
            columnWidths[i] = Math.Max(MinColumnWidth, Math.Max(headerWidth, maxContentWidth));
        }

        // Calculate visible columns based on terminal width
        var currentX = 1; // Start after left border
        var borderWidth = 1; // Width of each column separator

        for (var i = 0; i < columns.Count; i++)
        {
            var isFixed = i < _state.FixedColumnCount;

            // For scrollable columns, check if they should be visible
            var scrollableIndex = i - _state.FixedColumnCount;
            var isVisible = isFixed || scrollableIndex >= _state.FirstScrollableColumn;

            if (!isVisible)
            {
                layouts.Add(new ColumnLayout
                {
                    ColumnIndex = i,
                    DisplayWidth = columnWidths[i],
                    ScreenX = -1,
                    IsVisible = false,
                    IsFixed = isFixed,
                    HeaderText = columns[i].Alias ?? columns[i].LogicalName
                });
                continue;
            }

            // Check if column fits in remaining width
            var neededWidth = columnWidths[i] + ColumnPadding + borderWidth;
            if (currentX + neededWidth > terminalWidth - 1)
            {
                // Don't show columns that don't fit
                layouts.Add(new ColumnLayout
                {
                    ColumnIndex = i,
                    DisplayWidth = columnWidths[i],
                    ScreenX = -1,
                    IsVisible = false,
                    IsFixed = isFixed,
                    HeaderText = columns[i].Alias ?? columns[i].LogicalName
                });
                continue;
            }

            layouts.Add(new ColumnLayout
            {
                ColumnIndex = i,
                DisplayWidth = columnWidths[i],
                ScreenX = currentX,
                IsVisible = true,
                IsFixed = isFixed,
                HeaderText = columns[i].Alias ?? columns[i].LogicalName
            });

            currentX += neededWidth;
        }

        _state.SetColumnLayouts(layouts);
    }

    private static string TruncateOrPad(string text, int width)
    {
        if (text.Length > width)
        {
            return text[..(width - 1)] + "…";
        }
        return text.PadRight(width);
    }

    private static string GetPlainDisplayValue(QueryValue? value, QueryColumn column, int maxWidth)
    {
        if (value?.Value == null)
        {
            return "-";
        }

        var display = ValueFormatter.GetPlainValue(value, column);

        if (display.Length > maxWidth)
        {
            return display[..(maxWidth - 1)] + "…";
        }

        return display;
    }
}
