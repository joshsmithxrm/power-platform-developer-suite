using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Plugins;
using PPDS.Cli.Tests.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

[Trait("Category", "Unit")]
public sealed class PluginStepCapabilityPlanningTests
{
    [Fact]
    public async Task Preflight_UsesFunctionalStepIdentityAndPassesNullOnlyForNewStep()
    {
        var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var preId = Guid.NewGuid();
        var postId = Guid.NewGuid();
        var pre = Step("renamed pre", "PreOperation");
        var post = Step("renamed post", "PostOperation");
        var created = Step("new delete", "PreOperation");
        created.Message = "Delete";
        var assembly = Assembly("Existing", pre, post, created);
        service.Setup(s => s.GetAssemblyByNameAsync("Existing", CancellationToken.None))
            .ReturnsAsync(new PluginAssemblyInfo { Id = assemblyId, Name = "Existing" });
        service.Setup(s => s.ListTypesForAssemblyAsync(assemblyId, CancellationToken.None))
            .ReturnsAsync([new PluginTypeInfo { Id = typeId, TypeName = "Contoso.Handler" }]);
        service.Setup(s => s.ListStepsForTypeAsync(typeId, null, CancellationToken.None))
            .ReturnsAsync([
                StoredStep(postId, "same mutable name", "PostOperation"),
                StoredStep(preId, "same mutable name", "PreOperation")
            ]);
        var checkedSteps = new List<(PluginStepConfig Config, Guid? ExistingId)>();
        service.Setup(s => s.ValidateStepFieldCapabilitiesAsync(It.IsAny<PluginStepConfig>(), It.IsAny<Guid?>(), CancellationToken.None))
            .Callback<PluginStepConfig, Guid?, CancellationToken>((config, id, _) => checkedSteps.Add((config, id)))
            .Returns(Task.CompletedTask);

        await PluginRegistrationPlanningService.ValidateStepFieldCapabilitiesAsync(
            service.Object, [assembly], null, CancellationToken.None);

        Assert.Equal(3, checkedSteps.Count);
        Assert.Contains(checkedSteps, pair => ReferenceEquals(pair.Config, pre) && pair.ExistingId == preId);
        Assert.Contains(checkedSteps, pair => ReferenceEquals(pair.Config, post) && pair.ExistingId == postId);
        Assert.Contains(checkedSteps, pair => ReferenceEquals(pair.Config, created) && pair.ExistingId is null);
        AssertReadsOnly(service);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_LaterAssemblyCapabilityRefusal_PreventsEarlierPackageWrites(bool dryRun)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ppds-field-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // The first candidate is a real unsigned metadata fixture. Its bytes are only
            // inspected by preflight; no package or assembly is loaded or sent to Dataverse.
            var package = PluginPackageTestFixture.Create(directory, "first.nupkg", "ppds_CapabilityFirst",
                new TestPackageAssembly("CapabilityFirst", "first.dll", """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso {
                        public sealed class Handler : IPlugin {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """, ReferencesSdk: true));
            File.WriteAllBytes(Path.Combine(directory, "second.dll"), [0x4d, 0x5a]);
            var configPath = Path.Combine(directory, "registrations.json");
            File.WriteAllText(configPath, """
                {"version":"2.0","assemblies":[
                  {"name":"CapabilityFirst","type":"Nuget","packagePath":"first.nupkg",
                   "types":[{"typeName":"Contoso.Handler","steps":[{"name":"first","message":"Update","entity":"account","stage":"PostOperation"}]}]},
                  {"name":"CapabilitySecond","type":"Assembly","path":"second.dll",
                   "types":[{"typeName":"Contoso.Handler","steps":[{"name":"second","message":"Update","entity":"account","stage":"PostOperation"}]}]}
                ]}
                """);
            var assemblyId = Guid.NewGuid();
            var typeId = Guid.NewGuid();
            var existingStepId = Guid.NewGuid();
            var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
            service.Setup(s => s.GetPackageByNameAsync("ppds_CapabilityFirst", CancellationToken.None))
                .ReturnsAsync((PluginPackageInfo?)null);
            service.Setup(s => s.GetAssemblyByNameAsync("CapabilitySecond", CancellationToken.None))
                .ReturnsAsync(new PluginAssemblyInfo { Id = assemblyId, Name = "CapabilitySecond" });
            service.Setup(s => s.ListTypesForAssemblyAsync(assemblyId, CancellationToken.None))
                .ReturnsAsync([new PluginTypeInfo { Id = typeId, TypeName = "Contoso.Handler" }]);
            service.Setup(s => s.ListStepsForTypeAsync(typeId, null, CancellationToken.None))
                .ReturnsAsync([StoredStep(existingStepId, "old second name", "PostOperation")]);
            var checkedNames = new List<string?>();
            service.Setup(s => s.ValidateStepFieldCapabilitiesAsync(It.IsAny<PluginStepConfig>(), It.IsAny<Guid?>(), CancellationToken.None))
                .Returns<PluginStepConfig, Guid?, CancellationToken>((step, existingId, _) =>
                {
                    checkedNames.Add(step.Name);
                    if (step.Name == "first")
                    {
                        Assert.Null(existingId);
                        return Task.CompletedTask;
                    }
                    Assert.Equal(existingStepId, existingId);
                    return Task.FromException(new PpdsException(ErrorCodes.Plugin.FieldNotWritable,
                        "canUseReadOnlyConnection cannot be updated on this target."));
                });
            using var output = new StringWriter();

            var exit = await DeployCommand.ExecuteAsync(new FileInfo(configPath), null, null, null,
                clean: false, dryRun, new GlobalOptionValues { OutputFormat = OutputFormat.Json }, CancellationToken.None,
                serviceProviderFactory: _ => Task.FromResult(new ServiceCollection()
                    .AddSingleton(service.Object)
                    .AddSingleton(new Mock<ICustomApiService>(MockBehavior.Strict).Object)
                    .BuildServiceProvider()),
                outputWriter: new JsonOutputWriter(output));

            Assert.NotEqual(0, exit);
            Assert.Equal(["first", "second"], checkedNames);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(ErrorCodes.Plugin.FieldNotWritable,
                json.RootElement.GetProperty("error").GetProperty("code").GetString());
            AssertReadsOnly(service);
            service.Verify(s => s.UpsertPackageAsync(It.IsAny<string>(), It.IsAny<byte[]>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.True(File.Exists(package));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Preflight_ExistingPackageWithoutConfiguredAssembly_ChecksCreateCapabilities()
    {
        var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        var packageId = Guid.NewGuid();
        var assembly = Assembly("ExpectedAssembly", Step("step", "PostOperation"));
        assembly.Type = "Nuget";
        service.Setup(s => s.GetPackageByNameAsync("InspectedPackage", CancellationToken.None))
            .ReturnsAsync(new PluginPackageInfo { Id = packageId, Name = "InspectedPackage" });
        service.Setup(s => s.GetAssemblyIdForPackageAsync(packageId, "ExpectedAssembly", CancellationToken.None))
            .ReturnsAsync((Guid?)null);
        service.Setup(s => s.ValidateStepFieldCapabilitiesAsync(assembly.Types[0].Steps[0], null, CancellationToken.None))
            .Returns(Task.CompletedTask);

        await PluginRegistrationPlanningService.ValidateStepFieldCapabilitiesAsync(service.Object, [assembly],
            new Dictionary<string, string> { ["ExpectedAssembly"] = "InspectedPackage" }, CancellationToken.None);

        service.Verify(s => s.ValidateStepFieldCapabilitiesAsync(assembly.Types[0].Steps[0], null,
            CancellationToken.None), Times.Once);
        AssertReadsOnly(service);
    }

    [Fact]
    public async Task Preflight_MissingInspectedPackageIdentity_RefusesBeforeServiceCalls()
    {
        var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        var assembly = Assembly("ExpectedAssembly", Step("step", "PostOperation"));
        assembly.Type = "Nuget";

        var error = await Assert.ThrowsAsync<PpdsException>(() =>
            PluginRegistrationPlanningService.ValidateStepFieldCapabilitiesAsync(service.Object,
                [assembly], null, CancellationToken.None));

        Assert.Equal(ErrorCodes.Plugin.FieldCapabilityUnknown, error.ErrorCode);
        Assert.Empty(service.Invocations);
    }

    [Fact]
    public async Task Preflight_LegacyOmittedField_RequiresNoNewRead()
    {
        var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        var step = Step("legacy", "PostOperation");
        step.OperationIntent = RegistrationOperationIntent.Deploy;

        await PluginRegistrationPlanningService.ValidateStepFieldCapabilitiesAsync(service.Object,
            [Assembly("Legacy", step)], null, CancellationToken.None);

        Assert.Empty(service.Invocations);
    }

    private static PluginStepConfig Step(string name, string stage) => new()
    {
        Name = name, Message = "Update", Entity = "account", Stage = stage,
        Mode = "Synchronous", OperationIntent = RegistrationOperationIntent.Reconcile
    };

    private static PluginStepInfo StoredStep(Guid id, string name, string stage) => new()
    {
        Id = id, Name = name, Message = "Update", PrimaryEntity = "account", Stage = stage,
        Mode = "Synchronous", CanUseReadOnlyConnection = true
    };

    private static PluginAssemblyConfig Assembly(string name, params PluginStepConfig[] steps) => new()
    {
        Name = name, Type = "Assembly",
        Types = [new PluginTypeConfig { TypeName = "Contoso.Handler", Steps = [.. steps] }]
    };

    private static void AssertReadsOnly(Mock<IPluginRegistrationService> service) =>
        Assert.All(service.Invocations, invocation => Assert.True(
            invocation.Method.Name.StartsWith("Get", StringComparison.Ordinal) ||
            invocation.Method.Name.StartsWith("List", StringComparison.Ordinal) ||
            invocation.Method.Name == nameof(IPluginRegistrationService.ValidateStepFieldCapabilitiesAsync),
            invocation.Method.Name));
}
