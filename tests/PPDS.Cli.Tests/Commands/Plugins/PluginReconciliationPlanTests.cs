using System.Text.Json;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Services.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class PluginReconciliationPlanTests
{
    [Fact]
    public void Plan_ExposesBroadDefaultsWithoutOrdinaryOrSecureValues()
    {
        var assembly = new PluginAssemblyConfig
        {
            Name = "Fixture",
            Types = [new() { TypeName = "Fixture.Type", Steps = [new()
            {
                Message = "Update", Entity = "account", Stage = "PostOperation",
                Description = "SYNTHETIC-DESCRIPTION", UnsecureConfiguration = "SYNTHETIC-CONFIGURATION",
                SecureConfiguration = "SYNTHETIC-SECURE",
                Images = [new() { Name = "before", ImageType = "PreImage" }]
            }] }]
        };
        var plan = PluginReconciliationPlanService.Describe(assembly);
        Assert.Contains(plan, p => p.Field == "attributes" && p.Intent == "all attributes");
        Assert.Contains(plan, p => p.Field == "filteringAttributes" && p.Intent == "no filtering");
        Assert.Contains(plan, p => p.Field == "runAsUser" && p.Intent == "CallingUser");
        Assert.Contains(plan, p => p.Field == "canBeBypassed" && p.Intent == "default");
        Assert.Contains(plan, p => p.Field == "secureConfiguration" && p.Intent.Contains("unknown"));
        Assert.DoesNotContain("SYNTHETIC", JsonSerializer.Serialize(plan));
    }

    [Fact]
    public void Plan_CancellationStopsBeforeReadingFieldValues()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var assembly = new PluginAssemblyConfig { Types = [new() { Steps = [new()] }] };
        Assert.Throws<OperationCanceledException>(() => PluginReconciliationPlanService.Describe(assembly, source.Token));
    }
}
