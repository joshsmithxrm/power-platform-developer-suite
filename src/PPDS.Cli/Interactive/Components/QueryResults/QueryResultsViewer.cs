using PPDS.Dataverse.Query;
using Spectre.Console;

namespace PPDS.Cli.Interactive.Components.QueryResults;

/// <summary>
/// Main orchestrator for displaying query results.
/// Chooses between table view and record view based on column count.
/// </summary>
internal static class QueryResultsViewer
{
    /// <summary>
    /// Maximum columns for table view. Above this, record view is used.
    /// </summary>
    public const int TableViewMaxColumns = 6;

    /// <summary>
    /// Result of showing query results.
    /// </summary>
    public enum ShowResult
    {
        /// <summary>User wants to go back to the main menu.</summary>
        Back,
        /// <summary>User wants to enter a new query.</summary>
        NewQuery,
        /// <summary>User wants to exit the entire interactive CLI.</summary>
        Exit
    }

    /// <summary>
    /// Shows query results with automatic view selection.
    /// </summary>
    /// <param name="result">The query result to display.</param>
    /// <param name="fetchPage">Optional function to fetch additional pages.</param>
    /// <param name="environmentUrl">The environment URL for building record links.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The action the user wants to take next.</returns>
    public static async Task<ShowResult> ShowAsync(
        QueryResult result,
        Func<int, string?, Task<QueryResult>>? fetchPage,
        string? environmentUrl,
        CancellationToken cancellationToken)
    {
        if (result.Count == 0)
        {
            ShowEmptyResult(result);
            return ShowResult.NewQuery;
        }

        var state = new RecordNavigationState(result, environmentUrl);

        // Choose initial view based on column count
        var useTableView = result.Columns.Count <= TableViewMaxColumns;

        while (!cancellationToken.IsCancellationRequested)
        {
            ViewResult viewResult;

            if (useTableView)
            {
                viewResult = await TableView.ShowAsync(state, fetchPage, cancellationToken);
            }
            else
            {
                viewResult = await RecordView.ShowAsync(state, fetchPage, cancellationToken);
            }

            switch (viewResult)
            {
                case ViewResult.Back:
                    return ShowResult.Back;

                case ViewResult.NewQuery:
                    return ShowResult.NewQuery;

                case ViewResult.Exit:
                    return ShowResult.Exit;

                case ViewResult.SwitchToRecordView:
                    useTableView = false;
                    break;

                case ViewResult.SwitchToTableView:
                    useTableView = true;
                    break;
            }
        }

        return ShowResult.Back;
    }

    private static void ShowEmptyResult(QueryResult result)
    {
        AnsiConsole.Clear();

        var content =
            $"{Styles.MutedText("Entity:")} {Markup.Escape(result.EntityLogicalName)}\n" +
            $"{Styles.MutedText("Time:")} {result.ExecutionTimeMs}ms";

        var panel = new Panel(content)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Styles.HeaderBorder,
            Header = new PanelHeader(" Query Results ", Justify.Center),
            Padding = new Padding(1, 0, 1, 0)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Styles.WarningText("No records found."));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(Styles.MutedText("Press any key to continue..."));
        Console.ReadKey(true);
    }
}
