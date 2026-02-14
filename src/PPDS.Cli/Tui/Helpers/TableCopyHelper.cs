using System.Data;
using System.Text;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Helpers;

/// <summary>
/// Shared copy logic for table views. Implements smart copy behavior:
/// single cell copies raw value, multi-cell copies with headers (TSV).
/// Ctrl+Shift+C inverts the default (single cell adds header, multi-cell removes headers).
/// </summary>
internal static class TableCopyHelper
{
    /// <summary>
    /// Result of a copy operation, containing the status message to display.
    /// </summary>
    internal readonly record struct CopyResult(bool Success, string StatusMessage);

    /// <summary>
    /// Returns the copy-related hint text for the status bar, varying by selection state.
    /// </summary>
    internal static string GetCopyHint(TableView tableView)
    {
        var selection = GetSelectionExtent(tableView);
        return selection.IsSingleCell
            ? "Ctrl+C: copy value | Ctrl+Shift+C: with header"
            : "Ctrl+C: copy with headers | Ctrl+Shift+C: values only";
    }

    /// <summary>
    /// Performs a smart copy based on the current selection in a TableView.
    /// </summary>
    /// <param name="tableView">The Terminal.Gui TableView with selection state.</param>
    /// <param name="sourceTable">The underlying DataTable containing the data.</param>
    /// <param name="invertHeaders">When true, inverts the default header behavior (Ctrl+Shift+C).</param>
    internal static CopyResult CopySelection(TableView tableView, DataTable sourceTable, bool invertHeaders)
    {
        if (sourceTable == null || sourceTable.Rows.Count == 0 || tableView.SelectedRow < 0)
            return new CopyResult(false, "No cell selected");

        var selection = GetSelectionExtent(tableView);

        if (selection.IsSingleCell)
            return CopySingleCell(tableView, sourceTable, invertHeaders);

        return CopyMultiCell(sourceTable, selection, includeHeaders: !invertHeaders);
    }

    private static CopyResult CopySingleCell(TableView tableView, DataTable sourceTable, bool invertHeaders)
    {
        var row = tableView.SelectedRow;
        var col = tableView.SelectedColumn;

        if (row < 0 || row >= sourceTable.Rows.Count ||
            col < 0 || col >= sourceTable.Columns.Count)
            return new CopyResult(false, "No cell selected");

        var value = sourceTable.Rows[row][col]?.ToString() ?? string.Empty;

        string text;
        string hint;

        if (invertHeaders)
        {
            // Ctrl+Shift+C on single cell: include header
            var header = sourceTable.Columns[col].ColumnName;
            text = $"{header}\n{SanitizeValue(value)}";
            hint = "Copied with header (Ctrl+C for value only)";
        }
        else
        {
            // Ctrl+C on single cell: raw value
            text = value;
            var displayValue = value.Length > 40 ? value[..37] + "..." : value;
            hint = $"Copied: {displayValue} (Ctrl+Shift+C to include header)";
        }

        var success = Clipboard.TrySetClipboardData(text);
        return new CopyResult(success, success ? hint : $"Copy failed. Value: {value}");
    }

    private static CopyResult CopyMultiCell(
        DataTable sourceTable,
        SelectionExtent selection,
        bool includeHeaders)
    {
        var sb = new StringBuilder();
        var colStart = selection.ColStart;
        var colEnd = selection.ColEnd;

        // Clamp to actual column bounds
        colStart = Math.Max(0, colStart);
        colEnd = Math.Min(sourceTable.Columns.Count - 1, colEnd);
        var colCount = colEnd - colStart + 1;

        if (includeHeaders)
        {
            var headers = new List<string>();
            for (int c = colStart; c <= colEnd; c++)
            {
                headers.Add(sourceTable.Columns[c].ColumnName);
            }
            sb.AppendLine(string.Join("\t", headers));
        }

        var rowIndices = selection.RowIndices;
        foreach (var rowIndex in rowIndices)
        {
            if (rowIndex < 0 || rowIndex >= sourceTable.Rows.Count) continue;

            var row = sourceTable.Rows[rowIndex];
            var values = new List<string>();
            for (int c = colStart; c <= colEnd; c++)
            {
                values.Add(SanitizeValue(row[c]?.ToString() ?? string.Empty));
            }
            sb.AppendLine(string.Join("\t", values));
        }

        var text = sb.ToString().TrimEnd();
        var success = Clipboard.TrySetClipboardData(text);

        var rowCount = rowIndices.Count;
        var sizeDesc = colCount < sourceTable.Columns.Count
            ? $"{rowCount} rows x {colCount} cols"
            : $"{rowCount} row(s)";

        string hint;
        if (includeHeaders)
        {
            hint = $"Copied {sizeDesc} with headers (Ctrl+Shift+C for values only)";
        }
        else
        {
            hint = $"Copied {sizeDesc} (Ctrl+C to include headers)";
        }

        return new CopyResult(success, success ? hint : "Copy failed");
    }

    private static string SanitizeValue(string value)
    {
        return value.Replace("\t", " ").Replace("\n", " ").Replace("\r", "");
    }

    private readonly record struct SelectionExtent(
        List<int> RowIndices,
        int ColStart,
        int ColEnd,
        bool IsSingleCell);

    private static SelectionExtent GetSelectionExtent(TableView tableView)
    {
        var rowIndices = new List<int>();
        int colStart = int.MaxValue;
        int colEnd = int.MinValue;

        if (tableView.MultiSelectedRegions != null)
        {
            foreach (var region in tableView.MultiSelectedRegions)
            {
                var rect = region.Rect;
                for (int r = rect.Y; r < rect.Y + rect.Height; r++)
                {
                    if (!rowIndices.Contains(r))
                        rowIndices.Add(r);
                }
                colStart = Math.Min(colStart, rect.X);
                colEnd = Math.Max(colEnd, rect.X + rect.Width - 1);
            }
        }

        // If no multi-selection regions, fall back to single selected cell
        if (rowIndices.Count == 0)
        {
            rowIndices.Add(tableView.SelectedRow);
            colStart = tableView.SelectedColumn;
            colEnd = tableView.SelectedColumn;
        }

        var isSingleCell = rowIndices.Count == 1 && colStart == colEnd;

        rowIndices.Sort();
        return new SelectionExtent(rowIndices, colStart, colEnd, isSingleCell);
    }
}
