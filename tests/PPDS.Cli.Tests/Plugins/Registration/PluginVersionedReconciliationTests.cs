using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

[Trait("Category", "Unit")]
public sealed class PluginVersionedReconciliationTests
{
    private static PluginRegistrationConfig File(string? version, string properties = "") =>
        PluginRegistrationJson.Deserialize("{" + (version == null ? "" : $"\"version\":\"{version}\",") +
            "\"assemblies\":[{\"name\":\"Offline\",\"types\":[{\"typeName\":\"Handler\",\"steps\":[{\"name\":\"step\",\"message\":\"Update\",\"stage\":\"PostOperation\",\"mode\":\"Asynchronous\"" +
            properties + "}]}]}]}");

    private static PluginStepConfig Step(PluginRegistrationConfig config) => config.Assemblies[0].Types[0].Steps[0];

    [Theory]
    [InlineData(null)]
    [InlineData("1.0")]
    public void LegacyVersions_PreserveOmittedNullAndEmptyOptionalFields(string? version)
    {
        foreach (var properties in new[] { "", ",\"description\":null,\"filteringAttributes\":null,\"unsecureConfiguration\":null,\"runAsUser\":null,\"asyncAutoDelete\":null,\"canBeBypassed\":null", ",\"description\":\"\",\"filteringAttributes\":\"\",\"unsecureConfiguration\":\"\",\"runAsUser\":\"\"" })
        {
            var step = Step(File(version, properties));
            Assert.Equal(RegistrationOperationIntent.Deploy, step.OperationIntent);
            var plan = PluginRegistrationNormalizer.NormalizeStep(step, step.OperationIntent);
            foreach (var key in new[] { "description", "filteringAttributes", "unsecureConfiguration", "runAsUser", "asyncAutoDelete", "canBeBypassed", "canUseReadOnlyConnection" })
                Assert.False(plan.ContainsKey(key), key);
            Assert.Equal("Asynchronous", plan["mode"]);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"description\":null,\"filteringAttributes\":null,\"unsecureConfiguration\":null,\"runAsUser\":null,\"asyncAutoDelete\":null,\"canBeBypassed\":null,\"canUseReadOnlyConnection\":null")]
    public void VersionTwo_OmissionAndNullReconcileDeclaredDefaults(string properties)
    {
        var root = File("2.0", properties);
        var step = Step(root);
        PluginRegistrationPlanningService.ValidateAssembly(root.Assemblies[0], CancellationToken.None);
        Assert.Equal(RegistrationOperationIntent.Reconcile, step.OperationIntent);
        var plan = PluginRegistrationNormalizer.NormalizeStep(step, step.OperationIntent);
        foreach (var key in new[] { "description", "filteringAttributes", "unsecureConfiguration", "runAsUser" }) Assert.Null(plan[key]);
        Assert.Equal(false, plan["asyncAutoDelete"]);
        Assert.Equal(true, plan["canBeBypassed"]);
        Assert.Equal(false, plan["canUseReadOnlyConnection"]);
        var image = new PluginImageConfig { Name = "before", ImageType = "PreImage" };
        step.Images.Add(image);
        PluginRegistrationContract.Apply(root);
        var imagePlan = PluginRegistrationNormalizer.NormalizeImage(image, "Update", image.OperationIntent);
        Assert.Null(imagePlan["attributes"]); Assert.Null(imagePlan["description"]);
        Assert.Equal("before", imagePlan["entityAlias"]); Assert.Equal("Target", imagePlan["messagePropertyName"]);
        step.Mode = "Synchronous";
        Assert.False(PluginRegistrationNormalizer.NormalizeStep(step, step.OperationIntent).ContainsKey("asyncAutoDelete"));
    }

    [Theory]
    [InlineData("{\"version\":\"3.0\"}")]
    [InlineData("{\"version\":null}")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":\"2.0\",\"Version\":\"1.0\"}")]
    [InlineData("{\"assemblies\":null}")]
    [InlineData("{\"assemblies\":[null]}")]
    [InlineData("{\"assemblies\":[{\"types\":[{\"steps\":[null]}]}]}")]
    public void InvalidVersionOrStructure_FailsWithSafeCodedError(string json)
    {
        var error = Assert.Throws<PpdsException>(() => PluginRegistrationJson.Deserialize(json));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.ErrorCode);
    }

    [Fact]
    public void GenericModelRoundTrip_RetainsSecretAndPatchIntent_ExplicitOutputRedacts()
    {
        var config = JsonSerializer.Deserialize<PluginStepConfig>("""{"message":"Update","stage":"PostOperation","secureConfiguration":"PRIVATE-INPUT","description":"legacy"}""")!;
        Assert.Equal(RegistrationOperationIntent.Patch, config.OperationIntent);
        var generic = JsonSerializer.Serialize(config);
        Assert.Contains("PRIVATE-INPUT", generic);
        Assert.Equal("PRIVATE-INPUT", JsonSerializer.Deserialize<PluginStepConfig>(generic)!.SecureConfiguration);
        var root = File("2.0"); root.Assemblies[0].Types[0].Steps[0] = config;
        config.Description = null;
        var output = PluginRegistrationJson.SerializeForOutput(root, false);
        Assert.DoesNotContain("PRIVATE-INPUT", output); Assert.DoesNotContain("secureConfiguration", output);
        Assert.Equal(1, PluginRegistrationJson.CountExcludedSecrets(root));
        var roundTrip = Step(PluginRegistrationJson.Deserialize(output));
        Assert.True(roundTrip.IsSpecified("description")); Assert.Null(roundTrip.Description);
        Assert.False(roundTrip.IsSpecified("filteringAttributes"));
        Assert.Equal(RegistrationOperationIntent.Reconcile, roundTrip.OperationIntent);
        var error = Assert.Throws<PpdsException>(() => PluginRegistrationJson.Deserialize("{\"secureConfiguration\":\"PRIVATE-INPUT\""));
        Assert.DoesNotContain("PRIVATE-INPUT", error.ToString());
    }

    [Theory]
    [InlineData(RegistrationOperationIntent.Patch, false)]
    [InlineData(RegistrationOperationIntent.Deploy, false)]
    [InlineData(RegistrationOperationIntent.Update, true)]
    [InlineData(RegistrationOperationIntent.Reconcile, true)]
    public void EmptyUpdateClears_StaySeparateFromRegisterPlaceholders(RegistrationOperationIntent intent, bool writes)
    {
        var step = new PluginStepConfig { Message = "Create", Stage = "PostOperation", Description = "", FilteringAttributes = "" };
        var plan = PluginRegistrationNormalizer.NormalizeStep(step, intent);
        Assert.Equal(writes, plan.ContainsKey("description")); Assert.Equal(writes, plan.ContainsKey("filteringAttributes"));
        var image = new PluginImageConfig { Attributes = "", Description = "" };
        var imagePlan = PluginRegistrationNormalizer.NormalizeImagePatch(image, intent);
        Assert.Equal(writes, imagePlan.ContainsKey("attributes"));
        if (intent == RegistrationOperationIntent.Update)
        {
            Assert.Equal("", plan["description"]); Assert.Equal("", plan["filteringAttributes"]); Assert.Equal("", imagePlan["attributes"]);
            step.Description = null; image.Attributes = null;
            Assert.False(PluginRegistrationNormalizer.NormalizeStep(step, intent).ContainsKey("description"));
            Assert.False(PluginRegistrationNormalizer.NormalizeImagePatch(image, intent).ContainsKey("attributes"));
        }
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("2.0")]
    public void CallingUserAndFalse_AreExplicitForBothFileVersions(string version)
    {
        var step = Step(File(version, ",\"runAsUser\":\"CallingUser\",\"asyncAutoDelete\":false,\"canBeBypassed\":false"));
        var plan = PluginRegistrationNormalizer.NormalizeStep(step, step.OperationIntent);
        Assert.Null(plan["runAsUser"]); Assert.Equal(false, plan["asyncAutoDelete"]); Assert.Equal(false, plan["canBeBypassed"]);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("2.0", false)]
    public void ExtractionVersion_ControlsLegacyOverlayAndResetInheritance(string version, bool inherits)
    {
        var old = File(version, ",\"description\":\"old\",\"runAsUser\":\"old@example.invalid\",\"asyncAutoDelete\":true,\"resetFields\":[\"filteringAttributes\"],\"images\":[{\"name\":\"pre\",\"imageType\":\"PreImage\",\"resetFields\":[\"description\"]}]");
        var freshRoot = File(version, ",\"images\":[{\"name\":\"pre\",\"imageType\":\"PreImage\"}]");
        var fresh = freshRoot.Assemblies[0]; var step = Step(freshRoot);
        PluginExtractionMergeService.MergeAssemblyConfig(fresh, old);
        Assert.Equal(inherits, step.IsSpecified("description")); Assert.Equal(inherits, step.IsSpecified("runAsUser"));
        Assert.Equal(inherits, step.IsSpecified("asyncAutoDelete"));
        Assert.Equal(inherits, step.ResetFields.Contains("filteringAttributes"));
        Assert.Equal(inherits, step.Images[0].ResetFields.Contains("description"));
        Assert.Equal(version, old.Version);
        var output = PluginRegistrationJson.SerializeForOutput(freshRoot);
        Assert.Equal(version, PluginRegistrationJson.Deserialize(output).Version);
    }

    [Fact]
    public void ExtractionExplicitVersion_CanOptIntoFreshDefaultsWithoutRewritingInput()
    {
        var old = File("1.0", ",\"description\":\"sticky\"");
        var fresh = File("1.0");
        PluginExtractionMergeService.MergeAssemblyConfig(fresh.Assemblies[0], old, "2.0");
        Assert.False(Step(fresh).IsSpecified("description")); Assert.Equal("1.0", old.Version);
        Assert.Contains(ExtractCommand.Create().Options, option => option.Name == "--registration-version");
    }

    [Fact]
    public void UnsupportedExistingVersion_CannotBeBypassedWithExtractionOverride()
    {
        var old = File("1.0", ",\"description\":\"sticky\""); old.Version = "3.0";
        var fresh = File("1.0");
        Assert.Throws<PpdsException>(() => PluginExtractionMergeService.MergeAssemblyConfig(fresh.Assemblies[0], old, "2.0"));
        Assert.False(Step(fresh).IsSpecified("description"));
    }

    [Fact]
    public void GenericUnnamedStepRoundTrip_PreservesGeneratedNaming()
    {
        var input = new PluginStepConfig { Message = "Update", Stage = "PostOperation" };
        var parsed = JsonSerializer.Deserialize<PluginStepConfig>(JsonSerializer.Serialize(input))!;
        Assert.Null(parsed.Name); Assert.Equal(RegistrationOperationIntent.Patch, parsed.OperationIntent);
        var normalized = PluginRegistrationNormalizer.NormalizeStep(parsed, parsed.OperationIntent);
        Assert.Null(normalized["name"]);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("assembly")]
    [InlineData("type")]
    [InlineData("step")]
    [InlineData("image")]
    public void UnknownFields_VersionTwoRejectsAllScopesWithoutStamping_LegacyPreserves(string scope)
    {
        var root = File("1.0", ",\"images\":[{\"name\":\"pre\",\"imageType\":\"PreImage\"}]");
        var step = Step(root);
        var image = step.Images[0];
        var unknown = new Dictionary<string, JsonElement>
        {
            ["SYNTHETIC-UNKNOWN-KEY"] = JsonSerializer.SerializeToElement("SYNTHETIC-PRIVATE-VALUE")
        };
        switch (scope)
        {
            case "root": root.ExtensionData = unknown; break;
            case "assembly": root.Assemblies[0].ExtensionData = unknown; break;
            case "type": root.Assemblies[0].Types[0].ExtensionData = unknown; break;
            case "step": step.ExtensionData = unknown; break;
            case "image": image.ExtensionData = unknown; break;
        }
        var legacyJson = PluginRegistrationJson.SerializeForOutput(root, false);
        Assert.Contains("SYNTHETIC-UNKNOWN-KEY", legacyJson);
        Assert.Contains("SYNTHETIC-PRIVATE-VALUE", legacyJson);
        Assert.Equal(RegistrationOperationIntent.Deploy, PluginRegistrationContract.Apply(PluginRegistrationJson.Deserialize(legacyJson)));

        root.Version = "2.0";
        // A valid earlier sibling must not be stamped before a later unsupported field fails.
        var earlier = new PluginStepConfig { Message = "Update", Stage = "PostOperation" };
        root.Assemblies.Insert(0, new PluginAssemblyConfig { Types = [new() { Steps = [earlier] }] });
        var error = Assert.Throws<PpdsException>(() => PluginRegistrationContract.Apply(root));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.ErrorCode);
        Assert.Contains($"{scope} scope (1 field(s))", error.UserMessage);
        Assert.DoesNotContain("SYNTHETIC-UNKNOWN-KEY", error.ToString());
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-VALUE", error.ToString());
        Assert.Equal(RegistrationOperationIntent.Patch, root.Assemblies[0].OperationIntent);
        Assert.Equal(RegistrationOperationIntent.Deploy, root.Assemblies[1].OperationIntent);
        Assert.Equal(RegistrationOperationIntent.Patch, earlier.OperationIntent);
        Assert.Equal(RegistrationOperationIntent.Deploy, step.OperationIntent);
        Assert.Equal(RegistrationOperationIntent.Deploy, image.OperationIntent);
        Assert.Throws<PpdsException>(() => PluginRegistrationJson.SerializeForOutput(root));
        Assert.Throws<PpdsException>(() => PluginRegistrationJson.Deserialize(legacyJson.Replace("\"version\":\"1.0\"", "\"version\":\"2.0\"")));
    }

    [Fact]
    public void VersionTwo_KnownTypedMetadataAndEmptyExtensionDataRemainSupported()
    {
        var root = File("2.0");
        root.Schema = "registrations.schema.json";
        root.GeneratedAt = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        root.ExtensionData = [];
        root.Assemblies[0].Solution = "example";
        root.Assemblies[0].ExtensionData = [];
        root.Assemblies[0].Types[0].ExtensionData = [];
        Step(root).ExtensionData = [];
        var roundTrip = PluginRegistrationJson.Deserialize(PluginRegistrationJson.SerializeForOutput(root));
        Assert.Equal(root.Schema, roundTrip.Schema);
        Assert.Equal(root.GeneratedAt, roundTrip.GeneratedAt);
        Assert.Equal("example", roundTrip.Assemblies[0].Solution);
        Assert.Equal(RegistrationOperationIntent.Reconcile, Step(roundTrip).OperationIntent);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("assembly")]
    [InlineData("type")]
    [InlineData("step")]
    [InlineData("image")]
    public void ExtractionUpgrade_RejectsUnknownEvenInUnmatchedSourceBeforeMerge(string scope)
    {
        var existing = File("1.0", ",\"description\":\"old\"");
        var unmatched = File("1.0", ",\"images\":[{\"name\":\"pre\",\"imageType\":\"PreImage\"}]").Assemblies[0];
        unmatched.Name = "Unmatched";
        existing.Assemblies.Add(unmatched);
        var unknown = new Dictionary<string, JsonElement> { ["unsupported"] = JsonSerializer.SerializeToElement("PRIVATE") };
        switch (scope)
        {
            case "root": existing.ExtensionData = unknown; break;
            case "assembly": unmatched.ExtensionData = unknown; break;
            case "type": unmatched.Types[0].ExtensionData = unknown; break;
            case "step": unmatched.Types[0].Steps[0].ExtensionData = unknown; break;
            case "image": unmatched.Types[0].Steps[0].Images[0].ExtensionData = unknown; break;
        }
        var fresh = new PluginAssemblyConfig { Name = "Offline", Types = [new() { TypeName = "Handler", Steps = [new() { Name = "step", Message = "Update", Stage = "PostOperation", Mode = "Asynchronous" }] }] };
        var error = Assert.Throws<PpdsException>(() => PluginExtractionMergeService.MergeAssemblyConfig(fresh, existing, "2.0"));
        Assert.DoesNotContain("PRIVATE", error.ToString());
        Assert.False(fresh.Types[0].Steps[0].IsSpecified("description"));
        Assert.Equal(RegistrationOperationIntent.Patch, fresh.OperationIntent);
        Assert.Equal(RegistrationOperationIntent.Patch, fresh.Types[0].Steps[0].OperationIntent);
        Assert.Equal("1.0", existing.Version);
        Assert.Equal(RegistrationOperationIntent.Deploy, existing.Assemblies[0].OperationIntent);
        Assert.Equal(RegistrationOperationIntent.Deploy, unmatched.Types[0].Steps[0].OperationIntent);
    }

    [Fact]
    public void ValidateTargetVersion_DoesNotChangeSourceVersionOrOperationIntents()
    {
        var root = File("1.0");
        Assert.Equal(RegistrationOperationIntent.Reconcile, PluginRegistrationContract.ValidateForVersion(root, "2.0"));
        Assert.Equal("1.0", root.Version);
        Assert.Equal(RegistrationOperationIntent.Deploy, root.Assemblies[0].OperationIntent);
        Assert.Equal(RegistrationOperationIntent.Deploy, Step(root).OperationIntent);
    }

    [Fact]
    public void VersionTwo_EmptyAssembliesRetainFileIntent_GenericRoundTripRemainsPatch()
    {
        var root = PluginRegistrationJson.Deserialize("""{"version":"2.0","assemblies":[{"name":"Empty","types":[]},{"name":"EmptyType","types":[{"typeName":"Handler","steps":[]}]}]}""");
        Assert.All(root.Assemblies, assembly => Assert.Equal(RegistrationOperationIntent.Reconcile, assembly.OperationIntent));
        var generic = JsonSerializer.Serialize(root.Assemblies[0]);
        Assert.DoesNotContain("OperationIntent", generic);
        Assert.Equal(RegistrationOperationIntent.Patch, JsonSerializer.Deserialize<PluginAssemblyConfig>(generic)!.OperationIntent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectReconcileUnknownFields_RejectsBeforeClientCheckout(bool isImage)
    {
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        var service = new PluginRegistrationService(pool.Object, new InactiveFakeShakedownGuard(),
            NullLogger<PluginRegistrationService>.Instance);
        var metadata = new Dictionary<string, JsonElement>
        {
            ["SYNTHETIC-UNKNOWN-KEY"] = JsonSerializer.SerializeToElement("SYNTHETIC-PRIVATE-VALUE")
        };
        var step = new PluginStepConfig { Name = "step", Message = "Update", Stage = "PostOperation",
            OperationIntent = RegistrationOperationIntent.Reconcile, ExtensionData = metadata };
        var image = new PluginImageConfig { Name = "pre", ImageType = "PreImage",
            OperationIntent = RegistrationOperationIntent.Reconcile, ExtensionData = metadata };
        var error = await Assert.ThrowsAsync<PpdsException>(() => isImage
            ? service.UpsertImageAsync(Guid.NewGuid(), image, "Update")
            : service.UpsertStepAsync(Guid.NewGuid(), "pluginType", step, Guid.NewGuid(), null));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.ErrorCode);
        Assert.Contains(isImage ? "image scope (1 field(s))" : "step scope (1 field(s))", error.UserMessage);
        Assert.DoesNotContain("SYNTHETIC", error.ToString());
        // No connection is checked out, so no SDK CRUD or lookup can run.
        pool.VerifyNoOtherCalls();
        foreach (var legacyIntent in new[] { RegistrationOperationIntent.Patch, RegistrationOperationIntent.Deploy, RegistrationOperationIntent.Update })
        {
            if (isImage) _ = PluginRegistrationNormalizer.NormalizeImage(image, "Update", legacyIntent);
            else _ = PluginRegistrationNormalizer.NormalizeStep(step, legacyIntent);
        }
    }
}
