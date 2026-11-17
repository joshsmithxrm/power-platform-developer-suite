using System.Text.Json;
using PPDS.Cli.Services.Plugins;
using Xunit;
using Store = PPDS.Cli.Tests.Commands.Plugins.PluginRoundTripConvergenceTests.InMemoryRegistrationStore;

namespace PPDS.Cli.Tests.Commands.Plugins;

/// <summary>
/// AC-139: deploy (update-versus-create), image pruning, diff and the 2.0 post-deploy verification pair a
/// configured image with a stored image by one rule (<see cref="PluginImageNames"/>): names that differ only
/// by case are the same image. Runs the actual command pipelines and step/image service code against the
/// round-trip row store, which evaluates string conditions case-insensitively as Dataverse's default
/// collation does. This does not prove Dataverse server behavior.
/// </summary>
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
    public async Task StoredNamesDifferingOnlyByCase_AreAmbiguous_NotMissingOrOrphaned_AndCleanDeletesNothing()
    {
        using var store = new Store("2.0");
        store.AddStoredImage("BEFORE"); // stored: "Before" and "BEFORE"

        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(2, diffExit);
        using (var diff = JsonDocument.Parse(diffOutput))
        {
            Assert.Equal("incomplete", diff.RootElement.GetProperty("metadata").GetProperty("status").GetString());
            var assembly = diff.RootElement.GetProperty("data")[0];
            Assert.Empty(assembly.GetProperty("missingImages").EnumerateArray());
            Assert.Empty(assembly.GetProperty("orphanedImages").EnumerateArray());
        }

        Assert.NotEqual(0, await store.DeployAsync(clean: true));
        Assert.Equal(0, store.Deletes);
        Assert.Equal(2, store.StoredImageNames().Count);
    }

    [Fact]
    public void OneSharedRule_IsOrdinalCaseInsensitive()
    {
        Assert.True(PluginImageNames.Same("PreImage", "preimage"));
        Assert.False(PluginImageNames.Same("PreImage", "PostImage"));
        Assert.Same(StringComparer.OrdinalIgnoreCase, PluginImageNames.Comparer);
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
