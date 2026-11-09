using Microsoft.Crm.Sdk.Messages;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using DeleteRelationshipModel = PPDS.Dataverse.Metadata.Authoring.DeleteRelationshipRequest;
using DeleteKeyModel = PPDS.Dataverse.Metadata.Authoring.DeleteKeyRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
public sealed class MetadataDeleteDependencyTests
{
    [Theory]
    [InlineData("table", 1)]
    [InlineData("column", 2)]
    [InlineData("relationship", 10)]
    [InlineData("many-to-many", 10)]
    [InlineData("key", 14)]
    [InlineData("choice", 9)]
    [Trait("AC", "AC-114")]
    [Trait("AC", "AC-115")]
    public async Task DryRun_ResolvesUnpublishedIdentityAndRequestsDependencies(string family, int type)
    {
        using var f = new Fixture(family);
        await f.Run();
        var dependency = Assert.Single(f.Requests.OfType<RetrieveDependenciesForDeleteRequest>());
        Assert.Equal(type, dependency.ComponentType); Assert.Equal(Fixture.TargetId, dependency.ObjectId);
        Assert.Equal(2, f.Requests.Count);
        f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("table")]
    [InlineData("column")]
    [InlineData("relationship")]
    [InlineData("many-to-many")]
    [InlineData("key")]
    [InlineData("choice")]
    [Trait("AC", "AC-115")]
    [Trait("AC", "AC-116")]
    public async Task Blockers_RejectWithoutDiscardingUnknownTypeOrObjectId(string family)
    {
        using var f = new Fixture(family) { Blockers = true };
        var ex = await Assert.ThrowsAnyAsync<PpdsException>(() => f.Run());
        Assert.Equal(MetadataErrorCodes.DependencyConflict, ex.ErrorCode);
        Assert.NotNull(ex.Context);
        Assert.True(ex.Context.TryGetValue("preview", out var preview));
        var payload = JsonSerializer.Serialize(preview);
        Assert.Contains(Fixture.ViewId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Fixture.UnknownId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("90000", payload);
        f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("column")]
    [InlineData("choice")]
    [Trait("AC", "AC-116")]
    public async Task NameReadFailure_KeepsKnownBlockersWithoutEchoingDiagnostics(string family)
    {
        using var f = new Fixture(family) { Blockers = true, FailName = true };
        var ex = await Assert.ThrowsAnyAsync<PpdsException>(() => f.Run());
        Assert.Equal(MetadataErrorCodes.DependencyConflict, ex.ErrorCode);
        Assert.Contains("name", f.Stages);
        var json = JsonSerializer.Serialize(ex.Context);
        Assert.Contains(Fixture.ViewId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Fixture.UnknownId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SyntheticClientSecret", json + ex.UserMessage);
        f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("table", "missing-id")]
    [InlineData("column", "missing-id")]
    [InlineData("relationship", "missing-id")]
    [InlineData("key", "missing-id")]
    [InlineData("choice", "missing-id")]
    [InlineData("table", "wrong-name")]
    [InlineData("column", "wrong-name")]
    [InlineData("relationship", "wrong-name")]
    [InlineData("key", "wrong-name")]
    [InlineData("choice", "wrong-name")]
    [Trait("AC", "AC-117")]
    public async Task InvalidIdentity_FailsBeforeDependencyRead(string family, string shape)
    {
        using var f = new Fixture(family) { IdentityShape = shape };
        await Assert.ThrowsAnyAsync<PpdsException>(() => f.Run());
        Assert.Single(f.Requests); Assert.DoesNotContain(f.Requests, r => r is RetrieveDependenciesForDeleteRequest);
        f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("table", "null-collection")]
    [InlineData("column", "missing-object")]
    [InlineData("relationship", "missing-type")]
    [InlineData("key", "more-records")]
    [InlineData("choice", "wrong-response")]
    [Trait("AC", "AC-117")]
    public async Task IncompleteDependencies_CannotBecomeEmptySuccess(string family, string shape)
    {
        using var f = new Fixture(family) { DependencyShape = shape };
        await Assert.ThrowsAnyAsync<PpdsException>(() => f.Run());
        Assert.Single(f.Requests.OfType<RetrieveDependenciesForDeleteRequest>());
        f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("table", "identity")]
    [InlineData("column", "identity")]
    [InlineData("relationship", "dependency")]
    [InlineData("many-to-many", "dependency")]
    [InlineData("key", "dependency")]
    [InlineData("choice", "name")]
    [Trait("AC", "AC-118")]
    public async Task NewPreflightStageCancellation_PreservesTokenIdentityAndStops(string family, string stage)
    {
        // These are added-contract negative baselines on B3's zero-read dry-run,
        // not a claim that B3 previously performed and mishandled these reads.
        using var f = new Fixture(family) { Blockers = stage == "name", CancelStage = stage };
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run());
        Assert.Same(f.Cancellation, ex); Assert.Equal(f.Token, ex.CancellationToken);
        Assert.Equal(stage, f.Stages.Last());
        f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("table")]
    [InlineData("column")]
    [InlineData("relationship")]
    [InlineData("key")]
    [InlineData("choice")]
    [Trait("AC", "AC-118")]
    public async Task DependencySdkFault_KeepsCauseAndFixedMessage(string family)
    {
        using var f = new Fixture(family) { FailDependency = true };
        var ex = await Assert.ThrowsAnyAsync<PpdsException>(() => f.Run());
        Assert.Same(f.SdkFailure, ex.InnerException);
        Assert.DoesNotContain("SyntheticClientSecret", ex.UserMessage);
        f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("table", false)]
    [InlineData("column", false)]
    [InlineData("relationship", false)]
    [InlineData("key", false)]
    [InlineData("choice", false)]
    [InlineData("table", true)]
    [InlineData("column", true)]
    [InlineData("relationship", true)]
    [InlineData("key", true)]
    [InlineData("choice", true)]
    [Trait("AC", "AC-119")]
    public async Task ExistingInputAndSafetyRefusals_PrecedeAnyCheckout(string family, bool safety)
    {
        using var f = new Fixture(family, safety);
        if (safety) await Assert.ThrowsAnyAsync<PpdsException>(() => f.Run());
        else await Assert.ThrowsAsync<MetadataValidationException>(() => f.Run(blankSolution: true));
        Assert.Empty(f.Requests); Assert.Equal(0, f.Acquired); f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("table")]
    [InlineData("column")]
    [InlineData("relationship")]
    [InlineData("key")]
    [InlineData("choice")]
    [Trait("AC", "AC-119")]
    public async Task ActualDelete_ReadsBeforeExactlyOneOriginalDelete(string family)
    {
        using var f = new Fixture(family) { AllowDelete = true };
        await f.Run(dryRun: false);
        Assert.Equal(new[] { "identity", "dependency", "delete" }, f.Stages);
        Assert.Single(f.Requests.Where(r => r.RequestName.StartsWith("Delete", StringComparison.Ordinal)));
        f.AssertReturned(); f.AssertMutationCache();
    }

    [Theory]
    [InlineData("table")]
    [InlineData("column")]
    [InlineData("relationship")]
    [InlineData("key")]
    [InlineData("choice")]
    [Trait("AC", "AC-119")]
    public async Task PreviewThenActual_RechecksAndRefusesNewBlockers(string family)
    {
        using var f = new Fixture(family) { AllowDelete = true, BlockersFromDependencyRead = 2 };
        await f.Run();
        var ex = await Assert.ThrowsAnyAsync<PpdsException>(() => f.Run(dryRun: false));
        Assert.Equal(MetadataErrorCodes.DependencyConflict, ex.ErrorCode);
        Assert.Equal(2, f.Requests.OfType<RetrieveDependenciesForDeleteRequest>().Count());
        f.AssertReadsAndReturned();
    }

    [Fact]
    [Trait("AC", "AC-121")]
    public void ExistingTaskDeleteContracts_RetainOptionalPreviewCapability()
    {
        var service = typeof(DataverseMetadataAuthoringService);
        Assert.NotNull(service.GetInterface("IMetadataDeletePreviewService"));
        foreach (var name in new[] { "DeleteTableAsync", "DeleteColumnAsync", "DeleteRelationshipAsync", "DeleteKeyAsync", "DeleteGlobalChoiceAsync" })
        {
            var method = typeof(IMetadataAuthoringService).GetMethod(name)!;
            Assert.Equal(typeof(Task), method.ReturnType);
            Assert.Equal(3, method.GetParameters().Length);
        }
        Assert.Contains(service.GetConstructors(), ctor => ctor.GetParameters().Length == 5);
    }

    internal sealed class Fixture : IDisposable
    {
        internal static readonly Guid TargetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        internal static readonly Guid ViewId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        internal static readonly Guid UnknownId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        private readonly string _family;
        private readonly CancellationTokenSource _cts = new();
        private CancellationToken _operationToken;
        internal bool OperationTokenWasBound { get; private set; }
        internal int TokenMismatches { get; private set; }
        internal int ServiceEntries { get; private set; }
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly Mock<ICachedMetadataProvider> _cache = new(MockBehavior.Strict);
        private readonly List<int> _operations = [], _returned = [];
        private int _active, _dependencyReads;
        internal int Acquired { get; private set; }
        internal List<OrganizationRequest> Requests { get; } = [];
        internal List<string> Stages { get; } = [];
        internal bool Blockers { get; init; }
        internal string? IdentityShape { get; init; }
        internal string? DependencyShape { get; init; }
        internal string? CancelStage { get; init; }
        internal bool FailDependency { get; init; }
        internal bool FailName { get; init; }
        internal bool AllowDelete { get; init; }
        internal int BlockersFromDependencyRead { get; init; }
        internal CancellationToken Token => _operationToken;
        internal OperationCanceledException Cancellation { get; private set; }
        internal Exception SdkFailure { get; } = new InvalidOperationException("ClientSecret=SyntheticClientSecret; fake provider diagnostic");
        internal DataverseMetadataAuthoringService Service { get; }

        internal Fixture(string family, bool safety = false)
        {
            _family = family; _operationToken = _cts.Token; Cancellation = new OperationCanceledException(Token);
            var guard = new Mock<IShakedownGuard>(MockBehavior.Strict);
            var setup = guard.Setup(g => g.EnsureCanMutate(It.IsAny<string>()));
            setup.Callback(() => ServiceEntries++);
            if (safety) setup.Throws(new PpdsException(ErrorCodes.Safety.ShakedownActive, "Synthetic read-only guard"));
            _cache.Setup(c => c.InvalidateEntityList());
            _cache.Setup(c => c.InvalidateEntity("new_table"));
            _cache.Setup(c => c.InvalidateAll());
            _cache.Setup(c => c.InvalidateGlobalOptionSets());
            _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
                .Returns<DataverseClientOptions?, string?, CancellationToken>((_, _, ct) =>
                {
                    CheckToken(ct); Assert.Equal(0, _active); _active++;
                    var id = ++Acquired; var client = new Mock<IPooledClient>(MockBehavior.Strict);
                    client.Setup(c => c.DisposeAsync()).Callback(() => { _active--; _returned.Add(id); }).Returns(ValueTask.CompletedTask);
                    client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                        .Returns<OrganizationRequest, CancellationToken>((request, token) =>
                        {
                            Requests.Add(request);
                            var stage = request is RetrieveDependenciesForDeleteRequest ? "dependency" : request.RequestName.StartsWith("Delete", StringComparison.Ordinal) ? "delete" : "identity";
                            if (stage == "identity") ValidateIdentityRequest(request);
                            if (request is RetrieveDependenciesForDeleteRequest dependency)
                            {
                                Assert.Equal(TargetId, dependency.ObjectId);
                                Assert.Equal(_family switch { "table" => 1, "column" => 2, "relationship" or "many-to-many" => 10, "key" => 14, "choice" => 9, _ => throw new InvalidOperationException() }, dependency.ComponentType);
                            }
                            Begin(id, stage, token);
                            if (stage == "delete")
                            {
                                Assert.True(AllowDelete);
                                Assert.Equal(_family switch { "table" => "DeleteEntity", "column" => "DeleteAttribute", "relationship" or "many-to-many" => "DeleteRelationship", "key" => "DeleteEntityKey", "choice" => "DeleteOptionSet", _ => throw new InvalidOperationException() }, request.RequestName);
                                switch (request)
                                {
                                    case DeleteEntityRequest r: Assert.Equal("new_table", r.LogicalName); break;
                                    case DeleteAttributeRequest r: Assert.Equal("new_table", r.EntityLogicalName); Assert.Equal("new_column", r.LogicalName); break;
                                    case Microsoft.Xrm.Sdk.Messages.DeleteRelationshipRequest r: Assert.Equal("new_name", r.Name); break;
                                    case DeleteEntityKeyRequest r: Assert.Equal("new_table", r.EntityLogicalName); Assert.Equal("new_name", r.Name); break;
                                    case DeleteOptionSetRequest r: Assert.Equal("new_name", r.Name); break;
                                    default: throw new InvalidOperationException("Unrecognized mutation");
                                }
                                return Task.FromResult(new OrganizationResponse());
                            }
                            if (stage == "dependency")
                            {
                                _dependencyReads++;
                                if (FailDependency) throw SdkFailure;
                                if (DependencyShape == "wrong-response") return Task.FromResult(new OrganizationResponse());
                                var rows = new EntityCollection();
                                var blocked = Blockers || BlockersFromDependencyRead > 0 && _dependencyReads >= BlockersFromDependencyRead;
                                if (blocked || DependencyShape is "missing-object" or "missing-type")
                                {
                                    var view = new Entity("dependency") { ["dependentcomponentobjectid"] = ViewId, ["dependentcomponenttype"] = new OptionSetValue(26) };
                                    if (DependencyShape == "missing-object") view.Attributes.Remove("dependentcomponentobjectid");
                                    if (DependencyShape == "missing-type") view.Attributes.Remove("dependentcomponenttype");
                                    rows.Entities.Add(view);
                                    if (blocked) rows.Entities.Add(new Entity("dependency") { ["dependentcomponentobjectid"] = UnknownId, ["dependentcomponenttype"] = new OptionSetValue(90000) });
                                }
                                rows.MoreRecords = DependencyShape == "more-records";
                                return Task.FromResult<OrganizationResponse>(new RetrieveDependenciesForDeleteResponse { Results = new ParameterCollection { ["EntityCollection"] = DependencyShape == "null-collection" ? null! : rows } });
                            }
                            var metadataId = IdentityShape == "missing-id" ? (Guid?)null : TargetId;
                            var column = IdentityShape == "wrong-name" ? "other" : "new_column";
                            var schema = IdentityShape == "wrong-name" ? "other" : "new_name";
                            OrganizationResponse response = request switch
                            {
                                RetrieveEntityRequest r => Entity(r, metadataId),
                                RetrieveAttributeRequest r => Attribute(r, metadataId, column),
                                RetrieveRelationshipRequest r => Relationship(r, metadataId, schema),
                                RetrieveEntityKeyRequest r => Key(r, metadataId, schema),
                                RetrieveOptionSetRequest r => Choice(r, metadataId, schema),
                                _ => throw new InvalidOperationException("Unexpected SDK mutation/request: " + request.RequestName)
                            };
                            return Task.FromResult(response);
                        });
                    client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                        .Returns<QueryBase, CancellationToken>((query, token) =>
                        {
                            var q = Assert.IsType<QueryExpression>(query);
                            Assert.Equal("savedquery", q.EntityName);
                            Assert.False(q.ColumnSet.AllColumns);
                            Assert.Contains("name", q.ColumnSet.Columns);
                            Assert.All(q.ColumnSet.Columns, column => Assert.Contains(column, new[] { "name", "savedqueryid" }));
                            Assert.Empty(q.LinkEntities);
                            Assert.Empty(q.Criteria.Filters);
                            var predicate = Assert.Single(q.Criteria.Conditions);
                            Assert.Equal("savedqueryid", predicate.AttributeName);
                            Assert.True(predicate.Operator is ConditionOperator.Equal or ConditionOperator.In);
                            Assert.Equal(ViewId, Assert.IsType<Guid>(Assert.Single(predicate.Values)));
                            Begin(id, "name", token); if (FailName) throw SdkFailure;
                            return Task.FromResult(new EntityCollection([new Entity("savedquery", ViewId) { ["name"] = "Blocking view" }]));
                        });
                    return Task.FromResult(client.Object);
                });
            Service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), guard.Object, cacheProvider: _cache.Object);
        }

        internal void BindOperationToken(CancellationToken token)
        {
            Assert.False(OperationTokenWasBound);
            Assert.Equal(0, ServiceEntries); Assert.Equal(0, Acquired); Assert.Equal(0, _active); Assert.Empty(_operations);
            _operationToken = token; Cancellation = new OperationCanceledException(token); OperationTokenWasBound = true;
        }
        private void CheckToken(CancellationToken token)
        {
            if (Token != token) TokenMismatches++;
            Assert.Equal(Token, token);
        }
        private void Begin(int id, string stage, CancellationToken token)
        {
            CheckToken(token); Assert.Equal(1, _active); _operations.Add(id); Stages.Add(stage);
            if (stage == CancelStage) throw Cancellation;
        }
        private static void ValidateIdentityRequest(OrganizationRequest request)
        {
            static void NoConflictingId(object? id) => Assert.True(id == null || id.Equals(Guid.Empty) || id.Equals(TargetId));
            switch (request)
            {
                case RetrieveEntityRequest r:
                    Assert.Equal("new_table", r.LogicalName); Assert.Equal(EntityFilters.Entity, r.EntityFilters);
                    Assert.True(r.RetrieveAsIfPublished); NoConflictingId(r.MetadataId); break;
                case RetrieveAttributeRequest r:
                    Assert.Equal("new_table", r.EntityLogicalName); Assert.Equal("new_column", r.LogicalName);
                    Assert.True(r.RetrieveAsIfPublished); NoConflictingId(r.MetadataId); break;
                case RetrieveRelationshipRequest r:
                    Assert.Equal("new_name", r.Name); Assert.True(r.RetrieveAsIfPublished); NoConflictingId(r.MetadataId); break;
                case RetrieveEntityKeyRequest r:
                    Assert.Equal("new_table", r.EntityLogicalName); Assert.Equal("new_name", r.LogicalName);
                    Assert.True(r.RetrieveAsIfPublished); NoConflictingId(r.MetadataId); break;
                case RetrieveOptionSetRequest r:
                    Assert.Equal("new_name", r.Name); Assert.True(r.RetrieveAsIfPublished); NoConflictingId(r.MetadataId); break;
                default: throw new InvalidOperationException("Unexpected identity lookup: " + request.RequestName);
            }
        }
        private OrganizationResponse Entity(RetrieveEntityRequest r, Guid? id)
        { ValidateIdentityRequest(r); return new RetrieveEntityResponse { Results = new ParameterCollection { ["EntityMetadata"] = new EntityMetadata { MetadataId = id, LogicalName = IdentityShape == "wrong-name" ? "other" : "new_table" } } }; }
        private static OrganizationResponse Attribute(RetrieveAttributeRequest r, Guid? id, string name)
        { ValidateIdentityRequest(r); return new RetrieveAttributeResponse { Results = new ParameterCollection { ["AttributeMetadata"] = new StringAttributeMetadata { MetadataId = id, LogicalName = name } } }; }
        private OrganizationResponse Relationship(RetrieveRelationshipRequest r, Guid? id, string name)
        { ValidateIdentityRequest(r); RelationshipMetadataBase metadata = _family == "many-to-many" ? new ManyToManyRelationshipMetadata() : new OneToManyRelationshipMetadata(); metadata.MetadataId = id; metadata.SchemaName = name; return new RetrieveRelationshipResponse { Results = new ParameterCollection { ["RelationshipMetadata"] = metadata } }; }
        private static OrganizationResponse Key(RetrieveEntityKeyRequest r, Guid? id, string name)
        { ValidateIdentityRequest(r); return new RetrieveEntityKeyResponse { Results = new ParameterCollection { ["EntityKeyMetadata"] = new EntityKeyMetadata { MetadataId = id, LogicalName = name, SchemaName = name } } }; }
        private static OrganizationResponse Choice(RetrieveOptionSetRequest r, Guid? id, string name)
        { ValidateIdentityRequest(r); return new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = new OptionSetMetadata { MetadataId = id, Name = name, IsGlobal = true } } }; }
        internal Task Run(bool blankSolution = false, bool dryRun = true)
        {
            var solution = blankSolution ? " " : "SyntheticSolution";
            return _family switch
            {
                "table" => Service.DeleteTableAsync(new DeleteTableRequest { SolutionUniqueName = solution, EntityLogicalName = "new_table", DryRun = dryRun }, ct: Token),
                "column" => Service.DeleteColumnAsync(new DeleteColumnRequest { SolutionUniqueName = solution, EntityLogicalName = "new_table", ColumnLogicalName = "new_column", DryRun = dryRun }, ct: Token),
                "relationship" or "many-to-many" => Service.DeleteRelationshipAsync(new DeleteRelationshipModel { SolutionUniqueName = solution, SchemaName = "new_name", DryRun = dryRun }, ct: Token),
                "key" => Service.DeleteKeyAsync(new DeleteKeyModel { SolutionUniqueName = solution, EntityLogicalName = "new_table", KeyLogicalName = "new_name", DryRun = dryRun }, ct: Token),
                "choice" => Service.DeleteGlobalChoiceAsync(new DeleteGlobalChoiceRequest { SolutionUniqueName = solution, Name = "new_name", DryRun = dryRun }, ct: Token),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
        internal void AssertReadsAndReturned()
        {
            AssertReturned();
            Assert.All(Requests, r => Assert.StartsWith("Retrieve", r.RequestName)); _cache.VerifyNoOtherCalls();
        }
        internal void AssertReturned()
        {
            Assert.Equal(0, _active); Assert.Equal(Enumerable.Range(1, Acquired), _returned);
            Assert.Equal(Acquired, _operations.Count); Assert.All(_operations.GroupBy(i => i), g => Assert.Single(g));
        }
        internal void AssertMutationCache()
        {
            _cache.Verify(c => c.InvalidateEntityList(), _family == "table" ? Times.Once() : Times.Never());
            _cache.Verify(c => c.InvalidateEntity("new_table"), _family is "table" or "column" or "key" ? Times.Once() : Times.Never());
            _cache.Verify(c => c.InvalidateAll(), _family is "relationship" or "many-to-many" ? Times.Once() : Times.Never());
            _cache.Verify(c => c.InvalidateGlobalOptionSets(), _family == "choice" ? Times.Once() : Times.Never());
            _cache.VerifyNoOtherCalls();
        }
        public void Dispose() => _cts.Dispose();
    }
}
