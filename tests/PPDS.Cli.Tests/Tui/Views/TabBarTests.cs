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
        _tabBar = new TabBar(_tabManager);
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

    private sealed class StubScreen : TuiScreenBase
    {
        private readonly string _title;
        public override string Title => _title;

        public StubScreen(InteractiveSession session, string title = "Stub")
            : base(session) { _title = title; }

        protected override void RegisterHotkeys(IHotkeyRegistry registry) { }
    }
}
