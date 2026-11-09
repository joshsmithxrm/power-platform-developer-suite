using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using PPDS.Dataverse.Pooling;
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
    [InlineData("foreign-parent")]
    [InlineData("duplicate-package")]
    public async Task PackageScope_RequiresExactPackageIdentityAndAssemblyParent(string shape)
    {
        var f = new Fixture(); var packageId = Guid.NewGuid(); const string packageName = "Fixture.Package";
        f.Config.Type = "Nuget";
        f.Assembly.PackageId = shape == "foreign-parent" ? Guid.NewGuid() : packageId;
        var packages = new List<PluginPackageInfo> { new() { Id = packageId, Name = packageName } };
        if (shape == "duplicate-package") packages.Add(new() { Id = Guid.NewGuid(), Name = packageName });
        f.Service.Setup(s => s.ListPackagesAsync(packageName, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(packages);
        f.Service.Setup(s => s.GetPackageByIdAsync(packageId, It.IsAny<CancellationToken>())).ReturnsAsync(packages[0]);
        Task<PluginImagePruningPlan> Run() => PluginImagePruningService.PlanAsync(f.Service.Object, [f.Config],
            new Dictionary<string, string> { [f.Config.Name] = packageName }, CancellationToken.None);
        if (shape == "verified")
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
