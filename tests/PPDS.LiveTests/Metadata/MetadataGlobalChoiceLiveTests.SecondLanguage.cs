using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Sdk;
using SdkUpdateOptionValue = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkUpdateStateValue = Microsoft.Xrm.Sdk.Messages.UpdateStateValueRequest;

namespace PPDS.LiveTests.Metadata;

/// <summary>
/// Requirement 3 (a label write leaves other languages alone) on the paths the other multi-language tests do not
/// cover: a state-value update, an option description update, clearing an option, table or column description that has
/// a translation, and every create that touches a component which already has translations (a column, a Boolean column,
/// an alternate key, a 1:N relationship and an option added to a table or a global choice). Run with a second language
/// provisioned (PPDS_TEST_SECOND_LCID).
/// <para>
/// Second-language setup labels are set with <see cref="SetSecondLanguageAsync"/>: Dataverse was observed to acknowledge
/// metadata writes and drop their second-language labels for stretches of several minutes after other metadata activity
/// (specs/metadata-authoring.md "Label languages", "Second-language labels dropped for a while"); the helper waits up to
/// 12 minutes for it to pass, then fails saying the precondition could not be set up.
/// </para>
/// </summary>
public sealed partial class MetadataGlobalChoiceLiveTests
{
    /// <summary>How long a setup write that carries a second-language label is retried (stretches of 6 to more than 45 minutes were observed).</summary>
    private static readonly TimeSpan SecondLanguageWindow = TimeSpan.FromMinutes(12);

    [MetadataLiveMultiLanguageFact]
    public async Task SecondLanguage_SurvivesOptionDescriptionAndStateValueUpdates_AndClearingDescriptions()
    {
        var (pool, service) = await ConnectAsync();
        var second = await RequireSecondLanguageAsync(pool);
        var name = NewName("sld");
        // The translations are given at create and then checked (and re-sent until stored). Adding a translation
        // afterwards to a global option created without one, by UpdateOptionValue, was not stored for 12 minutes in two
        // full runs here (observed 2026-11-18; the product never sends such a write), while a create carrying it was.
        await WithChoiceAsync(pool, name, "Second language", new Label("Second language", Base), [Labels("Open", (second, "Ouvert"))], async () =>
        {
            var value = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single().Value!.Value;
            await SetSecondLanguageAsync("global option label and description",
                () => ExecuteAsync(pool, new SdkUpdateOptionValue { OptionSetName = name, Value = value, MergeLabels = true, SolutionUniqueName = Solution,
                    Label = Labels("Open", (second, "Ouvert")), Description = Labels("Option description", (second, "Description de l'option")) }),
                async () => (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single() is var o
                    && Text(o.Label, second) == "Ouvert" && Text(o.Description, second) == "Description de l'option");

            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, Description = "Changed description", SolutionUniqueName = Solution });
            var option = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
            _output.WriteLine($"OBSERVED option description update: description {string.Join(", ", Pairs(option.Description))}; label {string.Join(", ", Pairs(option.Label))}");
            Text(option.Description, Base).Should().Be("Changed description");
            Text(option.Description, second).Should().Be("Description de l'option", "an option description update keeps the other language's description");
            Text(option.Label, second).Should().Be("Ouvert", "a description-only update changes no label");

            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = value, Description = "", SolutionUniqueName = Solution });
            option = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single();
            _output.WriteLine($"OBSERVED option description cleared: description {string.Join(", ", Pairs(option.Description))}");
            Text(option.Description, Base).Should().BeNullOrEmpty("clearing empties the base-language description");
            Text(option.Description, second).Should().Be("Description de l'option", "clearing keeps the other language's description");
        }, optionDescriptions: [Labels("Option description", (second, "Description de l'option"))]);

        await WithTableAsync(pool, Labels("Open", null), async (entity, column) =>
        {
            await SetSecondLanguageAsync("table description",
                () => ExecuteAsync(pool, new UpdateEntityRequest { MergeLabels = true, SolutionUniqueName = Solution,
                    Entity = new EntityMetadata { LogicalName = entity, Description = Labels("Table description", (second, "Description de la table")) } }),
                async () => Text((await RetrieveTableAsync(pool, entity)).Description, second) == "Description de la table");
            await SetSecondLanguageAsync("column description",
                () => ExecuteAsync(pool, new UpdateAttributeRequest { EntityName = entity, MergeLabels = true, SolutionUniqueName = Solution,
                    Attribute = new PicklistAttributeMetadata { LogicalName = column, Description = Labels("Column description", (second, "Description de la colonne")) } }),
                async () => Text((await RetrieveColumnAsync(pool, entity, column)).Description, second) == "Description de la colonne");
            await SetSecondLanguageAsync("state value label",
                () => ExecuteAsync(pool, new SdkUpdateStateValue { EntityLogicalName = entity, AttributeLogicalName = "statecode", Value = 0, MergeLabels = true, Label = Labels("Active", (second, "Actif")) }),
                async () => Text((await RetrieveOptionsAsync(pool, entity, "statecode")).Single(o => o.Value == 0).Label, second) == "Actif");

            await service.UpdateStateValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateStateValueRequest { EntityLogicalName = entity, AttributeLogicalName = "statecode", Value = 0, Label = "Live state" });
            var state = (await RetrieveOptionsAsync(pool, entity, "statecode")).Single(o => o.Value == 0).Label;
            _output.WriteLine($"OBSERVED state value update: {string.Join(", ", Pairs(state))}");
            Text(state, Base).Should().Be("Live state");
            Text(state, second).Should().Be("Actif", "a state-value update keeps the other language's label");

            await service.UpdateTableAsync(new UpdateTableRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, Description = "" });
            var table = await RetrieveTableAsync(pool, entity);
            _output.WriteLine($"OBSERVED table description cleared: {string.Join(", ", Pairs(table.Description))}");
            Text(table.Description, Base).Should().BeNullOrEmpty();
            Text(table.Description, second).Should().Be("Description de la table", "clearing a table description keeps the other language's text");

            await service.UpdateColumnAsync(new UpdateColumnRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, ColumnLogicalName = column, Description = "" });
            var after = await RetrieveColumnAsync(pool, entity, column);
            _output.WriteLine($"OBSERVED column description cleared: {string.Join(", ", Pairs(after.Description))}");
            Text(after.Description, Base).Should().BeNullOrEmpty();
            Text(after.Description, second).Should().Be("Description de la colonne", "clearing a column description keeps the other language's text");
        });
    }

    [MetadataLiveMultiLanguageFact]
    public async Task Creates_KeepTheSecondLanguageOfWhatTheyTouch()
    {
        var (pool, service) = await ConnectAsync();
        var second = await RequireSecondLanguageAsync(pool);
        await WithTableAsync(pool, Labels("Open", null), async (entity, column) =>
        {
            await SetSecondLanguageAsync("table labels",
                () => ExecuteAsync(pool, new UpdateEntityRequest { MergeLabels = true, SolutionUniqueName = Solution, Entity = new EntityMetadata
                {
                    LogicalName = entity, DisplayName = Labels("PPDS label live test", (second, "Test FR")),
                    DisplayCollectionName = Labels("PPDS label live tests", (second, "Tests FR")), Description = Labels("Temporary metadata. Safe to delete.", (second, "Métadonnées temporaires"))
                } }),
                async () => (await RetrieveTableAsync(pool, entity)) is var t && Text(t.DisplayName, second) == "Test FR"
                    && Text(t.DisplayCollectionName, second) == "Tests FR" && Text(t.Description, second) == "Métadonnées temporaires");
            var open = (await RetrieveOptionsAsync(pool, entity, column)).Single().Value!.Value;
            var setLocal = new SdkUpdateOptionValue { Value = open, MergeLabels = true, SolutionUniqueName = Solution, Label = Labels("Open", (second, "Ouvert")) };
            setLocal["EntityLogicalName"] = entity; setLocal["AttributeLogicalName"] = column;
            await SetSecondLanguageAsync("local option label", () => ExecuteAsync(pool, setLocal),
                async () => Text((await RetrieveOptionsAsync(pool, entity, column)).Single(o => o.Value == open).Label, second) == "Ouvert");
            var tableBefore = await RetrieveTableAsync(pool, entity);

            async Task TranslationsKept(string after)
            {
                var table = await RetrieveTableAsync(pool, entity);
                var local = (await RetrieveOptionsAsync(pool, entity, column)).Single(o => o.Value == open).Label;
                _output.WriteLine($"OBSERVED after {after}: table display {string.Join(", ", Pairs(table.DisplayName))}; local option {string.Join(", ", Pairs(local))}");
                Pairs(table.DisplayName).Should().BeEquivalentTo(Pairs(tableBefore.DisplayName), $"{after} leaves the table's display name in every language");
                Pairs(table.DisplayCollectionName).Should().BeEquivalentTo(Pairs(tableBefore.DisplayCollectionName), $"{after} leaves the table's plural name in every language");
                Pairs(table.Description).Should().BeEquivalentTo(Pairs(tableBefore.Description), $"{after} leaves the table's description in every language");
                Text(local, second).Should().Be("Ouvert", $"{after} leaves the existing option's other-language label");
            }

            var suffix = Guid.NewGuid().ToString("N")[..6];
            var text = $"{Prefix}_t{suffix}";
            await service.CreateColumnAsync(new CreateColumnRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, SchemaName = text, DisplayName = "Text", ColumnType = SchemaColumnType.String, MaxLength = 100 });
            await TranslationsKept("column create");
            await service.CreateColumnAsync(new CreateColumnRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, SchemaName = $"{Prefix}_f{suffix}", DisplayName = "Flag", ColumnType = SchemaColumnType.Boolean });
            await TranslationsKept("Boolean column create");
            await service.CreateKeyAsync(new CreateKeyRequest { SolutionUniqueName = Solution, EntityLogicalName = entity, SchemaName = $"{Prefix}_k{suffix}", DisplayName = "Key", KeyAttributes = [text] });
            await TranslationsKept("alternate key create");
            await service.AddColumnOptionAsync(new AddColumnOptionRequest { EntityLogicalName = entity, ColumnLogicalName = column, Label = "Added", SolutionUniqueName = Solution });
            await TranslationsKept("add-option on the column");

            var child = $"{Prefix}_ppdslch{suffix}";
            await RunOwnedAsync($"table '{child}' (service create)",
                () => service.CreateTableAsync(new CreateTableRequest { SolutionUniqueName = Solution, SchemaName = child, DisplayName = "PPDS child", PluralDisplayName = "PPDS children", PrimaryAttributeSchemaName = $"{Prefix}_cn{suffix}", OwnershipType = "UserOwned" }),
                () => ExecuteAsync(pool, new DeleteEntityRequest { LogicalName = child }),
                async () =>
                {
                    await service.CreateOneToManyAsync(new PPDS.Dataverse.Metadata.Authoring.CreateOneToManyRequest
                    {
                        SolutionUniqueName = Solution, SchemaName = $"{Prefix}_ppdslrel{suffix}", ReferencedEntity = entity, ReferencingEntity = child,
                        LookupSchemaName = $"{Prefix}_lp{suffix}", LookupDisplayName = "Parent"
                    });
                    await TranslationsKept("1:N relationship create");
                });
        });

        var name = NewName("slc");
        // The translation is given at create (see SecondLanguage_SurvivesOptionDescriptionAndStateValueUpdates_AndClearingDescriptions).
        await WithChoiceAsync(pool, name, "Second language", new Label("Second language", Base), [Labels("Open", (second, "Ouvert"))], async () =>
        {
            var value = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single().Value!.Value;
            await SetSecondLanguageAsync("global option label",
                () => ExecuteAsync(pool, new SdkUpdateOptionValue { OptionSetName = name, Value = value, MergeLabels = true, SolutionUniqueName = Solution, Label = Labels("Open", (second, "Ouvert")) }),
                async () => Text((await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single(o => o.Value == value).Label, second) == "Ouvert");
            await service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = name, Label = "Added", SolutionUniqueName = Solution });
            var existing = (await RetrieveChoiceAsync(pool, name, asIfPublished: true)).Options.Single(o => o.Value == value).Label;
            _output.WriteLine($"OBSERVED existing global option after add-option: {string.Join(", ", Pairs(existing))}");
            Text(existing, second).Should().Be("Ouvert", "adding an option leaves the other options' translations");
        });
    }

    /// <summary>
    /// Sends a setup write that carries a second-language label and checks the label is stored. Dataverse was observed to
    /// acknowledge such writes and drop the second-language label for stretches of several minutes (once more than 45)
    /// after other metadata activity, so the write is repeated every 30 seconds until the label is stored, for up to
    /// <see cref="SecondLanguageWindow"/>; then the test fails saying the precondition could not be set up. Each retry is
    /// written to the output.
    /// </summary>
    private async Task SetSecondLanguageAsync(string what, Func<Task> write, Func<Task<bool>> stored)
    {
        var started = DateTime.UtcNow;
        for (var attempt = 1; ; attempt++)
        {
            await write();
            if (await stored())
            {
                if (attempt > 1)
                    _output.WriteLine($"OBSERVED setup of the {what}: the second-language label was stored on attempt {attempt}, {(DateTime.UtcNow - started).TotalSeconds:F0}s after the first.");
                return;
            }
            _output.WriteLine($"OBSERVED setup of the {what}: Dataverse acknowledged the write but did not store its second-language label (attempt {attempt}, " +
                $"{(DateTime.UtcNow - started).TotalSeconds:F0}s); retrying in 30 seconds.");
            if (DateTime.UtcNow - started > SecondLanguageWindow)
                throw new XunitException($"Precondition not met: Dataverse did not store the second-language label of the {what} within " +
                    $"{SecondLanguageWindow.TotalMinutes:F0} minutes of retries. Nothing the product does is involved in this setup step.");
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }
}
