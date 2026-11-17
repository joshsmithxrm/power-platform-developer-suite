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
        // The colour rides on the insert (observed live to be stored); the second draft read confirms it. No option
        // collection or attribute is re-sent, so the other draft options and their labels cannot be touched.
        var insert = Assert.Single(f.Requests, r => r is InsertOptionValueRequest or InsertStatusValueRequest);
        Assert.Equal("#123456", insert["Color"]);
        Assert.DoesNotContain(f.Requests, r => r is SdkUpdateOption or UpdateAttributeRequest);
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
        Assert.All(f.Reads, r => Assert.True(r.RetrieveAsIfPublished)); // Target resolution and color readback include drafts (#1531).
        // One request: the Color parameter and no Label, so an unpublished label edit cannot be reverted or rewritten.
        var update = Assert.Single(f.Requests.OfType<SdkUpdateOption>());
        Assert.Equal(10000, update.Value);
        Assert.Equal("#654321", update["Color"]);
        Assert.False(update.Parameters.ContainsKey("Label"));
        Assert.DoesNotContain(f.Requests, r => r is UpdateAttributeRequest);
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
        Assert.True(Assert.Single(f.Reads).RetrieveAsIfPublished); // the colour read-back
        Assert.Equal("#123456", Assert.Single(f.Requests.OfType<InsertOptionValueRequest>())["Color"]);
        Assert.DoesNotContain(f.Requests, r => r is SdkUpdateOption);
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
    [Trait("AC", "AC-72")]
    public async Task UpdateAndRemove_TargetResolutionReadsDrafts(string kind, bool remove)
    {
        // #1531: 10001 exists only in unpublished (draft) metadata. It used to fail with OptionNotFound.
        using var f = new Fixture(kind);
        if (remove) await f.RemoveAsync(10001); else await f.UpdateAsync(10001);
        Assert.True(Assert.Single(f.Reads).RetrieveAsIfPublished);
        if (remove) Assert.Equal(10001, Assert.Single(f.Requests.OfType<SdkDeleteOption>()).Value);
        else
        {
            var update = Assert.Single(f.Requests.OfType<SdkUpdateOption>());
            Assert.Equal(10001, update.Value);
            Assert.False(update.Parameters.ContainsKey("Label")); // The current draft label is left alone (not re-sent).
        }
        f.AssertRoutingAndCancellation();
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("choices", false)]
    [InlineData("status", true)]
    [InlineData("choice", true)]
    [InlineData("choices", true)]
    [Trait("AC", "AC-72")]
    public async Task AddThenUpdateOrRemove_WithoutPublish_TargetsTheNewOption(string kind, bool remove)
    {
        using var f = new Fixture(kind);
        var added = await f.AddAsync(); // Unpublished insert; derived from drafts as 10002.
        Assert.Equal(10002, added);
        if (remove) await f.RemoveAsync(added); else await f.UpdateAsync(added);
        Assert.DoesNotContain(f.Requests, r => r.RequestName == "PublishXml");
        if (remove) Assert.Equal(added, Assert.Single(f.Requests.OfType<SdkDeleteOption>()).Value);
        else Assert.Equal(added, Assert.Single(f.Requests.OfType<SdkUpdateOption>()).Value);
        Assert.All(f.Reads, r => Assert.True(r.RetrieveAsIfPublished));
        f.AssertRoutingAndCancellation();
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("choices", false)]
    [InlineData("status", true)]
    [InlineData("choice", true)]
    [InlineData("choices", true)]
    [Trait("AC", "AC-72")]
    public async Task UpdateAndRemove_MissingFromDrafts_StillOptionNotFound(string kind, bool remove)
    {
        using var f = new Fixture(kind);
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => remove ? f.RemoveAsync(55555) : f.UpdateAsync(55555));
        Assert.Equal(MetadataErrorCodes.OptionNotFound, ex.ErrorCode);
        Assert.True(Assert.Single(f.Reads).RetrieveAsIfPublished);
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("choices", false)]
    [InlineData("status", true)]
    [InlineData("choice", true)]
    [InlineData("choices", true)]
    [Trait("AC", "AC-140")]
    public async Task LabelTarget_PublishedAndDraftDisagree_IsRefusedAndNothingIsWritten(string kind, bool remove)
    {
        // Review scenario: published 1="Open", 2="Closed"; drafts relabel 1="Closed", 2="Done".
        // A listing shows 2="Closed", so --label Closed must not act on value 1.
        using var f = new Fixture(kind, draft: [(1, "Closed"), (2, "Done")], published: [(1, "Open"), (2, "Closed")]);
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => remove ? f.RemoveByLabelAsync("Closed") : f.UpdateByLabelAsync("Closed"));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.Contains("--value", ex.Message);
        Assert.Contains("publish", ex.Message);
        Assert.DoesNotContain(f.Requests, r => r is SdkUpdateOption or SdkDeleteOption);
        Assert.Equal(new[] { true, false }, f.Reads.Select(r => r.RetrieveAsIfPublished));
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("choices", false)]
    [InlineData("status", true)]
    [InlineData("choice", true)]
    [InlineData("choices", true)]
    [Trait("AC", "AC-140")]
    public async Task LabelTarget_SameOptionInDraftsButSeveralInPublished_IsRefused(string kind, bool remove)
    {
        // Drafts: only 1 is "Open". Published listing shows "Open" on 1 and 2.
        using var f = new Fixture(kind, draft: [(1, "Open"), (2, "Reopened")], published: [(1, "Open"), (2, "Open")]);
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => remove ? f.RemoveByLabelAsync("Open") : f.UpdateByLabelAsync("Open"));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.DoesNotContain(f.Requests, r => r is SdkUpdateOption or SdkDeleteOption);
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("choices", false)]
    [InlineData("status", true)]
    [InlineData("choice", true)]
    [InlineData("choices", true)]
    [Trait("AC", "AC-140")]
    public async Task LabelTarget_OnlyInPublishedView_IsRefused(string kind, bool remove)
    {
        // Published 1="Open"; drafts relabel it "Reopened". --label Open would be OPTION_NOT_FOUND in drafts,
        // although the listing shows it; refuse with guidance instead.
        using var f = new Fixture(kind, draft: [(1, "Reopened")], published: [(1, "Open")]);
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => remove ? f.RemoveByLabelAsync("Open") : f.UpdateByLabelAsync("Open"));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.DoesNotContain(f.Requests, r => r is SdkUpdateOption or SdkDeleteOption);
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("choices", true)]
    [InlineData("status", true)]
    [Trait("AC", "AC-140")]
    public async Task LabelTarget_AgreeingOrDraftOnly_Proceeds(string kind, bool remove)
    {
        // "Same" resolves to 1 in both views; "Fresh" exists only in drafts (unpublished relabel/add).
        using var f = new Fixture(kind, draft: [(1, "Same"), (2, "Fresh")], published: [(1, "Same"), (2, "Stale")]);
        if (remove) { await f.RemoveByLabelAsync("Same"); await f.RemoveByLabelAsync("Fresh"); }
        else { await f.UpdateByLabelAsync("Same"); await f.UpdateByLabelAsync("Fresh"); }
        var values = remove
            ? f.Requests.OfType<SdkDeleteOption>().Select(r => r.Value)
            : f.Requests.OfType<SdkUpdateOption>().Select(r => r.Value);
        Assert.Equal(new[] { 1, 2 }, values);
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("status", true)]
    [InlineData("choice", true)]
    [Trait("AC", "AC-136")]
    public async Task UpdateAndRemove_CallerCancellation_PropagatesInsteadOfSdkFailure(string kind, bool remove)
    {
        var f = new Fixture(kind);
        using (f)
        {
            f.WriteFailureFactory = () => { f.Cancel(); return f.Cancellation; };
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => remove ? f.RemoveAsync(10000) : f.UpdateAsync(10000));
            Assert.Same(f.Cancellation, ex);
            Assert.True(ex.CancellationToken.IsCancellationRequested);
        }
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("status", true)]
    [InlineData("choice", true)]
    [Trait("AC", "AC-136")]
    public async Task UpdateAndRemove_NonCallerCancellation_KeepsSdkFailure(string kind, bool remove)
    {
        // A transport-level cancellation (caller's token not cancelled) may hide a committed write, so it keeps
        // the previous SDK_OPERATION_FAILED reporting.
        using var f = new Fixture(kind);
        var transport = new TaskCanceledException("Synthetic HTTP-layer timeout");
        f.WriteFailureFactory = () => transport;
        var ex = await Assert.ThrowsAsync<PpdsException>(() => remove ? f.RemoveAsync(10000) : f.UpdateAsync(10000));
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, ex.ErrorCode);
        Assert.Same(transport, ex.InnerException);
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("choice", false)]
    [InlineData("status", true)]
    [InlineData("choice", true)]
    [Trait("AC", "AC-136")]
    public async Task UpdateAndRemove_SdkFault_SurfacesFaultCodeOnly(string kind, bool remove)
    {
        const string serverText = "Synthetic server diagnostic that must stay private.";
        using var f = new Fixture(kind)
        {
            WriteFailure = new System.ServiceModel.FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040203), Message = serverText }, serverText)
        };
        var ex = await Assert.ThrowsAsync<PpdsException>(() => remove ? f.RemoveAsync(10000) : f.UpdateAsync(10000));
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, ex.ErrorCode);
        Assert.Contains("Dataverse fault 0x80040203.", ex.UserMessage);
        Assert.DoesNotContain(serverText, ex.UserMessage);
        Assert.Same(f.WriteFailure, ex.InnerException);
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
        // The colour rode on the insert; only the confirming read was cancelled, and nothing else was written.
        Assert.Equal("#123456", Assert.Single(f.Requests, r => r is InsertOptionValueRequest or InsertStatusValueRequest)["Color"]);
        Assert.DoesNotContain(f.Requests, r => r is SdkUpdateOption);
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
        private readonly List<(int Value, string Label)> _draft;
        private readonly List<(int Value, string Label)> _published;
        private readonly Dictionary<int, string> _colors = [];
        private readonly List<CancellationToken> _tokens = [];
        public List<OrganizationRequest> Requests { get; } = [];
        public IEnumerable<RetrieveAttributeRequest> Reads => Requests.OfType<RetrieveAttributeRequest>();
        private readonly CancellationTokenSource _cancellationSource = new();
        public CancellationToken Token => _cancellationSource.Token;
        public OperationCanceledException Cancellation { get; }
        public int CancelReadNumber { get; init; }
        public Exception? InsertFailure { get; init; }
        public Exception? WriteFailure { get; init; }
        public Func<Exception>? WriteFailureFactory { get; set; }
        public void Cancel() => _cancellationSource.Cancel();
        public string Attribute => _kind == "status" ? "statuscode" : "new_choice";
        public DataverseMetadataAuthoringService Service { get; }

        public Fixture(string kind, List<(int Value, string Label)>? draft = null, List<(int Value, string Label)>? published = null)
        {
            _kind = kind;
            _draft = draft ?? [(10000, "Draft label"), (10001, "Unpublished")];
            _published = published ?? [(10000, "Published")];
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
                            var values = read.RetrieveAsIfPublished ? _draft : _published;
                            var set = new OptionSetMetadata { IsGlobal = false };
                            foreach (var (value, label) in values)
                            {
                                OptionMetadata option = kind == "status" ? new StatusOptionMetadata { State = 0 } : new OptionMetadata();
                                option.Value = value;
                                option.Label = new Label(label, 1033);
                                option.Color = _colors.GetValueOrDefault(value);
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
                            if (request.Parameters.TryGetValue("Color", out var insertedColor)) _colors[assigned] = (string)insertedColor;
                            OrganizationResponse response = request is InsertStatusValueRequest ? new InsertStatusValueResponse() : new InsertOptionValueResponse();
                            response.Results["NewOptionValue"] = assigned;
                            return Task.FromResult(response);
                        case SdkUpdateOption update:
                            if (WriteFailureFactory != null) throw WriteFailureFactory();
                            if (WriteFailure != null) throw WriteFailure;
                            if (update.Parameters.TryGetValue("Color", out var updatedColor)) _colors[update.Value] = (string)updatedColor;
                            return Task.FromResult<OrganizationResponse>(new UpdateOptionValueResponse());
                        case SdkDeleteOption:
                            if (WriteFailureFactory != null) throw WriteFailureFactory();
                            if (WriteFailure != null) throw WriteFailure;
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

        public Task UpdateByLabelAsync(string label) => _kind == "status"
            ? Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest { EntityLogicalName = "new_table", Label = label, NewLabel = "Renamed" }, Token)
            : Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = Attribute, Label = label, NewLabel = "Renamed" }, Token);

        public Task RemoveByLabelAsync(string label) => _kind == "status"
            ? Service.RemoveStatusReasonAsync(new RemoveStatusReasonRequest { EntityLogicalName = "new_table", Label = label }, Token)
            : Service.RemoveColumnOptionAsync(new RemoveColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = Attribute, Label = label }, Token);

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
            Assert.All(Requests.OfType<SdkUpdateOption>(), write =>
            {
                Assert.Equal("new_table", write["EntityLogicalName"]);
                Assert.Equal(Attribute, write["AttributeLogicalName"]);
            });
        }
    }
}
