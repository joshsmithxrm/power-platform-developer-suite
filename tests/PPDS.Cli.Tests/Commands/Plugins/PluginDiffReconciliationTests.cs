using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Moq;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Plugins;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

[Trait("Category", "Unit")]
public sealed class PluginDiffReconciliationTests
{
    internal static (PluginAssemblyConfig Config, PluginStepConfig Step, PluginStepInfo Env,
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
        Guid? unresolvedId = null;
        service.Setup(s => s.GetSdkMessageIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(unresolvedId);
        service.Setup(s => s.ResolveRunAsUserAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(unresolvedId);
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

    [Theory]
    [InlineData("name", "new-handler", "handler")]
    [InlineData("executionOrder", "7", "1")]
    [InlineData("enabled", "false", "true")]
    [InlineData("deployment", "Both", "ServerOnly")]
    [InlineData("invocationSource", "Internal", "Parent")]
    [InlineData("filteringAttributes", "name,telephone1", "oldattribute")]
    [InlineData("unsecureConfiguration", "(redacted)", "(redacted)")]
    [InlineData("description", "new description", "old description")]
    [InlineData("runAsUser", "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222")]
    [InlineData("asyncAutoDelete", "true", "false")]
    [InlineData("canBeBypassed", "false", "true")]
    [InlineData("canUseReadOnlyConnection", "true", "false")]
    [InlineData("imageType", "PostImage", "PreImage")]
    [InlineData("attributes", "telephone1", "name")]
    [InlineData("entityAlias", "alias", "before")]
    [InlineData("messagePropertyName", "Id", "Target")]
    [InlineData("imageDescription", "new description", "old description")]
    public async Task RealCommand_EachManagedFieldDifference_ReportsExpectedActualAndDriftExit(
        string field, string expected, string actual)
    {
        var f = Fixture();
        switch (field)
        {
            case "name": f.Step.Name = "new-handler"; break;
            case "executionOrder": f.Step.ExecutionOrder = 7; break;
            case "enabled": f.Step.Enabled = false; break;
            case "deployment": f.Step.Deployment = "2"; break;
            case "invocationSource": f.Step.InvocationSource = "-1"; break;
            case "filteringAttributes":
                f.Step.FilteringAttributes = " telephone1,NAME,name ";
                f.Env.FilteringAttributes = "oldattribute";
                break;
            case "unsecureConfiguration":
                f.Step.UnsecureConfiguration = "PRIVATE-EXPECTED";
                f.Env.Configuration = "PRIVATE-ACTUAL";
                break;
            case "description": f.Step.Description = expected; f.Env.Description = actual; break;
            case "runAsUser":
                f.Step.RunAsUser = "operator@example.invalid";
                f.Env.ImpersonatingUserId = Guid.Parse(actual);
                f.Service.Setup(s => s.ResolveRunAsUserAsync(f.Step.RunAsUser, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Guid.Parse(expected));
                break;
            case "asyncAutoDelete":
                f.Step.Mode = f.Env.Mode = "Asynchronous";
                f.Step.AsyncAutoDelete = true;
                f.Env.AsyncAutoDelete = false;
                break;
            case "canBeBypassed": f.Step.CanBeBypassed = false; break;
            case "canUseReadOnlyConnection": f.Step.CanUseReadOnlyConnection = true; break;
            case "imageType": f.Image.ImageType = "1"; break;
            case "attributes": f.Image.Attributes = " TELEPHONE1,telephone1 "; break;
            case "entityAlias": f.Image.EntityAlias = expected; break;
            case "messagePropertyName": f.Image.MessagePropertyName = expected; break;
            case "imageDescription": f.Image.Description = expected; f.EnvImage.Description = actual; break;
        }

        var input = JsonSerializer.Serialize(new PluginRegistrationConfig { Version = "2.0", Assemblies = [f.Config] });
        var (exit, output, error) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(1, exit);
        Assert.Empty(error);
        Assert.DoesNotContain("PRIVATE-EXPECTED", output);
        Assert.DoesNotContain("PRIVATE-ACTUAL", output);
        using var document = JsonDocument.Parse(output);
        var metadata = document.RootElement.GetProperty("metadata");
        Assert.Equal("drift", metadata.GetProperty("status").GetString());
        Assert.True(metadata.GetProperty("isComplete").GetBoolean());
        Assert.True(metadata.GetProperty("hasDrift").GetBoolean());
        var assembly = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.Empty(assembly.GetProperty("missingSteps").EnumerateArray());
        Assert.Empty(assembly.GetProperty("orphanedSteps").EnumerateArray());
        Assert.Empty(assembly.GetProperty("missingImages").EnumerateArray());
        Assert.Empty(assembly.GetProperty("orphanedImages").EnumerateArray());
        var change = Assert.Single(assembly.GetProperty("modifiedSteps").EnumerateArray()
            .Concat(assembly.GetProperty("modifiedImages").EnumerateArray())
            .SelectMany(modified => modified.GetProperty("changes").EnumerateArray()));
        Assert.Equal(field == "imageDescription" ? "description" : field, change.GetProperty("property").GetString());
        Assert.Equal(expected, change.GetProperty("expected").GetString());
        Assert.Equal(actual, change.GetProperty("actual").GetString());
    }

    [Fact]
    [Trait("AC", "AC-138")]
    public async Task OmittedOptionalFields_FilteringAttributesIsComparedAsIn142DescriptionIsNot()
    {
        var f = Fixture();
        f.Env.FilteringAttributes = "telephone1";
        f.Env.Description = "existing";
        var drift = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);

        // filteringAttributes: compared against none even though omitted (1.4.2 parity) -> drift.
        Assert.True(drift.HasDrift);
        Assert.True(drift.IsComplete);
        var change = Assert.Single(Assert.Single(drift.ModifiedSteps).Changes);
        Assert.Equal(new PropertyChange("filteringAttributes", "(none)", "telephone1"), change);
        Assert.Empty(drift.ModifiedImages);
        Assert.Contains(drift.Coverage.Checked, c => c.Field == "filteringAttributes");
        Assert.DoesNotContain(drift.Coverage.Unmanaged, c => c.Field == "filteringAttributes");
        Assert.Equal(ComparedOmissionKinds.FilterableStep, drift.LegacyComparedOmissions);
        Assert.Equal(PluginLegacyPreservationService.DescribeComparedOmissionWarning(ComparedOmissionKinds.FilterableStep),
            Assert.Single(drift.Warnings));

        // description: still preserved, not compared, advisory only.
        Assert.Contains(drift.Coverage.Unmanaged, c => c.Field == "description");
        Assert.Contains(drift.PreservedLegacyValues!, v => v.Property == "description");
        Assert.DoesNotContain(drift.PreservedLegacyValues!, v => v.Property == "filteringAttributes");
    }

    [Fact]
    [Trait("AC", "AC-138")]
    public async Task OmittedDescription_AloneIsPreservedNotComparedAndNotDrift()
    {
        var f = Fixture();
        f.Env.Description = "existing";
        var drift = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        Assert.False(drift.HasDrift);
        Assert.True(drift.IsComplete);
        Assert.Equal(ComparedOmissionKinds.None, drift.LegacyComparedOmissions);
        Assert.Empty(drift.Warnings);
        Assert.Contains(drift.PreservedLegacyValues!, v => v.Property == "description");
    }

    [Theory]
    [Trait("AC", "AC-138")]
    [InlineData(null, false, "absent")]
    [InlineData("1.0", false, "absent")]
    [InlineData("1.0", false, "null")]
    [InlineData("1.0", false, "empty")]
    [InlineData(null, true, "absent")]
    [InlineData("1.0", true, "absent")]
    [InlineData("1.0", true, "null")]
    [InlineData("1.0", true, "empty")]
    public async Task RealCommand_Version1UnsetAttributeSet_StoredValueIsDriftWithGuidanceOnStderr(string? version, bool onImage, string shape)
    {
        var f = Fixture();
        string property, actual;
        if (onImage)
        {
            f.EnvImage.Attributes = " Telephone1,NAME ";
            (property, actual) = ("attributes", "name,telephone1");
        }
        else
        {
            f.Env.FilteringAttributes = "telephone1,Name";
            (property, actual) = ("filteringAttributes", "name,telephone1");
        }
        f.Env.Description = "stored description"; // other omissions stay advisory
        var kind = onImage ? ComparedOmissionKinds.Image : ComparedOmissionKinds.FilterableStep;
        // Hand-written so "absent" is truly absent (the model serialiser writes explicit nulls).
        var input = FileJson(version, AssemblyJson(
            step: onImage ? "" : Shape("filteringAttributes", shape),
            image: onImage ? Shape("attributes", shape) : NamedImageAttributes));

        var (exit, output, error) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(1, exit);
        Assert.Empty(error);
        using (var document = JsonDocument.Parse(output))
        {
            var root = document.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            var metadata = root.GetProperty("metadata");
            Assert.Equal("drift", metadata.GetProperty("status").GetString());
            Assert.True(metadata.GetProperty("isComplete").GetBoolean());
            Assert.True(metadata.GetProperty("hasDrift").GetBoolean());
            var assembly = Assert.Single(root.GetProperty("data").EnumerateArray());
            var modified = Assert.Single(assembly.GetProperty(onImage ? "modifiedImages" : "modifiedSteps").EnumerateArray());
            Assert.Empty(assembly.GetProperty(onImage ? "modifiedSteps" : "modifiedImages").EnumerateArray());
            var change = Assert.Single(modified.GetProperty("changes").EnumerateArray());
            Assert.Equal(property, change.GetProperty("property").GetString());
            Assert.Equal("(none)", change.GetProperty("expected").GetString());
            Assert.Equal(actual, change.GetProperty("actual").GetString());
            Assert.Equal(PluginLegacyPreservationService.DescribeComparedOmissionWarning(kind),
                Assert.Single(assembly.GetProperty("warnings").EnumerateArray()).GetString());
            // Reported as drift, so not repeated in the advisory; the other omission remains advisory.
            var preserved = assembly.GetProperty("preservedLegacyValues").EnumerateArray()
                .Select(v => v.GetProperty("property").GetString()).ToList();
            Assert.Equal(["description"], preserved);
        }

        // Stdout is pinned by PluginDiffConsoleStdoutTests; the injected writer is unused in text mode.
        (exit, _, error) = await Invoke(f.Service.Object, input, json: false);
        Assert.Equal(1, exit);
        Assert.Contains($"{property}: (none) -> {actual}", error);
        Assert.Contains("Notice: " + PluginLegacyPreservationService.DescribeComparedOmissionGuidance(kind), error);
        Assert.Equal(1, Occurrences(error, PluginLegacyPreservationService.ComparedOmissionIntro));
        Assert.Equal(1, Occurrences(error, PluginLegacyPreservationService.AdoptionGuidance));
        Assert.Equal(!onImage, error.Contains(PluginLegacyPreservationService.FilterableStepOption, StringComparison.Ordinal));
        Assert.Equal(onImage, error.Contains(PluginLegacyPreservationService.ImageOption, StringComparison.Ordinal));
        Assert.DoesNotContain(PluginLegacyPreservationService.OtherStepOption, error);
        Assert.Contains("must be removed too", error);
        Assert.Contains("description stored stored description", error);
        Assert.DoesNotContain($"{property} stored", error);
    }

    [Theory]
    [Trait("AC", "AC-138")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public async Task RealCommand_Version1AbsentAttributeSets_StoredEmptyIsMatch(string? stored)
    {
        var f = Fixture();
        f.Env.FilteringAttributes = stored;
        f.EnvImage.Attributes = stored;
        var input = FileJson(null, AssemblyJson(image: "")); // unversioned; both properties absent
        var (exit, output, error) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(0, exit);
        Assert.Empty(error);
        using (var document = JsonDocument.Parse(output))
        {
            Assert.Equal("match", document.RootElement.GetProperty("metadata").GetProperty("status").GetString());
            Assert.Empty(document.RootElement.GetProperty("data")[0].GetProperty("warnings").EnumerateArray());
        }
        (exit, _, error) = await Invoke(f.Service.Object, input, json: false);
        Assert.Equal(0, exit);
        Assert.DoesNotContain(PluginLegacyPreservationService.ComparedOmissionIntro, error);
    }

    [Fact]
    [Trait("AC", "AC-138")]
    public async Task RealCommand_Version1SetAttributeSets_EqualAfterNormalisationMatch()
    {
        var f = Fixture();
        f.Step.FilteringAttributes = " Telephone1 , NAME,name";
        f.Env.FilteringAttributes = "name,telephone1";
        f.Image.Attributes = "NAME ";
        f.EnvImage.Attributes = "name";
        var input = JsonSerializer.Serialize(new PluginRegistrationConfig { Version = "1.0", Assemblies = [f.Config] });
        var (exit, _, error) = await Invoke(f.Service.Object, input, json: false);
        Assert.Equal(0, exit);
        Assert.DoesNotContain(PluginLegacyPreservationService.ComparedOmissionIntro, error);
        (exit, var output, _) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output);
        Assert.Equal("match", document.RootElement.GetProperty("metadata").GetProperty("status").GetString());
    }

    [Fact]
    [Trait("AC", "AC-138")]
    public async Task RealCommand_Version1NonUpdateStep_StoredFilteringAttributesIsDriftAndGuidanceOffersOnlyAcceptedOptions()
    {
        var f = Fixture();
        f.Env.Message = "Create";
        f.Env.FilteringAttributes = "name";
        f.Service.Setup(s => s.ListImagesForStepAsync(f.Env.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var input = FileJson("1.0", AssemblyJson(message: "Create", images: false));
        var (exit, output, _) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(1, exit);
        using (var document = JsonDocument.Parse(output))
        {
            var assembly = document.RootElement.GetProperty("data")[0];
            var change = Assert.Single(Assert.Single(assembly.GetProperty("modifiedSteps").EnumerateArray())
                .GetProperty("changes").EnumerateArray());
            Assert.Equal(("filteringAttributes", "(none)", "name"), (change.GetProperty("property").GetString(),
                change.GetProperty("expected").GetString(), change.GetProperty("actual").GetString()));
            Assert.Equal(PluginLegacyPreservationService.DescribeComparedOmissionWarning(ComparedOmissionKinds.OtherStep),
                Assert.Single(assembly.GetProperty("warnings").EnumerateArray()).GetString());
        }
        var (textExit, _, error) = await Invoke(f.Service.Object, input, json: false);
        Assert.Equal(1, textExit);
        Assert.Contains(PluginLegacyPreservationService.OtherStepOption, error);
        Assert.DoesNotContain(PluginLegacyPreservationService.FilterableStepOption, error);
        Assert.DoesNotContain("set filteringAttributes in the file", error);

        // The option it does not offer is the one the validator rejects for this message.
        var setToMatch = FileJson("1.0", AssemblyJson(message: "Create", step: ",\"filteringAttributes\":\"name\"", images: false));
        Assert.Equal(3, (await Invoke(f.Service.Object, setToMatch, json: true)).Exit);
        // The option it offers is accepted: the reset is managed (still drift until deploy clears it), no guidance.
        var reset = FileJson("1.0", AssemblyJson(message: "Create", step: ",\"resetFields\":[\"filteringAttributes\"]", images: false));
        (exit, output, _) = await Invoke(f.Service.Object, reset, json: true);
        Assert.Equal(1, exit);
        using var resetDocument = JsonDocument.Parse(output);
        Assert.Empty(resetDocument.RootElement.GetProperty("data")[0].GetProperty("warnings").EnumerateArray());
    }

    [Theory]
    [Trait("AC", "AC-138")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealCommand_Version1ResetFieldsWithStoredValue_IsOneManagedChangeWithoutComparedOmissionGuidance(bool onImage)
    {
        var f = Fixture();
        if (onImage) f.EnvImage.Attributes = "name";
        else f.Env.FilteringAttributes = "telephone1";
        var input = FileJson("1.0", AssemblyJson(
            step: onImage ? "" : ",\"resetFields\":[\"filteringAttributes\"]",
            image: onImage ? ",\"resetFields\":[\"attributes\"]" : NamedImageAttributes));
        var (exit, output, _) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(1, exit);
        using (var document = JsonDocument.Parse(output))
        {
            var assembly = document.RootElement.GetProperty("data")[0];
            var change = Assert.Single(assembly.GetProperty("modifiedSteps").EnumerateArray()
                .Concat(assembly.GetProperty("modifiedImages").EnumerateArray())
                .SelectMany(m => m.GetProperty("changes").EnumerateArray()));
            Assert.Equal(onImage ? ("attributes", "(none)", "name") : ("filteringAttributes", "(none)", "telephone1"),
                (change.GetProperty("property").GetString(), change.GetProperty("expected").GetString(), change.GetProperty("actual").GetString()));
            Assert.Empty(assembly.GetProperty("warnings").EnumerateArray()); // already managed: no compared-omission guidance
        }
        var (textExit, _, error) = await Invoke(f.Service.Object, input, json: false);
        Assert.Equal(1, textExit);
        Assert.DoesNotContain(PluginLegacyPreservationService.ComparedOmissionIntro, error);
    }

    [Theory]
    [Trait("AC", "AC-138")]
    [InlineData(false, "null")]
    [InlineData(false, "empty")]
    [InlineData(true, "null")]
    [InlineData(true, "empty")]
    public async Task RealCommand_Version1ResetFieldsWithExplicitNullOrEmptyProperty_IsStillRejected(bool onImage, string shape)
    {
        // The validator is unchanged: the guidance therefore says to remove the property before listing it.
        var f = Fixture();
        var input = FileJson("1.0", AssemblyJson(
            step: onImage ? "" : Shape("filteringAttributes", shape) + ",\"resetFields\":[\"filteringAttributes\"]",
            image: onImage ? Shape("attributes", shape) + ",\"resetFields\":[\"attributes\"]" : NamedImageAttributes));
        var (exit, output, _) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(3, exit);
        using var document = JsonDocument.Parse(output);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("Validation.InvalidValue", error.GetProperty("code").GetString());
        Assert.Contains("cannot specify both a value and resetFields intent", error.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("AC", "AC-138")]
    public async Task RealCommand_TwoDriftingAssemblies_PrintGuidanceOnceWithTheUnionOfOptions()
    {
        var f = Fixture();
        f.Env.FilteringAttributes = "telephone1";
        f.Service.Setup(s => s.GetAssemblyByNameAsync("OfflineFixture2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PluginAssemblyInfo { Id = Guid.NewGuid(), Name = "OfflineFixture2" });
        var input = FileJson("1.0", AssemblyJson(),
            AssemblyJson(name: "OfflineFixture2", image: ""));  // second assembly also leaves image attributes unset

        var (exit, output, _) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(1, exit);
        using (var document = JsonDocument.Parse(output))
        {
            var warnings = document.RootElement.GetProperty("data").EnumerateArray()
                .Select(a => Assert.Single(a.GetProperty("warnings").EnumerateArray()).GetString()).ToList();
            Assert.Equal([
                PluginLegacyPreservationService.DescribeComparedOmissionWarning(ComparedOmissionKinds.FilterableStep),
                PluginLegacyPreservationService.DescribeComparedOmissionWarning(ComparedOmissionKinds.FilterableStep | ComparedOmissionKinds.Image)
            ], warnings);
        }
        var (textExit, _, error) = await Invoke(f.Service.Object, input, json: false);
        Assert.Equal(1, textExit);
        Assert.Equal(2, Occurrences(error, "[~] Modified step: handler"));
        Assert.Equal(1, Occurrences(error, PluginLegacyPreservationService.ComparedOmissionIntro));
        Assert.Equal(1, Occurrences(error, PluginLegacyPreservationService.FilterableStepOption));
        Assert.Equal(1, Occurrences(error, PluginLegacyPreservationService.ImageOption));
        Assert.Equal(1, Occurrences(error, PluginLegacyPreservationService.AdoptionGuidance));
        Assert.DoesNotContain("[!] " + PluginLegacyPreservationService.ComparedOmissionIntro, error);
    }

    [Fact]
    [Trait("AC", "AC-138")]
    public async Task RealCommand_Version2SameData_IsUnchangedDriftWithoutLegacyGuidance()
    {
        var f = Fixture();
        f.Env.FilteringAttributes = "telephone1";
        f.Image.Attributes = null;
        var input = JsonSerializer.Serialize(new PluginRegistrationConfig { Version = "2.0", Assemblies = [f.Config] });
        var (exit, output, error) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(1, exit);
        Assert.Empty(error);
        using (var document = JsonDocument.Parse(output))
        {
            var assembly = document.RootElement.GetProperty("data")[0];
            Assert.Equal("drift", document.RootElement.GetProperty("metadata").GetProperty("status").GetString());
            var changes = assembly.GetProperty("modifiedSteps").EnumerateArray()
                .Concat(assembly.GetProperty("modifiedImages").EnumerateArray())
                .SelectMany(m => m.GetProperty("changes").EnumerateArray())
                .Select(c => (c.GetProperty("property").GetString(), c.GetProperty("expected").GetString(), c.GetProperty("actual").GetString()))
                .ToList();
            Assert.Equal([("filteringAttributes", "(none)", "telephone1"), ("attributes", "(none)", "name")], changes);
            Assert.Empty(assembly.GetProperty("warnings").EnumerateArray());
            Assert.False(assembly.TryGetProperty("preservedLegacyValues", out _));
        }
        (exit, _, error) = await Invoke(f.Service.Object, input, json: false);
        Assert.Equal(1, exit);
        Assert.DoesNotContain(PluginLegacyPreservationService.ComparedOmissionIntro, error);
        Assert.DoesNotContain("Notice:", error);
    }

    [Fact]
    [Trait("AC", "AC-138")]
    public async Task RealCommand_Version1UnknownPropertyWithComparedOmissionDrift_RemainsIncomplete()
    {
        var f = Fixture();
        f.Env.FilteringAttributes = "telephone1";
        var input = FileJson("1.0", AssemblyJson(step: ",\"unrecognizedSetting\":\"x\""));
        var (exit, output, _) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(ExitCodes.Incomplete, exit);
        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        var metadata = document.RootElement.GetProperty("metadata");
        Assert.Equal("incomplete", metadata.GetProperty("status").GetString());
        Assert.True(metadata.GetProperty("hasDrift").GetBoolean());
        Assert.False(metadata.GetProperty("isComplete").GetBoolean());
    }

    internal const string NamedImageAttributes = ",\"attributes\":\"name\"";

    /// <summary>One fixture assembly as hand-written JSON, so an absent property is truly absent.</summary>
    internal static string AssemblyJson(string name = "OfflineFixture", string step = "", string image = NamedImageAttributes,
        string message = "Update", bool images = true)
    {
        var imageJson = images ? "{\"name\":\"before\",\"imageType\":\"PreImage\"" + image + "}" : "";
        return "{\"name\":\"" + name + "\",\"types\":[{\"typeName\":\"Offline.Handler\",\"steps\":[{\"name\":\"handler\"," +
            "\"message\":\"" + message + "\",\"entity\":\"account\",\"stage\":\"PostOperation\"" + step +
            ",\"images\":[" + imageJson + "]}]}]}";
    }

    internal static string FileJson(string? version, params string[] assemblies) =>
        "{" + (version is null ? "" : "\"version\":\"" + version + "\",") + "\"assemblies\":[" + string.Join(",", assemblies) + "]}";

    internal static string Shape(string property, string shape) => shape switch
    {
        "absent" => "",
        "null" => $",\"{property}\":null",
        "empty" => $",\"{property}\":\"\"",
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    internal static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
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
        f.Step.OperationIntent = RegistrationOperationIntent.Reconcile;
        f.Image.OperationIntent = RegistrationOperationIntent.Reconcile;
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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TOP-SECRET-DO-NOT-PRINT")]
    [Trait("AC", "AC-81")]
    public async Task SecureConfiguration_SuppliedOrOmitted_IsExcludedWithoutMakingComparisonIncomplete(string? secret)
    {
        var f = Fixture();
        f.Step.SecureConfiguration = secret;
        var result = await DiffCommand.ComputeDriftAsync(f.Service.Object, f.Config);
        // Write-only input is reported, never compared, and never makes the comparison incomplete.
        Assert.True(result.IsComplete);
        Assert.False(result.HasDrift);
        Assert.Empty(result.Coverage.Unsupported);
        Assert.Contains(result.Coverage.Excluded, c => c.Field.StartsWith("secureConfiguration"));
        Assert.Equal(secret is not null, result.Coverage.Excluded.Any(c => c.Field.Contains("supplied but not compared")));
        Assert.DoesNotContain("TOP-SECRET-DO-NOT-PRINT", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("1.0", false, 0)]
    [InlineData("1.0", true, 1)]
    [InlineData("2.0", false, 0)]
    [InlineData("2.0", true, 1)]
    [Trait("AC", "AC-134")]
    public async Task RealCommand_SuppliedSecureConfiguration_DoesNotChangeStatusOrExit(string version, bool drift, int expectedExit)
    {
        var f = Fixture();
        if (drift) f.Env.IsEnabled = false;
        var input = JsonSerializer.Serialize(new PluginRegistrationConfig { Version = version, Assemblies = [f.Config] })
            .Replace("\"name\":\"handler\"", "\"name\":\"handler\",\"secureConfiguration\":\"SECRET-SUPPLIED\"");
        foreach (var json in new[] { true, false })
        {
            var (exit, output, error) = await Invoke(f.Service.Object, input, json);
            Assert.Equal(expectedExit, exit);
            Assert.DoesNotContain("SECRET-SUPPLIED", output + error);
            if (json)
            {
                using var document = JsonDocument.Parse(output);
                Assert.True(document.RootElement.GetProperty("success").GetBoolean());
                var metadata = document.RootElement.GetProperty("metadata");
                Assert.True(metadata.GetProperty("isComplete").GetBoolean());
                Assert.Equal(drift ? "drift" : "match", metadata.GetProperty("status").GetString());
                var coverage = document.RootElement.GetProperty("data")[0].GetProperty("coverage");
                Assert.Empty(coverage.GetProperty("unsupported").EnumerateArray());
                Assert.Contains(coverage.GetProperty("excluded").EnumerateArray(), item =>
                    item.GetProperty("field").GetString()!.Contains("secureConfiguration supplied but not compared"));
            }
            else
                Assert.Contains("Excluded: type:Offline.Handler/step:handler: secureConfiguration supplied but not compared", error);
        }
    }

    [Fact]
    [Trait("AC", "AC-135")]
    public async Task LegacyOmittedValues_AreReportedAsPreservedNoticeWithoutChangingExit()
    {
        var f = Fixture();
        // Omitted filteringAttributes and image attributes are compared under 1.0 (1.4.2 parity) and
        // covered by the AC-138 tests; every other omitted optional property is advisory only.
        f.Env.Configuration = "PRIVATE-STORED-CONFIG";
        f.Env.Description = "stored description";
        f.Env.ImpersonatingUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        f.Env.CanBeBypassed = false;
        f.Env.CanUseReadOnlyConnection = true;
        f.EnvImage.Description = "stored image description";
        var input = JsonSerializer.Serialize(new PluginRegistrationConfig { Assemblies = [f.Config] });

        var (exit, output, error) = await Invoke(f.Service.Object, input, json: true);
        Assert.Equal(0, exit);
        Assert.DoesNotContain("PRIVATE-STORED-CONFIG", output + error);
        using (var document = JsonDocument.Parse(output))
        {
            Assert.Equal("match", document.RootElement.GetProperty("metadata").GetProperty("status").GetString());
            var preserved = document.RootElement.GetProperty("data")[0].GetProperty("preservedLegacyValues").EnumerateArray()
                .Select(v => (Path: v.GetProperty("path").GetString(), Property: v.GetProperty("property").GetString(),
                    Stored: v.GetProperty("stored").GetString(), Version2: v.GetProperty("version2Value").GetString())).ToList();
            const string step = "assembly:OfflineFixture/type:Offline.Handler/step:handler";
            Assert.Contains((step, "unsecureConfiguration", "(redacted)", "(redacted)"), preserved);
            Assert.Contains((step, "description", "stored description", "(none)"), preserved);
            Assert.Contains((step, "runAsUser", "33333333-3333-3333-3333-333333333333", "CallingUser"), preserved);
            Assert.Contains((step, "canBeBypassed", "false", "true"), preserved);
            Assert.Contains((step, "canUseReadOnlyConnection", "true", "false"), preserved);
            Assert.Contains((step + "/image:before", "description", "stored image description", "(none)"), preserved);
            Assert.Equal(6, preserved.Count);
        }

        (exit, output, error) = await Invoke(f.Service.Object, input, json: false);
        Assert.Equal(0, exit);
        Assert.Empty(output);
        Assert.Contains("preserved, not managed, under registration version 1.0", error);
        Assert.Contains("description stored stored description (2.0 default (none))", error);
        Assert.DoesNotContain(PluginLegacyPreservationService.ComparedOmissionIntro, error);
        Assert.Contains("unsecureConfiguration stored (redacted)", error);
        Assert.Contains("--registration-version 2.0", error);
        Assert.Contains("\"version\": \"2.0\"", error);
        Assert.Contains("--clean removes only owned extra images", error);
        Assert.DoesNotContain("PRIVATE-STORED-CONFIG", error);
    }

    [Fact]
    [Trait("AC", "AC-135")]
    public async Task LegacyNotice_IsAbsentWhenStoredValuesEqualVersion2DefaultsAndForVersion2Files()
    {
        var f = Fixture();
        f.Image.Attributes = null;
        f.EnvImage.Attributes = null;
        var (exit, _, error) = await Invoke(f.Service.Object,
            JsonSerializer.Serialize(new PluginRegistrationConfig { Assemblies = [f.Config] }), json: false);
        Assert.Equal(0, exit);
        Assert.DoesNotContain("Notice:", error);

        f.Env.Description = "stored description";
        (exit, var output, _) = await Invoke(f.Service.Object,
            JsonSerializer.Serialize(new PluginRegistrationConfig { Version = "2.0", Assemblies = [f.Config] }), json: true);
        Assert.Equal(1, exit); // 2.0 manages the omission: real drift, not a notice
        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.GetProperty("data")[0].TryGetProperty("preservedLegacyValues", out _));
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
    [InlineData("unknown", 12, false)]
    [InlineData("both", 12, true)]
    [InlineData("secret", 0, false)]
    [Trait("AC", "AC-93")]
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
        Assert.Equal(expectedExit != ExitCodes.Incomplete, metadata.GetProperty("isComplete").GetBoolean());
        Assert.Equal(expectedExit == ExitCodes.Incomplete ? "incomplete" : expectedDrift ? "drift" : "match", metadata.GetProperty("status").GetString());
        Assert.Equal(expectedExit != ExitCodes.Incomplete, document.RootElement.GetProperty("success").GetBoolean()); // success=false only when incomplete
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
    [Trait("AC", "AC-93")]
    [InlineData(false, 12)]
    [InlineData(true, 0)]
    public async Task RealCommand_IncludeHidden_ReversesReadExclusion(bool includeHidden, int expectedExit)
    {
        var f = Fixture();
        f.Env.IsHidden = true;
        var (exit, output, _) = await Invoke(f.Service.Object,
            JsonSerializer.Serialize(new PluginRegistrationConfig { Assemblies = [f.Config] }), json: true, includeHidden);
        Assert.Equal(expectedExit, exit);
        using var document = JsonDocument.Parse(output);
        // A hidden step excluded without --include-hidden is a genuinely incomplete comparison.
        Assert.Equal(includeHidden, document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(includeHidden ? "match" : "incomplete",
            document.RootElement.GetProperty("metadata").GetProperty("status").GetString());
        var coverage = document.RootElement.GetProperty("data")[0].GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("totalSteps").GetInt32());
        Assert.Equal(1, coverage.GetProperty("hiddenSteps").GetInt32());
        Assert.Equal(includeHidden ? 0 : 1, coverage.GetProperty("excludedHiddenSteps").GetInt32());
    }

    [Fact]
    public async Task RealCommand_UnexpectedFailure_ExitsFailureNotIncomplete()
    {
        var f = Fixture();
        f.Service.Setup(s => s.GetAssemblyByNameAsync("OfflineFixture", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("synthetic failure"));
        var (exit, output, _) = await Invoke(f.Service.Object,
            JsonSerializer.Serialize(new PluginRegistrationConfig { Assemblies = [f.Config] }), json: true);
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(2, exit);
        using var document = JsonDocument.Parse(output);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("metadata", out _));
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

    internal static async Task<(int Exit, string Output, string Error)> Invoke(
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

/// <summary>
/// Text-mode diff never receives the injected stdout writer, so "nothing on stdout" is only
/// observable on the real process console. Swaps Console.Out/Error, hence the serialized collection.
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class PluginDiffConsoleStdoutTests
{
    [Fact]
    [Trait("AC", "AC-138")]
    public async Task RealCommand_TextMode_ComparedOmissionGuidanceIsOnStderrAndNothingReachesStdout()
    {
        var f = PluginDiffReconciliationTests.Fixture();
        f.Env.FilteringAttributes = "telephone1";
        f.EnvImage.Attributes = "name";
        var input = PluginDiffReconciliationTests.FileJson("1.0", PluginDiffReconciliationTests.AssemblyJson(image: ""));
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var stdout = new StringWriter();
        using var processStderr = new StringWriter();
        int exit;
        string error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(processStderr);
            (exit, _, error) = await PluginDiffReconciliationTests.Invoke(f.Service.Object, input, json: false);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
        Assert.Equal(1, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Contains(PluginLegacyPreservationService.DescribeComparedOmissionGuidance(
            ComparedOmissionKinds.FilterableStep | ComparedOmissionKinds.Image), error);
    }
}
