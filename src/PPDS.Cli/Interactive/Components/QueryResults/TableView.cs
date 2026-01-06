using PPDS.Dataverse.Query;
using Spectre.Console;

namespace PPDS.Cli.Interactive.Components.QueryResults;

/// <summary>
/// Displays query results in a table format for narrow queries (few columns).
/// </summary>
internal static class TableView
{
    private const int MaxRowsPerPage = 25;
    private const int MinColumnWidth = 8;
    private const int MaxColumnWidth = 50;

    /// <summary>
    /// Navigation actions available in table view.
    /// </summary>
    private enum TableAction
    {
        NextPage,
        PreviousPage,
        SwitchToRecordView,
        NewQuery,
        Back
    }

    /// <summary>
    /// Shows query results in table format with navigation.
    /// </summary>
    public static async Task<ViewResult> ShowAsync(
        RecordNavigationState state,
        Func<int, string?, Task<QueryResult>>? fetchPage,
        CancellationToken cancellationToken)
    {
        var displayOffset = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            RenderTable(state, displayOffset);

            var action = ShowNavigationMenu(state, displayOffset);

            switch (action)
            {
                case TableAction.NextPage:
                    if (displayOffset + MaxRowsPerPage < state.TotalLoaded)
                    {
                        displayOffset += MaxRowsPerPage;
                    }
                    else if (state.MoreRecordsAvailable && fetchPage != null)
                    {
                        await LoadMoreRecords(state, fetchPage, cancellationToken);
                        if (displayOffset + MaxRowsPerPage < state.TotalLoaded)
                        {
                            displayOffset += MaxRowsPerPage;
                        }
                    }
                    break;

                case TableAction.PreviousPage:
                    displayOffset = Math.Max(0, displayOffset - MaxRowsPerPage);
                    break;

                case TableAction.SwitchToRecordView:
                    state.CurrentIndex = displayOffset;
                    return ViewResult.SwitchToRecordView;

                case TableAction.NewQuery:
                    return ViewResult.NewQuery;

                case TableAction.Back:
                    return ViewResult.Back;
            }
        }

        return ViewResult.Back;
    }

    private static void RenderTable(RecordNavigationState state, int displayOffset)
    {
        // Header panel
        var recordRange = $"{displayOffset + 1}-{Math.Min(displayOffset + MaxRowsPerPage, state.TotalLoaded)}";
        var headerContent =
            $"{Styles.MutedText("Entity:")} {Markup.Escape(state.EntityName)}\n" +
            $"{Styles.MutedText("Records:")} {recordRange} of {state.DisplayTotal}";

        var header = new Panel(headerContent)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Styles.HeaderBorder,
            Header = new PanelHeader(" Query Results ", Justify.Center),
            Padding = new Padding(1, 0, 1, 0)
        };
        AnsiConsole.Write(header);
        AnsiConsole.WriteLine();

        // Calculate column widths
        var terminalWidth = Console.WindowWidth;
        var columnWidths = CalculateColumnWidths(state, terminalWidth);

        // Create table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Styles.Primary);

        // Add columns with calculated widths
        for (var i = 0; i < state.Columns.Count; i++)
        {
            var column = state.Columns[i];
            var headerText = column.Alias ?? column.LogicalName;
            var tableColumn = new TableColumn(Markup.Escape(headerText))
                .Width(columnWidths[i]);

            // Right-align numeric columns
            if (IsNumericColumn(column))
            {
                tableColumn.RightAligned();
            }

            table.AddColumn(tableColumn);
        }

        // Add rows
        var displayRecords = state.AllRecords
            .Skip(displayOffset)
            .Take(MaxRowsPerPage);

        foreach (var record in displayRecords)
        {
            var cells = new List<string>();

            for (var i = 0; i < state.Columns.Count; i++)
            {
                var column = state.Columns[i];
                var key = column.Alias ?? column.LogicalName;
                record.TryGetValue(key, out var value);

                var formatted = ValueFormatter.FormatForTable(value, column, columnWidths[i]);
                cells.Add(formatted);
            }

            table.AddRow(cells.ToArray());
        }

        AnsiConsole.Write(table);

        // Summary line
        AnsiConsole.WriteLine();
        var summary = Styles.MutedText($"Time: {state.ExecutionTimeMs}ms");
        if (state.MoreRecordsAvailable)
        {
            summary += " | " + Styles.PrimaryText("More records available");
        }
        AnsiConsole.MarkupLine(summary);
    }

    private static int[] CalculateColumnWidths(RecordNavigationState state, int terminalWidth)
    {
        var columnCount = state.Columns.Count;

        // Reserve space for borders: | col | col | = (columnCount + 1) * 3 + padding
        var reservedWidth = (columnCount + 1) * 3 + 4;
        var availableWidth = Math.Max(terminalWidth - reservedWidth, columnCount * MinColumnWidth);

        // Calculate desired width for each column based on content
        var desiredWidths = new int[columnCount];

        for (var i = 0; i < columnCount; i++)
        {
            var column = state.Columns[i];
            var headerWidth = (column.Alias ?? column.LogicalName).Length;

            // Sample content width from records
            var contentWidths = state.AllRecords
                .Take(50) // Sample first 50 records
                .Select(r =>
                {
                    var key = column.Alias ?? column.LogicalName;
                    r.TryGetValue(key, out var value);
                    return ValueFormatter.GetPlainValue(value, column).Length;
                })
                .ToList();

            var maxContentWidth = contentWidths.Count > 0 ? contentWidths.Max() : 0;
            desiredWidths[i] = Math.Clamp(
                Math.Max(headerWidth, maxContentWidth),
                MinColumnWidth,
                MaxColumnWidth);
        }

        // If total desired fits, use it
        var totalDesired = desiredWidths.Sum();
        if (totalDesired <= availableWidth)
        {
            return desiredWidths;
        }

        // Scale down proportionally
        var scale = (double)availableWidth / totalDesired;
        var scaledWidths = desiredWidths
            .Select(w => Math.Max(MinColumnWidth, (int)(w * scale)))
            .ToArray();

        return scaledWidths;
    }

    private static bool IsNumericColumn(QueryColumn column)
    {
        return column.DataType is
            QueryColumnType.Integer or
            QueryColumnType.BigInt or
            QueryColumnType.Decimal or
            QueryColumnType.Double or
            QueryColumnType.Money;
    }

    private static TableAction ShowNavigationMenu(RecordNavigationState state, int displayOffset)
    {
        var choices = new List<TableNavigationChoice>();

        var hasNextPage = displayOffset + MaxRowsPerPage < state.TotalLoaded || state.MoreRecordsAvailable;
        var hasPrevPage = displayOffset > 0;

        if (hasNextPage)
        {
            choices.Add(new TableNavigationChoice
            {
                Label = "Next Page",
                Action = TableAction.NextPage
            });
        }

        if (hasPrevPage)
        {
            choices.Add(new TableNavigationChoice
            {
                Label = "Previous Page",
                Action = TableAction.PreviousPage
            });
        }

        choices.Add(new TableNavigationChoice
        {
            Label = "View Records (detailed)",
            Action = TableAction.SwitchToRecordView
        });

        choices.Add(new TableNavigationChoice
        {
            Label = "New Query",
            Action = TableAction.NewQuery
        });

        choices.Add(new TableNavigationChoice
        {
            Label = "[Back]",
            Action = TableAction.Back
        });

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<TableNavigationChoice>()
                .Title(Styles.MutedText("Navigate:"))
                .HighlightStyle(Styles.SelectionHighlight)
                .AddChoices(choices)
                .UseConverter(FormatChoice));

        return selected.Action;
    }

    private static string FormatChoice(TableNavigationChoice choice)
    {
        if (choice.Action == TableAction.Back)
        {
            return Styles.MutedText(choice.Label);
        }
        return choice.Label;
    }

    private static async Task LoadMoreRecords(
        RecordNavigationState state,
        Func<int, string?, Task<QueryResult>> fetchPage,
        CancellationToken cancellationToken)
    {
        var (pageNumber, cookie) = state.GetNextPageInfo();

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Styles.Primary)
            .StartAsync($"Loading page {pageNumber}...", async _ =>
                await fetchPage(pageNumber, cookie));

        state.AddPage(result);
    }

    private sealed class TableNavigationChoice
    {
        public required string Label { get; init; }
        public required TableAction Action { get; init; }
    }
}

/// <summary>
/// Result of a view operation.
/// </summary>
internal enum ViewResult
{
    /// <summary>User wants to go back to the main menu.</summary>
    Back,
    /// <summary>User wants to enter a new query.</summary>
    NewQuery,
    /// <summary>User wants to switch to record view.</summary>
    SwitchToRecordView,
    /// <summary>User wants to switch to table view.</summary>
    SwitchToTableView,
    /// <summary>User wants to exit the entire interactive CLI.</summary>
    Exit
}
