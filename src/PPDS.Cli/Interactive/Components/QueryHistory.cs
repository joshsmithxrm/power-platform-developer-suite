using Spectre.Console;

namespace PPDS.Cli.Interactive.Components;

/// <summary>
/// Manages query history for the interactive SQL wizard.
/// Tracks recent queries for easy recall.
/// </summary>
internal static class QueryHistory
{
    private const int MaxHistorySize = 20;
    private static readonly List<string> _history = new(capacity: MaxHistorySize);

    /// <summary>
    /// Gets the recent queries (most recent first).
    /// </summary>
    public static IReadOnlyList<string> Recent => _history;

    /// <summary>
    /// Gets whether there are any queries in history.
    /// </summary>
    public static bool HasHistory => _history.Count > 0;

    /// <summary>
    /// Adds a query to the history.
    /// If the query already exists, it's moved to the front.
    /// </summary>
    /// <param name="query">The query to add.</param>
    public static void Add(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        // Normalize whitespace for comparison
        var normalized = NormalizeQuery(query);

        // Remove existing occurrence (if any) to move it to front
        var existingIndex = _history.FindIndex(q => NormalizeQuery(q) == normalized);
        if (existingIndex >= 0)
        {
            _history.RemoveAt(existingIndex);
        }

        // Add to front
        _history.Insert(0, query.Trim());

        // Trim to max size
        while (_history.Count > MaxHistorySize)
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    /// <summary>
    /// Shows a selector for recent queries.
    /// </summary>
    /// <returns>The selected query, or null if cancelled or no history.</returns>
    public static string? ShowSelector()
    {
        if (!HasHistory)
        {
            AnsiConsole.MarkupLine(Styles.WarningText("No query history yet."));
            AnsiConsole.MarkupLine(Styles.MutedText("Press any key to continue..."));
            Console.ReadKey(true);
            return null;
        }

        var choices = _history
            .Select((q, i) => new HistoryChoice { Index = i, Query = q })
            .ToList();

        // Add cancel option
        choices.Add(new HistoryChoice { Index = -1, Query = "[Cancel]", IsCancel = true });

        try
        {
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<HistoryChoice>()
                    .Title(Styles.MutedText("Select a query from history:"))
                    .PageSize(15)
                    .HighlightStyle(Styles.SelectionHighlight)
                    .AddChoices(choices)
                    .UseConverter(FormatChoice));

            return selected.IsCancel ? null : selected.Query;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Clears all query history.
    /// </summary>
    public static void Clear()
    {
        _history.Clear();
    }

    private static string NormalizeQuery(string query)
    {
        // Normalize for comparison: lowercase, collapse whitespace
        return string.Join(' ', query.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    private static string FormatChoice(HistoryChoice choice)
    {
        if (choice.IsCancel)
        {
            return Styles.MutedText(choice.Query);
        }

        // Truncate long queries for display
        var display = choice.Query;
        if (display.Length > 60)
        {
            display = display[..57] + "...";
        }

        return $"{choice.Index + 1}. {Markup.Escape(display)}";
    }

    private sealed class HistoryChoice
    {
        public required int Index { get; init; }
        public required string Query { get; init; }
        public bool IsCancel { get; init; }
    }
}
