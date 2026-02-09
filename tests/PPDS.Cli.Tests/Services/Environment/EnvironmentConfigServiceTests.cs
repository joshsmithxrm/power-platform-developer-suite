using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Environment;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

[Trait("Category", "TuiUnit")]
public class EnvironmentConfigServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EnvironmentConfigStore _store;
    private readonly EnvironmentConfigService _service;

    public EnvironmentConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
        _service = new EnvironmentConfigService(_store);
    }

    [Fact]
    public async Task ResolveColorAsync_ExplicitColor_WinsOverType()
    {
        await _service.SaveConfigAsync("https://org.crm.dynamics.com",
            type: "Production", color: EnvironmentColor.Blue);
        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.Blue, color);
    }

    [Fact]
    public async Task ResolveColorAsync_TypeDefault_UsedWhenNoExplicitColor()
    {
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", type: "Production");
        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.Red, color);
    }

    [Fact]
    public async Task ResolveColorAsync_CustomType_UsesTypeDefaults()
    {
        await _service.SaveTypeDefaultAsync("Gold", EnvironmentColor.BrightYellow);
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", type: "Gold");
        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.BrightYellow, color);
    }

    [Fact]
    public async Task ResolveColorAsync_NoConfig_FallsBackToUrlKeywords()
    {
        // URL with dev keyword → Development → Green
        var color = await _service.ResolveColorAsync("https://org-dev.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.Green, color);
    }

    [Fact]
    public async Task ResolveColorAsync_NoConfig_PlainUrl_ReturnsGray()
    {
        // Plain CRM URL with no keywords → no type detected → Gray
        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.Gray, color);
    }

    [Fact]
    public async Task ResolveColorAsync_UnknownUrl_ReturnsGray()
    {
        var color = await _service.ResolveColorAsync("https://some-random-url.example.com");
        Assert.Equal(EnvironmentColor.Gray, color);
    }

    [Fact]
    public async Task ResolveTypeAsync_UserConfig_WinsOverDiscovery()
    {
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", type: "UAT");
        var type = await _service.ResolveTypeAsync("https://org.crm.dynamics.com", discoveredType: "Sandbox");
        Assert.Equal("UAT", type);
    }

    [Fact]
    public async Task ResolveTypeAsync_Discovery_WinsOverUrlHeuristics()
    {
        var type = await _service.ResolveTypeAsync("https://org.crm.dynamics.com", discoveredType: "Sandbox");
        Assert.Equal("Sandbox", type);
    }

    [Fact]
    public async Task ResolveTypeAsync_NoConfigNoDiscovery_FallsBackToUrl()
    {
        var type = await _service.ResolveTypeAsync("https://org-dev.crm.dynamics.com");
        Assert.Equal("Development", type);
    }

    [Fact]
    public async Task GetAllTypeDefaultsAsync_MergesBuiltInAndCustom()
    {
        await _service.SaveTypeDefaultAsync("Gold", EnvironmentColor.BrightYellow);
        var defaults = await _service.GetAllTypeDefaultsAsync();
        Assert.True(defaults.TryGetValue("Production", out var productionColor), "Should have built-in Production");
        Assert.True(defaults.TryGetValue("Gold", out var goldColor), "Should have custom Gold");
        Assert.Equal(EnvironmentColor.Red, productionColor);
        Assert.Equal(EnvironmentColor.BrightYellow, goldColor);
    }

    [Fact]
    public async Task GetAllTypeDefaultsAsync_CustomOverridesBuiltIn()
    {
        await _service.SaveTypeDefaultAsync("Production", EnvironmentColor.BrightRed);
        var defaults = await _service.GetAllTypeDefaultsAsync();
        Assert.Equal(EnvironmentColor.BrightRed, defaults["Production"]);
    }

    [Fact]
    public async Task ResolveLabelAsync_ReturnsConfiguredLabel()
    {
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", label: "Contoso Prod");
        var label = await _service.ResolveLabelAsync("https://org.crm.dynamics.com");
        Assert.Equal("Contoso Prod", label);
    }

    [Fact]
    public async Task ResolveLabelAsync_NoConfig_ReturnsNull()
    {
        var label = await _service.ResolveLabelAsync("https://org.crm.dynamics.com");
        Assert.Null(label);
    }

    [Fact]
    public void DetectTypeFromUrl_PlainCrmUrl_ReturnsNull()
    {
        // CRM regional suffix tells us nothing about environment type
        Assert.Null(EnvironmentConfigService.DetectTypeFromUrl("https://org.crm.dynamics.com"));
    }

    [Fact]
    public void DetectTypeFromUrl_RegionalCrmUrl_ReturnsNull()
    {
        // crm9 is UK region, not a sandbox indicator
        Assert.Null(EnvironmentConfigService.DetectTypeFromUrl("https://org.crm9.dynamics.com"));
    }

    [Fact]
    public void DetectTypeFromUrl_DevKeyword()
    {
        Assert.Equal("Development", EnvironmentConfigService.DetectTypeFromUrl("https://org-dev.crm.dynamics.com"));
    }

    [Fact]
    public void DetectTypeFromUrl_UnknownUrl()
    {
        Assert.Null(EnvironmentConfigService.DetectTypeFromUrl("https://some-random.example.com"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }
}
