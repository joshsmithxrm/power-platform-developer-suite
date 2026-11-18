using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Metadata;

/// <summary>
/// Regression tests for the French-base findings of 2026-11-17 (an organisation whose base language is 1036, English
/// not provisioned): D1, 1033 label updates acknowledged but not stored (UpdateOptionValue, UpdateOptionSet, and an
/// empty description); D2, 1033 table/column label updates stored over the 1036 base label with a notice saying it was
/// "not changed"; D3, 1033 inserts and status-reason updates rejected with a raw 0x80044195 fault. They run on any base
/// language: the service now writes the organisation's base language (read from organization.languagecode) and reads
/// every label write back. On an organisation whose base language is not 1033 and where 1033 is not provisioned, each
/// test first replays the old 1033 request shape raw and records what Dataverse does with it ("before"), then runs the
/// service ("after") and asserts what is stored and shown.
/// </summary>
public sealed partial class MetadataGlobalChoiceLiveTests
{
    // D1: option, local option and global choice label updates, and clearing a description, are stored.
    [MetadataLiveSkippableFact]
    public async Task BaseLanguage_D1_LabelUpdatesAreStored_NotSilentlyIgnored()
    {
        var (pool, service) = await ConnectAsync();
        var replayOld = await ReplaysTheOld1033ShapeAsync(pool);
        Skip.IfNot(replayOld, OldShapeDoesNotApply);
        var name = NewName("bd1");
        await WithChoiceAsync(pool, name, "Base choice", new Label("Base description", Base), [new Label("Open", Base)], async () =>
        {
            var value = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single().Value!.Value;
            if (replayOld)
            {
                var raw = new Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest { OptionSetName = name, Value = value, MergeLabels = true, SolutionUniqueName = Solution, Label = new Label("Raw 1033", 1033) };
                var rawOutcome = await Record.ExceptionAsync(() => ExecuteAsync(pool, raw));
                var afterRaw = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
                _output.WriteLine($"OBSERVED D1 before (raw UpdateOptionValue with a 1033 label, base {Base}): {Describe(rawOutcome)}; stored {string.Join(", ", Pairs(afterRaw.Label))}");
                // The old failure: the 1033 label is never stored (accepted and ignored, or rejected with 0x80044195);
                // either way the old command could not have changed the label.
                Pairs(afterRaw.Label).Should().Equal(new[] { (Base, "Open") }, "D1 before: the old 1033 label write changes nothing");
                if (rawOutcome is not null)
                    FindFault(rawOutcome)?.ErrorCode.Should().Be(InvalidLanguageCode, "D1 before: a rejected 1033 label is the invalid-language fault");

                var rawChoice = await RetrieveChoiceAsync(pool, name, asIfPublished: true);
                rawChoice.DisplayName = new Label("Raw 1033 choice", 1033);
                var rawChoiceOutcome = await Record.ExceptionAsync(() => ExecuteAsync(pool, new UpdateOptionSetRequest { OptionSet = rawChoice, MergeLabels = true, SolutionUniqueName = Solution }));
                var choiceAfterRaw = await RetrieveChoiceAsync(pool, name, asIfPublished: true);
                _output.WriteLine($"OBSERVED D1 before (raw UpdateOptionSet with a 1033 display name, base {Base}): {Describe(rawChoiceOutcome)}; stored {string.Join(", ", Pairs(choiceAfterRaw.DisplayName))}");
                Pairs(choiceAfterRaw.DisplayName).Should().Equal(new[] { (Base, "Base choice") }, "D1 before: the old 1033 display-name write changes nothing");
                if (rawChoiceOutcome is not null)
                    FindFault(rawChoiceOutcome)?.ErrorCode.Should().Be(InvalidLanguageCode);
            }

            // Global option label.
            var result = await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, NewLabel = "Closed", SolutionUniqueName = Solution });
            var option = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
            _output.WriteLine($"OBSERVED D1 after (service update-option --new-label Closed): stored {string.Join(", ", Pairs(option.Label))}; shown {ShownText(option.Label)}; notices {result.ValidationMessages.Count}");
            Pairs(option.Label).Should().Equal(new[] { (Base, "Closed") }, "the label is written in the base language only, and stored");
            result.ValidationMessages.Should().BeEmpty("the caller is shown the base-language label, which this write changed");

            // Global choice display name and description.
            result = await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = name, DisplayName = "Base choice renamed", Description = "Base description changed" });
            var choice = await RetrieveChoiceAsync(pool, name, asIfPublished: true);
            _output.WriteLine($"OBSERVED D1 after (service optionset update --display-name --description): display {string.Join(", ", Pairs(choice.DisplayName))}; description {string.Join(", ", Pairs(choice.Description))}; notices {result.ValidationMessages.Count}");
            Pairs(choice.DisplayName).Should().Equal(new[] { (Base, "Base choice renamed") });
            Pairs(choice.Description).Should().Equal(new[] { (Base, "Base description changed") });

            // Clearing the description clears the base-language text only.
            await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = name, Description = "" });
            choice = await RetrieveChoiceAsync(pool, name, asIfPublished: true);
            _output.WriteLine($"OBSERVED D1 after (service optionset update --description \"\"): description {string.Join(", ", Pairs(choice.Description))}");
            Text(choice.Description, Base).Should().BeNullOrEmpty("--description \"\" clears the base-language description");
        });

        await WithTableAsync(pool, Labels("Open", null), async (entity, column) =>
        {
            var local = (await RetrieveOptionsAsync(pool, entity, column)).Single().Value!.Value;
            await service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = entity, ColumnLogicalName = column, Value = local, NewLabel = "Closed", SolutionUniqueName = Solution });
            var option = (await RetrieveOptionsAsync(pool, entity, column)).Single();
            _output.WriteLine($"OBSERVED D1 after (service attribute update-option --new-label Closed): stored {string.Join(", ", Pairs(option.Label))}");
            Pairs(option.Label).Should().Equal(new[] { (Base, "Closed") });
        });
    }

    // D2: table and column label updates write the base label, and give no notice claiming it was not changed.
    [MetadataLiveSkippableFact]
    public async Task BaseLanguage_D2_TableAndColumnUpdates_WriteTheBaseLabel_WithoutAFalseNotice()
    {
        var (pool, service) = await ConnectAsync();
        var replayOld = await ReplaysTheOld1033ShapeAsync(pool);
        Skip.IfNot(replayOld, OldShapeDoesNotApply);
        await WithTableAsync(pool, Labels("Open", null), async (entity, column) =>
        {
            if (replayOld)
            {
                var raw = await Record.ExceptionAsync(() => ExecuteAsync(pool, new UpdateEntityRequest
                {
                    Entity = new EntityMetadata { LogicalName = entity, DisplayName = new Label("Raw 1033 table", 1033) }, MergeLabels = true, SolutionUniqueName = Solution
                }));
                var afterRaw = await RetrieveTableAsync(pool, entity);
                _output.WriteLine($"OBSERVED D2 before (raw UpdateEntity with a 1033 display name, base {Base}): {Describe(raw)}; stored display {string.Join(", ", Pairs(afterRaw.DisplayName))}");
                // The old failure: the 1033 text is accepted and stored as the base label, which the old notice called unchanged.
                raw.Should().BeNull("D2 before: the old 1033 table label write is accepted");
                Pairs(afterRaw.DisplayName).Should().Equal(new[] { (Base, "Raw 1033 table") }, "D2 before: the 1033 text replaced the base label");
            }

            var tableShownBefore = ShownText((await RetrieveTableAsync(pool, entity)).DisplayName);
            var result = await service.UpdateTableAsync(new UpdateTableRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, DisplayName = "Base table renamed", Description = "Base table description" });
            var table = await RetrieveTableAsync(pool, entity);
            _output.WriteLine($"OBSERVED D2 after (service table update): shown before {tableShownBefore}; display {string.Join(", ", Pairs(table.DisplayName))}; description {string.Join(", ", Pairs(table.Description))}; shown after {ShownText(table.DisplayName)}; " +
                $"notices {result.ValidationMessages.Count}{string.Concat(result.ValidationMessages.Select(m => $"; [{m.Field} {m.Rule}] {m.Message}"))}");
            Pairs(table.DisplayName).Should().Equal(new[] { (Base, "Base table renamed") });
            Pairs(table.Description).Should().Equal(new[] { (Base, "Base table description") });
            AssertNoticesAreTrue(result, table.DisplayName, table.Description);

            var result2 = await service.UpdateColumnAsync(new UpdateColumnRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, ColumnLogicalName = column, DisplayName = "Base pick renamed", Description = "Base pick description" });
            var after = await RetrieveColumnAsync(pool, entity, column);
            _output.WriteLine($"OBSERVED D2 after (service column update): display {string.Join(", ", Pairs(after.DisplayName))}; description {string.Join(", ", Pairs(after.Description))}; " +
                $"notices {result2.ValidationMessages.Count}{string.Concat(result2.ValidationMessages.Select(m => $"; [{m.Field} {m.Rule}] {m.Message}"))}");
            Pairs(after.DisplayName).Should().Equal(new[] { (Base, "Base pick renamed") });
            Pairs(after.Description).Should().Equal(new[] { (Base, "Base pick description") });
            AssertNoticesAreTrue(result2, after.DisplayName, after.Description);

            // Clearing a column and a table description clears the base text only.
            await service.UpdateTableAsync(new UpdateTableRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, Description = "" });
            table = await RetrieveTableAsync(pool, entity);
            _output.WriteLine($"OBSERVED D2 after (service table update --description \"\"): description {string.Join(", ", Pairs(table.Description))}");
            Text(table.Description, Base).Should().BeNullOrEmpty();
        });
    }

    // D3: inserts and status-reason label updates succeed (no raw 0x80044195 "language code 1033 is not valid" fault).
    [MetadataLiveSkippableFact]
    public async Task BaseLanguage_D3_InsertsAndStatusReasonUpdates_Succeed()
    {
        var (pool, service) = await ConnectAsync();
        var replayOld = await ReplaysTheOld1033ShapeAsync(pool);
        Skip.IfNot(replayOld, OldShapeDoesNotApply);
        var name = NewName("bd3");
        await WithChoiceAsync(pool, name, "Base insert", new Label("Base insert", Base), [new Label("One", Base)], async () =>
        {
            if (replayOld)
            {
                var raw = await ObserveAsync(pool, "InsertOptionValue with a 1033 label", new InsertOptionValueRequest { OptionSetName = name, Label = new Label("Raw 1033", 1033), SolutionUniqueName = Solution });
                _output.WriteLine($"OBSERVED D3 before (raw {raw.Operation}, base {Base}): {raw.Outcome}");
                raw.Code.Should().Be(InvalidLanguageCode, "D3 before: the old 1033 insert is rejected with the invalid-language fault");
            }
            var added = await service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = name, Label = "Added", Description = "Added description", SolutionUniqueName = Solution });
            var option = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single(o => o.Value == added);
            _output.WriteLine($"OBSERVED D3 after (service add-option): value {added}; label {string.Join(", ", Pairs(option.Label))}; description {string.Join(", ", Pairs(option.Description))}");
            Pairs(option.Label).Should().Equal(new[] { (Base, "Added") });
            Pairs(option.Description).Should().Equal(new[] { (Base, "Added description") });
        });

        await WithTableAsync(pool, Labels("Open", null), async (entity, column) =>
        {
            var reason = (await ActiveReasonAsync(pool, entity)).Value!.Value;
            if (replayOld)
            {
                var raw = new Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest { Value = reason, MergeLabels = true, Label = new Label("Raw 1033", 1033) };
                raw["EntityLogicalName"] = entity; raw["AttributeLogicalName"] = "statuscode";
                var outcome = await ObserveAsync(pool, "status-reason UpdateOptionValue with a 1033 label", raw);
                _output.WriteLine($"OBSERVED D3 before (raw {outcome.Operation}, base {Base}): {outcome.Outcome}");
                outcome.Code.Should().Be(InvalidLanguageCode, "D3 before: the old 1033 status-reason update is rejected");
                var insert = await ObserveAsync(pool, "InsertStatusValue with a 1033 label",
                    new InsertStatusValueRequest { EntityLogicalName = entity, AttributeLogicalName = "statuscode", StateCode = 0, Label = new Label("Raw 1033", 1033), SolutionUniqueName = Solution });
                _output.WriteLine($"OBSERVED D3 before (raw {insert.Operation}, base {Base}): {insert.Outcome}");
                insert.Code.Should().Be(InvalidLanguageCode, "D3 before: the old 1033 status-reason insert is rejected");
            }

            await service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = entity, Value = reason, NewLabel = "Live", SolutionUniqueName = Solution });
            var status = await ReasonAsync(pool, entity, reason);
            _output.WriteLine($"OBSERVED D3 after (service update-statusreason --new-label Live): label {string.Join(", ", Pairs(status.Label))}");
            Pairs(status.Label).Should().Equal(new[] { (Base, "Live") });

            var addedReason = await service.AddStatusReasonAsync(new AddStatusReasonRequest { EntityLogicalName = entity, Label = "Extra", StateCode = 0, SolutionUniqueName = Solution });
            var extra = await ReasonAsync(pool, entity, addedReason);
            _output.WriteLine($"OBSERVED D3 after (service add-statusreason): value {addedReason}; label {string.Join(", ", Pairs(extra.Label))}");
            Pairs(extra.Label).Should().Equal(new[] { (Base, "Extra") });

            var addedLocal = await service.AddColumnOptionAsync(new AddColumnOptionRequest { EntityLogicalName = entity, ColumnLogicalName = column, Label = "Second", SolutionUniqueName = Solution });
            var second = (await RetrieveOptionsAsync(pool, entity, column)).Single(o => o.Value == addedLocal);
            _output.WriteLine($"OBSERVED D3 after (service attribute add-option): value {addedLocal}; label {string.Join(", ", Pairs(second.Label))}");
            Pairs(second.Label).Should().Equal(new[] { (Base, "Second") });
        });
    }

    // Read-back exactness: what Dataverse stores for label text with surrounding spaces, inner double spaces,
    // non-ASCII characters and a line break, and that the service's exact comparison accepts it.
    [MetadataLiveFact]
    public async Task BaseLanguage_LabelReadBack_RecordsWhatDataverseStores()
    {
        var (pool, service) = await ConnectAsync();
        var name = NewName("brb");
        string[] texts = ["  Padded  ", "Inner  double", "Fermé déjà ✓", "Line\nbreak"];
        await WithChoiceAsync(pool, name, "Read-back", new Label("Read-back", Base), [new Label("One", Base)], async () =>
        {
            var value = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single().Value!.Value;
            foreach (var text in texts)
            {
                var raw = new Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest { OptionSetName = name, Value = value, MergeLabels = true, SolutionUniqueName = Solution, Label = new Label("Reset", Base) };
                await ExecuteAsync(pool, raw);
                raw.Label = new Label(text, Base);
                await ExecuteAsync(pool, raw);
                var stored = Text((await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single().Label, Base);
                _output.WriteLine($"OBSERVED raw UpdateOptionValue label {Quote(text)}: stored {Quote(stored)}");

                await ExecuteAsync(pool, new Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest { OptionSetName = name, Value = value, MergeLabels = true, SolutionUniqueName = Solution, Label = new Label("Reset", Base) });
                var outcome = await Record.ExceptionAsync(() => service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, NewLabel = text, SolutionUniqueName = Solution }));
                _output.WriteLine($"OBSERVED service update-option --new-label {Quote(text)}: {Describe(outcome)}");
                if (stored == text)
                    outcome.Should().BeNull($"Dataverse stored {Quote(text)} verbatim, so the read-back must confirm it");
                else
                    outcome.Should().BeOfType<PpdsException>("a label not stored as sent is reported, never as applied")
                        .Which.ErrorCode.Should().Be(MetadataErrorCodes.WriteNotConfirmed);
            }

        });

        await WithTableAsync(pool, Labels("Open", null), async (entity, _) =>
        {
            foreach (var text in texts)
            {
                await ExecuteAsync(pool, new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = entity, DisplayName = new Label(text, Base) }, MergeLabels = true, SolutionUniqueName = Solution });
                var stored = Text((await RetrieveTableAsync(pool, entity)).DisplayName, Base);
                _output.WriteLine($"OBSERVED raw UpdateEntity display name {Quote(text)}: stored {Quote(stored)}");
                // Reset first, so the service's own write (not the raw probe) is what the read-back can confirm.
                await ExecuteAsync(pool, new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = entity, DisplayName = new Label("Reset", Base) }, MergeLabels = true, SolutionUniqueName = Solution });
                var outcome = await Record.ExceptionAsync(() => service.UpdateTableAsync(new UpdateTableRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, DisplayName = text }));
                _output.WriteLine($"OBSERVED service table update --display-name {Quote(text)}: {Describe(outcome)}");
                if (stored == text)
                    outcome.Should().BeNull($"Dataverse stored {Quote(text)} verbatim, so the read-back must confirm it");
                else
                    outcome.Should().BeOfType<PpdsException>("a label not stored as sent is reported, never as applied")
                        .Which.ErrorCode.Should().Be(MetadataErrorCodes.WriteNotConfirmed);
            }
        });
    }

    // Read-only: what Dataverse returns for a global choice name that does not exist, on the retrieve paths the product
    // uses, and that the product reports it as not found (not a generic failure) whatever the organisation's language.
    // The not-found recognition relies on the fault code 0x80040217 with the English wording as a fallback; the CLI's
    // `optionset get` hint matches the English wording of the message it is given.
    [MetadataLiveFact]
    public async Task UnknownGlobalChoiceName_IsReportedAsNotFound()
    {
        var (pool, service) = await ConnectAsync();
        var missing = NewName("none"); // never created
        _output.WriteLine($"Unknown global choice name '{missing}'; organisation base language {Base}.");

        foreach (var asIfPublished in new[] { false, true })
        {
            var raw = await ObserveAsync(pool, $"RetrieveOptionSet unknown ({(asIfPublished ? "as-if-published" : "published")})",
                new RetrieveOptionSetRequest { Name = missing, RetrieveAsIfPublished = asIfPublished });
            _output.WriteLine($"OBSERVED {raw.Operation}: {raw.Outcome}");
            raw.Code.Should().Be(ObjectDoesNotExist, $"the not-found recognition assumes 0x80040217, but Dataverse returned {raw.Outcome}");
        }

        // Query service (CLI `optionset get`): the original fault surfaces, with its code and a message the CLI's
        // not-found hint recognises ("Could not find" / "optionset").
        var query = await Record.ExceptionAsync(() => new DataverseMetadataQueryService(pool).GetOptionSetAsync(missing));
        _output.WriteLine($"OBSERVED query service GetOptionSetAsync on an unknown name: {Describe(query)}");
        query.Should().NotBeNull();
        FindFault(query!)?.ErrorCode.Should().Be(ObjectDoesNotExist);
        (query!.Message.Contains("Could not find", StringComparison.OrdinalIgnoreCase) || query.Message.Contains("optionset", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("`optionset get` reports 'not found' (exit NotFoundError) only for this wording");

        // Authoring service: the original not-found fault surfaces untouched (AC-134), so a caller can recognise it by
        // its code. How the CLI then classifies it is recorded: ExceptionMapper has no rule for this fault code.
        foreach (var (operation, call) in new (string, Func<Task>)[]
        {
            ("optionset update", () => service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = missing, DisplayName = "Never" })),
            ("optionset update-option", () => service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = missing, Value = 1, NewLabel = "Never", SolutionUniqueName = Solution }))
        })
        {
            var ex = await Record.ExceptionAsync(call);
            _output.WriteLine($"OBSERVED service {operation} on an unknown name: {Describe(ex)}");
            ex.Should().NotBeNull(operation);
            FindFault(ex!).Should().NotBeNull($"{operation} keeps the Dataverse fault");
            FindFault(ex!)!.ErrorCode.Should().Be(ObjectDoesNotExist, operation);
            var (error, exit) = ExceptionMapper.MapWithExitCode(ex!);
            _output.WriteLine($"OBSERVED CLI classification of {operation} on an unknown name: code {error.Code}, exit {exit} (not-found exit is {ExitCodes.NotFoundError})");
        }
        (await ObserveAsync(pool, "RetrieveOptionSet unknown after the probes", new RetrieveOptionSetRequest { Name = missing, RetrieveAsIfPublished = true })).Code
            .Should().Be(ObjectDoesNotExist, "nothing was created");
    }

    /// <summary>Dataverse's "the language code is not a valid language for this organization" fault (observed live for 1033 on a French-base organisation).</summary>
    private const int InvalidLanguageCode = unchecked((int)0x80044195);

    private const string OldShapeDoesNotApply = "This regression replays the old 1033 label requests, which only misbehave on an organisation whose base " +
        "language is not 1033 and where 1033 is not provisioned. Here the base language is 1033 or 1033 is provisioned, so there is nothing to " +
        "reproduce; the ordinary live tests cover base-language writes on this organisation.";

    /// <summary>
    /// True when the old request shape (a 1033 label) is worth replaying: the base language is not 1033 and 1033 is
    /// not provisioned (the French-base organisation case). Prints what was read.
    /// </summary>
    private async Task<bool> ReplaysTheOld1033ShapeAsync(IDataverseConnectionPool pool)
    {
        var languages = ((RetrieveProvisionedLanguagesResponse)await ExecuteAsync(pool, new RetrieveProvisionedLanguagesRequest())).RetrieveProvisionedLanguages;
        var replay = Base != 1033 && !languages.Contains(1033);
        _output.WriteLine($"OBSERVED organisation base language {Base}; provisioned {string.Join(", ", languages)}; replaying the old 1033 request shape: {(replay ? "yes" : "no")}");
        return replay;
    }

    /// <summary>
    /// Every notice must be true: it quotes a stored non-base label that the caller is still shown after the write
    /// (spec AC-145). With no translation stored, the caller is shown the base label and there must be no notice.
    /// </summary>
    private static void AssertNoticesAreTrue(MetadataLabelWriteResult result, params Label?[] after)
    {
        foreach (var notice in result.ValidationMessages)
        {
            notice.Rule.Should().Be(MetadataLabelWriteResult.BaseLanguageLabelOnlyRule);
            notice.Message.Should().StartWith($"Only the base-language (LCID {Base}) ");
            after.Any(l => l?.UserLocalizedLabel is { } shown && shown.LanguageCode != Base && notice.Message.EndsWith($"still reads '{shown.Label}'.", StringComparison.Ordinal))
                .Should().BeTrue($"the notice '{notice.Message}' must describe a non-base label the caller is still shown");
        }
        foreach (var label in after)
            if (label?.UserLocalizedLabel is { } shown && shown.LanguageCode == Base)
                result.ValidationMessages.Should().NotContain(m => m.Message.Contains($"still reads '{shown.Label}'"), "the base label the caller is shown was changed");
    }

    private static string ShownText(Label? label) => label?.UserLocalizedLabel is { } shown ? $"LCID {shown.LanguageCode} '{shown.Label}'" : "null";

    private static string Quote(string? text) => text is null ? "null" : "'" + text.Replace("\n", "\\n") + "'";
}
