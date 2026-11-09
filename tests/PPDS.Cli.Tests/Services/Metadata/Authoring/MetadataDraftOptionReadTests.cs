using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Client;
using Xunit;
using SdkUpdateOption = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkDeleteOption = Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
public sealed class MetadataDraftOptionReadTests
{
    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("choices", false)]
    [InlineData("status", true)]
    [InlineData("choice", true)]
    [InlineData("choices", true)]
    public async Task Allocation_UsesDraftValues(string kind, bool dryRun)
    {
        using var f = new Fixture(kind);
        var value = await f.AddAsync(dryRun: dryRun);
        Assert.Equal(10002, value); // 10000 is published; 10001 exists only in drafts.
        Assert.True(Assert.Single(f.Reads).RetrieveAsIfPublished);
        if (dryRun) Assert.Single(f.Requests);
        else
        {
            Assert.Equal(10002, (int)f.Requests.Single(r => r.RequestName.StartsWith("Insert", StringComparison.Ordinal))["Value"]);
            Assert.Equal(10003, await f.AddAsync()); // A second unpublished insert must advance again.
        }
        f.AssertRoutingAndCancellation();
    }

    [Theory]
    [InlineData("status")]
    [InlineData("choice")]
    [InlineData("choices")]
    public async Task ExplicitDraftCollision_RejectsBeforeInsert(string kind)
    {
        using var f = new Fixture(kind);
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => f.AddAsync(value: 10001));
        Assert.Equal(MetadataErrorCodes.DuplicateOptionValue, ex.ErrorCode);
        Assert.IsType<RetrieveAttributeRequest>(Assert.Single(f.Requests));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("choice")]
    [InlineData("choices")]
    public async Task ExplicitUnusedValue_IsPreserved(string kind)
    {
        using var f = new Fixture(kind);
        Assert.Equal(77, await f.AddAsync(value: 77));
        Assert.Equal(77, (int)f.Requests.Last()["Value"]);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("choice")]
    [InlineData("choices")]
    public async Task AddWithColor_ReadsDraftAndPreservesOtherOptions(string kind)
    {
        using var f = new Fixture(kind);
        Assert.Equal(77, await f.AddAsync(value: 77, color: "#123456"));
        Assert.Equal(2, f.Reads.Count());
        Assert.All(f.Reads, r => Assert.True(r.RetrieveAsIfPublished));
        var updated = Assert.IsAssignableFrom<EnumAttributeMetadata>(Assert.Single(f.Requests.OfType<UpdateAttributeRequest>()).Attribute);
        Assert.Equal("#123456", updated.OptionSet.Options.Single(o => o.Value == 77).Color);
        Assert.Equal("Draft label", updated.OptionSet.Options.Single(o => o.Value == 10000).Label.LocalizedLabels[0].Label);
        Assert.Equal("Unpublished", updated.OptionSet.Options.Single(o => o.Value == 10001).Label.LocalizedLabels[0].Label);
        Assert.Null(updated.RequiredLevel); // Keep the existing minimal color carrier.
        Assert.DoesNotContain(f.Requests, r => r.RequestName == "PublishXml");
        f.AssertRoutingAndCancellation();
    }

    [Theory]
    [InlineData("status")]
    [InlineData("choice")]
    [InlineData("choices")]
    public async Task UpdateColor_PreservesDraftOptions(string kind)
    {
        using var f = new Fixture(kind);
        await f.UpdateAsync(10000, "#654321");
        Assert.False(f.Reads.First().RetrieveAsIfPublished); // Target resolution stays published.
        Assert.True(f.Reads.Last().RetrieveAsIfPublished); // Shared color readback includes drafts.
        var update = Assert.Single(f.Requests.OfType<UpdateAttributeRequest>());
        var options = Assert.IsAssignableFrom<EnumAttributeMetadata>(update.Attribute).OptionSet.Options;
        Assert.Contains(options, o => o.Value == 10001);
        Assert.Equal("#654321", options.Single(o => o.Value == 10000).Color);
        f.AssertRoutingAndCancellation();
    }

    [Fact]
    public async Task GenericLocalOptionAdd_ColorReadsDraft()
    {
        using var f = new Fixture("choice");
        var value = await f.Service.AddOptionValueAsync(new AddOptionValueRequest
        {
            OptionSetName = "unused_local_set", EntityLogicalName = "new_table", AttributeLogicalName = f.Attribute,
            Label = "New", Value = 77, Color = "#123456"
        }, f.Token);
        Assert.Equal(77, value);
        Assert.True(Assert.Single(f.Reads).RetrieveAsIfPublished);
        Assert.Single(f.Requests.OfType<UpdateAttributeRequest>());
        f.AssertRoutingAndCancellation();
    }

    [Theory]
    [InlineData("status")]
    [InlineData("choice")]
    [InlineData("choices")]
    public async Task PublicList_RemainsPublished(string kind)
    {
        using var f = new Fixture(kind);
        if (kind == "status")
        {
            var option = Assert.Single(await f.Service.ListStatusReasonsAsync("new_table", f.Token));
            Assert.Equal(10000, option.Value);
            Assert.Equal("Published", option.Label);
        }
        else
        {
            var option = Assert.Single(await f.Service.ListColumnOptionsAsync("new_table", f.Attribute, f.Token));
            Assert.Equal(10000, option.Value);
            Assert.Equal("Published", option.Label);
        }
        Assert.False(Assert.Single(f.Reads).RetrieveAsIfPublished);
        Assert.Single(f.Requests);
        f.AssertRoutingAndCancellation();
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("choices", false)]
    [InlineData("status", true)]
    [InlineData("choice", true)]
    [InlineData("choices", true)]
    public async Task UpdateAndRemove_TargetResolutionRemainsPublished(string kind, bool remove)
    {
        using var f = new Fixture(kind);
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => remove ? f.RemoveAsync(10001) : f.UpdateAsync(10001));
        Assert.Equal(MetadataErrorCodes.OptionNotFound, ex.ErrorCode);
        Assert.False(Assert.Single(f.Reads).RetrieveAsIfPublished);
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("choice")]
    public async Task AllocationCancellation_Propagates(string kind)
    {
        using var f = new Fixture(kind) { CancelReadNumber = 1 };
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.AddAsync());
        Assert.Same(f.Cancellation, ex);
        Assert.Single(f.Requests);
        f.AssertRoutingAndCancellation();
    }

    [Theory]
    [InlineData("status")]
    [InlineData("choice")]
    public async Task ColorReadCancellation_Propagates(string kind)
    {
        using var f = new Fixture(kind) { CancelReadNumber = 2 };
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.AddAsync(value: 77, color: "#123456"));
        Assert.Same(f.Cancellation, ex);
        Assert.DoesNotContain(f.Requests, r => r is UpdateAttributeRequest);
        f.AssertRoutingAndCancellation();
    }

    [Fact]
    public async Task AddStatusSdkFailure_UsesFixedMessageInCliEnvelope()
    {
        const string sdkMessage = "Synthetic SDK fault: this status value is not allowed.";
        using var f = new Fixture("status") { InsertFailure = new InvalidOperationException(sdkMessage) };
        var ex = await Assert.ThrowsAsync<PpdsException>(() => f.AddAsync(value: 77));
        Assert.Same(f.InsertFailure, ex.InnerException);
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, ex.ErrorCode);
        Assert.Equal("Failed to add status reason 'New' to 'new_table'.", ex.UserMessage);
        Assert.DoesNotContain(sdkMessage, ex.UserMessage);
        // EntityCommand.ExecuteAddStatusReasonAsync uses these exact mapper/writer contracts.
        // Source trace is recorded with the evidence; no auth-dependent CLI seam is required.
        var error = ExceptionMapper.Map(ex, context: "adding status reason to 'new_table'");
        using var output = new StringWriter();
        new JsonOutputWriter(output).WriteError(error);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(ex.UserMessage, json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain(sdkMessage, output.ToString());
        Assert.Equal(ExitCodes.Failure, ExceptionMapper.ToExitCode(ex));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _kind;
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly Mock<IPooledClient> _client = new(MockBehavior.Loose);
        private readonly List<(int Value, string Label)> _draft = [(10000, "Draft label"), (10001, "Unpublished")];
        private readonly List<CancellationToken> _tokens = [];
        public List<OrganizationRequest> Requests { get; } = [];
        public IEnumerable<RetrieveAttributeRequest> Reads => Requests.OfType<RetrieveAttributeRequest>();
        private readonly CancellationTokenSource _cancellationSource = new();
        public CancellationToken Token => _cancellationSource.Token;
        public OperationCanceledException Cancellation { get; }
        public int CancelReadNumber { get; init; }
        public Exception? InsertFailure { get; init; }
        public string Attribute => _kind == "status" ? "statuscode" : "new_choice";
        public DataverseMetadataAuthoringService Service { get; }

        public Fixture(string kind)
        {
            _kind = kind;
            Cancellation = new OperationCanceledException(Token);
            _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
                .Callback<DataverseClientOptions?, string?, CancellationToken>((_, _, token) => _tokens.Add(token))
                .ReturnsAsync(_client.Object);
            _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, token) =>
                {
                    _tokens.Add(token);
                    Requests.Add(request);
                    switch (request)
                    {
                        case RetrieveAttributeRequest read:
                            if (Reads.Count() == CancelReadNumber) throw Cancellation;
                            var values = read.RetrieveAsIfPublished ? _draft : [(10000, "Published")];
                            var set = new OptionSetMetadata { IsGlobal = false };
                            foreach (var (value, label) in values)
                            {
                                OptionMetadata option = kind == "status" ? new StatusOptionMetadata { State = 0 } : new OptionMetadata();
                                option.Value = value;
                                option.Label = new Label(label, 1033);
                                set.Options.Add(option);
                            }
                            EnumAttributeMetadata attribute = kind switch
                            {
                                "status" => new StatusAttributeMetadata(),
                                "choices" => new MultiSelectPicklistAttributeMetadata(),
                                _ => new PicklistAttributeMetadata()
                            };
                            attribute.OptionSet = set;
                            attribute.LogicalName = Attribute;
                            attribute.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired);
                            return Task.FromResult<OrganizationResponse>(new RetrieveAttributeResponse { Results = new ParameterCollection { ["AttributeMetadata"] = attribute } });
                        case InsertStatusValueRequest:
                        case InsertOptionValueRequest:
                            if (InsertFailure != null) throw InsertFailure;
                            var assigned = (int)request["Value"];
                            _draft.Add((assigned, "New"));
                            OrganizationResponse response = request is InsertStatusValueRequest ? new InsertStatusValueResponse() : new InsertOptionValueResponse();
                            response.Results["NewOptionValue"] = assigned;
                            return Task.FromResult(response);
                        case SdkUpdateOption:
                            return Task.FromResult<OrganizationResponse>(new UpdateOptionValueResponse());
                        case UpdateAttributeRequest:
                            return Task.FromResult<OrganizationResponse>(new UpdateAttributeResponse());
                        case SdkDeleteOption:
                            return Task.FromResult<OrganizationResponse>(new DeleteOptionValueResponse());
                        default: throw new InvalidOperationException("Unexpected SDK request in offline fixture.");
                    }
                });
            _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .Returns<QueryBase, CancellationToken>((query, token) =>
                {
                    _tokens.Add(token);
                    var entity = ((QueryExpression)query).EntityName;
                    var row = new Entity(entity);
                    if (entity == "solution") row["publisherid"] = new EntityReference("publisher", Guid.Parse("11111111-1111-1111-1111-111111111111"));
                    else row["customizationoptionvalueprefix"] = 1;
                    return Task.FromResult(new EntityCollection(new List<Entity> { row }));
                });
            Service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
        }

        public Task<int> AddAsync(int? value = null, bool dryRun = false, string? color = null) => _kind == "status"
            ? Service.AddStatusReasonAsync(new AddStatusReasonRequest { EntityLogicalName = "new_table", Label = "New", StateCode = 0, Value = value, SolutionUniqueName = value.HasValue ? null : "test_solution", DryRun = dryRun, Color = color }, ct: Token)
            : Service.AddColumnOptionAsync(new AddColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = Attribute, Label = "New", Value = value, SolutionUniqueName = value.HasValue ? null : "test_solution", DryRun = dryRun, Color = color }, Token);

        public Task UpdateAsync(int value, string? color = null) => _kind == "status"
            ? Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = "new_table", Value = value, Color = color }, Token)
            : Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = Attribute, Value = value, Color = color }, Token);

        public Task RemoveAsync(int value) => _kind == "status"
            ? Service.RemoveStatusReasonAsync(new RemoveStatusReasonRequest { EntityLogicalName = "new_table", Value = value }, Token)
            : Service.RemoveColumnOptionAsync(new RemoveColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = Attribute, Value = value }, Token);

        public void Dispose() => _cancellationSource.Dispose();

        public void AssertRoutingAndCancellation()
        {
            Assert.NotEmpty(_tokens);
            Assert.All(_tokens, token => Assert.Equal(Token, token));
            Assert.All(Reads, read =>
            {
                Assert.Equal("new_table", read.EntityLogicalName);
                Assert.Equal(Attribute, read.LogicalName);
            });
            Assert.All(Requests.Where(request => request is InsertStatusValueRequest or InsertOptionValueRequest), insert =>
            {
                Assert.Equal("new_table", insert["EntityLogicalName"]);
                Assert.Equal(Attribute, insert["AttributeLogicalName"]);
            });
            Assert.All(Requests.OfType<UpdateAttributeRequest>(), write => Assert.Equal("new_table", write.EntityName));
        }
    }
}
