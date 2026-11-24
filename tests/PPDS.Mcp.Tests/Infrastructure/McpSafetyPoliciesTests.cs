using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Environment;
using PPDS.Mcp.Infrastructure;
using Xunit;

namespace PPDS.Mcp.Tests.Infrastructure;

/// <summary>The MCP server never lowers an environment's write protection, confirmed or not (#1829).</summary>
public sealed class McpSafetyPoliciesTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";

    private readonly string _tempDir;
    private readonly EnvironmentConfigStore _store;

    public McpSafetyPoliciesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-mcppolicy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task McpRegistration_RefusesLoweringEvenWhenConfirmed_AndSavesNothing()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);
        using var provider = BuildLikeMcp();

        var save = () => provider.GetRequiredService<IEnvironmentConfigService>()
            .SaveConfigAsync(Url, type: EnvironmentType.Development, confirmLowerProtection: Url);

        (await save.Should().ThrowAsync<PpdsException>()).Which.ErrorCode
            .Should().Be(EnvironmentConfirmationErrorCodes.ProtectionLoweringRefused);
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
    }

    [Fact]
    public async Task McpRegistration_AllowsChangesThatDoNotLower()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Development);
        using var provider = BuildLikeMcp();

        var saved = await provider.GetRequiredService<IEnvironmentConfigService>()
            .SaveConfigAsync(Url, label: "Dev", type: EnvironmentType.Production);

        saved.Type.Should().Be(EnvironmentType.Production);
    }

    [Fact]
    public void McpRegistration_RegistersTheRefusePolicy()
    {
        using var provider = BuildLikeMcp();

        provider.GetRequiredService<ProtectionLoweringPolicy>().Should().BeSameAs(ProtectionLoweringPolicy.Refuse);
    }

    // The host (Program.cs) and every per-call provider (McpToolContext) call these two in this order.
    private ServiceProvider BuildLikeMcp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_store); // AddAuthServices uses TryAdd, so this temporary store wins.
        services.AddCliApplicationServices();
        services.AddMcpSafetyPolicies();
        return services.BuildServiceProvider();
    }
}
