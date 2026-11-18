using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// AC-147: table and typed-column label updates write base-language labels (here an English-base organisation, LCID 1033)
/// with MergeLabels, never re-send an unrequested label, and report <c>BASE_LANGUAGE_LABEL_ONLY</c> when the label Dataverse
/// shows the caller is a stored label in another language that differs from the requested text. A non-1033 base is
/// covered by <see cref="BaseLanguageLabelWriteTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TableColumnLabelLanguageTests
{
    private const string Table = "new_table";
    private const string Column = "new_text";

    [Fact]
    [Trait("AC", "AC-147")]
    public async Task TableUpdate_ForANonEnglishUser_MergesEnglishLabels_AndReportsEachUnchangedShownLabel()
    {
        var f = new Fixture(userLanguage: 1036);
        var result = await f.Service.UpdateTableAsync(new UpdateTableRequest
        {
            SolutionUniqueName = "Sol", EntityLogicalName = Table,
            DisplayName = "Customer", PluralDisplayName = "Customers", Description = "Customer table"
        });

        var read = Assert.Single(f.Requests.OfType<RetrieveEntityRequest>(), r => !r.RetrieveAsIfPublished); // published, like the column read
        Assert.Single(f.Requests.OfType<RetrieveEntityRequest>(), r => r.RetrieveAsIfPublished); // the label read-back
        Assert.Equal(EntityFilters.Entity, read.EntityFilters);
        var update = Assert.Single(f.Requests.OfType<UpdateEntityRequest>());
        Assert.True(update.MergeLabels);
        Assert.Equal((1033, "Customer"), Single(update.Entity.DisplayName));
        Assert.Equal((1033, "Customers"), Single(update.Entity.DisplayCollectionName));
        Assert.Equal((1033, "Customer table"), Single(update.Entity.Description));

        Assert.Equal(new[] { "DisplayName", "PluralDisplayName", "Description" }, result.ValidationMessages.Select(m => m.Field));
        Assert.All(result.ValidationMessages, m =>
        {
            Assert.Equal(MetadataLabelWriteResult.BaseLanguageLabelOnlyRule, m.Rule);
            Assert.Contains("base-language (LCID 1033)", m.Message);
            Assert.Contains("LCID 1036", m.Message);
        });
        Assert.Contains("'Compte'", result.ValidationMessages[0].Message);
        Assert.Contains("plural name", result.ValidationMessages[1].Message);
    }

    [Fact]
    [Trait("AC", "AC-147")]
    public async Task TableUpdate_WithoutALabel_ReadsNothing_AndStillMerges()
    {
        var f = new Fixture(userLanguage: 1036);
        var result = await f.Service.UpdateTableAsync(new UpdateTableRequest
        {
            SolutionUniqueName = "Sol", EntityLogicalName = Table, HasNotes = true
        });

        Assert.Empty(result.ValidationMessages);
        Assert.DoesNotContain(f.Requests, r => r is RetrieveEntityRequest);
        var update = Assert.Single(f.Requests.OfType<UpdateEntityRequest>());
        Assert.True(update.MergeLabels);
        Assert.Null(update.Entity.DisplayName);
        Assert.Null(update.Entity.DisplayCollectionName);
        Assert.Null(update.Entity.Description);
    }

    [Fact]
    [Trait("AC", "AC-147")]
    public async Task TableUpdate_NoNotice_ForAnEnglishUser_OrWhenTheShownLabelAlreadyReadsTheText()
    {
        Assert.Empty((await new Fixture(userLanguage: 1033).Service.UpdateTableAsync(new UpdateTableRequest
        {
            SolutionUniqueName = "Sol", EntityLogicalName = Table, DisplayName = "Customer"
        })).ValidationMessages);
        Assert.Empty((await new Fixture(userLanguage: 1036).Service.UpdateTableAsync(new UpdateTableRequest
        {
            SolutionUniqueName = "Sol", EntityLogicalName = Table, DisplayName = "Compte"
        })).ValidationMessages);
    }

    [Fact]
    [Trait("AC", "AC-147")]
    public async Task TypedColumnUpdate_ForANonEnglishUser_MergesEnglishLabels_AndReportsTheUnchangedShownLabels()
    {
        var f = new Fixture(userLanguage: 1036);
        var result = await f.Service.UpdateColumnAsync(new UpdateColumnRequest
        {
            SolutionUniqueName = "Sol", EntityLogicalName = Table, ColumnLogicalName = Column,
            DisplayName = "Name", Description = "The name"
        });

        var update = Assert.Single(f.Requests.OfType<UpdateAttributeRequest>());
        Assert.True(update.MergeLabels);
        Assert.Equal((1033, "Name"), Single(update.Attribute.DisplayName));
        Assert.Equal((1033, "The name"), Single(update.Attribute.Description));
        Assert.Equal(new[] { "DisplayName", "Description" }, result.ValidationMessages.Select(m => m.Field));
        Assert.Contains("'Nom'", result.ValidationMessages[0].Message);
    }

    [Fact]
    [Trait("AC", "AC-147")]
    public async Task TypedColumnUpdate_WithoutALabel_SendsNoLabel_AndGivesNoNotice()
    {
        var f = new Fixture(userLanguage: 1036);
        var result = await f.Service.UpdateColumnAsync(new UpdateColumnRequest
        {
            SolutionUniqueName = "Sol", EntityLogicalName = Table, ColumnLogicalName = Column, MaxLength = 200
        });

        Assert.Empty(result.ValidationMessages);
        var update = Assert.Single(f.Requests.OfType<UpdateAttributeRequest>());
        Assert.True(update.MergeLabels);
        Assert.Null(update.Attribute.DisplayName);
        Assert.Null(update.Attribute.Description);
    }

    [Fact]
    [Trait("AC", "AC-147")]
    public async Task DryRuns_ReturnNoNotices_AndSendNothing()
    {
        // Table and column update dry-runs are input-only (their documented scope): they read nothing, so they give
        // no notice, with or without a requested label.
        var f = new Fixture(userLanguage: 1036);
        Assert.Same(MetadataLabelWriteResult.None, await f.Service.UpdateTableAsync(new UpdateTableRequest
        {
            SolutionUniqueName = "Sol", EntityLogicalName = Table, DisplayName = "Customer", DryRun = true
        }));
        Assert.Same(MetadataLabelWriteResult.None, await f.Service.UpdateColumnAsync(new UpdateColumnRequest
        {
            SolutionUniqueName = "Sol", EntityLogicalName = Table, ColumnLogicalName = Column, DisplayName = "Name", DryRun = true
        }));
        Assert.Empty(f.Requests);
    }

    private static (int, string) Single(Label label)
    {
        var only = Assert.Single(label.LocalizedLabels);
        return (only.LanguageCode, only.Label);
    }

    private sealed class Fixture
    {
        private readonly int _userLanguage;
        private readonly LabelWriteStore _store = new();
        public List<OrganizationRequest> Requests { get; } = [];
        public DataverseMetadataAuthoringService Service { get; }

        public Fixture(int userLanguage)
        {
            _userLanguage = userLanguage;
            var client = new Mock<IPooledClient>();
            client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                {
                    Requests.Add(request);
                    _store.Record(request);
                    OrganizationResponse response = request switch
                    {
                        RetrieveEntityRequest => new RetrieveEntityResponse { Results = new ParameterCollection
                        {
                            ["EntityMetadata"] = new EntityMetadata
                            {
                                LogicalName = Table,
                                DisplayName = Shown("Account", "Compte"),
                                DisplayCollectionName = Shown("Accounts", "Comptes"),
                                Description = Shown("Account table", "Table des comptes")
                            }
                        } },
                        RetrieveAttributeRequest => new RetrieveAttributeResponse { Results = new ParameterCollection
                        {
                            ["AttributeMetadata"] = new StringAttributeMetadata
                            {
                                LogicalName = Column, SchemaName = "new_Text", MaxLength = 100,
                                DisplayName = Shown("Name", "Nom"), Description = Shown("The name", "Le nom")
                            }
                        } },
                        UpdateEntityRequest => new UpdateEntityResponse(),
                        UpdateAttributeRequest => new UpdateAttributeResponse(),
                        _ => throw new InvalidOperationException("Unexpected SDK request: " + request.RequestName)
                    };
                    return Task.FromResult(_store.Apply(request, response));
                });
            var pool = new Mock<IDataverseConnectionPool>();
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
            Service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard(), baseLanguage: new FixedOrganizationBaseLanguage(1033));
        }

        /// <summary>English and French text; the user-localised label is the caller's UI language.</summary>
        private Label Shown(string english, string french) => new(
            _userLanguage == 1033 ? new LocalizedLabel(english, 1033) : new LocalizedLabel(french, 1036),
            new LocalizedLabel[] { new(english, 1033), new(french, 1036) });
    }
}
