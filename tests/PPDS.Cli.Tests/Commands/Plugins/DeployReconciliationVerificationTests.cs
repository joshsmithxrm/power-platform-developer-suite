using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

// Runs the actual command pipeline with mocked registration services. No auth profile,
// Dataverse, native plugin loading, or persisted convergence is claimed by this fixture.
public sealed class DeployReconciliationVerificationTests
{
    [Theory]
    [InlineData(false, false, 0, "match")]
    [InlineData(true, false, 1, "drift")]
    [InlineData(false, true, 2, "incomplete")]
    [InlineData(true, true, 2, "incomplete")]
    public async Task Version2_PostApplyReadControlsEnvelopeAndExitCode(
        bool storedDrift, bool unknownCapability, int expectedExit, string expectedStatus)
    {
        using var fixture = new CommandFixture("2.0");
        if (storedDrift) fixture.StoredStep.Description = "stored-description-not-cleared";
        if (unknownCapability)
        {
            fixture.StoredStep.CanBeBypassed = null;
            fixture.StoredStep.UnavailableProperties.Add("canBeBypassed");
        }

        var exit = await fixture.RunAsync();

        Assert.Equal(expectedExit, exit);
        using var json = JsonDocument.Parse(fixture.Output.ToString());
        var envelope = json.RootElement;
        Assert.Equal(expectedExit == 0, envelope.GetProperty("success").GetBoolean());
        var row = SingleDeployment(envelope);
        Assert.Equal(expectedStatus, row.GetProperty("reconciliationStatus").GetString());
        var verification = row.GetProperty("verification");
        Assert.Equal(storedDrift, verification.GetProperty("hasDrift").GetBoolean());
        Assert.Equal(!unknownCapability, verification.GetProperty("isComplete").GetBoolean());
        if (storedDrift)
            Assert.Contains(verification.GetProperty("modifiedSteps").EnumerateArray(), step =>
                step.GetProperty("changes").EnumerateArray().Any(change =>
                    change.GetProperty("property").GetString() == "description"));
        if (unknownCapability)
            Assert.Contains(verification.GetProperty("coverage").GetProperty("unsupported").EnumerateArray(), item =>
                item.GetProperty("field").GetString()!.Contains("canBeBypassed", StringComparison.Ordinal));
        Assert.Equal(1, row.GetProperty("stepsUpdated").GetInt32());
        Assert.Contains(row.GetProperty("outcomes").EnumerateArray(), outcome =>
            outcome.GetProperty("outcome").GetString() == "acknowledged");
        fixture.VerifyStoredStateReadAfterWrite();
        fixture.VerifyNoDeletes();
    }

    [Fact]
    public async Task Version2_ExtraStoredImageIsDriftWithoutIndependentDeletion()
    {
        using var fixture = new CommandFixture("2.0");
        fixture.StoredImages.Add(new PluginImageInfo { Id = Guid.NewGuid(), Name = "unowned-image", ImageType = "PreImage" });

        Assert.Equal(1, await fixture.RunAsync());

        using var json = JsonDocument.Parse(fixture.Output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        var row = SingleDeployment(json.RootElement);
        Assert.Equal("drift", row.GetProperty("reconciliationStatus").GetString());
        var orphan = Assert.Single(row.GetProperty("verification").GetProperty("orphanedImages").EnumerateArray());
        Assert.Equal("unowned-image", orphan.GetProperty("imageName").GetString());
        Assert.Equal(0, row.GetProperty("imagesDeleted").GetInt32());
        Assert.Equal(0, row.GetProperty("stepsDeleted").GetInt32());
        fixture.VerifyStoredStateReadAfterWrite();
        fixture.VerifyNoDeletes();
    }

    [Fact]
    public async Task Version1_DoesNotAddPostApplyVerification()
    {
        using var fixture = new CommandFixture("1.0");
        fixture.StoredStep.Description = "legacy-preserved-description";

        Assert.Equal(0, await fixture.RunAsync());

        using var json = JsonDocument.Parse(fixture.Output.ToString());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        var row = SingleDeployment(json.RootElement);
        Assert.False(row.TryGetProperty("verification", out _));
        Assert.False(row.TryGetProperty("reconciliationStatus", out _));
        Assert.Contains("write-step", fixture.Events);
        Assert.DoesNotContain("verify", fixture.Events);
        fixture.Service.Verify(s => s.GetAssemblyByNameAsync("LocalFixture", CancellationToken.None), Times.Never);
        fixture.VerifyNoDeletes();
    }

    [Fact]
    public async Task Version2_DryRunIsPreviewOnlyWithoutWritesOrPostApplyVerification()
    {
        using var fixture = new CommandFixture("2.0");
        fixture.StoredStep.Description = "still-stored";
        const string ordinaryMarker = "synthetic-ordinary-config-marker";
        const string secureMarker = "synthetic-secure-config-marker";
        fixture.ConfigurePreviewInputs(ordinaryMarker, secureMarker);

        Assert.Equal(0, await fixture.RunAsync(dryRun: true));

        using var json = JsonDocument.Parse(fixture.Output.ToString());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        var row = SingleDeployment(json.RootElement);
        Assert.True(row.GetProperty("dryRun").GetBoolean());
        Assert.Equal("preview-only", row.GetProperty("reconciliationStatus").GetString());
        Assert.False(row.TryGetProperty("verification", out _));
        Assert.Empty(row.GetProperty("outcomes").EnumerateArray());
        Assert.Equal(1, row.GetProperty("imagesCreated").GetInt32());
        var plan = row.GetProperty("reconciliationPlan");
        AssertPlanIntent(plan, "types[0].steps[0]", "filteringAttributes", "no filtering");
        AssertPlanIntent(plan, "types[0].steps[0]", "runAsUser", "CallingUser");
        AssertPlanIntent(plan, "types[0].steps[0].images[0]", "attributes", "all attributes");
        AssertPlanIntent(plan, "types[0].steps[0]", "unsecureConfiguration", "set");
        AssertPlanIntent(plan, "types[0].steps[0]", "secureConfiguration", "write-only; equality unknown");
        Assert.DoesNotContain(ordinaryMarker, fixture.Output.ToString());
        Assert.DoesNotContain(secureMarker, fixture.Output.ToString());
        Assert.DoesNotContain("verify", fixture.Events);
        Assert.All(fixture.Service.Invocations, call => Assert.True(
            call.Method.Name.StartsWith("Get", StringComparison.Ordinal) ||
            call.Method.Name.StartsWith("List", StringComparison.Ordinal), call.Method.Name));
    }

    [Theory]
    [InlineData("2.0", "configured-step")]
    [InlineData("2.0", "empty-steps")]
    [InlineData("2.0", "empty-types")]
    [InlineData("1.0", "configured-step")]
    public async Task OrphanStepWarning_UsesCleanupGuidanceForTheSelectedFileVersion(string version, string shape)
    {
        using var fixture = new CommandFixture(version);
        if (shape == "configured-step")
            fixture.AdditionalStoredSteps.Add(new PluginStepInfo
            {
                Id = Guid.NewGuid(), Name = "unowned-step", Message = "Create", PrimaryEntity = "account",
                Stage = "PostOperation", Mode = "Synchronous"
            });
        else
            fixture.ConfigureEmptyRegistration(shape);

        Assert.Equal(version == "2.0" ? 1 : 0, await fixture.RunAsync());

        using var json = JsonDocument.Parse(fixture.Output.ToString());
        var row = SingleDeployment(json.RootElement);
        var warning = Assert.Single(row.GetProperty("warnings").EnumerateArray()).GetString()!;
        Assert.Contains("left in place", warning, StringComparison.OrdinalIgnoreCase);
        if (version == "2.0")
        {
            Assert.Contains("Version 2 --clean removes owned images only", warning, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Re-run with --clean to remove them", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("separate", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Parent registrations remain drift", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("adoption", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("drift", row.GetProperty("reconciliationStatus").GetString());
        }
        else
            Assert.Contains("--clean", warning);
        Assert.Equal(0, row.GetProperty("stepsDeleted").GetInt32());
        fixture.VerifyNoDeletes();
    }

    private static void AssertPlanIntent(JsonElement plan, string path, string field, string intent) =>
        Assert.Contains(plan.EnumerateArray(), item => item.GetProperty("path").GetString() == path &&
            item.GetProperty("field").GetString() == field && item.GetProperty("intent").GetString() == intent);

    [Fact]
    public async Task Version2_CleanOmittedImagesIsRejectedBeforeConnectingOrMutating()
    {
        using var fixture = new CommandFixture("2.0");
        fixture.OmitImages();

        Assert.NotEqual(0, await fixture.RunAsync(clean: true));

        using var json = JsonDocument.Parse(fixture.Output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Validation.InvalidValue", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.ProviderCalls);
        Assert.Empty(fixture.Service.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Version2_CleanPlansBeforeWritesAndPrunesOnlyImages(bool dryRun)
    {
        using var fixture = new CommandFixture("2.0");
        var extra = new PluginImageInfo { Id = Guid.NewGuid(), Name = "Extra", ImageType = "PreImage", StepId = fixture.StoredStep.Id };
        fixture.StoredImages.Add(extra);

        Assert.Equal(0, await fixture.RunAsync(dryRun: dryRun, clean: true));

        using var json = JsonDocument.Parse(fixture.Output.ToString());
        var row = SingleDeployment(json.RootElement);
        var cleanup = row.GetProperty("imageCleanup");
        Assert.Equal(extra.Id.ToString(), cleanup.GetProperty("plan").GetProperty("images")[0].GetProperty("imageId").GetString());
        Assert.Equal(dryRun ? "preview-only" : "completed", cleanup.GetProperty("status").GetString());
        Assert.Equal(dryRun ? 0 : 1, row.GetProperty("imagesDeleted").GetInt32());
        Assert.Equal(0, row.GetProperty("stepsDeleted").GetInt32());
        Assert.DoesNotContain(fixture.Service.Invocations, call => call.Method.Name is "DeleteStepAsync" or "DeletePluginTypeAsync");
        if (dryRun) fixture.VerifyNoDeletes();
        else
        {
            Assert.True(fixture.Events.IndexOf("cleanup-plan") < fixture.Events.IndexOf("write-assembly"));
            Assert.True(fixture.Events.IndexOf("delete-image") > fixture.Events.IndexOf("write-step"));
            Assert.Empty(fixture.StoredImages);
        }
    }

    private static JsonElement SingleDeployment(JsonElement envelope)
    {
        Assert.Equal("1.0", envelope.GetProperty("version").GetString());
        var data = envelope.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        return Assert.Single(data.EnumerateArray());
    }

    private sealed class CommandFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ppds-deploy-verification-{Guid.NewGuid():N}");
        private readonly Guid _assemblyId = Guid.NewGuid();
        private readonly Guid _typeId = Guid.NewGuid();
        private readonly Guid _messageId = Guid.NewGuid();
        private readonly Guid _filterId = Guid.NewGuid();
        public Mock<IPluginRegistrationService> Service { get; } = new(MockBehavior.Strict);
        public StringWriter Output { get; } = new();
        public List<string> Events { get; } = [];
        public List<PluginImageInfo> StoredImages { get; } = [];
        public List<PluginStepInfo> AdditionalStoredSteps { get; } = [];
        public int ProviderCalls { get; private set; }
        public PluginStepInfo StoredStep { get; } = new()
        {
            Id = Guid.NewGuid(), Name = "Update account", Message = "Update", PrimaryEntity = "account",
            Stage = "PostOperation", Mode = "Synchronous", ExecutionOrder = 1, IsEnabled = true,
            Deployment = "ServerOnly", InvocationSource = "Parent", CanBeBypassed = true,
            CanUseReadOnlyConnection = false
        };

        public CommandFixture(string version)
        {
            StoredStep.PluginTypeId = _typeId;
            Directory.CreateDirectory(_directory);
            // Existing DeployPreviewTests uses this local artifact seam: bytes are buffered,
            // passed to the mock, and never loaded or uploaded by these tests.
            File.WriteAllBytes(Path.Combine(_directory, "fixture.dll"), [0x4d, 0x5a]);
            File.WriteAllText(Path.Combine(_directory, "registrations.json"), $$"""
                {"version":"{{version}}","assemblies":[{"name":"LocalFixture","type":"Assembly","path":"fixture.dll",
                  "types":[{"typeName":"Contoso.Handler","steps":[{"name":"Update account","message":"Update",
                    "entity":"account","stage":"PostOperation","mode":"Synchronous","images":[]}]}]}]}
                """);
            Service.Setup(s => s.GetAssemblyByNameAsync("LocalFixture", CancellationToken.None))
                .ReturnsAsync(new PluginAssemblyInfo { Id = _assemblyId, Name = "LocalFixture" });
            Service.Setup(s => s.ListTypesForAssemblyAsync(_assemblyId, CancellationToken.None))
                .ReturnsAsync([new PluginTypeInfo { Id = _typeId, TypeName = "Contoso.Handler" }]);
            Service.Setup(s => s.ListStepsForTypeAsync(_typeId, It.IsAny<PluginListOptions?>(), CancellationToken.None))
                .Callback<Guid, PluginListOptions?, CancellationToken>((_, options, _) =>
                    Events.Add(options?.IncludeHidden == true ? "verify" : "inventory"))
                .ReturnsAsync(() => [StoredStep, .. AdditionalStoredSteps]);
            Service.Setup(s => s.ListImagesForStepAsync(StoredStep.Id, CancellationToken.None)).ReturnsAsync(() => StoredImages);
            Service.Setup(s => s.ListAssembliesAsync("LocalFixture", It.IsAny<PluginListOptions?>(), CancellationToken.None))
                .Callback(() => Events.Add("cleanup-plan"))
                .ReturnsAsync([new PluginAssemblyInfo { Id = _assemblyId, Name = "LocalFixture" }]);
            Service.Setup(s => s.GetAssemblyByIdAsync(_assemblyId, CancellationToken.None))
                .ReturnsAsync(new PluginAssemblyInfo { Id = _assemblyId, Name = "LocalFixture" });
            Service.Setup(s => s.GetPluginTypeByNameOrIdAsync(_typeId.ToString("D"), CancellationToken.None))
                .ReturnsAsync(new PluginTypeInfo { Id = _typeId, TypeName = "Contoso.Handler", AssemblyId = _assemblyId });
            Service.Setup(s => s.GetStepByNameOrIdAsync(StoredStep.Id.ToString("D"), CancellationToken.None)).ReturnsAsync(() => StoredStep);
            Service.Setup(s => s.GetImageByNameOrIdAsync(It.IsAny<string>(), CancellationToken.None))
                .ReturnsAsync((string id, CancellationToken _) => StoredImages.SingleOrDefault(i => i.Id.ToString("D") == id));
            Service.Setup(s => s.DeleteImageAsync(It.IsAny<Guid>(), CancellationToken.None))
                .Callback<Guid, CancellationToken>((id, _) => { Events.Add("delete-image"); StoredImages.RemoveAll(i => i.Id == id); })
                .Returns(Task.CompletedTask);
            Service.Setup(s => s.GetSdkMessageIdAsync("Update", CancellationToken.None)).ReturnsAsync(_messageId);
            Service.Setup(s => s.GetSdkMessageFilterIdAsync(_messageId, "account", null, CancellationToken.None)).ReturnsAsync(_filterId);
            Guid? callingUserId = null;
            Service.Setup(s => s.ResolveRunAsUserAsync(null, CancellationToken.None)).ReturnsAsync(callingUserId);
            Service.Setup(s => s.UpsertAssemblyAsync("LocalFixture", It.Is<byte[]>(b => b.SequenceEqual(new byte[] { 0x4d, 0x5a })), null, CancellationToken.None))
                .Callback(() => Events.Add("write-assembly")).ReturnsAsync(_assemblyId);
            Service.Setup(s => s.UpsertPluginTypeAsync(_assemblyId, "Contoso.Handler", null, CancellationToken.None))
                .Callback(() => Events.Add("write-type")).ReturnsAsync(_typeId);
            Service.Setup(s => s.UpsertStepAsync(_typeId, "pluginType", It.IsAny<PluginStepConfig>(),
                    _messageId, _filterId, null, It.IsAny<StepIdentityResolution?>(), CancellationToken.None))
                .Callback(() => Events.Add("write-step")).ReturnsAsync(StoredStep.Id);
        }

        public void ConfigurePreviewInputs(string ordinaryMarker, string secureMarker)
        {
            var path = Path.Combine(_directory, "registrations.json");
            var config = JsonNode.Parse(File.ReadAllText(path))!;
            var step = config["assemblies"]![0]!["types"]![0]!["steps"]![0]!;
            step["unsecureConfiguration"] = ordinaryMarker;
            step["secureConfiguration"] = secureMarker;
            step["images"] = new JsonArray(new JsonObject { ["name"] = "Before", ["imageType"] = "PreImage" });
            File.WriteAllText(path, config.ToJsonString());
        }

        public void OmitImages()
        {
            var path = Path.Combine(_directory, "registrations.json");
            var config = JsonNode.Parse(File.ReadAllText(path))!;
            config["assemblies"]![0]!["types"]![0]!["steps"]![0]!.AsObject().Remove("images");
            File.WriteAllText(path, config.ToJsonString());
        }

        public void ConfigureEmptyRegistration(string shape)
        {
            var path = Path.Combine(_directory, "registrations.json");
            var config = JsonNode.Parse(File.ReadAllText(path))!;
            var assembly = config["assemblies"]![0]!;
            if (shape == "empty-types") assembly["types"] = new JsonArray();
            else assembly["types"]![0]!["steps"] = new JsonArray();
            File.WriteAllText(path, config.ToJsonString());
        }

        public Task<int> RunAsync(bool dryRun = false, bool clean = false) => DeployCommand.ExecuteAsync(
            new FileInfo(Path.Combine(_directory, "registrations.json")), null, null, null, clean, dryRun,
            new GlobalOptionValues { OutputFormat = OutputFormat.Json }, CancellationToken.None,
            serviceProviderFactory: _ =>
            {
                ProviderCalls++;
                return Task.FromResult(new ServiceCollection()
                    .AddSingleton(Service.Object)
                    .AddSingleton(new Mock<ICustomApiService>(MockBehavior.Strict).Object)
                    .BuildServiceProvider());
            },
            outputWriter: new JsonOutputWriter(Output));

        public void VerifyStoredStateReadAfterWrite()
        {
            Assert.Equal(1, ProviderCalls);
            Assert.Equal(1, Events.Count(e => e == "write-step"));
            Assert.Equal(1, Events.Count(e => e == "verify"));
            Assert.True(Events.IndexOf("verify") > Events.IndexOf("write-step"));
        }

        public void VerifyNoDeletes() => Assert.DoesNotContain(Service.Invocations,
            call => call.Method.Name.StartsWith("Delete", StringComparison.Ordinal));

        public void Dispose()
        {
            Output.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
