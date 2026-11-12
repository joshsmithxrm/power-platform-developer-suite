using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using PPDS.Cli.Tests.Plugins;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Client;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

[Trait("Category", "Unit")]
public sealed class ImagePruningSafetyTests
{
    [Fact]
    public async Task Authority_IsValidatedAcrossAllAssembliesBeforeReading()
    {
        var f = new Fixture();
        var later = new PluginAssemblyConfig { Name = "Later", OperationIntent = RegistrationOperationIntent.Reconcile,
            Types = [new PluginTypeConfig { TypeName = "Later.Type", Steps = [new PluginStepConfig { Message = "Update", Stage = "PostOperation" }] }] };
        await Assert.ThrowsAsync<PpdsException>(() => PluginImagePruningService.PlanAsync(f.Service.Object, [f.Config, later], null, CancellationToken.None));
        Assert.Empty(f.Service.Invocations);
    }

    [Fact]
    public async Task LaterAssemblyAmbiguity_CannotPartiallyApplyAnEarlierPlan()
    {
        var f = new Fixture();
        var later = new PluginAssemblyConfig { Name = "Later", OperationIntent = RegistrationOperationIntent.Reconcile };
        f.Service.Setup(s => s.ListAssembliesAsync("Later", It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PluginAssemblyInfo { Id = Guid.NewGuid(), Name = "Later" }, new PluginAssemblyInfo { Id = Guid.NewGuid(), Name = "Later" }]);
        await Assert.ThrowsAsync<PpdsException>(() => PluginImagePruningService.PlanAsync(f.Service.Object, [f.Config, later], null, CancellationToken.None));
        Assert.Empty(f.Deletes);
    }

    [Theory]
    [InlineData("assembly-package")]
    [InlineData("type-parent")]
    [InlineData("step-parent")]
    [InlineData("image-parent")]
    [InlineData("image-name")]
    [InlineData("step-stage")]
    [InlineData("protected-image")]
    public async Task ChangedOwnershipOrIdentity_RejectsBeforeAnyDelete(string change)
    {
        var f = new Fixture(); var plan = await f.Plan();
        switch (change)
        {
            case "assembly-package": f.Assembly.PackageId = Guid.NewGuid(); break;
            case "type-parent": f.Type.AssemblyId = Guid.NewGuid(); break;
            case "step-parent": f.Step.PluginTypeId = Guid.NewGuid(); break;
            case "image-parent": f.Image.StepId = Guid.NewGuid(); break;
            case "image-name": f.Image.Name = "Changed"; break;
            case "step-stage": f.Step.Stage = "PreOperation"; break;
            case "protected-image": f.Image.IsCustomizable = false; break;
        }
        await Assert.ThrowsAsync<PpdsException>(() => PluginImagePruningService.ApplyAsync(f.Service.Object, plan, CancellationToken.None));
        Assert.Empty(f.Deletes);
    }

    [Fact]
    public async Task NewUnplannedImage_RemainsAndIsVisibleInCounts()
    {
        var f = new Fixture(); var plan = await f.Plan(); var late = f.AddImage("Late");
        var result = await PluginImagePruningService.ApplyAsync(f.Service.Object, plan, CancellationToken.None);
        Assert.Equal(new[] { f.Image.Id }, f.Deletes);
        Assert.Contains(late, f.Images); Assert.Equal(1, result.NewImagesRetained);
        Assert.Equal(1, result.ImagesDeleted); Assert.True(result.Success);
    }

    [Fact]
    public async Task NewAmbiguousStepAfterPlanning_RejectsBeforeDelete()
    {
        var f = new Fixture(); var plan = await f.Plan();
        f.Service.Setup(s => s.ListStepsForTypeAsync(f.Type.Id, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([f.Step, new PluginStepInfo { Id = Guid.NewGuid(), Name = "Other display name", Message = f.Step.Message,
                PrimaryEntity = f.Step.PrimaryEntity, Stage = f.Step.Stage, Mode = f.Step.Mode, PluginTypeId = f.Type.Id }]);
        await Assert.ThrowsAsync<PpdsException>(() => PluginImagePruningService.ApplyAsync(f.Service.Object, plan, CancellationToken.None));
        Assert.Empty(f.Deletes);
    }

    [Fact]
    public async Task DeleteFailure_RetainsAcknowledgementAndUnknownDispatchWithoutDiagnostics()
    {
        var f = new Fixture(); var second = f.AddImage("Second"); var plan = await f.Plan();
        f.Service.Setup(s => s.DeleteImageAsync(second.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("synthetic-private-marker"));
        var error = await Assert.ThrowsAsync<PpdsException>(() => PluginImagePruningService.ApplyAsync(f.Service.Object, plan, CancellationToken.None));
        var result = Assert.IsType<PluginImagePruningResult>(PluginImagePruningService.InterruptedResult(error));
        Assert.Equal(1, result.ImagesDeleted); Assert.True(result.Partial); Assert.False(result.Success);
        Assert.Equal(new[] { "acknowledged", "unknown" }, result.Outcomes.Select(o => o.Outcome));
        Assert.DoesNotContain("synthetic-private-marker", error.UserMessage + JsonSerializer.Serialize(result));
        Assert.Equal(new[] { f.Image.Id }, f.Deletes);
    }

    [Fact]
    public async Task CancellationAfterAcknowledgement_StopsBeforeAnotherDispatch()
    {
        var f = new Fixture(); f.AddImage("Second"); var plan = await f.Plan();
        using var cancellation = new CancellationTokenSource();
        f.AfterDelete = cancellation.Cancel;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PluginImagePruningService.ApplyAsync(f.Service.Object, plan, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        var result = Assert.IsType<PluginImagePruningResult>(PluginImagePruningService.InterruptedResult(error));
        Assert.Equal("cancelled", result.Status); Assert.Equal(1, result.ImagesDeleted);
        Assert.Single(f.Deletes); Assert.Single(result.Outcomes);
    }

    [Fact]
    public async Task RawInventoryFailure_IsCodedAndKeepsPrivateDiagnosticsOutOfTheMessage()
    {
        var f = new Fixture(); var fault = new InvalidOperationException("synthetic-private-marker");
        f.Service.Setup(s => s.ListImagesForStepAsync(f.Step.Id, It.IsAny<CancellationToken>())).ThrowsAsync(fault);
        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Plan());
        Assert.Same(fault, error.InnerException); Assert.DoesNotContain("synthetic-private-marker", error.UserMessage);
        Assert.Empty(f.Deletes);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("prefixed-sibling")]
    [InlineData("foreign-parent")]
    [InlineData("duplicate-package")]
    public async Task PackageScope_RequiresExactPackageIdentityAndAssemblyParent(string shape)
    {
        var f = new Fixture(); var packageId = Guid.NewGuid(); const string artifactId = "Fixture.Package";
        var packageName = shape == "prefixed-sibling" ? "ppds_" + artifactId : artifactId;
        f.Config.Type = "Nuget";
        f.Assembly.PackageId = shape == "foreign-parent" ? Guid.NewGuid() : packageId;
        var packages = new List<PluginPackageInfo> { new() { Id = packageId, Name = packageName, UniqueName = packageName } };
        if (shape == "duplicate-package") packages.Add(new() { Id = Guid.NewGuid(), Name = packageName });
        f.Service.Setup(s => s.ListPackagesAsync(packageName, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(packages);
        var resolution = PluginPackageResolutionTestFactory.Create(artifactId, packageId, packageName, packageName);
        f.Service.Setup(s => s.ValidatePackageResolutionAsync(resolution, It.IsAny<CancellationToken>(), null))
            .Returns(() => shape == "duplicate-package"
                ? Task.FromException(new PpdsException(ErrorCodes.Operation.Duplicate, "Package identity became ambiguous."))
                : Task.CompletedTask);
        f.Service.Setup(s => s.GetPackageByIdAsync(packageId, It.IsAny<CancellationToken>())).ReturnsAsync(packages[0]);
        f.Service.Setup(s => s.GetAssemblyIdForPackageAsync(packageId, f.Config.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(f.Assembly.Id);
        if (shape == "prefixed-sibling")
            f.Service.Setup(s => s.ListAssembliesAsync(f.Config.Name, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("A global same-name inventory must not select the sibling package."));
        Task<PluginImagePruningPlan> Run() => PluginImagePruningService.PlanAsync(f.Service.Object, [f.Config],
            new Dictionary<string, PluginPackageResolution> { [f.Config.Name] = resolution }, CancellationToken.None);
        if (shape is "verified" or "prefixed-sibling")
        {
            var plan = await Run(); Assert.Equal(packageId, Assert.Single(plan.Images).Scope.PackageId);
            var result = await PluginImagePruningService.ApplyAsync(f.Service.Object, plan, CancellationToken.None);
            Assert.True(result.Success); Assert.Equal(new[] { f.Image.Id }, f.Deletes);
        }
        else
        {
            await Assert.ThrowsAsync<PpdsException>(() => Run()); Assert.Empty(f.Deletes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCleanCommand_UsesTheSharedImageOnlyPlanAndJson(bool dryRun)
    {
        var f = new Fixture();
        var directory = Path.Combine(Path.GetTempPath(), "ppds-image-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "registrations.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new PluginRegistrationConfig { Version = "2.0", Assemblies = [f.Config] }));
            using var output = new StringWriter();
            var command = CleanCommand.Create(_ => Task.FromResult(new ServiceCollection().AddSingleton(f.Service.Object).BuildServiceProvider()),
                outputWriter: new JsonOutputWriter(output));
            var arguments = new List<string> { "--config", path, "--output-format", "json" };
            if (dryRun) arguments.Add("--dry-run");
            var parsed = command.Parse(arguments.ToArray()); Assert.Empty(parsed.Errors);
            Assert.Equal(0, await parsed.InvokeAsync());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            var cleanup = document.RootElement.GetProperty("data")[0].GetProperty("imageCleanup");
            Assert.Equal(f.Image.Id.ToString(), cleanup.GetProperty("plan").GetProperty("images")[0].GetProperty("imageId").GetString());
            Assert.Equal(dryRun ? 0 : 1, cleanup.GetProperty("imagesDeleted").GetInt32());
            Assert.Equal(dryRun ? 0 : 1, f.Deletes.Count);
            Assert.DoesNotContain(f.Service.Invocations, call => call.Method.Name is "DeleteStepAsync" or "DeletePluginTypeAsync" or "UpsertStepAsync");
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("1.0", false)]
    [InlineData("1.0", true)]
    [InlineData("2.0", false)]
    [InlineData("2.0", true)]
    public Task ActualCleanCommand_AbsentPackageWithoutSolutionDoesNotBlockExistingScope(string version, bool dryRun)
        => AssertAbsentPackageCleanupAsync(version, dryRun, aliasPair: false);

    [Theory]
    [InlineData("1.0", false)]
    [InlineData("1.0", true)]
    [InlineData("2.0", false)]
    [InlineData("2.0", true)]
    public Task ActualCleanCommand_AbsentAliasPackagesDoNotInvokeDeploymentCreateGuard(string version, bool dryRun)
        => AssertAbsentPackageCleanupAsync(version, dryRun, aliasPair: true);

    private static async Task AssertAbsentPackageCleanupAsync(string version, bool dryRun, bool aliasPair)
    {
        var f = new Fixture();
        var directory = Path.Combine(Path.GetTempPath(), "ppds-clean-absent-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string[] artifactIds = aliasPair ? ["Thing", "ppds_Thing"] : ["Absent.Package"];
            string[] candidates = aliasPair ? ["Thing", "ppds_Thing", "ppds_ppds_Thing"] : ["Absent.Package"];
            var absentConfigs = new List<PluginAssemblyConfig>();
            for (var index = 0; index < artifactIds.Length; index++)
            {
                var assemblyName = "AbsentAssembly" + index;
                var packagePath = PluginPackageTestFixture.Create(directory, $"absent{index}.nupkg", artifactIds[index],
                    new TestPackageAssembly(assemblyName, "absent.dll", """
                        using System;
                        using Microsoft.Xrm.Sdk;
                        namespace Absent {
                            public sealed class Handler : IPlugin {
                                public void Execute(IServiceProvider serviceProvider) { }
                            }
                        }
                        """, ReferencesSdk: true));
                absentConfigs.Add(new PluginAssemblyConfig
                {
                    Name = assemblyName, Type = "Nuget", PackagePath = Path.GetFileName(packagePath), Types = [],
                    Solution = aliasPair ? "FixtureSolution" : null
                });
            }
            var solutionId = Guid.NewGuid();
            var publisherId = Guid.NewGuid();
            var client = new Mock<IPooledClient>(MockBehavior.Strict);
            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
            client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
            pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(client.Object);
            client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((QueryBase query, CancellationToken _) =>
                {
                    var exact = Assert.IsType<QueryExpression>(query);
                    if (exact.EntityName == "solution")
                    {
                        Assert.True(aliasPair);
                        Assert.Equal("FixtureSolution", Assert.Single(Assert.Single(exact.Criteria.Conditions).Values));
                        return new EntityCollection([new Entity("solution", solutionId)
                        {
                            ["uniquename"] = "FixtureSolution", ["ismanaged"] = false,
                            ["publisherid"] = new EntityReference("publisher", publisherId)
                        }]);
                    }
                    Assert.Equal("pluginpackage", exact.EntityName);
                    Assert.Equal(2, exact.Criteria.Conditions.Count);
                    Assert.All(exact.Criteria.Conditions, condition =>
                    {
                        Assert.Equal(ConditionOperator.Equal, condition.Operator);
                        Assert.Contains(Assert.IsType<string>(Assert.Single(condition.Values)), candidates);
                    });
                    return new EntityCollection();
                });
            client.Setup(c => c.RetrieveAsync("publisher", publisherId, It.IsAny<ColumnSet>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Entity("publisher", publisherId) { ["customizationprefix"] = "ppds" });
            var identityService = new PluginRegistrationService(pool.Object, new InactiveFakeShakedownGuard(),
                NullLogger<PluginRegistrationService>.Instance);
            f.Service.Setup(s => s.ResolvePackageForCleanupAsync(It.IsAny<byte[]>(), It.IsAny<string?>(), null, It.IsAny<CancellationToken>(), null))
                .Returns((byte[] bytes, string? solution, string? selector, CancellationToken ct, IProgressReporter? progress) =>
                    identityService.ResolvePackageForCleanupAsync(bytes, solution, selector, ct, progress));
            f.Service.Setup(s => s.ValidatePackageResolutionAsync(It.IsAny<PluginPackageResolution>(), It.IsAny<CancellationToken>(), null))
                .Returns((PluginPackageResolution plan, CancellationToken ct, IProgressReporter? progress) =>
                    identityService.ValidatePackageResolutionAsync(plan, ct, progress));

            var deletedSteps = new List<Guid>();
            var orphanId = Guid.NewGuid();
            if (version == "1.0")
            {
                f.Service.Setup(s => s.GetAssemblyByNameAsync(f.Assembly.Name, It.IsAny<CancellationToken>())).ReturnsAsync(f.Assembly);
                f.Service.Setup(s => s.ListStepsForTypeAsync(f.Type.Id, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync([f.Step, new PluginStepInfo
                    {
                        Id = orphanId, Name = "Owned orphan", PluginTypeId = f.Type.Id, Message = "Update",
                        PrimaryEntity = "account", Stage = "PreOperation", Mode = "Synchronous"
                    }]);
                f.Service.Setup(s => s.DeleteStepAsync(orphanId, It.IsAny<CancellationToken>()))
                    .Callback<Guid, CancellationToken>((id, _) => deletedSteps.Add(id)).Returns(Task.CompletedTask);
            }
            var configPath = Path.Combine(directory, "registrations.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(new PluginRegistrationConfig
            {
                Version = version, Assemblies = [f.Config, .. absentConfigs]
            }));
            using var output = new StringWriter();
            var command = CleanCommand.Create(_ => Task.FromResult(new ServiceCollection().AddSingleton(f.Service.Object).BuildServiceProvider()),
                outputWriter: new JsonOutputWriter(output));
            var arguments = new List<string> { "--config", configPath, "--output-format", "json" };
            if (dryRun) arguments.Add("--dry-run");
            var parsed = command.Parse(arguments.ToArray());
            Assert.Empty(parsed.Errors);

            var exit = await parsed.InvokeAsync();

            Assert.True(exit == 0, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            var results = document.RootElement.GetProperty("data");
            Assert.Equal(1 + absentConfigs.Count, results.GetArrayLength());
            if (version == "2.0")
            {
                foreach (var row in results.EnumerateArray().Skip(1))
                {
                    var absent = row.GetProperty("imageCleanup");
                    Assert.Equal("no-existing-package-observed", absent.GetProperty("plan").GetProperty("inventories")[0]
                        .GetProperty("status").GetString());
                }
                Assert.Equal(dryRun ? 0 : 1, f.Deletes.Count);
                if (!dryRun) Assert.Equal(f.Image.Id, Assert.Single(f.Deletes));
                Assert.Empty(deletedSteps);
            }
            else
            {
                Assert.Equal(dryRun ? 0 : 1, deletedSteps.Count);
                if (!dryRun) Assert.Equal(orphanId, Assert.Single(deletedSteps));
                Assert.Empty(f.Deletes);
                foreach (var row in results.EnumerateArray().Skip(1))
                    Assert.Empty(row.GetProperty("orphanedSteps").EnumerateArray());
            }
            f.Service.Verify(s => s.ResolvePackageForCleanupAsync(It.IsAny<byte[]>(), It.IsAny<string?>(), null,
                It.IsAny<CancellationToken>(), null), Times.Exactly(absentConfigs.Count));
            f.Service.Verify(s => s.ResolvePackageAsync(It.IsAny<byte[]>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), null), Times.Never);
            Assert.DoesNotContain(f.Service.Invocations, call => call.Method.Name.StartsWith("Upsert", StringComparison.Ordinal)
                || call.Method.Name == "DeletePluginTypeAsync");
            Assert.DoesNotContain(client.Invocations, call => call.Method.Name is "ExecuteAsync" or "DeleteAsync" or "UpdateAsync" or "CreateAsync");
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ActualRegistrationDelete_DispatchesOneImageIdWithGuardTokenAndReturnedLease()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token; var id = Guid.NewGuid();
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        var client = new Mock<IPooledClient>(MockBehavior.Strict);
        var guard = new Mock<IShakedownGuard>(MockBehavior.Strict);
        guard.Setup(g => g.EnsureCanMutate("plugins.image.delete"));
        pool.Setup(p => p.GetClientAsync(null, null, token)).ReturnsAsync(client.Object);
        client.Setup(c => c.DeleteAsync("sdkmessageprocessingstepimage", id, token)).Returns(Task.CompletedTask);
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var service = new PluginRegistrationService(pool.Object, guard.Object, NullLogger<PluginRegistrationService>.Instance);
        await service.DeleteImageAsync(id, token);
        guard.Verify(g => g.EnsureCanMutate("plugins.image.delete"), Times.Once);
        pool.Verify(p => p.GetClientAsync(null, null, token), Times.Once);
        client.Verify(c => c.DeleteAsync("sdkmessageprocessingstepimage", id, token), Times.Once);
        client.Verify(c => c.DisposeAsync(), Times.Once);
        guard.VerifyNoOtherCalls(); pool.VerifyNoOtherCalls(); client.VerifyNoOtherCalls();
    }

    [Fact]
    public void GenericJson_NonemptyImperativeImageListAndInputSecretRemain()
    {
        var step = new PluginStepConfig { SecureConfiguration = "synthetic-input-secret" };
        step.Images.Add(new PluginImageConfig { Name = "Keep", ImageType = "PreImage" });
        var restored = JsonSerializer.Deserialize<PluginStepConfig>(JsonSerializer.Serialize(step))!;
        Assert.Single(restored.Images); Assert.True(restored.IsSpecified("images"));
        Assert.Equal("synthetic-input-secret", restored.SecureConfiguration);
    }

    private sealed class Fixture
    {
        internal Mock<IPluginRegistrationService> Service { get; } = new(MockBehavior.Strict);
        internal PluginAssemblyInfo Assembly { get; } = new() { Id = Guid.NewGuid(), Name = "Fixture" };
        internal PluginTypeInfo Type { get; }
        internal PluginStepInfo Step { get; }
        internal PluginImageInfo Image { get; }
        internal PluginAssemblyConfig Config { get; }
        internal List<PluginImageInfo> Images { get; } = [];
        internal List<Guid> Deletes { get; } = [];
        internal Action? AfterDelete { get; set; }

        internal Fixture()
        {
            Type = new() { Id = Guid.NewGuid(), TypeName = "Contoso.Handler", AssemblyId = Assembly.Id };
            Step = new() { Id = Guid.NewGuid(), Name = "Update", PluginTypeId = Type.Id, Message = "Update", PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous" };
            Image = AddImage("Extra");
            Config = new() { Name = Assembly.Name, OperationIntent = RegistrationOperationIntent.Reconcile,
                Types = [new PluginTypeConfig { TypeName = Type.TypeName, Steps = [new PluginStepConfig {
                    Message = Step.Message, Entity = Step.PrimaryEntity, Stage = Step.Stage, Images = [] }] }] };
            Service.Setup(s => s.ListAssembliesAsync(Assembly.Name, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync([Assembly]);
            Service.Setup(s => s.GetAssemblyByIdAsync(Assembly.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => Assembly);
            Service.Setup(s => s.ListTypesForAssemblyAsync(Assembly.Id, It.IsAny<CancellationToken>())).ReturnsAsync([Type]);
            Service.Setup(s => s.GetPluginTypeByNameOrIdAsync(Type.Id.ToString("D"), It.IsAny<CancellationToken>())).ReturnsAsync(() => Type);
            Service.Setup(s => s.ListStepsForTypeAsync(Type.Id, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync([Step]);
            Service.Setup(s => s.GetStepByNameOrIdAsync(Step.Id.ToString("D"), It.IsAny<CancellationToken>())).ReturnsAsync(() => Step);
            Service.Setup(s => s.ListImagesForStepAsync(Step.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => Images);
            Service.Setup(s => s.GetImageByNameOrIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => Images.SingleOrDefault(i => i.Id.ToString("D") == id));
            Service.Setup(s => s.DeleteImageAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, CancellationToken>((id, _) => { Deletes.Add(id); Images.RemoveAll(i => i.Id == id); AfterDelete?.Invoke(); })
                .Returns(Task.CompletedTask);
        }

        internal PluginImageInfo AddImage(string name)
        {
            var image = new PluginImageInfo { Id = Guid.NewGuid(), Name = name, ImageType = "PreImage", StepId = Step.Id };
            Images.Add(image); return image;
        }
        internal Task<PluginImagePruningPlan> Plan() => PluginImagePruningService.PlanAsync(Service.Object, [Config], null, CancellationToken.None);
    }
}
