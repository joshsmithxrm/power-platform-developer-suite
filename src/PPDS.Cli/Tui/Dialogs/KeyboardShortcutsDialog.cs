using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog displaying keyboard shortcuts help.
/// Reads live bindings from the <see cref="IHotkeyRegistry"/> so the display
/// stays in sync with actually registered hotkeys.
/// </summary>
internal sealed class KeyboardShortcutsDialog : TuiDialog, ITuiStateCapture<KeyboardShortcutsDialogState>
{
    private static readonly IReadOnlyList<ShortcutEntry> BuiltInShortcuts = new List<ShortcutEntry>
    {
        new("Arrows", "Navigate cells", "Table Navigation"),
        new("PgUp/Dn", "Page up/down", "Table Navigation"),
        new("Home/End", "First/last row", "Table Navigation"),
        new("Ctrl+C", "Copy (multi-select includes headers)", "Table Navigation"),
        new("Ctrl+Shift+C", "Copy (inverted header behavior)", "Table Navigation"),
    };

    private readonly IReadOnlyList<ShortcutEntry> _allShortcuts;

    /// <summary>
    /// Creates a new keyboard shortcuts dialog that reads bindings from the hotkey registry.
    /// </summary>
    /// <param name="registry">The hotkey registry to read bindings from.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public KeyboardShortcutsDialog(IHotkeyRegistry? registry = null, InteractiveSession? session = null)
        : base("Keyboard Shortcuts", session)
    {
        _allShortcuts = BuildShortcutList(registry);

        var text = FormatShortcutsText(_allShortcuts);
        var lineCount = text.Split('\n').Length;

        Width = 55;
        Height = Math.Min(lineCount + 6, 30); // +6 for border, padding, button

        var content = new Label(text)
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 2
        };

        var closeButton = new Button("_OK")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        Add(content, closeButton);
    }

    private static IReadOnlyList<ShortcutEntry> BuildShortcutList(IHotkeyRegistry? registry)
    {
        var entries = new List<ShortcutEntry>();

        if (registry != null)
        {
            foreach (var binding in registry.GetAllBindings())
            {
                var scope = binding.Scope switch
                {
                    HotkeyScope.Global => "Global",
                    HotkeyScope.Screen => GetScreenScopeName(binding.Owner),
                    HotkeyScope.Dialog => "Dialog",
                    _ => "Other"
                };
                entries.Add(new ShortcutEntry(
                    HotkeyRegistry.FormatKey(binding.Key),
                    binding.Description,
                    scope));
            }
        }

        entries.AddRange(BuiltInShortcuts);
        return entries.AsReadOnly();
    }

    private static string GetScreenScopeName(object? owner) => owner?.GetType().Name switch
    {
        "SqlQueryScreen" => "SQL Query Screen",
        _ => "Screen"
    };

    private static string FormatShortcutsText(IReadOnlyList<ShortcutEntry> shortcuts)
    {
        var grouped = shortcuts
            .GroupBy(s => s.Scope)
            .OrderBy(g => GetScopeOrder(g.Key));

        var sections = new List<string>();
        foreach (var group in grouped)
        {
            var header = group.Key switch
            {
                "Global" => "Global Shortcuts (work everywhere):",
                _ => $"{group.Key}:"
            };

            var maxKeyLen = group.Max(s => s.Key.Length);
            var lines = group.Select(s => $"  {s.Key.PadRight(maxKeyLen)} - {s.Description}");
            sections.Add(header + "\n" + string.Join("\n", lines));
        }

        return string.Join("\n\n", sections);
    }

    private static int GetScopeOrder(string scope) => scope switch
    {
        "Global" => 0,
        "Tab Management" => 1,
        "Table Navigation" => 99,
        _ => 10 // Screen-specific sections in the middle
    };

    /// <inheritdoc />
    public KeyboardShortcutsDialogState CaptureState() => new(
        Title: Title?.ToString() ?? string.Empty,
        Shortcuts: _allShortcuts,
        ShortcutCount: _allShortcuts.Count);
}
