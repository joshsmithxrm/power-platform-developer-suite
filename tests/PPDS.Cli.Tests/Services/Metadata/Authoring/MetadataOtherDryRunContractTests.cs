using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>Characterizes the actual read boundary behind each non-schema-delete preview.</summary>
[Trait("Category", "Unit")]
public sealed class MetadataOtherDryRunContractTests
{
    public static IEnumerable<object[]> Operations()
    {
        foreach (var operation in new[] { "table-create", "table-update", "column-create", "column-update", "one-to-many", "many-to-many", "relationship-update", "choice-create", "choice-update", "option-add", "option-update", "option-remove", "key-create", "status-add", "local-add", "local-update", "local-remove" })
            yield return [operation];
    }

    [Theory]
    [MemberData(nameof(Operations))]
    [Trait("AC", "AC-122")]
    public async Task EveryBranch_OnlyPerformsItsDocumentedReads_NoCacheOrWrite(string operation)
    {
        using var f = new Fixture(operation);
        var value = await f.Run();
        Assert.Equal(f.ExpectedReads, f.Reads); f.AssertReturned();
        if (operation == "option-add") Assert.Equal(0, value); // SDK auto-assignment has not occurred.
        if (operation is "status-add" or "local-add") Assert.Equal(1000000, value); // A draft value, never persisted.
    }

    [Theory]
    [InlineData("table-create")]
    [InlineData("column-create")]
    [InlineData("one-to-many")]
    [InlineData("many-to-many")]
    [InlineData("choice-create")]
    [InlineData("option-update")]
    [InlineData("option-remove")]
    [InlineData("key-create")]
    [InlineData("status-add")]
    [InlineData("local-add")]
    [InlineData("local-update")]
    [InlineData("local-remove")]
    [Trait("AC", "AC-118")]
    public async Task ReadCancellation_PreservesExactCauseTokenAndReleasesLease(string operation)
    {
        using var f = new Fixture(operation, cancel: true);
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run());
        Assert.Same(f.Cancellation, ex); Assert.Equal(f.Token, ex.CancellationToken);
        Assert.Single(f.Reads); f.AssertReturned();
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly Guid PublisherId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        private readonly string _operation;
        private readonly bool _cancel;
        private readonly CancellationTokenSource _cts = new();
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly Mock<ICachedMetadataProvider> _cache = new(MockBehavior.Strict);
        private readonly DataverseMetadataAuthoringService _service;
        private int _active, _acquired, _returned;
        private readonly List<int> _uses = [];
        internal List<string> Reads { get; } = [];
        internal CancellationToken Token => _cts.Token;
        internal OperationCanceledException Cancellation { get; }
        internal string[] ExpectedReads => _operation switch
        {
            "table-create" or "column-create" or "one-to-many" or "many-to-many" or "choice-create" or "key-create" => ["solution", "publisher"],
            "status-add" => ["statuscode:True", "solution", "publisher"],
            "local-add" => ["new_column:True", "solution", "publisher"],
            "local-update" or "local-remove" => ["new_column:False"],
            "option-update" or "option-remove" => ["new_choice:True"], _ => []
        };
        internal Fixture(string operation, bool cancel = false)
        {
            _operation = operation; _cancel = cancel; Cancellation = new OperationCanceledException(Token);
            var guard = new Mock<IShakedownGuard>(MockBehavior.Strict);
            guard.Setup(g => g.EnsureCanMutate(It.IsAny<string>()));
            _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
                .Returns<DataverseClientOptions?, string?, CancellationToken>((_, _, token) =>
                {
                    Assert.Equal(Token, token); Assert.Equal(0, _active); _active++;
                    var id = ++_acquired; var client = new Mock<IPooledClient>(MockBehavior.Strict);
                    client.Setup(c => c.DisposeAsync()).Callback(() => { _active--; _returned++; }).Returns(ValueTask.CompletedTask);
                    void Begin(string read, CancellationToken ct)
                    {
                        Assert.Equal(Token, ct); Assert.Equal(1, _active); _uses.Add(id); Reads.Add(read);
                        if (_cancel) throw Cancellation;
                    }
                    client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                        .Returns<QueryBase, CancellationToken>((query, ct) =>
                        {
                            var q = Assert.IsType<QueryExpression>(query); Assert.False(q.ColumnSet.AllColumns);
                            var condition = Assert.Single(q.Criteria.Conditions); Assert.Equal(ConditionOperator.Equal, condition.Operator);
                            if (q.EntityName == "solution") { Assert.Equal("uniquename", condition.AttributeName); Assert.Equal("SyntheticSolution", Assert.Single(condition.Values)); }
                            else { Assert.Equal("publisher", q.EntityName); Assert.Equal("publisherid", condition.AttributeName); Assert.Equal(PublisherId, Assert.Single(condition.Values)); }
                            Begin(q.EntityName, ct);
                            var row = q.EntityName == "solution" ? new Entity("solution") { ["publisherid"] = new EntityReference("publisher", PublisherId) }
                                : new Entity("publisher", PublisherId) { ["customizationprefix"] = "new", ["customizationoptionvalueprefix"] = 100 };
                            return Task.FromResult(new EntityCollection([row]));
                        });
                    client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                        .Returns<OrganizationRequest, CancellationToken>((request, ct) =>
                        {
                            if (request is RetrieveOptionSetRequest choice)
                            {
                                Assert.Equal("new_choice", choice.Name); Begin(choice.Name + ":" + choice.RetrieveAsIfPublished, ct);
                                var options = new OptionSetMetadata { Name = choice.Name, IsGlobal = true }; options.Options.Add(new OptionMetadata(new Label("Label", 1033), 17));
                                return Task.FromResult<OrganizationResponse>(new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = options } });
                            }
                            var attribute = Assert.IsType<RetrieveAttributeRequest>(request); Assert.Equal("new_table", attribute.EntityLogicalName);
                            Assert.Equal(_operation == "status-add" ? "statuscode" : "new_column", attribute.LogicalName);
                            Begin(attribute.LogicalName + ":" + attribute.RetrieveAsIfPublished, ct);
                            var set = new OptionSetMetadata { IsGlobal = false }; set.Options.Add(new OptionMetadata(new Label("Label", 1033), 17));
                            AttributeMetadata metadata = attribute.LogicalName == "statuscode" ? new StatusAttributeMetadata { OptionSet = new OptionSetMetadata() } : new PicklistAttributeMetadata { OptionSet = set };
                            return Task.FromResult<OrganizationResponse>(new RetrieveAttributeResponse { Results = new ParameterCollection { ["AttributeMetadata"] = metadata } });
                        });
                    return Task.FromResult(client.Object);
                });
            _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), guard.Object, cacheProvider: _cache.Object);
        }
        internal async Task<int?> Run()
        {
            switch (_operation)
            {
                case "table-create": await _service.CreateTableAsync(new CreateTableRequest { SolutionUniqueName = "SyntheticSolution", SchemaName = "new_table", DisplayName = "Table", PluralDisplayName = "Tables", DryRun = true }, ct: Token); break;
                case "table-update": await _service.UpdateTableAsync(new UpdateTableRequest { SolutionUniqueName = "SyntheticSolution", EntityLogicalName = "new_table", DryRun = true }, ct: Token); break;
                case "column-create": await _service.CreateColumnAsync(new CreateColumnRequest { SolutionUniqueName = "SyntheticSolution", EntityLogicalName = "new_table", SchemaName = "new_column", DisplayName = "Column", ColumnType = SchemaColumnType.String, DryRun = true }, ct: Token); break;
                case "column-update": await _service.UpdateColumnAsync(new UpdateColumnRequest { SolutionUniqueName = "SyntheticSolution", EntityLogicalName = "new_table", ColumnLogicalName = "new_column", DryRun = true }, ct: Token); break;
                case "one-to-many": await _service.CreateOneToManyAsync(new PPDS.Dataverse.Metadata.Authoring.CreateOneToManyRequest { SolutionUniqueName = "SyntheticSolution", ReferencedEntity = "new_table", ReferencingEntity = "new_child", SchemaName = "new_rel", LookupSchemaName = "new_parent", LookupDisplayName = "Parent", DryRun = true }, ct: Token); break;
                case "many-to-many": await _service.CreateManyToManyAsync(new PPDS.Dataverse.Metadata.Authoring.CreateManyToManyRequest { SolutionUniqueName = "SyntheticSolution", Entity1LogicalName = "new_table", Entity2LogicalName = "new_child", SchemaName = "new_rel", DryRun = true }, ct: Token); break;
                case "relationship-update": await _service.UpdateRelationshipAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateRelationshipRequest { SolutionUniqueName = "SyntheticSolution", SchemaName = "new_rel", DryRun = true }, ct: Token); break;
                case "choice-create": await _service.CreateGlobalChoiceAsync(new CreateGlobalChoiceRequest { SolutionUniqueName = "SyntheticSolution", SchemaName = "new_choice", DisplayName = "Choice", DryRun = true }, ct: Token); break;
                case "choice-update": await _service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = "SyntheticSolution", Name = "new_choice", DryRun = true }, ct: Token); break;
                case "option-add": return await _service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = "new_choice", Label = "Label", DryRun = true }, Token);
                case "option-update": await _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = "new_choice", Value = 17, NewLabel = "Renamed", DryRun = true }, Token); break;
                case "option-remove": await _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest { OptionSetName = "new_choice", Value = 17, DryRun = true }, Token); break;
                case "key-create": await _service.CreateKeyAsync(new CreateKeyRequest { SolutionUniqueName = "SyntheticSolution", EntityLogicalName = "new_table", SchemaName = "new_key", DisplayName = "Key", KeyAttributes = ["new_column"], DryRun = true }, ct: Token); break;
                case "status-add": return await _service.AddStatusReasonAsync(new AddStatusReasonRequest { EntityLogicalName = "new_table", Label = "Label", SolutionUniqueName = "SyntheticSolution", DryRun = true }, ct: Token);
                case "local-add": return await _service.AddColumnOptionAsync(new AddColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = "new_column", Label = "Label", SolutionUniqueName = "SyntheticSolution", DryRun = true }, Token);
                case "local-update": await _service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = "new_column", Value = 17, NewLabel = "Renamed", DryRun = true }, Token); break;
                case "local-remove": await _service.RemoveColumnOptionAsync(new RemoveColumnOptionRequest { EntityLogicalName = "new_table", ColumnLogicalName = "new_column", Value = 17, DryRun = true }, Token); break;
                default: throw new ArgumentOutOfRangeException();
            }
            return null;
        }
        internal void AssertReturned()
        {
            Assert.Equal(0, _active); Assert.Equal(_acquired, _returned); Assert.Equal(_acquired, _uses.Count);
            Assert.All(_uses.GroupBy(i => i), group => Assert.Single(group)); _cache.VerifyNoOtherCalls();
        }
        public void Dispose() => _cts.Dispose();
    }
}
