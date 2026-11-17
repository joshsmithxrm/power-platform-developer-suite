using System.Text.Json;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;
using Store = PPDS.Cli.Tests.Commands.Plugins.PluginRoundTripConvergenceTests.InMemoryRegistrationStore;

namespace PPDS.Cli.Tests.Commands.Plugins;

/// <summary>
/// AC-139: deploy (update-versus-create), image pruning, diff, the 2.0 post-deploy verification and the 1.0
/// preserved-value advisory pair a configured image with a stored image by one rule
/// (<see cref="PluginImageNames"/>): names that differ only by case are the same image. Runs the actual
/// command pipelines and step/image service code against the round-trip row store, which evaluates string
/// conditions case-insensitively as Dataverse does by default. This does not prove Dataverse server behavior.
/// </summary>
// The text-mode deploy case swaps Console.Error, so the class joins the console-capture collection.
[Collection(nameof(ConsoleCaptureCollection))]
[Trait("Category", "Unit")]
[Trait("AC", "AC-139")]
public sealed class PluginImageNameMatchingTests
{
    private const string StoredName = "before";

    [Theory]
    [InlineData("2.0")]
    [InlineData("1.0")]
    public async Task CaseOnlyNameDifference_DiffReportsNameChange_DeployUpdatesInPlace_ThenDiffMatches(string version)
    {
        using var store = new Store(version);
        store.SetStoredImageName(StoredName); // configured as "Before"

        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(1, diffExit);
        using (var diff = JsonDocument.Parse(diffOutput))
        {
            Assert.Equal("drift", diff.RootElement.GetProperty("metadata").GetProperty("status").GetString());
            var assembly = diff.RootElement.GetProperty("data")[0];
            Assert.Empty(assembly.GetProperty("missingImages").EnumerateArray());
            Assert.Empty(assembly.GetProperty("orphanedImages").EnumerateArray());
            var modified = Assert.Single(assembly.GetProperty("modifiedImages").EnumerateArray());
            var change = Assert.Single(modified.GetProperty("changes").EnumerateArray());
            Assert.Equal("name", change.GetProperty("property").GetString());
            Assert.Equal(Store.ImageName, change.GetProperty("expected").GetString());
            Assert.Equal(StoredName, change.GetProperty("actual").GetString());
        }
        var (_, text) = await store.DiffAsync(json: false);
        Assert.Contains($"Modified image: {Store.ImageName}", text);
        Assert.DoesNotContain("Missing image", text);
        Assert.DoesNotContain("Orphaned image", text);

        // Preview and real deploy both count an update, not a create.
        Assert.Equal(0, await store.DeployAsync(dryRun: true));
        AssertImageCounts(store.LastDeployOutput, created: 0, updated: 1);
        Assert.Equal(0, await store.DeployAsync());
        AssertImageCounts(store.LastDeployOutput, created: 0, updated: 1);
        if (version == "2.0") AssertVerificationMatched(store.LastDeployOutput);

        // The same row now carries the configured name; nothing was created or deleted.
        Assert.Equal([Store.ImageName], store.StoredImageNames());
        Assert.Equal(0, store.Deletes);

        var (afterExit, afterOutput) = await store.DiffAsync(json: true);
        Assert.Equal(0, afterExit);
        using var after = JsonDocument.Parse(afterOutput);
        Assert.Equal("match", after.RootElement.GetProperty("metadata").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Version2Clean_CaseOnlyNameDifference_IsNeverPlannedForDeletionAndConverges(bool dryRun)
    {
        using var store = new Store("2.0");
        store.SetStoredImageName(StoredName);

        var exit = await store.DeployAsync(clean: true, dryRun: dryRun);
        Assert.True(exit == 0, store.LastDeployOutput);
        using (var deploy = JsonDocument.Parse(store.LastDeployOutput))
        {
            var row = Assert.Single(deploy.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(0, row.GetProperty("imagesCreated").GetInt32());
            Assert.Equal(1, row.GetProperty("imagesUpdated").GetInt32());
            Assert.Empty(row.GetProperty("imageCleanup").GetProperty("plan").GetProperty("images").EnumerateArray());
        }
        Assert.Equal(0, store.Deletes);
        Assert.Single(store.StoredImageNames());

        if (!dryRun)
        {
            AssertVerificationMatched(store.LastDeployOutput);
            Assert.Equal([Store.ImageName], store.StoredImageNames());
            var (diffExit, _) = await store.DiffAsync(json: true);
            Assert.Equal(0, diffExit);
        }
    }

    [Fact]
    public async Task Version1_CaseOnlyNameDifference_PreservedValueAdvisoryStillPairsTheImage()
    {
        using var store = new Store("1.0");
        var spec = PluginRoundTripConvergenceTests.PropertySpec.For("imageDescription", store.UserId);
        store.SetConfigured(spec, include: true);
        Assert.Equal(0, await store.DeployAsync());
        store.SetConfigured(spec, include: false); // 1.0 preserves the stored description
        store.SetStoredImageName(StoredName);

        var (exit, output) = await store.DiffAsync(json: true);
        Assert.Equal(1, exit); // the name change
        using var diff = JsonDocument.Parse(output);
        var preserved = diff.RootElement.GetProperty("data")[0].GetProperty("preservedLegacyValues").EnumerateArray().ToList();
        Assert.Contains(preserved, v => v.GetProperty("path").GetString() == spec.Path(store) &&
            v.GetProperty("property").GetString() == spec.Field);
    }

    [Theory]
    [InlineData("2.0", "Before")]
    [InlineData("2.0", "BEFORE")]
    [InlineData("1.0", "before")]
    public async Task ConfiguredNamesCollidingIgnoringCase_AreRejectedBeforeAnyWrite_ByDeployAndDiff(string version, string second)
    {
        using var store = new Store(version);
        store.AddConfiguredImage(second);

        Assert.Equal(3, await store.DeployAsync());
        Assert.Contains("equal ignoring case", store.LastDeployOutput);
        Assert.Contains(Store.StepName, store.LastDeployOutput);
        Assert.Equal(0, store.Writes); // no assembly upload, type upsert, step or image write
        Assert.Equal(0, store.Connections); // rejected before connecting

        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(3, diffExit);
        Assert.Equal(0, store.Connections);
        using (var diff = JsonDocument.Parse(diffOutput))
        {
            Assert.False(diff.RootElement.TryGetProperty("metadata", out var metadata) && metadata.ValueKind != JsonValueKind.Null,
                "a validation error carries no comparison status");
            var error = diff.RootElement.GetProperty("error");
            Assert.Equal("Validation.InvalidValue", error.GetProperty("code").GetString());
            var message = error.GetProperty("message").GetString()!;
            Assert.Contains($"'{Store.ImageName}', '{second}'", message);
            Assert.Contains($"Step '{Store.StepName}'", message);
        }

        if (version == "2.0")
        {
            Assert.Equal(3, await store.DeployAsync(clean: true));
            Assert.Equal(3, await store.CleanAsync());
            Assert.Contains("equal ignoring case", store.LastCleanOutput);
            Assert.Equal(0, store.Writes);
            Assert.Equal(0, store.Deletes);
            Assert.Equal(0, store.Connections);
        }
    }

    [Fact]
    public async Task ConfiguredPreAndPostImagesWithOneName_PointToImageTypeBoth()
    {
        using var store = new Store("1.0");
        store.AddConfiguredImage(Store.ImageName, imageType: "PostImage");

        Assert.Equal(3, await store.DeployAsync());
        using var deploy = JsonDocument.Parse(store.LastDeployOutput);
        var message = deploy.RootElement.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("imageType \"Both\"", message);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Version1PluginsClean_IsNotBlockedByCollidingImageNames_BecauseItNeverPairsImages()
    {
        using var store = new Store("1.0");
        store.AddConfiguredImage("before");

        Assert.True(await store.CleanAsync() == 0, store.LastCleanOutput);
        Assert.Equal(0, store.Deletes); // the configured step is not orphaned
    }

    [Fact]
    public async Task StoredNamesCollidingWithAConfiguredName_AreIncompleteWithIds_AndCleanRefusesBeforeAnyWrite()
    {
        using var store = new Store("2.0");
        var extraId = store.AddStoredImage("BEFORE"); // stored: "Before" and "BEFORE"

        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(ExitCodes.Incomplete, diffExit);
        using (var diff = JsonDocument.Parse(diffOutput))
        {
            Assert.Equal("incomplete", diff.RootElement.GetProperty("metadata").GetProperty("status").GetString());
            var assembly = diff.RootElement.GetProperty("data")[0];
            Assert.Empty(assembly.GetProperty("missingImages").EnumerateArray());
            Assert.Empty(assembly.GetProperty("orphanedImages").EnumerateArray());
            var reason = Assert.Single(assembly.GetProperty("coverage").GetProperty("unsupported").EnumerateArray(),
                u => u.GetProperty("field").GetString()!.StartsWith("duplicate image names", StringComparison.Ordinal))
                .GetProperty("field").GetString()!;
            Assert.Contains("in the environment", reason);
            Assert.Contains($"'BEFORE' ({extraId:D})", reason);
            Assert.Contains($"configured image '{Store.ImageName}'", reason);
            Assert.Contains("ppds plugins unregister image <image-id>", reason);
        }

        // The plan is refused before any write (no assembly upload either), naming the stored rows.
        Assert.Equal(3, await store.DeployAsync(clean: true));
        Assert.Contains("cannot select duplicate image names within a step", store.LastDeployOutput);
        Assert.Contains(extraId.ToString("D"), store.LastDeployOutput);
        Assert.Contains("remove the extra stored image", store.LastDeployOutput);
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, store.Deletes);
        Assert.Equal(2, store.StoredImageNames().Count);
    }

    [Fact]
    public async Task Version2TextDeploy_IncompleteVerification_ShowsTheReasonsAndPointsToDiff_WithoutValues()
    {
        using var store = new Store("2.0");
        store.SetStep("unsecureConfiguration", System.Text.Json.Nodes.JsonValue.Create(Store.ConfigurationValue));
        var extraId = store.AddStoredImage("BEFORE");

        var (exit, error) = await store.DeployTextAsync();
        Assert.Equal(ExitCodes.Incomplete, exit);
        Assert.Contains("Nonsecret reconciliation verification: incomplete", error);
        Assert.Contains("Unknown: ", error);
        Assert.Contains(extraId.ToString("D"), error);
        Assert.Contains("ppds plugins unregister image <image-id>", error);
        Assert.Contains("Run 'ppds plugins diff --config registrations.json'", error);
        Assert.DoesNotContain(Store.ConfigurationValue, error);
    }

    [Fact]
    public async Task StoredNamesCollidingOnlyWithEachOther_AreOrdinaryOrphans_ButCleanupStillRefuses()
    {
        using var store = new Store("2.0");
        var upperId = store.AddStoredImage("Extra");
        var lowerId = store.AddStoredImage("EXTRA");

        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(1, diffExit);
        using (var diff = JsonDocument.Parse(diffOutput))
        {
            Assert.Equal("drift", diff.RootElement.GetProperty("metadata").GetProperty("status").GetString());
            var orphans = diff.RootElement.GetProperty("data")[0].GetProperty("orphanedImages").EnumerateArray()
                .Select(o => o.GetProperty("imageName").GetString()).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(["EXTRA", "Extra"], orphans);
        }

        // Neither row may be chosen for deletion over the other: both cleanup paths refuse, naming both IDs.
        Assert.Equal(3, await store.DeployAsync(clean: true));
        Assert.Contains(upperId.ToString("D"), store.LastDeployOutput);
        Assert.Contains(lowerId.ToString("D"), store.LastDeployOutput);
        Assert.Contains("remove one or both", store.LastDeployOutput);
        Assert.Equal(3, await store.CleanAsync());
        Assert.Contains(upperId.ToString("D"), store.LastCleanOutput);
        Assert.Contains(lowerId.ToString("D"), store.LastCleanOutput);
        Assert.Contains("remove one or both", store.LastCleanOutput);
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, store.Deletes);
        Assert.Equal(3, store.StoredImageNames().Count);
    }

    [Fact]
    public async Task DiffServiceBackstop_RejectsConfiguredCollisionsWithoutReadingTheEnvironment()
    {
        var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict); // any read would throw
        var step = new PluginStepConfig { Name = "s", Message = "Update", Entity = "account" };
        step.Images.Add(new PluginImageConfig { Name = "Pre", ImageType = "PreImage" });
        step.Images.Add(new PluginImageConfig { Name = "PRE", ImageType = "PreImage" });
        var assembly = new PluginAssemblyConfig { Name = "A", Types = [new PluginTypeConfig { TypeName = "T", Steps = [step] }] };

        var error = await Assert.ThrowsAsync<PpdsException>(() => new PluginDiffService(service.Object).CompareAssemblyAsync(assembly));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.ErrorCode);
        Assert.Contains("'Pre', 'PRE'", error.Message);
        Assert.Empty(service.Invocations);
    }

    [Fact]
    public void SharedRule_IsOrdinalIgnoreCase_AndPairsCaseOnlyDifferences()
    {
        // Pinned to the ordinal comparer: a culture-sensitive one would differ for some inputs.
        Assert.Same(StringComparer.OrdinalIgnoreCase, PluginImageNames.Comparer);
        Assert.True(PluginImageNames.Same("PreImage", "preimage"));
        Assert.False(PluginImageNames.Same("PreImage", "PostImage"));
        // Known unpaired cases (see PluginImageNames remarks): other collation-equal differences are not paired.
        Assert.False(PluginImageNames.Same("Pré", "Pre"));
        Assert.False(PluginImageNames.Same("Pre ", "Pre"));
    }

    private static void AssertImageCounts(string output, int created, int updated)
    {
        using var deploy = JsonDocument.Parse(output);
        var row = Assert.Single(deploy.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(created, row.GetProperty("imagesCreated").GetInt32());
        Assert.Equal(updated, row.GetProperty("imagesUpdated").GetInt32());
    }

    private static void AssertVerificationMatched(string output)
    {
        using var deploy = JsonDocument.Parse(output);
        Assert.True(deploy.RootElement.GetProperty("success").GetBoolean(), output);
        var row = Assert.Single(deploy.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal("match", row.GetProperty("reconciliationStatus").GetString());
    }
}
