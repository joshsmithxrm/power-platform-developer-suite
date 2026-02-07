using PPDS.Auth.Profiles;
using PPDS.Cli.Tests.Mocks;
using PPDS.Cli.Tui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

[Trait("Category", "TuiUnit")]
public sealed class ServiceCachingTests : IDisposable
{
    private readonly TempProfileStore _tempStore;
    private readonly InteractiveSession _session;

    public ServiceCachingTests()
    {
        _tempStore = new TempProfileStore();
        _session = new InteractiveSession(
            null,
            _tempStore.Store,
            new MockServiceProviderFactory());
    }

    public void Dispose()
    {
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tempStore.Dispose();
    }

    [Fact]
    public void GetProfileService_ReturnsSameInstance()
    {
        var first = _session.GetProfileService();
        var second = _session.GetProfileService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetEnvironmentService_ReturnsSameInstance()
    {
        var first = _session.GetEnvironmentService();
        var second = _session.GetEnvironmentService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetThemeService_ReturnsSameInstance()
    {
        var first = _session.GetThemeService();
        var second = _session.GetThemeService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetQueryHistoryService_ReturnsSameInstance()
    {
        var first = _session.GetQueryHistoryService();
        var second = _session.GetQueryHistoryService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetExportService_ReturnsSameInstance()
    {
        var first = _session.GetExportService();
        var second = _session.GetExportService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetErrorService_ReturnsSameInstance()
    {
        // Already cached — verify it still works
        var first = _session.GetErrorService();
        var second = _session.GetErrorService();
        Assert.Same(first, second);
    }

    [Fact]
    public void GetHotkeyRegistry_ReturnsSameInstance()
    {
        // Already cached — verify it still works
        var first = _session.GetHotkeyRegistry();
        var second = _session.GetHotkeyRegistry();
        Assert.Same(first, second);
    }
}
