using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Screens;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

[Trait("Category", "TuiUnit")]
public sealed class TuiScreenBaseTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly InteractiveSession _session;

    public TuiScreenBaseTests()
    {
        _tempStore = new TempProfileStore();
        _session = new InteractiveSession(null, _tempStore.Store, new MockServiceProviderFactory());
    }

    public void Dispose()
    {
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tempStore.Dispose();
    }

    [Fact]
    public void Constructor_SetsSessionAndErrorService()
    {
        using var screen = new StubScreen(_session);
        Assert.NotNull(screen.Content);
        Assert.Equal("Stub", screen.Title);
    }

    [Fact]
    public void Constructor_BindsEnvironmentUrl()
    {
        using var screen = new StubScreen(_session, "https://dev.crm.dynamics.com");
        Assert.Equal("https://dev.crm.dynamics.com", screen.EnvironmentUrl);
    }

    [Fact]
    public void Dispose_CancelsCancellationToken()
    {
        using var screen = new StubScreen(_session);
        var token = screen.ExposedCancellationToken;
        Assert.False(token.IsCancellationRequested);

        screen.Dispose();

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void OnActivated_RegistersHotkeys()
    {
        using var screen = new StubScreen(_session);
        var registry = new HotkeyRegistry();

        screen.OnActivated(registry);

        var bindings = registry.GetAllBindings();
        Assert.Contains(bindings, b => b.Description == "Test hotkey");
    }

    [Fact]
    public void OnDeactivating_ClearsHotkeys()
    {
        using var screen = new StubScreen(_session);
        var registry = new HotkeyRegistry();

        screen.OnActivated(registry);
        Assert.NotEmpty(registry.GetAllBindings());

        screen.OnDeactivating();
        Assert.Empty(registry.GetAllBindings());
    }

    [Fact]
    public void Dispose_CallsOnDeactivating()
    {
        using var screen = new StubScreen(_session);
        var registry = new HotkeyRegistry();
        screen.OnActivated(registry);

        screen.Dispose();

        Assert.Empty(registry.GetAllBindings());
    }

    [Fact]
    public void Dispose_CallsOnDispose()
    {
        var screen = new StubScreen(_session);
        screen.Dispose();
        Assert.True(screen.OnDisposeWasCalled);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var screen = new StubScreen(_session);
        screen.Dispose();
        var exception = Record.Exception(() => screen.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void RequestClose_RaisesCloseRequestedEvent()
    {
        using var screen = new StubScreen(_session);
        var raised = false;
        screen.CloseRequested += () => raised = true;

        screen.InvokeRequestClose();

        Assert.True(raised);
    }

    [Fact]
    public void NotifyMenuChanged_RaisesMenuStateChangedEvent()
    {
        using var screen = new StubScreen(_session);
        var raised = false;
        screen.MenuStateChanged += () => raised = true;

        screen.InvokeNotifyMenuChanged();

        Assert.True(raised);
    }

    /// <summary>
    /// Concrete test implementation of TuiScreenBase.
    /// </summary>
    private sealed class StubScreen : TuiScreenBase
    {
        public override string Title => "Stub";
        public bool OnDisposeWasCalled { get; private set; }
        public CancellationToken ExposedCancellationToken => ScreenCancellation;

        public StubScreen(InteractiveSession session, string? environmentUrl = null)
            : base(session, environmentUrl)
        {
        }

        protected override void RegisterHotkeys(IHotkeyRegistry registry)
        {
            RegisterHotkey(registry, Key.F5, "Test hotkey", () => { });
        }

        protected override void OnDispose()
        {
            OnDisposeWasCalled = true;
        }

        public void InvokeRequestClose() => RequestClose();
        public void InvokeNotifyMenuChanged() => NotifyMenuChanged();
    }
}
