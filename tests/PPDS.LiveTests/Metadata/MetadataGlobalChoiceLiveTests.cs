using System.ServiceModel;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using SdkUpdateOptionValue = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;

namespace PPDS.LiveTests.Metadata;

/// <summary>
/// Live checks of the Dataverse behaviour the pre-release metadata fixes assume (specs/metadata-authoring.md
/// AC-133, AC-134, AC-141, AC-142 and the "Label languages" section). Each test creates its own uniquely named
/// throwaway global choice (and, for local/status options, a throwaway table) in PPDS_TEST_SOLUTION and deletes it
/// in a finally block. The authoring calls go through the real <see cref="DataverseMetadataAuthoringService"/> the
/// CLI, TUI and MCP use; setup and read-back use raw SDK requests. Observed fault codes and messages are written to
/// the test output so an assumption that does not hold is reported with what was actually seen.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MetadataGlobalChoiceLiveTests : LiveTestBase
{
    private const int English = 1033;
    private const int ObjectDoesNotExist = unchecked((int)0x80040217);

    private readonly ITestOutputHelper _output;
    private DataverseConnectionPool? _pool;
    private ServiceClientSource? _source;

    public MetadataGlobalChoiceLiveTests(ITestOutputHelper output) => _output = output;

    private static string Prefix => Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")!.Trim().ToLowerInvariant();
    private static string Solution => Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!.Trim();

    // 1. AC-141: optionset update keeps an unpublished edit to a property it does not change.
    [SkipIfNoSolution]
    public async Task ChoiceUpdate_KeepsAnUnpublishedDescription_ChangesOnlyTheDisplayName_AndPublishesNothing()
    {
        var (pool, service) = await ConnectAsync();
        var name = NewName("upd");
        await CreateChoiceAsync(pool, name, "Original name", "Published description", [("One", null), ("Two", null)]);
        try
        {
            await PublishChoiceAsync(pool, name);
            var before = await RetrieveChoiceAsync(pool, name, asIfPublished: true);

            // An unpublished description edit made by someone else.
            before.Description = new Label("Unpublished description", English);
            await ExecuteAsync(pool, new UpdateOptionSetRequest { OptionSet = before, MergeLabels = true, SolutionUniqueName = Solution });

            await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = name, DisplayName = "Renamed" });

            var draft = await RetrieveChoiceAsync(pool, name, asIfPublished: true);
            Text(draft.DisplayName, English).Should().Be("Renamed");
            Text(draft.Description, English).Should().Be("Unpublished description", "the unpublished description must not revert to the published copy");
            draft.Options.Select(o => (o.Value, Text(o.Label, English))).Should().BeEquivalentTo(
                before.Options.Select(o => (o.Value, Text(o.Label, English))), "options must be untouched");

            var published = await RetrieveChoiceAsync(pool, name, asIfPublished: false);
            Text(published.DisplayName, English).Should().Be("Original name", "optionset update publishes nothing");
            Text(published.Description, English).Should().Be("Published description", "optionset update publishes nothing");
        }
        finally
        {
            await DeleteChoiceAsync(pool, name);
        }
    }

    // 2. AC-142: --new-label keeps a second language; colour- and description-only updates change no label.
    [SkipIfNoSolution]
    public async Task GlobalOptionUpdate_NewLabelKeepsOtherLanguages_ColourAndDescriptionOnlyChangeNoLabel()
    {
        var (pool, service) = await ConnectAsync();
        var second = await SecondLanguageAsync(pool);
        if (second is null) _output.WriteLine("SKIPPED multi-language assertions: no provisioned language other than 1033.");
        var name = NewName("lbl");
        await CreateChoiceAsync(pool, name, "Label test", "Label test", [("Open", second is null ? null : (second.Value, "Ouvert-" + second.Value))]);
        try
        {
            var value = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single().Value!.Value;

            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, NewLabel = "Closed", SolutionUniqueName = Solution });
            var afterNewLabel = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
            Text(afterNewLabel.Label, English).Should().Be("Closed");
            if (second is not null)
                Text(afterNewLabel.Label, second.Value).Should().Be("Ouvert-" + second.Value, "MergeLabels must keep the other language's label");

            var labelsBefore = Pairs(afterNewLabel.Label);
            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, Color = "#123456", SolutionUniqueName = Solution });
            var afterColour = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
            Pairs(afterColour.Label).Should().BeEquivalentTo(labelsBefore, "a colour-only update must change no label");
            afterColour.Color.Should().Be("#123456");

            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, Description = "Only the description", SolutionUniqueName = Solution });
            var afterDescription = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
            Pairs(afterDescription.Label).Should().BeEquivalentTo(labelsBefore, "a description-only update must change no label");
            Text(afterDescription.Description, English).Should().Be("Only the description");
        }
        finally
        {
            await DeleteChoiceAsync(pool, name);
        }
    }

    // 3. AC-142 for a local column option and a status reason, on a throwaway custom table.
    [SkipIfNoSolution]
    public async Task LocalOptionAndStatusReason_NewLabelKeepsOtherLanguages_ColourOnlyChangesNoLabel()
    {
        var (pool, service) = await ConnectAsync();
        var second = await SecondLanguageAsync(pool);
        if (second is null) _output.WriteLine("SKIPPED multi-language assertions: no provisioned language other than 1033.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var table = $"{Prefix}_ppdslbl{suffix}";
        var column = $"{Prefix}_pick{suffix}";
        await ExecuteAsync(pool, new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                SchemaName = table, DisplayName = new Label("PPDS label live test", English),
                DisplayCollectionName = new Label("PPDS label live tests", English),
                Description = new Label("Temporary metadata. Safe to delete.", English),
                OwnershipType = OwnershipTypes.UserOwned, IsActivity = false
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                SchemaName = $"{Prefix}_name{suffix}", MaxLength = 100, FormatName = StringFormatName.Text,
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                DisplayName = new Label("Name", English)
            },
            SolutionUniqueName = Solution
        });
        var entity = table.ToLowerInvariant();
        try
        {
            var local = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
            local.Options.Add(new OptionMetadata(Labels("Open", second is null ? null : (second.Value, "Ouvert-" + second.Value)), null));
            await ExecuteAsync(pool, new CreateAttributeRequest
            {
                EntityName = entity, SolutionUniqueName = Solution,
                Attribute = new PicklistAttributeMetadata
                {
                    SchemaName = column, DisplayName = new Label("Pick", English), OptionSet = local,
                    RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
                }
            });

            // Local option.
            var option = (await RetrieveOptionsAsync(pool, entity, column.ToLowerInvariant())).Single();
            await service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = entity, ColumnLogicalName = column.ToLowerInvariant(), Value = option.Value, NewLabel = "Closed", SolutionUniqueName = Solution });
            option = (await RetrieveOptionsAsync(pool, entity, column.ToLowerInvariant())).Single();
            Text(option.Label, English).Should().Be("Closed");
            if (second is not null) Text(option.Label, second.Value).Should().Be("Ouvert-" + second.Value);
            var localLabels = Pairs(option.Label);
            await service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = entity, ColumnLogicalName = column.ToLowerInvariant(), Value = option.Value, Color = "#654321", SolutionUniqueName = Solution });
            Pairs((await RetrieveOptionsAsync(pool, entity, column.ToLowerInvariant())).Single().Label).Should().BeEquivalentTo(localLabels, "a colour-only update must change no label");

            // Status reason (the table's built-in 'Active' reason, state 0).
            var reason = (await RetrieveOptionsAsync(pool, entity, "statuscode")).OfType<StatusOptionMetadata>().First(o => o.State == 0);
            var reasonSecond = second is null ? null : Text(reason.Label, second.Value);
            _output.WriteLine($"Status reason {reason.Value} labels before: {string.Join(", ", Pairs(reason.Label))}");
            await service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = entity, Value = reason.Value, NewLabel = "Live", SolutionUniqueName = Solution });
            var reasonAfter = (await RetrieveOptionsAsync(pool, entity, "statuscode")).First(o => o.Value == reason.Value);
            Text(reasonAfter.Label, English).Should().Be("Live");
            if (second is not null && reasonSecond is not null)
                Text(reasonAfter.Label, second.Value).Should().Be(reasonSecond, "MergeLabels must keep the other language's status label");
            var reasonLabels = Pairs(reasonAfter.Label);
            await service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = entity, Value = reason.Value, Color = "#0A0B0C", SolutionUniqueName = Solution });
            Pairs((await RetrieveOptionsAsync(pool, entity, "statuscode")).First(o => o.Value == reason.Value).Label)
                .Should().BeEquivalentTo(reasonLabels, "a colour-only status update must change no label");
        }
        finally
        {
            try
            {
                await ExecuteAsync(pool, new DeleteEntityRequest { LogicalName = entity });
                _output.WriteLine($"Deleted table '{entity}'.");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cleanup of table '{entity}' failed (delete it manually if it lingers): {ex.Message}");
            }
        }
    }

    // 4. AC-133: which fault Dataverse returns for a mis-cased global choice name, and that the CLI resolves it.
    [SkipIfNoSolution]
    public async Task MisCasedName_ResolvesAndRecordsTheFaultCode()
    {
        var (pool, service) = await ConnectAsync();
        var name = NewName("case");
        await CreateChoiceAsync(pool, name, "Case test", "Case test", [("One", null), ("Two", null)]);
        try
        {
            var stored = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Name;
            var misCased = MisCase(stored);
            _output.WriteLine($"Stored name '{stored}', mis-cased request '{misCased}'.");
            var values = (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Select(o => o.Value!.Value).ToArray();

            var observed = new List<(string Operation, int? Code, string Outcome)>
            {
                await ObserveAsync(pool, "RetrieveOptionSet (published)", new RetrieveOptionSetRequest { Name = misCased }),
                await ObserveAsync(pool, "RetrieveOptionSet (as-if-published)", new RetrieveOptionSetRequest { Name = misCased, RetrieveAsIfPublished = true }),
                await ObserveAsync(pool, "InsertOptionValue", new InsertOptionValueRequest { OptionSetName = misCased, Label = new Label("Probe", English), SolutionUniqueName = Solution }),
                await ObserveAsync(pool, "OrderOption", new OrderOptionRequest { OptionSetName = misCased, Values = Enumerable.Reverse(values).ToArray(), SolutionUniqueName = Solution })
            };
            foreach (var (operation, code, outcome) in observed)
                _output.WriteLine($"OBSERVED {operation}: {outcome}");

            // The behaviour: every resolving path accepts the case variant.
            var added = await service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = misCased, Label = "Added", SolutionUniqueName = Solution });
            (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Should().Contain(o => o.Value == added);
            await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = misCased, DisplayName = "Case test renamed" });
            await service.ReorderOptionsAsync(new ReorderOptionsRequest { OptionSetName = misCased, Order = (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Select(o => o.Value!.Value).Reverse().ToArray(), SolutionUniqueName = Solution });
            await PublishChoiceAsync(pool, stored);
            (await new DataverseMetadataQueryService(pool).GetOptionSetAsync(misCased)).Name.Should().Be(stored);

            // The assumption: a rejected spelling is reported as ObjectDoesNotExist (0x80040217).
            foreach (var (operation, code, outcome) in observed.Where(o => o.Code.HasValue))
                code.Should().Be(ObjectDoesNotExist,
                    $"the not-found recognition assumes 0x80040217 for {operation}, but Dataverse returned {outcome}");
        }
        finally
        {
            await DeleteChoiceAsync(pool, name);
        }
    }

    // 5. AC-134: a missing solution on add-option surfaces the original error and writes nothing.
    [SkipIfNoSolution]
    public async Task MissingSolution_OnAddOption_SurfacesTheErrorAndWritesNothing()
    {
        var (pool, service) = await ConnectAsync();
        var name = NewName("sol");
        var missingSolution = "ppdsmissing" + Guid.NewGuid().ToString("N")[..12];
        await CreateChoiceAsync(pool, name, "Solution test", "Solution test", [("One", null)]);
        try
        {
            var stored = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Name;
            var raw = await ObserveAsync(pool, "InsertOptionValue (missing solution)",
                new InsertOptionValueRequest { OptionSetName = stored, Label = new Label("Probe", English), SolutionUniqueName = missingSolution });
            _output.WriteLine($"OBSERVED {raw.Operation}: {raw.Outcome}");
            raw.Code.Should().NotBeNull("Dataverse must reject an unknown solution");
            var count = (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Count;

            foreach (var spelling in new[] { stored, MisCase(stored) })
            {
                var ex = await Record.ExceptionAsync(() => service.AddOptionValueAsync(
                    new AddOptionValueRequest { OptionSetName = spelling, Label = "Must not exist", SolutionUniqueName = missingSolution }));
                _output.WriteLine($"OBSERVED add-option '{spelling}' with missing solution: {Describe(ex)}");
                ex.Should().NotBeNull();
                ex.Should().NotBeOfType<MetadataValidationException>("a missing solution is not a name-resolution failure");
                var fault = FindFault(ex!);
                fault.Should().NotBeNull("the original Dataverse fault must surface (directly or as the inner exception)");
                fault!.ErrorCode.Should().Be(raw.Code!.Value, "the original solution error must surface, not a name-resolution error");
                (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Count.Should().Be(count, "nothing may be written");
            }
        }
        finally
        {
            await DeleteChoiceAsync(pool, name);
        }
    }

    // 6. AC-142 clearing rule: record what `--description ""` does to a choice.
    [SkipIfNoSolution]
    public async Task EmptyDescription_RecordsWhatDataverseDoes()
    {
        var (pool, service) = await ConnectAsync();
        var second = await SecondLanguageAsync(pool);
        var name = NewName("clr");
        await CreateChoiceAsync(pool, name, "Clear test", "English description", [("One", null)], second is null ? null : (second.Value, "Description " + second.Value));
        try
        {
            _output.WriteLine($"Description before: {string.Join(", ", Pairs((await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Description))}");
            var ex = await Record.ExceptionAsync(() => service.UpdateGlobalChoiceAsync(
                new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = name, Description = "" }));
            _output.WriteLine($"OBSERVED optionset update --description \"\": {Describe(ex)}");
            var after = await RetrieveChoiceAsync(pool, name, asIfPublished: true);
            _output.WriteLine($"OBSERVED description after: {string.Join(", ", Pairs(after.Description))}");
            if (ex is null && second is not null)
                Text(after.Description, second.Value).Should().Be("Description " + second.Value, "MergeLabels keeps the other language");
        }
        finally
        {
            await DeleteChoiceAsync(pool, name);
        }
    }

    // ───── helpers ─────

    private async Task<(DataverseConnectionPool Pool, DataverseMetadataAuthoringService Service)> ConnectAsync()
    {
        _source = await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "MetadataGlobalChoiceLiveTests", maxPoolSize: 3);
        _pool = LiveTestHelpers.CreateConnectionPool(new[] { _source });
        return (_pool, new DataverseMetadataAuthoringService(_pool, new SchemaValidator(), new EnvironmentShakedownGuard()));
    }

    private static string NewName(string kind) => $"{Prefix}_ppds{kind}{Guid.NewGuid():N}"[..Math.Min(40, Prefix.Length + 21)];

    /// <summary>Upper-cases the part after the prefix so the spelling differs from the stored one by case only.</summary>
    private static string MisCase(string stored)
    {
        var cut = stored.IndexOf('_') + 1;
        return stored[..cut] + stored[cut..].ToUpperInvariant();
    }

    private static Label Labels(string english, (int Lcid, string Text)? other)
    {
        var label = new Label(english, English);
        if (other is { } o) label.LocalizedLabels.Add(new LocalizedLabel(o.Text, o.Lcid));
        return label;
    }

    private async Task CreateChoiceAsync(IDataverseConnectionPool pool, string name, string displayName, string description,
        (string English, (int Lcid, string Text)? Other)[] options, (int Lcid, string Text)? otherDescription = null)
    {
        var set = new OptionSetMetadata
        {
            Name = name, IsGlobal = true, OptionSetType = OptionSetType.Picklist,
            DisplayName = new Label(displayName, English), Description = Labels(description, otherDescription)
        };
        foreach (var (english, other) in options)
            set.Options.Add(new OptionMetadata(Labels(english, other), null));
        _output.WriteLine($"Creating global choice '{name}' in solution '{Solution}'.");
        await ExecuteAsync(pool, new CreateOptionSetRequest { OptionSet = set, SolutionUniqueName = Solution });
    }

    private async Task DeleteChoiceAsync(IDataverseConnectionPool pool, string name)
    {
        try
        {
            await ExecuteAsync(pool, new DeleteOptionSetRequest { Name = name });
            _output.WriteLine($"Deleted global choice '{name}'.");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Cleanup of '{name}' failed (delete it manually if it lingers): {ex.Message}");
        }
    }

    private static Task PublishChoiceAsync(IDataverseConnectionPool pool, string name) => ExecuteAsync(pool, new PublishXmlRequest
    {
        ParameterXml = $"<importexportxml><optionsets><optionset>{name}</optionset></optionsets></importexportxml>"
    });

    private static async Task<OptionSetMetadata> RetrieveChoiceAsync(IDataverseConnectionPool pool, string name, bool asIfPublished)
        => (OptionSetMetadata)((RetrieveOptionSetResponse)await ExecuteAsync(pool,
            new RetrieveOptionSetRequest { Name = name, RetrieveAsIfPublished = asIfPublished })).OptionSetMetadata;

    private static async Task<IReadOnlyList<OptionMetadata>> RetrieveOptionsAsync(IDataverseConnectionPool pool, string entity, string attribute)
    {
        var response = (RetrieveAttributeResponse)await ExecuteAsync(pool,
            new RetrieveAttributeRequest { EntityLogicalName = entity, LogicalName = attribute, RetrieveAsIfPublished = true });
        return ((EnumAttributeMetadata)response.AttributeMetadata).OptionSet.Options;
    }

    private static async Task<int?> SecondLanguageAsync(IDataverseConnectionPool pool)
    {
        var languages = ((RetrieveProvisionedLanguagesResponse)await ExecuteAsync(pool, new RetrieveProvisionedLanguagesRequest())).RetrieveProvisionedLanguages;
        return languages.Where(l => l != English).Cast<int?>().FirstOrDefault();
    }

    private static async Task<OrganizationResponse> ExecuteAsync(IDataverseConnectionPool pool, OrganizationRequest request)
    {
        await using var client = await pool.GetClientAsync();
        return await client.ExecuteAsync(request);
    }

    /// <summary>Sends a raw request and records its fault code and message, or that it was accepted.</summary>
    private static async Task<(string Operation, int? Code, string Outcome)> ObserveAsync(IDataverseConnectionPool pool, string operation, OrganizationRequest request)
    {
        try
        {
            await ExecuteAsync(pool, request);
            return (operation, null, "accepted (no fault: this lookup is not case-sensitive)");
        }
        catch (Exception ex) when (FindFault(ex) is { } fault)
        {
            return (operation, fault.ErrorCode, $"0x{unchecked((uint)fault.ErrorCode):x8} '{fault.Message}'");
        }
    }

    private static OrganizationServiceFault? FindFault(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is FaultException<OrganizationServiceFault> { Detail: not null } fault)
                return fault.Detail;
        return null;
    }

    private static string Describe(Exception? ex) => ex is null
        ? "succeeded"
        : $"{ex.GetType().Name}{(ex is PpdsException p ? " " + p.ErrorCode : "")}" +
          $"{(FindFault(ex) is { } f ? $" fault 0x{unchecked((uint)f.ErrorCode):x8}" : "")}: {ex.Message}";

    private static string? Text(Label? label, int lcid) => label?.LocalizedLabels?.FirstOrDefault(l => l.LanguageCode == lcid)?.Label;

    private static (int, string)[] Pairs(Label? label)
        => (label?.LocalizedLabels ?? []).Select(l => (l.LanguageCode, l.Label)).OrderBy(p => p.LanguageCode).ToArray();

    public override async Task DisposeAsync()
    {
        if (_pool is not null)
            await _pool.DisposeAsync();
        _source?.Dispose();
        Configuration.Dispose();
        await base.DisposeAsync();
    }

    /// <summary>Honours PPDS_SHAKEDOWN=1 like the production guard, without its file-system sentinel.</summary>
    private sealed class EnvironmentShakedownGuard : IShakedownGuard
    {
        public void EnsureCanMutate(string operationDescription)
        {
            if (Environment.GetEnvironmentVariable("PPDS_SHAKEDOWN") == "1")
                throw new PpdsException(ErrorCodes.Safety.ShakedownActive, $"Shakedown active: '{operationDescription}' refused.");
        }
    }
}
