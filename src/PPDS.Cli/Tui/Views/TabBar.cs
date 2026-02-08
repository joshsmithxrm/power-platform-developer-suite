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
    private Label? _addButton;
    private bool _isVisible;

    /// <summary>Raised when the [+] button is clicked to request a new tab.</summary>
    public event Action? NewTabClicked;

    public TabBar(TabManager tabManager)
    {
        _tabManager = tabManager ?? throw new ArgumentNullException(nameof(tabManager));

        X = 0;
        Y = 1; // Below menu bar
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
            // Clear existing labels and [+] button
            foreach (var label in _tabLabels)
            {
                Remove(label);
            }
            _tabLabels.Clear();
            if (_addButton != null)
            {
                Remove(_addButton);
                _addButton = null;
            }

            Visible = _isVisible;

            if (_tabManager.TabCount == 0) return;

            var xPos = 0;
            for (int i = 0; i < _tabManager.Tabs.Count; i++)
            {
                var tab = _tabManager.Tabs[i];
                var index = i; // Capture for closure
                var envLabel = GetEnvironmentLabel(tab.EnvironmentType);
                var text = string.IsNullOrEmpty(envLabel)
                    ? $" {i + 1}: {tab.Screen.Title} "
                    : $" {i + 1}: {tab.Screen.Title} [{envLabel}] ";

                var isActive = i == _tabManager.ActiveIndex;
                var label = new Label(text)
                {
                    X = xPos,
                    Y = 0,
                    Width = text.Length,
                    Height = 1,
                    ColorScheme = TuiColorPalette.GetTabScheme(tab.EnvironmentType, isActive)
                };

                label.MouseClick += (_) =>
                {
                    _tabManager.ActivateTab(index);
                };

                _tabLabels.Add(label);
                Add(label);
                xPos += text.Length;
            }

            // Add [+] button (separate from tab labels)
            _addButton = new Label(" [+] ")
            {
                X = xPos,
                Y = 0,
                Width = 5,
                Height = 1,
                ColorScheme = TuiColorPalette.MenuBar
            };
            _addButton.MouseClick += (_) => NewTabClicked?.Invoke();
            Add(_addButton);

            SetNeedsDisplay();
        }
    }

    private void UpdateHighlight()
    {
        if (Application.Driver == null) return;

        for (int i = 0; i < _tabManager.Tabs.Count && i < _tabLabels.Count; i++)
        {
            var isActive = i == _tabManager.ActiveIndex;
            _tabLabels[i].ColorScheme = TuiColorPalette.GetTabScheme(
                _tabManager.Tabs[i].EnvironmentType, isActive);
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

    private static string GetEnvironmentLabel(EnvironmentType envType) => envType switch
    {
        EnvironmentType.Production => "PROD",
        EnvironmentType.Sandbox => "SANDBOX",
        EnvironmentType.Development => "DEV",
        EnvironmentType.Trial => "TRIAL",
        _ => ""
    };

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
