using System.Reflection;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Extraction;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

public partial class PluginReconciliationWriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extraction_RetainsResetFieldsUnlessFreshAnnotationSuppliesIntent(bool freshValue)
    {
        var step = Step(); var image = new PluginImageConfig { Name = "pre", ImageType = "PreImage" }; step.Images.Add(image);
        if (freshValue) { step.Description = "fresh"; image.Description = "fresh"; }
        var prior = Step(); prior.ResetFields.Add("description");
        prior.Images.Add(new PluginImageConfig { Name = "pre", ResetFields = ["description"] });
        var fresh = new PluginAssemblyConfig { Name = "test", Types = [new() { TypeName = "Handler", Steps = [step] }] };
        var previous = new PluginRegistrationConfig { Assemblies = [new() { Name = "test", Types = [new() { TypeName = "Handler", Steps = [prior] }] }] };
        typeof(ExtractCommand).GetMethod("MergeAssemblyConfig", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [fresh, previous]);
        Assert.Equal(!freshValue, step.ResetFields.Contains("description"));
        Assert.Equal(!freshValue, image.ResetFields.Contains("description"));
        Assert.Equal(freshValue ? "fresh" : null, PluginRegistrationNormalizer.NormalizeStep(step, RegistrationOperationIntent.Deploy)["description"]);
        Assert.Equal(freshValue ? "fresh" : null, PluginRegistrationNormalizer.NormalizeImage(image, "Update", RegistrationOperationIntent.Deploy)["description"]);

        // Fresh reset intent also prevents a previously supplied value from being resurrected.
        step = Step(); step.ResetFields.Add("description"); prior.ResetFields.Clear(); prior.Description = "stale";
        fresh.Types[0].Steps[0] = step;
        typeof(ExtractCommand).GetMethod("MergeAssemblyConfig", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [fresh, previous]);
        Assert.False(step.IsSpecified("description")); Assert.Null(PluginRegistrationNormalizer.NormalizeStep(step, RegistrationOperationIntent.Deploy)["description"]);
    }

    [Fact]
    public void JsonPresence_RoundTripPreservesResetAndOmission_ExcludesSecurePlaintext()
    {
        var config = JsonSerializer.Deserialize<PluginStepConfig>("""{"name":"test","stage":"PostOperation","description":null,"asyncAutoDelete":false,"secureConfiguration":"DO-NOT-SERIALIZE"}""")!;
        var root = new PluginRegistrationConfig { Version = "2.0", Assemblies = [new() { Types = [new() { Steps = [config] }] }] };
        var json = PluginRegistrationJson.SerializeForOutput(root, false);
        Assert.Contains("\"description\":null", json);
        Assert.Contains("\"asyncAutoDelete\":false", json);
        Assert.DoesNotContain("filteringAttributes", json);
        Assert.DoesNotContain("DO-NOT-SERIALIZE", json);
        Assert.DoesNotContain("secureConfiguration", json);
        var roundTrip = PluginRegistrationJson.Deserialize(json).Assemblies[0].Types[0].Steps[0];
        Assert.True(roundTrip.IsSpecified("description"));
        Assert.False(roundTrip.IsSpecified("filteringAttributes"));
    }

    [Fact]
    public async Task DeployOmission_DoesNotWriteOptionalFields()
    {
        var config = JsonSerializer.Deserialize<PluginStepConfig>("""{"name":"test","message":"Update","stage":"PostOperation","mode":"Asynchronous"}""")!;
        config.OperationIntent = RegistrationOperationIntent.Deploy;
        await _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", config, Guid.NewGuid(), null);
        var write = Assert.Single(_writes);
        foreach (var key in new[] { "filteringattributes", "configuration", "description", "impersonatinguserid", "asyncautodelete", "canbebypassed", "canusereadonlyconnection" })
            Assert.False(write.Contains(key), key);
        Assert.True(write.Contains("supporteddeployment")); Assert.True(write.Contains("invocationsource"));
    }

    [Fact]
    public async Task PartialUpsert_PreservesFlagsStateModeRankAndImageOverrides()
    {
        _existing["statecode"] = new OptionSetValue(1);
        var config = new PluginStepConfig { Name = "test", Message = "Update", Stage = "PostOperation", Description = "", UnsecureConfiguration = null };
        await _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", config, Guid.NewGuid(), null);
        var step = Assert.Single(_writes);
        foreach (var key in new[] { "description", "configuration", "mode", "rank", "supporteddeployment", "invocationsource", "canbebypassed", "asyncautodelete" }) Assert.False(step.Contains(key), key);
        _client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        await _service.UpsertImageAsync(Guid.NewGuid(), new PluginImageConfig { Name = "pre", ImageType = "PreImage" }, "Update");
        foreach (var key in new[] { "entityalias", "messagepropertyname", "attributes", "description" }) Assert.False(_writes[1].Contains(key), key);
    }

    [Fact]
    public async Task ExplicitNullableBooleanNulls_ResetToDocumentedDefaults()
    {
        var config = JsonSerializer.Deserialize<PluginStepConfig>("""{"name":"test","stage":"PostOperation","mode":"Asynchronous","asyncAutoDelete":null,"canBeBypassed":null,"canUseReadOnlyConnection":null}""")!;
        config.OperationIntent = RegistrationOperationIntent.Reconcile;
        await _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", config, Guid.NewGuid(), null);
        var write = Assert.Single(_writes);
        Assert.Equal(false, write["asyncautodelete"]); Assert.Equal(true, write["canbebypassed"]); Assert.Equal(false, write["canusereadonlyconnection"]);
    }

    [Fact]
    public async Task VersionTwoOmission_WritesNonsecretDefaultsWithoutTouchingSecureConfiguration()
    {
        var root = PluginRegistrationJson.Deserialize("""{"version":"2.0","assemblies":[{"types":[{"steps":[{"name":"test","message":"Update","stage":"PostOperation","mode":"Asynchronous"}]}]}]}""");
        var config = root.Assemblies[0].Types[0].Steps[0];
        await _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", config, Guid.NewGuid(), null);
        var write = Assert.Single(_writes);
        foreach (var key in new[] { "filteringattributes", "configuration", "description", "impersonatinguserid" })
        { Assert.True(write.Contains(key), key); Assert.Null(write[key]); }
        Assert.Equal(false, write["asyncautodelete"]); Assert.Equal(true, write["canbebypassed"]); Assert.Equal(false, write["canusereadonlyconnection"]);
        Assert.False(write.Contains("sdkmessageprocessingstepsecureconfigid"));
        _client.Verify(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("System")]
    [InlineData(" SYSTEM ")]
    public async Task UnsupportedSystemSelector_RejectsBeforeAnyWrite(string selector)
    {
        var config = Step(); config.RunAsUser = selector; config.SecureConfiguration = "not-written";
        await Assert.ThrowsAsync<PpdsException>(() => _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", config, Guid.NewGuid(), null));
        Assert.Empty(_writes); Assert.Empty(_reads);
        _client.Verify(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UserResolution_RejectsAmbiguousAndMissing_ValidatesGuid_WithCancellation()
    {
        _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new("systemuser", Guid.NewGuid()), new("systemuser", Guid.NewGuid()) }));
        await Assert.ThrowsAsync<PpdsException>(() => _service.ResolveRunAsUserAsync("someone@example.invalid"));
        _client.Verify(c => c.RetrieveMultipleAsync(It.Is<QueryBase>(q => ((QueryExpression)q).EntityName == "systemuser" && ((QueryExpression)q).TopCount == 2), It.IsAny<CancellationToken>()), Times.Once);
        _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>())).ReturnsAsync(new EntityCollection());
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<PpdsException>(() => _service.ResolveRunAsUserAsync(id.ToString()));
        _client.Verify(c => c.RetrieveMultipleAsync(It.Is<QueryBase>(q => ((QueryExpression)q).Criteria.Conditions.Any(c => c.AttributeName == "systemuserid" && (Guid)c.Values[0] == id)), It.IsAny<CancellationToken>()), Times.Once);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ResolveRunAsUserAsync("CallingUser", cancelled.Token));
        Assert.Empty(_writes);
    }

    [Theory]
    [InlineData("{\"description\":\"value\",\"resetFields\":[\"description\"]}")]
    [InlineData("{\"resetFields\":[\"secureConfiguration\"]}")]
    [InlineData("{\"asyncAutoDelete\":true,\"mode\":\"Synchronous\"}")]
    [InlineData("{\"deployment\":\"unknown\"}")]
    [InlineData("{\"images\":null}")]
    public async Task InvalidIntent_RejectsBeforeAnyWrite(string properties)
    {
        var config = JsonSerializer.Deserialize<PluginStepConfig>(properties)!;
        config.Name = "test"; config.Message = "Update"; config.Stage = "PostOperation";
        await Assert.ThrowsAsync<PpdsException>(() => _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", config, Guid.NewGuid(), null));
        Assert.Empty(_writes); Assert.Empty(_reads);
    }

    [Fact]
    public async Task PartialUpdate_ResetIntentClearsOnlyRequestedFields()
    {
        _existing["mode"] = new OptionSetValue(1);
        await _service.UpdateStepAsync(_existing.Id, new StepUpdateRequest { AsyncAutoDelete = false, ResetFields = ["description", "unsecureConfiguration", "runAsUser"] });
        var step = Assert.Single(_writes);
        Assert.Null(step["description"]); Assert.Null(step["configuration"]); Assert.Null(step["impersonatinguserid"]); Assert.Equal(false, step["asyncautodelete"]);
        foreach (var key in new[] { "rank", "mode", "supporteddeployment" }) Assert.False(step.Contains(key), key);
        await _service.UpdateImageAsync(_existing.Id, new ImageUpdateRequest { ResetFields = ["attributes"] });
        Assert.Null(_writes[1]["attributes"]);
        Assert.False(_writes[1].Contains("description")); Assert.False(_writes[1].Contains("entityalias"));
    }

    [PPDS.Plugins.PluginStep(Message = "Update", EntityLogicalName = "account", Stage = PPDS.Plugins.PluginStage.PostOperation, CanBeBypassed = true, CanUseReadOnlyConnection = false, AsyncAutoDelete = false, Description = null)]
    private sealed class ExplicitDefaults { }

    [Fact]
    public void Extraction_PreservesExplicitDefaultsAndResets_WithoutResurrectingMergedValues()
    {
        var fixture = typeof(ExplicitDefaults);
        var attr = fixture.GetCustomAttributesData().Single(a => a.AttributeType == typeof(PPDS.Plugins.PluginStepAttribute));
        var config = (PluginStepConfig)typeof(AssemblyExtractor).GetMethod("MapStepAttribute", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [attr, fixture])!;
        Assert.True(config.CanBeBypassed); Assert.False(config.CanUseReadOnlyConnection); Assert.False(config.AsyncAutoDelete);
        Assert.True(config.IsSpecified("description")); Assert.Null(config.Description);
        var fresh = new PluginAssemblyConfig { Name = "test", Types = [new PluginTypeConfig { TypeName = "Handler", Steps = [config] }] };
        var previousStep = Step(); previousStep.Description = "old"; previousStep.AsyncAutoDelete = true; previousStep.Mode = config.Mode;
        var previous = new PluginRegistrationConfig { Assemblies = [new PluginAssemblyConfig { Name = "test", Types = [new PluginTypeConfig { TypeName = "Handler", Steps = [previousStep] }] }] };
        typeof(ExtractCommand).GetMethod("MergeAssemblyConfig", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [fresh, previous]);
        Assert.Null(config.Description); Assert.False(config.AsyncAutoDelete);
        var json = JsonSerializer.Serialize(config);
        Assert.Contains("\"canBeBypassed\":true", json); Assert.Contains("\"canUseReadOnlyConnection\":false", json); Assert.Contains("\"description\":null", json);
    }

    [Fact]
    public void NormalizationAndIdentity_UseSameAliasesAndAttributeSets()
    {
        var config = Step(); config.Stage = " preoperation "; config.Mode = "async"; config.FilteringAttributes = " Name,telephone1,name ";
        var plan = PluginRegistrationNormalizer.NormalizeStep(config, RegistrationOperationIntent.Deploy);
        Assert.Equal("PreOperation", plan["stage"]); Assert.Equal("Asynchronous", plan["mode"]); Assert.Equal("name,telephone1", plan["filteringAttributes"]);
        var identity = PluginStepIdentity.FromConfig("Handler", config);
        Assert.Equal("preoperation", identity.Stage); Assert.Equal("asynchronous", identity.Mode);
    }

    [Fact]
    public async Task UserPreflight_ValidatesAllAssembliesWithoutMutation_PropagatesCancellation()
    {
        var registration = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        var config = Step(); config.RunAsUser = "missing@example.invalid";
        var assembly = new PluginAssemblyConfig { Types = [new PluginTypeConfig { Steps = [config] }] };
        using var cts = new CancellationTokenSource();
        registration.Setup(s => s.ResolveRunAsUserAsync(config.RunAsUser, cts.Token)).ThrowsAsync(new PpdsException(ErrorCodes.Plugin.UserNotFound, "Missing user"));
        await Assert.ThrowsAsync<PpdsException>(() => PluginRegistrationPlanningService.ValidateRunAsUsersAsync(registration.Object, [assembly], cts.Token));
        registration.Verify(s => s.ResolveRunAsUserAsync(config.RunAsUser, cts.Token), Times.Once);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PluginRegistrationPlanningService.ValidateRunAsUsersAsync(registration.Object, [assembly], cts.Token));
        registration.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserPreflight_MapsRawFailureWithoutEchoingData_AndPreservesSpecificErrors(bool timeout)
    {
        var registration = new Mock<IPluginRegistrationService>();
        var step = Step(); step.RunAsUser = "test@example.invalid";
        var assemblies = new[] { new PluginAssemblyConfig { Types = [new PluginTypeConfig { Steps = [step] }] } };
        Exception raw = timeout ? new TimeoutException("server-confidential-data") : new InvalidOperationException("server-confidential-data");
        registration.Setup(s => s.ResolveRunAsUserAsync(step.RunAsUser, It.IsAny<CancellationToken>())).ThrowsAsync(raw);
        var mapped = await Assert.ThrowsAsync<PpdsException>(() => PluginRegistrationPlanningService.ValidateRunAsUsersAsync(registration.Object, assemblies, CancellationToken.None));
        Assert.Equal(ExceptionMapper.Map(raw).Code, mapped.ErrorCode);
        Assert.DoesNotContain("server-confidential-data", mapped.UserMessage);
        var specific = new PpdsException(ErrorCodes.Plugin.UserNotFound, "Known failure");
        registration.Setup(s => s.ResolveRunAsUserAsync(step.RunAsUser, It.IsAny<CancellationToken>())).ThrowsAsync(specific);
        Assert.Same(specific, await Assert.ThrowsAsync<PpdsException>(() => PluginRegistrationPlanningService.ValidateRunAsUsersAsync(registration.Object, assemblies, CancellationToken.None)));
        var cancellation = new OperationCanceledException();
        registration.Setup(s => s.ResolveRunAsUserAsync(step.RunAsUser, It.IsAny<CancellationToken>())).ThrowsAsync(cancellation);
        Assert.Same(cancellation, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PluginRegistrationPlanningService.ValidateRunAsUsersAsync(registration.Object, assemblies, CancellationToken.None)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeployUserPreflight_FailureBeforeArtifactUpload_ForPreviewAndReal(bool dryRun)
    {
        var path = Path.Combine(Path.GetTempPath(), "ppds-user-preflight-" + Guid.NewGuid().ToString("N") + ".dll");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        try
        {
            var step = Step(); step.RunAsUser = "missing@example.invalid";
            var assembly = new PluginAssemblyConfig { Name = "Research", Path = path, Types = [new PluginTypeConfig { TypeName = "Handler", Steps = [step] }] };
            var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
            service.Setup(s => s.ResolveRunAsUserAsync(step.RunAsUser, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PpdsException(ErrorCodes.Plugin.UserNotFound, "Missing user"));
            var result = await DeployCommand.DeployAssemblyAsync(service.Object, assembly, Path.GetDirectoryName(path)!, null, false, dryRun,
                new PPDS.Cli.Infrastructure.GlobalOptionValues { OutputFormat = PPDS.Cli.Commands.OutputFormat.Json }, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains("Missing user", result.Error);
            service.Verify(s => s.ResolveRunAsUserAsync(step.RunAsUser, It.IsAny<CancellationToken>()), Times.Once);
            service.VerifyNoOtherCalls();
        }
        finally { File.Delete(path); }
    }
}
