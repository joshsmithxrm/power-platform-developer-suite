using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.Query;
using Spectre.Console;

namespace PPDS.Cli.Interactive.Components.QueryResults;

/// <summary>
/// Interactive table view with keyboard navigation.
/// Supports arrow keys for row selection and horizontal scrolling.
/// </summary>
internal static class InteractiveTableView
{
    /// <summary>
    /// Shows query results in an interactive table.
    /// </summary>
    public static async Task<ViewResult> ShowAsync(
        RecordNavigationState navigationState,
        Func<int, string?, Task<QueryResult>>? fetchPage,
        CancellationToken cancellationToken)
    {
        // Prevent Ctrl+C from terminating the app - we handle it as copy
        var previousCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;

        var state = new InteractiveTableState(navigationState);
        var viewport = new TableViewport(state);

        // Initial render
        viewport.Render();

        while (!cancellationToken.IsCancellationRequested)
        {
            var action = TableInputHandler.ReadInput();
            var previousRow = state.SelectedRowIndex;

            switch (action)
            {
                case TableInputAction.MoveUp:
                    {
                        var previousFirstVisible = state.FirstVisibleRow;
                        if (state.MoveUp())
                        {
                            // Full redraw if viewport scrolled, otherwise just update highlight
                            if (state.FirstVisibleRow != previousFirstVisible)
                                viewport.Render();
                            else
                                viewport.UpdateRowHighlight(previousRow, state.SelectedRowIndex);
                        }
                    }
                    break;

                case TableInputAction.MoveDown:
                    {
                        var previousFirstVisible = state.FirstVisibleRow;
                        if (state.MoveDown())
                        {
                            // Full redraw if viewport scrolled, otherwise just update highlight
                            if (state.FirstVisibleRow != previousFirstVisible)
                                viewport.Render();
                            else
                                viewport.UpdateRowHighlight(previousRow, state.SelectedRowIndex);
                        }
                        else if (state.NeedsMoreRecords && fetchPage != null)
                        {
                            // Need to load more records
                            await LoadMoreRecords(state, fetchPage, cancellationToken);
                            if (state.MoveDown())
                            {
                                viewport.Render(); // Full re-render after loading
                            }
                        }
                    }
                    break;

                case TableInputAction.MoveLeft:
                    {
                        var previousFirstScrollable = state.FirstScrollableColumn;
                        if (state.MoveLeft())
                        {
                            // Full redraw if viewport scrolled horizontally
                            if (state.FirstScrollableColumn != previousFirstScrollable)
                                viewport.Render();
                            else
                                viewport.UpdateRowHighlight(previousRow, state.SelectedRowIndex);
                        }
                    }
                    break;

                case TableInputAction.MoveRight:
                    {
                        var previousFirstScrollable = state.FirstScrollableColumn;
                        if (state.MoveRight())
                        {
                            // Full redraw if viewport scrolled horizontally
                            if (state.FirstScrollableColumn != previousFirstScrollable)
                                viewport.Render();
                            else
                                viewport.UpdateRowHighlight(previousRow, state.SelectedRowIndex);
                        }
                    }
                    break;

                case TableInputAction.ScrollLeft:
                    if (state.ScrollLeft())
                    {
                        viewport.Render();
                    }
                    break;

                case TableInputAction.ScrollRight:
                    if (state.ScrollRight())
                    {
                        viewport.Render();
                    }
                    break;

                case TableInputAction.PageUp:
                    state.PageUp();
                    viewport.Render();
                    break;

                case TableInputAction.PageDown:
                    var wasAtEnd = state.SelectedRowIndex == state.TotalRows - 1;
                    state.PageDown();
                    if (wasAtEnd && state.NeedsMoreRecords && fetchPage != null)
                    {
                        await LoadMoreRecords(state, fetchPage, cancellationToken);
                    }
                    viewport.Render();
                    break;

                case TableInputAction.Home:
                    state.GoToStart();
                    viewport.Render();
                    break;

                case TableInputAction.End:
                    // Load all remaining records if going to end
                    while (navigationState.MoreRecordsAvailable && fetchPage != null)
                    {
                        await LoadMoreRecords(state, fetchPage, cancellationToken);
                    }
                    state.GoToEnd();
                    viewport.Render();
                    break;

                case TableInputAction.SelectRow:
                    state.SyncToNavigationState();
                    return ViewResult.SwitchToRecordView;

                case TableInputAction.SwitchToRecordView:
                    state.SyncToNavigationState();
                    return ViewResult.SwitchToRecordView;

                case TableInputAction.OpenInBrowser:
                    state.SyncToNavigationState();
                    OpenRecordInBrowser(navigationState, state, viewport);
                    break;

                case TableInputAction.CopyUrl:
                    state.SyncToNavigationState();
                    CopyRecordUrl(navigationState, state, viewport);
                    break;

                case TableInputAction.CopyCellContent:
                    CopyCellContent(state, viewport);
                    break;

                case TableInputAction.NewQuery:
                    RestoreConsole(previousCtrlC);
                    return ViewResult.NewQuery;

                case TableInputAction.Escape:
                    RestoreConsole(previousCtrlC);
                    return ViewResult.Back;

                case TableInputAction.Exit:
                    RestoreConsole(previousCtrlC);
                    return ViewResult.Exit;

                case TableInputAction.ShowHelp:
                    viewport.ShowHelpOverlay();
                    break;

                case TableInputAction.None:
                    // Unknown key, ignore
                    break;
            }
        }

        RestoreConsole(previousCtrlC);
        return ViewResult.Back;
    }

    private static void OpenRecordInBrowser(
        RecordNavigationState navState,
        InteractiveTableState tableState,
        TableViewport viewport)
    {
        var url = navState.GetCurrentRecordUrl();
        if (url != null)
        {
            if (BrowserHelper.OpenUrl(url))
            {
                tableState.StatusMessage = "Opened in browser";
            }
            else
            {
                tableState.StatusMessage = "Failed to open browser";
            }
        }
        else
        {
            tableState.StatusMessage = "Cannot build URL";
        }
        viewport.RenderStatusBar();
    }

    private static void CopyRecordUrl(
        RecordNavigationState navState,
        InteractiveTableState tableState,
        TableViewport viewport)
    {
        var url = navState.GetCurrentRecordUrl();
        if (url != null)
        {
            if (ClipboardHelper.CopyToClipboard(url))
            {
                tableState.StatusMessage = "URL copied to clipboard";
            }
            else
            {
                tableState.StatusMessage = url; // Show URL if copy failed
            }
        }
        else
        {
            tableState.StatusMessage = "Cannot build URL";
        }
        viewport.RenderStatusBar();
    }

    private static void CopyCellContent(
        InteractiveTableState tableState,
        TableViewport viewport)
    {
        var value = tableState.GetSelectedCellValue();
        if (value != null)
        {
            if (ClipboardHelper.CopyToClipboard(value))
            {
                // Truncate display value for status if too long
                var displayValue = value.Length > 30 ? value[..27] + "..." : value;
                tableState.StatusMessage = $"Copied: {displayValue}";
            }
            else
            {
                tableState.StatusMessage = "Copy failed";
            }
        }
        else
        {
            tableState.StatusMessage = "No value to copy";
        }
        viewport.RenderStatusBar();
    }

    private static async Task LoadMoreRecords(
        InteractiveTableState state,
        Func<int, string?, Task<QueryResult>> fetchPage,
        CancellationToken cancellationToken)
    {
        var (pageNumber, cookie) = state.NavigationState.GetNextPageInfo();

        // Show loading indicator
        state.StatusMessage = $"Loading page {pageNumber}...";

        try
        {
            var result = await fetchPage(pageNumber, cookie);
            state.NavigationState.AddPage(result);
            state.StatusMessage = null;
        }
        catch (Exception ex)
        {
            state.StatusMessage = $"Error: {ex.Message}";
        }
    }

    private static void RestoreConsole(bool previousCtrlC)
    {
        Console.TreatControlCAsInput = previousCtrlC;
        Console.CursorVisible = true;
        Console.ResetColor();
    }
}
