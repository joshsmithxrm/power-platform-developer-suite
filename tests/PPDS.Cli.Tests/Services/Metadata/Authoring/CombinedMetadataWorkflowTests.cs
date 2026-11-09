using System.CommandLine;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class CombinedMetadataWorkflowTests : IDisposable
{
    private const string Table = "new_table";
    private const string Solution = "TestSolution";
    private const string Key = "new_recordkey";
    private const string Local = "new_category";
    private const string Canonical = "new_ServiceType";
    private readonly CancellationTokenSource _cancellation = new();
    private CancellationToken _operationToken;
    private readonly Queue<Func<OrganizationRequest, OrganizationResponse>> _replies = new();
    private readonly List<int> _sdkLeases = [];
    private int _checkouts;
    private int? _activeLease;
    private readonly DataverseMetadataAuthoringService _service;

    public CombinedMetadataWorkflowTests()
    {
        _operationToken = _cancellation.Token;
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .Returns<DataverseClientOptions?, string?, CancellationToken>((_, _, token) =>
            {
                Assert.Equal(_operationToken, token);
                return Task.FromResult(CheckOut());
            });
        _service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
    }

    [Theory]
    [InlineData(SchemaColumnType.Choice)]
    [InlineData(SchemaColumnType.Choices)]
    public async Task GlobalAttachment_KeyLabels_DraftColorAndPublishedList_ShareTarget(SchemaColumnType type)
    {
        var dto = NewGlobalColumn(type);
        Expect<RetrieveAllOptionSetsRequest>(r =>
        {
            Assert.True(r.RetrieveAsIfPublished);
            var response = new RetrieveAllOptionSetsResponse();
            response.Results["OptionSetMetadata"] = new OptionSetMetadataBase[]
            {
                new OptionSetMetadata { Name = Canonical, IsGlobal = true }
            };
            return response;
        });
        Expect<CreateAttributeRequest>(r =>
        {
            Assert.Equal(Table, r.EntityName);
            Assert.Equal(Solution, r.SolutionUniqueName);
            EnumAttributeMetadata column = type == SchemaColumnType.Choices
                ? Assert.IsType<MultiSelectPicklistAttributeMetadata>(r.Attribute)
                : Assert.IsType<PicklistAttributeMetadata>(r.Attribute);
            Assert.True(column.OptionSet.IsGlobal);
            Assert.Equal(Canonical, column.OptionSet.Name);
            return new CreateAttributeResponse { Results = new ParameterCollection { ["AttributeId"] = Guid.NewGuid() } };
        });
        await _service.CreateColumnAsync(dto, ct: _cancellation.Token);
        Assert.Equal("New_sErViCeType", dto.OptionSetName);
        Assert.NotEqual(_sdkLeases[0], _sdkLeases[1]);
        AssertReleased();

        await UpdateKeyLabelAsync();
        await AddDraftOptionAndReadPublishedAsync();
        await PreviewDeleteAsync();
    }

    [Fact]
    public async Task FailedGlobalLookup_ReleasesLeaseAndAllowsIndependentKeyUpdate()
    {
        var fault = new PpdsException(ErrorCodes.Connection.Failed, "Synthetic catalog failure.");
        Expect<RetrieveAllOptionSetsRequest>(_ => throw fault);
        var thrown = await Assert.ThrowsAsync<PpdsException>(() =>
            _service.CreateColumnAsync(NewGlobalColumn(SchemaColumnType.Choice), ct: _cancellation.Token));
        Assert.Same(fault, thrown);
        Assert.Single(_sdkLeases);
        AssertReleased();
        await UpdateKeyLabelAsync();
    }

    [Fact]
    public async Task PrimaryIdLabelsWithPublish_ReturnsUpdateLeaseBeforePublish()
    {
        await UpdateKeyLabelAsync(publish: true);
    }

    private async Task UpdateKeyLabelAsync(bool publish = false)
    {
        var key = new AttributeMetadata
        {
            LogicalName = Key, SchemaName = "new_RecordKey", Description = new Label("Existing description", 1033),
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired)
        };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.IsPrimaryId))!.SetValue(key, true);
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!.SetValue(key, AttributeTypeCode.Uniqueidentifier);
        Expect<RetrieveAttributeRequest>(r =>
        {
            Assert.Equal(Table, r.EntityLogicalName);
            Assert.Equal(Key, r.LogicalName);
            Assert.False(r.RetrieveAsIfPublished);
            return AttributeResponse(key);
        });
        Expect<RetrieveEntityRequest>(r =>
        {
            Assert.Equal(Table, r.LogicalName);
            Assert.Equal(EntityFilters.Entity, r.EntityFilters);
            Assert.False(r.RetrieveAsIfPublished);
            var table = new EntityMetadata { LogicalName = Table };
            typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!.SetValue(table, Key);
            return new RetrieveEntityResponse { Results = new ParameterCollection { ["EntityMetadata"] = table } };
        });
        Expect<UpdateAttributeRequest>(r =>
        {
            Assert.Equal(Table, r.EntityName);
            Assert.True(r.MergeLabels);
            var fresh = Assert.IsType<AttributeMetadata>(r.Attribute);
            Assert.NotSame(key, fresh);
            Assert.Equal(Key, fresh.LogicalName);
            Assert.Equal(key.SchemaName, fresh.SchemaName);
            var label = Assert.Single(fresh.DisplayName.LocalizedLabels);
            Assert.Equal("Record key", label.Label);
            Assert.Equal(1033, label.LanguageCode);
            Assert.Null(fresh.Description);
            Assert.Null(fresh.RequiredLevel);
            Assert.Null(fresh.IsPrimaryId);
            return new UpdateAttributeResponse();
        });
        if (publish)
        {
            Expect<PublishXmlRequest>(r =>
            {
                Assert.Contains($"<entity>{Table}</entity>", r.ParameterXml);
                return new PublishXmlResponse();
            });
        }
        await _service.UpdateColumnAsync(new UpdateColumnRequest
        {
            SolutionUniqueName = Solution, EntityLogicalName = Table, ColumnLogicalName = Key, DisplayName = "Record key", Publish = publish
        }, ct: _cancellation.Token);
        AssertReleased();
    }

    private async Task AddDraftOptionAndReadPublishedAsync()
    {
        PicklistAttributeMetadata? lastRead = null;
        OptionMetadata? inserted = null;
        void ExpectRead(bool drafts)
        {
            Expect<RetrieveAttributeRequest>(r =>
            {
                Assert.Equal(Table, r.EntityLogicalName);
                Assert.Equal(Local, r.LogicalName);
                Assert.Equal(drafts, r.RetrieveAsIfPublished);
                var set = new OptionSetMetadata { IsGlobal = false };
                set.Options.Add(new OptionMetadata(new Label("Published", 1033), 10000));
                if (r.RetrieveAsIfPublished)
                {
                    set.Options.Add(new OptionMetadata(new Label("Draft only", 1033), 10001) { Color = "#010203" });
                    if (inserted != null) set.Options.Add(inserted);
                }
                lastRead = new PicklistAttributeMetadata
                {
                    LogicalName = Local, SchemaName = "new_Category", OptionSet = set,
                    RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired)
                };
                return AttributeResponse(lastRead);
            });
        }

        ExpectRead(true);
        Expect<InsertOptionValueRequest>(r =>
        {
            Assert.Equal(Table, r["EntityLogicalName"]);
            Assert.Equal(Local, r["AttributeLogicalName"]);
            Assert.Equal(10002, r.Value);
            inserted = new OptionMetadata { Label = r.Label, Value = r.Value };
            return new InsertOptionValueResponse { Results = new ParameterCollection { ["NewOptionValue"] = r.Value } };
        });
        ExpectRead(true);
        Expect<UpdateAttributeRequest>(r =>
        {
            Assert.Equal(Table, r.EntityName);
            var carrier = Assert.IsType<PicklistAttributeMetadata>(r.Attribute);
            Assert.NotSame(lastRead, carrier);
            Assert.Equal(Local, carrier.LogicalName);
            Assert.Equal("new_Category", carrier.SchemaName);
            Assert.Null(carrier.RequiredLevel);
            var draft = Assert.Single(carrier.OptionSet.Options, o => o.Value == 10001);
            Assert.Equal("Draft only", Assert.Single(draft.Label.LocalizedLabels).Label);
            Assert.Equal("#010203", draft.Color);
            Assert.Equal("#abcdef", Assert.Single(carrier.OptionSet.Options, o => o.Value == 10002).Color);
            return new UpdateAttributeResponse();
        });
        var value = await _service.AddColumnOptionAsync(new AddColumnOptionRequest
        {
            SolutionUniqueName = Solution, EntityLogicalName = Table, ColumnLogicalName = Local,
            Label = "Added", Color = "#abcdef"
        }, _cancellation.Token);
        Assert.Equal(10002, value);
        AssertReleased();

        ExpectRead(false);
        var published = Assert.Single(await _service.ListColumnOptionsAsync(Table, Local, _cancellation.Token));
        Assert.Equal(10000, published.Value);
        Assert.Equal("Published", published.Label);
        AssertReleased();
    }

    private async Task PreviewDeleteAsync()
    {
        var checkoutsBeforePreview = _checkouts;
        var sdkOperationsBeforePreview = _sdkLeases.Count;
        var previousToken = _operationToken;
        var targetMetadataId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var providerCalls = 0;
        Expect<RetrieveEntityRequest>(r =>
        {
            Assert.Equal(Table, r.LogicalName);
            Assert.Equal(EntityFilters.Entity, r.EntityFilters);
            Assert.True(r.RetrieveAsIfPublished);
            Assert.Equal(Guid.Empty, r.MetadataId);
            return new RetrieveEntityResponse { Results = new ParameterCollection
            {
                ["EntityMetadata"] = new EntityMetadata { LogicalName = Table, MetadataId = targetMetadataId }
            } };
        });
        Expect<RetrieveDependenciesForDeleteRequest>(r =>
        {
            Assert.Equal(targetMetadataId, r.ObjectId);
            Assert.Equal(1, r.ComponentType);
            return new RetrieveDependenciesForDeleteResponse { Results = new ParameterCollection
            {
                ["EntityCollection"] = new EntityCollection { MoreRecords = false, TotalRecordCount = 0, TotalRecordCountLimitExceeded = false }
            } };
        });
        var cmd = EntityCommand.CreateDeleteCommand(token =>
        {
            Assert.Equal(0, providerCalls++);
            Assert.Null(_activeLease);
            Assert.Equal(_cancellation.Token, previousToken);
            _operationToken = token;
            return Task.FromResult(new ServiceCollection().AddSingleton<IMetadataAuthoringService>(_service).BuildServiceProvider());
        });
        var parsed = cmd.Parse(["--solution", Solution, "--entity", Table, "--dry-run", "--output-format", "json"]);
        Assert.Empty(parsed.Errors);
        using var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            Assert.Equal(ExitCodes.Success, await parsed.InvokeAsync(cancellationToken: _cancellation.Token));
        }
        finally
        {
            Console.SetOut(original);
            _operationToken = previousToken;
        }
        Assert.Equal(1, providerCalls);
        Assert.Equal(_cancellation.Token, _operationToken);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("dryRun").GetBoolean());
        var preview = data.GetProperty("preview");
        Assert.Equal("delete-dependencies", preview.GetProperty("validationScope").GetString());
        Assert.True(preview.GetProperty("targetExistenceChecked").GetBoolean());
        Assert.True(preview.GetProperty("dependenciesChecked").GetBoolean());
        Assert.False(preview.GetProperty("deletionFeasibilityChecked").GetBoolean());
        Assert.Equal("no-blocking-dependencies", preview.GetProperty("status").GetString());
        Assert.Equal(targetMetadataId, preview.GetProperty("targetObjectId").GetGuid());
        Assert.Equal(1, preview.GetProperty("targetComponentType").GetInt32());
        Assert.Empty(preview.GetProperty("blockingComponents").EnumerateArray());
        Assert.Equal(0, preview.GetProperty("dependencyRowCount").GetInt32());
        Assert.Equal(0, preview.GetProperty("distinctComponentCount").GetInt32());
        Assert.Equal(checkoutsBeforePreview + 2, _checkouts);
        Assert.Equal(new[] { checkoutsBeforePreview + 1, checkoutsBeforePreview + 2 }, _sdkLeases.Skip(sdkOperationsBeforePreview));
        AssertReleased();
    }

    private IPooledClient CheckOut()
    {
        Assert.Null(_activeLease);
        var id = ++_checkouts;
        _activeLease = id;
        var client = new Mock<IPooledClient>(MockBehavior.Strict);
        client.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            Assert.Equal(id, _activeLease);
            _activeLease = null;
            return ValueTask.CompletedTask;
        });
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Returns<QueryBase, CancellationToken>((query, token) =>
            {
                Assert.Equal(_operationToken, token);
                Assert.Equal(id, _activeLease);
                var table = Assert.IsType<QueryExpression>(query).EntityName;
                var row = new Entity(table);
                if (table == "solution") row["publisherid"] = new EntityReference("publisher", Guid.Parse("11111111-1111-1111-1111-111111111111"));
                else
                {
                    Assert.Equal("publisher", table);
                    row["customizationprefix"] = "new";
                    row["customizationoptionvalueprefix"] = 1;
                }
                return Task.FromResult(new EntityCollection(new List<Entity> { row }));
            });
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((request, token) =>
            {
                Assert.Equal(_operationToken, token);
                Assert.Equal(id, _activeLease);
                _sdkLeases.Add(id);
                return Task.FromResult(_replies.Dequeue()(request));
            });
        return client.Object;
    }

    private void Expect<T>(Func<T, OrganizationResponse> reply) where T : OrganizationRequest =>
        _replies.Enqueue(request => reply(Assert.IsType<T>(request)));

    private void AssertReleased()
    {
        Assert.Empty(_replies);
        Assert.Null(_activeLease);
    }

    private static RetrieveAttributeResponse AttributeResponse(AttributeMetadata attribute) =>
        new() { Results = new ParameterCollection { ["AttributeMetadata"] = attribute } };

    private static CreateColumnRequest NewGlobalColumn(SchemaColumnType type) => new()
    {
        EntityLogicalName = Table, SolutionUniqueName = Solution, SchemaName = "new_Selection",
        DisplayName = "Selection", ColumnType = type, OptionSetName = "New_sErViCeType"
    };

    public void Dispose() => _cancellation.Dispose();
}
