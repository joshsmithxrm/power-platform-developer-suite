using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Moq;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

[Trait("Category", "Unit")]
public sealed class PluginDiffReconciliationTests
{
    private static (PluginAssemblyConfig Config, PluginStepConfig Step, PluginStepInfo Env,
        PluginImageConfig Image, PluginImageInfo EnvImage, Mock<IPluginRegistrationService> Service) Fixture()
    {
        var image = new PluginImageConfig { Name = "before", ImageType = "PreImage", Attributes = "name" };
        var step = new PluginStepConfig { Name = "handler", Message = "Update", Entity = "account",
            Stage = "PostOperation", Images = [image] };
        var env = new PluginStepInfo { Id = Guid.NewGuid(), Name = "handler", Message = "Update",
            PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous",
            ExecutionOrder = 1, IsEnabled = true, InvocationSource = "Parent",
            CanBeBypassed = true, CanUseReadOnlyConnection = false };
        var envImage = new PluginImageInfo { Name = "before", ImageType = "PreImage",
            Attributes = "name", EntityAlias = "before", MessagePropertyName = "Target" };
        var config = new PluginAssemblyConfig { Name = "OfflineFixture",
            Types = [new PluginTypeConfig { TypeName = "Offline.Handler", Steps = [step] }] };
        var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        service.Setup(s => s.GetAssemblyByNameAsync("OfflineFixture", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginAssemblyInfo { Id = Guid.NewGuid(), Name = "OfflineFixture" });
        service.Setup(s => s.ListTypesForAssemblyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PluginTypeInfo { Id = Guid.NewGuid(), TypeName = "Offline.Handler" }]);
        service.Setup(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([env]);
        service.Setup(s => s.ListImagesForStepAsync(env.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([envImage]);
        service.Setup(s => s.GetSdkMessageIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        service.Setup(s => s.ResolveRunAsUserAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        return (config, step, env, image, envImage, service);
    }

    [Theory]
    [InlineData("deployment")]
    [InlineData("enabled")]
    [InlineData("description")]
    [InlineData("entityAlias")]
    [InlineData("messagePropertyName")]
    public async Task ManagedProperties_ReportDifferences(string property)
    {
        var f = Fixture();
        switch (property)
        {
            case "deployment": f.Env.Deployment = "Both"; break;
            case "enabled": f.Env.IsEnabled = false; break;
            case "description": f.Step.Description = "desired"; f.Env.Description = "actual"; break;
            case "entityAlias": f.EnvImage.EntityAlias = "other"; break;
            case "messagePropertyName": f.EnvImage.MessagePropertyName = "other"; break;
        }
        var result = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.True(result.HasDrift);
        Assert.Contains(result.ModifiedSteps.SelectMany(s => s.Changes)
            .Concat(result.ModifiedImages.SelectMany(i => i.Changes)), c => c.Property == property);
    }

    [Fact]
    public async Task OmittedOptionalFields_PreserveAndDoNotCompare()
    {
        var f = Fixture();
        f.Env.FilteringAttributes = "telephone1";
        f.Env.Description = "existing";
        Assert.False((await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config)).HasDrift);
    }

    [Fact]
    public async Task AttributeSets_DeduplicateAndNormalize()
    {
        var f = Fixture();
        f.Step.FilteringAttributes = " NAME,name,telephone1 ";
        f.Env.FilteringAttributes = "telephone1,name";
        Assert.False((await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config)).HasDrift);
    }

    [Fact]
    public async Task NumericImageAlias_UsesWriteCanonicalization()
    {
        var f = Fixture();
        f.Image.ImageType = "0";
        Assert.False((await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config)).HasDrift);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("executionOrder")]
    [InlineData("enabled")]
    [InlineData("deployment")]
    [InlineData("invocationSource")]
    [InlineData("filteringAttributes")]
    [InlineData("unsecureConfiguration")]
    [InlineData("description")]
    [InlineData("asyncAutoDelete")]
    [InlineData("canBeBypassed")]
    [InlineData("canUseReadOnlyConnection")]
    [InlineData("imageType")]
    [InlineData("attributes")]
    [InlineData("entityAlias")]
    [InlineData("messagePropertyName")]
    [InlineData("imageDescription")]
    public async Task ManagedFieldEquality_IsCheckedAndComplete(string field)
    {
        var f = Fixture();
        var expectedField = field;
        switch (field)
        {
            case "name": break;
            case "executionOrder": f.Step.ExecutionOrder = f.Env.ExecutionOrder = 7; break;
            case "enabled": f.Step.Enabled = f.Env.IsEnabled = false; break;
            case "deployment": f.Step.Deployment = "0"; break;
            case "invocationSource": f.Step.InvocationSource = "-1"; f.Env.InvocationSource = "Internal"; break;
            case "filteringAttributes": f.Step.FilteringAttributes = " name,NAME "; f.Env.FilteringAttributes = "name"; break;
            case "unsecureConfiguration": f.Step.UnsecureConfiguration = f.Env.Configuration = " meaningful whitespace "; break;
            case "description": f.Step.Description = f.Env.Description = "description"; break;
            case "asyncAutoDelete": f.Step.Mode = f.Env.Mode = "Asynchronous"; f.Step.AsyncAutoDelete = f.Env.AsyncAutoDelete = true; break;
            case "canBeBypassed": f.Step.CanBeBypassed = f.Env.CanBeBypassed = false; break;
            case "canUseReadOnlyConnection": f.Step.CanUseReadOnlyConnection = f.Env.CanUseReadOnlyConnection = true; break;
            case "imageType": f.Image.ImageType = "0"; break;
            case "attributes": f.Image.Attributes = " NAME, name "; break;
            case "entityAlias": f.Image.EntityAlias = null; break;
            case "messagePropertyName": f.Image.MessagePropertyName = null; break;
            case "imageDescription": f.Image.Description = f.EnvImage.Description = "description"; expectedField = "description"; break;
        }
        var result = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.False(result.HasDrift);
        Assert.True(result.IsComplete);
        Assert.Contains(result.Coverage.Checked, c => c.Field == expectedField);
    }

    [Fact]
    public async Task IdentityAliases_UseSharedCanonicalState()
    {
        var f = Fixture();
        f.Step.Stage = " preoperation ";
        f.Step.Mode = "async";
        f.Env.Stage = "PreOperation";
        f.Env.Mode = "Asynchronous";
        var result = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.False(result.HasDrift);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task KnownMetadataAndScope_DoNotInventServerEquality()
    {
        var f = Fixture();
        f.Step.StepId = "annotation-association";
        f.Config.PackageContentSha256 = "metadata-only";
        f.Config.Solution = "configured-solution";
        var result = await new PluginDiffService(f.Service.Object).CompareAsync(new PluginRegistrationConfig
        {
            Schema = "registrations.schema.json", GeneratedAt = DateTimeOffset.UtcNow, Assemblies = [f.Config]
        });
        Assert.True(result.IsComplete);
        Assert.False(result.HasDrift);
        Assert.Contains(result.Coverage.Excluded, c => c.Field.Contains("Custom APIs"));
        Assert.Contains(result.Coverage.Excluded, c => c.Field.Contains("solution membership"));
    }

    [Fact]
    public async Task ExtraImages_AreDriftWithoutIndependentDeletion()
    {
        var f = Fixture();
        f.Step.Images.Clear();
        var result = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.True(result.IsComplete);
        Assert.True(result.HasDrift);
        Assert.Single(result.OrphanedImages);
        // Strict mock authorizes reads only: extra images do not trigger any mutation.
    }

    [Fact]
    public async Task DuplicateImageNames_RetainMissingOrphanedAndUniqueImageDrift()
    {
        var f = Fixture();
        f.Step.Images.Add(new PluginImageConfig { Name = "wanted", ImageType = "PreImage" });
        f.Step.Images.Add(new PluginImageConfig { Name = "duplicate", ImageType = "PreImage" });
        f.EnvImage.Attributes = "telephone1";
        f.Service.Setup(s => s.ListImagesForStepAsync(f.Env.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([f.EnvImage,
                new PluginImageInfo { Name = "duplicate", ImageType = "PreImage" },
                new PluginImageInfo { Name = "duplicate", ImageType = "PostImage" },
                new PluginImageInfo { Name = "extra", ImageType = "PreImage" }]);
        var result = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.False(result.IsComplete);
        Assert.True(result.HasDrift);
        Assert.Contains(result.MissingImages, i => i.ImageName == "wanted");
        Assert.Contains(result.OrphanedImages, i => i.ImageName == "extra");
        Assert.Contains(result.ModifiedImages, i => i.ImageName == "before");
        Assert.DoesNotContain(result.MissingImages, i => i.ImageName == "duplicate");
        Assert.Contains(result.Coverage.Unsupported, i => i.Field.Contains("duplicate image names"));
    }

    [Theory]
    [InlineData("filteringAttributes")]
    [InlineData("unsecureConfiguration")]
    [InlineData("description")]
    [InlineData("runAsUser")]
    [InlineData("asyncAutoDelete")]
    [InlineData("canBeBypassed")]
    [InlineData("canUseReadOnlyConnection")]
    [InlineData("invocationSource")]
    [InlineData("attributes")]
    [InlineData("imageDescription")]
    public async Task ExplicitManagedResets_DetectRemainingServerValues(string field)
    {
        var f = Fixture();
        var expectedProperty = field;
        switch (field)
        {
            case "filteringAttributes": f.Step.FilteringAttributes = null; f.Env.FilteringAttributes = "name"; break;
            case "unsecureConfiguration": f.Step.UnsecureConfiguration = ""; f.Env.Configuration = "PRIVATE-ACTUAL"; break;
            case "description": f.Step.Description = null; f.Env.Description = "old"; break;
            case "runAsUser": f.Step.RunAsUser = "CallingUser"; f.Env.ImpersonatingUserId = Guid.NewGuid(); break;
            case "asyncAutoDelete": f.Step.Mode = f.Env.Mode = "Asynchronous"; f.Step.AsyncAutoDelete = false; f.Env.AsyncAutoDelete = true; break;
            case "canBeBypassed": f.Step.CanBeBypassed = null; f.Env.CanBeBypassed = false; break;
            case "canUseReadOnlyConnection": f.Step.CanUseReadOnlyConnection = null; f.Env.CanUseReadOnlyConnection = true; break;
            case "invocationSource": f.Env.InvocationSource = "Child"; break;
            case "attributes": f.Image.Attributes = null; break;
            case "imageDescription": f.Image.Description = ""; f.EnvImage.Description = "old"; expectedProperty = "description"; break;
        }
        var drift = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.True(drift.IsComplete);
        var change = Assert.Single(drift.ModifiedSteps.SelectMany(s => s.Changes)
            .Concat(drift.ModifiedImages.SelectMany(i => i.Changes)));
        Assert.Equal(expectedProperty, change.Property);
        if (field == "unsecureConfiguration")
        {
            Assert.Equal("(redacted)", change.Expected);
            Assert.Equal("(redacted)", change.Actual);
            Assert.DoesNotContain("PRIVATE-ACTUAL", JsonSerializer.Serialize(drift));
        }
    }

    [Theory]
    [InlineData("same")]
    [InlineData("domain@example.invalid")]
    public async Task RunAsResolvedGuid_EquivalentSelectorsMatch(string selector)
    {
        var f = Fixture();
        var id = Guid.NewGuid();
        f.Step.RunAsUser = selector == "same" ? id.ToString() : selector;
        f.Env.ImpersonatingUserId = id;
        f.Service.Setup(s => s.ResolveRunAsUserAsync(f.Step.RunAsUser, It.IsAny<CancellationToken>())).ReturnsAsync(id);
        var drift = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.False(drift.HasDrift);
        Assert.True(drift.IsComplete);
    }

    [Fact]
    public async Task UnavailableManagedField_IsIncompleteRatherThanEqualDefault()
    {
        var f = Fixture();
        f.Step.CanBeBypassed = true;
        f.Env.UnavailableProperties.Add("canBeBypassed");
        var result = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.False(result.IsComplete);
        Assert.False(result.HasDrift);
        Assert.Contains(result.Coverage.Unsupported, c => c.Field.Contains("canBeBypassed"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", false)]
    [InlineData("TOP-SECRET-DO-NOT-PRINT", false)]
    public async Task SecureConfiguration_UnknownWhenSupplied_AlwaysExcluded(string? secret, bool complete)
    {
        var f = Fixture();
        f.Step.SecureConfiguration = secret;
        var result = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.Equal(complete, result.IsComplete);
        Assert.Contains(result.Coverage.Excluded, c => c.Field.StartsWith("secureConfiguration"));
        Assert.DoesNotContain("TOP-SECRET-DO-NOT-PRINT", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task HiddenInventory_IsCountedAndReversibleWithoutWrites()
    {
        var f = Fixture();
        f.Env.IsHidden = true;
        var service = new PluginDiffService(f.Service.Object);
        var excluded = await service.CompareAssemblyAsync(f.Config);
        Assert.False(excluded.IsComplete);
        Assert.Equal(1, excluded.Coverage.TotalSteps);
        Assert.Equal(1, excluded.Coverage.HiddenSteps);
        Assert.False(excluded.HasDrift);
        Assert.Equal(1, excluded.Coverage.ExcludedHiddenSteps);
        Assert.Equal(0, excluded.Coverage.ComparedSteps);
        var included = await service.CompareAssemblyAsync(f.Config, includeHidden: true);
        Assert.True(included.IsComplete);
        Assert.False(included.HasDrift);
        Assert.Equal(1, included.Coverage.ComparedSteps);
        Assert.Equal(1, included.Coverage.HiddenSteps);
        Assert.Equal(0, included.Coverage.ExcludedHiddenSteps);
        f.Service.Verify(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(),
            It.Is<PluginListOptions>(o => o.IncludeHidden), It.IsAny<CancellationToken>()), Times.Exactly(2));
        // Strict mock has no mutation methods configured: either comparison would throw on a write.
    }

    [Fact]
    public async Task UnreadableIdentity_CannotBecomeCompleteMissingOrphanedResult()
    {
        var f = Fixture();
        f.Env.Stage = "PreOperation";
        f.Env.UnavailableProperties.Add("stage");
        var result = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.True(result.HasDrift);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Coverage.Unsupported, c => c.Field.Contains("stage"));
    }

    [Fact]
    public async Task IdentityCollision_IsIncompleteEvenWhenPositionalPairsMatch()
    {
        var f = Fixture();
        var extra = new PluginStepInfo { Id = Guid.NewGuid(), Name = "other", Message = "Update",
            PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous",
            IsEnabled = true, InvocationSource = "Parent", ExecutionOrder = 1 };
        f.Service.Setup(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([f.Env, extra]);
        var result = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.False(result.IsComplete);
        Assert.True(result.HasDrift);
        Assert.Contains(result.Coverage.Unsupported, c => c.Path == "identity");
    }

    [Theory]
    [InlineData("match", 0, false)]
    [InlineData("drift", 1, true)]
    [InlineData("unknown", 2, false)]
    [InlineData("both", 2, true)]
    [InlineData("secret", 2, false)]
    public async Task RealCommand_JsonAndExitCode_ReportTruthfulStatus(string scenario, int expectedExit, bool expectedDrift)
    {
        var f = Fixture();
        if (scenario is "drift" or "both") f.Env.IsEnabled = false;
        var config = new PluginRegistrationConfig { Assemblies = [f.Config] };
        var input = JsonSerializer.Serialize(config);
        if (scenario is "unknown" or "both")
            input = input.Replace("\"name\":\"handler\"", "\"name\":\"handler\",\"unrecognizedSetting\":\"SECRET-UNKNOWN\"");
        if (scenario == "secret")
            input = input.Replace("\"name\":\"handler\"", "\"name\":\"handler\",\"secureConfiguration\":\"SECRET-SUPPLIED\"");
        var (exit, output, error) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(expectedExit, exit);
        Assert.Empty(error);
        Assert.DoesNotContain("SECRET-UNKNOWN", output);
        Assert.DoesNotContain("SECRET-SUPPLIED", output);
        using var document = JsonDocument.Parse(output);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("1.0", document.RootElement.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal("OfflineFixture", data[0].GetProperty("assemblyName").GetString());
        var metadata = document.RootElement.GetProperty("metadata");
        Assert.Equal(expectedDrift, metadata.GetProperty("hasDrift").GetBoolean());
        Assert.Equal(expectedExit != 2, metadata.GetProperty("isComplete").GetBoolean());
        Assert.Equal(expectedExit == 2 ? "incomplete" : expectedDrift ? "drift" : "match", metadata.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RealCommand_TextMatch_DoesNotClaimWholeEnvironmentEquality()
    {
        var f = Fixture();
        var (exit, output, error) = await Invoke(f.Service.Object,
            JsonSerializer.Serialize(new PluginRegistrationConfig { Assemblies = [f.Config] }), json: false);
        Assert.Equal(0, exit);
        Assert.Empty(output);
        Assert.Contains("Compared managed registration fields match.", error);
        Assert.Contains("Unmanaged:", error);
        Assert.Contains("secureConfiguration", error);
        Assert.DoesNotContain("Environment matches configuration", error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealCommand_UnsecureChange_RedactsAllOutput(bool json)
    {
        var f = Fixture();
        f.Step.UnsecureConfiguration = "PRIVATE-DESIRED";
        f.Env.Configuration = "PRIVATE-ACTUAL";
        var (exit, output, error) = await Invoke(f.Service.Object,
            JsonSerializer.Serialize(new PluginRegistrationConfig { Assemblies = [f.Config] }), json);
        Assert.Equal(1, exit);
        Assert.Contains("unsecureConfiguration", output + error);
        Assert.Contains("(redacted)", output + error);
        Assert.DoesNotContain("PRIVATE-DESIRED", output + error);
        Assert.DoesNotContain("PRIVATE-ACTUAL", output + error);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public async Task RealCommand_IncludeHidden_ReversesReadExclusion(bool includeHidden, int expectedExit)
    {
        var f = Fixture();
        f.Env.IsHidden = true;
        var (exit, output, _) = await Invoke(f.Service.Object,
            JsonSerializer.Serialize(new PluginRegistrationConfig { Assemblies = [f.Config] }), json: true, includeHidden);
        Assert.Equal(expectedExit, exit);
        using var document = JsonDocument.Parse(output);
        var coverage = document.RootElement.GetProperty("data")[0].GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("totalSteps").GetInt32());
        Assert.Equal(1, coverage.GetProperty("hiddenSteps").GetInt32());
        Assert.Equal(includeHidden ? 0 : 1, coverage.GetProperty("excludedHiddenSteps").GetInt32());
    }

    [Theory]
    [InlineData("Auth.InvalidCredentials", 5)]
    [InlineData("Connection.Failed", 4)]
    [InlineData("Validation.InvalidValue", 3)]
    public async Task RealCommand_SpecificErrorsRetainPrecedence(string code, int expectedExit)
    {
        var f = Fixture();
        f.Service.Setup(s => s.GetAssemblyByNameAsync("OfflineFixture", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PpdsException(code, "Safe failure"));
        var (exit, output, _) = await Invoke(f.Service.Object,
            JsonSerializer.Serialize(new PluginRegistrationConfig { Assemblies = [f.Config] }), json: true);
        Assert.Equal(expectedExit, exit);
        using var document = JsonDocument.Parse(output);
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<(int Exit, string Output, string Error)> Invoke(
        IPluginRegistrationService service, string config, bool json, bool includeHidden = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ppds-diff-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, config);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var command = DiffCommand.CreateForTesting(service, output, error);
            var args = new List<string> { "--config", path };
            if (json) args.AddRange(["--output-format", "json"]);
            if (includeHidden) args.Add("--include-hidden");
            var parsed = command.Parse(args.ToArray());
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            return (exit, output.ToString(), error.ToString());
        }
        finally { File.Delete(path); }
    }
}
