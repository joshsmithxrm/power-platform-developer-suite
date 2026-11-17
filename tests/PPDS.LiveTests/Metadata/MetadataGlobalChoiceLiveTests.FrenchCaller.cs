using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Sdk;
using PpdsUpdateOptionValue = PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest;
using SdkUpdateOptionValue = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;

namespace PPDS.LiveTests.Metadata;

/// <summary>
/// Live checks for a caller whose UI language is not the organisation's base language (specs/metadata-authoring.md
/// "Label languages", AC-142, AC-145, AC-147, AC-133). Each test builds throwaway components as the usual base-language
/// caller, then switches the CALLING user's <c>usersettings.uilanguageid</c> to PPDS_TEST_SECOND_LCID with
/// <see cref="CallerUiLanguageSwitch"/>, runs its checks, and restores the original value in a finally block before the
/// components are deleted. Opt-in only (PPDS_TEST_SWITCH_UI_LANGUAGE=1, <see cref="MetadataLiveSwitchUiLanguageFactAttribute"/>);
/// run these tests on their own:
/// <code>
/// dotnet test tests/PPDS.LiveTests/PPDS.LiveTests.csproj -f net8.0 --filter "FullyQualifiedName~MetadataGlobalChoiceLiveTests.FrenchCaller_" --logger "console;verbosity=detailed"
/// </code>
/// <para>
/// Which connection sees the new UI language is recorded, not assumed. Dataverse resolves <c>UserLocalizedLabel</c>
/// on the server from the caller's user settings; nothing on the client (ServiceClient or the PPDS pool) carries a UI
/// language, but the server may cache user settings. After the switch the test opens a FRESH connection pool and polls
/// a published label that exists in both languages on it and on the pool opened before the switch, printing an
/// OBSERVED line for each read, until the fresh pool serves the target language three reads in a row (up to 3 minutes).
/// Every French-caller read and write then goes through that fresh pool. After the restore the same probe must serve
/// the original language again, so a stale server cache cannot leak into the next test.
/// </para>
/// </summary>
public sealed partial class MetadataGlobalChoiceLiveTests
{
    private static readonly TimeSpan ServedLanguageTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The throwaway components every French-caller test starts from.</summary>
    private sealed record FrenchScene(
        string Choice, int Open, int Same, int EnglishOnly, int FrenchOnly,
        string Entity, string Column, string NameColumn, int LocalOpen, int Reason);

    /// <summary>A connection (and the real authoring service on it) that serves the switched UI language.</summary>
    private sealed record FrenchCaller(DataverseConnectionPool Pool, DataverseMetadataAuthoringService Service, int Lcid);

    // (a) and (b): what UserLocalizedLabel is for a caller whose UI language is the second language.
    [MetadataLiveSwitchUiLanguageFact]
    public Task FrenchCaller_UserLocalizedLabel_IsTheCallersLanguage_AndFallsBackToTheBaseLanguage() => FrenchSceneAsync(async (scene, french) =>
    {
        var fr = french.Lcid;
        foreach (var asIfPublished in new[] { false, true })
        {
            var view = asIfPublished ? "as-if-published" : "published";
            var choice = await RetrieveChoiceAsync(french.Pool, scene.Choice, asIfPublished);
            Shown($"choice display name ({view})", choice.DisplayName).Should().Be(((int?)fr, "Choix FR"), "a label that exists in the caller's language is shown in it");
            Shown($"choice description ({view})", choice.Description).Should().Be(((int?)fr, "Description FR"));
            Label OptionLabel(int value) => choice.Options.Single(o => o.Value == value).Label;
            Shown($"option with both labels ({view})", OptionLabel(scene.Open)).Should().Be(((int?)fr, "Ouvert"));
            Text(OptionLabel(scene.EnglishOnly), fr).Should().BeNull("precondition: the English-only option has no French label");
            Shown($"option with an English label only ({view})", OptionLabel(scene.EnglishOnly))
                .Should().Be(((int?)English, "English only"), "without a label in the caller's language the base-language label is shown (the spec's believed fallback)");
            Shown($"option with French text and empty English text ({view})", OptionLabel(scene.FrenchOnly))
                .Should().Be(((int?)fr, "Seulement"), "the caller's-language label is shown when it has text");

            var table = await ReadTableAsync(french.Pool, scene.Entity, asIfPublished);
            Shown($"table display name ({view})", table.DisplayName).Should().Be(((int?)fr, "Test FR"));
            Shown($"table plural name ({view})", table.DisplayCollectionName).Should().Be(((int?)fr, "Tests FR"));
            Text(table.Description, fr).Should().BeNull("precondition: the table description has no French label");
            Shown($"table description, English only ({view})", table.Description)
                .Should().Be(((int?)English, "Temporary metadata. Safe to delete."), "without a label in the caller's language the base-language label is shown");

            var column = await ReadColumnAsync(french.Pool, scene.Entity, scene.Column, asIfPublished);
            Shown($"column display name ({view})", column.DisplayName).Should().Be(((int?)fr, "Choix FR"));
            Shown($"column description ({view})", column.Description).Should().Be(((int?)fr, "Choix description FR"));
            var nameColumn = await ReadColumnAsync(french.Pool, scene.Entity, scene.NameColumn, asIfPublished);
            Text(nameColumn.DisplayName, fr).Should().BeNull("precondition: the primary name column has no French label");
            Shown($"primary name column display name, English only ({view})", nameColumn.DisplayName)
                .Should().Be(((int?)English, "Name"), "without a label in the caller's language the base-language label is shown");

            var local = (await RetrieveOptionsAsync(french.Pool, scene.Entity, scene.Column, asIfPublished)).Single(o => o.Value == scene.LocalOpen);
            Shown($"local option ({view})", local.Label).Should().Be(((int?)fr, "Ouvert"));
            var reason = (await RetrieveOptionsAsync(french.Pool, scene.Entity, "statuscode", asIfPublished)).Single(o => o.Value == scene.Reason);
            Shown($"status reason ({view})", reason.Label).Should().Be(((int?)fr, "Actif"));
        }
    });

    // (c) and (d) through the service: ENGLISH_LABEL_ONLY notices, their suppression, and what is written.
    [MetadataLiveSwitchUiLanguageFact]
    public Task FrenchCaller_EnglishLabelOnlyNotices_ThroughTheService() => FrenchSceneAsync(async (scene, french) =>
    {
        var (pool, service, fr) = (french.Pool, french.Service, french.Lcid);

        // Global option: notice, then English written and French kept.
        var result = await service.UpdateOptionValueAsync(new PpdsUpdateOptionValue { OptionSetName = scene.Choice, Value = scene.Open, NewLabel = "Closed", SolutionUniqueName = Solution });
        ExpectNotices("service update-option (global)", result, ("NewLabel", "label", "Ouvert"));
        await ExpectGlobalOptionAsync(pool, scene.Choice, scene.Open, "Closed", "Ouvert", fr);

        // Suppressed when the shown label already reads the requested text.
        result = await service.UpdateOptionValueAsync(new PpdsUpdateOptionValue { OptionSetName = scene.Choice, Value = scene.Same, NewLabel = "Pareil", SolutionUniqueName = Solution });
        ExpectNotices("service update-option (global), requested text equals the shown French label", result);
        await ExpectGlobalOptionAsync(pool, scene.Choice, scene.Same, "Pareil", "Pareil", fr);

        // Global choice display name and description.
        result = await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = scene.Choice, DisplayName = "Renamed", Description = "Changed" });
        ExpectNotices("service optionset update", result, ("DisplayName", "display name", "Choix FR"), ("Description", "description", "Description FR"));
        var choice = await RetrieveChoiceAsync(pool, scene.Choice, asIfPublished: true);
        Text(choice.DisplayName, English).Should().Be("Renamed"); Text(choice.DisplayName, fr).Should().Be("Choix FR", "MergeLabels keeps the French display name");
        Text(choice.Description, English).Should().Be("Changed"); Text(choice.Description, fr).Should().Be("Description FR", "MergeLabels keeps the French description");

        // Local column option.
        result = await service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = scene.Entity, ColumnLogicalName = scene.Column, Value = scene.LocalOpen, NewLabel = "Closed", SolutionUniqueName = Solution });
        ExpectNotices("service attribute update-option", result, ("NewLabel", "label", "Ouvert"));
        var local = (await RetrieveOptionsAsync(pool, scene.Entity, scene.Column)).Single(o => o.Value == scene.LocalOpen);
        Text(local.Label, English).Should().Be("Closed"); Text(local.Label, fr).Should().Be("Ouvert", "MergeLabels keeps the French local option label");

        // Status reason.
        result = await service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = scene.Entity, Value = scene.Reason, NewLabel = "Live", SolutionUniqueName = Solution });
        ExpectNotices("service entity update-statusreason", result, ("NewLabel", "label", "Actif"));
        var reason = await ReasonAsync(pool, scene.Entity, scene.Reason);
        Text(reason.Label, English).Should().Be("Live"); Text(reason.Label, fr).Should().Be("Actif", "MergeLabels keeps the French status label");

        // Table: the description has no French label, so it is shown in English and gives no notice.
        result = await service.UpdateTableAsync(new UpdateTableRequest
        {
            SolutionUniqueName = Solution, EntityLogicalName = scene.Entity, DisplayName = "Renamed", PluralDisplayName = "Renamed plural", Description = "Renamed description"
        });
        ExpectNotices("service table update", result, ("DisplayName", "display name", "Test FR"), ("PluralDisplayName", "plural name", "Tests FR"));
        var table = await ReadTableAsync(pool, scene.Entity, asIfPublished: true);
        Text(table.DisplayName, English).Should().Be("Renamed"); Text(table.DisplayName, fr).Should().Be("Test FR", "MergeLabels keeps the French display name");
        Text(table.DisplayCollectionName, English).Should().Be("Renamed plural"); Text(table.DisplayCollectionName, fr).Should().Be("Tests FR");
        Text(table.Description, English).Should().Be("Renamed description");

        // Typed column.
        result = await service.UpdateColumnAsync(new UpdateColumnRequest
        {
            SolutionUniqueName = Solution, EntityLogicalName = scene.Entity, ColumnLogicalName = scene.Column, DisplayName = "Pick renamed", Description = "Changed"
        });
        ExpectNotices("service column update", result, ("DisplayName", "display name", "Choix FR"), ("Description", "description", "Choix description FR"));
        var column = await ReadColumnAsync(pool, scene.Entity, scene.Column, asIfPublished: true);
        Text(column.DisplayName, English).Should().Be("Pick renamed"); Text(column.DisplayName, fr).Should().Be("Choix FR");
        Text(column.Description, English).Should().Be("Changed"); Text(column.Description, fr).Should().Be("Choix description FR");

        // A column whose only label is English is shown in English: no notice.
        result = await service.UpdateColumnAsync(new UpdateColumnRequest { SolutionUniqueName = Solution, EntityLogicalName = scene.Entity, ColumnLogicalName = scene.NameColumn, DisplayName = "Name renamed" });
        ExpectNotices("service column update, English-only label", result);
        Text((await ReadColumnAsync(pool, scene.Entity, scene.NameColumn, asIfPublished: true)).DisplayName, English).Should().Be("Name renamed");
    });

    // (c) and (d) through the CLI commands the unit tests cover (LabelNoticeCommandTests), on the real service.
    [MetadataLiveSwitchUiLanguageFact]
    public Task FrenchCaller_EnglishLabelOnlyNotices_ThroughTheCliCommands() => FrenchSceneAsync(async (scene, french) =>
    {
        var (pool, fr) = (french.Pool, french.Lcid);
        string[] Json(params string[] args) => [.. args, "--output-format", "json"];

        await ExpectCliNoticesAsync(french, Json("optionset", "update-option", "--solution", Solution, "--name", scene.Choice, "--value", $"{scene.Open}", "--new-label", "Closed"), ("NewLabel", "label", "Ouvert"));
        await ExpectGlobalOptionAsync(pool, scene.Choice, scene.Open, "Closed", "Ouvert", fr);

        await ExpectCliNoticesAsync(french, Json("optionset", "update-option", "--solution", Solution, "--name", scene.Choice, "--value", $"{scene.Same}", "--new-label", "Pareil"));
        await ExpectGlobalOptionAsync(pool, scene.Choice, scene.Same, "Pareil", "Pareil", fr);

        await ExpectCliNoticesAsync(french, Json("optionset", "update", "--solution", Solution, "--name", scene.Choice, "--display-name", "Renamed"), ("DisplayName", "display name", "Choix FR"));
        var choice = await RetrieveChoiceAsync(pool, scene.Choice, asIfPublished: true);
        Text(choice.DisplayName, English).Should().Be("Renamed"); Text(choice.DisplayName, fr).Should().Be("Choix FR");

        await ExpectCliNoticesAsync(french, Json("attribute", "update-option", "--solution", Solution, "--entity", scene.Entity, "--column", scene.Column, "--value", $"{scene.LocalOpen}", "--new-label", "Closed"), ("NewLabel", "label", "Ouvert"));
        var local = (await RetrieveOptionsAsync(pool, scene.Entity, scene.Column)).Single(o => o.Value == scene.LocalOpen);
        Text(local.Label, English).Should().Be("Closed"); Text(local.Label, fr).Should().Be("Ouvert");

        await ExpectCliNoticesAsync(french, Json("entity", "update-statusreason", "--solution", Solution, "--entity", scene.Entity, "--value", $"{scene.Reason}", "--new-label", "Live"), ("NewLabel", "label", "Actif"));
        var reason = await ReasonAsync(pool, scene.Entity, scene.Reason);
        Text(reason.Label, English).Should().Be("Live"); Text(reason.Label, fr).Should().Be("Actif");

        await ExpectCliNoticesAsync(french, Json("table", "update", "--solution", Solution, "--entity", scene.Entity, "--display-name", "Renamed"), ("DisplayName", "display name", "Test FR"));
        var table = await ReadTableAsync(pool, scene.Entity, asIfPublished: true);
        Text(table.DisplayName, English).Should().Be("Renamed"); Text(table.DisplayName, fr).Should().Be("Test FR");

        await ExpectCliNoticesAsync(french, Json("column", "update", "--solution", Solution, "--entity", scene.Entity, "--column", scene.Column, "--description", "Changed"), ("Description", "description", "Choix description FR"));
        var column = await ReadColumnAsync(pool, scene.Entity, scene.Column, asIfPublished: true);
        Text(column.Description, English).Should().Be("Changed"); Text(column.Description, fr).Should().Be("Choix description FR");

        // Text mode: a Warning line on stderr, nothing on stdout, exit 0.
        var (exit, stdout, stderr) = await InvokeCliAsync(french, "column", "update", "--solution", Solution, "--entity", scene.Entity, "--column", scene.Column, "--display-name", "Renamed");
        _output.WriteLine($"OBSERVED CLI text mode column update --display-name: exit {exit}; stderr: {stderr.Trim()}");
        exit.Should().Be(0);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("Warning: " + EnglishOnlyMessage("display name", fr, "Choix FR"));
    });

    // (e): dry-runs and choice-name case resolution with a French caller.
    [MetadataLiveSwitchUiLanguageFact]
    public Task FrenchCaller_DryRuns_AndChoiceNameCaseResolution() => FrenchSceneAsync(async (scene, french) =>
    {
        var (pool, service, fr) = (french.Pool, french.Service, french.Lcid);
        async Task<(int, string)[]> OptionPairs(int value) => Pairs((await RetrieveChoiceAsync(pool, scene.Choice, asIfPublished: true)).Options.Single(o => o.Value == value).Label);
        async Task<(int, string)[]> LocalPairs() => Pairs((await RetrieveOptionsAsync(pool, scene.Entity, scene.Column)).Single(o => o.Value == scene.LocalOpen).Label);

        // Option dry-runs read the target, so they report the notice; nothing is written.
        var before = await OptionPairs(scene.Open);
        var result = await service.UpdateOptionValueAsync(new PpdsUpdateOptionValue { OptionSetName = scene.Choice, Value = scene.Open, NewLabel = "Closed", SolutionUniqueName = Solution, DryRun = true });
        ExpectNotices("service update-option --dry-run (global)", result, ("NewLabel", "label", "Ouvert"));
        (await OptionPairs(scene.Open)).Should().BeEquivalentTo(before, "a dry-run writes nothing");

        var localBefore = await LocalPairs();
        result = await service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = scene.Entity, ColumnLogicalName = scene.Column, Value = scene.LocalOpen, NewLabel = "Closed", SolutionUniqueName = Solution, DryRun = true });
        ExpectNotices("service attribute update-option --dry-run", result, ("NewLabel", "label", "Ouvert"));
        (await LocalPairs()).Should().BeEquivalentTo(localBefore, "a dry-run writes nothing");

        await ExpectCliNoticesAsync(french, ["optionset", "update-option", "--solution", Solution, "--name", scene.Choice, "--value", $"{scene.Open}", "--new-label", "Closed", "--dry-run", "--output-format", "json"],
            ("NewLabel", "label", "Ouvert"));
        (await OptionPairs(scene.Open)).Should().BeEquivalentTo(before, "a CLI dry-run writes nothing");

        // Table and column dry-runs return no notices (AC-147) and write nothing.
        var tableBefore = await ReadTableAsync(pool, scene.Entity, asIfPublished: true);
        result = await service.UpdateTableAsync(new UpdateTableRequest { SolutionUniqueName = Solution, EntityLogicalName = scene.Entity, DisplayName = "Renamed", DryRun = true });
        ExpectNotices("service table update --dry-run", result);
        Pairs((await ReadTableAsync(pool, scene.Entity, asIfPublished: true)).DisplayName).Should().BeEquivalentTo(Pairs(tableBefore.DisplayName), "a dry-run writes nothing");
        var columnBefore = await ReadColumnAsync(pool, scene.Entity, scene.Column, asIfPublished: true);
        result = await service.UpdateColumnAsync(new UpdateColumnRequest { SolutionUniqueName = Solution, EntityLogicalName = scene.Entity, ColumnLogicalName = scene.Column, DisplayName = "Renamed", DryRun = true });
        ExpectNotices("service column update --dry-run", result);
        Pairs((await ReadColumnAsync(pool, scene.Entity, scene.Column, asIfPublished: true)).DisplayName).Should().BeEquivalentTo(Pairs(columnBefore.DisplayName), "a dry-run writes nothing");

        // Choice update dry-run: recorded (it returns before reading the choice); nothing is written.
        var choiceBefore = Pairs((await RetrieveChoiceAsync(pool, scene.Choice, asIfPublished: true)).DisplayName);
        result = await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = scene.Choice, DisplayName = "Renamed", DryRun = true });
        _output.WriteLine($"OBSERVED service optionset update --dry-run: {result.ValidationMessages.Count} notice(s)");
        Pairs((await RetrieveChoiceAsync(pool, scene.Choice, asIfPublished: true)).DisplayName).Should().BeEquivalentTo(choiceBefore, "a dry-run writes nothing");

        // Case resolution: a mis-cased name resolves to the stored one, with the same notices.
        var stored = (await RetrieveChoiceAsync(pool, scene.Choice, asIfPublished: true)).Name;
        var misCased = MisCase(stored);
        _output.WriteLine($"Stored name '{stored}', mis-cased request '{misCased}'.");
        result = await service.UpdateOptionValueAsync(new PpdsUpdateOptionValue { OptionSetName = misCased, Value = scene.Open, NewLabel = "Closed", SolutionUniqueName = Solution });
        ExpectNotices("service update-option with a mis-cased name", result, ("NewLabel", "label", "Ouvert"));
        await ExpectGlobalOptionAsync(pool, stored, scene.Open, "Closed", "Ouvert", fr);

        result = await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = misCased, DisplayName = "Renamed" });
        ExpectNotices("service optionset update with a mis-cased name", result, ("DisplayName", "display name", "Choix FR"));
        var renamed = await RetrieveChoiceAsync(pool, stored, asIfPublished: true);
        Text(renamed.DisplayName, English).Should().Be("Renamed"); Text(renamed.DisplayName, fr).Should().Be("Choix FR");

        var added = await service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = misCased, Label = "Added", SolutionUniqueName = Solution });
        var addedOptions = (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Where(o => o.Value == added).ToList();
        _output.WriteLine($"OBSERVED add-option with a mis-cased name as the French caller: value {added}; labels {string.Join(", ", addedOptions.SelectMany(o => Pairs(o.Label)))}");
        addedOptions.Should().ContainSingle("the retried add writes exactly once");
        Text(addedOptions[0].Label, English).Should().Be("Added", "add-option writes the English (1033) label");
        (await new DataverseMetadataQueryService(pool).GetOptionSetAsync(misCased)).Name.Should().Be(stored);

        // Label targeting uses the label shown to the caller (here the French one).
        await service.UpdateOptionValueAsync(new PpdsUpdateOptionValue { OptionSetName = scene.Choice, Label = "Pareil", Color = "#123456", SolutionUniqueName = Solution });
        (await RetrieveChoiceAsync(pool, stored, asIfPublished: true)).Options.Single(o => o.Value == scene.Same).Color.Should().Be("#123456", "--label with the shown French label targets that option");
        var byEnglish = await Record.ExceptionAsync(() => service.UpdateOptionValueAsync(new PpdsUpdateOptionValue { OptionSetName = scene.Choice, Label = "Same", Color = "#654321", SolutionUniqueName = Solution }));
        _output.WriteLine($"OBSERVED update-option --label <English text> as the French caller: {Describe(byEnglish)}");
        await service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = scene.Entity, ColumnLogicalName = scene.Column, Label = "Ouvert", Color = "#010203", SolutionUniqueName = Solution });
        (await RetrieveOptionsAsync(pool, scene.Entity, scene.Column)).Single(o => o.Value == scene.LocalOpen).Color.Should().Be("#010203", "--label with the shown French label targets the local option");
        await service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = scene.Entity, Label = "Actif", Color = "#070809", SolutionUniqueName = Solution });
        (await ReasonAsync(pool, scene.Entity, scene.Reason)).Color.Should().Be("#070809", "--label with the shown French label targets the status reason");
    });

    // (f): the fault Dataverse returns for an unknown or mis-cased choice name when the caller is French.
    [MetadataLiveSwitchUiLanguageFact]
    public Task FrenchCaller_UnknownChoiceName_RecordsTheFaultCodeAndMessageLanguage() => FrenchSceneAsync(async (scene, french) =>
    {
        var pool = french.Pool;
        var stored = (await RetrieveChoiceAsync(pool, scene.Choice, asIfPublished: true)).Name;
        var misCased = MisCase(stored);
        var missing = NewName("none"); // never created
        _output.WriteLine($"Stored name '{stored}', mis-cased '{misCased}', unknown '{missing}'.");

        async Task<(string Operation, int? Code, string Outcome)> Observe(string operation, OrganizationRequest request)
        {
            try
            {
                await ExecuteAsync(pool, request);
                return (operation, null, "accepted");
            }
            catch (Exception ex) when (FindFault(ex) is { } fault)
            {
                var english = fault.Message?.Contains("Could not find", StringComparison.OrdinalIgnoreCase) == true;
                return (operation, fault.ErrorCode, $"0x{unchecked((uint)fault.ErrorCode):x8} '{fault.Message}' (message language: {(english ? "English wording" : "not the English wording")})");
            }
        }

        var misCasedReads = new[]
        {
            await Observe("RetrieveOptionSet mis-cased (published)", new RetrieveOptionSetRequest { Name = misCased }),
            await Observe("RetrieveOptionSet mis-cased (as-if-published)", new RetrieveOptionSetRequest { Name = misCased, RetrieveAsIfPublished = true })
        };
        var unknown = new[]
        {
            await Observe("RetrieveOptionSet unknown (published)", new RetrieveOptionSetRequest { Name = missing }),
            await Observe("RetrieveOptionSet unknown (as-if-published)", new RetrieveOptionSetRequest { Name = missing, RetrieveAsIfPublished = true }),
            await Observe("InsertOptionValue unknown", new InsertOptionValueRequest { OptionSetName = missing, Label = new Label("Probe", English), SolutionUniqueName = Solution }),
            await Observe("OrderOption unknown", new OrderOptionRequest { OptionSetName = missing, Values = [1], SolutionUniqueName = Solution }),
            await Observe("UpdateOptionValue unknown", new SdkUpdateOptionValue { OptionSetName = missing, Value = 1, Label = new Label("Probe", English), MergeLabels = true, SolutionUniqueName = Solution })
        };
        foreach (var (operation, _, outcome) in misCasedReads.Concat(unknown))
            _output.WriteLine($"OBSERVED French caller {operation}: {outcome}");

        foreach (var (operation, code, outcome) in unknown)
            code.Should().Be(ObjectDoesNotExist, $"the not-found recognition assumes 0x80040217 for {operation}, but Dataverse returned {outcome}");
        foreach (var (operation, code, outcome) in misCasedReads.Where(o => o.Code.HasValue))
            code.Should().Be(ObjectDoesNotExist, $"the not-found recognition assumes 0x80040217 for {operation}, but Dataverse returned {outcome}");

        // Through the service: the original not-found fault surfaces and nothing is created.
        var ex = await Record.ExceptionAsync(() => french.Service.UpdateOptionValueAsync(
            new PpdsUpdateOptionValue { OptionSetName = missing, Value = 1, NewLabel = "Probe", SolutionUniqueName = Solution }));
        _output.WriteLine($"OBSERVED service update-option on an unknown name as the French caller: {Describe(ex)}");
        ex.Should().NotBeNull();
        FindFault(ex!).Should().NotBeNull("the original Dataverse fault is kept as the cause");
        FindFault(ex!)!.ErrorCode.Should().Be(ObjectDoesNotExist);
        var query = await Record.ExceptionAsync(() => new DataverseMetadataQueryService(pool).GetOptionSetAsync(missing));
        _output.WriteLine($"OBSERVED query service optionset {missing} as the French caller: {Describe(query)}");
        (await Observe("RetrieveOptionSet unknown after the probes", new RetrieveOptionSetRequest { Name = missing, RetrieveAsIfPublished = true })).Code
            .Should().Be(ObjectDoesNotExist, "the probes must not have created the unknown choice");
    });

    // ───── French-caller fixture ─────

    /// <summary>
    /// Builds the throwaway components as the base-language caller, switches the caller's UI language, runs
    /// <paramref name="body"/> on a fresh connection that serves the switched language, then restores the UI language
    /// (in a finally block) before the components are deleted.
    /// </summary>
    private async Task FrenchSceneAsync(Func<FrenchScene, FrenchCaller, Task> body)
    {
        var (pool, _) = await ConnectAsync();
        var second = await RequireSecondLanguageAsync(pool);
        // Checked before anything is created: refuses when the caller is not on the base language.
        var uiSwitch = await CallerUiLanguageSwitch.PrepareAsync(pool, second, _output.WriteLine);
        if (uiSwitch.BaseLanguage != English)
            throw new XunitException($"These tests expect base language 1033; the organisation's is {uiSwitch.BaseLanguage}.");

        var name = NewName("frc");
        await WithChoiceAsync(pool, name, "Choice EN", Labels("Description EN", (second, "Description FR")),
            [Labels("Open", (second, "Ouvert")), Labels("Same", (second, "Pareil")), new Label("English only", English), new Label("Seulement", second)], async () =>
        {
            var set = await RetrieveChoiceAsync(pool, name, asIfPublished: true);
            set.DisplayName = Labels("Choice EN", (second, "Choix FR"));
            await ExecuteAsync(pool, new UpdateOptionSetRequest { OptionSet = set, MergeLabels = true, SolutionUniqueName = Solution });
            var options = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options;
            int ValueOf(Func<OptionMetadata, bool> match) => options.Single(match).Value!.Value;
            var open = ValueOf(o => Text(o.Label, English) == "Open");
            var same = ValueOf(o => Text(o.Label, English) == "Same");
            var englishOnly = ValueOf(o => Text(o.Label, English) == "English only");
            var frenchOnly = ValueOf(o => Text(o.Label, second) == "Seulement");
            // The create stores the French-only label under 1033 too (observed 2026-11-17); give 1033 empty text.
            await ExecuteAsync(pool, new SdkUpdateOptionValue
            {
                OptionSetName = name, Value = frenchOnly, MergeLabels = false, SolutionUniqueName = Solution,
                Label = new Label(new LocalizedLabel("", English), new[] { new LocalizedLabel("", English), new LocalizedLabel("Seulement", second) })
            });
            await PublishChoiceAsync(pool, name);

            await WithTableAsync(pool, Labels("Open", (second, "Ouvert")), async (entity, column) =>
            {
                await ExecuteAsync(pool, new UpdateEntityRequest
                {
                    Entity = new EntityMetadata
                    {
                        LogicalName = entity,
                        DisplayName = Labels("PPDS label live test", (second, "Test FR")),
                        DisplayCollectionName = Labels("PPDS label live tests", (second, "Tests FR"))
                    },
                    MergeLabels = true, SolutionUniqueName = Solution
                });
                await ExecuteAsync(pool, new UpdateAttributeRequest
                {
                    EntityName = entity, MergeLabels = true, SolutionUniqueName = Solution,
                    Attribute = new PicklistAttributeMetadata { LogicalName = column, DisplayName = Labels("Pick", (second, "Choix FR")), Description = Labels("Pick description", (second, "Choix description FR")) }
                });
                var reason = (await ActiveReasonAsync(pool, entity)).Value!.Value;
                var reasonLabel = new SdkUpdateOptionValue { Value = reason, Label = Labels("Active", (second, "Actif")), MergeLabels = true, SolutionUniqueName = Solution };
                reasonLabel["EntityLogicalName"] = entity;
                reasonLabel["AttributeLogicalName"] = "statuscode";
                await ExecuteAsync(pool, reasonLabel);
                await ExecuteAsync(pool, new PublishXmlRequest { ParameterXml = $"<importexportxml><entities><entity>{entity}</entity></entities></importexportxml>" });

                var nameColumn = (await ReadTableAsync(pool, entity, asIfPublished: true)).PrimaryNameAttribute;
                var localOpen = (await RetrieveOptionsAsync(pool, entity, column)).Single().Value!.Value;
                var scene = new FrenchScene(name, open, same, englishOnly, frenchOnly, entity, column, nameColumn, localOpen, reason);
                _output.WriteLine($"Scene: {scene}");
                Shown("base-language caller, choice display name before the switch", (await RetrieveChoiceAsync(pool, name, asIfPublished: false)).DisplayName)
                    .Should().Be(((int?)English, "Choice EN"), "precondition: before the switch the caller is served the base language");

                await WithSwitchedUiLanguageAsync(pool, uiSwitch, name, french => body(scene, french));
            });
        });
    }

    private async Task WithSwitchedUiLanguageAsync(DataverseConnectionPool pool, CallerUiLanguageSwitch uiSwitch, string probeChoice, Func<FrenchCaller, Task> body)
    {
        Exception? failure = null;
        IConnectionSource? source = null;
        DataverseConnectionPool? frenchPool = null;
        try
        {
            await uiSwitch.ApplyAsync(pool);
            source = Configuration.HasClientSecretCredentials
                ? await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, "MetadataFrenchCallerLiveTests", maxPoolSize: 3)
                : await LiveTestProfileConnectionSource.CreateAsync(Configuration, "MetadataFrenchCallerLiveTests", maxPoolSize: 3);
            frenchPool = LiveTestHelpers.CreateConnectionPool(new[] { source });
            _output.WriteLine("Opened a fresh connection pool after the switch.");
            await WaitForServedLanguageAsync(frenchPool, pool, probeChoice, uiSwitch.Target, "after the switch");
            var service = new DataverseMetadataAuthoringService(frenchPool, new SchemaValidator(), new EnvironmentShakedownGuard());
            await body(new FrenchCaller(frenchPool, service, uiSwitch.Target));
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            string? restoreFailure = null;
            try
            {
                await uiSwitch.RestoreAsync(pool);
            }
            catch (Exception ex)
            {
                restoreFailure = ex.Message;
            }

            if (restoreFailure is null && frenchPool is not null)
            {
                try
                {
                    await WaitForServedLanguageAsync(frenchPool, pool, probeChoice, uiSwitch.Original, "after the restore");
                }
                catch (Exception ex)
                {
                    restoreFailure = ex.Message;
                }
            }

            // The pool disposes its sources; a source without a pool is disposed here.
            if (frenchPool is not null)
                await frenchPool.DisposeAsync();
            else
                source?.Dispose();

            if (restoreFailure is not null)
            {
                var message = restoreFailure + (failure is null ? "" : $" The test itself had also failed: {failure.GetType().Name}: {failure.Message}");
                _output.WriteLine(message);
                throw new XunitException(message);
            }
        }
    }

    /// <summary>
    /// Polls the published display name of <paramref name="probeChoice"/> (labelled in both languages) on the fresh
    /// pool and on the pool opened before the switch, printing each read, until the fresh pool serves
    /// <paramref name="lcid"/> three reads in a row.
    /// </summary>
    private async Task WaitForServedLanguageAsync(IDataverseConnectionPool fresh, IDataverseConnectionPool earlier, string probeChoice, int lcid, string step)
    {
        async Task<string> Served(IDataverseConnectionPool p)
        {
            try
            {
                var shown = (await RetrieveChoiceAsync(p, probeChoice, asIfPublished: false)).DisplayName?.UserLocalizedLabel;
                return shown is null ? "null" : $"{shown.LanguageCode}";
            }
            catch (Exception ex)
            {
                return $"read failed ({ex.GetType().Name}: {ex.Message})";
            }
        }

        var clock = Stopwatch.StartNew();
        var inARow = 0;
        for (var attempt = 1; ; attempt++)
        {
            var onFresh = await Served(fresh);
            var onEarlier = await Served(earlier);
            _output.WriteLine($"OBSERVED served language {step}, read {attempt} at {clock.Elapsed.TotalSeconds:F0}s: " +
                $"fresh connection LCID {onFresh}; connection opened before the switch LCID {onEarlier}");
            inARow = onFresh == $"{lcid}" ? inARow + 1 : 0;
            if (inARow == 3)
            {
                _output.WriteLine($"OBSERVED {step}: the fresh connection serves LCID {lcid} (3 reads in a row after {clock.Elapsed.TotalSeconds:F0}s); " +
                    $"the connection opened before the switch last served LCID {onEarlier}.");
                return;
            }

            if (clock.Elapsed > ServedLanguageTimeout)
                throw new XunitException($"Dataverse did not serve LCID {lcid} to the caller {step} within {ServedLanguageTimeout.TotalMinutes:F0} minutes " +
                    $"(last read: fresh connection {onFresh}, earlier connection {onEarlier}).");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    // ───── French-caller helpers ─────

    private (int? Lcid, string? Text) Shown(string what, Label? label)
    {
        var shown = label?.UserLocalizedLabel;
        _output.WriteLine($"OBSERVED {what}: UserLocalizedLabel {(shown is null ? "null" : $"LCID {shown.LanguageCode} '{shown.Label}'")}; localized labels: {string.Join(", ", Pairs(label))}");
        return (shown?.LanguageCode, shown?.Label);
    }

    /// <summary>The ENGLISH_LABEL_ONLY message the spec defines (AC-145).</summary>
    private static string EnglishOnlyMessage(string noun, int lcid, string shown)
        => $"Only the English (LCID 1033) {noun} is written. The {noun} Dataverse shows you (LCID {lcid}) is not changed and still reads '{shown}'.";

    private void ExpectNotices(string what, MetadataLabelWriteResult result, params (string Field, string Noun, string Shown)[] expected)
    {
        var fr = MetadataLivePrerequisites.SecondLanguage!.Value;
        _output.WriteLine($"OBSERVED {what}: {result.ValidationMessages.Count} notice(s)" +
            string.Concat(result.ValidationMessages.Select(m => $"; [{m.Field} {m.Rule}] {m.Message}")));
        result.ValidationMessages.Select(m => (m.Field, m.Rule, m.Message)).Should().BeEquivalentTo(
            expected.Select(e => (e.Field, MetadataLabelWriteResult.EnglishLabelOnlyRule, EnglishOnlyMessage(e.Noun, fr, e.Shown))), what);
    }

    private async Task ExpectCliNoticesAsync(FrenchCaller french, string[] args, params (string Field, string Noun, string Shown)[] expected)
    {
        var (exit, stdout, stderr) = await InvokeCliAsync(french, args);
        var what = "CLI " + string.Join(' ', args);
        _output.WriteLine($"OBSERVED {what}: exit {exit}; stdout {stdout.Trim()}");
        exit.Should().Be(0, $"{what} succeeds; stderr: {stderr}");
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        if (expected.Length == 0)
        {
            data.TryGetProperty("validationMessages", out _).Should().BeFalse($"{what}: no notice, so no validationMessages");
            return;
        }

        var fr = french.Lcid;
        data.GetProperty("validationMessages").EnumerateArray()
            .Select(m => (m.GetProperty("field").GetString(), m.GetProperty("rule").GetString(), m.GetProperty("message").GetString()))
            .Should().BeEquivalentTo(expected.Select(e => ((string?)e.Field, (string?)MetadataLabelWriteResult.EnglishLabelOnlyRule, (string?)EnglishOnlyMessage(e.Noun, fr, e.Shown))), what);
    }

    /// <summary>Runs a metadata CLI command in-process on the real authoring service bound to the French-caller pool.</summary>
    private async Task<(int Exit, string Stdout, string Stderr)> InvokeCliAsync(FrenchCaller french, params string[] args)
    {
        Task<ServiceProvider> Provider(CancellationToken _) => Task.FromResult(new ServiceCollection()
            .AddSingleton<IMetadataAuthoringService>(french.Service)
            .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "live-test" }, EnvironmentUrl = Configuration.DataverseUrl! })
            .BuildServiceProvider());

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var parsed = MetadataCommandGroup.Create(Provider).Parse(args);
            if (parsed.Errors.Count > 0)
                throw new XunitException($"CLI parse errors for '{string.Join(' ', args)}': {string.Join("; ", parsed.Errors.Select(e => e.Message))}");
            var exit = await parsed.InvokeAsync();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    private static async Task ExpectGlobalOptionAsync(IDataverseConnectionPool pool, string choice, int value, string english, string french, int lcid)
    {
        var label = (await RetrieveChoiceAsync(pool, choice, asIfPublished: true)).Options.Single(o => o.Value == value).Label;
        Text(label, English).Should().Be(english, "the English (1033) label is the one written");
        Text(label, lcid).Should().Be(french, "MergeLabels keeps the French label");
    }

    private static async Task<EntityMetadata> ReadTableAsync(IDataverseConnectionPool pool, string entity, bool asIfPublished)
        => ((RetrieveEntityResponse)await ExecuteAsync(pool,
            new RetrieveEntityRequest { LogicalName = entity, EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = asIfPublished })).EntityMetadata;

    private static async Task<AttributeMetadata> ReadColumnAsync(IDataverseConnectionPool pool, string entity, string attribute, bool asIfPublished)
        => ((RetrieveAttributeResponse)await ExecuteAsync(pool,
            new RetrieveAttributeRequest { EntityLogicalName = entity, LogicalName = attribute, RetrieveAsIfPublished = asIfPublished })).AttributeMetadata;
}
