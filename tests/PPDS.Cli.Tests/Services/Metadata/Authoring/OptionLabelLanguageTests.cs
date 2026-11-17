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
/// Label writes never change a label the caller did not ask to change, and a requested label is written for
/// LCID 1033 with MergeLabels so other languages keep their text. The fake models a caller whose UI language is
/// French (1036): every label carries English and French text and the user-localised label is the French one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OptionLabelLanguageTests
{
    public static TheoryData<string> Kinds => new() { "global", "local", "status" };

    [Theory]
    [MemberData(nameof(Kinds))]
    [Trait("AC", "AC-142")]
    public async Task UnrequestedLabel_IsResentUnchangedInEveryLanguage(string kind)
    {
        var f = new Fixture();
        await f.UpdateAsync(kind, value: 1, newLabel: null);
        var update = Assert.Single(f.Requests.OfType<SdkUpdateOption>());
        Assert.True(update.MergeLabels);
        Assert.Equal(new[] { (1033, "Open"), (1036, "Ouvert") }, Pairs(update.Label));
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
        Assert.Equal(new[] { (1033, "Open"), (1036, "Ouvert") }, Pairs(update.Label));
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

    private static (int, string)[] Pairs(Label label)
        => label.LocalizedLabels.Select(l => (l.LanguageCode, l.Label)).OrderBy(p => p.LanguageCode).ToArray();

    private sealed class Fixture
    {
        internal const string ChoiceName = "new_choice";
        public List<OrganizationRequest> Requests { get; } = [];
        public DataverseMetadataAuthoringService Service { get; }

        private static Label French(string english, string french) => new(
            new LocalizedLabel(french, 1036),
            new LocalizedLabel[] { new(english, 1033), new(french, 1036) });

        private static OptionSetMetadata OptionSet(bool status)
        {
            var set = new OptionSetMetadata
            {
                Name = ChoiceName, IsGlobal = !status, OptionSetType = OptionSetType.Picklist,
                DisplayName = French("English name", "Nom français"),
                Description = French("English description", "Description française")
            };
            OptionMetadata open = status ? new StatusOptionMetadata { State = 0 } : new OptionMetadata();
            open.Value = 1;
            open.Label = French("Open", "Ouvert");
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
                    return Task.FromResult(response);
                });
            Service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
        }

        public Task UpdateAsync(string kind, int value, string? newLabel) => kind switch
        {
            "global" => Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = ChoiceName, Value = value, NewLabel = newLabel, Description = newLabel == null ? "Changed description" : null }),
            "local" => Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = "new_pick", Value = value, NewLabel = newLabel }),
            _ => Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = "new_table", Value = value, NewLabel = newLabel })
        };

        public Task UpdateByLabelAsync(string kind, string label) => kind switch
        {
            "global" => Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = ChoiceName, Label = label, Description = "Changed description" }),
            "local" => Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = "new_pick", Label = label }),
            _ => Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = "new_table", Label = label })
        };
    }
}
