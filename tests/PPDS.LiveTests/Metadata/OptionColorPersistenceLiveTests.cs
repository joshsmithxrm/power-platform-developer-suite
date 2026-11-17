using System.ServiceModel;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Metadata;

/// <summary>
/// Live verification of the Dataverse option-colour contract on a throwaway global choice, with raw SDK requests and
/// one variable changed per step (specs/metadata-authoring.md "Option colours", AC-146). It asserts:
///  1. the create path honours <see cref="OptionMetadata.Color"/> and an option description;
///  2. <see cref="OptionMetadata.Color"/> re-sent through <c>UpdateOptionSet</c> is ignored, with and without
///     <c>MergeLabels</c>, and still after <c>PublishXml</c> (published and draft reads);
///  3. the <c>Color</c> parameter of <c>UpdateOptionValue</c> is stored without a solution (null), alone or together
///     with a <c>Label</c> and/or <c>Description</c>; an omitted <c>Label</c> or <c>Description</c> leaves that text
///     unchanged and an omitted <c>Color</c> leaves the colour unchanged;
///  4. an empty <c>SolutionUniqueName</c> is rejected with a fault and writes nothing;
///  5. the <c>Color</c> parameter of <c>InsertOptionValue</c> is stored with the new option and its description.
/// The local-option, status-reason and state-value counterparts are in
/// <see cref="MetadataGlobalChoiceLiveTests.ColourMechanisms_LocalOptionStatusReasonAndStateValue"/>.
/// Credentials as for <see cref="MetadataGlobalChoiceLiveTests"/> (including the local PPDS_TEST_PROFILE mode).
/// </summary>
[Trait("Category", "Integration")]
public sealed class OptionColorPersistenceLiveTests : LiveTestBase
{
    private const int English = 1033;

    private readonly ITestOutputHelper _output;
    private DataverseConnectionPool? _pool;
    private IConnectionSource? _source;

    public OptionColorPersistenceLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [MetadataLiveFact]
    public async Task GlobalChoice_OnlyTheColorParameterChangesAnExistingOptionsColour()
    {
        var prefix = Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")!.Trim().ToLowerInvariant();
        var solution = Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!.Trim();
        var setName = $"{prefix}_clr{Guid.NewGuid():N}".Substring(0, Math.Min(48, prefix.Length + 12));
        var ct = CancellationToken.None;

        _source = Configuration.HasClientSecretCredentials
            ? await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "OptionColorTest", maxPoolSize: 3)
            : await LiveTestProfileConnectionSource.CreateAsync(Configuration, "OptionColorTest", maxPoolSize: 3);
        _pool = LiveTestHelpers.CreateConnectionPool(new[] { _source });

        await using var client = await _pool.GetClientAsync(cancellationToken: ct);

        var optionSet = new OptionSetMetadata
        {
            Name = setName,
            DisplayName = new Label("PPDS option-color live test", English),
            Description = new Label("Temporary metadata — verifies option color persistence. Safe to delete.", English),
            IsGlobal = true,
            OptionSetType = OptionSetType.Picklist
        };
        optionSet.Options.Add(new OptionMetadata(new Label("One", English), null) { Color = "#A00001", Description = new Label("D1", English) });

        _output.WriteLine($"Creating global choice '{setName}' in solution '{solution}'.");
        await client.ExecuteAsync(new CreateOptionSetRequest { OptionSet = optionSet, SolutionUniqueName = solution }, ct);

        var bodyFailed = false;
        try
        {
            var value = 0;
            async Task<OptionSetMetadata> Read(bool asIfPublished = true) => (OptionSetMetadata)((RetrieveOptionSetResponse)await client.ExecuteAsync(
                new RetrieveOptionSetRequest { Name = setName, RetrieveAsIfPublished = asIfPublished }, ct)).OptionSetMetadata;
            async Task<OptionMetadata> One() => (await Read()).Options.Single(o => o.Value == value);
            async Task Expect(string step, string color, string label, string description)
            {
                var option = await One();
                _output.WriteLine($"OBSERVED {step}: color {option.Color ?? "null"}, label '{Text(option.Label)}', description '{Text(option.Description)}'");
                option.Color.Should().Be(color, step);
                Text(option.Label).Should().Be(label, step);
                Text(option.Description).Should().Be(description, step);
            }
            UpdateOptionValueRequest Update(string? solutionName) =>
                new() { OptionSetName = setName, Value = value, MergeLabels = true, SolutionUniqueName = solutionName };

            value = (await Read()).Options.Single().Value!.Value;
            await Expect("create with OptionMetadata.Color", "#A00001", "One", "D1");

            var withoutMerge = await Read();
            withoutMerge.Options.Single().Color = "#A00002";
            await client.ExecuteAsync(new UpdateOptionSetRequest { OptionSet = withoutMerge, SolutionUniqueName = solution }, ct);
            await Expect("UpdateOptionSet + OptionMetadata.Color, no MergeLabels", "#A00001", "One", "D1");

            var withMerge = await Read();
            withMerge.Options.Single().Color = "#A00003";
            await client.ExecuteAsync(new UpdateOptionSetRequest { OptionSet = withMerge, MergeLabels = true, SolutionUniqueName = solution }, ct);
            await client.ExecuteAsync(new PublishXmlRequest { ParameterXml = $"<importexportxml><optionsets><optionset>{setName}</optionset></optionsets></importexportxml>" }, ct);
            var published = (await Read(asIfPublished: false)).Options.Single().Color;
            _output.WriteLine($"OBSERVED UpdateOptionSet + OptionMetadata.Color + MergeLabels, then PublishXml: published color {published ?? "null"}");
            published.Should().Be("#A00001", "UpdateOptionSet ignores an existing option's colour even after publishing");
            await Expect("UpdateOptionSet + OptionMetadata.Color + MergeLabels, then PublishXml (draft)", "#A00001", "One", "D1");

            var colourOnly = Update(null);
            colourOnly["Color"] = "#A00004";
            await client.ExecuteAsync(colourOnly, ct);
            await Expect("UpdateOptionValue Color only, no solution", "#A00004", "One", "D1");

            var emptySolution = Update("");
            emptySolution["Color"] = "#A00005";
            var rejected = await Record.ExceptionAsync(() => client.ExecuteAsync(emptySolution, ct));
            _output.WriteLine($"OBSERVED UpdateOptionValue with SolutionUniqueName \"\": {(rejected is null ? "accepted" : $"{rejected.GetType().Name}: {rejected.Message}")}");
            rejected.Should().BeAssignableTo<FaultException<OrganizationServiceFault>>("an empty solution name is rejected");
            await Expect("after the rejected empty-solution request", "#A00004", "One", "D1");

            var labelAndColour = Update(solution);
            labelAndColour.Label = new Label("One renamed", English);
            labelAndColour["Color"] = "#A00006";
            await client.ExecuteAsync(labelAndColour, ct);
            await Expect("UpdateOptionValue Label + Color, with solution", "#A00006", "One renamed", "D1");

            var all = Update(null);
            all.Label = new Label("One again", English);
            all.Description = new Label("D2", English);
            all["Color"] = "#A00007";
            await client.ExecuteAsync(all, ct);
            await Expect("UpdateOptionValue Label + Description + Color, no solution", "#A00007", "One again", "D2");

            var descriptionOnly = Update(null);
            descriptionOnly.Description = new Label("D3", English);
            await client.ExecuteAsync(descriptionOnly, ct);
            await Expect("UpdateOptionValue Description only (no Label, no Color)", "#A00007", "One again", "D3");

            var insert = new InsertOptionValueRequest { OptionSetName = setName, Label = new Label("Two", English), Description = new Label("DI", English) };
            insert["Color"] = "#A00008";
            var added = ((InsertOptionValueResponse)await client.ExecuteAsync(insert, ct)).NewOptionValue;
            var inserted = (await Read()).Options.Single(o => o.Value == added);
            _output.WriteLine($"OBSERVED InsertOptionValue with Color, no solution: color {inserted.Color ?? "null"}, description '{Text(inserted.Description)}'");
            inserted.Color.Should().Be("#A00008");
            Text(inserted.Description).Should().Be("DI");
            await Expect("the existing option after the insert", "#A00007", "One again", "D3");
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

    private static string? Text(Label? label) => label?.LocalizedLabels?.FirstOrDefault(l => l.LanguageCode == English)?.Label;

    public override async Task DisposeAsync()
    {
        if (_pool is not null)
            await _pool.DisposeAsync();
        _source?.Dispose();
        Configuration.Dispose();
        await base.DisposeAsync();
    }
}
