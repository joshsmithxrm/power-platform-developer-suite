using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// Drift guard for the UNDOCUMENTED Dataverse internals that <c>model-driven-app add-copilot</c> relies on
/// (issue #1196). The committed reference fixture
/// (<c>Fixtures/ModelDrivenApps/appelement-bot-binding.json</c>) pins the known-good
/// <c>appelement</c> → <c>bot</c> binding shape: the <c>objectid</c> lookup logical name resolves to
/// <c>bot</c>, the polymorphic target set is <c>bot</c>/<c>aiskillconfig</c>/<c>mcpserver</c>, <c>objectid</c>
/// is create-only, and the <c>bot</c> exposes <c>isLightweightBot</c>.
///
/// None of these are contractually documented, so any can change without notice. This test asserts the
/// invariants the command depends on; when re-pointed at a live org's metadata, the same fixture is the
/// reference the live read is compared against. On mismatch it fails loudly — Dataverse internals changed,
/// revalidate add-copilot — instead of letting a user discover it in production.
///
/// Tagged Integration so it is excluded from the fast unit shard.
/// </summary>
[Trait("Category", "Integration")]
public class AppElementBindingDriftGuardTests
{
    private const string DriftMessage = "Dataverse internals changed — revalidate add-copilot";

    [Fact]
    public void ReferenceFixture_PinsTheRelied_OnAppElementBotBindingShape()
    {
        using var doc = LoadFixture();
        var root = doc.RootElement;

        var appelement = root.GetProperty("appelement");

        // 1. The polymorphic objectid bind read back as a bot lookup.
        appelement.GetProperty("_objectid_value@Microsoft.Dynamics.CRM.lookuplogicalname").GetString()
            .Should().Be("bot", because: DriftMessage);

        // 2. All three objectid targets share the single "objectid" navigation property (why the SDK
        //    EntityReference — not Web API @odata.bind — is required to disambiguate).
        var lookup = root.GetProperty("objectidLookup");
        lookup.GetProperty("navigationProperty").GetString()
            .Should().Be("objectid", because: DriftMessage);

        var targets = lookup.GetProperty("targets").EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        targets.Should().Contain(new[] { "bot", "aiskillconfig", "mcpserver" }, because: DriftMessage);

        // 3. objectid is create-only — updates to an existing row's objectid are silently dropped.
        lookup.GetProperty("createOnly").GetBoolean()
            .Should().BeTrue(because: DriftMessage);

        // 4. isLightweightBot — the prerequisite that makes a bot render as a model-driven app assistant.
        var bot = root.GetProperty("bot");
        bot.TryGetProperty("isLightweightBot", out var isLightweight)
            .Should().BeTrue(because: DriftMessage);
        isLightweight.GetBoolean().Should().BeTrue(because: DriftMessage);
    }

    private static JsonDocument LoadFixture([CallerFilePath] string? thisFile = null)
    {
        // Resolve the fixture relative to this source file so the test needs no csproj copy step.
        var dir = Path.GetDirectoryName(thisFile)!;
        var path = Path.GetFullPath(Path.Combine(dir, "..", "Fixtures", "ModelDrivenApps", "appelement-bot-binding.json"));
        File.Exists(path).Should().BeTrue($"reference fixture must be committed at {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
