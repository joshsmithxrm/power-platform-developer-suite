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
/// Live verification of the Dataverse option-colour contract (specs/metadata-authoring.md "Option colours").
///
/// Observed on a development organisation (2026-11-17), contrary to the earlier assumption (#1234): an existing
/// option's colour persists only through the <c>Color</c> parameter of <c>UpdateOptionValue</c> (set with the
/// request indexer, as the SDK class has no property for it); re-sending <see cref="OptionMetadata.Color"/>
/// through <c>UpdateOptionSet</c> is accepted but leaves the colour unchanged. This test pins that on a
/// throwaway global choice:
///  1. the create path honours <see cref="OptionMetadata.Color"/> (baseline),
///  2. <see cref="OptionMetadata.Color"/> + UpdateOptionSet does NOT change the colour, and
///  3. <c>UpdateOptionValueRequest["Color"]</c> DOES change it (the mechanism the authoring service uses).
/// Credentials as for <see cref="MetadataGlobalChoiceLiveTests"/> (including the local PPDS_TEST_PROFILE mode).
/// </summary>
[Trait("Category", "Integration")]
public sealed class OptionColorPersistenceLiveTests : LiveTestBase
{
    private const int LanguageCode = 1033;
    private const string ColorCreate = "#1A2B3C";   // applied on the create path
    private const string ColorMetadata = "#00C853"; // attempted (and observed to be ignored) via OptionMetadata.Color + UpdateOptionSet
    private const string ColorParameter = "#FF0000"; // applied via UpdateOptionValue's Color parameter

    private readonly ITestOutputHelper _output;
    private DataverseConnectionPool? _pool;
    private IConnectionSource? _source;

    public OptionColorPersistenceLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [MetadataLiveFact]
    public async Task OptionMetadataColorUpdate_IsIgnored_WhileTheUpdateOptionValueColorParameterPersists()
    {
        var prefix = Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")!.Trim().ToLowerInvariant();
        var solution = Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!.Trim();
        var setName = $"{prefix}_clr{Guid.NewGuid():N}".Substring(0, Math.Min(48, prefix.Length + 12));
        var ct = CancellationToken.None;

        _source = Configuration.HasClientSecretCredentials
            ? await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "OptionColorTest", maxPoolSize: 3)
            : await ProfileConnectionSource.CreateAsync(Configuration, "OptionColorTest", maxPoolSize: 3);
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

        var bodyFailed = false;
        try
        {
            var optionValue = await GetSingleOptionValueAsync(client, setName, ct);
            _output.WriteLine($"Option value assigned: {optionValue}.");

            // Baseline: the create path uses OptionMetadata.Color, which must persist.
            (await GetColorAsync(client, setName, optionValue, ct))
                .Should().Be(ColorCreate, "OptionMetadata.Color on the create path is honoured");

            // 2. OptionMetadata.Color on the retrieved set, re-sent via UpdateOptionSet.
            var retrievedSet = await RetrieveSetAsync(client, setName, ct);
            retrievedSet.Options.First(o => o.Value == optionValue).Color = ColorMetadata;
            await client.ExecuteAsync(new UpdateOptionSetRequest { OptionSet = retrievedSet, MergeLabels = true, SolutionUniqueName = solution }, ct);
            var afterMetadata = await GetColorAsync(client, setName, optionValue, ct);
            _output.WriteLine($"OBSERVED colour after OptionMetadata.Color + UpdateOptionSet: {afterMetadata ?? "null"}");
            afterMetadata.Should().Be(ColorCreate, "UpdateOptionSet does not change an existing option's colour");

            // 3. UpdateOptionValue's Color parameter, with no Label.
            var update = new UpdateOptionValueRequest { OptionSetName = setName, Value = optionValue, MergeLabels = true, SolutionUniqueName = solution };
            update["Color"] = ColorParameter;
            await client.ExecuteAsync(update, ct);
            var afterParameter = await RetrieveSetAsync(client, setName, ct);
            var option = afterParameter.Options.First(o => o.Value == optionValue);
            _output.WriteLine($"OBSERVED colour after UpdateOptionValue[\"Color\"]: {option.Color ?? "null"}");
            option.Color.Should().Be(ColorParameter, "UpdateOptionValue's Color parameter changes the colour");
            option.Label.LocalizedLabels.Should().ContainSingle(l => l.LanguageCode == LanguageCode && l.Label == "Initial",
                "an UpdateOptionValue without a Label leaves the label alone");
        }
        catch
        {
            bodyFailed = true;
            throw;
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
                var message = $"Cleanup failed: global choice '{setName}' was left in the environment; delete it manually. {ex.Message}";
                _output.WriteLine(message);
                if (!bodyFailed)
                    throw new Xunit.Sdk.XunitException(message);
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
