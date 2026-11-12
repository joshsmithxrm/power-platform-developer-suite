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
using PPDS.Cli.Tests.Services.Shared;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

[Trait("Category", "Unit")]
public sealed class ResolvedPackageOrchestrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_PrefixedExistingPackageResetRefusesBeforeAnyAssemblyWrites(bool dryRun)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ppds-resolved-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var packagePath = PluginPackageTestFixture.Create(directory, "package.nupkg", "CapabilityPackage",
                new TestPackageAssembly("CapabilityAssembly", "plugin.dll", """
                    using System;
                    using Microsoft.Xrm.Sdk;
                    namespace Contoso {
                        public sealed class Handler : IPlugin {
                            public void Execute(IServiceProvider serviceProvider) { }
                        }
                    }
                    """, ReferencesSdk: true));
            File.WriteAllBytes(Path.Combine(directory, "earlier.dll"), [0x4d, 0x5a]);
            var configPath = Path.Combine(directory, "registrations.json");
            File.WriteAllText(configPath, """
                {"version":"2.0","assemblies":[
                  {"name":"EarlierAssembly","type":"Assembly","path":"earlier.dll","types":[]},
                  {"name":"CapabilityAssembly","type":"Nuget","packagePath":"package.nupkg","solution":"FixtureSolution",
                   "types":[{"typeName":"Contoso.Handler","steps":[{"message":"Update","entity":"account","stage":"PostOperation"}]}]}
                ]}
                """);
            var bytes = File.ReadAllBytes(packagePath);
            var packageId = Guid.NewGuid();
            var assemblyId = Guid.NewGuid();
            var typeId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var resolution = PluginPackageResolutionTestFactory.Create("CapabilityPackage", packageId,
                "ppds_CapabilityPackage", "ppds_CapabilityPackage", bytes);
            var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
            service.Setup(s => s.ResolvePackageAsync(It.Is<byte[]>(b => b.SequenceEqual(bytes)), "FixtureSolution", null,
                    CancellationToken.None, null)).ReturnsAsync(resolution);
            service.Setup(s => s.GetAssemblyIdForPackageAsync(packageId, "CapabilityAssembly", CancellationToken.None))
                .ReturnsAsync(assemblyId);
            service.Setup(s => s.ListTypesForAssemblyAsync(assemblyId, CancellationToken.None))
                .ReturnsAsync([new PluginTypeInfo { Id = typeId, TypeName = "Contoso.Handler" }]);
            service.Setup(s => s.ListStepsForTypeAsync(typeId, null, CancellationToken.None))
                .ReturnsAsync([new PluginStepInfo
                {
                    Id = stepId, Name = "Renamed existing step", Message = "Update", PrimaryEntity = "account",
                    Stage = "PostOperation", Mode = "Synchronous", CanUseReadOnlyConnection = true
                }]);
            service.Setup(s => s.ValidateStepFieldCapabilitiesAsync(It.IsAny<PluginStepConfig>(), stepId, CancellationToken.None, null))
                .Callback<PluginStepConfig, Guid?, CancellationToken, PPDS.Cli.Infrastructure.Progress.IProgressReporter?>((step, _, _, _) =>
                {
                    Assert.False(step.IsSpecified("canUseReadOnlyConnection"));
                    Assert.Equal(false, PluginRegistrationNormalizer.NormalizeStep(step, RegistrationOperationIntent.Reconcile)["canUseReadOnlyConnection"]);
                })
                .ThrowsAsync(new PpdsException(ErrorCodes.Plugin.FieldNotWritable, "The requested reset cannot be persisted."));
            using var output = new StringWriter();

            var exit = await DeployCommand.ExecuteAsync(new FileInfo(configPath), null, null, null, false, dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json }, CancellationToken.None,
                serviceProviderFactory: _ => Task.FromResult(new ServiceCollection()
                    .AddSingleton(service.Object).AddSingleton(new Mock<ICustomApiService>(MockBehavior.Strict).Object)
                    .BuildServiceProvider()), outputWriter: new JsonOutputWriter(output));

            Assert.NotEqual(0, exit);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(ErrorCodes.Plugin.FieldNotWritable, json.RootElement.GetProperty("error").GetProperty("code").GetString());
            service.Verify(s => s.ResolvePackageAsync(It.IsAny<byte[]>(), "FixtureSolution", null,
                CancellationToken.None, null), Times.Once);
            service.Verify(s => s.ValidateStepFieldCapabilitiesAsync(It.IsAny<PluginStepConfig>(), stepId,
                CancellationToken.None, null), Times.Once);
            Assert.DoesNotContain(service.Invocations, i => i.Method.Name.StartsWith("Upsert", StringComparison.Ordinal)
                || i.Method.Name.StartsWith("Delete", StringComparison.Ordinal));
            service.Verify(s => s.GetPackageByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preflight_EqualOrNewStepUsesTheResolvedPackageTarget(bool existing)
    {
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var step = new PluginStepConfig
        {
            Message = "Update", Entity = "account", Stage = "PostOperation", OperationIntent = RegistrationOperationIntent.Reconcile
        };
        var config = new PluginAssemblyConfig
        {
            Name = "Assembly", Type = "Nuget", Types = [new() { TypeName = "Contoso.Handler", Steps = [step] }]
        };
        var resolution = PluginPackageResolutionTestFactory.Create("Artifact", existing ? packageId : null,
            "ppds_Artifact", "ppds_Artifact");
        var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        if (existing)
        {
            service.Setup(s => s.GetAssemblyIdForPackageAsync(packageId, "Assembly", CancellationToken.None)).ReturnsAsync(assemblyId);
            service.Setup(s => s.ListTypesForAssemblyAsync(assemblyId, CancellationToken.None))
                .ReturnsAsync([new PluginTypeInfo { Id = typeId, TypeName = "Contoso.Handler" }]);
            service.Setup(s => s.ListStepsForTypeAsync(typeId, null, CancellationToken.None))
                .ReturnsAsync([new PluginStepInfo
                {
                    Id = stepId, Message = "Update", PrimaryEntity = "account", Stage = "PostOperation",
                    Mode = "Synchronous", CanUseReadOnlyConnection = false
                }]);
        }
        service.Setup(s => s.ValidateStepFieldCapabilitiesAsync(step, existing ? stepId : null, CancellationToken.None, null))
            .Returns(Task.CompletedTask);

        await PluginRegistrationPlanningService.ValidateStepFieldCapabilitiesAsync(service.Object, [config],
            new Dictionary<string, PluginPackageResolution> { [config.Name] = resolution }, CancellationToken.None);

        service.Verify(s => s.ValidateStepFieldCapabilitiesAsync(step, existing ? stepId : null, CancellationToken.None, null), Times.Once);
        service.Verify(s => s.GetPackageByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.DoesNotContain(service.Invocations, i => i.Method.Name.StartsWith("Upsert", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-parent")]
    [InlineData("wrong-id")]
    public async Task Verification_BoundAssemblyNeverFallsBackToSameNamedSibling(string shape)
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        service.Setup(s => s.GetAssemblyByIdAsync(assemblyId, CancellationToken.None)).ReturnsAsync(shape == "missing" ? null :
            new PluginAssemblyInfo { Id = shape == "wrong-id" ? Guid.NewGuid() : assemblyId,
                Name = "Assembly", PackageId = shape == "wrong-parent" ? Guid.NewGuid() : packageId });
        var compare = new PluginDiffService(service.Object);
        Task<AssemblyDrift> Run() => compare.CompareAssemblyAsync(new PluginAssemblyConfig { Name = "Assembly", Type = "Nuget" },
            cancellationToken: CancellationToken.None, target: new PluginAssemblyIdentity(assemblyId, packageId));
        if (shape == "missing") Assert.True((await Run()).AssemblyMissing);
        else await Assert.ThrowsAsync<PpdsException>(Run);
        service.Verify(s => s.GetAssemblyByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LegacyPackageCleanup_UsesResolvedAssemblyAndNeverGlobalNameFallback()
    {
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var resolution = PluginPackageResolutionTestFactory.Create("Artifact", packageId, "ppds_Artifact", "ppds_Artifact");
        var config = new PluginAssemblyConfig
        {
            Name = "SharedAssemblyName", Type = "Nuget", OperationIntent = RegistrationOperationIntent.Deploy,
            AllTypeNames = ["Contoso.Retained"]
        };
        var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        var events = new List<string>();
        service.Setup(s => s.ValidatePackageResolutionAsync(resolution, CancellationToken.None, null))
            .Callback(() => events.Add("validate")).Returns(Task.CompletedTask);
        service.Setup(s => s.GetAssemblyIdForPackageAsync(packageId, config.Name, CancellationToken.None)).ReturnsAsync(assemblyId);
        service.Setup(s => s.GetAssemblyByIdAsync(assemblyId, CancellationToken.None))
            .ReturnsAsync(new PluginAssemblyInfo { Id = assemblyId, Name = config.Name, PackageId = packageId });
        service.Setup(s => s.ListTypesForAssemblyAsync(assemblyId, CancellationToken.None))
            .ReturnsAsync([new PluginTypeInfo { Id = typeId, TypeName = "Contoso.Retained", AssemblyId = assemblyId }]);
        service.Setup(s => s.ListStepsForTypeAsync(typeId, null, CancellationToken.None))
            .ReturnsAsync([new PluginStepInfo { Id = stepId, Name = "Owned orphan", Message = "Update", PrimaryEntity = "account",
                Stage = "PostOperation", Mode = "Synchronous", PluginTypeId = typeId }]);
        service.Setup(s => s.DeleteStepAsync(stepId, CancellationToken.None))
            .Callback(() => events.Add("delete-owned-step")).Returns(Task.CompletedTask);

        var result = await CleanCommand.CleanAssemblyAsync(service.Object, config, false,
            new GlobalOptionValues { OutputFormat = OutputFormat.Json }, CancellationToken.None, resolution);

        Assert.Equal(1, result.StepsDeleted);
        Assert.Equal(new[] { "validate", "validate", "delete-owned-step" }, events);
        service.Verify(s => s.GetAssemblyByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.DeletePluginTypeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Cleanup_UsesVerifiedSuccessorWithoutChangingOriginalOwnership()
    {
        var packageId = Guid.NewGuid();
        var original = PluginPackageResolutionTestFactory.Create("Artifact", packageId, "ppds_Artifact", "ppds_Artifact")
            with { ExistingArtifactSha256 = "old-content" };
        var verified = original with { ExistingArtifactSha256 = original.ArtifactSha256 };
        var scope = new PluginImagePruningScope("Assembly", Guid.NewGuid(), packageId, original.RegisteredName,
            Guid.NewGuid(), "Contoso.Handler", Guid.NewGuid(),
            PluginStepIdentity.FromConfig("Contoso.Handler", new PluginStepConfig { Message = "Update" }), original);
        var image = new PluginImagePruningTarget(scope, Guid.NewGuid(), "OldImage", "PreImage", null);
        var plan = new PluginImagePruningPlan([scope], [image], []);

        var successor = plan.WithVerifiedPackages([verified]);

        Assert.Same(original, plan.Scopes[0].PackageResolution);
        Assert.Same(verified, successor.Scopes[0].PackageResolution);
        Assert.Same(successor.Scopes[0], successor.Images[0].Scope);
        Assert.Equal(image.ImageId, successor.Images[0].ImageId);
        Assert.Throws<PpdsException>(() => plan.WithVerifiedPackages([verified with { RegisteredName = "other" }]));
    }
}
