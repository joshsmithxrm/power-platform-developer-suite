using PPDS.Cli.Services.Settings;
using PPDS.Cli.Services.PluginTraces;
using Xunit;

namespace PPDS.Cli.Tests.Services.Settings;

[Trait("Category", "TuiUnit")]
public class TuiStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly TuiStateStore _store;

    public TuiStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        System.Environment.SetEnvironmentVariable("PPDS_CONFIG_DIR", _tempDir);
        _filePath = Path.Combine(_tempDir, "tui-state.json");
        _store = new TuiStateStore(_filePath);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var state = new WebResourcesScreenState
        {
            SelectedSolutionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TextOnly = false
        };

        await _store.SaveScreenStateAsync("WebResources", "https://contoso.crm.dynamics.com", state);

        var loaded = await _store.LoadScreenStateAsync<WebResourcesScreenState>(
            "WebResources", "https://contoso.crm.dynamics.com");

        Assert.NotNull(loaded);
        Assert.Equal(state.SelectedSolutionId, loaded!.SelectedSolutionId);
        Assert.Equal(state.TextOnly, loaded.TextOnly);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsNull()
    {
        var result = await _store.LoadScreenStateAsync<WebResourcesScreenState>(
            "WebResources", "https://contoso.crm.dynamics.com");

        Assert.Null(result);
    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsNull()
    {
        await File.WriteAllTextAsync(_filePath, "not valid json {{{{");

        var result = await _store.LoadScreenStateAsync<WebResourcesScreenState>(
            "WebResources", "https://contoso.crm.dynamics.com");

        Assert.Null(result);
    }

    [Fact]
    public async Task Save_CreatesFileIfMissing()
    {
        var newPath = Path.Combine(_tempDir, "subdir", "tui-state.json");
        // PPDS_CONFIG_DIR points to _tempDir but we need a custom path in a subdirectory
        // The store uses ProfilePaths.EnsureDirectoryExists() which creates the PPDS_CONFIG_DIR,
        // but the file path's own directory must exist. We set PPDS_CONFIG_DIR to the subdir.
        var subDir = Path.Combine(_tempDir, "subdir");
        System.Environment.SetEnvironmentVariable("PPDS_CONFIG_DIR", subDir);

        using var store = new TuiStateStore(newPath);
        var state = new WebResourcesScreenState { TextOnly = true };

        await store.SaveScreenStateAsync("WebResources", "https://contoso.crm.dynamics.com", state);

        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public async Task StateIsScopedPerEnvironment()
    {
        var stateA = new WebResourcesScreenState
        {
            SelectedSolutionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            TextOnly = true
        };
        var stateB = new WebResourcesScreenState
        {
            SelectedSolutionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            TextOnly = false
        };

        await _store.SaveScreenStateAsync("WebResources", "https://envA.crm.dynamics.com", stateA);
        await _store.SaveScreenStateAsync("WebResources", "https://envB.crm.dynamics.com", stateB);

        var loadedA = await _store.LoadScreenStateAsync<WebResourcesScreenState>(
            "WebResources", "https://envA.crm.dynamics.com");
        var loadedB = await _store.LoadScreenStateAsync<WebResourcesScreenState>(
            "WebResources", "https://envB.crm.dynamics.com");

        Assert.NotNull(loadedA);
        Assert.NotNull(loadedB);
        Assert.Equal(stateA.SelectedSolutionId, loadedA!.SelectedSolutionId);
        Assert.Equal(stateB.SelectedSolutionId, loadedB!.SelectedSolutionId);
    }

    [Fact]
    public async Task StateIsScopedPerScreen()
    {
        var webState = new WebResourcesScreenState
        {
            SelectedSolutionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TextOnly = false
        };
        var connState = new SolutionFilterScreenState
        {
            SolutionFilter = "ContosoCore"
        };

        await _store.SaveScreenStateAsync("WebResources", "https://contoso.crm.dynamics.com", webState);
        await _store.SaveScreenStateAsync("ConnectionReferences", "https://contoso.crm.dynamics.com", connState);

        var loadedWeb = await _store.LoadScreenStateAsync<WebResourcesScreenState>(
            "WebResources", "https://contoso.crm.dynamics.com");
        var loadedConn = await _store.LoadScreenStateAsync<SolutionFilterScreenState>(
            "ConnectionReferences", "https://contoso.crm.dynamics.com");

        Assert.NotNull(loadedWeb);
        Assert.NotNull(loadedConn);
        Assert.Equal(webState.SelectedSolutionId, loadedWeb!.SelectedSolutionId);
        Assert.Equal(connState.SolutionFilter, loadedConn!.SolutionFilter);
    }

    [Fact]
    public async Task EnvironmentUrl_Normalized()
    {
        var state = new WebResourcesScreenState
        {
            SelectedSolutionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TextOnly = true
        };

        await _store.SaveScreenStateAsync(
            "WebResources", "HTTPS://Contoso.CRM.Dynamics.COM", state);

        var loaded = await _store.LoadScreenStateAsync<WebResourcesScreenState>(
            "WebResources", "https://contoso.crm.dynamics.com/");

        Assert.NotNull(loaded);
        Assert.Equal(state.SelectedSolutionId, loaded!.SelectedSolutionId);
    }

    [Fact]
    public async Task Save_PreservesOtherScreenState()
    {
        var webState = new WebResourcesScreenState
        {
            SelectedSolutionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TextOnly = false
        };
        var connState = new SolutionFilterScreenState
        {
            SolutionFilter = "ContosoCore"
        };

        await _store.SaveScreenStateAsync("WebResources", "https://contoso.crm.dynamics.com", webState);
        await _store.SaveScreenStateAsync("ConnectionReferences", "https://contoso.crm.dynamics.com", connState);

        // Load WebResources to verify it's still intact after ConnectionReferences was saved
        var loadedWeb = await _store.LoadScreenStateAsync<WebResourcesScreenState>(
            "WebResources", "https://contoso.crm.dynamics.com");

        Assert.NotNull(loadedWeb);
        Assert.Equal(webState.SelectedSolutionId, loadedWeb!.SelectedSolutionId);
        Assert.Equal(webState.TextOnly, loadedWeb.TextOnly);
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotCorrupt()
    {
        var tasks = Enumerable.Range(0, 10).Select(i =>
        {
            var state = new WebResourcesScreenState
            {
                SelectedSolutionId = Guid.NewGuid(),
                TextOnly = i % 2 == 0
            };
            return _store.SaveScreenStateAsync(
                $"Screen{i}", "https://contoso.crm.dynamics.com", state);
        });

        await Task.WhenAll(tasks);

        // Verify all reads succeed
        for (var i = 0; i < 10; i++)
        {
            var loaded = await _store.LoadScreenStateAsync<WebResourcesScreenState>(
                $"Screen{i}", "https://contoso.crm.dynamics.com");
            Assert.NotNull(loaded);
        }
    }

    [Fact]
    public async Task PluginTraceFilter_RoundTrips()
    {
        var filter = new PluginTraceFilter
        {
            TypeName = "MyPlugin",
            MessageName = "Create",
            Mode = PluginTraceMode.Synchronous,
            OperationType = PluginTraceOperationType.Plugin,
            CreatedAfter = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            CreatedBefore = new DateTime(2026, 3, 20, 23, 59, 59, DateTimeKind.Utc),
            HasException = true,
            MinDurationMs = 500
        };

        await _store.SaveScreenStateAsync(
            "PluginTraces", "https://contoso.crm.dynamics.com", filter);

        var loaded = await _store.LoadScreenStateAsync<PluginTraceFilter>(
            "PluginTraces", "https://contoso.crm.dynamics.com");

        Assert.NotNull(loaded);
        Assert.Equal("MyPlugin", loaded!.TypeName);
        Assert.Equal("Create", loaded.MessageName);
        Assert.Equal(PluginTraceMode.Synchronous, loaded.Mode);
        Assert.Equal(PluginTraceOperationType.Plugin, loaded.OperationType);
        Assert.Equal(filter.CreatedAfter, loaded.CreatedAfter);
        Assert.Equal(filter.CreatedBefore, loaded.CreatedBefore);
        Assert.True(loaded.HasException);
        Assert.Equal(500, loaded.MinDurationMs);
    }

    public void Dispose()
    {
        _store.Dispose();
        System.Environment.SetEnvironmentVariable("PPDS_CONFIG_DIR", null);
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }
}
