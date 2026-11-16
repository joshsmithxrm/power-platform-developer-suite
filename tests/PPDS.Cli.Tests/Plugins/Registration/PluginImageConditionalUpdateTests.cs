using System.ServiceModel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

// Models the request boundary, not Dataverse persistence or universal server compatibility.
public sealed class PluginImageConditionalUpdateTests
{
    private readonly Entity _snapshot = PluginImageUpdateTestContext.Snapshot(Guid.NewGuid());
    private readonly EntityMetadata _metadata = PluginImageUpdateTestContext.Metadata();
    private readonly List<Mock<IPooledClient>> _leases = [];
    private readonly List<DataverseClientOptions?> _options = [];
    private readonly List<UpdateRequest> _updates = [];
    private readonly List<CancellationToken> _tokens = [];
    private readonly PluginRegistrationService _service;
    private Func<UpdateRequest, OrganizationResponse> _dispatch = _ => new UpdateResponse();
    private Exception? _readFailure;
    private Exception? _disposalFailure;
    private int _disposalFailureLease = 4;
    private Exception? _writerAcquireFailure;
    private ColumnSet? _columns;

    public PluginImageConditionalUpdateTests()
    {
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DataverseClientOptions? options, string? _, CancellationToken token) =>
            {
                // Enforce one operation per lease and disposal before the next checkout.
                if (_leases.Count > 0) _leases[^1].Verify(c => c.DisposeAsync(), Times.Once);
                if (_leases.Count == 3 && _writerAcquireFailure is not null) throw _writerAcquireFailure;
                _options.Add(options); _tokens.Add(token);
                var client = new Mock<IPooledClient>(MockBehavior.Strict);
                var leaseNumber = _leases.Count + 1;
                var calls = 0;
                client.Setup(c => c.DisposeAsync()).Returns(() => leaseNumber == _disposalFailureLease && _disposalFailure is not null
                    ? ValueTask.FromException(_disposalFailure) : ValueTask.CompletedTask);
                client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((QueryBase query, CancellationToken ct) =>
                    {
                        Assert.Equal(1, ++calls); _tokens.Add(ct);
                        var actual = Assert.IsType<QueryExpression>(query);
                        Assert.Equal(PluginImageUpdateTestContext.Table, actual.EntityName);
                        return new EntityCollection([new Entity(_snapshot.LogicalName, _snapshot.Id)
                        {
                            ["name"] = "Earlier name", ["ismanaged"] = true,
                            ["iscustomizable"] = new BooleanManagedProperty(false)
                        }]);
                    });
                client.Setup(c => c.RetrieveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<ColumnSet>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string table, Guid id, ColumnSet columns, CancellationToken ct) =>
                    {
                        Assert.Equal(1, ++calls); _tokens.Add(ct);
                        Assert.Equal(PluginImageUpdateTestContext.Table, table);
                        _columns = columns;
                        if (_readFailure is not null) throw _readFailure;
                        return _snapshot;
                    });
                client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((OrganizationRequest request, CancellationToken ct) =>
                    {
                        Assert.Equal(1, ++calls); _tokens.Add(ct);
                        if (request is RetrieveEntityRequest metadataRequest)
                        {
                            Assert.Equal(PluginImageUpdateTestContext.Table, metadataRequest.LogicalName);
                            Assert.Equal(EntityFilters.Entity | EntityFilters.Attributes, metadataRequest.EntityFilters);
                            return PluginImageUpdateTestContext.Response(_metadata);
                        }
                        var update = Assert.IsType<UpdateRequest>(request);
                        _updates.Add(update);
                        return _dispatch(update);
                    });
                _leases.Add(client);
                return client.Object;
            });
        _service = new PluginRegistrationService(pool.Object, new InactiveFakeShakedownGuard(), NullLogger<PluginRegistrationService>.Instance);
    }

    [Fact]
    public async Task DescriptionOnly_PreservesFreshContextAndGenuineVersion_WithOneConditionalDispatch()
    {
        await _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest { Description = "Changed" });
        var update = Assert.Single(_updates);
        Assert.Equal(ConcurrencyBehavior.IfRowVersionMatches, update.ConcurrencyBehavior);
        Assert.Equal("101", update.Target.RowVersion);
        Assert.Equal(_snapshot.Id, update.Target.Id);
        Assert.Equal(PluginImageUpdateTestContext.Table, update.Target.LogicalName);
        Assert.Equal(PluginImageUpdateTestContext.Fields.Order(), update.Target.Attributes.Keys.Order());
        foreach (var field in PluginImageUpdateTestContext.Fields.Where(f => f != "description"))
            Assert.Equal(_snapshot[field], update.Target[field]);
        Assert.Equal("Changed", update.Target["description"]);
        Assert.Equal("Existing description", _snapshot["description"]); // Never mutate the retrieved object.
        Assert.Equal(PluginImageUpdateTestContext.Fields.Append("versionnumber").Order(), _columns!.Columns.Order());
        Assert.Equal(4, _leases.Count);
        Assert.All(_options.Take(3), Assert.Null);
        Assert.Equal(0, _options[3]!.MaxRetryCount);
        Assert.All(_leases, client => client.Verify(c => c.DisposeAsync(), Times.Once));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("value")]
    public async Task OmittedNullableValues_PreserveExactSnapshot(string kind)
    {
        foreach (var field in new[] { "attributes", "description" })
        {
            if (kind == "missing") _snapshot.Attributes.Remove(field);
            else _snapshot[field] = kind switch { "null" => null, "empty" => "", _ => "Existing" };
        }
        await _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "Renamed"));
        var target = Assert.Single(_updates).Target;
        Assert.Equal(kind switch { "empty" => "", "value" => "Existing", _ => null }, target["attributes"]);
        Assert.Equal(target["attributes"], target["description"]);
    }

    [Fact]
    public async Task ExplicitEmptyAndReset_KeepDistinctMeaningAndPreserveOtherFields()
    {
        await _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Attributes: "") { ResetFields = ["description"] });
        var target = Assert.Single(_updates).Target;
        Assert.Equal("", target["attributes"]); Assert.Null(target["description"]);
        Assert.Equal(_snapshot["entityalias"], target["entityalias"]);
        Assert.Equal(_snapshot["sdkmessageprocessingstepid"], target["sdkmessageprocessingstepid"]);
    }

    [Theory]
    [InlineData("PreImage", 0)]
    [InlineData("post", 1)]
    [InlineData("2", 2)]
    public async Task ExplicitEdits_NormalizeTypeAndAttributes_PreserveParent(string type, int expected)
    {
        await _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest("name,accountnumber,name", "Renamed")
        { ImageType = type, EntityAlias = "ChangedAlias", MessagePropertyName = "ChangedTarget" });
        var target = Assert.Single(_updates).Target;
        Assert.Equal(expected, target.GetAttributeValue<OptionSetValue>("imagetype").Value);
        Assert.Equal("accountnumber,name", target["attributes"]);
        Assert.Equal("Renamed", target["name"]);
        Assert.Equal("ChangedAlias", target["entityalias"]);
        Assert.Equal("ChangedTarget", target["messagepropertyname"]);
        Assert.Equal(_snapshot["sdkmessageprocessingstepid"], target["sdkmessageprocessingstepid"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task ConcurrencyCapabilityUnavailable_RefusesBeforeSnapshotAndMutation(bool? supported)
    {
        typeof(EntityMetadata).GetProperty("IsOptimisticConcurrencyEnabled")!.SetValue(_metadata, supported);
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New")));
        Assert.Equal(ErrorCodes.Plugin.FieldCapabilityUnknown, error.ErrorCode);
        Assert.Equal(2, _leases.Count); Assert.Empty(_updates);
    }

    [Theory]
    [InlineData("IsValidForRead", null)]
    [InlineData("IsValidForRead", false)]
    [InlineData("IsValidForUpdate", null)]
    [InlineData("IsValidForUpdate", false)]
    public async Task FieldCapabilityUnavailable_RefusesBeforeSnapshotAndMutation(string property, bool? allowed)
    {
        typeof(AttributeMetadata).GetProperty(property)!.SetValue(_metadata.Attributes.Single(a => a.LogicalName == "entityalias"), allowed);
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New")));
        Assert.Equal(property == "IsValidForUpdate" && allowed == false ? ErrorCodes.Plugin.FieldNotWritable : ErrorCodes.Plugin.FieldCapabilityUnknown, error.ErrorCode);
        Assert.Equal(2, _leases.Count); Assert.Empty(_updates);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("whitespace-version")]
    [InlineData("table")]
    [InlineData("name")]
    [InlineData("alias")]
    [InlineData("property")]
    [InlineData("type")]
    [InlineData("parent-table")]
    [InlineData("parent-id")]
    [InlineData("description-type")]
    [InlineData("attributes-type")]
    public async Task IncompleteSnapshot_RefusesWithoutMutationOrTokenFabrication(string defect)
    {
        switch (defect)
        {
            case "version": _snapshot.RowVersion = null; _snapshot["versionnumber"] = 123L; break;
            case "whitespace-version": _snapshot.RowVersion = " "; break;
            case "table": _snapshot.LogicalName = "account"; break;
            case "name": _snapshot.Attributes.Remove("name"); break;
            case "alias": _snapshot["entityalias"] = ""; break;
            case "property": _snapshot["messagepropertyname"] = null; break;
            case "type": _snapshot["imagetype"] = new OptionSetValue(9); break;
            case "parent-table": _snapshot["sdkmessageprocessingstepid"] = new EntityReference("account", Guid.NewGuid()); break;
            case "parent-id": _snapshot["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", Guid.Empty); break;
            case "description-type": _snapshot["description"] = 7; break;
            case "attributes-type": _snapshot["attributes"] = false; break;
        }
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New")));
        Assert.Equal(ErrorCodes.Plugin.FieldCapabilityUnknown, error.ErrorCode);
        Assert.Empty(_updates); Assert.Equal(3, _leases.Count);
    }

    [Fact]
    public async Task WrongSnapshotIdentity_RefusesBeforeMutation()
    {
        var requestedId = Guid.NewGuid();
        await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(requestedId, new ImageUpdateRequest(Name: "New")));
        Assert.Empty(_updates);
    }

    [Fact]
    public async Task UnexpectedResponse_DoesNotClaimAcknowledgementOrRetry()
    {
        _dispatch = _ => new OrganizationResponse();
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New")));
        Assert.Equal(ErrorCodes.Plugin.VerificationFailed, error.ErrorCode);
        Assert.Contains("uncertain", error.Message);
        Assert.Single(_updates);
    }

    [Fact]
    public async Task ConcurrentDrift_IsRejectedOnce_WithoutRefreshOrFallback()
    {
        var server = PluginImageUpdateTestContext.Snapshot(_snapshot.Id);
        server.RowVersion = "102"; server["description"] = "Concurrent edit";
        var fault = new FaultException<OrganizationServiceFault>(new() { ErrorCode = -2147088254 });
        _dispatch = request =>
        {
            Assert.NotEqual(server.RowVersion, request.Target.RowVersion);
            Assert.Equal(ConcurrencyBehavior.IfRowVersionMatches, request.ConcurrencyBehavior);
            throw fault;
        };
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest { Description = "Requested" }));
        Assert.Equal(ErrorCodes.Plugin.ImageUpdateConflict, error.ErrorCode);
        Assert.Same(fault, error.InnerException);
        Assert.Single(_updates); Assert.Equal(4, _leases.Count);
        Assert.Equal("Concurrent edit", server["description"]);
    }

    [Theory]
    [InlineData(-2147088253)]
    [InlineData(-2147088243)]
    [InlineData(-2147220970)]
    public async Task DispatchFault_IsCodedAndNeverRetried(int code)
    {
        var fault = new FaultException<OrganizationServiceFault>(new() { ErrorCode = code, Message = "PRIVATE-SDK-DETAIL" });
        _dispatch = _ => throw fault;
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New")));
        Assert.Same(fault, error.InnerException);
        Assert.DoesNotContain("PRIVATE-SDK-DETAIL", error.Message);
        Assert.NotEqual(ErrorCodes.Plugin.ImageUpdateConflict, error.ErrorCode);
        Assert.Single(_updates); Assert.Equal(4, _leases.Count);
    }

    [Fact]
    public async Task NestedMismatch_IsNotMisclassifiedAsKnownConflict()
    {
        _dispatch = _ => throw new FaultException<OrganizationServiceFault>(new() { ErrorCode = -2147220970, InnerFault = new() { ErrorCode = -2147088254 } });
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New")));
        Assert.NotEqual(ErrorCodes.Plugin.ImageUpdateConflict, error.ErrorCode); Assert.Single(_updates);
    }

    [Fact]
    public async Task Cancellation_PreservesTokenAndDisposesLease_WithoutMutation()
    {
        using var source = new CancellationTokenSource();
        var cancellation = new OperationCanceledException(source.Token);
        _readFailure = cancellation;
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New"), source.Token));
        Assert.Same(cancellation, error);
        Assert.All(_tokens, token => Assert.Equal(source.Token, token));
        Assert.All(_leases, client => client.Verify(c => c.DisposeAsync(), Times.Once));
        Assert.Empty(_updates);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("snapshot-disposal")]
    [InlineData("acquire")]
    public async Task PreparationFailure_IsNeverReportedAsARejectedOrUncertainMutation(string phase)
    {
        var failure = new FaultException<OrganizationServiceFault>(new() { ErrorCode = -2147088254 });
        if (phase == "snapshot") _readFailure = failure;
        if (phase == "snapshot-disposal") { _disposalFailure = failure; _disposalFailureLease = 3; }
        if (phase == "acquire") _writerAcquireFailure = failure;
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New")));
        Assert.NotEqual(ErrorCodes.Plugin.ImageUpdateConflict, error.ErrorCode);
        Assert.Contains("No image update was dispatched", error.Message);
        Assert.Same(failure, error.InnerException); Assert.Empty(_updates);
    }

    [Fact]
    public async Task DisposalFailureAfterAcknowledgement_PreservesAcknowledgedOutcome()
    {
        _disposalFailure = new FaultException<OrganizationServiceFault>(new() { ErrorCode = -2147088254 });
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New")));
        Assert.Equal(ErrorCodes.Operation.PartialFailure, error.ErrorCode);
        Assert.Contains("acknowledged", error.Message);
        Assert.DoesNotContain("rejected", error.Message); Assert.DoesNotContain("uncertain", error.Message);
        Assert.Same(_disposalFailure, error.InnerException); Assert.Single(_updates);
    }

    [Theory]
    [InlineData(-2147088254)]
    [InlineData(-2147220970)]
    public async Task DisposalFailureWhileUnwinding_PreservesActualDispatchFault(int code)
    {
        var fault = new FaultException<OrganizationServiceFault>(new() { ErrorCode = code });
        _dispatch = _ => throw fault;
        _disposalFailure = new InvalidOperationException("PRIVATE-CLEANUP");
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New")));
        Assert.Equal(code == -2147088254, error.ErrorCode == ErrorCodes.Plugin.ImageUpdateConflict);
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(new[] { fault, _disposalFailure }, aggregate.InnerExceptions);
        Assert.DoesNotContain("PRIVATE-CLEANUP", error.Message); Assert.Single(_updates);
    }

    [Fact]
    public async Task DisposalFailureAfterCancelledDispatch_PreservesOriginalCancellationAndToken()
    {
        using var source = new CancellationTokenSource();
        var cancellation = new OperationCanceledException(source.Token);
        _dispatch = _ => throw cancellation;
        _disposalFailure = new InvalidOperationException("PRIVATE-CLEANUP");
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New"), source.Token));
        Assert.Same(cancellation, error); Assert.Equal(source.Token, error.CancellationToken);
        Assert.Same(_disposalFailure, error.Data["ImageUpdateClientDisposalFailure"]);
        Assert.All(_tokens, token => Assert.Equal(source.Token, token)); Assert.Single(_updates);
    }

    [Fact]
    public async Task DisposalFailureWithoutAcknowledgement_PreservesUncertainOutcome()
    {
        _dispatch = _ => new OrganizationResponse();
        _disposalFailure = new FaultException<OrganizationServiceFault>(new() { ErrorCode = -2147088254 });
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest(Name: "New")));
        Assert.Equal(ErrorCodes.Plugin.VerificationFailed, error.ErrorCode);
        Assert.Contains("uncertain", error.Message);
        Assert.Same(_disposalFailure, error.InnerException); Assert.Single(_updates);
    }

    [Fact]
    public async Task EmptyPatchAndLegacyEmptyAlias_OnlyCheckExistence()
    {
        await _service.UpdateImageAsync(_snapshot.Id, new ImageUpdateRequest { EntityAlias = "", MessagePropertyName = "" });
        Assert.Single(_leases); Assert.Empty(_updates);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("alias")]
    [InlineData("reset")]
    public async Task InvalidRequest_RefusesBeforeNewReadsAndMutation(string field)
    {
        var request = field switch
        {
            "name" => new ImageUpdateRequest(Name: " "),
            "type" => new ImageUpdateRequest { ImageType = "invalid" },
            "alias" => new ImageUpdateRequest { EntityAlias = " " },
            _ => new ImageUpdateRequest { ResetFields = ["entityAlias"] }
        };
        await Assert.ThrowsAsync<PpdsException>(() => _service.UpdateImageAsync(_snapshot.Id, request));
        Assert.Single(_leases); Assert.Empty(_updates);
    }
}
