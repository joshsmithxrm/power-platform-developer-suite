using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

[Trait("Category", "TuiUnit")]
public class EnvironmentConfigStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EnvironmentConfigStore _store;

    public EnvironmentConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
    }

    [Fact]
    public async Task LoadAsync_NoFile_ReturnsEmptyCollection()
    {
        var collection = await _store.LoadAsync();
        Assert.Empty(collection.Environments);
        Assert.Empty(collection.TypeDefaults);
        Assert.Equal(1, collection.Version);
    }

    [Fact]
    public async Task SaveConfigAsync_NewEnvironment_CreatesEntry()
    {
        var config = await _store.SaveConfigAsync(
            "https://org.crm.dynamics.com",
            label: "Contoso Prod",
            type: "Production",
            color: EnvironmentColor.Red);

        Assert.Equal("Contoso Prod", config.Label);
        Assert.Equal("Production", config.Type);
        Assert.Equal(EnvironmentColor.Red, config.Color);
    }

    [Fact]
    public async Task SaveConfigAsync_ExistingEnvironment_MergesFields()
    {
        await _store.SaveConfigAsync("https://org.crm.dynamics.com", label: "Original");
        var updated = await _store.SaveConfigAsync("https://org.crm.dynamics.com", type: "Sandbox");

        Assert.Equal("Original", updated.Label);
        Assert.Equal("Sandbox", updated.Type);
    }

    [Fact]
    public async Task SaveConfigAsync_NormalizesUrl()
    {
        await _store.SaveConfigAsync("https://ORG.CRM.DYNAMICS.COM", label: "Test");
        var config = await _store.GetConfigAsync("https://org.crm.dynamics.com/");

        Assert.NotNull(config);
        Assert.Equal("Test", config!.Label);
    }

    [Fact]
    public async Task GetConfigAsync_NotFound_ReturnsNull()
    {
        var config = await _store.GetConfigAsync("https://nonexistent.crm.dynamics.com");
        Assert.Null(config);
    }

    [Fact]
    public async Task RemoveConfigAsync_Existing_ReturnsTrue()
    {
        await _store.SaveConfigAsync("https://org.crm.dynamics.com", label: "Test");
        var removed = await _store.RemoveConfigAsync("https://org.crm.dynamics.com");

        Assert.True(removed);

        _store.ClearCache();
        var config = await _store.GetConfigAsync("https://org.crm.dynamics.com");
        Assert.Null(config);
    }

    [Fact]
    public async Task RemoveConfigAsync_NotFound_ReturnsFalse()
    {
        var removed = await _store.RemoveConfigAsync("https://nonexistent.crm.dynamics.com");
        Assert.False(removed);
    }

    [Fact]
    public async Task RoundTrip_PersistsToDisk()
    {
        await _store.SaveConfigAsync("https://org.crm.dynamics.com",
            label: "Prod", type: "Production", color: EnvironmentColor.Red);

        // Create new store pointing to same file to verify persistence
        using var store2 = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
        var config = await store2.GetConfigAsync("https://org.crm.dynamics.com");

        Assert.NotNull(config);
        Assert.Equal("Prod", config!.Label);
        Assert.Equal("Production", config.Type);
        Assert.Equal(EnvironmentColor.Red, config.Color);
    }

    [Fact]
    public async Task TypeDefaults_RoundTrip()
    {
        var collection = await _store.LoadAsync();
        collection.TypeDefaults["UAT"] = EnvironmentColor.Brown;
        collection.TypeDefaults["Gold"] = EnvironmentColor.BrightYellow;
        await _store.SaveAsync(collection);

        _store.ClearCache();
        var reloaded = await _store.LoadAsync();

        Assert.Equal(EnvironmentColor.Brown, reloaded.TypeDefaults["UAT"]);
        Assert.Equal(EnvironmentColor.BrightYellow, reloaded.TypeDefaults["Gold"]);
    }

    [Fact]
    public async Task SaveConfigAsync_EmptyString_ClearsField()
    {
        await _store.SaveConfigAsync("https://org.crm.dynamics.com", label: "MyLabel");
        var result = await _store.SaveConfigAsync("https://org.crm.dynamics.com", label: "");
        Assert.Null(result.Label);
    }

    [Fact]
    public async Task SaveConfigAsync_ClearColor_RemovesExistingColor()
    {
        await _store.SaveConfigAsync("https://org.crm.dynamics.com",
            label: "Test", color: EnvironmentColor.Red);

        var result = await _store.SaveConfigAsync("https://org.crm.dynamics.com",
            clearColor: true);

        Assert.Null(result.Color);
        Assert.Equal("Test", result.Label); // other fields preserved
    }

    [Fact]
    public async Task SaveConfigAsync_NullColorWithoutClearColor_PreservesExistingColor()
    {
        await _store.SaveConfigAsync("https://org.crm.dynamics.com",
            color: EnvironmentColor.Green);

        var result = await _store.SaveConfigAsync("https://org.crm.dynamics.com",
            label: "Updated");

        Assert.Equal(EnvironmentColor.Green, result.Color);
    }

    [Fact]
    public async Task SafetySettings_RoundTrips()
    {
        var config = await _store.SaveConfigAsync(
            "https://test.crm.dynamics.com",
            label: "TEST",
            type: "Sandbox");

        config.SafetySettings = new QuerySafetySettings
        {
            WarnInsertThreshold = 10,
            WarnUpdateThreshold = 0,
            WarnDeleteThreshold = 0,
            PreventUpdateWithoutWhere = true,
            PreventDeleteWithoutWhere = true,
            DmlBatchSize = 200,
            MaxResultRows = 50000,
            QueryTimeoutSeconds = 120,
            UseTdsEndpoint = false,
            BypassCustomPlugins = BypassPluginMode.None,
            BypassPowerAutomateFlows = false
        };

        // Save the collection (config is stored by reference)
        await _store.SaveAsync(await _store.LoadAsync());

        // Reload from disk via fresh store
        using var store2 = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
        var reloaded = await store2.GetConfigAsync("https://test.crm.dynamics.com");

        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.SafetySettings);
        Assert.Equal(10, reloaded.SafetySettings!.WarnInsertThreshold);
        Assert.Equal(200, reloaded.SafetySettings.DmlBatchSize);
        Assert.True(reloaded.SafetySettings.PreventDeleteWithoutWhere);
        Assert.Equal(BypassPluginMode.None, reloaded.SafetySettings.BypassCustomPlugins);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }
}
