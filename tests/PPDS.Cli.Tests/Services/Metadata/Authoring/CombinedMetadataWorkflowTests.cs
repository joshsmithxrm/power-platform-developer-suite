using System.CommandLine;
using System.Text.Json;
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
    private readonly Queue<Func<OrganizationRequest, OrganizationResponse>> _replies = new();
    private readonly List<int> _sdkLeases = [];
    private int _checkouts;
    private int? _activeLease;
    private readonly DataverseMetadataAuthoringService _service;

    public CombinedMetadataWorkflowTests()
    {
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(null, null, _cancellation.Token)).ReturnsAsync(CheckOut);
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

    private async Task UpdateKeyLabelAsync()
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
        await _service.UpdateColumnAsync(new UpdateColumnRequest
        {
            SolutionUniqueName = Solution, EntityLogicalName = Table, ColumnLogicalName = Key, DisplayName = "Record key"
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
        var cmd = EntityCommand.CreateDeleteCommand(_ =>
            Task.FromResult(new ServiceCollection().AddSingleton<IMetadataAuthoringService>(_service).BuildServiceProvider()));
        var parsed = cmd.Parse(["--solution", Solution, "--entity", Table, "--dry-run", "--output-format", "json"]);
        Assert.Empty(parsed.Errors);
        using var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            Assert.Equal(ExitCodes.Success, await parsed.InvokeAsync());
        }
        finally
        {
            Console.SetOut(original);
        }
        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("dryRun").GetBoolean());
        var preview = data.GetProperty("preview");
        Assert.Equal("input-only", preview.GetProperty("validationScope").GetString());
        Assert.False(preview.GetProperty("targetExistenceChecked").GetBoolean());
        Assert.False(preview.GetProperty("dependenciesChecked").GetBoolean());
        Assert.False(preview.GetProperty("deletionFeasibilityChecked").GetBoolean());
        Assert.Equal(checkoutsBeforePreview, _checkouts);
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
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), _cancellation.Token))
            .Returns<QueryBase, CancellationToken>((query, _) =>
            {
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
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), _cancellation.Token))
            .Returns<OrganizationRequest, CancellationToken>((request, _) =>
            {
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
