using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Services.Plugins;

[Trait("Category", "Unit")]
public sealed class PluginLegacyPreservationServiceTests
{
    // A stored value differing from the 2.0 default for each resettable field. A field added to the
    // normalizer's reset set without an entry here (and a shared stored-value mapping) fails the test.
    private static void SetNonDefault(string field, PluginStepInfo stored)
    {
        switch (field)
        {
            case "filteringAttributes": stored.FilteringAttributes = "name"; break;
            case "unsecureConfiguration": stored.Configuration = "stored"; break;
            case "description": stored.Description = "stored"; break;
            case "runAsUser": stored.ImpersonatingUserId = Guid.NewGuid(); break;
            case "asyncAutoDelete": stored.AsyncAutoDelete = true; break;
            case "canBeBypassed": stored.CanBeBypassed = false; break;
            case "canUseReadOnlyConnection": stored.CanUseReadOnlyConnection = true; break;
            default: throw new Xunit.Sdk.XunitException($"No non-default stored value modelled for resettable step field '{field}'.");
        }
    }

    private static void SetNonDefault(string field, PluginImageInfo stored)
    {
        switch (field)
        {
            case "attributes": stored.Attributes = "name"; break;
            case "description": stored.Description = "stored"; break;
            default: throw new Xunit.Sdk.XunitException($"No non-default stored value modelled for resettable image field '{field}'.");
        }
    }

    private static PluginStepInfo DefaultStep() => new()
    {
        Id = Guid.NewGuid(), Name = "handler", Message = "Update", PrimaryEntity = "account", Stage = "PostOperation",
        Mode = "Asynchronous", ExecutionOrder = 1, IsEnabled = true, InvocationSource = "Parent",
        CanBeBypassed = true, CanUseReadOnlyConnection = false
    };

    private static PluginStepConfig LegacyStep() => new()
    {
        Name = "handler", Message = "Update", Entity = "account", Stage = "PostOperation", Mode = "Asynchronous",
        Images = [new PluginImageConfig { Name = "before", ImageType = "PreImage" }],
        OperationIntent = RegistrationOperationIntent.Deploy
    };

    [Fact]
    [Trait("AC", "AC-135")]
    public void EveryVersion2ResettableField_HasASharedStoredValueMappingAndIsReported()
    {
        Assert.NotEmpty(PluginRegistrationNormalizer.ResettableStepFields);
        foreach (var field in PluginRegistrationNormalizer.ResettableStepFields)
        {
            Assert.True(PluginStoredValues.TryStep(field, DefaultStep(), out _), field);
            var stored = DefaultStep();
            Assert.Empty(PluginLegacyPreservationService.ForStep("p", LegacyStep(), stored)); // defaults: nothing to report
            SetNonDefault(field, stored);
            var item = Assert.Single(PluginLegacyPreservationService.ForStep("p", LegacyStep(), stored));
            Assert.Equal(field, item.Property);
        }
        Assert.NotEmpty(PluginRegistrationNormalizer.ResettableImageFields);
        foreach (var field in PluginRegistrationNormalizer.ResettableImageFields)
        {
            var image = new PluginImageInfo { Name = "before", ImageType = "PreImage", EntityAlias = "before", MessagePropertyName = "Target" };
            Assert.True(PluginStoredValues.TryImage(field, image, out _), field);
            Assert.Empty(PluginLegacyPreservationService.ForImages("p", LegacyStep(), [image]));
            SetNonDefault(field, image);
            var item = Assert.Single(PluginLegacyPreservationService.ForImages("p", LegacyStep(), [image]));
            Assert.Equal(field, item.Property);
        }
    }

    [Fact]
    [Trait("AC", "AC-135")]
    public void TryCollect_FailureOmitsTheAdvisoryInsteadOfThrowing()
    {
        var stored = DefaultStep();
        stored.Description = "stored";
        // A null stored image name cannot come from the real reader; it forces the advisory path to throw.
        var input = new LegacyPreservationInput("p", LegacyStep(), stored, [new PluginImageInfo { Name = null! }]);
        Assert.Null(PluginLegacyPreservationService.TryCollect([input], out var failure));
        Assert.NotNull(failure);
        Assert.Null(PluginLegacyPreservationService.TryCollect([], out failure));
        Assert.Null(failure);
    }

    [Fact]
    [Trait("AC", "AC-135")]
    public void StepPaths_IncludeTheAssemblySoSameNamedStepsStayDistinct()
    {
        var first = new PreservedLegacyValue(PluginLegacyPreservationService.StepPath("A", "T", "S"), "description", "x", "(none)");
        var second = new PreservedLegacyValue(PluginLegacyPreservationService.StepPath("B", "T", "S"), "description", "y", "(none)");
        var lines = PluginLegacyPreservationService.DescribeNotice([first, second], includeValues: false).ToList();
        Assert.Contains("  assembly:A/type:T/step:S: description", lines);
        Assert.Contains("  assembly:B/type:T/step:S: description", lines);
    }
}
