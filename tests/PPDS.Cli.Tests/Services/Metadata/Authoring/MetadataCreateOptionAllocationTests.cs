using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Commands.Metadata.Attribute;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

// Real application service and actual pure CLI parsers; every SDK/pool dependency is strict and synthetic.
[Trait("Category", "Unit")]
public sealed class MetadataCreateOptionAllocationTests(ITestOutputHelper output)
{
    private const int First = 718980000;

    [Theory]
    [InlineData("choice")]
    [InlineData("choices")]
    [InlineData("global")]
    [Trait("AC", "AC-106")]
    public async Task OmittedValues_AreDistinctAndDoNotMutateCaller(string route)
    {
        using var f = new Fixture(route);
        var options = new[] { new OptionDefinition { Label = "One", Color = "#123456", Description = "First" }, new OptionDefinition { Label = "Two" } };
        var before = JsonSerializer.Serialize(options);
        await f.Run(options);
        output.WriteLine(JsonSerializer.Serialize(new { route, values = f.Values, before, after = JsonSerializer.Serialize(options), f.Stages }));
        Assert.Equal(new[] { First, First + 1 }, f.Values);
        Assert.Equal(new[] { "One", "Two" }, f.Labels);
        Assert.Equal(before, JsonSerializer.Serialize(options));
        f.AssertReturnedAndRouted();
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("choices")]
    [InlineData("global")]
    [Trait("AC", "AC-106")]
    public async Task ExplicitZero_IsPreservedAlongsideOmittedAndReservedValues(string route)
    {
        using var f = new Fixture(route);
        var options = new[] { new OptionDefinition { Label = "Zero", Value = 0 }, new OptionDefinition { Label = "Auto" }, new OptionDefinition { Label = "Reserved", Value = First } };
        var before = JsonSerializer.Serialize(options);
        await f.Run(options);
        output.WriteLine(JsonSerializer.Serialize(new { route, values = f.Values, before, after = JsonSerializer.Serialize(options) }));
        Assert.Equal(new[] { 0, First + 1, First }, f.Values);
        Assert.Equal(before, JsonSerializer.Serialize(options));
        f.AssertReturnedAndRouted();
    }

    [Theory]
    [InlineData("choice", 0)]
    [InlineData("choice", 718980007)]
    [InlineData("choices", 0)]
    [InlineData("global", 0)]
    [InlineData("global", 718980007)]
    [Trait("AC", "AC-107")]
    public async Task DuplicateExplicitValues_RejectBeforeCreate(string route, int value)
    {
        using var f = new Fixture(route);
        var options = new[] { new OptionDefinition { Label = "One", Value = value }, new OptionDefinition { Label = "Two", Value = value } };
        var before = JsonSerializer.Serialize(options);
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => f.Run(options));
        Assert.Contains(ex.ValidationMessages, m => m.Rule == MetadataErrorCodes.DuplicateOptionValue);
        Assert.Empty(f.Creates);
        Assert.Equal(before, JsonSerializer.Serialize(options));
        f.AssertReturnedAndRouted();
    }

    [Theory]
    [InlineData("choice", 0, true)]
    [InlineData("choices", 100000001, true)]
    [InlineData("global", 100000001, true)]
    [InlineData("choice", 718979999, true)]
    [InlineData("global", 718990000, true)]
    [InlineData("choice", 718980000, false)]
    [InlineData("global", 718989999, false)]
    [Trait("AC", "AC-108")]
    public async Task ExplicitRange_WarnsWithoutRejectingOrRewriting(string route, int value, bool warning)
    {
        using var f = new Fixture(route);
        var messages = await f.Run([new OptionDefinition { Label = "Explicit", Value = value }]);
        Assert.Equal(new[] { value }, f.Values);
        var warnings = messages.Where(m => m.Rule == "OptionValueOutsidePublisherRange").ToArray();
        Assert.Equal(warning ? 1 : 0, warnings.Length);
        if (warning)
        {
            Assert.Contains(value.ToString(), Assert.Single(warnings).Message);
            Assert.Contains(First.ToString(), warnings[0].Message);
            Assert.Contains((First + 9999).ToString(), warnings[0].Message);
            Assert.Contains(f.Reporter.Messages, m => m.Contains(value.ToString(), StringComparison.Ordinal));
        }
        f.AssertReturnedAndRouted();
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("global")]
    [Trait("AC", "AC-108")]
    public async Task DryRun_OmittedValuesValidateWithoutCreateOrCallerMutation(string route)
    {
        using var f = new Fixture(route);
        var options = new[] { new OptionDefinition { Label = "One" }, new OptionDefinition { Label = "Two" } };
        var before = JsonSerializer.Serialize(options);
        await f.Run(options, dryRun: true);
        Assert.Empty(f.Creates);
        Assert.Equal(before, JsonSerializer.Serialize(options));
        f.AssertReturnedAndRouted();
    }

    [Theory]
    [InlineData("choice", 1)]
    [InlineData("choice", 2)]
    [InlineData("choice", 3)]
    [InlineData("choice", 4)]
    [InlineData("choice", 5)]
    [InlineData("global", 1)]
    [InlineData("global", 2)]
    [InlineData("global", 3)]
    [InlineData("global", 4)]
    [InlineData("global", 5)]
    [Trait("AC", "AC-109")]
    public async Task CancellationAtEverySdkStage_PreservesIdentityAndCaller(string route, int stage)
    {
        using var f = new Fixture(route) { CancelAt = stage };
        var options = new[] { new OptionDefinition { Label = "Auto" } };
        var before = JsonSerializer.Serialize(options);
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(options));
        Assert.Same(f.Cancellation, ex);
        Assert.Equal(f.Token, ex.CancellationToken);
        Assert.Equal(stage, f.Stages.Count);
        Assert.Equal(before, JsonSerializer.Serialize(options));
        f.AssertReturnedAndRouted();
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("global")]
    [Trait("AC", "AC-109")]
    public async Task CreateSdkFailure_PreservesExistingCauseWithoutMutatingCaller(string route)
    {
        using var f = new Fixture(route) { FailCreate = true };
        var options = new[] { new OptionDefinition { Label = "Auto" } };
        var before = JsonSerializer.Serialize(options);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run(options));
        Assert.Same(f.SdkFailure, ex);
        Assert.Equal(before, JsonSerializer.Serialize(options));
        f.AssertReturnedAndRouted();
    }

    [Theory]
    [InlineData("choice")]
    [InlineData("global")]
    [Trait("AC", "AC-110")]
    public async Task ActualCliParser_LabelOnlyAndZero_ReachRealService(string route)
    {
        using var f = new Fixture(route);
        var options = route == "global"
            ? OptionSetCommand.ParseOptionDefinitions("Automatic,Zero=0,Reserved=718980000")
            : AttributeCommandGroup.ParseOptionSpecs(["Automatic", "Zero:0", "Reserved:718980000"], null, null)!;
        await f.Run(options);
        Assert.Equal(new[] { First + 1, 0, First }, f.Values);
        f.AssertReturnedAndRouted();
    }

    private sealed class Reporter : IMetadataAuthoringProgressReporter
    {
        internal List<string> Messages { get; } = [];
        public void ReportPhase(string phase, string? detail = null) { }
        public void ReportInfo(string message) => Messages.Add(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _route;
        private readonly CancellationTokenSource _cts = new();
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly List<(int Lease, string Stage)> _operations = [];
        private readonly List<int> _returned = [];
        private int _acquired, _active;
        internal List<string> Stages { get; } = [];
        internal List<OrganizationRequest> Creates { get; } = [];
        internal int[] Values { get; private set; } = [];
        internal string[] Labels { get; private set; } = [];
        internal Reporter Reporter { get; } = new();
        internal int CancelAt { get; init; }
        internal bool FailCreate { get; init; }
        internal CancellationToken Token => _cts.Token;
        internal OperationCanceledException Cancellation { get; }
        internal InvalidOperationException SdkFailure { get; } = new("Synthetic SDK creation failure");
        internal DataverseMetadataAuthoringService Service { get; }

        internal Fixture(string route)
        {
            _route = route;
            Cancellation = new OperationCanceledException(Token);
            _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
                .Returns<DataverseClientOptions?, string?, CancellationToken>((_, _, token) =>
                {
                    Assert.Equal(Token, token);
                    Assert.Equal(0, _active); // Fails a retained lease before any next checkout.
                    _active++;
                    var number = ++_acquired;
                    var lease = new Mock<IPooledClient>(MockBehavior.Strict);
                    lease.Setup(c => c.DisposeAsync()).Callback(() => { Assert.Equal(1, _active); _active--; _returned.Add(number); }).Returns(ValueTask.CompletedTask);
                    lease.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                        .Returns<QueryBase, CancellationToken>((query, ct) =>
                        {
                            var q = Assert.IsType<QueryExpression>(query);
                            var stage = q.EntityName + ":" + string.Join(",", q.ColumnSet.Columns);
                            Begin(number, stage, ct);
                            var row = q.EntityName == "solution"
                                ? new Entity("solution") { ["publisherid"] = new EntityReference("publisher", Guid.Parse("11111111-1111-1111-1111-111111111111")) }
                                : new Entity("publisher") { ["customizationprefix"] = "new", ["customizationoptionvalueprefix"] = 71898 };
                            Assert.Contains(q.EntityName, new[] { "solution", "publisher" });
                            return Task.FromResult(new EntityCollection([row]));
                        });
                    lease.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                        .Returns<OrganizationRequest, CancellationToken>((request, ct) =>
                        {
                            Begin(number, request.RequestName, ct);
                            Creates.Add(request);
                            OptionSetMetadata set;
                            OrganizationResponse response;
                            if (request is CreateAttributeRequest attribute)
                            {
                                Assert.Equal("new_table", attribute.EntityName);
                                Assert.Equal("SyntheticSolution", attribute.SolutionUniqueName);
                                set = Assert.IsAssignableFrom<EnumAttributeMetadata>(attribute.Attribute).OptionSet;
                                Assert.False(set.IsGlobal);
                                response = new CreateAttributeResponse { Results = new ParameterCollection { ["AttributeId"] = Guid.Parse("22222222-2222-2222-2222-222222222222") } };
                            }
                            else
                            {
                                var global = Assert.IsType<CreateOptionSetRequest>(request);
                                Assert.Equal("SyntheticSolution", global.SolutionUniqueName);
                                set = Assert.IsType<OptionSetMetadata>(global.OptionSet);
                                Assert.True(set.IsGlobal);
                                response = new CreateOptionSetResponse { Results = new ParameterCollection { ["OptionSetId"] = Guid.Parse("33333333-3333-3333-3333-333333333333") } };
                            }
                            Values = set.Options.Select(o => o.Value!.Value).ToArray();
                            Labels = set.Options.Select(o => Assert.Single(o.Label.LocalizedLabels).Label).ToArray();
                            if (FailCreate) throw SdkFailure;
                            return Task.FromResult(response);
                        });
                    return Task.FromResult(lease.Object);
                });
            Service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
        }

        private void Begin(int lease, string stage, CancellationToken ct)
        {
            Assert.Equal(Token, ct); Assert.Equal(1, _active);
            _operations.Add((lease, stage)); Stages.Add(stage);
            if (Stages.Count == CancelAt) throw Cancellation;
        }

        internal async Task<ValidationMessage[]> Run(OptionDefinition[] options, bool dryRun = false)
        {
            if (_route == "global")
            {
                var result = await Service.CreateGlobalChoiceAsync(new CreateGlobalChoiceRequest
                { SchemaName = "new_Choice", DisplayName = "Synthetic Choice", SolutionUniqueName = "SyntheticSolution", Options = options, DryRun = dryRun }, Reporter, Token);
                Assert.Equal(dryRun, result.WasDryRun);
                return result.ValidationMessages;
            }
            var column = await Service.CreateColumnAsync(new CreateColumnRequest
            {
                SchemaName = "new_Choice", EntityLogicalName = "new_table", DisplayName = "Synthetic Choice", SolutionUniqueName = "SyntheticSolution",
                ColumnType = _route == "choices" ? SchemaColumnType.Choices : SchemaColumnType.Choice, Options = options, DryRun = dryRun
            }, Reporter, Token);
            Assert.Equal(dryRun, column.WasDryRun);
            return column.ValidationMessages;
        }

        internal void AssertReturnedAndRouted()
        {
            Assert.Equal(0, _active);
            Assert.Equal(Enumerable.Range(1, _acquired), _returned);
            Assert.Equal(_acquired, _operations.Count);
            Assert.All(_operations.GroupBy(o => o.Lease), operations => Assert.Single(operations));
        }

        public void Dispose() => _cts.Dispose();
    }
}
