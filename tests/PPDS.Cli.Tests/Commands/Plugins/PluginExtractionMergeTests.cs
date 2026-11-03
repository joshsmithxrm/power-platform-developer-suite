using System.Reflection;
using System.Text.Json;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Services.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

[Trait("Category", "Unit")]
public sealed class PluginExtractionMergeTests
{
    private static PluginStepConfig Step(string? name = "handler", string mode = "Synchronous",
        string? secondary = null, string stage = "PostOperation") =>
        new() { Name = name, Message = "Update", Entity = "account", Stage = stage, Mode = mode, SecondaryEntity = secondary };

    private static PluginAssemblyConfig Assembly(params PluginStepConfig[] steps) => new()
    {
        Name = "Offline", Types = [new PluginTypeConfig { TypeName = "Offline.Handler", Steps = [.. steps] }]
    };

    private static PluginRegistrationConfig Existing(params PluginStepConfig[] steps) => new()
    {
        Assemblies = [Assembly(steps)]
    };

    private static void Merge(PluginAssemblyConfig fresh, PluginRegistrationConfig old)
        => PluginExtractionMergeService.MergeAssemblyConfig(fresh, old);

    [Fact]
    public void CommandCompatibilityWrapper_DelegatesToService()
    {
        var previous = Step(); previous.RunAsUser = "owner@example.invalid";
        var fresh = Step();
        typeof(ExtractCommand).GetMethod("MergeAssemblyConfig", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [Assembly(fresh), Existing(previous)]);
        Assert.Equal(previous.RunAsUser, fresh.RunAsUser);
    }

    [Fact]
    public void ModeSiblings_RetainTheirOwnRunAsAndDescriptions()
    {
        var oldSync = Step("sync"); oldSync.RunAsUser = "CallingUser"; oldSync.Description = "sync description";
        var oldAsync = Step("async", "Asynchronous"); oldAsync.RunAsUser = "d7000000-0000-0000-0000-000000000001";
        oldAsync.Description = "async description"; oldAsync.AsyncAutoDelete = true;
        var sync = Step("sync"); var asyncStep = Step("async", "Asynchronous");
        Merge(Assembly(asyncStep, sync), Existing(oldSync, oldAsync));
        Assert.Equal(oldAsync.RunAsUser, asyncStep.RunAsUser);
        Assert.Equal("async description", asyncStep.Description);
        Assert.True(asyncStep.AsyncAutoDelete);
        Assert.Equal("CallingUser", sync.RunAsUser);
        Assert.Equal("sync description", sync.Description);
        Assert.False(sync.IsSpecified("asyncAutoDelete"));
    }

    [Fact]
    public void SecondaryEntitySiblings_RetainTheirOwnSettings()
    {
        var oldContact = Step("contact", secondary: "contact"); oldContact.RunAsUser = "contact@example.invalid";
        var oldLead = Step("lead", secondary: "lead"); oldLead.RunAsUser = "lead@example.invalid";
        var contact = Step("contact", secondary: "contact"); var lead = Step("lead", secondary: "lead");
        Merge(Assembly(lead, contact), Existing(oldContact, oldLead));
        Assert.Equal("lead@example.invalid", lead.RunAsUser);
        Assert.Equal("contact@example.invalid", contact.RunAsUser);
    }

    [Theory]
    [InlineData("40", "0")]
    [InlineData(" postoperation ", "sync")]
    [InlineData("POSTOPERATION", "SYNCHRONOUS")]
    public void CanonicalIdentityAliases_MergeEquivalentSettings(string stage, string mode)
    {
        var previous = Step(stage: stage, mode: mode); previous.Description = "preserved";
        previous.Entity = " ACCOUNT "; previous.Message = " update ";
        var freshStep = Step();
        Merge(Assembly(freshStep), Existing(previous));
        Assert.Equal("preserved", freshStep.Description);
    }

    [Fact]
    public void DuplicateIdentity_ExactEffectiveNameSelectsCorrectSibling()
    {
        var alpha = Step("alpha"); alpha.RunAsUser = "alpha@example.invalid";
        var beta = Step("beta"); beta.RunAsUser = "beta@example.invalid";
        var freshBeta = Step("beta"); var freshAlpha = Step("alpha");
        Merge(Assembly(freshBeta, freshAlpha), Existing(alpha, beta));
        Assert.Equal(beta.RunAsUser, freshBeta.RunAsUser);
        Assert.Equal(alpha.RunAsUser, freshAlpha.RunAsUser);
    }

    [Fact]
    public void DuplicateIdentity_UnresolvedNamesRefuseArbitraryInheritance()
    {
        var alpha = Step("alpha"); alpha.RunAsUser = "alpha@example.invalid";
        var beta = Step("beta"); beta.RunAsUser = "beta@example.invalid";
        var fresh = Step("renamed");
        var error = Assert.Throws<PpdsException>(() => Merge(Assembly(fresh), Existing(alpha, beta)));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.ErrorCode);
        Assert.False(fresh.IsSpecified("runAsUser"));
    }

    [Fact]
    public void DuplicateIdentity_DuplicateEffectiveNameRefusesArbitraryInheritance()
    {
        var first = Step(); first.RunAsUser = "first@example.invalid";
        var second = Step(); second.RunAsUser = "second@example.invalid";
        var fresh = Step();
        Assert.Throws<PpdsException>(() => Merge(Assembly(fresh), Existing(first, second)));
        Assert.False(fresh.IsSpecified("runAsUser"));
    }

    [Fact]
    public void DuplicateIdentity_GeneratedAndExplicitEffectiveNamesDisambiguate()
    {
        var other = Step("other"); other.RunAsUser = "other@example.invalid";
        var generated = Step(null); generated.RunAsUser = "generated@example.invalid";
        var fresh = Step("Offline.Handler: Update of account");
        Merge(Assembly(fresh), Existing(other, generated));
        Assert.Equal(generated.RunAsUser, fresh.RunAsUser);
    }

    [Fact]
    public void DuplicateIdentity_ConsumedSourceIsNotReusedForNewSibling()
    {
        var previous = Step("existing"); previous.RunAsUser = "owner@example.invalid";
        var matched = Step("existing"); var added = Step("new");
        Merge(Assembly(added, matched), Existing(previous));
        Assert.Equal(previous.RunAsUser, matched.RunAsUser);
        Assert.False(added.IsSpecified("runAsUser"));
    }

    [Fact]
    public void LateAmbiguity_DoesNotPartiallyApplyEarlierInheritance()
    {
        var alpha = Step("alpha"); alpha.RunAsUser = "alpha@example.invalid";
        var beta = Step("beta"); beta.RunAsUser = "beta@example.invalid";
        var matched = Step("alpha"); var ambiguous = Step("renamed");
        var fresh = Assembly(matched, ambiguous);
        var existing = Existing(alpha, beta);
        existing.Assemblies[0].Solution = "old-solution";
        Assert.Throws<PpdsException>(() => Merge(fresh, existing));
        Assert.Null(fresh.Solution);
        Assert.False(matched.IsSpecified("runAsUser"));
        Assert.False(ambiguous.IsSpecified("runAsUser"));
    }

    [Fact]
    public void UniqueIdentity_RenamePreservesSettings()
    {
        var previous = Step("old"); previous.RunAsUser = "owner@example.invalid";
        var fresh = Step("new");
        Merge(Assembly(fresh), Existing(previous));
        Assert.Equal("new", fresh.Name);
        Assert.Equal(previous.RunAsUser, fresh.RunAsUser);
    }

    [Fact]
    public void UnspecifiedOnBothSides_DoesNotResurrectNullPresence()
    {
        var fresh = Step();
        Merge(Assembly(fresh), Existing(Step()));
        foreach (var field in new[] { "deployment", "runAsUser", "description", "asyncAutoDelete" })
            Assert.False(fresh.IsSpecified(field), field);
        var json = PluginRegistrationJson.SerializeForOutput(new PluginRegistrationConfig { Assemblies = [Assembly(fresh)] });
        Assert.DoesNotContain("\"runAsUser\"", json);
        Assert.DoesNotContain("\"description\"", json);
    }

    [Fact]
    public void ResetAndFreshValuePrecedence_SurvivesMerge()
    {
        var previous = Step(); previous.RunAsUser = "old@example.invalid"; previous.Description = "old";
        previous.ResetFields.Add("asyncAutoDelete");
        previous.Images.Add(new() { Name = "pre", ResetFields = ["description"] });
        var fresh = Step(); fresh.RunAsUser = null; fresh.ResetFields.Add("description"); fresh.AsyncAutoDelete = false;
        var image = new PluginImageConfig { Name = "pre", ImageType = "PreImage" }; fresh.Images.Add(image);
        Merge(Assembly(fresh), Existing(previous));
        Assert.True(fresh.IsSpecified("runAsUser")); Assert.Null(fresh.RunAsUser);
        Assert.False(fresh.IsSpecified("description")); Assert.Contains("description", fresh.ResetFields);
        Assert.False(fresh.AsyncAutoDelete); Assert.DoesNotContain("asyncAutoDelete", fresh.ResetFields);
        Assert.Contains("description", image.ResetFields);
        Assert.False(image.IsSpecified("description"));
    }

    [Fact]
    public void UnknownMetadata_IsPreservedAtEveryMatchedLevel()
    {
        var old = Existing(Step());
        old.Assemblies[0].ExtensionData = Metadata("assembly", "value");
        old.Assemblies[0].Types[0].ExtensionData = Metadata("type", "value");
        old.Assemblies[0].Types[0].Steps[0].ExtensionData = Metadata("step", "value");
        old.Assemblies[0].Types[0].Steps[0].Images.Add(new() { Name = "pre", ExtensionData = Metadata("image", "value") });
        var step = Step(); step.Images.Add(new() { Name = "pre", ImageType = "PreImage" });
        var fresh = Assembly(step);
        Merge(fresh, old);
        Assert.Equal("value", fresh.ExtensionData!["assembly"].GetString());
        Assert.Equal("value", fresh.Types[0].ExtensionData!["type"].GetString());
        Assert.Equal("value", step.ExtensionData!["step"].GetString());
        Assert.Equal("value", step.Images[0].ExtensionData!["image"].GetString());
    }

    [Fact]
    public void FreshMetadataWinsWithoutDroppingPriorUnknownKeys()
    {
        var previous = Step(); previous.ExtensionData = Metadata("prior", "keep");
        previous.ExtensionData["shared"] = JsonSerializer.SerializeToElement("old");
        var fresh = Step(); fresh.ExtensionData = Metadata("shared", "fresh");
        Merge(Assembly(fresh), Existing(previous));
        Assert.Equal("keep", fresh.ExtensionData!["prior"].GetString());
        Assert.Equal("fresh", fresh.ExtensionData["shared"].GetString());
        fresh.ExtensionData["prior"] = JsonSerializer.SerializeToElement("changed");
        Assert.Equal("keep", previous.ExtensionData["prior"].GetString());
    }

    [Fact]
    public void ImageIdentity_ExactNameAvoidsWrongCaseSiblingMetadata()
    {
        var previous = Step();
        previous.Images.Add(new() { Name = "Pre", ExtensionData = Metadata("owner", "upper") });
        previous.Images.Add(new() { Name = "pre", ExtensionData = Metadata("owner", "lower") });
        var fresh = Step(); fresh.Images.Add(new() { Name = "pre", ImageType = "PreImage" });
        Merge(Assembly(fresh), Existing(previous));
        Assert.Equal("lower", fresh.Images[0].ExtensionData!["owner"].GetString());
    }

    [Fact]
    public void DuplicateImageName_RefusesArbitraryResetInheritance()
    {
        var previous = Step();
        previous.Images.Add(new() { Name = "pre", ResetFields = ["attributes"] });
        previous.Images.Add(new() { Name = "pre", ResetFields = ["description"] });
        var fresh = Step(); fresh.Images.Add(new() { Name = "pre", ImageType = "PreImage" });
        Assert.Throws<PpdsException>(() => Merge(Assembly(fresh), Existing(previous)));
        Assert.Empty(fresh.Images[0].ResetFields);
    }

    private static Dictionary<string, JsonElement> Metadata(string name, string value) =>
        new() { [name] = JsonSerializer.SerializeToElement(value) };

    [Theory]
    [InlineData("""{"assemblies":null}""")]
    [InlineData("""{"assemblies":[null]}""")]
    [InlineData("""{"assemblies":[{"name":null,"types":[]}]}""")]
    [InlineData("""{"assemblies":[{"name":" ","types":[]}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":null}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":[null]}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":[{"typeName":null,"steps":[]}]}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":[{"typeName":"Offline.Handler","steps":null}]}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":[{"typeName":"Offline.Handler","steps":[null]}]}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":[{"typeName":"Offline.Handler","steps":[{"message":null}]}]}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":[{"typeName":"Offline.Handler","steps":[{"message":"Update","images":null}]}]}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":[{"typeName":"Offline.Handler","steps":[{"message":"Update","images":[null]}]}]}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":[{"typeName":"Offline.Handler","steps":[{"message":"Update","images":[{"name":null}]}]}]}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":[{"typeName":"Offline.Handler","steps":[{"message":"Update","resetFields":null}]}]}]}""")]
    [InlineData("""{"assemblies":[{"name":"Offline","types":[{"typeName":"Offline.Handler","steps":[{"message":"Update","resetFields":[null]}]}]}]}""")]
    public void MalformedExistingJson_ReturnsSafeValidationError(string json)
    {
        var previous = JsonSerializer.Deserialize<PluginRegistrationConfig>(json)!;
        var fresh = Assembly(Step());
        var error = Assert.Throws<PpdsException>(() => Merge(fresh, previous));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.ErrorCode);
        Assert.Equal(ExitCodes.InvalidArguments, ExceptionMapper.ToExitCode(error));
        Assert.False(fresh.Types[0].Steps[0].IsSpecified("runAsUser"));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("assemblyName")]
    [InlineData("types")]
    [InlineData("type")]
    [InlineData("typeName")]
    [InlineData("steps")]
    [InlineData("step")]
    [InlineData("stepName")]
    [InlineData("message")]
    [InlineData("images")]
    [InlineData("image")]
    [InlineData("imageName")]
    [InlineData("stepResets")]
    [InlineData("imageResets")]
    public void MalformedFreshModel_ReturnsSafeValidationError(string part)
    {
        var fresh = Assembly(Step());
        fresh.Types[0].Steps[0].Images.Add(new() { Name = "pre", ImageType = "PreImage" });
        var step = fresh.Types[0].Steps[0];
        switch (part)
        {
            case "root": fresh = null!; break;
            case "assemblyName": fresh.Name = null!; break;
            case "types": fresh.Types = null!; break;
            case "type": fresh.Types[0] = null!; break;
            case "typeName": fresh.Types[0].TypeName = " "; break;
            case "steps": fresh.Types[0].Steps = null!; break;
            case "step": fresh.Types[0].Steps[0] = null!; break;
            case "stepName": step.Name = " "; break;
            case "message": step.Message = null!; break;
            case "images": step.Images = null!; break;
            case "image": step.Images[0] = null!; break;
            case "imageName": step.Images[0].Name = " "; break;
            case "stepResets": step.ResetFields = null!; break;
            case "imageResets": step.Images[0].ResetFields = null!; break;
        }
        var error = Assert.Throws<PpdsException>(() => Merge(fresh, Existing(Step())));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.ErrorCode);
        Assert.DoesNotContain("NullReferenceException", error.UserMessage);
    }

    [Fact]
    public void InvalidExistingRoot_ReturnsStructuredValidation()
    {
        var error = Assert.Throws<PpdsException>(() => Merge(Assembly(Step()), null!));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.ErrorCode);
    }

    [Fact]
    public void UnexpectedMetadataAccessFailure_IsWrappedWithoutValues()
    {
        var previous = Existing(Step());
        using (var document = JsonDocument.Parse("\"DO-NOT-ECHO\""))
            previous.Assemblies[0].ExtensionData = new() { ["customMetadata"] = document.RootElement };
        var error = Assert.Throws<PpdsException>(() => Merge(Assembly(Step()), previous));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.ErrorCode);
        Assert.DoesNotContain("DO-NOT-ECHO", error.UserMessage);
    }
}
