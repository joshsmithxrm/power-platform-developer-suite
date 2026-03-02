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
            type: EnvironmentType.Production, color: EnvironmentColor.Blue);
        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.Blue, color);
    }

    [Fact]
    public async Task ResolveColorAsync_TypeDefault_UsedWhenNoExplicitColor()
    {
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", type: EnvironmentType.Production);
        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.Red, color);
    }

    [Fact]
    public async Task ResolveColorAsync_CustomTypeOverride_UsesTypeDefaults()
    {
        await _service.SaveTypeDefaultAsync(EnvironmentType.Production, EnvironmentColor.BrightYellow);
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", type: EnvironmentType.Production);
        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.BrightYellow, color);
    }

    [Fact]
    public async Task ResolveColorAsync_NoConfig_FallsBackToUrlKeywords()
    {
        // URL with dev keyword -> Development -> Green
        var color = await _service.ResolveColorAsync("https://org-dev.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.Green, color);
    }

    [Fact]
    public async Task ResolveColorAsync_NoConfig_PlainUrl_ReturnsGray()
    {
        // Plain CRM URL with no keywords -> no type detected -> Gray
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
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", type: EnvironmentType.Test);
        var type = await _service.ResolveTypeAsync("https://org.crm.dynamics.com", discoveredType: "Sandbox");
        Assert.Equal(EnvironmentType.Test, type);
    }

    [Fact]
    public async Task ResolveTypeAsync_Discovery_WinsOverUrlHeuristics()
    {
        var type = await _service.ResolveTypeAsync("https://org.crm.dynamics.com", discoveredType: "Sandbox");
        Assert.Equal(EnvironmentType.Sandbox, type);
    }

    [Fact]
    public async Task ResolveTypeAsync_NoConfigNoDiscovery_FallsBackToUrl()
    {
        var type = await _service.ResolveTypeAsync("https://org-dev.crm.dynamics.com");
        Assert.Equal(EnvironmentType.Development, type);
    }

    [Fact]
    public async Task ResolveTypeAsync_NoConfigNoDiscoveryNoMatch_ReturnsUnknown()
    {
        var type = await _service.ResolveTypeAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentType.Unknown, type);
    }

    [Fact]
    public async Task GetAllTypeDefaultsAsync_MergesBuiltInAndOverrides()
    {
        await _service.SaveTypeDefaultAsync(EnvironmentType.Sandbox, EnvironmentColor.BrightYellow);
        var defaults = await _service.GetAllTypeDefaultsAsync();
        Assert.True(defaults.TryGetValue(EnvironmentType.Production, out var productionColor), "Should have built-in Production");
        Assert.True(defaults.TryGetValue(EnvironmentType.Sandbox, out var sandboxColor), "Should have Sandbox (overridden)");
        Assert.Equal(EnvironmentColor.Red, productionColor);
        Assert.Equal(EnvironmentColor.BrightYellow, sandboxColor);
    }

    [Fact]
    public async Task GetAllTypeDefaultsAsync_CustomOverridesBuiltIn()
    {
        await _service.SaveTypeDefaultAsync(EnvironmentType.Production, EnvironmentColor.BrightRed);
        var defaults = await _service.GetAllTypeDefaultsAsync();
        Assert.Equal(EnvironmentColor.BrightRed, defaults[EnvironmentType.Production]);
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
    public void DetectTypeFromUrl_PlainCrmUrl_ReturnsUnknown()
    {
        // CRM regional suffix tells us nothing about environment type
        Assert.Equal(EnvironmentType.Unknown, EnvironmentConfigService.DetectTypeFromUrl("https://org.crm.dynamics.com"));
    }

    [Fact]
    public void DetectTypeFromUrl_RegionalCrmUrl_ReturnsUnknown()
    {
        // crm9 is UK region, not a sandbox indicator
        Assert.Equal(EnvironmentType.Unknown, EnvironmentConfigService.DetectTypeFromUrl("https://org.crm9.dynamics.com"));
    }

    [Fact]
    public void DetectTypeFromUrl_DevKeyword()
    {
        Assert.Equal(EnvironmentType.Development, EnvironmentConfigService.DetectTypeFromUrl("https://org-dev.crm.dynamics.com"));
    }

    [Fact]
    public void DetectTypeFromUrl_UnknownUrl()
    {
        Assert.Equal(EnvironmentType.Unknown, EnvironmentConfigService.DetectTypeFromUrl("https://some-random.example.com"));
    }

    [Fact]
    public void ParseDiscoveryType_MapsKnownTypes()
    {
        Assert.Equal(EnvironmentType.Production, EnvironmentConfigService.ParseDiscoveryType("Production"));
        Assert.Equal(EnvironmentType.Sandbox, EnvironmentConfigService.ParseDiscoveryType("Sandbox"));
        Assert.Equal(EnvironmentType.Development, EnvironmentConfigService.ParseDiscoveryType("Developer"));
        Assert.Equal(EnvironmentType.Development, EnvironmentConfigService.ParseDiscoveryType("Development"));
        Assert.Equal(EnvironmentType.Trial, EnvironmentConfigService.ParseDiscoveryType("Trial"));
        Assert.Equal(EnvironmentType.Test, EnvironmentConfigService.ParseDiscoveryType("Test"));
    }

    [Fact]
    public void ParseDiscoveryType_UnknownType_ReturnsUnknown()
    {
        Assert.Equal(EnvironmentType.Unknown, EnvironmentConfigService.ParseDiscoveryType("SomeCustomType"));
        Assert.Equal(EnvironmentType.Unknown, EnvironmentConfigService.ParseDiscoveryType(null));
        Assert.Equal(EnvironmentType.Unknown, EnvironmentConfigService.ParseDiscoveryType(""));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }
}
