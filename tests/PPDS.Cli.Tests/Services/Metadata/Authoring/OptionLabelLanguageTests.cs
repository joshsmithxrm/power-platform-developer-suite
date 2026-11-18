using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using SdkUpdateOption = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkUpdateState = Microsoft.Xrm.Sdk.Messages.UpdateStateValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Label writes never change a label the caller did not ask to change, and a requested label is written in the
/// base language (this fixture: an English-base organisation, LCID 1033; a non-1033 base is covered by
/// BaseLanguageLabelWriteTests) with MergeLabels so other languages keep their text. The fake models a caller whose UI language is
/// French (1036): every label carries English and French text and the user-localised label is the French one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OptionLabelLanguageTests
{
    public static TheoryData<string> Kinds => new() { "global", "local", "status" };

    /// <summary>
    /// An update that does not ask to change the label sends no Label at all, whatever the existing label holds, so
    /// it cannot replace the English text with the caller's user-language text. Asserted live: an UpdateOptionValue
    /// without a Label leaves the English label unchanged; a requested English label keeps the French one.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnrequestedCases))]
    [Trait("AC", "AC-142")]
    public async Task UnrequestedLabel_IsNeverSent(string kind, string existing)
    {
        var f = new Fixture
        {
            OpenLabel = existing switch
            {
                "empty-translation" => () => new Label(new LocalizedLabel("Open", 1033), new LocalizedLabel[] { new("Open", 1033), new("", 1036) }),
                "no-english" => () => new Label("Ouvert", 1036),
                "user-localised-only" => () => new Label { UserLocalizedLabel = new LocalizedLabel("Ouvert", 1036) },
                _ => null
            }
        };
        await f.UpdateAsync(kind, value: 1, newLabel: null);
        var update = Assert.Single(f.Requests.OfType<SdkUpdateOption>());
        Assert.True(update.MergeLabels);
        Assert.False(update.Parameters.ContainsKey("Label"));
    }

    public static TheoryData<string, string> UnrequestedCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var kind in new[] { "global", "local", "status" })
            foreach (var existing in new[] { "two-languages", "empty-translation", "no-english", "user-localised-only" })
                data.Add(kind, existing);
        return data;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("AC", "AC-142")]
    public async Task RequestedLabel_IsWrittenInEnglishAndMergedWithOtherLanguages(string kind)
    {
        var f = new Fixture();
        await f.UpdateAsync(kind, value: 1, newLabel: "Closed");
        var update = Assert.Single(f.Requests.OfType<SdkUpdateOption>());
        Assert.True(update.MergeLabels);
        Assert.Equal(new[] { (1033, "Closed") }, Pairs(update.Label));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("AC", "AC-142")]
    public async Task LabelSelector_MatchesTheUserLanguageLabel_AndLeavesTheEnglishLabelAlone(string kind)
    {
        var f = new Fixture();
        await f.UpdateByLabelAsync(kind, "Ouvert");
        var update = Assert.Single(f.Requests.OfType<SdkUpdateOption>());
        Assert.Equal(1, update.Value);
        Assert.True(update.MergeLabels);
        Assert.False(update.Parameters.ContainsKey("Label"), "a label used only as a selector is not written");
    }

    [Fact]
    [Trait("AC", "AC-142")]
    public async Task ChoiceUpdate_MergesTheRequestedEnglishLabel_AndResendsOtherLabelsInEveryLanguage()
    {
        var f = new Fixture();
        await f.Service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = "Sol", Name = Fixture.ChoiceName, DisplayName = "Renamed" });
        var update = Assert.Single(f.Requests.OfType<UpdateOptionSetRequest>());
        Assert.True(update.MergeLabels);
        Assert.Equal(new[] { (1033, "Renamed") }, Pairs(update.OptionSet.DisplayName));
        Assert.Equal(new[] { (1033, "English description"), (1036, "Description française") }, Pairs(update.OptionSet.Description));
    }

    [Fact]
    [Trait("AC", "AC-142")]
    public async Task StateValueUpdate_MergesTheEnglishLabel()
    {
        var f = new Fixture();
        await f.Service.UpdateStateValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateStateValueRequest
        {
            EntityLogicalName = "new_table", AttributeLogicalName = "statecode", Value = 0, Label = "Live"
        });
        var update = Assert.Single(f.Requests.OfType<SdkUpdateState>());
        Assert.True(update.MergeLabels);
        Assert.Equal(new[] { (1033, "Live") }, Pairs(update.Label));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("AC", "AC-145")]
    public async Task RequestedLabel_ForANonEnglishUser_SaysOnlyTheEnglishLabelIsWritten(string kind)
    {
        var f = new Fixture();
        var result = await f.UpdateAsync(kind, value: 1, newLabel: "Fermé");
        var notice = Assert.Single(result.ValidationMessages);
        Assert.Equal(MetadataLabelWriteResult.BaseLanguageLabelOnlyRule, notice.Rule);
        Assert.Equal("NewLabel", notice.Field);
        Assert.Contains("base-language (LCID 1033)", notice.Message);
        Assert.Contains("LCID 1036", notice.Message);
        Assert.Contains("'Ouvert'", notice.Message);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("AC", "AC-145")]
    public async Task NoNotice_WhenNoLabelIsRequested_OrTheUserLanguageIsEnglish(string kind)
    {
        Assert.Empty((await new Fixture().UpdateAsync(kind, value: 1, newLabel: null)).ValidationMessages);
        Assert.Empty((await new Fixture { UserLanguage = 1033 }.UpdateAsync(kind, value: 1, newLabel: "Closed")).ValidationMessages);
    }

    [Fact]
    [Trait("AC", "AC-145")]
    public async Task ChoiceUpdate_ForANonEnglishUser_SaysOnlyTheEnglishDisplayNameIsWritten()
    {
        var f = new Fixture();
        var result = await f.Service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = "Sol", Name = Fixture.ChoiceName, DisplayName = "Renamed" });
        var notice = Assert.Single(result.ValidationMessages);
        Assert.Equal("DisplayName", notice.Field);
        Assert.Contains("'Nom français'", notice.Message);
    }

    [Fact]
    [Trait("AC", "AC-145")]
    public async Task ChoiceUpdate_Description_ForANonEnglishUser_ReportsTheDescriptionNotice()
    {
        var result = await new Fixture().Service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = "Sol", Name = Fixture.ChoiceName, Description = "New text" });
        var notice = Assert.Single(result.ValidationMessages);
        Assert.Equal("Description", notice.Field);
        Assert.Contains("description Dataverse shows you (LCID 1036)", notice.Message);
        Assert.Contains("'Description française'", notice.Message);
        Assert.DoesNotContain("your language", notice.Message);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("AC", "AC-145")]
    public async Task NoNotice_WhenTheShownLabelAlreadyReadsTheRequestedText(string kind)
        => Assert.Empty((await new Fixture().UpdateAsync(kind, value: 1, newLabel: "Ouvert")).ValidationMessages);

    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("AC", "AC-142")]
    public async Task UnrequestedLabel_WithNoText_IsNotSent(string kind)
    {
        var f = new Fixture { OpenLabel = () => new Label(new LocalizedLabel("", 1033), new LocalizedLabel[] { new("", 1033) }) };
        await f.UpdateAsync(kind, value: 1, newLabel: null);
        var update = Assert.Single(f.Requests.OfType<SdkUpdateOption>());
        Assert.False(update.Parameters.ContainsKey("Label"));
        Assert.True(update.MergeLabels);
    }

    [Fact]
    [Trait("AC", "AC-142")]
    public async Task ColorOnlyUpdate_SendsOneRequestWithNoLabel_AndMergesLabels()
    {
        var f = new Fixture();
        await f.Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = Fixture.ChoiceName, Value = 1, Color = "#112233" });
        var update = Assert.Single(f.Requests.OfType<SdkUpdateOption>());
        Assert.Equal("#112233", update["Color"]);
        Assert.False(update.Parameters.ContainsKey("Label"));
        Assert.True(update.MergeLabels);
        Assert.DoesNotContain(f.Requests, r => r is UpdateOptionSetRequest);
    }

    private static (int, string)[] Pairs(Label label)
        => label.LocalizedLabels.Select(l => (l.LanguageCode, l.Label)).OrderBy(p => p.LanguageCode).ToArray();

    private sealed class Fixture
    {
        internal const string ChoiceName = "new_choice";
        public List<OrganizationRequest> Requests { get; } = [];
        public DataverseMetadataAuthoringService Service { get; }

        /// <summary>The caller's UI language: the language of every user-localised label.</summary>
        public int UserLanguage { get; init; } = 1036;

        /// <summary>Overrides the labels of option 1 (default: English 'Open', French 'Ouvert').</summary>
        public Func<Label>? OpenLabel { get; init; }

        private string? _storedColor;
        private readonly LabelWriteStore _store = new();

        private Label French(string english, string french) => new(
            UserLanguage == 1033 ? new LocalizedLabel(english, 1033) : new LocalizedLabel(french, 1036),
            new LocalizedLabel[] { new(english, 1033), new(french, 1036) });

        private OptionSetMetadata OptionSet(bool status)
        {
            var set = new OptionSetMetadata
            {
                Name = ChoiceName, IsGlobal = !status, OptionSetType = OptionSetType.Picklist,
                DisplayName = French("English name", "Nom français"),
                Description = French("English description", "Description française")
            };
            OptionMetadata open = status ? new StatusOptionMetadata { State = 0 } : new OptionMetadata();
            open.Value = 1;
            open.Label = OpenLabel?.Invoke() ?? French("Open", "Ouvert");
            open.Color = _storedColor;
            set.Options.Add(open);
            OptionMetadata other = status ? new StatusOptionMetadata { State = 0 } : new OptionMetadata();
            other.Value = 2;
            other.Label = French("Other", "Autre");
            set.Options.Add(other);
            return set;
        }

        public Fixture()
        {
            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
            var client = new Mock<IPooledClient>(MockBehavior.Loose);
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
            client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                {
                    Requests.Add(request);
                    _store.Record(request);
                    if (request is SdkUpdateOption { Value: 1 } written && written.Parameters.TryGetValue("Color", out var color))
                        _storedColor = (string?)color;
                    OrganizationResponse response = request switch
                    {
                        RetrieveOptionSetRequest => new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = OptionSet(status: false) } },
                        RetrieveAttributeRequest read => new RetrieveAttributeResponse
                        {
                            Results = new ParameterCollection
                            {
                                ["AttributeMetadata"] = read.LogicalName == "statuscode"
                                    ? new StatusAttributeMetadata { LogicalName = "statuscode", OptionSet = OptionSet(status: true) }
                                    : new PicklistAttributeMetadata { LogicalName = read.LogicalName, OptionSet = OptionSet(status: false) }
                            }
                        },
                        SdkUpdateOption => new UpdateOptionValueResponse(),
                        SdkUpdateState => new UpdateStateValueResponse(),
                        UpdateOptionSetRequest => new UpdateOptionSetResponse(),
                        _ => throw new InvalidOperationException("Unexpected SDK request in offline fixture: " + request.RequestName)
                    };
                    return Task.FromResult(_store.Apply(request, response));
                });
            Service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard(), baseLanguage: new FixedOrganizationBaseLanguage(1033));
        }

        public Task<MetadataLabelWriteResult> UpdateAsync(string kind, int value, string? newLabel) => kind switch
        {
            "global" => Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = ChoiceName, Value = value, NewLabel = newLabel, Description = newLabel == null ? "Changed description" : null }),
            // Without a new label, local and status updates change the colour (an update must change something).
            "local" => Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = "new_pick", Value = value, NewLabel = newLabel, Color = newLabel == null ? "#112233" : null }),
            _ => Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = "new_table", Value = value, NewLabel = newLabel, Color = newLabel == null ? "#112233" : null })
        };

        public Task<MetadataLabelWriteResult> UpdateByLabelAsync(string kind, string label) => kind switch
        {
            "global" => Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = ChoiceName, Label = label, Description = "Changed description" }),
            "local" => Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = "new_pick", Label = label, Color = "#112233" }),
            _ => Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = "new_table", Label = label, Color = "#112233" })
        };
    }
}
