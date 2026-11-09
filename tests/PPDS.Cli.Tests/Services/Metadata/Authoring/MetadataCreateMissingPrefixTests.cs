using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
[Trait("AC", "AC-108")]
public sealed class MetadataCreateMissingPrefixTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 718980007)]
    [InlineData(true, 0)]
    [InlineData(true, 718980007)]
    public async Task ExplicitOnly_MissingNumericPrefixWarnsAndPreservesValue(bool global, int value)
    {
        using var f = new Fixture(global);
        var warnings = await f.Run(new OptionDefinition { Label = "Explicit", Value = value });
        Assert.Equal(value, Assert.Single(f.Values));
        Assert.Contains(warnings, w => w.Rule == "OptionValueRangeUnavailable");
        f.AssertReturned();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Omission_MissingNumericPrefixRetainsValidationFailure(bool global)
    {
        using var f = new Fixture(global);
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => f.Run(new OptionDefinition { Label = "Automatic" }));
        Assert.Equal(MetadataErrorCodes.MissingRequiredField, ex.ErrorCode);
        Assert.Empty(f.Values);
        f.AssertReturned();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool _global;
        private readonly CancellationTokenSource _cts = new();
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly List<int> _operations = [], _returned = [];
        private int _acquired, _active;
        internal List<int> Values { get; } = [];
        private DataverseMetadataAuthoringService Service { get; }
        internal Fixture(bool global)
        {
            _global = global;
            _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
                .Returns<DataverseClientOptions?, string?, CancellationToken>((_, _, ct) =>
                {
                    Assert.Equal(_cts.Token, ct); Assert.Equal(0, _active); _active++;
                    var id = ++_acquired;
                    var client = new Mock<IPooledClient>(MockBehavior.Strict);
                    client.Setup(c => c.DisposeAsync()).Callback(() => { _active--; _returned.Add(id); }).Returns(ValueTask.CompletedTask);
                    client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                        .Returns<QueryBase, CancellationToken>((query, token) =>
                        {
                            Assert.Equal(_cts.Token, token); _operations.Add(id);
                            var q = Assert.IsType<QueryExpression>(query);
                            var row = q.EntityName == "solution"
                                ? new Entity("solution") { ["publisherid"] = new EntityReference("publisher", Guid.Parse("11111111-1111-1111-1111-111111111111")) }
                                : new Entity("publisher") { ["customizationprefix"] = "new" }; // Deliberately no numeric prefix.
                            return Task.FromResult(new EntityCollection([row]));
                        });
                    client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                        .Returns<OrganizationRequest, CancellationToken>((request, token) =>
                        {
                            Assert.Equal(_cts.Token, token); _operations.Add(id);
                            if (request is CreateAttributeRequest a)
                            {
                                Values.AddRange(Assert.IsAssignableFrom<EnumAttributeMetadata>(a.Attribute).OptionSet.Options.Select(o => o.Value!.Value));
                                return Task.FromResult<OrganizationResponse>(new CreateAttributeResponse());
                            }
                            var choice = Assert.IsType<CreateOptionSetRequest>(request);
                            Values.AddRange(Assert.IsType<OptionSetMetadata>(choice.OptionSet).Options.Select(o => o.Value!.Value));
                            return Task.FromResult<OrganizationResponse>(new CreateOptionSetResponse());
                        });
                    return Task.FromResult(client.Object);
                });
            Service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
        }
        internal async Task<ValidationMessage[]> Run(OptionDefinition option)
        {
            if (_global)
                return (await Service.CreateGlobalChoiceAsync(new CreateGlobalChoiceRequest
                { SolutionUniqueName = "SyntheticSolution", SchemaName = "new_Choice", DisplayName = "Choice", Options = [option] }, ct: _cts.Token)).ValidationMessages;
            return (await Service.CreateColumnAsync(new CreateColumnRequest
            { SolutionUniqueName = "SyntheticSolution", SchemaName = "new_Choice", EntityLogicalName = "new_table", DisplayName = "Choice", ColumnType = SchemaColumnType.Choice, Options = [option] }, ct: _cts.Token)).ValidationMessages;
        }
        internal void AssertReturned()
        {
            Assert.Equal(0, _active); Assert.Equal(Enumerable.Range(1, _acquired), _returned);
            Assert.Equal(_acquired, _operations.Count); Assert.All(_operations.GroupBy(v => v), g => Assert.Single(g));
        }
        public void Dispose() => _cts.Dispose();
    }
}
