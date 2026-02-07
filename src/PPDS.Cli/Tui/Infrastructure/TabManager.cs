using PPDS.Cli.Tui.Screens;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Manages tab lifecycle: add, close, switch, and cycle through tabs.
/// Each tab owns a screen instance bound to a specific environment.
/// </summary>
internal sealed class TabManager : ITuiStateCapture<TabManagerState>, IDisposable
{
    private readonly List<TabInfo> _tabs = new();
    private readonly ITuiThemeService _themeService;
    private int _activeIndex = -1;

    /// <summary>Raised when a tab is added or removed.</summary>
    public event Action? TabsChanged;

    /// <summary>Raised when the active tab changes.</summary>
    public event Action? ActiveTabChanged;

    public int TabCount => _tabs.Count;
    public int ActiveIndex => _activeIndex;
    public TabInfo? ActiveTab => _activeIndex >= 0 && _activeIndex < _tabs.Count
        ? _tabs[_activeIndex]
        : null;

    public IReadOnlyList<TabInfo> Tabs => _tabs.AsReadOnly();

    public TabManager(ITuiThemeService themeService)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
    }

    /// <summary>
    /// Adds a new tab and makes it active.
    /// </summary>
    public void AddTab(ITuiScreen screen, string? environmentUrl, string? environmentDisplayName = null)
    {
        var envType = _themeService.DetectEnvironmentType(environmentUrl);
        var tab = new TabInfo(screen, environmentUrl, environmentDisplayName, envType);
        _tabs.Add(tab);
        _activeIndex = _tabs.Count - 1;

        TabsChanged?.Invoke();
        ActiveTabChanged?.Invoke();
    }

    /// <summary>
    /// Closes the tab at the given index and disposes its screen.
    /// </summary>
    public void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        var tab = _tabs[index];
        _tabs.RemoveAt(index);
        tab.Screen.Dispose();

        if (_tabs.Count == 0)
        {
            _activeIndex = -1;
        }
        else if (_activeIndex >= _tabs.Count)
        {
            _activeIndex = _tabs.Count - 1;
        }
        else if (_activeIndex > index)
        {
            _activeIndex--;
        }

        TabsChanged?.Invoke();
        ActiveTabChanged?.Invoke();
    }

    /// <summary>
    /// Activates the tab at the given index.
    /// </summary>
    public void ActivateTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        if (index == _activeIndex) return;

        _activeIndex = index;
        ActiveTabChanged?.Invoke();
    }

    /// <summary>Cycles to the next tab (wraps around).</summary>
    public void ActivateNext()
    {
        if (_tabs.Count <= 1) return;
        _activeIndex = (_activeIndex + 1) % _tabs.Count;
        ActiveTabChanged?.Invoke();
    }

    /// <summary>Cycles to the previous tab (wraps around).</summary>
    public void ActivatePrevious()
    {
        if (_tabs.Count <= 1) return;
        _activeIndex = (_activeIndex - 1 + _tabs.Count) % _tabs.Count;
        ActiveTabChanged?.Invoke();
    }

    /// <inheritdoc />
    public TabManagerState CaptureState()
    {
        var tabs = _tabs.Select((t, i) => new TabSummary(
            ScreenType: t.Screen.GetType().Name,
            Title: t.Screen.Title,
            EnvironmentUrl: t.EnvironmentUrl,
            EnvironmentType: t.EnvironmentType,
            IsActive: i == _activeIndex
        )).ToList();

        return new TabManagerState(
            TabCount: _tabs.Count,
            ActiveIndex: _activeIndex,
            Tabs: tabs);
    }

    public void Dispose()
    {
        foreach (var tab in _tabs)
        {
            try { tab.Screen.Dispose(); }
            catch { /* continue */ }
        }
        _tabs.Clear();
        _activeIndex = -1;
    }
}

/// <summary>
/// Information about a single tab.
/// </summary>
internal sealed record TabInfo(
    ITuiScreen Screen,
    string? EnvironmentUrl,
    string? EnvironmentDisplayName,
    EnvironmentType EnvironmentType);
