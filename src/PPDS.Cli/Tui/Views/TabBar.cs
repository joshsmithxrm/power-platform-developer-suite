using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// Horizontal tab bar View for switching between open tabs.
/// Renders environment-colored tab labels with active tab highlight.
/// </summary>
internal sealed class TabBar : View, ITuiStateCapture<TabBarState>
{
    private readonly TabManager _tabManager;
    private readonly List<Label> _tabLabels = new();
    private bool _isVisible;

    public TabBar(TabManager tabManager)
    {
        _tabManager = tabManager ?? throw new ArgumentNullException(nameof(tabManager));

        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = 1;
        ColorScheme = TuiColorPalette.MenuBar;

        _tabManager.TabsChanged += Rebuild;
        _tabManager.ActiveTabChanged += UpdateHighlight;
    }

    private void Rebuild()
    {
        _isVisible = _tabManager.TabCount > 0;

        if (Application.Driver != null)
        {
            // Clear existing labels
            foreach (var label in _tabLabels)
            {
                Remove(label);
            }
            _tabLabels.Clear();

            Visible = _isVisible;

            if (_tabManager.TabCount == 0) return;

            var xPos = 0;
            for (int i = 0; i < _tabManager.Tabs.Count; i++)
            {
                var tab = _tabManager.Tabs[i];
                var index = i; // Capture for closure
                var text = $" {i + 1}: {tab.Screen.Title} ";

                var label = new Label(text)
                {
                    X = xPos,
                    Y = 0,
                    Width = text.Length,
                    Height = 1,
                    ColorScheme = i == _tabManager.ActiveIndex
                        ? TuiColorPalette.TabActive
                        : TuiColorPalette.TabInactive
                };

                label.MouseClick += (_) =>
                {
                    _tabManager.ActivateTab(index);
                };

                _tabLabels.Add(label);
                Add(label);
                xPos += text.Length;
            }

            // Add [+] button
            var addLabel = new Label(" [+] ")
            {
                X = xPos,
                Y = 0,
                Width = 5,
                Height = 1,
                ColorScheme = TuiColorPalette.MenuBar
            };
            _tabLabels.Add(addLabel);
            Add(addLabel);

            SetNeedsDisplay();
        }
    }

    private void UpdateHighlight()
    {
        if (Application.Driver == null) return;

        for (int i = 0; i < _tabManager.Tabs.Count && i < _tabLabels.Count; i++)
        {
            _tabLabels[i].ColorScheme = i == _tabManager.ActiveIndex
                ? TuiColorPalette.TabActive
                : TuiColorPalette.TabInactive;
        }
        SetNeedsDisplay();
    }

    /// <inheritdoc />
    public TabBarState CaptureState()
    {
        var labels = _tabManager.Tabs
            .Select(t => t.Screen.Title)
            .ToList();

        return new TabBarState(
            TabCount: _tabManager.TabCount,
            ActiveIndex: _tabManager.ActiveIndex,
            TabLabels: labels,
            IsVisible: _isVisible);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tabManager.TabsChanged -= Rebuild;
            _tabManager.ActiveTabChanged -= UpdateHighlight;
        }
        base.Dispose(disposing);
    }
}
