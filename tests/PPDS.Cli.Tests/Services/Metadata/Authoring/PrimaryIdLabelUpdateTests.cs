using System.CommandLine;
using System.Runtime.Serialization;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
public sealed class PrimaryIdLabelUpdateTests : IDisposable
{
    private const string Table = "new_table";
    // Deliberately does not end in 'id'. The table definition identifies the key.
    private const string Key = "new_recordkey";
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
    private readonly Mock<ICachedMetadataProvider> _cache = new();
    private readonly List<OrganizationRequest> _requests = [];
    private readonly DataverseMetadataAuthoringService _service;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly List<string> _lifecycle = [];
    private AttributeMetadata _attribute = NewCandidate();
    private EntityMetadata? _entity = NewEntity("NEW_RECORDKEY");
    private Exception? _lookupFault;
    private Exception? _updateFault;
    private Exception? _checkoutFault;
    private int _failedCheckout;
    private int _checkoutCount;

    public PrimaryIdLabelUpdateTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, _cancellation.Token)).ReturnsAsync(CheckOutClient);
        _cache.Setup(c => c.InvalidateEntity(Table)).Callback(() => _lifecycle.Add("invalidate"));
        _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(),
            new InactiveFakeShakedownGuard(), cacheProvider: _cache.Object);
    }

    private IPooledClient CheckOutClient()
    {
        if (++_checkoutCount == _failedCheckout)
        {
            _lifecycle.Add("checkout failed");
            throw _checkoutFault!;
        }

        // A second checkout while the only slot is occupied would wait for itself. Fail immediately.
        if (!_slot.Wait(0, _cancellation.Token))
            throw new TimeoutException("The metadata operation still holds the only pool slot.");

        _lifecycle.Add("checkout");
        var client = new Mock<IPooledClient>(MockBehavior.Strict);
        var released = false;
        client.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            Assert.False(released);
            released = true;
            _slot.Release();
            _lifecycle.Add("dispose");
            return ValueTask.CompletedTask;
        });
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), _cancellation.Token))
            .Returns<OrganizationRequest, CancellationToken>((request, _) =>
            {
                Assert.False(released);
                _lifecycle.Add(request.RequestName);
                _requests.Add(request);
                OrganizationResponse response = request switch
                {
                    RetrieveAttributeRequest => new RetrieveAttributeResponse
                    {
                        Results = new ParameterCollection { ["AttributeMetadata"] = _attribute }
                    },
                    RetrieveEntityRequest when _lookupFault != null => throw _lookupFault,
                    RetrieveEntityRequest => new RetrieveEntityResponse
                    {
                        Results = new ParameterCollection { ["EntityMetadata"] = _entity }
                    },
                    UpdateAttributeRequest when _updateFault != null => throw _updateFault,
                    UpdateAttributeRequest => new UpdateAttributeResponse(),
                    PublishXmlRequest => new PublishXmlResponse(),
                    _ => throw new InvalidOperationException("Unexpected SDK request in test.")
                };
                return Task.FromResult(response);
            });
        return client.Object;
    }

    [Theory, Trait("AC", "AC-74"), Trait("AC", "AC-77")]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task SingleSlot_UpdatesAndPublishesInOrder(bool primaryId, bool publish)
    {
        if (!primaryId)
            _attribute = new StringAttributeMetadata { LogicalName = Key, SchemaName = "new_RecordKey" };

        await UpdateAsync(NewRequest("New label", publish: publish));
        var expected = new List<string> { "RetrieveAttribute" };
        if (primaryId) expected.Add("RetrieveEntity");
        expected.Add("UpdateAttribute");
        expected.Add("invalidate");
        if (publish) expected.Add("PublishXml");
        AssertSingleSlotOrder(expected);
    }

    [Theory, Trait("AC", "AC-77")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleSlot_FailedOperationReturnsClient(bool cancelLookup)
    {
        Exception fault;
        if (cancelLookup)
            _lookupFault = fault = new OperationCanceledException(_cancellation.Token);
        else
            _updateFault = fault = new PpdsException(MetadataErrorCodes.SdkOperationFailed, "Synthetic update fault.");

        var failure = await Record.ExceptionAsync(() => UpdateAsync(NewRequest("New label", publish: true)));
        Assert.Same(fault, failure);
        var expected = new List<string> { "RetrieveAttribute", "RetrieveEntity" };
        if (!cancelLookup) expected.Add("UpdateAttribute");
        AssertSingleSlotOrder(expected);
        _cache.VerifyNoOtherCalls();
    }

    [Theory, Trait("AC", "AC-77")]
    [InlineData(2)] // Authoritative table read.
    [InlineData(3)] // Update.
    public async Task SingleSlot_CheckoutFailureLeavesSlotFree(int checkout)
    {
        _failedCheckout = checkout;
        _checkoutFault = new TimeoutException("Synthetic checkout timeout.");
        var failure = await Record.ExceptionAsync(() => UpdateAsync(NewRequest("New label", publish: true)));
        Assert.Same(_checkoutFault, failure);
        var expected = new List<string> { "RetrieveAttribute" };
        if (checkout == 3) expected.Add("RetrieveEntity");
        expected.Add("checkout failed");
        AssertSingleSlotOrder(expected);
        _cache.VerifyNoOtherCalls();
    }

    private void AssertSingleSlotOrder(List<string> steps)
    {
        var expected = new List<string>();
        foreach (var step in steps)
        {
            if (step is "invalidate" or "checkout failed")
                expected.Add(step);
            else
                expected.AddRange(["checkout", step, "dispose"]);
        }
        Assert.Equal(expected, _lifecycle);
        Assert.Equal(1, _slot.CurrentCount);
    }

    [Theory]
    [InlineData("Record number", null, false)]
    [InlineData(null, "Shown on the form", false)]
    [InlineData("Record number", "Shown on the form", true)]
    [InlineData(null, "", false)]
    public async Task VerifiedPrimaryId_SendsOnlyRequestedLabels(string? displayName, string? description, bool publish)
    {
        var request = NewRequest(displayName, description, publish);
        request.ColumnLogicalName = "NEW_RECORDKEY";
        await UpdateAsync(request);

        var expectedRequests = new List<string> { "RetrieveAttribute", "RetrieveEntity", "UpdateAttribute" };
        if (publish) expectedRequests.Add("PublishXml");
        Assert.Equal(expectedRequests, _requests.Select(r => r.RequestName));
        var attributeRead = Assert.IsType<RetrieveAttributeRequest>(_requests[0]);
        Assert.Equal(Table, attributeRead.EntityLogicalName);
        Assert.Equal(request.ColumnLogicalName, attributeRead.LogicalName);
        var tableRead = Assert.IsType<RetrieveEntityRequest>(_requests[1]);
        Assert.Equal(Table, tableRead.LogicalName);
        Assert.Equal(EntityFilters.Entity, tableRead.EntityFilters);
        Assert.Equal(attributeRead.RetrieveAsIfPublished, tableRead.RetrieveAsIfPublished);
        var update = Assert.IsType<UpdateAttributeRequest>(_requests[2]);
        Assert.Equal(Table, update.EntityName);
        Assert.Equal("TestSolution", update.SolutionUniqueName);
        Assert.True(update.MergeLabels);
        Assert.NotSame(_attribute, update.Attribute);
        Assert.IsType<AttributeMetadata>(update.Attribute);

        // Compare the serialized SDK contract with just the two identifiers and requested LCID 1033 labels.
        // The retrieved object also carries a metadata ID, required level, managed properties and translations.
        var expected = new AttributeMetadata { LogicalName = Key, SchemaName = "new_RecordKey" };
        if (displayName != null) expected.DisplayName = new Label(displayName, 1033);
        if (description != null) expected.Description = new Label(description, 1033);
        Assert.True(XNode.DeepEquals(Serialize(expected), Serialize(update.Attribute)));
        Assert.Equal(2, _attribute.DisplayName.LocalizedLabels.Count);
        Assert.Equal("Old English", _attribute.DisplayName.LocalizedLabels[0].Label);
        _cache.Verify(c => c.InvalidateEntity(Table), Times.Once);
        if (publish) Assert.Contains($"<entity>{Table}</entity>", Assert.IsType<PublishXmlRequest>(_requests[3]).ParameterXml);
    }

    [Theory]
    [InlineData("nonprimary-guid")]
    [InlineData("unknown-primary")]
    [InlineData("entity-name")]
    [InlineData("unknown-type")]
    [InlineData("state")]
    [InlineData("status")]
    public async Task UnsupportedShape_RemainsRejected(string shape)
    {
        _attribute = shape switch
        {
            "nonprimary-guid" => NewCandidate(primary: false),
            "unknown-primary" => NewCandidate(primary: null),
            "entity-name" => NewCandidate(type: AttributeTypeCode.EntityName),
            "unknown-type" => NewCandidate(type: null),
            "state" => new StateAttributeMetadata(),
            "status" => new StatusAttributeMetadata(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => UpdateAsync(NewRequest("New")));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, error.ErrorCode);
        Assert.IsType<RetrieveAttributeRequest>(Assert.Single(_requests));
        _cache.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("address-guid")]
    [InlineData("missing-name")]
    [InlineData("missing-definition")]
    [InlineData("wrong-retrieved-column")]
    [InlineData("wrong-requested-column")]
    public async Task UnconfirmedPrimaryId_RejectsBeforeWrite(string shape)
    {
        var request = NewRequest("New", publish: true);
        switch (shape)
        {
            case "address-guid":
                _attribute.LogicalName = "address1_addressid";
                request.ColumnLogicalName = _attribute.LogicalName;
                break;
            case "missing-name": _entity = NewEntity(null); break;
            case "missing-definition": _entity = null; break;
            case "wrong-retrieved-column": _attribute.LogicalName = "unexpected"; break;
            case "wrong-requested-column": request.ColumnLogicalName = "unrelatedid"; break;
        }
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => UpdateAsync(request));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, error.ErrorCode);
        Assert.Contains("primary ID", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "RetrieveAttribute", "RetrieveEntity" }, _requests.Select(r => r.RequestName));
        _cache.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(nameof(UpdateColumnRequest.RequiredLevel), "")]
    [InlineData(nameof(UpdateColumnRequest.MaxLength), 0)]
    [InlineData(nameof(UpdateColumnRequest.MinValue), 0d)]
    [InlineData(nameof(UpdateColumnRequest.MaxValue), 0d)]
    [InlineData(nameof(UpdateColumnRequest.Precision), 0)]
    [InlineData(nameof(UpdateColumnRequest.Format), "")]
    [InlineData(nameof(UpdateColumnRequest.IsAuditEnabled), false)]
    [InlineData(nameof(UpdateColumnRequest.IsSecured), false)]
    [InlineData(nameof(UpdateColumnRequest.IsValidForAdvancedFind), false)]
    [InlineData(nameof(UpdateColumnRequest.AutoNumberFormat), "")]
    public async Task IncompatibleSettings_RejectBeforeTableLookup(string field, object suppliedValue)
    {
        var request = NewRequest("New", publish: true);
        typeof(UpdateColumnRequest).GetProperty(field)!.SetValue(request, suppliedValue);
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => UpdateAsync(request));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, error.ErrorCode);
        Assert.Contains("display name", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("description", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<RetrieveAttributeRequest>(Assert.Single(_requests));
        _cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NoLabels_RejectsBeforeTableLookup()
    {
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => UpdateAsync(NewRequest(publish: true)));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, error.ErrorCode);
        Assert.Contains("display name", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<RetrieveAttributeRequest>(Assert.Single(_requests));
        _cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DryRun_ValidatesInputsWithoutMetadata()
    {
        var request = NewRequest("New", publish: true);
        request.DryRun = true;
        request.MaxLength = 0; // The target's type is unknown in dry-run.
        await UpdateAsync(request);
        request.SolutionUniqueName = " ";
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => UpdateAsync(request));
        Assert.Equal(MetadataErrorCodes.MissingRequiredField, error.ErrorCode);
        _pool.VerifyNoOtherCalls();
        _cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TypedUpdate_PreservesRequestShape()
    {
        _attribute = new StringAttributeMetadata { LogicalName = Key, SchemaName = "new_RecordKey", MaxLength = 50 };
        var request = NewRequest("New");
        request.MaxLength = 90;
        await UpdateAsync(request);
        Assert.Equal(new[] { "RetrieveAttribute", "UpdateAttribute" }, _requests.Select(r => r.RequestName));
        var update = Assert.IsType<UpdateAttributeRequest>(_requests[1]);
        Assert.NotSame(_attribute, update.Attribute);
        Assert.Equal(90, Assert.IsType<StringAttributeMetadata>(update.Attribute).MaxLength);
        Assert.Equal(new UpdateAttributeRequest().MergeLabels, update.MergeLabels);
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("coded")]
    [InlineData("cancel")]
    public async Task TableLookupFailure_StopsBeforeWrite(string kind)
    {
        _lookupFault = kind switch
        {
            "cancel" => new OperationCanceledException(_cancellation.Token),
            "coded" => new PpdsException("Test.Lookup", "Lookup failed."),
            _ => new InvalidOperationException("Synthetic table metadata error.")
        };
        var error = await Record.ExceptionAsync(() => UpdateAsync(NewRequest("New", publish: true)));
        if (kind == "sdk")
        {
            var wrapper = Assert.IsType<PpdsException>(error);
            Assert.Equal(MetadataErrorCodes.SdkOperationFailed, wrapper.ErrorCode);
            Assert.Same(_lookupFault, wrapper.InnerException);
        }
        else Assert.Same(_lookupFault, error);
        Assert.Equal(new[] { "RetrieveAttribute", "RetrieveEntity" }, _requests.Select(r => r.RequestName));
        _cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateFailure_SkipsPublishAndCacheInvalidation()
    {
        _updateFault = new PpdsException(MetadataErrorCodes.SdkOperationFailed, "Synthetic update failure.");
        var failure = await Assert.ThrowsAsync<PpdsException>(() => UpdateAsync(NewRequest("New", publish: true)));
        Assert.Same(_updateFault, failure);
        Assert.Equal(new[] { "RetrieveAttribute", "RetrieveEntity", "UpdateAttribute" }, _requests.Select(r => r.RequestName));
        _cache.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("attribute")]
    [InlineData("column")]
    public void CliUpdate_AcceptsBothLabelFlags(string noun)
    {
        var root = MetadataCommandGroup.Create();
        var parsed = root.Parse([noun, "update", "--solution", "TestSolution", "--entity", Table,
            "--column", Key, "--display-name", "New label", "--description", "New description"]);
        Assert.Empty(parsed.Errors);
        var update = parsed.CommandResult.Command;
        Assert.Equal("update", update.Name);
        Assert.Equal("New label", parsed.GetValue((Option<string?>)update.Options.Single(o => o.Name == "--display-name")));
        Assert.Equal("New description", parsed.GetValue((Option<string?>)update.Options.Single(o => o.Name == "--description")));
    }

    private Task UpdateAsync(UpdateColumnRequest request) => _service.UpdateColumnAsync(request, ct: _cancellation.Token);

    private static UpdateColumnRequest NewRequest(string? displayName = null, string? description = null, bool publish = false) => new()
    {
        EntityLogicalName = Table, ColumnLogicalName = Key, SolutionUniqueName = "TestSolution",
        DisplayName = displayName, Description = description, Publish = publish
    };

    private static AttributeMetadata NewCandidate(bool? primary = true, AttributeTypeCode? type = AttributeTypeCode.Uniqueidentifier)
    {
        var attribute = new AttributeMetadata
        {
            LogicalName = Key, SchemaName = "new_RecordKey", MetadataId = Guid.NewGuid(),
            DisplayName = new Label("Old English", 1033), Description = new Label("Old description", 1033),
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired),
            IsCustomizable = new BooleanManagedProperty(true), IsRenameable = new BooleanManagedProperty(true)
        };
        attribute.DisplayName.LocalizedLabels.Add(new LocalizedLabel("Ancien", 1036));
        // SDK exposes server-computed metadata with non-public setters.
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.IsPrimaryId))!.SetValue(attribute, primary);
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!.SetValue(attribute, type);
        return attribute;
    }

    private static EntityMetadata NewEntity(string? primaryName)
    {
        var entity = new EntityMetadata { LogicalName = Table };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!.SetValue(entity, primaryName);
        return entity;
    }

    private static XElement Serialize(AttributeMetadata metadata)
    {
        using var stream = new MemoryStream();
        new DataContractSerializer(typeof(AttributeMetadata)).WriteObject(stream, metadata);
        stream.Position = 0;
        return XElement.Load(stream);
    }

    public void Dispose()
    {
        _slot.Dispose();
        _cancellation.Dispose();
    }
}
