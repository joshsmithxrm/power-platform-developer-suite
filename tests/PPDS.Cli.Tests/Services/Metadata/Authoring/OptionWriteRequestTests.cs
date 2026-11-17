using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using SdkDeleteOption = Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest;
using SdkUpdateOption = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Request-shape rules shared by every option-value message (AC-146): a blank solution name is sent as none (Dataverse
/// rejects "" on all of them, asserted live), an update must request something, an empty description clears it, and
/// the generic update with entity + attribute (including statecode) writes and confirms the colour on that attribute.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OptionWriteRequestTests
{
    private const string Choice = "new_choice", Table = "new_table", Column = "new_pick";

    public static TheoryData<string> Operations() => new()
    {
        "global-add", "global-update", "global-delete", "global-reorder",
        "status-add", "status-update", "status-remove",
        "column-add", "column-update", "column-remove"
    };

    [Theory]
    [MemberData(nameof(Operations))]
    [Trait("AC", "AC-146")]
    public async Task BlankSolution_IsSentAsNoSolution_OnEveryOptionMessage(string operation)
    {
        foreach (var blank in new[] { "", " " })
        {
            var f = new Fixture();
            await f.Run(operation, blank);
            var written = Assert.Single(f.Requests, r => r is InsertOptionValueRequest or InsertStatusValueRequest or SdkUpdateOption or SdkDeleteOption or OrderOptionRequest);
            Assert.Null(written["SolutionUniqueName"]);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    [Trait("AC", "AC-146")]
    public async Task NamedSolution_IsSentUnchanged(string operation)
    {
        var f = new Fixture();
        await f.Run(operation, "Sol");
        Assert.Equal("Sol", Assert.Single(f.Requests, r => r is InsertOptionValueRequest or InsertStatusValueRequest or SdkUpdateOption or SdkDeleteOption or OrderOptionRequest)["SolutionUniqueName"]);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("status")]
    [InlineData("column")]
    [Trait("AC", "AC-146")]
    public async Task UpdateThatRequestsNothing_IsRefusedBeforeAnyDataverseCall(string kind)
    {
        var f = new Fixture();
        Task call = kind switch
        {
            "global" => f.Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = Choice, Value = 1 }),
            "status" => f.Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = Table, Value = 1 }),
            _ => f.Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = Table, ColumnLogicalName = Column, Value = 1 })
        };
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => call);
        Assert.Equal(MetadataErrorCodes.MissingRequiredField, error.ErrorCode);
        Assert.StartsWith("Nothing to update", error.Message);
        Assert.Empty(f.Requests);
    }

    [Fact]
    [Trait("AC", "AC-146")]
    public async Task EmptyDescription_IsSentAsAnEmptyEnglishDescription()
    {
        // Asserted live: UpdateOptionValue with an empty 1033 description clears it.
        var f = new Fixture();
        await f.Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = Choice, Value = 1, Description = "" });
        var update = Assert.Single(f.Requests.OfType<SdkUpdateOption>());
        var description = Assert.Single(update.Description.LocalizedLabels);
        Assert.Equal((1033, ""), (description.LanguageCode, description.Label));
        Assert.False(update.Parameters.ContainsKey("Label"));
    }

    [Theory]
    [InlineData("statecode")]
    [InlineData("statuscode")]
    [InlineData(Column)]
    [Trait("AC", "AC-146")]
    public async Task GenericUpdate_WithEntityAndAttribute_WritesAndConfirmsTheColorOnThatAttribute(string attribute)
    {
        var f = new Fixture();
        await f.Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            OptionSetName = Choice, Value = 1, EntityLogicalName = Table, AttributeLogicalName = attribute, Color = "#ABCDEF"
        });
        var update = Assert.Single(f.Requests.OfType<SdkUpdateOption>());
        Assert.Equal(Table, update["EntityLogicalName"]);
        Assert.Equal(attribute, update["AttributeLogicalName"]);
        Assert.Equal("#ABCDEF", update["Color"]);
        // The read-back targets the attribute, not the global choice used to resolve the option.
        var readBack = Assert.Single(f.Requests.OfType<RetrieveAttributeRequest>());
        Assert.Equal(attribute, readBack.LogicalName);
        Assert.True(readBack.RetrieveAsIfPublished);
    }

    [Fact]
    [Trait("AC", "AC-146")]
    public async Task StoredColor_IsComparedExactly()
    {
        // Dataverse stores the Color parameter verbatim (asserted live), so a value stored with another case is a mismatch.
        var f = new Fixture { StoreColorAs = c => c.ToLowerInvariant() };
        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Service.UpdateOptionValueAsync(
            new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = Choice, Value = 1, Color = "#ABCDEF" }));
        Assert.Equal(MetadataErrorCodes.WriteNotConfirmed, error.ErrorCode);

        var verbatim = new Fixture();
        await verbatim.Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = Choice, Value = 1, Color = "red" });
        Assert.Equal("red", Assert.Single(verbatim.Requests.OfType<SdkUpdateOption>())["Color"]);
    }

    private sealed class Fixture
    {
        private readonly Dictionary<(string Target, int Value), string> _colors = [];
        public List<OrganizationRequest> Requests { get; } = [];
        public DataverseMetadataAuthoringService Service { get; }
        public Func<string, string> StoreColorAs { get; init; } = c => c;

        public Fixture()
        {
            var client = new Mock<IPooledClient>();
            client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                {
                    Requests.Add(request);
                    var target = request.Parameters.TryGetValue("AttributeLogicalName", out var a) && a is string attribute ? attribute : Choice;
                    OrganizationResponse response = request switch
                    {
                        RetrieveOptionSetRequest => new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = Set(Choice, false, false) } },
                        RetrieveAttributeRequest read => new RetrieveAttributeResponse { Results = new ParameterCollection { ["AttributeMetadata"] = Attribute(read.LogicalName) } },
                        InsertOptionValueRequest or InsertStatusValueRequest => Inserted(request, target),
                        SdkUpdateOption update => Store(update, target, update.Value, new UpdateOptionValueResponse()),
                        SdkDeleteOption => new DeleteOptionValueResponse(),
                        OrderOptionRequest => new OrderOptionResponse(),
                        _ => new OrganizationResponse()
                    };
                    return Task.FromResult(response);
                });
            client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .Returns<QueryBase, CancellationToken>((query, _) =>
                {
                    var entity = ((QueryExpression)query).EntityName;
                    var row = new Entity(entity);
                    if (entity == "solution") row["publisherid"] = new EntityReference("publisher", Guid.Parse("11111111-1111-1111-1111-111111111111"));
                    else { row["customizationoptionvalueprefix"] = 1; row["customizationprefix"] = "new"; }
                    return Task.FromResult(new EntityCollection(new List<Entity> { row }));
                });
            var pool = new Mock<IDataverseConnectionPool>();
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
            Service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
        }

        private OrganizationResponse Inserted(OrganizationRequest request, string target)
        {
            OrganizationResponse response = request is InsertStatusValueRequest ? new InsertStatusValueResponse() : new InsertOptionValueResponse();
            response.Results["NewOptionValue"] = 5;
            return Store(request, target, 5, response);
        }

        private OrganizationResponse Store(OrganizationRequest request, string target, int value, OrganizationResponse response)
        {
            if (request.Parameters.TryGetValue("Color", out var color)) _colors[(target, value)] = StoreColorAs((string)color);
            return response;
        }

        private OptionSetMetadata Set(string target, bool status, bool state)
        {
            var set = new OptionSetMetadata { Name = Choice, IsGlobal = target == Choice, OptionSetType = OptionSetType.Picklist };
            foreach (var value in state ? new[] { 0, 1 } : new[] { 1, 5 })
            {
                OptionMetadata option = state ? new StateOptionMetadata() : status ? new StatusOptionMetadata { State = 0 } : new OptionMetadata();
                option.Value = value;
                option.Label = new Label(value == 1 ? "One" : value == 0 ? "Active" : "New", 1033);
                option.Color = _colors.GetValueOrDefault((target, value));
                set.Options.Add(option);
            }
            return set;
        }

        private AttributeMetadata Attribute(string logicalName) => logicalName switch
        {
            "statecode" => new StateAttributeMetadata { LogicalName = logicalName, OptionSet = Set(logicalName, false, true) },
            "statuscode" => new StatusAttributeMetadata { LogicalName = logicalName, OptionSet = Set(logicalName, true, false) },
            _ => new PicklistAttributeMetadata { LogicalName = logicalName, OptionSet = Set(logicalName, false, false) }
        };

        public Task Run(string operation, string solution) => operation switch
        {
            "global-add" => Service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = Choice, Label = "New", Value = 5, SolutionUniqueName = solution }),
            "global-update" => Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = Choice, Value = 1, NewLabel = "Renamed", SolutionUniqueName = solution }),
            "global-delete" => Service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest { OptionSetName = Choice, Value = 1, SolutionUniqueName = solution }),
            "global-reorder" => Service.ReorderOptionsAsync(new ReorderOptionsRequest { OptionSetName = Choice, Order = [5, 1], SolutionUniqueName = solution }),
            "status-add" => Service.AddStatusReasonAsync(new AddStatusReasonRequest { EntityLogicalName = Table, Label = "New", StateCode = 0, Value = 7, SolutionUniqueName = solution }),
            "status-update" => Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = Table, Value = 1, NewLabel = "Renamed", SolutionUniqueName = solution }),
            "status-remove" => Service.RemoveStatusReasonAsync(new RemoveStatusReasonRequest { EntityLogicalName = Table, Value = 1, SolutionUniqueName = solution }),
            "column-add" => Service.AddColumnOptionAsync(new AddColumnOptionRequest { EntityLogicalName = Table, ColumnLogicalName = Column, Label = "New", Value = 7, SolutionUniqueName = solution }),
            "column-update" => Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = Table, ColumnLogicalName = Column, Value = 1, NewLabel = "Renamed", SolutionUniqueName = solution }),
            _ => Service.RemoveColumnOptionAsync(new RemoveColumnOptionRequest { EntityLogicalName = Table, ColumnLogicalName = Column, Value = 1, SolutionUniqueName = solution })
        };
    }
}
