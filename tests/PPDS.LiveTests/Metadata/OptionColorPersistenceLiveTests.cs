using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Metadata;

/// <summary>
/// Live verification of the Dataverse option-color contract that Gemini review flagged (#review).
///
/// The Insert/Update OptionValue messages have NO <c>Color</c> parameter — the authoring service previously
/// set <c>request["Color"]</c>, which only writes an unrecognized entry into the request's Parameters
/// collection that the platform silently ignores. The supported mechanism is to set
/// <see cref="OptionMetadata.Color"/> on the retrieved option set and re-send it via UpdateOptionSet
/// (global) / UpdateAttribute (local).
///
/// This test proves both halves of that verdict against a real org on a throwaway global choice:
///  1. the create path honors <see cref="OptionMetadata.Color"/> (baseline),
///  2. <c>UpdateOptionValueRequest["Color"]</c> does NOT change the color (the bug), and
///  3. <c>OptionMetadata.Color</c> + UpdateOptionSet DOES change it (the fix).
/// </summary>
[Trait("Category", "Integration")]
public sealed class OptionColorPersistenceLiveTests : LiveTestBase
{
    private const int LanguageCode = 1033;
    private const string ColorCreate = "#1A2B3C";   // applied on the create path
    private const string ColorIndexer = "#FF0000";  // attempted (and expected to be ignored) via request["Color"]
    private const string ColorMetadata = "#00C853"; // applied via OptionMetadata.Color + UpdateOptionSet

    private readonly ITestOutputHelper _output;
    private DataverseConnectionPool? _pool;
    private ServiceClientSource? _source;

    public OptionColorPersistenceLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkipIfNoSolution]
    public async Task UpdateOptionValueColorIndexer_IsIgnored_WhileOptionMetadataColorPersists()
    {
        var prefix = Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")!.Trim().ToLowerInvariant();
        var solution = Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!.Trim();
        var setName = $"{prefix}_clr{Guid.NewGuid():N}".Substring(0, Math.Min(48, prefix.Length + 12));
        var ct = CancellationToken.None;

        _source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "OptionColorTest", maxPoolSize: 3);
        _pool = LiveTestHelpers.CreateConnectionPool(new[] { _source });

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);

        // 1. Create a throwaway global choice with a single option that carries a color (system-assigned value).
        var optionSet = new OptionSetMetadata
        {
            Name = setName,
            DisplayName = new Label("PPDS option-color live test", LanguageCode),
            Description = new Label("Temporary metadata — verifies option color persistence. Safe to delete.", LanguageCode),
            IsGlobal = true,
            OptionSetType = OptionSetType.Picklist
        };
        optionSet.Options.Add(new OptionMetadata(new Label("Initial", LanguageCode), null) { Color = ColorCreate });

        _output.WriteLine($"Creating global choice '{setName}' in solution '{solution}'.");
        await client.ExecuteAsync(new CreateOptionSetRequest { OptionSet = optionSet, SolutionUniqueName = solution }, ct);

        try
        {
            var optionValue = await GetSingleOptionValueAsync(client, setName, ct);
            _output.WriteLine($"Option value assigned: {optionValue}.");

            // Baseline: the create path uses OptionMetadata.Color, which must persist.
            (await GetColorAsync(client, setName, optionValue, ct))
                .Should().Be(ColorCreate, "OptionMetadata.Color on the create path is the supported mechanism");

            // 2. The broken approach: stuff a Color parameter onto UpdateOptionValueRequest via the indexer.
            var indexerUpdate = new UpdateOptionValueRequest
            {
                OptionSetName = setName,
                Value = optionValue,
                Label = new Label("Initial", LanguageCode),
                MergeLabels = true
            };
            indexerUpdate["Color"] = ColorIndexer; // UpdateOptionValue has no Color parameter — expected to be dropped.

            _output.WriteLine("Attempting color change via UpdateOptionValueRequest[\"Color\"] (expected to be ignored).");
            await client.ExecuteAsync(indexerUpdate, ct);

            var afterIndexer = await GetColorAsync(client, setName, optionValue, ct);
            afterIndexer.Should().NotBe(ColorIndexer,
                "UpdateOptionValueRequest has no Color parameter, so request[\"Color\"] must be silently ignored by the platform");
            afterIndexer.Should().Be(ColorCreate, "the indexer write must leave the color unchanged");

            // 3. The fix: set OptionMetadata.Color on the retrieved set and re-send via UpdateOptionSet.
            var retrieved = (RetrieveOptionSetResponse)await client.ExecuteAsync(
                new RetrieveOptionSetRequest { Name = setName, RetrieveAsIfPublished = true }, ct);
            var retrievedSet = (OptionSetMetadata)retrieved.OptionSetMetadata;
            retrievedSet.Options.First(o => o.Value == optionValue).Color = ColorMetadata;

            _output.WriteLine("Applying color via OptionMetadata.Color + UpdateOptionSetRequest (the fix).");
            await client.ExecuteAsync(new UpdateOptionSetRequest { OptionSet = retrievedSet, SolutionUniqueName = solution }, ct);

            (await GetColorAsync(client, setName, optionValue, ct))
                .Should().Be(ColorMetadata, "OptionMetadata.Color + UpdateOptionSet is the supported mechanism and must persist");
        }
        finally
        {
            try
            {
                await client.ExecuteAsync(new DeleteOptionSetRequest { Name = setName }, ct);
                _output.WriteLine($"Deleted global choice '{setName}'.");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cleanup of '{setName}' failed (delete manually if it lingers): {ex.Message}");
            }
        }
    }

    private static async Task<int> GetSingleOptionValueAsync(IPooledClient client, string setName, CancellationToken ct)
    {
        var set = await RetrieveSetAsync(client, setName, ct);
        return set.Options.Single().Value!.Value;
    }

    private static async Task<string?> GetColorAsync(IPooledClient client, string setName, int value, CancellationToken ct)
    {
        var set = await RetrieveSetAsync(client, setName, ct);
        return set.Options.First(o => o.Value == value).Color;
    }

    private static async Task<OptionSetMetadata> RetrieveSetAsync(IPooledClient client, string setName, CancellationToken ct)
    {
        var response = (RetrieveOptionSetResponse)await client.ExecuteAsync(
            new RetrieveOptionSetRequest { Name = setName, RetrieveAsIfPublished = true }, ct);
        return (OptionSetMetadata)response.OptionSetMetadata;
    }

    public override async Task DisposeAsync()
    {
        if (_pool is not null)
            await _pool.DisposeAsync();
        _source?.Dispose();
        Configuration.Dispose();
        await base.DisposeAsync();
    }
}
