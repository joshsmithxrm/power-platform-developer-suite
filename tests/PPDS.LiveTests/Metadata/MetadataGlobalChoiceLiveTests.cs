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
/// AC-134, AC-141, AC-142, AC-145 and "Label languages"). Run against a DEVELOPMENT environment only:
/// <code>
/// dotnet test tests/PPDS.LiveTests/PPDS.LiveTests.csproj -f net8.0 --filter "FullyQualifiedName~MetadataGlobalChoiceLiveTests" --logger "console;verbosity=detailed"
/// </code>
/// Settings: DATAVERSE_URL, PPDS_TEST_APP_ID, PPDS_TEST_CLIENT_SECRET, PPDS_TEST_TENANT_ID, PPDS_TEST_SOLUTION,
/// PPDS_TEST_PREFIX. For a local run without a secret, set DATAVERSE_URL and PPDS_TEST_PROFILE (the name of an existing,
/// already signed-in PPDS auth profile bound to that environment) instead of the three PPDS_TEST_APP_ID/CLIENT_SECRET/
/// TENANT_ID settings: the token is acquired silently through the product's profile store and credential provider, the
/// run fails rather than prompting when a sign-in would be needed, and the connected organisation must equal
/// DATAVERSE_URL before anything is written (<see cref="ProfileConnectionSource"/>). Optional PPDS_TEST_SECOND_LCID (a provisioned language other than 1033) for the multi-language
/// tests, which are reported as skipped without it. The detailed console logger is needed to see the "OBSERVED" lines
/// (fault codes, messages, label languages); xunit hides the output of passing tests otherwise.
/// <para>
/// Each test creates its own uniquely named throwaway global choice (the local/status tests a throwaway custom table)
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
            raw.Code.Should().NotBeNull("Dataverse must reject an unknown solution");
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
            outcome.Should().BeOneOf("cleared", "kept", "rejected");
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
            : await ProfileConnectionSource.CreateAsync(Configuration, "MetadataGlobalChoiceLiveTests", maxPoolSize: 3);
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

    private static async Task<IReadOnlyList<OptionMetadata>> RetrieveOptionsAsync(IDataverseConnectionPool pool, string entity, string attribute)
    {
        var response = (RetrieveAttributeResponse)await ExecuteAsync(pool,
            new RetrieveAttributeRequest { EntityLogicalName = entity, LogicalName = attribute, RetrieveAsIfPublished = true });
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
