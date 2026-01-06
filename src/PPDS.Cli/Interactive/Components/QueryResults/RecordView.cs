using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.Query;
using Spectre.Console;

namespace PPDS.Cli.Interactive.Components.QueryResults;

/// <summary>
/// Displays query results one record at a time with grouped fields.
/// Best for wide queries with many columns.
/// </summary>
internal static class RecordView
{
    private const int FieldNameWidth = 25;

    /// <summary>
    /// Navigation actions available in record view.
    /// </summary>
    private enum RecordAction
    {
        Previous,
        Next,
        JumpTo,
        ToggleNulls,
        OpenInBrowser,
        CopyUrl,
        SwitchToTableView,
        NewQuery,
        Back
    }

    /// <summary>
    /// Shows query results in record-by-record format with navigation.
    /// </summary>
    public static async Task<ViewResult> ShowAsync(
        RecordNavigationState state,
        Func<int, string?, Task<QueryResult>>? fetchPage,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            RenderRecord(state);

            var action = ShowNavigationMenu(state);

            switch (action)
            {
                case RecordAction.Previous:
                    state.MovePrevious();
                    state.LastAction = NavigationAction.Previous;
                    break;

                case RecordAction.Next:
                    if (!state.MoveNext() && state.MoreRecordsAvailable && fetchPage != null)
                    {
                        // Need to load more records
                        await LoadMoreRecords(state, fetchPage, cancellationToken);
                        state.MoveNext();
                    }
                    state.LastAction = NavigationAction.Next;
                    break;

                case RecordAction.JumpTo:
                    await ShowJumpToDialog(state);
                    state.LastAction = NavigationAction.JumpTo;
                    break;

                case RecordAction.ToggleNulls:
                    state.ShowNullValues = !state.ShowNullValues;
                    state.LastAction = NavigationAction.ToggleNulls;
                    break;

                case RecordAction.OpenInBrowser:
                    OpenRecordInBrowser(state);
                    state.LastAction = NavigationAction.OpenInBrowser;
                    break;

                case RecordAction.CopyUrl:
                    CopyRecordUrl(state);
                    state.LastAction = NavigationAction.CopyUrl;
                    break;

                case RecordAction.SwitchToTableView:
                    return ViewResult.SwitchToTableView;

                case RecordAction.NewQuery:
                    return ViewResult.NewQuery;

                case RecordAction.Back:
                    return ViewResult.Back;
            }
        }

        return ViewResult.Back;
    }

    private static void OpenRecordInBrowser(RecordNavigationState state)
    {
        var url = state.GetCurrentRecordUrl();
        if (url != null)
        {
            BrowserHelper.OpenUrl(url);
        }
    }

    private static void CopyRecordUrl(RecordNavigationState state)
    {
        var url = state.GetCurrentRecordUrl();
        if (url != null)
        {
            if (ClipboardHelper.CopyToClipboard(url))
            {
                AnsiConsole.MarkupLine(Styles.SuccessText("URL copied to clipboard"));
            }
            else
            {
                AnsiConsole.MarkupLine(Styles.MutedText($"URL: {url}"));
            }
            Thread.Sleep(500); // Brief pause to show message
        }
    }

    private static void RenderRecord(RecordNavigationState state)
    {
        // Header panel
        var headerContent =
            $"{Styles.MutedText("Record")} {state.CurrentIndex + 1} {Styles.MutedText("of")} {state.DisplayTotal}\n" +
            $"{Styles.MutedText("Entity:")} {Markup.Escape(state.EntityName)}";

        var header = new Panel(headerContent)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Styles.HeaderBorder,
            Header = new PanelHeader(" Record View ", Justify.Center),
            Padding = new Padding(1, 0, 1, 0)
        };
        AnsiConsole.Write(header);
        AnsiConsole.WriteLine();

        // Group and render fields
        var record = state.CurrentRecord;
        var groups = FieldGrouper.GroupFields(state.Columns, record, state.ShowNullValues);

        foreach (var group in groups.Where(g => g.Fields.Count > 0))
        {
            RenderFieldGroup(group);
        }

        // Summary
        AnsiConsole.MarkupLine(Styles.MutedText($"Time: {state.ExecutionTimeMs}ms | Columns: {state.Columns.Count}"));
        if (!state.ShowNullValues)
        {
            var nullCount = state.Columns.Count - groups.Sum(g => g.Fields.Count);
            if (nullCount > 0)
            {
                AnsiConsole.MarkupLine(Styles.MutedText($"({nullCount} empty fields hidden)"));
            }
        }
    }

    private static void RenderFieldGroup(FieldGroup group)
    {
        // Group header
        AnsiConsole.MarkupLine(Styles.MutedText($"--- {group.Name} ---"));

        // Use a grid for aligned field:value display
        var grid = new Grid();
        grid.AddColumn(new GridColumn().Width(FieldNameWidth).NoWrap());
        grid.AddColumn(new GridColumn());

        foreach (var field in group.Fields)
        {
            var nameCell = Styles.MutedText(TruncateFieldName(field.DisplayName));
            var valueCell = ValueFormatter.Format(field.Value, field.Column);
            grid.AddRow(nameCell, valueCell);
        }

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
    }

    private static string TruncateFieldName(string name)
    {
        if (name.Length <= FieldNameWidth - 2)
        {
            return name;
        }
        return name[..(FieldNameWidth - 5)] + "...";
    }

    private static RecordAction ShowNavigationMenu(RecordNavigationState state)
    {
        var choices = new List<RecordNavigationChoice>();

        // Determine which navigation action should be first (default)
        var preferNext = state.LastAction == NavigationAction.Next ||
                         state.LastAction == NavigationAction.JumpTo ||
                         state.LastAction == NavigationAction.None;

        // Navigation choices - smart ordering based on last action
        var previousChoice = state.CanMovePrevious
            ? new RecordNavigationChoice { Label = "< Previous Record", Action = RecordAction.Previous }
            : null;

        var nextChoice = state.CanMoveNext
            ? new RecordNavigationChoice
            {
                Label = state.NeedsMoreRecords ? "> Next Record (load more)" : "> Next Record",
                Action = RecordAction.Next
            }
            : null;

        // Add navigation choices in smart order (default first)
        if (preferNext)
        {
            if (nextChoice != null) choices.Add(nextChoice);
            if (previousChoice != null) choices.Add(previousChoice);
        }
        else
        {
            if (previousChoice != null) choices.Add(previousChoice);
            if (nextChoice != null) choices.Add(nextChoice);
        }

        if (state.TotalLoaded > 3)
        {
            choices.Add(new RecordNavigationChoice
            {
                Label = "# Jump to Record...",
                Action = RecordAction.JumpTo
            });
        }

        // Browser integration - only show if URL can be constructed
        if (state.CanBuildRecordUrl)
        {
            choices.Add(new RecordNavigationChoice
            {
                Label = "Open in Browser",
                Action = RecordAction.OpenInBrowser
            });

            choices.Add(new RecordNavigationChoice
            {
                Label = "Copy Record URL",
                Action = RecordAction.CopyUrl
            });
        }

        choices.Add(new RecordNavigationChoice
        {
            Label = state.ShowNullValues ? "Hide Empty Fields" : "Show Empty Fields",
            Action = RecordAction.ToggleNulls
        });

        // Only show table view option if we have few enough columns
        if (state.Columns.Count <= QueryResultsViewer.TableViewMaxColumns)
        {
            choices.Add(new RecordNavigationChoice
            {
                Label = "View as Table",
                Action = RecordAction.SwitchToTableView
            });
        }

        choices.Add(new RecordNavigationChoice
        {
            Label = "New Query",
            Action = RecordAction.NewQuery
        });

        choices.Add(new RecordNavigationChoice
        {
            Label = "[Back]",
            Action = RecordAction.Back
        });

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<RecordNavigationChoice>()
                .Title(Styles.MutedText("Navigate:"))
                .HighlightStyle(Styles.SelectionHighlight)
                .AddChoices(choices)
                .UseConverter(FormatChoice));

        return selected.Action;
    }

    private static string FormatChoice(RecordNavigationChoice choice)
    {
        if (choice.Action == RecordAction.Back)
        {
            return Styles.MutedText(choice.Label);
        }
        return choice.Label;
    }

    private static Task ShowJumpToDialog(RecordNavigationState state)
    {
        AnsiConsole.WriteLine();

        var prompt = new TextPrompt<int>(Styles.MutedText($"Enter record number (1-{state.TotalLoaded}):"))
            .DefaultValue(state.CurrentIndex + 1)
            .Validate(n =>
            {
                if (n < 1 || n > state.TotalLoaded)
                {
                    return ValidationResult.Error($"Please enter a number between 1 and {state.TotalLoaded}");
                }
                return ValidationResult.Success();
            });

        try
        {
            var recordNumber = AnsiConsole.Prompt(prompt);
            state.JumpTo(recordNumber - 1); // Convert to 0-based index
        }
        catch (OperationCanceledException)
        {
            // User cancelled, stay on current record
        }

        return Task.CompletedTask;
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

    private sealed class RecordNavigationChoice
    {
        public required string Label { get; init; }
        public required RecordAction Action { get; init; }
    }
}
