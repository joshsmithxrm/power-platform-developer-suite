using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using PPDS.Cli.Tui.Views;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Views;

[Trait("Category", "TuiUnit")]
public sealed class TabBarTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly InteractiveSession _session;
    private readonly TabManager _tabManager;
    private readonly TabBar _tabBar;

    public TabBarTests()
    {
        _tempStore = new TempProfileStore();
        _session = new InteractiveSession(null, _tempStore.Store, new MockServiceProviderFactory());
        _tabManager = new TabManager(new TuiThemeService());
        _tabBar = new TabBar(_tabManager, new TuiThemeService());
    }

    public void Dispose()
    {
        _tabBar.Dispose();
        _tabManager.Dispose();
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tempStore.Dispose();
    }

    [Fact]
    public void NoTabs_IsNotVisible()
    {
        var state = _tabBar.CaptureState();
        Assert.Equal(0, state.TabCount);
        Assert.False(state.IsVisible);
    }

    [Fact]
    public void WithTabs_IsVisible()
    {
        _tabManager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        var state = _tabBar.CaptureState();

        Assert.Equal(1, state.TabCount);
        Assert.True(state.IsVisible);
    }

    [Fact]
    public void TabLabels_ReflectScreenTitles()
    {
        _tabManager.AddTab(new StubScreen(_session, "SQL DEV"), "https://dev.crm.dynamics.com", "DEV");
        _tabManager.AddTab(new StubScreen(_session, "SQL PROD"), "https://prod.crm.dynamics.com", "PROD");

        var state = _tabBar.CaptureState();

        Assert.Equal(2, state.TabLabels.Count);
        Assert.Contains("SQL DEV", state.TabLabels[0]);
        Assert.Contains("SQL PROD", state.TabLabels[1]);
    }

    [Fact]
    public void ActiveIndex_MatchesTabManager()
    {
        _tabManager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _tabManager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");
        _tabManager.ActivateTab(0);

        var state = _tabBar.CaptureState();
        Assert.Equal(0, state.ActiveIndex);
    }

    [Fact]
    public void CaptureState_ReflectsEnvironmentAwareTitles()
    {
        // Arrange - set up session with environment display name
        _session.UpdateDisplayedEnvironment("https://dev.crm.dynamics.com", "Dev Env");
        _tabManager.AddTab(
            new StubScreen(_session, "SQL Query - Dev Env"),
            "https://dev.crm.dynamics.com", "Dev Env");

        // Act
        var state = _tabBar.CaptureState();

        // Assert - state captures the environment-aware title
        Assert.Single(state.TabLabels);
        Assert.Contains("Dev Env", state.TabLabels[0]);
    }

    [Fact]
    public void TabManager_TracksEnvironmentType_ForBadgeRendering()
    {
        // Arrange - add tabs with different environment types
        // TabManager.AddTab uses TuiThemeService to detect type from URL keywords
        _tabManager.AddTab(
            new StubScreen(_session, "SQL DEV"),
            "https://contoso-dev.crm.dynamics.com", "DEV");
        _tabManager.AddTab(
            new StubScreen(_session, "SQL PROD"),
            "https://contoso.crm.dynamics.com", "PROD");

        // Act
        var state = _tabManager.CaptureState();

        // Assert - dev keyword detected; plain URL is Unknown (region != type)
        Assert.Equal(EnvironmentType.Development, state.Tabs[0].EnvironmentType);
        Assert.Equal(EnvironmentType.Unknown, state.Tabs[1].EnvironmentType);
    }

    [Fact]
    public void TabManager_UnknownEnvironment_DetectedAsUnknown()
    {
        // Arrange - add tab with unrecognizable URL
        _tabManager.AddTab(
            new StubScreen(_session, "SQL Custom"),
            "https://custom.example.com", "Custom");

        // Act
        var state = _tabManager.CaptureState();

        // Assert - unknown type means no badge will be rendered
        Assert.Equal(EnvironmentType.Unknown, state.Tabs[0].EnvironmentType);
    }

    [Fact]
    public void TabManager_NullEnvironmentUrl_DetectedAsUnknown()
    {
        // Arrange
        _tabManager.AddTab(new StubScreen(_session), null, null);

        // Act
        var state = _tabManager.CaptureState();

        // Assert
        Assert.Equal(EnvironmentType.Unknown, state.Tabs[0].EnvironmentType);
    }

    private sealed class StubScreen : TuiScreenBase
    {
        private readonly string _title;
        public override string Title => _title;

        public StubScreen(InteractiveSession session, string title = "Stub")
            : base(session) { _title = title; }

        protected override void RegisterHotkeys(IHotkeyRegistry registry) { }
    }
}
