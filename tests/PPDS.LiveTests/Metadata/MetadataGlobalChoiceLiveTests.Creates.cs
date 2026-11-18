using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Metadata;

/// <summary>
/// Every create path through the service (the code the CLI, TUI, MCP and RPC call), on any base language: table and its
/// primary name column, a text column with a description, a Boolean column, Choice and Choices columns with local
/// options, a global choice with options (created under a mixed-case name), a 1:N relationship and its lookup, an
/// alternate key, and a state-value update. Each label is checked by a raw as-if-published SDK read in every language
/// it is stored in. A service call that returns has confirmed its labels by its own read-back (it raises
/// WRITE_NOT_CONFIRMED otherwise), so each check also asserts that a success was reported only for what is stored.
/// </summary>
public sealed partial class MetadataGlobalChoiceLiveTests
{
    [MetadataLiveFact]
    public async Task Creates_ThroughTheService_StoreAndConfirmEveryLabel()
    {
        var (pool, service) = await ConnectAsync();
        var languages = await ProvisionedLanguagesAsync(pool);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parent = $"{Prefix}_ppdscp{suffix}";
        var child = $"{Prefix}_ppdscc{suffix}";
        var choice = $"{Prefix}_PpdsCc{suffix}"; // mixed case on purpose

        await RunOwnedAsync($"table '{parent}' (service create)",
            () => service.CreateTableAsync(new CreateTableRequest
            {
                SolutionUniqueName = Solution, SchemaName = parent, DisplayName = "PPDS create test", PluralDisplayName = "PPDS create tests",
                Description = "Temporary metadata. Safe to delete.", PrimaryAttributeSchemaName = $"{Prefix}_name{suffix}",
                PrimaryAttributeDisplayName = "Create name", OwnershipType = "UserOwned"
            }),
            () => ExecuteAsync(pool, new DeleteEntityRequest { LogicalName = parent }),
            async () =>
            {
                // Table and primary name column.
                var table = await RetrieveTableAsync(pool, parent);
                AssertStored("table display name", table.DisplayName, "PPDS create test", languages);
                AssertStored("table plural name", table.DisplayCollectionName, "PPDS create tests", languages);
                AssertStored("table description", table.Description, "Temporary metadata. Safe to delete.", languages);
                AssertStored("primary name column display name", (await RetrieveColumnAsync(pool, parent, $"{Prefix}_name{suffix}")).DisplayName, "Create name", languages);

                // A text column with a description.
                var text = $"{Prefix}_text{suffix}";
                await service.CreateColumnAsync(new CreateColumnRequest
                {
                    SolutionUniqueName = Solution, EntityLogicalName = parent, SchemaName = text, DisplayName = "Create text", Description = "Create text description",
                    ColumnType = SchemaColumnType.String, MaxLength = 100
                });
                var textColumn = await RetrieveColumnAsync(pool, parent, text);
                AssertStored("text column display name", textColumn.DisplayName, "Create text", languages);
                AssertStored("text column description", textColumn.Description, "Create text description", languages);

                // A Boolean column with its true and false labels.
                var flag = $"{Prefix}_flag{suffix}";
                await service.CreateColumnAsync(new CreateColumnRequest
                {
                    SolutionUniqueName = Solution, EntityLogicalName = parent, SchemaName = flag, DisplayName = "Create flag",
                    ColumnType = SchemaColumnType.Boolean, TrueLabel = "Create yes", FalseLabel = "Create no"
                });
                var flags = (BooleanAttributeMetadata)await RetrieveColumnAsync(pool, parent, flag);
                AssertStored("Boolean column display name", flags.DisplayName, "Create flag", languages);
                AssertStored("Boolean true label", flags.OptionSet.TrueOption.Label, "Create yes", languages);
                AssertStored("Boolean false label", flags.OptionSet.FalseOption.Label, "Create no", languages);

                // Choice and Choices columns with local options (one with a colour).
                foreach (var type in new[] { SchemaColumnType.Choice, SchemaColumnType.Choices })
                {
                    var pick = $"{Prefix}_{(type == SchemaColumnType.Choice ? "pick" : "multi")}{suffix}";
                    await service.CreateColumnAsync(new CreateColumnRequest
                    {
                        SolutionUniqueName = Solution, EntityLogicalName = parent, SchemaName = pick, DisplayName = $"Create {type}",
                        ColumnType = type, Options = [new OptionDefinition { Label = "Create red", Color = "#AA0000" }, new OptionDefinition { Label = "Create blue" }]
                    });
                    var options = await RetrieveOptionsAsync(pool, parent, pick);
                    _output.WriteLine($"OBSERVED {type} column create, local options: {string.Join("; ", options.Select(o => $"{o.Value}: {string.Join(", ", Pairs(o.Label))} color {o.Color ?? "null"}"))}");
                    options.Select(o => Text(o.Label, Base)).Should().Equal(new[] { "Create red", "Create blue" }, $"{type} local option labels in the base language");
                    foreach (var option in options)
                        AssertStored($"{type} option {option.Value} label", option.Label, Text(option.Label, Base)!, languages);
                }

                // An alternate key on the text column: is the just-created key returned at once, as-if-published and published?
                var key = $"{Prefix}_key{suffix}";
                var created = DateTime.UtcNow;
                await service.CreateKeyAsync(new CreateKeyRequest { SolutionUniqueName = Solution, EntityLogicalName = parent, SchemaName = key, DisplayName = "Create key", KeyAttributes = [text] });
                foreach (var asIfPublished in new[] { true, false })
                {
                    var stored = ((RetrieveEntityKeyResponse)await ExecuteAsync(pool, new RetrieveEntityKeyRequest { EntityLogicalName = parent, LogicalName = key.ToLowerInvariant(), RetrieveAsIfPublished = asIfPublished })).EntityKeyMetadata;
                    _output.WriteLine($"OBSERVED alternate key read {(asIfPublished ? "as-if-published" : "published")} {(DateTime.UtcNow - created).TotalSeconds:F0}s after create: display {string.Join(", ", Pairs(stored.DisplayName))}; index status {stored.EntityKeyIndexStatus}");
                    AssertStored($"alternate key display name ({(asIfPublished ? "as-if-published" : "published")} read)", stored.DisplayName, "Create key", languages);
                }

                // A state value.
                await service.UpdateStateValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateStateValueRequest { EntityLogicalName = parent, AttributeLogicalName = "statecode", Value = 0, Label = "Create active" });
                var state = (await RetrieveOptionsAsync(pool, parent, "statecode")).Single(o => o.Value == 0);
                _output.WriteLine($"OBSERVED state value update: {string.Join(", ", Pairs(state.Label))}");
                Text(state.Label, Base).Should().Be("Create active");

                // A second table, then a 1:N relationship to it with a lookup.
                await RunOwnedAsync($"table '{child}' (service create)",
                    () => service.CreateTableAsync(new CreateTableRequest
                    {
                        SolutionUniqueName = Solution, SchemaName = child, DisplayName = "PPDS create child", PluralDisplayName = "PPDS create children",
                        PrimaryAttributeSchemaName = $"{Prefix}_cname{suffix}", OwnershipType = "UserOwned"
                    }),
                    () => ExecuteAsync(pool, new DeleteEntityRequest { LogicalName = child }),
                    async () =>
                    {
                        var lookup = $"{Prefix}_parent{suffix}";
                        await service.CreateOneToManyAsync(new PPDS.Dataverse.Metadata.Authoring.CreateOneToManyRequest
                        {
                            SolutionUniqueName = Solution, SchemaName = $"{Prefix}_ppdsrel{suffix}",
                            ReferencedEntity = parent, ReferencingEntity = child, LookupSchemaName = lookup, LookupDisplayName = "Create parent"
                        });
                        AssertStored("lookup display name", (await RetrieveColumnAsync(pool, child, lookup)).DisplayName, "Create parent", languages);
                    });
            });

        // A global choice under a mixed-case name: what name Dataverse stores, and that the create confirmed its labels.
        await RunOwnedAsync($"global choice '{choice}' (service create)",
            () => service.CreateGlobalChoiceAsync(new CreateGlobalChoiceRequest
            {
                SolutionUniqueName = Solution, SchemaName = choice, DisplayName = "Create choice", Description = "Create choice description",
                Options = [new OptionDefinition { Label = "Create one" }, new OptionDefinition { Label = "Create two" }]
            }),
            () => DeleteChoiceUnderAnySpellingAsync(pool, choice),
            async () =>
            {
                var all = ((RetrieveAllOptionSetsResponse)await ExecuteAsync(pool, new RetrieveAllOptionSetsRequest { RetrieveAsIfPublished = true })).OptionSetMetadata;
                var stored = all.Single(o => string.Equals(o.Name, choice, StringComparison.OrdinalIgnoreCase));
                _output.WriteLine($"OBSERVED global choice created as '{choice}' is stored as '{stored.Name}' ({(stored.Name == choice ? "same spelling" : "different spelling")})");
                var set = await RetrieveChoiceAsync(pool, stored.Name, asIfPublished: true);
                AssertStored("global choice display name", set.DisplayName, "Create choice", languages);
                AssertStored("global choice description", set.Description, "Create choice description", languages);
                set.Options.Select(o => Text(o.Label, Base)).Should().Equal(new[] { "Create one", "Create two" });
                foreach (var option in set.Options)
                    AssertStored($"global choice option {option.Value} label", option.Label, Text(option.Label, Base)!, languages);
            });
    }

    /// <summary>
    /// Asserts the text a create sent is stored in the base language, and records every other stored language. Another
    /// provisioned language may hold a copy of the same text (observed for creates once a second language is
    /// provisioned); any other text there fails the check.
    /// </summary>
    private void AssertStored(string what, Label? label, string expected, IReadOnlyCollection<int> languages)
    {
        _output.WriteLine($"OBSERVED {what}: {string.Join(", ", Pairs(label))}");
        Text(label, Base).Should().Be(expected, $"{what}: the create was reported as successful, so the base-language text must be stored as sent");
        foreach (var (lcid, text) in Pairs(label).Where(p => p.Item1 != Base))
        {
            languages.Should().Contain(lcid, $"{what}: a stored label is in a provisioned language");
            text.Should().Be(expected, $"{what}: LCID {lcid} holds only a copy of the text the create sent");
        }
    }

    private static async Task<IReadOnlyCollection<int>> ProvisionedLanguagesAsync(IDataverseConnectionPool pool)
        => ((RetrieveProvisionedLanguagesResponse)await ExecuteAsync(pool, new RetrieveProvisionedLanguagesRequest())).RetrieveProvisionedLanguages;

    /// <summary>Deletes a global choice this test created, under its stored spelling (which may differ in case).</summary>
    private static async Task DeleteChoiceUnderAnySpellingAsync(IDataverseConnectionPool pool, string requested)
    {
        var all = ((RetrieveAllOptionSetsResponse)await ExecuteAsync(pool, new RetrieveAllOptionSetsRequest { RetrieveAsIfPublished = true })).OptionSetMetadata;
        var stored = all.Single(o => string.Equals(o.Name, requested, StringComparison.OrdinalIgnoreCase)).Name;
        await ExecuteAsync(pool, new DeleteOptionSetRequest { Name = stored });
    }
}
