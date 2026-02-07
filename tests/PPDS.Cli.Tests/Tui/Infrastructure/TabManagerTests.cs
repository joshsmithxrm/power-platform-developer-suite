using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

[Trait("Category", "TuiUnit")]
public sealed class TabManagerTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly InteractiveSession _session;
    private readonly TabManager _manager;

    public TabManagerTests()
    {
        _tempStore = new TempProfileStore();
        _session = new InteractiveSession(null, _tempStore.Store, new MockServiceProviderFactory());
        _manager = new TabManager(new TuiThemeService());
    }

    public void Dispose()
    {
        _manager.Dispose();
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tempStore.Dispose();
    }

    [Fact]
    public void Initial_NoTabs()
    {
        Assert.Equal(0, _manager.TabCount);
        Assert.Null(_manager.ActiveTab);
        Assert.Equal(-1, _manager.ActiveIndex);
    }

    [Fact]
    public void AddTab_SetsAsActive()
    {
        var screen = new StubScreen(_session);
        _manager.AddTab(screen, "https://dev.crm.dynamics.com", "DEV");

        Assert.Equal(1, _manager.TabCount);
        Assert.Equal(0, _manager.ActiveIndex);
        Assert.Same(screen, _manager.ActiveTab?.Screen);
    }

    [Fact]
    public void AddTab_MultipleTabs_LastIsActive()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");

        Assert.Equal(2, _manager.TabCount);
        Assert.Equal(1, _manager.ActiveIndex);
    }

    [Fact]
    public void ActivateTab_SwitchesActive()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");

        _manager.ActivateTab(0);

        Assert.Equal(0, _manager.ActiveIndex);
    }

    [Fact]
    public void CloseTab_RemovesAndActivatesAdjacent()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");
        _manager.AddTab(new StubScreen(_session), "https://qa.crm4.dynamics.com", "QA");

        _manager.ActivateTab(1); // PROD active
        _manager.CloseTab(1);    // Close PROD

        Assert.Equal(2, _manager.TabCount);
        // Should activate the tab that took index 1's place (QA)
        Assert.Equal(1, _manager.ActiveIndex);
    }

    [Fact]
    public void CloseTab_LastTab_ActivatesPrevious()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");

        _manager.ActivateTab(1); // PROD active
        _manager.CloseTab(1);    // Close last tab

        Assert.Equal(1, _manager.TabCount);
        Assert.Equal(0, _manager.ActiveIndex); // Falls back to DEV
    }

    [Fact]
    public void CloseTab_OnlyTab_NoActive()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.CloseTab(0);

        Assert.Equal(0, _manager.TabCount);
        Assert.Equal(-1, _manager.ActiveIndex);
        Assert.Null(_manager.ActiveTab);
    }

    [Fact]
    public void CloseTab_DisposesScreen()
    {
        var screen = new StubScreen(_session);
        _manager.AddTab(screen, "https://dev.crm.dynamics.com", "DEV");

        _manager.CloseTab(0);

        Assert.True(screen.IsDisposed);
    }

    [Fact]
    public void ActivateNext_Cycles()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");
        _manager.AddTab(new StubScreen(_session), "https://qa.crm4.dynamics.com", "QA");
        _manager.ActivateTab(0);

        _manager.ActivateNext();
        Assert.Equal(1, _manager.ActiveIndex);

        _manager.ActivateNext();
        Assert.Equal(2, _manager.ActiveIndex);

        _manager.ActivateNext(); // Wraps
        Assert.Equal(0, _manager.ActiveIndex);
    }

    [Fact]
    public void ActivatePrevious_Cycles()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");
        _manager.ActivateTab(0);

        _manager.ActivatePrevious(); // Wraps
        Assert.Equal(1, _manager.ActiveIndex);
    }

    [Fact]
    public void TabsChanged_FiresOnAddAndClose()
    {
        var count = 0;
        _manager.TabsChanged += () => count++;

        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.CloseTab(0);

        Assert.Equal(2, count);
    }

    [Fact]
    public void ActiveTabChanged_FiresOnSwitch()
    {
        var count = 0;
        _manager.ActiveTabChanged += () => count++;

        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");
        _manager.ActivateTab(0);

        Assert.True(count >= 3); // Add activates + explicit switch
    }

    [Fact]
    public void CaptureState_ReflectsTabs()
    {
        _manager.AddTab(new StubScreen(_session), "https://dev.crm.dynamics.com", "DEV");
        _manager.AddTab(new StubScreen(_session), "https://prod.crm.dynamics.com", "PROD");

        var state = _manager.CaptureState();

        Assert.Equal(2, state.TabCount);
        Assert.Equal(1, state.ActiveIndex);
        Assert.Equal(2, state.Tabs.Count);
        Assert.Equal("https://dev.crm.dynamics.com", state.Tabs[0].EnvironmentUrl);
        Assert.Equal("https://prod.crm.dynamics.com", state.Tabs[1].EnvironmentUrl);
        Assert.False(state.Tabs[0].IsActive);
        Assert.True(state.Tabs[1].IsActive);
    }

    private sealed class StubScreen : TuiScreenBase
    {
        public override string Title => "Stub";
        public bool IsDisposed { get; private set; }

        public StubScreen(InteractiveSession session)
            : base(session) { }

        protected override void RegisterHotkeys(IHotkeyRegistry registry) { }

        protected override void OnDispose()
        {
            IsDisposed = true;
        }
    }
}
