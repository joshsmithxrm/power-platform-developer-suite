using System.ServiceModel;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace PPDS.LiveTests.Metadata;

/// <summary>
/// Live checks of the Dataverse behaviour the pre-release metadata fixes assume (specs/metadata-authoring.md AC-133,
/// AC-134, AC-141, AC-142, AC-145, AC-146, AC-147, "Option colours" and "Label languages"). Run against a DEVELOPMENT environment only:
/// <code>
/// dotnet test tests/PPDS.LiveTests/PPDS.LiveTests.csproj -f net8.0 --filter "FullyQualifiedName~MetadataGlobalChoiceLiveTests" --logger "console;verbosity=detailed"
/// </code>
/// Settings: DATAVERSE_URL, PPDS_TEST_APP_ID, PPDS_TEST_CLIENT_SECRET, PPDS_TEST_TENANT_ID, PPDS_TEST_SOLUTION,
/// PPDS_TEST_PREFIX. For a local run without a secret, set DATAVERSE_URL and PPDS_TEST_PROFILE (the name of an existing InteractiveBrowser,
/// already signed-in PPDS auth profile bound to that environment) instead of the three PPDS_TEST_APP_ID/CLIENT_SECRET/
/// TENANT_ID settings: the token is acquired silently through the product's profile store and credential provider, the
/// run fails rather than prompting when a sign-in would be needed, and the connected organisation must equal
/// DATAVERSE_URL before anything is written (<see cref="LiveTestProfileConnectionSource"/>). Optional PPDS_TEST_SECOND_LCID (a provisioned language other than 1033) for the multi-language
/// tests, which are reported as skipped without it. The detailed console logger is needed to see the "OBSERVED" lines
/// (fault codes, messages, label languages); xunit hides the output of passing tests otherwise.
/// <para>
/// Each test creates its own uniquely named throwaway global choice (the local/status and table/column tests a throwaway custom table)
/// in PPDS_TEST_SOLUTION, prints its name before creating it, and deletes it in a finally block. A failed delete
/// fails the test with the leftover name (or, when the test already failed, is written to the output without masking
/// that failure). Only the generated choices are published (PublishXml naming them); nothing pre-existing is touched.
/// Authoring calls go through the real <see cref="DataverseMetadataAuthoringService"/> used by the CLI, TUI and MCP;
/// setup and read-back use raw SDK requests.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MetadataGlobalChoiceLiveTests : LiveTestBase
{
    private const int English = 1033;
    private const int ObjectDoesNotExist = unchecked((int)0x80040217);

    private readonly ITestOutputHelper _output;
    private DataverseConnectionPool? _pool;
    private IConnectionSource? _source;

    public MetadataGlobalChoiceLiveTests(ITestOutputHelper output) => _output = output;

    private static string Prefix => Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")!.Trim().ToLowerInvariant();
    private static string Solution => Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!.Trim();

    // AC-141: optionset update changes only the requested property and keeps another edit to a property it does not
    // change. Whether a global choice's label edit stays unpublished until PublishXml is recorded, not assumed: on the
    // organisation observed (2026-11-17) an UpdateOptionSet label edit is returned by the published read at once.
    [MetadataLiveFact]
    public async Task ChoiceUpdate_ChangesOnlyTheDisplayName_KeepsAnotherDescriptionEdit()
    {
        var (pool, service) = await ConnectAsync();
        var name = NewName("upd");
        await WithChoiceAsync(pool, name, "Original name", new Label("Published description", English),
            [new Label("One", English), new Label("Two", English)], async () =>
        {
            await PublishChoiceAsync(pool, name);
            var before = await RetrieveChoiceAsync(pool, name, asIfPublished: true);

            // A description edit made by someone else, not published.
            before.Description = new Label("Edited description", English);
            await ExecuteAsync(pool, new UpdateOptionSetRequest { OptionSet = before, MergeLabels = true, SolutionUniqueName = Solution });
            var publishedAfterEdit = Text((await RetrieveChoiceAsync(pool, name, asIfPublished: false)).Description, English);
            var editIsLiveWithoutPublish = publishedAfterEdit == "Edited description";
            _output.WriteLine($"OBSERVED published read after an unpublished UpdateOptionSet description edit: '{publishedAfterEdit}' " +
                (editIsLiveWithoutPublish ? "(the edit is visible without PublishXml)" : "(the edit waits for PublishXml)"));
            publishedAfterEdit.Should().BeOneOf("Edited description", "Published description");

            await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = name, DisplayName = "Renamed" });

            var draft = await RetrieveChoiceAsync(pool, name, asIfPublished: true);
            Text(draft.DisplayName, English).Should().Be("Renamed");
            Text(draft.Description, English).Should().Be("Edited description", "the other edit must not revert to the published copy");
            draft.Options.Select(o => (o.Value, Text(o.Label, English))).Should().BeEquivalentTo(
                before.Options.Select(o => (o.Value, Text(o.Label, English))), "options must be untouched");

            var published = await RetrieveChoiceAsync(pool, name, asIfPublished: false);
            _output.WriteLine($"OBSERVED published read after optionset update: display name '{Text(published.DisplayName, English)}', description '{Text(published.Description, English)}'.");
            if (editIsLiveWithoutPublish)
            {
                // No publish boundary exists to check; the published read must show the same single change.
                Text(published.DisplayName, English).Should().Be("Renamed");
                Text(published.Description, English).Should().Be("Edited description");
            }
            else
            {
                Text(published.DisplayName, English).Should().Be("Original name", "optionset update publishes nothing");
                Text(published.Description, English).Should().Be("Published description", "optionset update publishes nothing");
            }
        });
    }

    // AC-142 (English part): --new-label writes English; colour- and description-only updates change no label.
    [MetadataLiveFact]
    public async Task GlobalOptionUpdate_NewLabelWritesEnglish_ColourAndDescriptionOnlyChangeNoLabel()
    {
        var (pool, service) = await ConnectAsync();
        var name = NewName("lbl");
        await WithChoiceAsync(pool, name, "Label test", new Label("Label test", English), [new Label("Open", English)], async () =>
        {
            var value = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single().Value!.Value;
            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, NewLabel = "Closed", SolutionUniqueName = Solution });
            var option = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
            Text(option.Label, English).Should().Be("Closed");
            await AssertColourAndDescriptionChangeNoLabelAsync(pool, service, name, value);

            // add-option --color: the colour lands on the new option only.
            var added = await service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = name, Label = "Added", Color = "#ABCDEF", SolutionUniqueName = Solution });
            var options = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options;
            options.Single(o => o.Value == added).Color.Should().Be("#ABCDEF", "add-option --color must persist the colour");
            options.Single(o => o.Value == value).Color.Should().Be("#123456", "the other option keeps its colour");
        });
    }

    // AC-142 (MergeLabels): --new-label keeps the second language's label.
    [MetadataLiveMultiLanguageFact]
    public async Task GlobalOptionUpdate_NewLabel_KeepsTheSecondLanguage()
    {
        var (pool, service) = await ConnectAsync();
        var second = await RequireSecondLanguageAsync(pool);
        var name = NewName("lbm");
        await WithChoiceAsync(pool, name, "Label test", new Label("Label test", English), [Labels("Open", (second, "Ouvert"))], async () =>
        {
            var value = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single().Value!.Value;
            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, NewLabel = "Closed", SolutionUniqueName = Solution });
            var option = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
            Text(option.Label, English).Should().Be("Closed");
            Text(option.Label, second).Should().Be("Ouvert", "MergeLabels must keep the other language's label");
            await AssertColourAndDescriptionChangeNoLabelAsync(pool, service, name, value);
        });
    }

    // AC-142 for a local column option and a status reason (English part), on a throwaway custom table.
    [MetadataLiveFact]
    public async Task LocalOptionAndStatusReason_NewLabelWritesEnglish_ColourOnlyChangesNoLabel()
    {
        var (pool, service) = await ConnectAsync();
        await WithTableAsync(pool, Labels("Open", null), async (entity, column) =>
        {
            var option = (await RetrieveOptionsAsync(pool, entity, column)).Single();
            await service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = entity, ColumnLogicalName = column, Value = option.Value, NewLabel = "Closed", SolutionUniqueName = Solution });
            option = (await RetrieveOptionsAsync(pool, entity, column)).Single();
            Text(option.Label, English).Should().Be("Closed");
            await AssertLocalColourChangesNoLabelAsync(pool, service, entity, column, option.Value!.Value);

            var reason = await ActiveReasonAsync(pool, entity);
            await service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = entity, Value = reason.Value, NewLabel = "Live", SolutionUniqueName = Solution });
            Text((await ReasonAsync(pool, entity, reason.Value!.Value)).Label, English).Should().Be("Live");
            await AssertStatusColourChangesNoLabelAsync(pool, service, entity, reason.Value!.Value);
        });
    }

    // AC-142 (MergeLabels) for a local column option and a status reason.
    [MetadataLiveMultiLanguageFact]
    public async Task LocalOptionAndStatusReason_NewLabel_KeepsTheSecondLanguage()
    {
        var (pool, service) = await ConnectAsync();
        var second = await RequireSecondLanguageAsync(pool);
        await WithTableAsync(pool, Labels("Open", (second, "Ouvert")), async (entity, column) =>
        {
            var option = (await RetrieveOptionsAsync(pool, entity, column)).Single();
            Text(option.Label, second).Should().Be("Ouvert", "precondition: the local option carries a second-language label");
            await service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = entity, ColumnLogicalName = column, Value = option.Value, NewLabel = "Closed", SolutionUniqueName = Solution });
            option = (await RetrieveOptionsAsync(pool, entity, column)).Single();
            Text(option.Label, English).Should().Be("Closed");
            Text(option.Label, second).Should().Be("Ouvert", "MergeLabels must keep the other language's local option label");

            // Give the built-in reason an explicit second-language label first, so the check never depends on
            // whatever labels the platform generated.
            var reason = await ActiveReasonAsync(pool, entity);
            var setup = new Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest
            {
                Value = reason.Value!.Value, Label = Labels("Active", (second, "Actif")), MergeLabels = true, SolutionUniqueName = Solution
            };
            setup["EntityLogicalName"] = entity;
            setup["AttributeLogicalName"] = "statuscode";
            await ExecuteAsync(pool, setup);
            Text((await ReasonAsync(pool, entity, reason.Value!.Value)).Label, second).Should().Be("Actif", "precondition: the status reason carries a second-language label");

            await service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = entity, Value = reason.Value, NewLabel = "Live", SolutionUniqueName = Solution });
            var after = await ReasonAsync(pool, entity, reason.Value!.Value);
            Text(after.Label, English).Should().Be("Live");
            Text(after.Label, second).Should().Be("Actif", "MergeLabels must keep the other language's status label");
        });
    }

    // AC-147 (English part): table and typed-column label updates write the requested English label only and leave
    // every unrequested label alone.
    [MetadataLiveFact]
    public async Task TableAndColumnLabelUpdates_WriteEnglish_AndLeaveUnrequestedLabelsAlone()
    {
        var (pool, service) = await ConnectAsync();
        await WithTableAsync(pool, Labels("Open", null), async (entity, column) =>
        {
            var tableBefore = await RetrieveTableAsync(pool, entity);
            var tableResult = await service.UpdateTableAsync(new UpdateTableRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, DisplayName = "PPDS label live test renamed" });
            var table = await RetrieveTableAsync(pool, entity);
            _output.WriteLine($"OBSERVED table after display-name update: display {string.Join(", ", Pairs(table.DisplayName))}; plural {string.Join(", ", Pairs(table.DisplayCollectionName))}; description {string.Join(", ", Pairs(table.Description))}; notices {tableResult.ValidationMessages.Count}");
            Text(table.DisplayName, English).Should().Be("PPDS label live test renamed");
            Pairs(table.DisplayCollectionName).Should().BeEquivalentTo(Pairs(tableBefore.DisplayCollectionName), "the plural name was not requested");
            Pairs(table.Description).Should().BeEquivalentTo(Pairs(tableBefore.Description), "the description was not requested");

            await service.UpdateTableAsync(new UpdateTableRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, PluralDisplayName = "PPDS renamed tests", Description = "Changed description" });
            table = await RetrieveTableAsync(pool, entity);
            Text(table.DisplayName, English).Should().Be("PPDS label live test renamed", "the display name was not requested this time");
            Text(table.DisplayCollectionName, English).Should().Be("PPDS renamed tests");
            Text(table.Description, English).Should().Be("Changed description");

            var columnBefore = await RetrieveColumnAsync(pool, entity, column);
            var columnResult = await service.UpdateColumnAsync(new UpdateColumnRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, ColumnLogicalName = column, Description = "Pick description" });
            var after = await RetrieveColumnAsync(pool, entity, column);
            _output.WriteLine($"OBSERVED column after description update: display {string.Join(", ", Pairs(after.DisplayName))}; description {string.Join(", ", Pairs(after.Description))}; notices {columnResult.ValidationMessages.Count}");
            Text(after.Description, English).Should().Be("Pick description");
            Pairs(after.DisplayName).Should().BeEquivalentTo(Pairs(columnBefore.DisplayName), "the display name was not requested");

            await service.UpdateColumnAsync(new UpdateColumnRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, ColumnLogicalName = column, DisplayName = "Pick renamed" });
            after = await RetrieveColumnAsync(pool, entity, column);
            Text(after.DisplayName, English).Should().Be("Pick renamed");
            Text(after.Description, English).Should().Be("Pick description", "the description was not requested this time");
        });
    }

    // AC-147 (MergeLabels): table and typed-column label updates keep the second language's labels.
    [MetadataLiveMultiLanguageFact]
    public async Task TableAndColumnLabelUpdates_KeepTheSecondLanguage()
    {
        var (pool, service) = await ConnectAsync();
        var second = await RequireSecondLanguageAsync(pool);
        await WithTableAsync(pool, Labels("Open", null), async (entity, column) =>
        {
            // Give the table and the column explicit second-language labels first.
            await ExecuteAsync(pool, new UpdateEntityRequest
            {
                Entity = new EntityMetadata
                {
                    LogicalName = entity,
                    DisplayName = Labels("PPDS label live test", (second, "Test " + second)),
                    DisplayCollectionName = Labels("PPDS label live tests", (second, "Tests " + second)),
                    Description = Labels("Temporary metadata. Safe to delete.", (second, "Description " + second))
                },
                MergeLabels = true, SolutionUniqueName = Solution
            });
            await ExecuteAsync(pool, new UpdateAttributeRequest
            {
                EntityName = entity, MergeLabels = true, SolutionUniqueName = Solution,
                Attribute = new PicklistAttributeMetadata
                {
                    LogicalName = column, DisplayName = Labels("Pick", (second, "Choix " + second)), Description = Labels("Pick description", (second, "Choix description " + second))
                }
            });
            var table = await RetrieveTableAsync(pool, entity);
            Text(table.DisplayName, second).Should().Be("Test " + second, "precondition: the table carries a second-language label");

            await service.UpdateTableAsync(new UpdateTableRequest
            {
                SolutionUniqueName = Solution, EntityLogicalName = entity,
                DisplayName = "Renamed", PluralDisplayName = "Renamed plural", Description = "Renamed description"
            });
            table = await RetrieveTableAsync(pool, entity);
            Text(table.DisplayName, English).Should().Be("Renamed");
            Text(table.DisplayName, second).Should().Be("Test " + second, "MergeLabels must keep the table's other-language display name");
            Text(table.DisplayCollectionName, second).Should().Be("Tests " + second, "MergeLabels must keep the table's other-language plural name");
            Text(table.Description, second).Should().Be("Description " + second, "MergeLabels must keep the table's other-language description");

            await service.UpdateColumnAsync(new UpdateColumnRequest
            {
                SolutionUniqueName = Solution, EntityLogicalName = entity, ColumnLogicalName = column, DisplayName = "Pick renamed", Description = "Changed"
            });
            var after = await RetrieveColumnAsync(pool, entity, column);
            Text(after.DisplayName, English).Should().Be("Pick renamed");
            Text(after.DisplayName, second).Should().Be("Choix " + second, "MergeLabels must keep the column's other-language display name");
            Text(after.Description, second).Should().Be("Choix description " + second, "MergeLabels must keep the column's other-language description");
        });
    }

    // AC-133: which fault Dataverse returns for a mis-cased global choice name, and that the service resolves it.
    [MetadataLiveFact]
    public async Task MisCasedName_ResolvesAndRecordsTheFaultCode()
    {
        var (pool, service) = await ConnectAsync();
        var name = NewName("case");
        await WithChoiceAsync(pool, name, "Case test", new Label("Case test", English), [new Label("One", English), new Label("Two", English)], async () =>
        {
            var stored = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Name;
            var misCased = MisCase(stored);
            _output.WriteLine($"Stored name '{stored}', mis-cased request '{misCased}'.");

            var observed = new List<(string Operation, int? Code, string Outcome)>
            {
                await ObserveAsync(pool, "RetrieveOptionSet (published)", new RetrieveOptionSetRequest { Name = misCased }),
                await ObserveAsync(pool, "RetrieveOptionSet (as-if-published)", new RetrieveOptionSetRequest { Name = misCased, RetrieveAsIfPublished = true }),
                await ObserveAsync(pool, "InsertOptionValue", new InsertOptionValueRequest { OptionSetName = misCased, Label = new Label("Probe", English), SolutionUniqueName = Solution })
            };
            var insertProbeAccepted = observed[2].Code is null;
            // Re-read so the OrderOption probe sends the current values even if the insert probe was accepted.
            var currentValues = (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Select(o => o.Value!.Value).ToArray();
            observed.Add(await ObserveAsync(pool, "OrderOption", new OrderOptionRequest { OptionSetName = misCased, Values = Enumerable.Reverse(currentValues).ToArray(), SolutionUniqueName = Solution }));
            foreach (var (operation, _, outcome) in observed)
                _output.WriteLine($"OBSERVED {operation}: {outcome}");

            observed.Should().Contain(o => o.Code.HasValue,
                "at least one lookup must reject the mis-cased name; if every probe was accepted, the lookup is case-insensitive here and AC-133 was not exercised");

            // The behaviour: every resolving path accepts the case variant.
            var added = await service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = misCased, Label = "Added", SolutionUniqueName = Solution });
            var labels = (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Select(o => Text(o.Label, English)).ToList();
            var expected = new List<string?> { "One", "Two", "Added" };
            if (insertProbeAccepted) expected.Add("Probe");
            labels.Should().BeEquivalentTo(expected, "nothing may be written under a rejected spelling, and the retried add writes exactly once");
            (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Should().ContainSingle(o => o.Value == added);

            await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = misCased, DisplayName = "Case test renamed" });
            await service.ReorderOptionsAsync(new ReorderOptionsRequest
            {
                OptionSetName = misCased, SolutionUniqueName = Solution,
                Order = Enumerable.Reverse((await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Select(o => o.Value!.Value)).ToArray()
            });
            await PublishChoiceAsync(pool, stored);
            (await new DataverseMetadataQueryService(pool).GetOptionSetAsync(misCased)).Name.Should().Be(stored);

            // The assumption: a rejected spelling is reported as ObjectDoesNotExist (0x80040217).
            foreach (var (operation, code, outcome) in observed.Where(o => o.Code.HasValue))
                code.Should().Be(ObjectDoesNotExist,
                    $"the not-found recognition assumes 0x80040217 for {operation}, but Dataverse returned {outcome}");
        });
    }

    // AC-134: a missing solution on add-option surfaces the original error and writes nothing.
    [MetadataLiveFact]
    public async Task MissingSolution_OnAddOption_SurfacesTheErrorAndWritesNothing()
    {
        var (pool, service) = await ConnectAsync();
        var name = NewName("sol");
        var missingSolution = "ppdsmissing" + Guid.NewGuid().ToString("N")[..12];
        await WithChoiceAsync(pool, name, "Solution test", new Label("Solution test", English), [new Label("One", English)], async () =>
        {
            var stored = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Name;
            (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Should().HaveCount(1, "precondition");
            var raw = await ObserveAsync(pool, "InsertOptionValue (missing solution)",
                new InsertOptionValueRequest { OptionSetName = stored, Label = new Label("Probe", English), SolutionUniqueName = missingSolution });
            _output.WriteLine($"OBSERVED {raw.Operation}: {raw.Outcome}");
            raw.Code.Should().Be(ObjectDoesNotExist, "an unknown solution on InsertOptionValue is ObjectDoesNotExist (0x80040217)");
            (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Should().HaveCount(1, "the rejected raw probe must write nothing");

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
                (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Should().HaveCount(1, "nothing may be written");
            }
        });
    }

    // AC-142 clearing rule: what `optionset update --description ""` does to the English description.
    [MetadataLiveFact]
    public async Task EmptyDescription_RecordsWhatDataverseDoes()
    {
        var (pool, service) = await ConnectAsync();
        var name = NewName("clr");
        await WithChoiceAsync(pool, name, "Clear test", new Label("English description", English), [new Label("One", English)], async () =>
        {
            var outcome = await ClearDescriptionAsync(pool, service, name);
            outcome.Should().Be("cleared", "an empty English description sent with MergeLabels clears the English text");
            (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Description.LocalizedLabels
                .Should().ContainSingle(l => l.LanguageCode == English && l.Label == "", "the 1033 entry stays, with empty text");
        });
    }

    // AC-142 clearing rule: the second language's description survives `--description ""`.
    [MetadataLiveMultiLanguageFact]
    public async Task EmptyDescription_KeepsTheSecondLanguage()
    {
        var (pool, service) = await ConnectAsync();
        var second = await RequireSecondLanguageAsync(pool);
        var name = NewName("clm");
        await WithChoiceAsync(pool, name, "Clear test", Labels("English description", (second, "Description " + second)), [new Label("One", English)], async () =>
        {
            var outcome = await ClearDescriptionAsync(pool, service, name);
            outcome.Should().BeOneOf("cleared", "kept", "rejected");
            Text((await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Description, second)
                .Should().Be("Description " + second, "MergeLabels keeps the other language whatever happens to the English text");
        });
    }

    // AC-145 wording: what UserLocalizedLabel holds when the target has no label in the caller's UI language.
    [MetadataLiveMultiLanguageFact]
    public async Task UserLocalizedLabel_WithoutALabelInTheCallersLanguage_IsRecorded()
    {
        var (pool, _) = await ConnectAsync();
        var second = await RequireSecondLanguageAsync(pool);
        var (uiLanguage, baseLanguage) = await CallerAndBaseLanguageAsync(pool);
        var effectiveUi = uiLanguage == 0 ? baseLanguage : uiLanguage;
        var onlyLanguage = effectiveUi == second ? English : second;
        _output.WriteLine($"OBSERVED caller UI language {uiLanguage} (0 = base), base language {baseLanguage}; option labelled only in {onlyLanguage}.");
        var name = NewName("ull");
        await WithChoiceAsync(pool, name, "User label test", new Label("User label test", English), [new Label("Only", onlyLanguage)], async () =>
        {
            foreach (var asIfPublished in new[] { true, false })
            {
                if (!asIfPublished) await PublishChoiceAsync(pool, name);
                var label = (await RetrieveChoiceAsync(pool, name, asIfPublished)).Options.Single().Label;
                var shown = label.UserLocalizedLabel;
                _output.WriteLine($"OBSERVED UserLocalizedLabel ({(asIfPublished ? "as-if-published" : "published")}): " +
                    (shown is null ? "null" : $"LCID {shown.LanguageCode} '{shown.Label}'") + $"; localized labels: {string.Join(", ", Pairs(label))}");
                if (shown is not null)
                    label.LocalizedLabels.Select(l => l.LanguageCode).Should().Contain(shown.LanguageCode,
                        "a user-localised label must be one of the option's own labels");
            }
        });
    }

    // AC-146 raw mechanisms for a local option, a status reason and a state value, one variable per step: the
    // OptionMetadata.Color carrier through UpdateAttribute is ignored (also after PublishXml); the Color parameter of
    // UpdateOptionValue / InsertOptionValue / InsertStatusValue is stored without a solution, alone or with a Label and
    // Description; an omitted Label, Description or Color leaves that value unchanged.
    [MetadataLiveFact]
    public async Task ColourMechanisms_LocalOptionStatusReasonAndStateValue()
    {
        var (pool, _) = await ConnectAsync();
        await WithTableAsync(pool, Labels("Open", null), async (entity, column) =>
        {
            async Task<OptionMetadata> Opt(string attribute, int value, bool asIfPublished = true)
                => (await RetrieveOptionsAsync(pool, entity, attribute, asIfPublished)).Single(o => o.Value == value);
            async Task Expect(string step, string attribute, int value, string? color, string label, string? description)
            {
                var option = await Opt(attribute, value);
                _output.WriteLine($"OBSERVED {step}: color {option.Color ?? "null"}, label '{Text(option.Label, English)}', description '{Text(option.Description, English)}'");
                option.Color.Should().Be(color, step);
                Text(option.Label, English).Should().Be(label, step);
                Text(option.Description, English).Should().Be(description, step);
            }
            Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest Update(string attribute, int value, string? solution)
            {
                var request = new Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest { Value = value, MergeLabels = true, SolutionUniqueName = solution };
                request["EntityLogicalName"] = entity;
                request["AttributeLogicalName"] = attribute;
                return request;
            }
            Task Publish() => ExecuteAsync(pool, new PublishXmlRequest { ParameterXml = $"<importexportxml><entities><entity>{entity}</entity></entities></importexportxml>" });
            async Task Carrier(string attribute, int value, string color)
            {
                var unchanged = (await Opt(attribute, value, asIfPublished: false)).Color;
                var read = (EnumAttributeMetadata)((RetrieveAttributeResponse)await ExecuteAsync(pool,
                    new RetrieveAttributeRequest { EntityLogicalName = entity, LogicalName = attribute, RetrieveAsIfPublished = true })).AttributeMetadata;
                read.OptionSet.Options.Single(o => o.Value == value).Color = color;
                EnumAttributeMetadata carrier = attribute == "statuscode" ? new StatusAttributeMetadata() : new PicklistAttributeMetadata();
                carrier.LogicalName = read.LogicalName; carrier.SchemaName = read.SchemaName; carrier.OptionSet = read.OptionSet;
                await ExecuteAsync(pool, new UpdateAttributeRequest { EntityName = entity, Attribute = carrier, SolutionUniqueName = Solution });
                await Publish();
                var published = (await Opt(attribute, value, asIfPublished: false)).Color;
                _output.WriteLine($"OBSERVED {attribute} UpdateAttribute carrier {color}, then PublishXml: published color {published ?? "null"}");
                published.Should().Be(unchanged, "UpdateAttribute ignores an existing option's colour even after publishing");
            }

            // Local option.
            var local = (await RetrieveOptionsAsync(pool, entity, column)).Single().Value!.Value;
            var description = Update(column, local, null);
            description.Description = new Label("LD", English);
            await ExecuteAsync(pool, description);
            await Expect("local UpdateOptionValue Description only", column, local, null, "Open", "LD");
            await Carrier(column, local, "#B00002");
            await Expect("local after the UpdateAttribute carrier (draft)", column, local, null, "Open", "LD");
            var colour = Update(column, local, null);
            colour["Color"] = "#B00003";
            await ExecuteAsync(pool, colour);
            await Expect("local UpdateOptionValue Color only, no solution", column, local, "#B00003", "Open", "LD");
            var all = Update(column, local, Solution);
            all.Label = new Label("Open renamed", English); all.Description = new Label("LD2", English); all["Color"] = "#B00004";
            await ExecuteAsync(pool, all);
            await Expect("local UpdateOptionValue Label + Description + Color", column, local, "#B00004", "Open renamed", "LD2");
            var insert = new InsertOptionValueRequest { Label = new Label("Second", English), Description = new Label("LDI", English), Value = local + 1 };
            insert["EntityLogicalName"] = entity; insert["AttributeLogicalName"] = column; insert["Color"] = "#B00005";
            await ExecuteAsync(pool, insert);
            await Expect("local InsertOptionValue with Color, no solution", column, local + 1, "#B00005", "Second", "LDI");

            // Status reason.
            var reason = (await ActiveReasonAsync(pool, entity)).Value!.Value;
            var statusDescription = Update("statuscode", reason, null);
            statusDescription.Description = new Label("SD", English);
            await ExecuteAsync(pool, statusDescription);
            await Expect("status UpdateOptionValue Description only", "statuscode", reason, null, "Active", "SD");
            await Carrier("statuscode", reason, "#C00001");
            await Expect("status after the UpdateAttribute carrier (draft)", "statuscode", reason, null, "Active", "SD");
            var statusColour = Update("statuscode", reason, null);
            statusColour["Color"] = "#C00002";
            await ExecuteAsync(pool, statusColour);
            await Expect("status UpdateOptionValue Color only, no solution", "statuscode", reason, "#C00002", "Active", "SD");
            var statusAll = Update("statuscode", reason, Solution);
            statusAll.Label = new Label("Live", English); statusAll.Description = new Label("SD2", English); statusAll["Color"] = "#C00003";
            await ExecuteAsync(pool, statusAll);
            await Expect("status UpdateOptionValue Label + Description + Color", "statuscode", reason, "#C00003", "Live", "SD2");
            var statusInsert = new InsertStatusValueRequest
            {
                EntityLogicalName = entity, AttributeLogicalName = "statuscode", StateCode = 0, Value = local + 2,
                Label = new Label("Extra", English), Description = new Label("SDI", English)
            };
            statusInsert["Color"] = "#C00004";
            await ExecuteAsync(pool, statusInsert);
            await Expect("status InsertStatusValue with Color, no solution", "statuscode", local + 2, "#C00004", "Extra", "SDI");

            // State value (statecode): the generic option update can reach it with a colour.
            var state = Update("statecode", 0, null);
            state["Color"] = "#D00001";
            await ExecuteAsync(pool, state);
            await Expect("statecode UpdateOptionValue Color only, no solution", "statecode", 0, "#D00001", "Active", null);

            // Empty solution on the local and status messages: rejected with a fault, nothing written.
            async Task RejectsEmptySolution(string what, OrganizationRequest request)
            {
                var fault = await Record.ExceptionAsync(() => ExecuteAsync(pool, request));
                _output.WriteLine($"OBSERVED {what} with SolutionUniqueName \"\": {(fault is null ? "accepted" : fault.Message)}");
                fault.Should().NotBeNull(what);
                FindFault(fault!).Should().NotBeNull(what);
                fault!.Message.Should().Contain("solution name is not valid", what);
            }
            var localDelete = new Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest { Value = local + 1, SolutionUniqueName = "" };
            localDelete["EntityLogicalName"] = entity; localDelete["AttributeLogicalName"] = column;
            await RejectsEmptySolution("local DeleteOptionValue", localDelete);
            (await RetrieveOptionsAsync(pool, entity, column)).Should().Contain(o => o.Value == local + 1);
            await RejectsEmptySolution("InsertStatusValue", new InsertStatusValueRequest
            {
                EntityLogicalName = entity, AttributeLogicalName = "statuscode", StateCode = 0, Label = new Label("Empty", English), SolutionUniqueName = ""
            });
            var statusDelete = new Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest { Value = local + 2, SolutionUniqueName = "" };
            statusDelete["EntityLogicalName"] = entity; statusDelete["AttributeLogicalName"] = "statuscode";
            await RejectsEmptySolution("status DeleteOptionValue", statusDelete);
            (await RetrieveOptionsAsync(pool, entity, "statuscode")).Should().Contain(o => o.Value == local + 2);

            // Non-canonical colour text is stored verbatim on local, status and state options too (update and insert).
            string[] formats = ["#ff0000", "#F00", "FF0000", "Red"];
            for (var i = 0; i < formats.Length; i++)
            {
                var input = formats[i];
                foreach (var (what, attribute, value) in new[] { ("local", column, local), ("status", "statuscode", reason), ("statecode", "statecode", 0) })
                {
                    var update = Update(attribute, value, null);
                    update["Color"] = input;
                    await ExecuteAsync(pool, update);
                    var stored = (await Opt(attribute, value)).Color;
                    _output.WriteLine($"OBSERVED {what} UpdateOptionValue Color '{input}': stored '{stored}'");
                    stored.Should().Be(input, $"{what} UpdateOptionValue stores the colour verbatim");
                }
                var localInsert = new InsertOptionValueRequest { Label = new Label("Fmt " + i, English), Value = local + 10 + i };
                localInsert["EntityLogicalName"] = entity; localInsert["AttributeLogicalName"] = column; localInsert["Color"] = input;
                await ExecuteAsync(pool, localInsert);
                var localStored = (await Opt(column, local + 10 + i)).Color;
                _output.WriteLine($"OBSERVED local InsertOptionValue Color '{input}': stored '{localStored}'");
                localStored.Should().Be(input, "local InsertOptionValue stores the colour verbatim");
                var statusInsertFormat = new InsertStatusValueRequest
                {
                    EntityLogicalName = entity, AttributeLogicalName = "statuscode", StateCode = 0, Value = local + 20 + i, Label = new Label("Fmt " + i, English)
                };
                statusInsertFormat["Color"] = input;
                await ExecuteAsync(pool, statusInsertFormat);
                var statusStored = (await Opt("statuscode", local + 20 + i)).Color;
                _output.WriteLine($"OBSERVED status InsertStatusValue Color '{input}': stored '{statusStored}'");
                statusStored.Should().Be(input, "InsertStatusValue stores the colour verbatim");
            }

            // A multi-select (Choices) column: the Color parameter is stored there too.
            var choices = $"{Prefix}_multi{Guid.NewGuid().ToString("N")[..6]}".ToLowerInvariant();
            var multi = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
            multi.Options.Add(new OptionMetadata(new Label("Red", English), null));
            await ExecuteAsync(pool, new CreateAttributeRequest
            {
                EntityName = entity, SolutionUniqueName = Solution,
                Attribute = new MultiSelectPicklistAttributeMetadata
                {
                    SchemaName = choices, DisplayName = new Label("Multi", English), OptionSet = multi,
                    RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
                }
            });
            var multiValue = (await RetrieveOptionsAsync(pool, entity, choices)).Single().Value!.Value;
            var multiColour = Update(choices, multiValue, null);
            multiColour["Color"] = "#E00001";
            await ExecuteAsync(pool, multiColour);
            await Expect("multi-select UpdateOptionValue Color only, no solution", choices, multiValue, "#E00001", "Red", null);
        });
    }

    // AC-146 through the service, in the request shapes production sends: the colour rides on the same request as the
    // label/description, with no solution (null) or an empty one (sent as none); add-option/add-statusreason carry it
    // on the insert; a colour-only update leaves the label and the description alone.
    [MetadataLiveFact]
    public async Task ServiceColourWrites_KeepDescriptionsAndLabels_WithoutASolution()
    {
        var (pool, service) = await ConnectAsync();
        var name = NewName("svc");
        await WithChoiceAsync(pool, name, "Service colour test", new Label("Service colour test", English), [new Label("One", English)], async () =>
        {
            var one = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single().Value!.Value;
            async Task<OptionMetadata> Option(int value) => (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single(o => o.Value == value);

            // Description + colour with the request DTO's default SolutionUniqueName (""), which is sent as none.
            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = one, Description = "Kept description", Color = "#112233" });
            var option = await Option(one);
            _output.WriteLine($"OBSERVED service update-option --description --color, empty solution: color {option.Color}, label '{Text(option.Label, English)}', description '{Text(option.Description, English)}'");
            option.Color.Should().Be("#112233"); Text(option.Description, English).Should().Be("Kept description"); Text(option.Label, English).Should().Be("One");

            // A colour-only update afterwards keeps the description and the label.
            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = one, Color = "#445566", SolutionUniqueName = Solution });
            option = await Option(one);
            option.Color.Should().Be("#445566"); Text(option.Description, English).Should().Be("Kept description", "a colour-only update keeps the description"); Text(option.Label, English).Should().Be("One");

            // --new-label and --color in one command land together.
            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = one, NewLabel = "One renamed", Color = "#778899" });
            option = await Option(one);
            option.Color.Should().Be("#778899"); Text(option.Label, English).Should().Be("One renamed"); Text(option.Description, English).Should().Be("Kept description");

            // add-option --description D --color C with no solution.
            var added = await service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = name, Label = "Added", Description = "Added description", Color = "#ABCDEF" });
            var addedOption = await Option(added);
            _output.WriteLine($"OBSERVED service add-option --description --color, empty solution: color {addedOption.Color}, description '{Text(addedOption.Description, English)}'");
            addedOption.Color.Should().Be("#ABCDEF"); Text(addedOption.Description, English).Should().Be("Added description");
        });

        await WithTableAsync(pool, Labels("Open", null), async (entity, column) =>
        {
            var local = (await RetrieveOptionsAsync(pool, entity, column)).Single().Value!.Value;
            var setDescription = new Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest { Value = local, MergeLabels = true, Description = new Label("Local description", English) };
            setDescription["EntityLogicalName"] = entity; setDescription["AttributeLogicalName"] = column;
            await ExecuteAsync(pool, setDescription);
            await service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = entity, ColumnLogicalName = column, Value = local, Color = "#010203" });
            var option = (await RetrieveOptionsAsync(pool, entity, column)).Single(o => o.Value == local);
            _output.WriteLine($"OBSERVED service attribute update-option --color, no solution: color {option.Color}, label '{Text(option.Label, English)}', description '{Text(option.Description, English)}'");
            option.Color.Should().Be("#010203"); Text(option.Description, English).Should().Be("Local description"); Text(option.Label, English).Should().Be("Open");

            var addedLocal = await service.AddColumnOptionAsync(new AddColumnOptionRequest { EntityLogicalName = entity, ColumnLogicalName = column, Label = "Second", Value = local + 1, Color = "#040506", SolutionUniqueName = " " });
            (await RetrieveOptionsAsync(pool, entity, column)).Single(o => o.Value == addedLocal).Color.Should().Be("#040506");

            var reason = (await ActiveReasonAsync(pool, entity)).Value!.Value;
            var setStatusDescription = new Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest { Value = reason, MergeLabels = true, Description = new Label("Status description", English) };
            setStatusDescription["EntityLogicalName"] = entity; setStatusDescription["AttributeLogicalName"] = "statuscode";
            await ExecuteAsync(pool, setStatusDescription);
            await service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = entity, Value = reason, Color = "#070809" });
            var status = await ReasonAsync(pool, entity, reason);
            _output.WriteLine($"OBSERVED service update-statusreason --color, no solution: color {status.Color}, label '{Text(status.Label, English)}', description '{Text(status.Description, English)}'");
            status.Color.Should().Be("#070809"); Text(status.Description, English).Should().Be("Status description"); Text(status.Label, English).Should().Be("Active");

            var addedReason = await service.AddStatusReasonAsync(new AddStatusReasonRequest { EntityLogicalName = entity, Label = "Extra", StateCode = 0, Value = local + 2, Color = "#0A0B0C" });
            (await ReasonAsync(pool, entity, addedReason)).Color.Should().Be("#0A0B0C");
        });
    }

    // ───── shared assertions ─────

    private async Task AssertColourAndDescriptionChangeNoLabelAsync(IDataverseConnectionPool pool, DataverseMetadataAuthoringService service, string name, int value)
    {
        var labelsBefore = Pairs((await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single().Label);
        await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, Color = "#123456", SolutionUniqueName = Solution });
        var afterColour = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
        Pairs(afterColour.Label).Should().BeEquivalentTo(labelsBefore, "a colour-only update must change no label");
        afterColour.Color.Should().Be("#123456", "the colour must persist");

        await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, Description = "Only the description", SolutionUniqueName = Solution });
        var afterDescription = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
        Pairs(afterDescription.Label).Should().BeEquivalentTo(labelsBefore, "a description-only update must change no label");
        Text(afterDescription.Description, English).Should().Be("Only the description");
    }

    private static async Task AssertLocalColourChangesNoLabelAsync(IDataverseConnectionPool pool, DataverseMetadataAuthoringService service, string entity, string column, int value)
    {
        var before = Pairs((await RetrieveOptionsAsync(pool, entity, column)).Single().Label);
        await service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = entity, ColumnLogicalName = column, Value = value, Color = "#654321", SolutionUniqueName = Solution });
        var after = (await RetrieveOptionsAsync(pool, entity, column)).Single();
        Pairs(after.Label).Should().BeEquivalentTo(before, "a colour-only update must change no label");
        after.Color.Should().Be("#654321", "the colour must persist");
    }

    private static async Task AssertStatusColourChangesNoLabelAsync(IDataverseConnectionPool pool, DataverseMetadataAuthoringService service, string entity, int value)
    {
        var before = Pairs((await ReasonAsync(pool, entity, value)).Label);
        await service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = entity, Value = value, Color = "#0A0B0C", SolutionUniqueName = Solution });
        var after = await ReasonAsync(pool, entity, value);
        Pairs(after.Label).Should().BeEquivalentTo(before, "a colour-only status update must change no label");
        after.Color.Should().Be("#0A0B0C", "the colour must persist");
    }

    /// <summary>Runs `--description ""` and classifies the result; any non-Dataverse failure is rethrown.</summary>
    private async Task<string> ClearDescriptionAsync(IDataverseConnectionPool pool, DataverseMetadataAuthoringService service, string name)
    {
        _output.WriteLine($"Description before: {string.Join(", ", Pairs((await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Description))}");
        try
        {
            await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = name, Description = "" });
        }
        catch (Exception ex) when (FindFault(ex) is not null)
        {
            _output.WriteLine($"OBSERVED optionset update --description \"\": rejected, {Describe(ex)}");
            return "rejected";
        }

        var after = await RetrieveChoiceAsync(pool, name, asIfPublished: true);
        var english = Text(after.Description, English);
        _output.WriteLine($"OBSERVED optionset update --description \"\": succeeded; description after: {string.Join(", ", Pairs(after.Description))}");
        return string.IsNullOrEmpty(english) ? "cleared" : english == "English description" ? "kept" : $"unexpected English text '{english}'";
    }

    // ───── lifecycle ─────

    private async Task<(DataverseConnectionPool Pool, DataverseMetadataAuthoringService Service)> ConnectAsync()
    {
        var guard = new EnvironmentShakedownGuard();
        guard.EnsureCanMutate("livetests.metadata.setup"); // before any raw schema write
        _source = Configuration.HasClientSecretCredentials
            ? await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "MetadataGlobalChoiceLiveTests", maxPoolSize: 3)
            : await LiveTestProfileConnectionSource.CreateAsync(Configuration, "MetadataGlobalChoiceLiveTests", maxPoolSize: 3);
        _pool = LiveTestHelpers.CreateConnectionPool(new[] { _source });
        return (_pool, new DataverseMetadataAuthoringService(_pool, new SchemaValidator(), guard));
    }

    /// <summary>
    /// Creates an owned object, runs <paramref name="body"/>, then deletes the object. The name is printed before
    /// creation. A create that fails without a Dataverse fault (e.g. a client timeout after the server committed) is
    /// followed by a best-effort delete of that exact name. A failed delete fails the test with the leftover name,
    /// unless the body already failed, in which case it is written to the output and the body's failure stands.
    /// </summary>
    private async Task RunOwnedAsync(string what, Func<Task> create, Func<Task> delete, Func<Task> body)
    {
        _output.WriteLine($"Creating {what} in solution '{Solution}'.");
        try
        {
            await create();
        }
        catch (Exception ex) when (FindFault(ex) is null)
        {
            _output.WriteLine($"Create of {what} failed without a Dataverse fault ({ex.Message}); attempting a best-effort delete.");
            try { await delete(); _output.WriteLine($"Best-effort delete of {what} succeeded."); }
            catch (Exception cleanup) { _output.WriteLine($"Best-effort delete of {what} failed (it may not exist; if it does, delete it manually): {cleanup.Message}"); }
            throw;
        }

        var bodyFailed = false;
        try
        {
            await body();
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
                await delete();
                _output.WriteLine($"Deleted {what}.");
            }
            catch (Exception cleanup)
            {
                var message = $"Cleanup failed: {what} was left in the environment; delete it manually. {cleanup.GetType().Name}: {cleanup.Message}";
                _output.WriteLine(message);
                if (!bodyFailed)
                    throw new XunitException(message);
            }
        }
    }

    private Task WithChoiceAsync(IDataverseConnectionPool pool, string name, string displayName, Label description, Label[] options, Func<Task> body)
    {
        var set = new OptionSetMetadata
        {
            Name = name, IsGlobal = true, OptionSetType = OptionSetType.Picklist,
            DisplayName = new Label(displayName, English), Description = description
        };
        foreach (var label in options)
            set.Options.Add(new OptionMetadata(label, null));
        return RunOwnedAsync($"global choice '{name}'",
            () => ExecuteAsync(pool, new CreateOptionSetRequest { OptionSet = set, SolutionUniqueName = Solution }),
            () => ExecuteAsync(pool, new DeleteOptionSetRequest { Name = name }),
            body);
    }

    /// <summary>A throwaway user-owned table with one local picklist column carrying <paramref name="optionLabel"/>.</summary>
    private Task WithTableAsync(IDataverseConnectionPool pool, Label optionLabel, Func<string, string, Task> body)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var table = $"{Prefix}_ppdslbl{suffix}";
        var column = $"{Prefix}_pick{suffix}";
        var entity = table.ToLowerInvariant();
        return RunOwnedAsync($"table '{entity}'",
            () => ExecuteAsync(pool, new CreateEntityRequest
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
            }),
            () => ExecuteAsync(pool, new DeleteEntityRequest { LogicalName = entity }),
            async () =>
            {
                var local = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
                local.Options.Add(new OptionMetadata(optionLabel, null));
                await ExecuteAsync(pool, new CreateAttributeRequest
                {
                    EntityName = entity, SolutionUniqueName = Solution,
                    Attribute = new PicklistAttributeMetadata
                    {
                        SchemaName = column, DisplayName = new Label("Pick", English), OptionSet = local,
                        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None)
                    }
                });
                await body(entity, column.ToLowerInvariant());
            });
    }

    // ───── helpers ─────

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

    private static Task PublishChoiceAsync(IDataverseConnectionPool pool, string name) => ExecuteAsync(pool, new PublishXmlRequest
    {
        ParameterXml = $"<importexportxml><optionsets><optionset>{name}</optionset></optionsets></importexportxml>"
    });

    private static async Task<OptionSetMetadata> RetrieveChoiceAsync(IDataverseConnectionPool pool, string name, bool asIfPublished)
        => (OptionSetMetadata)((RetrieveOptionSetResponse)await ExecuteAsync(pool,
            new RetrieveOptionSetRequest { Name = name, RetrieveAsIfPublished = asIfPublished })).OptionSetMetadata;

    private static async Task<EntityMetadata> RetrieveTableAsync(IDataverseConnectionPool pool, string entity)
        => ((RetrieveEntityResponse)await ExecuteAsync(pool,
            new RetrieveEntityRequest { LogicalName = entity, EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true })).EntityMetadata;

    private static async Task<AttributeMetadata> RetrieveColumnAsync(IDataverseConnectionPool pool, string entity, string attribute)
        => ((RetrieveAttributeResponse)await ExecuteAsync(pool,
            new RetrieveAttributeRequest { EntityLogicalName = entity, LogicalName = attribute, RetrieveAsIfPublished = true })).AttributeMetadata;

    private static async Task<IReadOnlyList<OptionMetadata>> RetrieveOptionsAsync(IDataverseConnectionPool pool, string entity, string attribute, bool asIfPublished = true)
    {
        var response = (RetrieveAttributeResponse)await ExecuteAsync(pool,
            new RetrieveAttributeRequest { EntityLogicalName = entity, LogicalName = attribute, RetrieveAsIfPublished = asIfPublished });
        return ((EnumAttributeMetadata)response.AttributeMetadata).OptionSet.Options;
    }

    private static async Task<StatusOptionMetadata> ActiveReasonAsync(IDataverseConnectionPool pool, string entity)
        => (await RetrieveOptionsAsync(pool, entity, "statuscode")).OfType<StatusOptionMetadata>().First(o => o.State == 0);

    private static async Task<OptionMetadata> ReasonAsync(IDataverseConnectionPool pool, string entity, int value)
        => (await RetrieveOptionsAsync(pool, entity, "statuscode")).First(o => o.Value == value);

    /// <summary>PPDS_TEST_SECOND_LCID, verified to be provisioned; fails (not skips) when it is not.</summary>
    private static async Task<int> RequireSecondLanguageAsync(IDataverseConnectionPool pool)
    {
        var second = MetadataLivePrerequisites.SecondLanguage!.Value;
        var languages = ((RetrieveProvisionedLanguagesResponse)await ExecuteAsync(pool, new RetrieveProvisionedLanguagesRequest())).RetrieveProvisionedLanguages;
        if (!languages.Contains(second))
            throw new XunitException($"PPDS_TEST_SECOND_LCID={second} is not provisioned in this organisation (provisioned: {string.Join(", ", languages)}).");
        if (!languages.Contains(English))
            throw new XunitException($"LCID 1033 is not provisioned in this organisation (provisioned: {string.Join(", ", languages)}); these tests write English labels.");
        return second;
    }

    private static async Task<(int UiLanguage, int BaseLanguage)> CallerAndBaseLanguageAsync(IDataverseConnectionPool pool)
    {
        await using var client = await pool.GetClientAsync();
        var userId = ((WhoAmIResponse)await client.ExecuteAsync(new WhoAmIRequest())).UserId;
        var settings = new QueryExpression("usersettings") { ColumnSet = new ColumnSet("uilanguageid") };
        settings.Criteria.AddCondition("systemuserid", ConditionOperator.Equal, userId);
        var ui = (await client.RetrieveMultipleAsync(settings)).Entities.FirstOrDefault()?.GetAttributeValue<int>("uilanguageid") ?? 0;
        var org = (await client.RetrieveMultipleAsync(new QueryExpression("organization") { ColumnSet = new ColumnSet("languagecode") })).Entities.First();
        return (ui, org.GetAttributeValue<int>("languagecode"));
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

    /// <summary>
    /// Honours PPDS_SHAKEDOWN=1 like the production guard (the sentinel-file activation is not checked). It is also
    /// called before the first raw schema write, so an active shakedown stops the test before anything is created.
    /// </summary>
    private sealed class EnvironmentShakedownGuard : IShakedownGuard
    {
        public void EnsureCanMutate(string operationDescription)
        {
            if (Environment.GetEnvironmentVariable("PPDS_SHAKEDOWN") == "1")
                throw new PpdsException(ErrorCodes.Safety.ShakedownActive, $"Shakedown active: '{operationDescription}' refused.");
        }
    }
}
