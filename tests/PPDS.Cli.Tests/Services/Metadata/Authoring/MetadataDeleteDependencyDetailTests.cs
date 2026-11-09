using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
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

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
public sealed class MetadataDeleteDependencyDetailTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(26)]
    [InlineData(70)]
    [InlineData(10377)]
    [Trait("AC", "AC-116")]
    public async Task KnownComponentNames_UseExactTypedIdOrMinimalNameQuery(int type)
    {
        using var f = new Fixture(type); var preview = await f.Preview();
        var row = Assert.Single(preview.BlockingComponents);
        Assert.Equal(type, row.ComponentType); Assert.Equal(Fixture.DependentId, row.ObjectId);
        Assert.Equal("dep_name", row.Name); Assert.Equal("resolved", row.NameResolutionStatus);
        Assert.Equal("blocked", preview.Status); Assert.True(preview.DependenciesChecked); f.Verify();
    }

    [Theory]
    [InlineData(90000)]
    [InlineData(10377)]
    [Trait("AC", "AC-116")]
    public async Task UnknownOrUnverifiedModernType_KeepsIdentityAndNeverQueriesGuessedTable(int type)
    {
        using var f = new Fixture(type, "unverified"); var preview = await f.Preview();
        var row = Assert.Single(preview.BlockingComponents);
        Assert.Equal(type, row.ComponentType); Assert.Equal(Fixture.DependentId, row.ObjectId); Assert.Null(row.Name);
        Assert.Equal("blocked", preview.Status); Assert.NotEmpty(preview.Warnings); Assert.Equal(0, f.NameQueries); f.Verify();
    }

    [Fact]
    [Trait("AC", "AC-116")]
    public async Task DuplicateDependencyEdges_AreRetainedButNamesReadOncePerIdentity()
    {
        using var f = new Fixture(26, "duplicates"); var preview = await f.Preview();
        Assert.Equal(2, preview.DependencyRowCount); Assert.Equal(1, preview.DistinctComponentCount);
        Assert.All(preview.BlockingComponents, row => Assert.Equal("dep_name", row.Name));
        Assert.Equal(1, f.NameQueries); f.Verify();
    }

    [Theory]
    [InlineData("partial-total")]
    [InlineData("partial-more")]
    [InlineData("partial-limit")]
    [Trait("AC", "AC-117")]
    public async Task PartialCollection_RetainsKnownEdgesAndRemainsIncomplete(string scenario)
    {
        using var f = new Fixture(26, scenario); var preview = await f.Preview();
        Assert.Single(preview.BlockingComponents); Assert.Equal("incomplete", preview.Status);
        Assert.False(preview.DependenciesChecked); Assert.Equal(0, f.NameQueries); f.Verify();
    }

    [Theory]
    [InlineData("column")]
    [InlineData("key")]
    [Trait("AC", "AC-114")]
    public async Task ConflictingReturnedParent_FailsBeforeDependencyRead(string family)
    {
        using var f = new Fixture(26, "wrong-parent");
        var kind = family == "column" ? MetadataDeleteKind.Column : MetadataDeleteKind.Key;
        var preview = await f.Service.PreviewDeleteAsync(new MetadataDeleteTarget(kind, "SyntheticSolution", "new_table", "new_name"), f.Token);
        Assert.Equal("incomplete", preview.Status); Assert.False(preview.TargetExistenceChecked); Assert.Equal(0, f.DependencyReads); f.Verify();
    }

    [Theory]
    [InlineData("ambiguous-name")]
    [InlineData("partial-name")]
    [InlineData("wrong-name-row")]
    [InlineData("limited-name")]
    [Trait("AC", "AC-116")]
    public async Task IncompleteNameResult_NeverClearsTheKnownBlocker(string scenario)
    {
        using var f = new Fixture(26, scenario); var preview = await f.Preview();
        Assert.Equal("blocked", preview.Status); Assert.Single(preview.BlockingComponents); Assert.Null(preview.BlockingComponents[0].Name);
        Assert.NotEmpty(preview.Warnings); f.Verify();
    }

    [Fact]
    [Trait("AC", "AC-118")]
    public async Task OptionalNameAuthFailure_PreservesClassificationAndCauseWithoutProviderText()
    {
        using var f = new Fixture(26, "auth-name"); var preview = await f.Preview();
        var issue = Assert.Single(preview.NameResolutionIssues);
        Assert.Equal(ErrorCodes.Auth.Expired, issue.ErrorCode); Assert.True(issue.RequiresReauthentication);
        Assert.Same(f.AuthFailure, preview.FailureCause); Assert.Equal("blocked", preview.Status);
        Assert.DoesNotContain("SyntheticClientSecret", JsonSerializer.Serialize(preview)); f.Verify();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(26)]
    [InlineData(70)]
    [InlineData(10377)]
    [Trait("AC", "AC-118")]
    public async Task OptionalNameCancellation_PropagatesExactExceptionAndReturnsEveryLease(int type)
    {
        using var f = new Fixture(type, "cancel-name");
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Preview());
        Assert.Same(f.Cancellation, ex); Assert.Equal(f.Token, ex.CancellationToken); f.Verify();
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly Guid TargetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        internal static readonly Guid DependentId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly Mock<ICachedMetadataProvider> _cache = new(MockBehavior.Strict);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<int> _used = [], _returned = [];
        private int _acquired, _active;
        internal int NameQueries { get; private set; }
        internal int DependencyReads { get; private set; }
        internal CancellationToken Token => _cts.Token;
        internal OperationCanceledException Cancellation { get; }
        internal PpdsAuthException AuthFailure { get; } = new(ErrorCodes.Auth.Expired, "ClientSecret=SyntheticClientSecret") { RequiresReauthentication = true };
        internal DataverseMetadataAuthoringService Service { get; }
        internal Fixture(int type, string scenario = "")
        {
            Cancellation = new OperationCanceledException(Token);
            var guard = new Mock<IShakedownGuard>(MockBehavior.Strict); guard.Setup(g => g.EnsureCanMutate(It.IsAny<string>()));
            _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
                .Returns<DataverseClientOptions?, string?, CancellationToken>((_, _, token) =>
                {
                    Assert.Equal(Token, token); Assert.Equal(0, _active); _active++; var id = ++_acquired;
                    var client = new Mock<IPooledClient>(MockBehavior.Strict);
                    client.Setup(c => c.DisposeAsync()).Callback(() => { _active--; _returned.Add(id); }).Returns(ValueTask.CompletedTask);
                    void Begin(CancellationToken ct, bool name)
                    {
                        Assert.Equal(Token, ct); Assert.Equal(1, _active); _used.Add(id);
                        if (name && scenario == "cancel-name") throw Cancellation;
                    }
                    client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                        .Returns<OrganizationRequest, CancellationToken>((request, ct) =>
                        {
                            if (request is RetrieveDependenciesForDeleteRequest deps)
                            {
                                Begin(ct, false); DependencyReads++; Assert.Equal(1, deps.ComponentType); Assert.Equal(TargetId, deps.ObjectId);
                                var row = new Entity("dependency", Guid.Parse("55555555-5555-5555-5555-555555555555")) { ["dependentcomponenttype"] = new OptionSetValue(type), ["dependentcomponentobjectid"] = DependentId };
                                var rows = new EntityCollection([row]); if (scenario == "duplicates") rows.Entities.Add(row);
                                rows.MoreRecords = scenario == "partial-more"; rows.TotalRecordCount = scenario == "partial-total" ? 2 : rows.Entities.Count; rows.TotalRecordCountLimitExceeded = scenario == "partial-limit";
                                return Task.FromResult<OrganizationResponse>(new RetrieveDependenciesForDeleteResponse { Results = new ParameterCollection { ["EntityCollection"] = rows } });
                            }
                            var namedTarget = request is RetrieveEntityRequest { LogicalName: "new_table" } or RetrieveAttributeRequest { LogicalName: "new_name" } or RetrieveEntityKeyRequest { LogicalName: "new_name" };
                            Begin(ct, !namedTarget);
                            if (namedTarget)
                            {
                                if (request is RetrieveEntityRequest table) { Assert.True(table.RetrieveAsIfPublished); Assert.Equal(EntityFilters.Entity, table.EntityFilters); return Response(new EntityMetadata { LogicalName = "new_table", MetadataId = TargetId }); }
                                if (request is RetrieveAttributeRequest column)
                                {
                                    Assert.True(column.RetrieveAsIfPublished); Assert.Equal("new_table", column.EntityLogicalName);
                                    var attributeMetadata = new StringAttributeMetadata { LogicalName = "new_name", MetadataId = TargetId };
                                    typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.EntityLogicalName))!.SetValue(attributeMetadata, "other_parent");
                                    Assert.Equal("other_parent", attributeMetadata.EntityLogicalName);
                                    return Response(attributeMetadata);
                                }
                                var key = Assert.IsType<RetrieveEntityKeyRequest>(request); Assert.True(key.RetrieveAsIfPublished); Assert.Equal("new_table", key.EntityLogicalName);
                                var keyMetadata = new EntityKeyMetadata { LogicalName = "new_name", MetadataId = TargetId };
                                typeof(EntityKeyMetadata).GetProperty(nameof(EntityKeyMetadata.EntityLogicalName))!.SetValue(keyMetadata, "other_parent");
                                Assert.Equal("other_parent", keyMetadata.EntityLogicalName);
                                return Response(keyMetadata);
                            }
                            if (type == 10377)
                            {
                                var modern = Assert.IsType<RetrieveEntityRequest>(request); Assert.Equal("controlconfiguration", modern.LogicalName); Assert.True(modern.RetrieveAsIfPublished); Assert.Equal(EntityFilters.Entity, modern.EntityFilters);
                                var metadata = new EntityMetadata { LogicalName = "controlconfiguration" };
                                typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryIdAttribute))!.SetValue(metadata, "controlconfigurationid");
                                typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.PrimaryNameAttribute))!.SetValue(metadata, "name");
                                typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.ObjectTypeCode))!.SetValue(metadata, scenario == "unverified" ? 10378 : 10377);
                                return Response(metadata);
                            }
                            MetadataBase dependent = type switch
                            {
                                1 => EntityName(Assert.IsType<RetrieveEntityRequest>(request)),
                                2 => AttributeName(Assert.IsType<RetrieveAttributeRequest>(request)),
                                9 => ChoiceName(Assert.IsType<RetrieveOptionSetRequest>(request)),
                                10 => RelationshipName(Assert.IsType<RetrieveRelationshipRequest>(request)),
                                14 => KeyName(Assert.IsType<RetrieveEntityKeyRequest>(request)), _ => throw new InvalidOperationException("Unexpected SDK request")
                            };
                            return Response(dependent);
                        });
                    client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                        .Returns<QueryBase, CancellationToken>((query, ct) =>
                        {
                            Begin(ct, true); NameQueries++;
                            var q = Assert.IsType<QueryExpression>(query); var table = type switch { 26 => "savedquery", 70 => "fieldsecurityprofile", 10377 => "controlconfiguration", _ => throw new InvalidOperationException("Guessed component table") };
                            Assert.Equal(table, q.EntityName); Assert.False(q.ColumnSet.AllColumns); Assert.Equal("name", Assert.Single(q.ColumnSet.Columns)); Assert.Empty(q.LinkEntities); Assert.Empty(q.Criteria.Filters);
                            var predicate = Assert.Single(q.Criteria.Conditions); Assert.Equal(table + "id", predicate.AttributeName); Assert.Equal(ConditionOperator.Equal, predicate.Operator); Assert.Equal(DependentId, Assert.Single(predicate.Values));
                            if (scenario == "auth-name") throw AuthFailure;
                            var row = new Entity(scenario == "wrong-name-row" ? "wrong_table" : table, DependentId) { ["name"] = "dep_name" };
                            var rows = new EntityCollection([row]) { MoreRecords = scenario == "partial-name", TotalRecordCountLimitExceeded = scenario == "limited-name" }; if (scenario == "ambiguous-name") rows.Entities.Add(row);
                            return Task.FromResult(rows);
                        });
                    return Task.FromResult(client.Object);
                });
            Service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), guard.Object, cacheProvider: _cache.Object);
        }
        private static MetadataBase EntityName(RetrieveEntityRequest r) { Assert.Equal(DependentId, r.MetadataId); Assert.True(r.RetrieveAsIfPublished); Assert.Equal(EntityFilters.Entity, r.EntityFilters); return new EntityMetadata { MetadataId = DependentId, LogicalName = "dep_name" }; }
        private static MetadataBase AttributeName(RetrieveAttributeRequest r) { Assert.Equal(DependentId, r.MetadataId); Assert.True(r.RetrieveAsIfPublished); return new StringAttributeMetadata { MetadataId = DependentId, LogicalName = "dep_name" }; }
        private static MetadataBase ChoiceName(RetrieveOptionSetRequest r) { Assert.Equal(DependentId, r.MetadataId); Assert.True(r.RetrieveAsIfPublished); return new OptionSetMetadata { MetadataId = DependentId, Name = "dep_name", IsGlobal = true }; }
        private static MetadataBase RelationshipName(RetrieveRelationshipRequest r) { Assert.Equal(DependentId, r.MetadataId); Assert.True(r.RetrieveAsIfPublished); return new OneToManyRelationshipMetadata { MetadataId = DependentId, SchemaName = "dep_name" }; }
        private static MetadataBase KeyName(RetrieveEntityKeyRequest r) { Assert.Equal(DependentId, r.MetadataId); Assert.True(r.RetrieveAsIfPublished); return new EntityKeyMetadata { MetadataId = DependentId, LogicalName = "dep_name" }; }
        private static Task<OrganizationResponse> Response(MetadataBase metadata) => Task.FromResult<OrganizationResponse>(metadata switch
        {
            EntityMetadata m => new RetrieveEntityResponse { Results = new ParameterCollection { ["EntityMetadata"] = m } },
            AttributeMetadata m => new RetrieveAttributeResponse { Results = new ParameterCollection { ["AttributeMetadata"] = m } },
            OptionSetMetadataBase m => new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = m } },
            RelationshipMetadataBase m => new RetrieveRelationshipResponse { Results = new ParameterCollection { ["RelationshipMetadata"] = m } },
            EntityKeyMetadata m => new RetrieveEntityKeyResponse { Results = new ParameterCollection { ["EntityKeyMetadata"] = m } }, _ => throw new InvalidOperationException()
        });
        internal Task<MetadataDeletePreviewResult> Preview() => Service.PreviewDeleteAsync(new MetadataDeleteTarget(MetadataDeleteKind.Table, "SyntheticSolution", "new_table"), Token);
        internal void Verify() { Assert.Equal(0, _active); Assert.Equal(Enumerable.Range(1, _acquired), _returned); Assert.Equal(_acquired, _used.Count); Assert.All(_used.GroupBy(i => i), g => Assert.Single(g)); _cache.VerifyNoOtherCalls(); }
        public void Dispose() => _cts.Dispose();
    }
}
