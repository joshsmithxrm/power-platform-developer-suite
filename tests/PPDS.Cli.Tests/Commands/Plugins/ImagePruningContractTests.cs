using System.Text.Json;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

// Common before/fixed regressions use only pre-existing entrypoints and DTOs.
// Every registration call is mocked: no auth, Dataverse or native assembly loading.
public sealed class ImagePruningContractTests
{
    [Fact]
    public async Task ExplicitEmptyImages_DeletesOnlyImagesOnConfiguredStep()
    {
        var f = new Fixture();
        f.AddUnconfiguredParent();

        await f.CleanAsync();

        Assert.Equal(new[] { f.Image.Id }, f.DeletedImages);
        Assert.Empty(f.DeletedSteps);
        Assert.Empty(f.DeletedTypes);
    }

    [Fact]
    public async Task ExplicitKeepList_RetainsNamedImageAndDeletesOnlyExtraId()
    {
        var f = new Fixture();
        f.ConfigStep.Images = [new PluginImageConfig { Name = "Keep", ImageType = "PreImage" }];
        var keep = f.NewImage("Keep");
        f.Images.Add(keep);

        await f.CleanAsync();

        Assert.Equal(new[] { f.Image.Id }, f.DeletedImages);
        Assert.Contains(keep, f.Images);
    }

    [Fact]
    public async Task OmittedImages_RejectsBeforeAnyMutation()
    {
        var f = new Fixture(omittedImages: true);
        f.AddUnconfiguredParent();

        await Assert.ThrowsAsync<PpdsException>(() => f.CleanAsync());

        f.AssertNoMutations();
    }

    [Fact]
    public async Task EmptyConfiguredTypes_NeverDeletesUnconfiguredParents()
    {
        var f = new Fixture();
        f.Assembly.Types.Clear();
        f.Assembly.AllTypeNames.Clear();

        await f.CleanAsync();

        f.AssertNoMutations();
    }

    [Fact]
    public async Task Version1_CleanupRetainsLegacyParentDeletionWithoutIndependentImagePruning()
    {
        var f = new Fixture();
        f.Assembly.OperationIntent = RegistrationOperationIntent.Deploy;
        f.ConfigStep.OperationIntent = RegistrationOperationIntent.Deploy;
        f.AddUnconfiguredParent();

        await f.CleanAsync();

        Assert.Single(f.DeletedSteps);
        Assert.Single(f.DeletedTypes);
        Assert.Empty(f.DeletedImages);
        Assert.Contains(f.Image, f.Images);
    }

    [Fact]
    public async Task DryRun_ReportsExactImageAndParentIdsWithoutMutations()
    {
        var f = new Fixture();

        var result = await f.CleanAsync(dryRun: true);

        f.AssertNoMutations();
        var output = JsonSerializer.Serialize(result);
        Assert.Contains(f.Image.Id.ToString(), output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(f.Step.Id.ToString(), output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(f.Type.Id.ToString(), output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(f.StoredAssembly.Id.ToString(), output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("assembly")]
    [InlineData("type")]
    [InlineData("step")]
    [InlineData("image")]
    public async Task AmbiguousInventory_RejectsWholePlanBeforeMutations(string scope)
    {
        var f = new Fixture();
        if (scope == "assembly") f.Assemblies.Add(new PluginAssemblyInfo { Id = Guid.NewGuid(), Name = f.StoredAssembly.Name });
        if (scope == "type") f.Types.Add(new PluginTypeInfo { Id = Guid.NewGuid(), TypeName = f.Type.TypeName, AssemblyId = f.StoredAssembly.Id });
        if (scope == "step") f.Steps.Add(new PluginStepInfo
        {
            Id = Guid.NewGuid(), PluginTypeId = f.Type.Id, Name = "Different label", Message = f.Step.Message,
            PrimaryEntity = f.Step.PrimaryEntity, Stage = f.Step.Stage, Mode = f.Step.Mode
        });
        if (scope == "image") f.Images.Add(f.NewImage(f.Image.Name));

        await Assert.ThrowsAsync<PpdsException>(() => f.CleanAsync());

        f.AssertNoMutations();
    }

    [Theory]
    [InlineData("type-parent")]
    [InlineData("step-parent")]
    [InlineData("image-parent")]
    [InlineData("step-stage")]
    [InlineData("image-name")]
    public async Task UnavailableOrForeignOwnership_RejectsBeforeMutations(string field)
    {
        var f = new Fixture();
        if (field == "type-parent") f.Type.AssemblyId = null;
        if (field == "step-parent") f.Step.PluginTypeId = Guid.NewGuid();
        if (field == "image-parent") f.Image.StepId = null;
        if (field == "step-stage") f.Step.UnavailableProperties.Add("stage");
        if (field == "image-name") f.Image.UnavailableProperties.Add("name");

        await Assert.ThrowsAsync<PpdsException>(() => f.CleanAsync());

        f.AssertNoMutations();
    }

    [Fact]
    public async Task LaterStepInventoryFailure_DoesNotPartiallyDeleteEarlierImages()
    {
        var f = new Fixture();
        var second = new PluginStepConfig { Message = "Create", Entity = "contact", Stage = "PostOperation", Images = [] };
        f.Assembly.Types[0].Steps.Add(second);
        var secondStep = new PluginStepInfo
        {
            Id = Guid.NewGuid(), Name = "Create contact", PluginTypeId = f.Type.Id,
            Message = "Create", PrimaryEntity = "contact", Stage = "PostOperation", Mode = "Synchronous"
        };
        f.Steps.Add(secondStep);
        f.Service.Setup(s => s.ListImagesForStepAsync(secondStep.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("synthetic-inventory-marker"));

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.CleanAsync());

        Assert.DoesNotContain("synthetic-inventory-marker", error.UserMessage);
        f.AssertNoMutations();
    }

    [Fact]
    public async Task CancellationBeforePlanning_PreservesCancellationAndMakesNoMutation()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.CleanAsync(cancellationToken: cancellation.Token));

        f.AssertNoMutations();
    }

    [Theory]
    [InlineData("generic-step", false)]
    [InlineData("generic-step", true)]
    [InlineData("generic-config", false)]
    [InlineData("generic-config", true)]
    [InlineData("explicit-output", false)]
    [InlineData("explicit-output", true)]
    public void JsonRoundTrip_PreservesEmptyVersusOmittedImageAuthority(string codec, bool present)
    {
        var json = "{\"version\":\"2.0\",\"assemblies\":[{\"name\":\"LocalFixture\",\"types\":[{\"typeName\":\"Contoso.Handler\",\"steps\":[{\"message\":\"Update\",\"entity\":\"account\",\"stage\":\"PostOperation\"" +
            (present ? ",\"images\":[]" : "") + "}]}]}]}";
        var config = PluginRegistrationJson.Deserialize(json);
        var step = config.Assemblies[0].Types[0].Steps[0];
        PluginStepConfig roundTrip;
        if (codec == "generic-step") roundTrip = JsonSerializer.Deserialize<PluginStepConfig>(JsonSerializer.Serialize(step))!;
        else
        {
            var output = codec == "generic-config" ? JsonSerializer.Serialize(config) : PluginRegistrationJson.SerializeForOutput(config);
            roundTrip = PluginRegistrationJson.Deserialize(output).Assemblies[0].Types[0].Steps[0];
        }

        Assert.Equal(present, roundTrip.IsSpecified("images"));
        Assert.Empty(roundTrip.Images);
    }

    [Fact]
    public void DuplicateImageLists_AreRejectedInsteadOfSelectingDeleteAuthority()
    {
        var json = "{\"version\":\"2.0\",\"assemblies\":[{\"name\":\"LocalFixture\",\"types\":[{\"typeName\":\"Contoso.Handler\",\"steps\":[{\"message\":\"Update\",\"entity\":\"account\",\"images\":[{\"name\":\"Keep\",\"imageType\":\"PreImage\"}],\"Images\":[]}]}]}]}";

        Assert.Throws<PpdsException>(() => PluginRegistrationJson.Deserialize(json));
    }

    private sealed class Fixture
    {
        public Mock<IPluginRegistrationService> Service { get; } = new(MockBehavior.Strict);
        public PluginAssemblyInfo StoredAssembly { get; } = new() { Id = Guid.NewGuid(), Name = "LocalFixture" };
        public PluginTypeInfo Type { get; } = new() { Id = Guid.NewGuid(), TypeName = "Contoso.Handler" };
        public PluginStepInfo Step { get; } = new()
        {
            Id = Guid.NewGuid(), Name = "Update account", Message = "Update", PrimaryEntity = "account",
            Stage = "PostOperation", Mode = "Synchronous"
        };
        public PluginImageInfo Image { get; }
        public List<PluginAssemblyInfo> Assemblies { get; } = [];
        public List<PluginTypeInfo> Types { get; } = [];
        public List<PluginStepInfo> Steps { get; } = [];
        public List<PluginImageInfo> Images { get; } = [];
        public List<Guid> DeletedImages { get; } = [];
        public List<Guid> DeletedSteps { get; } = [];
        public List<Guid> DeletedTypes { get; } = [];
        public PluginStepConfig ConfigStep { get; }
        public PluginAssemblyConfig Assembly { get; }

        public Fixture(bool omittedImages = false)
        {
            Type.AssemblyId = StoredAssembly.Id;
            Step.PluginTypeId = Type.Id;
            Assemblies.Add(StoredAssembly);
            Types.Add(Type);
            Steps.Add(Step);
            Image = NewImage("Extra");
            Images.Add(Image);
            ConfigStep = new PluginStepConfig { Name = Step.Name, Message = Step.Message, Entity = Step.PrimaryEntity, Stage = Step.Stage, OperationIntent = RegistrationOperationIntent.Reconcile };
            if (!omittedImages) ConfigStep.Images = [];
            Assembly = new PluginAssemblyConfig
            {
                Name = StoredAssembly.Name,
                OperationIntent = RegistrationOperationIntent.Reconcile,
                Types = [new PluginTypeConfig { TypeName = Type.TypeName, Steps = [ConfigStep] }],
                AllTypeNames = [Type.TypeName]
            };
            Service.Setup(s => s.GetAssemblyByNameAsync(StoredAssembly.Name, It.IsAny<CancellationToken>())).ReturnsAsync(StoredAssembly);
            Service.Setup(s => s.ListAssembliesAsync(StoredAssembly.Name, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Assemblies);
            Service.Setup(s => s.GetAssemblyByIdAsync(StoredAssembly.Id, It.IsAny<CancellationToken>())).ReturnsAsync(StoredAssembly);
            Service.Setup(s => s.ListTypesForAssemblyAsync(StoredAssembly.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => Types);
            Service.Setup(s => s.GetPluginTypeByNameOrIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => Types.SingleOrDefault(t => t.Id.ToString() == id));
            Service.Setup(s => s.ListStepsForTypeAsync(It.Is<Guid>(id => Types.Any(type => type.Id == id)), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, PluginListOptions? _, CancellationToken _) => Steps.Where(s => s.PluginTypeId == id).ToList());
            // A deliberately foreign parent still appears in the queried inventory: providers cannot
            // be trusted merely because a filter was requested.
            Service.Setup(s => s.ListStepsForTypeAsync(Type.Id, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Steps.Where(s => s == Step || s.PluginTypeId == Type.Id).ToList());
            Service.Setup(s => s.GetStepByNameOrIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => Steps.SingleOrDefault(s => s.Id.ToString() == id));
            Service.Setup(s => s.ListImagesForStepAsync(It.Is<Guid>(id => Steps.Any(step => step.Id == id)), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => Images.Where(i => i.StepId == id || id == Step.Id && i == Image).ToList());
            Service.Setup(s => s.GetImageByNameOrIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => Images.SingleOrDefault(i => i.Id.ToString() == id));
            Service.Setup(s => s.DeleteImageAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, CancellationToken>((id, _) => { DeletedImages.Add(id); Images.RemoveAll(i => i.Id == id); }).Returns(Task.CompletedTask);
            Service.Setup(s => s.DeleteStepAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, CancellationToken>((id, _) => DeletedSteps.Add(id)).Returns(Task.CompletedTask);
            Service.Setup(s => s.DeletePluginTypeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, CancellationToken>((id, _) => DeletedTypes.Add(id)).Returns(Task.CompletedTask);
        }

        public PluginImageInfo NewImage(string name) => new()
        {
            Id = Guid.NewGuid(), Name = name, ImageType = "PreImage", StepId = Step.Id,
            EntityAlias = name, MessagePropertyName = "Target"
        };

        public void AddUnconfiguredParent()
        {
            var type = new PluginTypeInfo { Id = Guid.NewGuid(), TypeName = "Foreign.Handler", AssemblyId = StoredAssembly.Id };
            Types.Add(type);
            Steps.Add(new PluginStepInfo { Id = Guid.NewGuid(), Name = "Leave parent", Message = "Create", PrimaryEntity = "contact", Stage = "PostOperation", Mode = "Synchronous", PluginTypeId = type.Id });
        }

        public Task<CleanCommand.CleanResult> CleanAsync(bool dryRun = false, CancellationToken cancellationToken = default) =>
            CleanCommand.CleanAssemblyAsync(Service.Object, Assembly, dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json }, cancellationToken);

        public void AssertNoMutations()
        {
            Assert.Empty(DeletedImages);
            Assert.Empty(DeletedSteps);
            Assert.Empty(DeletedTypes);
            Assert.DoesNotContain(Service.Invocations, call =>
                call.Method.Name.StartsWith("Delete", StringComparison.Ordinal) ||
                call.Method.Name.StartsWith("Upsert", StringComparison.Ordinal) ||
                call.Method.Name.StartsWith("Update", StringComparison.Ordinal));
        }
    }
}
