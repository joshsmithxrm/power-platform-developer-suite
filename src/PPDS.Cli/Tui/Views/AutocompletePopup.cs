using System;
using System.Collections.Generic;
using System.Linq;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Sql.Intellisense;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// A floating popup overlay that shows SQL IntelliSense completion suggestions.
/// Added as a subview on top of a text editor — not a Dialog or Window (those steal focus).
/// </summary>
/// <remarks>
/// <para>
/// The popup contains a <see cref="ListView"/> sized to show at most <see cref="MaxVisibleItems"/> items.
/// </para>
/// <para>
/// Supports filter-as-you-type: as the user types, the list filters to items whose labels
/// start with the typed prefix (falling back to contains matching).
/// Accept with Tab or Enter inserts the completion text. Dismiss with Escape.
/// </para>
/// </remarks>
internal sealed class AutocompletePopup : View
{
    /// <summary>
    /// Maximum number of items visible at once in the popup list.
    /// </summary>
    internal const int MaxVisibleItems = 8;

    private readonly ListView _listView;
    private IReadOnlyList<SqlCompletion> _allItems = Array.Empty<SqlCompletion>();
    private List<SqlCompletion> _filteredItems = new();
    private string _filterText = string.Empty;

    /// <summary>
    /// Fired when the user accepts a completion (Tab or Enter).
    /// </summary>
    public event Action<SqlCompletion>? CompletionAccepted;

    /// <summary>
    /// Gets whether the popup is currently showing.
    /// </summary>
    public bool IsShowing { get; private set; }

    /// <summary>
    /// Initializes a new <see cref="AutocompletePopup"/>.
    /// </summary>
    public AutocompletePopup()
    {
        Visible = false;

        // The popup renders its own border via Redraw, but we use a simple
        // list view for the item display.
        _listView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
            ColorScheme = TuiColorPalette.Default
        };

        // Use SelectedChanged to update the color indicator
        _listView.SelectedItemChanged += OnSelectedItemChanged;

        Add(_listView);
    }

    /// <summary>
    /// Shows the popup at the specified position with the given completion items.
    /// </summary>
    /// <param name="items">The completion items to display.</param>
    /// <param name="x">X position relative to the parent view.</param>
    /// <param name="y">Y position relative to the parent view.</param>
    public void Show(IReadOnlyList<SqlCompletion> items, int x, int y)
    {
        if (items == null || items.Count == 0)
        {
            Hide();
            return;
        }

        _allItems = items;
        _filterText = string.Empty;
        _filteredItems = items.ToList();

        UpdateListSource();
        PositionPopup(x, y);

        Visible = true;
        IsShowing = true;
        SetNeedsDisplay();
    }

    /// <summary>
    /// Hides the popup and clears state.
    /// </summary>
    public void Hide()
    {
        if (!IsShowing) return;

        Visible = false;
        IsShowing = false;
        _allItems = Array.Empty<SqlCompletion>();
        _filteredItems.Clear();
        _filterText = string.Empty;
    }

    /// <summary>
    /// Updates the filter text and re-filters the completion list.
    /// </summary>
    /// <param name="filterText">The current typed prefix to filter by.</param>
    public void UpdateFilter(string filterText)
    {
        _filterText = filterText ?? string.Empty;
        ApplyFilter();

        if (_filteredItems.Count == 0)
        {
            Hide();
        }
    }

    /// <summary>
    /// Processes a key event for popup navigation, acceptance, or dismissal.
    /// Returns true if the key was handled by the popup.
    /// </summary>
    /// <param name="keyEvent">The key event to process.</param>
    /// <returns>True if the key was consumed by the popup; false otherwise.</returns>
    public bool ProcessKeyEvent(KeyEvent keyEvent)
    {
        if (!IsShowing) return false;

        switch (keyEvent.Key)
        {
            case Key.Esc:
                Hide();
                return true;

            case Key.Enter:
            case Key.Tab:
                AcceptSelected();
                return true;

            case Key.CursorUp:
                MoveSelection(-1);
                return true;

            case Key.CursorDown:
                MoveSelection(1);
                return true;

            case Key.PageUp:
                MoveSelection(-8); // Page size matches max visible items
                return true;

            case Key.PageDown:
                MoveSelection(8);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Formats a completion item for display in the list: "K SELECT".
    /// </summary>
    internal static string FormatItem(SqlCompletion item)
    {
        return item.Label;
    }

    private void AcceptSelected()
    {
        if (_filteredItems.Count == 0) return;

        var idx = _listView.SelectedItem;
        if (idx >= 0 && idx < _filteredItems.Count)
        {
            var selected = _filteredItems[idx];
            Hide();
            CompletionAccepted?.Invoke(selected);
        }
    }

    private void MoveSelection(int delta)
    {
        if (_filteredItems.Count == 0) return;

        var newIdx = _listView.SelectedItem + delta;
        if (newIdx < 0) newIdx = 0;
        if (newIdx >= _filteredItems.Count) newIdx = _filteredItems.Count - 1;

        _listView.SelectedItem = newIdx;
        _listView.SetNeedsDisplay();
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrEmpty(_filterText))
        {
            _filteredItems = _allItems.ToList();
        }
        else
        {
            // Try starts-with first, fallback to contains
            var startsWithMatches = _allItems
                .Where(c => c.Label.StartsWith(_filterText, StringComparison.OrdinalIgnoreCase))
                .ToList();

            _filteredItems = startsWithMatches.Count > 0
                ? startsWithMatches
                : _allItems
                    .Where(c => c.Label.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }

        UpdateListSource();
    }

    private void UpdateListSource()
    {
        var displayItems = _filteredItems.Select(FormatItem).ToList();
        _listView.SetSource(displayItems);

        if (displayItems.Count > 0)
        {
            _listView.SelectedItem = 0;
        }

        // Resize popup to fit content
        var visibleCount = Math.Min(_filteredItems.Count, MaxVisibleItems);
        var maxWidth = _filteredItems.Count > 0
            ? _filteredItems.Max(c => FormatItem(c).Length) + 2 // +2 for padding
            : 20;
        maxWidth = Math.Max(maxWidth, 15); // minimum width

        Width = maxWidth;
        Height = visibleCount;
        _listView.Height = visibleCount;

        SetNeedsDisplay();
    }

    private void PositionPopup(int x, int y)
    {
        X = x;
        Y = y;
    }

    private void OnSelectedItemChanged(ListViewItemEventArgs args)
    {
        // Re-render to show selection highlighting
        SetNeedsDisplay();
    }

    /// <inheritdoc />
    public override void Redraw(Rect bounds)
    {
        if (Driver == null)
        {
            base.Redraw(bounds);
            return;
        }

        if (_filteredItems.Count == 0)
        {
            base.Redraw(bounds);
            return;
        }

        // Draw items manually with proper coloring
        var normalScheme = TuiColorPalette.Default;
        var selectedScheme = TuiColorPalette.Selected;
        var selectedIdx = _listView.SelectedItem;

        for (int row = 0; row < bounds.Height; row++)
        {
            var itemIdx = row + _listView.TopItem;
            if (itemIdx < _filteredItems.Count)
            {
                var isSelected = itemIdx == selectedIdx;
                var attr = isSelected
                    ? selectedScheme.Normal
                    : normalScheme.Normal;

                Driver.SetAttribute(attr);

                var text = FormatItem(_filteredItems[itemIdx]);
                var col = 0;
                foreach (var ch in text)
                {
                    if (col >= bounds.Width) break;
                    AddRune(col, row, ch);
                    col++;
                }

                // Pad remaining columns
                for (; col < bounds.Width; col++)
                {
                    AddRune(col, row, ' ');
                }
            }
            else
            {
                // Empty row below items
                Driver.SetAttribute(normalScheme.Normal);
                for (int col = 0; col < bounds.Width; col++)
                {
                    AddRune(col, row, ' ');
                }
            }
        }
    }
}
